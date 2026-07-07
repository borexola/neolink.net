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
    private const int MaxMessage = 1 << 20;   // sanity cap for RTSP replies and RTP frames

    private readonly string _tag;
    private readonly Uri _url;
    private readonly string? _user;
    private readonly string? _pass;
    private readonly IMediaSink _sink;

    private NetworkStream _net = null!;
    private int _cseq;
    private string? _session;
    private string? _authHeader;        // computed after a 401 challenge, reused afterwards
    private string? _digestRealm, _digestNonce;

    // Depacketization state
    private VideoCodec _codec = VideoCodec.H264;
    private readonly List<byte[]> _auNals = new();
    private readonly MemoryStream _fu = new();
    private uint _auTimestamp;
    private bool _haveAuTs;
    private byte[]? _spropNals;         // SPS/PPS(/VPS) from the SDP, injected before the first keyframe
    private bool _spropInjected;

    /// <param name="url">rtsp://[user:pass@]host[:port]/path — credentials may ride the URL.</param>
    public RtspPuller(string tag, string url, IMediaSink sink)
    {
        _tag = tag;
        _url = new Uri(url);
        _sink = sink;
        if (_url.UserInfo.Length > 0)
        {
            var parts = _url.UserInfo.Split(':', 2);
            _user = Uri.UnescapeDataString(parts[0]);
            _pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
    }

    /// <summary>URL without credentials, safe for request lines and logs.</summary>
    private string BareUrl =>
        $"rtsp://{_url.Host}:{(_url.Port > 0 ? _url.Port : 554)}{_url.PathAndQuery}";

    /// <summary>Connects, negotiates and pumps RTP until cancelled or the peer drops.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(_url.Host, _url.Port > 0 ? _url.Port : 554, connectCts.Token).ConfigureAwait(false);
        tcp.NoDelay = true;
        _net = tcp.GetStream();

        var (sdp, contentBase) = await DescribeAsync(ct).ConfigureAwait(false);
        var track = ParseSdpVideo(sdp, contentBase);
        _codec = track.Codec;
        _spropNals = track.SpropNals;
        Log.Info($"{_tag}: RTSP video track {track.Codec} ({track.Control})");

        var setup = await RequestAsync("SETUP", track.Control,
            "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n", ct).ConfigureAwait(false);
        _session = (HeaderOf(setup.Headers, "Session") ?? "").Split(';')[0].Trim();
        if (_session.Length == 0) throw new IOException("RTSP SETUP returned no session");

        await RequestAsync("PLAY", BareUrl, "Range: npt=0.000-\r\n", ct).ConfigureAwait(false);

        var lastKeepAlive = DateTime.UtcNow;
        var buf4 = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            // Keep-alives ride the same socket; the reply is consumed inline below.
            if (DateTime.UtcNow - lastKeepAlive > KeepAliveEvery)
            {
                lastKeepAlive = DateTime.UtcNow;
                await SendRequestAsync("GET_PARAMETER", BareUrl, "", ct).ConfigureAwait(false);
            }

            await ReadExactAsync(buf4, 1, ct).ConfigureAwait(false);
            if (buf4[0] == (byte)'$')
            {
                // Interleaved binary: channel byte + big-endian length + payload.
                await ReadExactAsync(buf4.AsMemory(1, 3), ct).ConfigureAwait(false);
                int channel = buf4[1];
                int len = (buf4[2] << 8) | buf4[3];
                var payload = new byte[len];
                await ReadExactAsync(payload, ct).ConfigureAwait(false);
                if (channel == 0)
                    OnRtp(payload);
                // channel 1 = RTCP sender reports — nothing we need
            }
            else
            {
                // An inline RTSP message (keep-alive reply or a server request):
                // consume it whole so framing stays intact.
                await ReadMessageTailAsync(buf4[0], ct).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ RTSP plumbing

    private async Task<(string Body, List<string> Headers)> DescribeAsync(CancellationToken ct)
    {
        var res = await RequestAsync("DESCRIBE", BareUrl, "Accept: application/sdp\r\n", ct).ConfigureAwait(false);
        return (res.Body, res.Headers);
    }

    /// <summary>Sends a request and reads its response, retrying once with credentials on 401.</summary>
    private async Task<(int Status, List<string> Headers, string Body)> RequestAsync(
        string method, string url, string extraHeaders, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await SendRequestAsync(method, url, extraHeaders, ct).ConfigureAwait(false);
            var res = await ReadResponseAsync(ct).ConfigureAwait(false);
            if (res.Status == 401 && attempt == 0 && _user != null)
            {
                BuildAuth(res.Headers, method, url);
                continue;
            }
            if (res.Status is not (200 or 0))
                throw new IOException($"RTSP {method} failed: {res.Status}");
            return res;
        }
    }

    private async Task SendRequestAsync(string method, string url, string extraHeaders, CancellationToken ct)
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
        var bytes = Encoding.UTF8.GetBytes(req.ToString());
        await _net.WriteAsync(bytes, ct).ConfigureAwait(false);
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
                var payload = new byte[len];
                await ReadExactAsync(payload, ct).ConfigureAwait(false);
                if (one[1] == 0) OnRtp(payload);
                continue;
            }
            var (headers, body) = await ReadMessageTailAsync(one[0], ct).ConfigureAwait(false);
            if (headers.Count == 0) continue;
            var first = headers[0];
            if (first.StartsWith("RTSP/", StringComparison.Ordinal))
            {
                var parts = first.Split(' ');
                int status = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
                return (status, headers, body);
            }
            // A server-initiated request (e.g. OPTIONS ping) — ignored; loop on.
        }
    }

    /// <summary>Reads the rest of an RTSP message whose first byte was already consumed.</summary>
    private async Task<(List<string> Headers, string Body)> ReadMessageTailAsync(byte firstByte, CancellationToken ct)
    {
        var raw = new MemoryStream();
        raw.WriteByte(firstByte);
        var one = new byte[1];
        // Header section ends at CRLFCRLF.
        while (true)
        {
            await ReadExactAsync(one, ct).ConfigureAwait(false);
            raw.WriteByte(one[0]);
            if (raw.Length > MaxMessage) throw new IOException("RTSP message too large");
            var b = raw.GetBuffer();
            if (raw.Length >= 4
                && b[raw.Length - 4] == '\r' && b[raw.Length - 3] == '\n'
                && b[raw.Length - 2] == '\r' && b[raw.Length - 1] == '\n')
                break;
        }
        var headText = Encoding.UTF8.GetString(raw.GetBuffer(), 0, (int)raw.Length);
        var headers = headText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
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
        await _net.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct) =>
        await _net.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);

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

    private sealed record VideoTrack(VideoCodec Codec, string Control, byte[]? SpropNals);

    /// <summary>Finds the H.264/H.265 video media section and its control URL + sprop parameter sets.</summary>
    private VideoTrack ParseSdpVideo(string sdp, List<string> describeHeaders)
    {
        string baseUrl = HeaderOf(describeHeaders, "Content-Base")
            ?? HeaderOf(describeHeaders, "Content-Location") ?? BareUrl;

        var lines = sdp.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        bool inVideo = false;
        VideoCodec? codec = null;
        string? control = null;
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
                if (line.Contains("H264", StringComparison.OrdinalIgnoreCase)) codec = VideoCodec.H264;
                else if (line.Contains("H265", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("HEVC", StringComparison.OrdinalIgnoreCase)) codec = VideoCodec.H265;
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

        // Control may be absolute, relative, or "*" (use the base).
        string trackUrl = control == null || control == "*"
            ? baseUrl
            : control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                ? control
                : baseUrl.TrimEnd('/') + "/" + control;
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

    private void OnRtp(byte[] pkt)
    {
        if (pkt.Length < 12 || (pkt[0] >> 6) != 2) return; // not RTP v2
        bool marker = (pkt[1] & 0x80) != 0;
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(pkt.AsSpan(4));
        int headerLen = 12 + (pkt[0] & 0x0F) * 4;          // CSRC list
        if ((pkt[0] & 0x10) != 0)                          // extension header
        {
            if (pkt.Length < headerLen + 4) return;
            headerLen += 4 + BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(headerLen + 2)) * 4;
        }
        int padding = (pkt[0] & 0x20) != 0 && pkt.Length > headerLen ? pkt[^1] : 0;
        int payloadLen = pkt.Length - headerLen - padding;
        if (payloadLen <= 0) return;
        var payload = pkt.AsSpan(headerLen, payloadLen);

        // A new RTP timestamp means a new access unit — flush what we hold.
        if (_haveAuTs && ts != _auTimestamp)
            EmitAccessUnit();
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
            case >= 1 and <= 23:                       // single NAL unit
                _auNals.Add(p.ToArray());
                break;
            case 24:                                   // STAP-A: (len,nal)*
                for (int i = 1; i + 2 <= p.Length;)
                {
                    int len = BinaryPrimitives.ReadUInt16BigEndian(p[i..]);
                    i += 2;
                    if (len <= 0 || i + len > p.Length) break;
                    _auNals.Add(p.Slice(i, len).ToArray());
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
                if (_fu.Length > 0 && _fu.Length + p.Length < MaxMessage)
                    _fu.Write(p[2..]);
                if (end && _fu.Length > 0)
                {
                    _auNals.Add(_fu.ToArray());
                    _fu.SetLength(0);
                }
                break;
        }
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
                    _auNals.Add(p.Slice(i, len).ToArray());
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
                if (_fu.Length > 0 && _fu.Length + p.Length < MaxMessage)
                    _fu.Write(p[3..]);
                if (end && _fu.Length > 0)
                {
                    _auNals.Add(_fu.ToArray());
                    _fu.SetLength(0);
                }
                break;
            default:                                   // single NAL unit
                _auNals.Add(p.ToArray());
                break;
        }
    }

    private void EmitAccessUnit()
    {
        if (_auNals.Count == 0) return;
        bool keyframe = _auNals.Any(n => _codec == VideoCodec.H264
            ? H26x.H264NalType(n) == H26x.H264Idr
            : H26x.H265NalType(n) is >= 16 and <= 21); // BLA/IDR/CRA (IRAP)
        bool hasParams = _auNals.Any(n => _codec == VideoCodec.H264
            ? H26x.H264NalType(n) == H26x.H264Sps
            : H26x.H265NalType(n) == H26x.H265Sps);

        var au = new MemoryStream();
        // Some cameras never repeat SPS/PPS in-band: seed them from the SDP so the
        // hub can answer DESCRIBE/init. Once injected, in-band sets take over.
        if (keyframe && !hasParams && _spropNals != null && !_spropInjected)
        {
            au.Write(_spropNals);
            _spropInjected = true;
        }
        foreach (var nal in _auNals)
        {
            au.Write(new byte[] { 0, 0, 0, 1 });
            au.Write(nal);
        }
        _auNals.Clear();

        // RTP timestamps are 90 kHz; the hub wants a wrapping microsecond counter.
        uint microseconds = unchecked((uint)(_auTimestamp * 100UL / 9));
        _sink.PublishVideo(new VideoFrame(_codec, keyframe, microseconds, null, au.ToArray()));
    }
}
