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
                    // Interleaved client data (usually RTCP receiver reports) — consume and ignore.
                    var head = await ReadExactAsync(4, ct).ConfigureAwait(false);
                    int len = (head[2] << 8) | head[3];
                    await ReadExactAsync(len, ct).ConfigureAwait(false);
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

    private (RtspMount? mount, string path, int trackId) ResolveUri(string uri)
    {
        string path = uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            path = parsed.AbsolutePath;
        path = Uri.UnescapeDataString(path);

        int trackId = -1;
        const string trackMarker = "/trackID=";
        int idx = path.LastIndexOf(trackMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            _ = int.TryParse(path[(idx + trackMarker.Length)..], out trackId);
            path = path[..idx];
        }
        return (_server.FindMount(path), path, trackId);
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
        var (mount, path, _) = ResolveUri(req.Uri);
        if (mount == null)
        {
            await RespondAsync(req, 404, "Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        if (!await CheckAuthAsync(req, mount, ct).ConfigureAwait(false)) return;

        if (!await mount.Hub.WaitForDescribeInfoAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
        {
            Log.Warn($"DESCRIBE {path}: stream not ready (camera offline or still connecting)");
            await RespondAsync(req, 503, "Service Unavailable", "Retry-After: 5", ct: ct).ConfigureAwait(false);
            return;
        }

        string sdp = Sdp.Build(mount.Hub, mount.Hub.Name);
        string contentBase = req.Uri.TrimEnd('/') + "/";
        await RespondAsync(req, 200, "OK",
            $"Content-Base: {contentBase}\r\nContent-Type: application/sdp",
            body: Encoding.ASCII.GetBytes(sdp), ct: ct).ConfigureAwait(false);
    }

    private async Task HandleSetupAsync(RtspRequest req, CancellationToken ct)
    {
        var (mount, _, trackId) = ResolveUri(req.Uri);
        if (mount == null)
        {
            await RespondAsync(req, 404, "Not Found", ct: ct).ConfigureAwait(false);
            return;
        }
        if (!await CheckAuthAsync(req, mount, ct).ConfigureAwait(false)) return;
        if (trackId is not (0 or 1)) trackId = 0;

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
            session = new RtspSession(this, mount);
            _sessions[session.Id] = session;
        }
        else if (session.Playing)
        {
            await RespondAsync(req, 455, "Method Not Valid in This State", sessionId: session.Id, ct: ct).ConfigureAwait(false);
            return;
        }

        var spec = ParseTransport(transportHeader);
        string responseTransport;
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // slow-client guard
        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
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

    private readonly RtspConnection _conn;
    private readonly RtspMount _mount;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public RtspSession(RtspConnection conn, RtspMount mount)
    {
        _conn = conn;
        _mount = mount;
    }

    public void SetTrack(int trackId, TrackTransport transport)
    {
        if (trackId == 0) Video = transport;
        else Audio = transport;
    }

    public void Play(CancellationToken ct)
    {
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
        Video?.Close();
        Audio?.Close();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var hub = _mount.Hub;
        var (subId, reader) = hub.Subscribe(viewer: true);
        long lastIndex = -1;
        bool waitKeyframe = true; // always start on a keyframe

        Log.Info($"{hub.Name}: client started streaming (session {Id})");
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
                        foreach (var rtp in Video.Packetizer.PacketizeVideo(codec, au, v.RtpTs))
                            await SendAsync(Video, rtp, ct).ConfigureAwait(false);
                        break;
                    }
                    case HubAudioAac a when Audio != null:
                        await SendAsync(Audio, Audio.Packetizer.PacketizeAac(a.Au, a.RtpTs), ct).ConfigureAwait(false);
                        break;
                    case HubAudioPcm p when Audio != null:
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
