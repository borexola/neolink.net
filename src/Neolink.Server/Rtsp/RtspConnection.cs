// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Neolink.Media;
using Neolink.Streaming;

namespace Neolink.Rtsp;

/// <summary>One RTSP control connection and the sessions/pumps created on it.</summary>
public sealed class RtspConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly RtspServer _server;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, RtspSession> _sessions = new();
    private readonly byte[] _readBuf = new byte[8192];
    private readonly EndPoint? _remote;
    /// <summary>Once-per-process flag for the "Opus requested but no usable ffmpeg" refusal log.</summary>
    private static int _opusRefusalLogged;
    /// <summary>Unsupported ?audio= values already logged: one line each (a retrying
    /// client would spam), capped so hostile scanners can't grow it unbounded.</summary>
    private static readonly HashSet<string> UnknownAudioLogged = new();
    /// <summary>The audio choice the last DESCRIBE on this connection settled on.
    /// SETUP falls back to it when its own URI carries no ?audio= — some clients
    /// resolve control URIs strictly per RFC 3986 and drop the query.</summary>
    private bool? _describedOpus;
    private int _readLen;
    private int _readPos;

    public RtspConnection(TcpClient client, RtspServer server)
    {
        _client = client;
        _stream = client.GetStream();
        _server = server;
        _remote = client.Client.RemoteEndPoint;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Debug($"RTSP client connected: {_remote}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int first = await PeekByteAsync(ct).ConfigureAwait(false);
                if (first < 0) break;

                if (first == '$')
                {
                    // Interleaved client data: RTCP receiver reports (ignored) or, on a
                    // backchannel channel, G.711 audio the client is talking to the camera.
                    var head = await ReadExactAsync(4, ct).ConfigureAwait(false);
                    byte channel = head[1];
                    int len = (head[2] << 8) | head[3];
                    var payload = await ReadExactAsync(len, ct).ConfigureAwait(false);
                    RouteInterleaved(channel, payload);
                    continue;
                }

                var request = await ReadRequestAsync(ct).ConfigureAwait(false);
                if (request == null) break;
                await HandleRequestAsync(request, ct).ConfigureAwait(false);
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { } // connection killed by the stalled-client guard
        finally
        {
            foreach (var session in _sessions.Values)
                session.Stop();
            _sessions.Clear();
            _client.Dispose();
            Log.Debug($"RTSP client disconnected: {_remote}");
        }
    }

    // ------------------------------------------------------------- request parsing

    private sealed class RtspRequest
    {
        public required string Method;
        public required string Uri;
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public string CSeq => Headers.GetValueOrDefault("CSeq", "0");
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private async Task<RtspRequest?> ReadRequestAsync(CancellationToken ct)
    {
        string? requestLine = await ReadLineAsync(ct).ConfigureAwait(false);
        while (requestLine == "")
            requestLine = await ReadLineAsync(ct).ConfigureAwait(false);
        if (requestLine == null) return null;

        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 3)
        {
            Log.Debug($"Malformed RTSP request line: {requestLine}");
            return null;
        }
        var req = new RtspRequest { Method = parts[0].ToUpperInvariant(), Uri = parts[1] };

        while (true)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return null;
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon > 0)
                req.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (int.TryParse(req.Header("Content-Length"), out int bodyLen) && bodyLen > 0)
            await ReadExactAsync(bodyLen, ct).ConfigureAwait(false); // read and discard

        return req;
    }

    // ------------------------------------------------------------- request handling

    private async Task HandleRequestAsync(RtspRequest req, CancellationToken ct)
    {
        Log.Debug($"RTSP {req.Method} {req.Uri}");
        switch (req.Method)
        {
            case "OPTIONS":
                await RespondAsync(req, 200, "OK",
                    "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, GET_PARAMETER, SET_PARAMETER, TEARDOWN",
                    ct: ct).ConfigureAwait(false);
                break;
            case "DESCRIBE":
                await HandleDescribeAsync(req, ct).ConfigureAwait(false);
                break;
            case "SETUP":
                await HandleSetupAsync(req, ct).ConfigureAwait(false);
                break;
            case "PLAY":
                await HandlePlayAsync(req, ct).ConfigureAwait(false);
                break;
            case "PAUSE":
                await HandlePauseAsync(req, ct).ConfigureAwait(false);
                break;
            case "GET_PARAMETER":
            case "SET_PARAMETER":
                await RespondAsync(req, 200, "OK", sessionId: req.Header("Session"), ct: ct).ConfigureAwait(false);
                break;
            case "TEARDOWN":
                await HandleTeardownAsync(req, ct).ConfigureAwait(false);
                break;
            default:
                await RespondAsync(req, 501, "Not Implemented", ct: ct).ConfigureAwait(false);
                break;
        }
    }

    private (RtspMount? mount, string path, int trackId, string? audio) ResolveUri(string uri)
    {
        var (path, trackId, audio) = ParseUri(uri);
        return (_server.FindMount(path), path, trackId, audio);
    }

    /// <summary>Splits a request URI into mount path, trackID and the ?audio= query
    /// value. The SDP's control attribute is the relative "trackID=N", and clients
    /// resolve it against our Content-Base in two different-looking ways once a
    /// query is in play: a URL-aware resolver joins the PATH and keeps the query
    /// last ("/cam/trackID=1?audio=opus"), while one that concatenates the
    /// Content-Base string verbatim leaves the marker trailing the query
    /// ("/cam?audio=opus/trackID=1"). Both must resolve to the same track — reading
    /// the trackID out of only one of the two positions silently turns the audio
    /// SETUP into a second video track, and the stream plays with no sound.</summary>
    internal static (string path, int trackId, string? audio) ParseUri(string uri)
    {
        string rest = uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            rest = parsed.PathAndQuery;

        string path = rest, query = "";
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            path = rest[..q];
            query = rest[(q + 1)..];
        }

        // Query first: that is where a verbatim Content-Base concatenation puts it.
        int trackId = TakeTrackId(ref query);
        if (trackId < 0) trackId = TakeTrackId(ref path);

        string? audio = null;
        // Only "audio" is understood today; unknown KEYS are ignored so future
        // parameters can be added without breaking older servers or clients.
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("audio", StringComparison.OrdinalIgnoreCase))
                audio = Uri.UnescapeDataString(kv[1]).Trim().ToLowerInvariant();
        }
        return (Uri.UnescapeDataString(path), trackId, audio);
    }

    /// <summary>Cuts a "/trackID=N" marker out of <paramref name="s"/> and returns N,
    /// or -1 when there is none. Only the DIGITS are consumed, so whatever follows
    /// the id (a query the client kept on the end) survives in place.</summary>
    private static int TakeTrackId(ref string s)
    {
        const string marker = "/trackID=";
        int idx = s.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return -1;
        int start = idx + marker.Length, end = start;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        if (end == start || !int.TryParse(s[start..end], out int id)) return -1;
        s = s[..idx] + s[end..];
        return id;
    }

    /// <summary>Maps a ?audio= value to a session's Opus choice: null when the URL
    /// names none (the mount's audio_transcode default applies), true/false for
    /// opus/original. Returns false for a value that is not a supported format.</summary>
    internal static bool TryMapAudio(string? audio, out bool? opus)
    {
        opus = audio switch { "opus" => true, "original" => false, _ => null };
        return audio is null or "opus" or "original";
    }

    private async Task<bool> CheckAuthAsync(RtspRequest req, RtspMount mount, CancellationToken ct)
    {
        if (_server.Authorize(mount, req.Header("Authorization")))
            return true;
        await RespondAsync(req, 401, "Unauthorized", "WWW-Authenticate: Basic realm=\"neolink\"", ct: ct).ConfigureAwait(false);
        return false;
    }

    private async Task HandleDescribeAsync(RtspRequest req, CancellationToken ct)
    {
        var (mount, path, _, audio) = ResolveUri(req.Uri);
        if (mount == null)
        {
            await RespondAsync(req, 404, "Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        if (!await CheckAuthAsync(req, mount, ct).ConfigureAwait(false)) return;

        // ?audio= picks this client's codec. An unsupported format is refused
        // loudly — a silent fallback would leave the user debugging why the
        // codec they asked for never arrived.
        if (!TryMapAudio(audio, out var audioChoice))
        {
            bool logIt;
            lock (UnknownAudioLogged)
                logIt = UnknownAudioLogged.Count < 16 && UnknownAudioLogged.Add(audio!);
            if (logIt)
                Log.Warn($"DESCRIBE {path}: unsupported audio format \"{audio}\" — " +
                         "supported: ?audio=opus, ?audio=original — answering 404");
            await RespondAsync(req, 404, "Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        bool opus = audioChoice ?? mount.Opus;

        // Opus is only honest when the located ffmpeg can actually encode it
        // (probed once, lazily). Refusing here beats promising Opus in the SDP
        // and delivering silence.
        if (opus && !Media.Ffmpeg.SupportsOpus)
        {
            if (Interlocked.Exchange(ref _opusRefusalLogged, 1) == 0)
                Log.Warn($"DESCRIBE {path}: Opus audio was requested but " +
                         (Media.Ffmpeg.ExePath == null
                             ? "no ffmpeg was found (install one on PATH or set NEOLINK_FFMPEG)"
                             : "this ffmpeg has no libopus encoder") +
                         " — ?audio=opus answers 404 until that changes");
            await RespondAsync(req, 404, "Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        _describedOpus = opus;

        if (!await mount.Hub.WaitForDescribeInfoAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
        {
            Log.Warn($"DESCRIBE {path}: stream not ready (camera offline or still connecting)");
            await RespondAsync(req, 503, "Service Unavailable", "Retry-After: 5", ct: ct).ConfigureAwait(false);
            return;
        }

        // ONVIF backchannel is opt-in: only add the sendonly talk track when the
        // client asks for it (Require: ...backchannel) AND the camera has a speaker.
        // Plain players (VLC, ffmpeg, go2rtc without backchannel) never send the
        // header, so their SDP is unchanged.
        bool backchannel = false;
        if (WantsBackchannel(req) && mount.Talk != null)
        {
            try
            {
                var caps = await mount.Talk.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                backchannel = caps.Features.Talk;
            }
            catch (Exception ex)
            {
                Log.Debug($"{mount.Hub.Name}: backchannel capability probe failed: {Log.Flatten(ex)}");
            }
        }

        string sdp = Sdp.Build(mount.Hub, mount.Hub.Name, backchannel, opus: opus);
        string contentBase = req.Uri.TrimEnd('/') + "/";
        await RespondAsync(req, 200, "OK",
            $"Content-Base: {contentBase}\r\nContent-Type: application/sdp",
            body: Encoding.ASCII.GetBytes(sdp), ct: ct).ConfigureAwait(false);
    }

    /// <summary>Whether the request opts into the ONVIF two-way-talk backchannel.</summary>
    private static bool WantsBackchannel(RtspRequest req)
    {
        var require = req.Header("Require");
        return require != null && require.Contains("backchannel", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleSetupAsync(RtspRequest req, CancellationToken ct)
    {
        var (mount, _, trackId, audio) = ResolveUri(req.Uri);
        if (mount == null)
        {
            await RespondAsync(req, 404, "Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        if (!await CheckAuthAsync(req, mount, ct).ConfigureAwait(false)) return;
        if (trackId is not (0 or 1 or Sdp.BackchannelTrackId)) trackId = 0;

        var transportHeader = req.Header("Transport");
        if (transportHeader == null)
        {
            await RespondAsync(req, 461, "Unsupported Transport", ct: ct).ConfigureAwait(false);
            return;
        }

        // Find or create the session
        RtspSession? session = null;
        var sessionId = req.Header("Session");
        if (sessionId != null)
            _sessions.TryGetValue(sessionId, out session);
        if (session == null)
        {
            // The codec choice travels on the query: this URI's own ?audio= wins,
            // else what the DESCRIBE on this connection settled on, else the mount
            // default. Unknown values were already policed at DESCRIBE.
            bool opus = (TryMapAudio(audio, out var choice) ? choice : null)
                ?? _describedOpus ?? mount.Opus;
            if (opus && !Media.Ffmpeg.SupportsOpus)
                opus = false; // DESCRIBE-less client; never promise silence
            session = new RtspSession(this, mount, opus);
            _sessions[session.Id] = session;
        }
        else if (session.Playing)
        {
            await RespondAsync(req, 455, "Method Not Valid in This State", sessionId: session.Id, ct: ct).ConfigureAwait(false);
            return;
        }

        var spec = ParseTransport(transportHeader);
        string responseTransport;

        if (trackId == Sdp.BackchannelTrackId)
        {
            // Backchannel: the client SENDS G.711 audio to us. Only TCP-interleaved
            // is accepted (go2rtc's default; forces a retry for the rare UDP case).
            if (mount.Talk == null || !spec.Tcp)
            {
                await RespondAsync(req, 461, "Unsupported Transport", sessionId: session.Id, ct: ct).ConfigureAwait(false);
                return;
            }
            int ch = spec.InterleavedRtp ?? Sdp.BackchannelTrackId * 2;
            session.SetupBackchannel((byte)ch, mount.Talk);
            responseTransport = $"RTP/AVP/TCP;unicast;mode=record;interleaved={ch}-{ch + 1}";
        }
        else
        {
            var packetizer = new RtpPacketizer(trackId == 0 ? Sdp.VideoPayloadType : Sdp.AudioPayloadType);
            if (spec.Tcp)
            {
                int ch = spec.InterleavedRtp ?? trackId * 2;
                session.SetTrack(trackId, TrackTransport.ForTcp((byte)ch, packetizer));
                responseTransport = $"RTP/AVP/TCP;unicast;interleaved={ch}-{ch + 1};ssrc={packetizer.Ssrc:X8}";
            }
            else if (spec.ClientRtpPort.HasValue)
            {
                var remoteIp = ((IPEndPoint)_client.Client.RemoteEndPoint!).Address;
                var target = new IPEndPoint(remoteIp, spec.ClientRtpPort.Value);
                var (transport, rtpPort, rtcpPort) = TrackTransport.ForUdp(target, packetizer);
                session.SetTrack(trackId, transport);
                responseTransport =
                    $"RTP/AVP;unicast;client_port={spec.ClientRtpPort}-{spec.ClientRtpPort + 1};" +
                    $"server_port={rtpPort}-{rtcpPort};ssrc={packetizer.Ssrc:X8}";
            }
            else
            {
                await RespondAsync(req, 461, "Unsupported Transport", ct: ct).ConfigureAwait(false);
                return;
            }
        }

        await RespondAsync(req, 200, "OK",
            $"Transport: {responseTransport}",
            sessionId: session.Id, ct: ct).ConfigureAwait(false);
    }

    private sealed record TransportSpec(bool Tcp, int? InterleavedRtp, int? ClientRtpPort);

    private static TransportSpec ParseTransport(string header)
    {
        // Clients may offer several transports separated by ','; take the first supported one.
        foreach (var offer in header.Split(','))
        {
            var fields = offer.Trim().Split(';');
            var proto = fields[0].Trim().ToUpperInvariant();
            bool tcp = proto.Contains("/TCP");
            if (!proto.StartsWith("RTP/AVP")) continue;

            int? interleaved = null, clientPort = null;
            foreach (var f in fields.Skip(1))
            {
                var kv = f.Split('=', 2);
                var key = kv[0].Trim().ToLowerInvariant();
                if (kv.Length == 2)
                {
                    var range = kv[1].Split('-');
                    if (key == "interleaved" && int.TryParse(range[0], out var i)) interleaved = i;
                    if (key == "client_port" && int.TryParse(range[0], out var p)) clientPort = p;
                }
            }
            if (tcp || clientPort.HasValue)
                return new TransportSpec(tcp, interleaved, clientPort);
        }
        return new TransportSpec(false, null, null);
    }

    private async Task HandlePlayAsync(RtspRequest req, CancellationToken ct)
    {
        var session = FindSession(req);
        if (session == null)
        {
            await RespondAsync(req, 454, "Session Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        session.Play(ct);

        var rtpInfoParts = new List<string>();
        string baseUri = req.Uri.TrimEnd('/');
        if (session.Video != null)
            rtpInfoParts.Add($"url={baseUri}/trackID=0;seq={session.Video.Packetizer.Seq}");
        if (session.Audio != null)
            rtpInfoParts.Add($"url={baseUri}/trackID=1;seq={session.Audio.Packetizer.Seq}");

        await RespondAsync(req, 200, "OK",
            $"Range: npt=now-\r\nRTP-Info: {string.Join(",", rtpInfoParts)}",
            sessionId: session.Id, ct: ct).ConfigureAwait(false);
    }

    private async Task HandlePauseAsync(RtspRequest req, CancellationToken ct)
    {
        var session = FindSession(req);
        if (session == null)
        {
            await RespondAsync(req, 454, "Session Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        session.Pause();
        await RespondAsync(req, 200, "OK", sessionId: session.Id, ct: ct).ConfigureAwait(false);
    }

    private async Task HandleTeardownAsync(RtspRequest req, CancellationToken ct)
    {
        var session = FindSession(req);
        if (session != null)
        {
            session.Stop();
            _sessions.Remove(session.Id);
        }
        await RespondAsync(req, 200, "OK", sessionId: session?.Id, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Routes an interleaved client frame to the backchannel receiver that
    /// owns its channel; anything else (RTCP receiver reports) is ignored.</summary>
    private void RouteInterleaved(byte channel, byte[] rtpPacket)
    {
        foreach (var s in _sessions.Values)
            if (s.BackchannelChannel == channel)
            {
                s.FeedBackchannel(rtpPacket);
                return;
            }
    }

    private RtspSession? FindSession(RtspRequest req)
    {
        var id = req.Header("Session");
        if (id == null)
            return _sessions.Count == 1 ? _sessions.Values.First() : null;
        return _sessions.GetValueOrDefault(id.Split(';')[0].Trim());
    }

    // ------------------------------------------------------------- responses & I/O

    private async Task RespondAsync(RtspRequest req, int code, string reason,
        string? extraHeaders = null, string? sessionId = null, byte[]? body = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append($"RTSP/1.0 {code} {reason}\r\n");
        sb.Append($"CSeq: {req.CSeq}\r\n");
        sb.Append("Server: Neolink.NET\r\n");
        if (sessionId != null) sb.Append($"Session: {sessionId};timeout=60\r\n");
        if (extraHeaders != null) sb.Append(extraHeaders).Append("\r\n");
        sb.Append($"Content-Length: {body?.Length ?? 0}\r\n");
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(head, ct).ConfigureAwait(false);
            if (body != null)
                await _stream.WriteAsync(body, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sends an RTP packet over the control connection (interleaved framing).</summary>
    internal async Task SendInterleavedAsync(byte channel, byte[] rtpPacket, CancellationToken ct)
    {
        var frame = new byte[4 + rtpPacket.Length];
        frame[0] = 0x24; // '$'
        frame[1] = channel;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)rtpPacket.Length);
        rtpPacket.CopyTo(frame, 4);
        await WriteGuardedAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one frame's worth of RTP packets as a SINGLE write. Interleaved video
    /// used to cost one send syscall per RTP packet — ~700/s per viewer on a 4K
    /// stream, which is real CPU under virtualization. All the '$'-framed packets
    /// of an access unit are coalesced into one pooled buffer and one write.
    /// </summary>
    internal async Task SendInterleavedBatchAsync(byte channel, List<byte[]> rtpPackets, CancellationToken ct)
    {
        if (rtpPackets.Count == 0) return;
        int total = 0;
        foreach (var p in rtpPackets) total += 4 + p.Length;
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
        try
        {
            int off = 0;
            foreach (var p in rtpPackets)
            {
                buf[off] = 0x24; // '$'
                buf[off + 1] = channel;
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off + 2), (ushort)p.Length);
                p.CopyTo(buf, off + 4);
                off += 4 + p.Length;
            }
            await WriteGuardedAsync(buf.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private async Task WriteGuardedAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // slow-client guard
        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(data, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client stopped reading. A half-dead connection is worse than a dead
            // one (the client sits blind until its own watchdog fires), so close it
            // outright; that also wakes the request loop, which cleans up the sessions.
            Log.Warn($"RTSP client {_remote} stalled (did not accept data for 10s); closing connection");
            _client.Dispose();
            throw new IOException("RTSP client stalled");
        }
    }

    private async Task<int> PeekByteAsync(CancellationToken ct)
    {
        if (_readPos >= _readLen)
        {
            _readLen = await _stream.ReadAsync(_readBuf, ct).ConfigureAwait(false);
            _readPos = 0;
            if (_readLen == 0) return -1;
        }
        return _readBuf[_readPos];
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        int done = 0;
        while (done < count)
        {
            if (_readPos >= _readLen)
            {
                _readLen = await _stream.ReadAsync(_readBuf, ct).ConfigureAwait(false);
                _readPos = 0;
                if (_readLen == 0) throw new IOException("connection closed");
            }
            int n = Math.Min(count - done, _readLen - _readPos);
            Array.Copy(_readBuf, _readPos, result, done, n);
            _readPos += n;
            done += n;
        }
        return result;
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_readPos >= _readLen)
            {
                _readLen = await _stream.ReadAsync(_readBuf, ct).ConfigureAwait(false);
                _readPos = 0;
                if (_readLen == 0) return sb.Length > 0 ? sb.ToString() : null;
            }
            byte b = _readBuf[_readPos++];
            if (b == '\n')
                return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
            if (sb.Length > 16384) throw new IOException("RTSP line too long");
        }
    }
}

// ===================================================================== session

internal sealed class TrackTransport
{
    public bool Tcp { get; private init; }
    public byte RtpChannel { get; private init; }
    public IPEndPoint? ClientEndpoint { get; private init; }
    public Socket? UdpSocket { get; private init; }
    public Socket? UdpRtcpSocket { get; private init; }
    public required RtpPacketizer Packetizer { get; init; }

    public static TrackTransport ForTcp(byte channel, RtpPacketizer packetizer) =>
        new() { Tcp = true, RtpChannel = channel, Packetizer = packetizer };

    public static (TrackTransport transport, int rtpPort, int rtcpPort) ForUdp(IPEndPoint client, RtpPacketizer packetizer)
    {
        var rtp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var rtcp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        rtp.Bind(new IPEndPoint(IPAddress.Any, 0));
        rtcp.Bind(new IPEndPoint(IPAddress.Any, 0));
        var t = new TrackTransport
        {
            Tcp = false,
            ClientEndpoint = client,
            UdpSocket = rtp,
            UdpRtcpSocket = rtcp,
            Packetizer = packetizer,
        };
        return (t, ((IPEndPoint)rtp.LocalEndPoint!).Port, ((IPEndPoint)rtcp.LocalEndPoint!).Port);
    }

    public void Close()
    {
        UdpSocket?.Dispose();
        UdpRtcpSocket?.Dispose();
    }
}

internal sealed class RtspSession
{
    public string Id { get; } = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..8]);
    public TrackTransport? Video { get; private set; }
    public TrackTransport? Audio { get; private set; }
    public bool Playing => _pumpTask is { IsCompleted: false };

    /// <summary>Interleaved channel the client sends backchannel audio on, or null.</summary>
    public byte? BackchannelChannel { get; private set; }

    private readonly RtspConnection _conn;
    private readonly RtspMount _mount;
    /// <summary>This session's audio: transcoded Opus or the camera's original
    /// track. Settled at SETUP from the URL's ?audio= (falling back to the mount's
    /// audio_transcode default) and fixed for the session's lifetime.</summary>
    private readonly bool _opus;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private BackchannelReceiver? _backchannel;

    public RtspSession(RtspConnection conn, RtspMount mount, bool opus)
    {
        _conn = conn;
        _mount = mount;
        _opus = opus;
    }

    public void SetTrack(int trackId, TrackTransport transport)
    {
        if (trackId == 0) Video = transport;
        else Audio = transport;
    }

    /// <summary>Registers a two-way-talk receive track on an interleaved channel.</summary>
    public void SetupBackchannel(byte channel, ICameraControl talk)
    {
        BackchannelChannel = channel;
        _backchannel = new BackchannelReceiver(talk);
    }

    public void FeedBackchannel(byte[] rtpPacket) => _backchannel?.OnRtp(rtpPacket);

    public void Play(CancellationToken ct)
    {
        // The backchannel is tied to the connection lifetime, not the media pump, so
        // PAUSE doesn't cut talk; it ends on TEARDOWN or disconnect (via Stop).
        _backchannel?.Start(ct);
        if (Playing) return;
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _pumpCts.Token;
        _pumpTask = Task.Run(() => PumpAsync(token), CancellationToken.None);
    }

    public void Pause()
    {
        _pumpCts?.Cancel();
        _pumpTask = null;
    }

    public void Stop()
    {
        _pumpCts?.Cancel();
        _pumpTask = null;
        _backchannel?.Stop();
        Video?.Close();
        Audio?.Close();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var hub = _mount.Hub;
        var (subId, reader) = hub.Subscribe(viewer: true);
        long lastIndex = -1;
        bool waitKeyframe = true; // always start on a keyframe

        // Opus sessions vote for the transcoder: ffmpeg runs only while at least
        // one Opus session is playing, and stops with the last one.
        if (_opus) hub.AcquireOpus();
        Log.Info($"{hub.Name}: client started streaming (session {Id}" +
                 $"{(_opus ? ", Opus audio" : "")})");
        try
        {
            await foreach (var packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                bool gap = lastIndex >= 0 && packet.Index != lastIndex + 1;
                lastIndex = packet.Index;
                if (gap) waitKeyframe = true;

                switch (packet)
                {
                    case HubVideo v when Video != null:
                    {
                        if (waitKeyframe)
                        {
                            if (!v.Keyframe) continue;
                            waitKeyframe = false;
                        }
                        var codec = hub.Codec ?? VideoCodec.H264;
                        var au = v.Keyframe ? EnsureParameterSets(hub, codec, v.AnnexB) : v.AnnexB;
                        var packets = Video.Packetizer.PacketizeVideo(codec, au, v.RtpTs);
                        if (Video.Tcp)
                        {
                            // One write per frame instead of one per packet (see
                            // SendInterleavedBatchAsync). UDP keeps per-packet sends:
                            // datagram boundaries ARE the packet boundaries.
                            await _conn.SendInterleavedBatchAsync(Video.RtpChannel, packets, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (var rtp in packets)
                                await SendAsync(Video, rtp, ct).ConfigureAwait(false);
                        }
                        break;
                    }
                    // The URL decided this session's audio: on an Opus session the
                    // transcoded packets ARE the track and the originals are
                    // skipped; on an original session it's the exact opposite. Both
                    // kinds flow through the hub side by side.
                    case HubAudioOpus o when Audio != null && _opus:
                        await SendAsync(Audio, Audio.Packetizer.PacketizeOpus(o.Packet, o.RtpTs), ct).ConfigureAwait(false);
                        break;
                    case HubAudioAac a when Audio != null && !_opus:
                        await SendAsync(Audio, Audio.Packetizer.PacketizeAac(a.Au, a.RtpTs), ct).ConfigureAwait(false);
                        break;
                    case HubAudioPcm p when Audio != null && !_opus:
                        foreach (var rtp in Audio.Packetizer.PacketizePcm(p.Pcm, p.RtpTs))
                            await SendAsync(Audio, rtp, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"{hub.Name}: session {Id} pump ended: {Log.Flatten(ex)}");
        }
        finally
        {
            if (_opus) hub.ReleaseOpus();
            hub.Unsubscribe(subId);
            Log.Info($"{hub.Name}: client stopped streaming (session {Id})");
        }
    }

    /// <summary>Prepends cached SPS/PPS(/VPS) to keyframes that lack them (players need them to decode).</summary>
    private static byte[] EnsureParameterSets(IStreamHub hub, VideoCodec codec, byte[] annexB)
    {
        bool hasSps = false;
        foreach (var nal in H26x.SplitNals(annexB))
        {
            int type = codec == VideoCodec.H264 ? H26x.H264NalType(nal.Span) : H26x.H265NalType(nal.Span);
            if ((codec == VideoCodec.H264 && type == H26x.H264Sps) ||
                (codec == VideoCodec.H265 && type == H26x.H265Sps))
            {
                hasSps = true;
                break;
            }
        }
        if (hasSps) return annexB;

        using var ms = new MemoryStream();
        static void Write(byte[]? nal, MemoryStream stream)
        {
            if (nal == null) return;
            stream.Write(new byte[] { 0, 0, 0, 1 });
            stream.Write(nal);
        }
        if (codec == VideoCodec.H265) Write(hub.Vps, ms);
        Write(hub.Sps, ms);
        Write(hub.Pps, ms);
        ms.Write(annexB);
        return ms.ToArray();
    }

    private async Task SendAsync(TrackTransport track, byte[] rtp, CancellationToken ct)
    {
        if (track.Tcp)
        {
            await _conn.SendInterleavedAsync(track.RtpChannel, rtp, ct).ConfigureAwait(false);
        }
        else if (track.UdpSocket != null && track.ClientEndpoint != null)
        {
            await track.UdpSocket.SendToAsync(rtp, SocketFlags.None, track.ClientEndpoint, ct).ConfigureAwait(false);
        }
    }
}
