// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Neolink.Media;
using Neolink.Streaming;

namespace Neolink.Protocol;

/// <summary>
/// Minimal RTSP-over-TCP pull client for generic (non-Reolink) cameras: one
/// connection, video track only, RTP interleaved on the same socket (no UDP
/// ports to open). Speaks just enough of the protocol for real-world cameras:
/// DESCRIBE → SETUP (TCP interleaved) → PLAY, Basic and Digest authentication,
/// H.264/H.265 depacketization (single NAL, STAP-A/AP, FU-A/FU) into Annex-B
/// access units published to the stream hub, and a periodic keep-alive.
/// One instance handles one connection; the owning service reconnects.
/// </summary>
public sealed class RtspPuller
{
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    // A reply, and the video once playing, must keep coming; an on-demand relay (MediaMTX)
    // may take its own 10 s to start the source before it answers DESCRIBE.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);
    private const int MaxMessage = 1 << 20;   // sanity cap for RTSP replies
    private const int MaxNal = 8 << 20;       // a 4K IDR slice can pass 1 MiB
    private const int MaxRedirects = 3;

    private readonly string _tag;
    private Uri _url;
    private readonly string? _user;
    private readonly string? _pass;
    private readonly IMediaSink _sink;

    private NetworkStream _net = null!;
    private Stream _read = null!;
    private int _cseq;
    private string? _session;
    private string? _authHeader;        // computed after a 401 challenge, reused afterwards
    private string? _digestRealm, _digestNonce;
    private int _videoChannel;          // the interleaved channel the camera chose in its SETUP reply
    private TimeSpan _keepAlive = KeepAliveEvery;
    private string _keepAliveMethod = "GET_PARAMETER";
    private int _keepAliveCseq = -1;

    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    // Depacketization state
    private VideoCodec _codec = VideoCodec.H264;
    private readonly List<byte[]> _auNals = new();
    private readonly MemoryStream _fu = new();
    private readonly MemoryStream _au = new();
    // Interleaved packets land here and are depacketized before the next read,
    // so one grow-only buffer replaces an allocation per packet.
    private byte[] _pkt = new byte[2048];
    private uint _auTimestamp;
    private bool _haveAuTs;
    private ushort _lastSeq;
    private bool _haveSeq;
    private bool _lastMarker;           // the previous packet ended an access unit
    private bool _auBroken;             // a packet of this access unit was lost: it is dropped, not published
    private readonly MemoryStream _paramsAhead = new(); // parameter sets sent as a "frame" of their own
    private bool _paramsAheadHasSps;
    private byte[]? _spropNals;         // SPS/PPS(/VPS) from the SDP, injected before the first keyframe
    private bool _spropInjected;

    /// <summary>When video first reached the hub on this connection; null until then.</summary>
    public DateTime? StreamingSince { get; private set; }

    /// <param name="url">rtsp://[user:pass@]host[:port]/path — credentials may ride the URL.</param>
    public RtspPuller(string tag, string url, IMediaSink sink)
    {
        _tag = tag;
        _sink = sink;
        // The login is read as the config's mask reads it: a raw '@', '/', '?' or '#' in the
        // password is common, and System.Uri would take it for a fragment or a bad port.
        var (bare, user, pass) = OnvifClient.SplitCredentials(url);
        _url = new Uri(bare);
        _user = user;
        _pass = user == null ? null : pass ?? "";
    }

    private int Port => _url.Port > 0 ? _url.Port : 554;

    /// <summary>URL without credentials, safe for request lines and logs.</summary>
    internal string BareUrl => $"rtsp://{_url.Host}:{Port}{_url.PathAndQuery}";

    /// <summary>Connects, negotiates and pumps RTP until cancelled or the peer drops.
    /// Follows a DESCRIBE redirect, as go2rtc and ffmpeg do.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            try
            {
                await SessionAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (RedirectException r) when (hop < MaxRedirects)
            {
                Log.Info($"{_tag}: RTSP redirected to rtsp://{r.Location.Host}:{(r.Location.Port > 0 ? r.Location.Port : 554)}{r.Location.PathAndQuery}");
                _url = r.Location;
                _session = null;
                _authHeader = null;
                _digestNonce = null;
            }
        }
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(_url.DnsSafeHost, Port, connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"no connection to {_url.Host}:{Port} within {ConnectTimeout.TotalSeconds:0}s");
            }
        }
        tcp.NoDelay = true;
        _net = tcp.GetStream();
        // Reads are buffered (the pump otherwise costs 3 recv syscalls per RTP
        // packet, and the header scan one per byte — see BcConnection, which got
        // the same treatment); writes stay on the raw stream so requests flush.
        _read = new BufferedStream(_net, 64 * 1024);
        try
        {
            var describe = await RequestAsync("DESCRIBE", BareUrl, "Accept: application/sdp\r\n", ct).ConfigureAwait(false);
            var track = ParseSdpVideo(describe.Body, describe.Headers);
            _codec = track.Codec;
            _spropNals = track.SpropNals;
            Log.Info($"{_tag}: RTSP video track {track.Codec} ({track.Control})");

            var setup = await RequestAsync("SETUP", track.Control,
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n", ct).ConfigureAwait(false);
            ReadSession(HeaderOf(setup.Headers, "Session"));
            if (string.IsNullOrEmpty(_session)) throw new IOException("RTSP SETUP returned no session");
            // The camera may answer with other channels than the ones asked for (go2rtc).
            _videoChannel = InterleavedChannel(HeaderOf(setup.Headers, "Transport")) ?? 0;

            await RequestAsync("PLAY", BareUrl, "Range: npt=0.000-\r\n", ct).ConfigureAwait(false);
            await PumpAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads interleaved RTP until cancelled, the peer drops, or the video stops.</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        // Re-armed by video packets only: RTCP or keep-alive replies alone mean the picture has stopped.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        long armedAt = Environment.TickCount64;
        var lastKeepAlive = DateTime.UtcNow;
        var head = new byte[4];
        try
        {
            while (true)
            {
                // Keep-alives ride the same socket; the reply is consumed inline below.
                if (DateTime.UtcNow - lastKeepAlive > _keepAlive)
                {
                    lastKeepAlive = DateTime.UtcNow;
                    _keepAliveCseq = _cseq + 1;
                    await SendRequestAsync(_keepAliveMethod, BareUrl, "", idle.Token).ConfigureAwait(false);
                }

                await ReadExactAsync(head, 1, idle.Token).ConfigureAwait(false);
                if (head[0] is >= (byte)'A' and <= (byte)'Z')
                {
                    // An inline RTSP message (keep-alive reply or a server request).
                    var (headers, _) = await ReadMessageTailAsync(head[0], idle.Token).ConfigureAwait(false);
                    await OnInlineMessageAsync(headers, idle.Token).ConfigureAwait(false);
                    continue;
                }
                if (head[0] != (byte)'$')
                    await SkipToFrameAsync(idle.Token).ConfigureAwait(false);

                // Interleaved binary: channel byte + big-endian length + payload.
                await ReadExactAsync(head.AsMemory(1, 3), idle.Token).ConfigureAwait(false);
                int channel = head[1];
                int len = (head[2] << 8) | head[3];
                if (_pkt.Length < len) _pkt = new byte[Math.Max(len, _pkt.Length * 2)];
                await ReadExactAsync(_pkt.AsMemory(0, len), idle.Token).ConfigureAwait(false);
                if (channel != _videoChannel) continue; // RTCP sender reports — nothing we need
                OnRtp(_pkt.AsSpan(0, len));
                if (Environment.TickCount64 - armedAt > 1000)
                {
                    idle.CancelAfter(IdleTimeout);
                    armedAt = Environment.TickCount64;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"no video from the camera for {IdleTimeout.TotalSeconds:0}s");
        }
    }

    /// <summary>Skips stray bytes up to the next '$' frame marker (go2rtc resyncs the same way).</summary>
    private async Task SkipToFrameAsync(CancellationToken ct)
    {
        var one = new byte[1];
        for (int skipped = 1; ; skipped++)
        {
            if (skipped > MaxMessage) throw new IOException("RTSP stream lost its framing");
            await ReadExactAsync(one, ct).ConfigureAwait(false);
            if (one[0] == (byte)'$')
            {
                Log.Debug($"{_tag}: skipped {skipped} stray byte(s) in the RTSP stream");
                return;
            }
        }
    }

    /// <summary>A keep-alive reply or a request from the camera, while streaming.</summary>
    private async Task OnInlineMessageAsync(List<string> headers, CancellationToken ct)
    {
        if (headers.Count == 0) return;
        var first = headers[0];
        if (first.StartsWith("RTSP/", StringComparison.Ordinal))
        {
            if (!int.TryParse(HeaderOf(headers, "CSeq"), out var cseq) || cseq != _keepAliveCseq) return;
            int status = Status(first);
            // A Digest nonce gone stale mid-session: the next keep-alive signs with the fresh one.
            if (status == 401 && _user != null && _digestNonce != null)
            {
                try { BuildAuth(headers, _keepAliveMethod, BareUrl); }
                catch (IOException) { /* no usable challenge; the session will say so */ }
                return;
            }
            // GET_PARAMETER is optional in RTSP; a camera that lacks it gets OPTIONS, which every server must answer.
            if (_keepAliveMethod == "GET_PARAMETER" && status is 400 or 405 or 501 or 551)
            {
                Log.Debug($"{_tag}: camera refused GET_PARAMETER ({first}); keeping the session alive with OPTIONS");
                _keepAliveMethod = "OPTIONS";
            }
            return;
        }
        await AnswerServerRequestAsync(headers, ct).ConfigureAwait(false);
    }

    /// <summary>Answers a camera's OPTIONS ping, which some servers need to keep the session.</summary>
    private async Task AnswerServerRequestAsync(List<string> headers, CancellationToken ct)
    {
        if (!headers[0].StartsWith("OPTIONS ", StringComparison.Ordinal)) return;
        var reply = new StringBuilder("RTSP/1.0 200 OK\r\n");
        if (HeaderOf(headers, "CSeq") is { } cseq) reply.Append("CSeq: ").Append(cseq).Append("\r\n");
        if (_session != null) reply.Append("Session: ").Append(_session).Append("\r\n");
        reply.Append("\r\n");
        await _net.WriteAsync(Encoding.UTF8.GetBytes(reply.ToString()), ct).ConfigureAwait(false);
    }

    /// <summary>Best-effort TEARDOWN, so a camera with few stream slots frees ours now rather than at its timeout.</summary>
    private async Task TeardownAsync()
    {
        if (_session == null) return;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _net.WriteAsync(BuildRequest("TEARDOWN", BareUrl, ""), limit.Token).ConfigureAwait(false);
        }
        catch { /* the connection is going anyway */ }
    }

    private void ReadSession(string? header)
    {
        // Session: 216525287999;timeout=60
        if (header == null) return;
        var parts = header.Split(';');
        _session = parts[0].Trim();
        foreach (var p in parts.Skip(1))
        {
            var kv = p.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("timeout", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(kv[1].Trim(), out var seconds) && seconds > 0)
                _keepAlive = TimeSpan.FromSeconds(Math.Clamp(seconds / 2, 1, (int)KeepAliveEvery.TotalSeconds));
        }
    }

    internal static int? InterleavedChannel(string? transport)
    {
        // Transport: RTP/AVP/TCP;unicast;interleaved=10-11;ssrc=10117CB7
        if (transport == null) return null;
        foreach (var p in transport.Split(';'))
        {
            var kv = p.Trim();
            if (!kv.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase)) continue;
            var first = kv["interleaved=".Length..].Split('-')[0];
            return int.TryParse(first, out var ch) && ch is >= 0 and <= 255 ? ch : null;
        }
        return null;
    }

    private static int Status(string statusLine)
    {
        var parts = statusLine.Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
    }

    private sealed class RedirectException(Uri location) : Exception("RTSP redirect")
    {
        public Uri Location { get; } = location;
    }

    // ------------------------------------------------------------------ RTSP plumbing

    /// <summary>Sends a request and reads its response, retrying once with credentials on 401.</summary>
    private async Task<(int Status, List<string> Headers, string Body)> RequestAsync(
        string method, string url, string extraHeaders, CancellationToken ct)
    {
        using var reply = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reply.CancelAfter(ReplyTimeout);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                await SendRequestAsync(method, url, extraHeaders, reply.Token).ConfigureAwait(false);
                var res = await ReadResponseAsync(reply.Token).ConfigureAwait(false);
                if (res.Status == 401 && attempt == 0 && _user != null)
                {
                    BuildAuth(res.Headers, method, url);
                    continue;
                }
                if (res.Status is 301 or 302 && method == "DESCRIBE"
                    && Uri.TryCreate(HeaderOf(res.Headers, "Location"), UriKind.Absolute, out var to)
                    && to.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
                    throw new RedirectException(to);
                if (res.Status is not (200 or 0))
                    throw new IOException($"RTSP {method} failed: {res.Headers[0].Split(' ', 2).ElementAtOrDefault(1) ?? res.Status.ToString()}");
                return res;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"no reply to RTSP {method} within {ReplyTimeout.TotalSeconds:0}s");
        }
    }

    private async Task SendRequestAsync(string method, string url, string extraHeaders, CancellationToken ct) =>
        await _net.WriteAsync(BuildRequest(method, url, extraHeaders), ct).ConfigureAwait(false);

    private byte[] BuildRequest(string method, string url, string extraHeaders)
    {
        // Digest responses are per-method/URI: refresh before every request.
        if (_digestNonce != null && _user != null)
            _authHeader = DigestHeader(method, url);
        var req = new StringBuilder()
            .Append(method).Append(' ').Append(url).Append(" RTSP/1.0\r\n")
            .Append("CSeq: ").Append(++_cseq).Append("\r\n")
            .Append("User-Agent: Neolink.NET\r\n");
        if (_session != null) req.Append("Session: ").Append(_session).Append("\r\n");
        if (_authHeader != null) req.Append(_authHeader).Append("\r\n");
        req.Append(extraHeaders).Append("\r\n");
        return Encoding.UTF8.GetBytes(req.ToString());
    }

    /// <summary>Reads one RTSP response, skipping any interleaved RTP that arrives first.</summary>
    private async Task<(int Status, List<string> Headers, string Body)> ReadResponseAsync(CancellationToken ct)
    {
        var one = new byte[4];
        while (true)
        {
            await ReadExactAsync(one, 1, ct).ConfigureAwait(false);
            if (one[0] == (byte)'$')
            {
                await ReadExactAsync(one.AsMemory(1, 3), ct).ConfigureAwait(false);
                int len = (one[2] << 8) | one[3];
                if (_pkt.Length < len) _pkt = new byte[Math.Max(len, _pkt.Length * 2)];
                await ReadExactAsync(_pkt.AsMemory(0, len), ct).ConfigureAwait(false);
                if (one[1] == _videoChannel) OnRtp(_pkt.AsSpan(0, len));
                continue;
            }
            var (headers, body) = await ReadMessageTailAsync(one[0], ct).ConfigureAwait(false);
            if (headers.Count == 0) continue;
            var first = headers[0];
            if (first.StartsWith("RTSP/", StringComparison.Ordinal))
                return (Status(first), headers, body);
            await AnswerServerRequestAsync(headers, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the rest of an RTSP message whose first byte was already consumed.</summary>
    private async Task<(List<string> Headers, string Body)> ReadMessageTailAsync(byte firstByte, CancellationToken ct)
    {
        var raw = new MemoryStream();
        raw.WriteByte(firstByte);
        var one = new byte[1];
        // The header section ends at a blank line: CRLFCRLF, or LFLF from servers that skip the CR.
        while (true)
        {
            await ReadExactAsync(one, ct).ConfigureAwait(false);
            raw.WriteByte(one[0]);
            if (raw.Length > MaxMessage) throw new IOException("RTSP message too large");
            var b = raw.GetBuffer();
            long n = raw.Length;
            if (n >= 2 && b[n - 1] == '\n'
                && (b[n - 2] == '\n' || (n >= 3 && b[n - 2] == '\r' && b[n - 3] == '\n')))
                break;
        }
        var headText = Encoding.UTF8.GetString(raw.GetBuffer(), 0, (int)raw.Length);
        var headers = headText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        int contentLength = int.TryParse(HeaderOf(headers, "Content-Length"), out var cl) ? cl : 0;
        if (contentLength is < 0 or > MaxMessage) throw new IOException("bad RTSP Content-Length");
        var bodyBytes = new byte[contentLength];
        if (contentLength > 0)
            await ReadExactAsync(bodyBytes, ct).ConfigureAwait(false);
        return (headers, Encoding.UTF8.GetString(bodyBytes));
    }

    private static string? HeaderOf(List<string> headers, string name) =>
        headers.FirstOrDefault(h => h.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1].Trim();

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct) =>
        await _read.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct) =>
        await _read.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);

    // ------------------------------------------------------------------ authentication

    private void BuildAuth(List<string> headers, string method, string url)
    {
        var challenge = headers.FirstOrDefault(h =>
            h.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
        if (challenge == null) throw new IOException("RTSP 401 without WWW-Authenticate");

        if (challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            _digestRealm = ChallengeParam(challenge, "realm");
            _digestNonce = ChallengeParam(challenge, "nonce");
            if (_digestRealm == null || _digestNonce == null)
                throw new IOException("RTSP digest challenge missing realm/nonce");
            _authHeader = DigestHeader(method, url);
        }
        else if (challenge.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_pass}"));
            _authHeader = $"Authorization: Basic {token}";
        }
        else
        {
            throw new IOException($"unsupported RTSP auth scheme: {challenge.Split(' ')[0]}");
        }
    }

    private string DigestHeader(string method, string url)
    {
        static string Md5Hex(string s) =>
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        var ha1 = Md5Hex($"{_user}:{_digestRealm}:{_pass}");
        var ha2 = Md5Hex($"{method}:{url}");
        var response = Md5Hex($"{ha1}:{_digestNonce}:{ha2}");
        return $"Authorization: Digest username=\"{_user}\", realm=\"{_digestRealm}\", " +
               $"nonce=\"{_digestNonce}\", uri=\"{url}\", response=\"{response}\"";
    }

    private static string? ChallengeParam(string challenge, string name)
    {
        var idx = challenge.IndexOf(name + "=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int start = idx + name.Length + 2;
        int end = challenge.IndexOf('"', start);
        return end < 0 ? null : challenge[start..end];
    }

    // ------------------------------------------------------------------ SDP

    internal sealed record VideoTrack(VideoCodec Codec, string Control, byte[]? SpropNals);

    private VideoTrack ParseSdpVideo(string sdp, List<string> describeHeaders) =>
        ParseSdpVideo(sdp, HeaderOf(describeHeaders, "Content-Base")
                           ?? HeaderOf(describeHeaders, "Content-Location") ?? BareUrl);

    /// <summary>Finds the H.264/H.265 video media section and its control URL + sprop parameter sets.</summary>
    internal static VideoTrack ParseSdpVideo(string sdp, string baseUrl)
    {
        // Seen without its scheme ("192.168.253.220:1935/", go2rtc#1852).
        if (!baseUrl.Contains("://", StringComparison.Ordinal)) baseUrl = "rtsp://" + baseUrl;

        var lines = sdp.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        bool inVideo = false;
        VideoCodec? codec = null;
        string? control = null, payloadType = null;
        byte[]? sprop = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inVideo && codec != null) break; // our section is complete
                inVideo = line.StartsWith("m=video", StringComparison.Ordinal);
                continue;
            }
            if (!inVideo) continue;

            if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                VideoCodec? found = line.Contains("H264", StringComparison.OrdinalIgnoreCase) ? VideoCodec.H264
                    : line.Contains("H265", StringComparison.OrdinalIgnoreCase)
                      || line.Contains("HEVC", StringComparison.OrdinalIgnoreCase) ? VideoCodec.H265
                    : null;
                if (found != null)
                {
                    codec = found;
                    payloadType = line["a=rtpmap:".Length..].Split(' ')[0].Trim();
                }
            }
            else if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                control = line["a=control:".Length..].Trim();
            }
            else if (line.StartsWith("a=fmtp:", StringComparison.Ordinal))
            {
                sprop ??= ParseSprop(line);
            }
        }

        if (codec == null)
            throw new IOException("no H.264/H.265 video track in the RTSP DESCRIBE");
        // Some cameras file the video's fmtp under another m= section (go2rtc: WebRTC#419).
        if (sprop == null && payloadType != null)
            sprop = lines.Where(l => l.StartsWith($"a=fmtp:{payloadType} ", StringComparison.Ordinal))
                .Select(ParseSprop).FirstOrDefault(s => s != null);

        // Control may be absolute, relative, or "*" (use the base). A leading '/' must not
        // double the slash (Verint Nextiva answers "//media/1/video/1" with 404, go2rtc#1236).
        string trackUrl = control == null || control == "*"
            ? baseUrl
            : control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                ? control
                : baseUrl.TrimEnd('/') + (control.StartsWith('/') ? "" : "/") + control;
        return new VideoTrack(codec.Value, trackUrl, sprop);
    }

    /// <summary>sprop-parameter-sets / sprop-sps/pps/vps → concatenated Annex-B NALs.</summary>
    private static byte[]? ParseSprop(string fmtpLine)
    {
        var result = new MemoryStream();
        foreach (var key in new[] { "sprop-parameter-sets=", "sprop-vps=", "sprop-sps=", "sprop-pps=" })
        {
            int idx = fmtpLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var value = fmtpLine[(idx + key.Length)..].Split(';')[0].Trim();
            foreach (var b64 in value.Split(','))
            {
                try
                {
                    var nal = Convert.FromBase64String(b64.Trim());
                    if (nal.Length == 0) continue;
                    result.Write(new byte[] { 0, 0, 0, 1 });
                    result.Write(nal);
                }
                catch (FormatException) { }
            }
        }
        return result.Length > 0 ? result.ToArray() : null;
    }

    // ------------------------------------------------------------------ RTP depacketization

    private void OnRtp(ReadOnlySpan<byte> pkt)
    {
        if (pkt.Length < 12 || (pkt[0] >> 6) != 2) return; // not RTP v2
        bool marker = (pkt[1] & 0x80) != 0;
        bool prevMarker = _lastMarker;
        _lastMarker = marker;
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(pkt[2..]);
        // A frame with a hole (packets drop even over TCP) is dropped, not published damaged, as go2rtc
        // and Frigate do. Only a jump forward is a loss: a number repeated or behind (never advanced) is not.
        int ahead = (ushort)(seq - _lastSeq);
        bool lost = _haveSeq && ahead is > 1 and < 0x8000;
        if (!_haveSeq || ahead is > 0 and < 0x8000) _lastSeq = seq;
        _haveSeq = true;
        if (lost)
        {
            _fu.SetLength(0);
            _auBroken = true;
        }
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(pkt[4..]);
        int headerLen = 12 + (pkt[0] & 0x0F) * 4;          // CSRC list
        if ((pkt[0] & 0x10) != 0)                          // extension header
        {
            if (pkt.Length < headerLen + 4) return;
            headerLen += 4 + BinaryPrimitives.ReadUInt16BigEndian(pkt[(headerLen + 2)..]) * 4;
        }
        int padding = (pkt[0] & 0x20) != 0 && pkt.Length > headerLen ? pkt[^1] : 0;
        int payloadLen = pkt.Length - headerLen - padding;
        if (payloadLen <= 0) return;
        var payload = pkt.Slice(headerLen, payloadLen);

        // A new RTP timestamp means a new access unit — flush what we hold.
        if (_haveAuTs && ts != _auTimestamp)
        {
            EmitAccessUnit();
            // The loss opened this one when the last frame had ended, or when more than one packet went.
            if (lost && (prevMarker || ahead > 2)) _auBroken = true;
        }
        _auTimestamp = ts;
        _haveAuTs = true;

        if (_codec == VideoCodec.H264) DepacketizeH264(payload);
        else DepacketizeH265(payload);

        if (marker)
            EmitAccessUnit();
    }

    private void DepacketizeH264(ReadOnlySpan<byte> p)
    {
        int type = p[0] & 0x1F;
        switch (type)
        {
            case >= 0 and <= 23:                       // single NAL unit (0: starts with a start code)
                AddNal(p);
                break;
            case 24:                                   // STAP-A: (len,nal)*
                for (int i = 1; i + 2 <= p.Length;)
                {
                    int len = BinaryPrimitives.ReadUInt16BigEndian(p[i..]);
                    i += 2;
                    if (len <= 0 || i + len > p.Length) break;
                    AddNal(p.Slice(i, len));
                    i += len;
                }
                break;
            case 28:                                   // FU-A fragments
                if (p.Length < 2) return;
                bool start = (p[1] & 0x80) != 0, end = (p[1] & 0x40) != 0;
                if (start)
                {
                    _fu.SetLength(0);
                    _fu.WriteByte((byte)((p[0] & 0xE0) | (p[1] & 0x1F))); // rebuilt NAL header
                }
                AppendFragment(p[2..], end);
                break;
        }
    }

    /// <summary>Adds a fragment to the NAL being reassembled, finishing it on the end bit.
    /// One past the cap is dropped whole: a truncated NAL decodes as garbage.</summary>
    private void AppendFragment(ReadOnlySpan<byte> part, bool end)
    {
        if (_fu.Length == 0) return; // its start fragment was lost
        if (_fu.Length + part.Length > MaxNal)
        {
            _fu.SetLength(0);
            _auBroken = true;
            return;
        }
        _fu.Write(part);
        if (!end) return;
        AddNal(_fu.GetBuffer().AsSpan(0, (int)_fu.Length));
        _fu.SetLength(0);
    }

    private static readonly byte[] StartCode3 = { 0, 0, 1 };

    /// <summary>Adds a NAL — or each of several that a buggy camera packed into one payload behind
    /// start codes (go2rtc: "SPS+PPS+IFrame separated by 00 00 00 01"). A real NAL never contains 00 00 01.</summary>
    private void AddNal(ReadOnlySpan<byte> nal)
    {
        if (nal.IndexOf(StartCode3) < 0)
        {
            _auNals.Add(nal.ToArray());
            return;
        }
        foreach (var unit in H26x.SplitNals((byte[])[.. StartCode, .. nal]))
            if (unit.Length > 0) _auNals.Add(unit.ToArray());
    }

    private void DepacketizeH265(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return;
        int type = (p[0] >> 1) & 0x3F;
        switch (type)
        {
            case 48:                                   // AP: (len,nal)*
                for (int i = 2; i + 2 <= p.Length;)
                {
                    int len = BinaryPrimitives.ReadUInt16BigEndian(p[i..]);
                    i += 2;
                    if (len <= 0 || i + len > p.Length) break;
                    AddNal(p.Slice(i, len));
                    i += len;
                }
                break;
            case 49:                                   // FU fragments
                if (p.Length < 3) return;
                bool start = (p[2] & 0x80) != 0, end = (p[2] & 0x40) != 0;
                if (start)
                {
                    _fu.SetLength(0);
                    int nalType = p[2] & 0x3F;
                    _fu.WriteByte((byte)((p[0] & 0x81) | (nalType << 1)));
                    _fu.WriteByte(p[1]);
                }
                AppendFragment(p[3..], end);
                break;
            default:                                   // single NAL unit
                AddNal(p);
                break;
        }
    }

    private void EmitAccessUnit()
    {
        if (_auBroken)
        {
            _auNals.Clear();
            _auBroken = false;
            return;
        }
        if (_auNals.Count == 0) return;
        bool h264 = _codec == VideoCodec.H264;
        bool keyframe = false, hasSps = false, hasPicture = false;
        foreach (var nal in _auNals)
        {
            int t = h264 ? H26x.H264NalType(nal) : H26x.H265NalType(nal);
            keyframe |= h264 ? t == H26x.H264Idr : t is >= 16 and <= 21; // H.265: BLA/IDR/CRA (IRAP)
            hasSps |= t == (h264 ? H26x.H264Sps : H26x.H265Sps);
            hasPicture |= h264 ? t is >= 1 and <= 5 : t is >= 0 and <= 31;
        }
        if (!hasPicture)
        {
            // Parameter sets sent as a frame of their own (marker bit on SPS/PPS: Tapo TC70,
            // Reolink Duo 2, per go2rtc) lead the next picture instead.
            foreach (var nal in _auNals)
            {
                _paramsAhead.Write(StartCode);
                _paramsAhead.Write(nal);
            }
            _paramsAheadHasSps |= hasSps;
            _auNals.Clear();
            if (_paramsAhead.Length > MaxMessage)
            {
                _paramsAhead.SetLength(0);
                _paramsAheadHasSps = false;
            }
            return;
        }
        hasSps |= _paramsAheadHasSps;

        _au.SetLength(0);
        // Some cameras never repeat SPS/PPS in-band: seed them from the SDP so the
        // hub can answer DESCRIBE/init. Once injected, in-band sets take over.
        if (keyframe && !hasSps && _spropNals != null && !_spropInjected)
        {
            _au.Write(_spropNals);
            _spropInjected = true;
        }
        if (_paramsAhead.Length > 0)
        {
            _au.Write(_paramsAhead.GetBuffer(), 0, (int)_paramsAhead.Length);
            _paramsAhead.SetLength(0);
            _paramsAheadHasSps = false;
        }
        foreach (var nal in _auNals)
        {
            _au.Write(StartCode);
            _au.Write(nal);
        }
        _auNals.Clear();

        // RTP timestamps are 90 kHz; the hub wants a wrapping microsecond counter.
        uint microseconds = unchecked((uint)(_auTimestamp * 100UL / 9));
        StreamingSince ??= DateTime.UtcNow;
        _sink.PublishVideo(new VideoFrame(_codec, keyframe, microseconds, null, _au.ToArray()));
    }

    /// <summary>Test seam: one RTP packet from the video channel.</summary>
    internal void FeedRtpForTest(byte[] pkt) => OnRtp(pkt);
}
