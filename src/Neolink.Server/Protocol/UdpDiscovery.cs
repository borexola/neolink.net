using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Neolink;

/// <summary>
/// Baichuan-over-UDP discovery — the transport battery-only cameras (Argus
/// family and friends) speak INSTEAD of TCP. Those models never listen on
/// TCP 9000; a client finds and wakes them with a UDP handshake:
///
///   client  →  C2D_C  ("connect to UID …")   to camera ports 2015/2018
///   camera  →  D2C_C_R (rsp 0 = accepted, negotiated cid/did + timers)
///
/// after which ordinary BC messages ride inside UDP data packets with an
/// ack/retransmit layer. Wire format (validated against the reference Rust
/// implementation, crates/core/src/bcudp):
///
///   discovery packet: magic 0x2A87CF3A · payload len · 1 · tid · CRC · payload
///   (all u32 little-endian; CRC over the ENCRYPTED payload; the XML payload
///   is XOR-encrypted with a keystream derived from a fixed key and the tid)
///
/// What exists here today is the opt-in DIAGNOSTIC PROBE ("udp_probe" +
/// "uid" in the camera config): it mimics the official client's first
/// packets and logs every step — what was sent where, every datagram that
/// comes back, decoded when possible — so a user with such a camera can
/// paste the log into an issue and tell us exactly how far the handshake
/// gets. The full UDP transport will build on the same primitives.
/// Logs never contain credentials, and the camera UID is masked.
/// </summary>
public static class UdpDiscovery
{
    // Wire magics (little-endian u32 at offset 0).
    internal const uint MagicDiscovery = 0x2A87CF3A; // handshake / negotiation
    internal const uint MagicAck = 0x2A87CF20;       // transport acknowledgment
    internal const uint MagicData = 0x2A87CF10;      // transport data (BC inside)

    /// <summary>Camera-side discovery ports (the official client targets both).</summary>
    // NEOLINK_UDP_PORTS overrides the discovery target ports (e.g. "2015") — a
    // field-diagnosis knob: probing the ports separately is how to tell whether a
    // battery model's low-power wake chip listens on both or just one of them.
    private static readonly int[] CameraPorts = ParsePortsEnv() ?? new[] { 2015, 2018 };

    private static int[]? ParsePortsEnv()
    {
        var raw = Environment.GetEnvironmentVariable("NEOLINK_UDP_PORTS");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var ports = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var v) && v is > 0 and < 65536 ? v : 0)
            .Where(v => v != 0).ToArray();
        if (ports.Length == 0) return null;
        Log.Warn($"NEOLINK_UDP_PORTS override active — UDP discovery targets port(s) {string.Join("/", ports)} only");
        return ports;
    }

    // The fixed XOR key; each word is offset by the packet's tid, so the tid
    // in the clear-text header is all a receiver needs to decrypt.
    private static readonly uint[] XmlKey =
    {
        0x1f2d3c4b, 0x5a6c7f8d, 0x38172e4b, 0x8271635a,
        0x863f1a2b, 0xa5c6f7d8, 0x8371e1b4, 0x17f2d3a5,
    };

    // ------------------------------------------------------------------ primitives

    /// <summary>Symmetric XOR "encryption" of a discovery payload, in place.
    /// Keystream: each key word + tid (wrapping), little-endian bytes, cycled.</summary>
    internal static void Crypt(uint tid, Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            uint word = unchecked(XmlKey[(i >> 2) & 7] + tid);
            data[i] ^= (byte)(word >> ((i & 3) * 8));
        }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Discovery checksum: reflected CRC-32 with a RAW register —
    /// initial value 0 and no final complement (not the zlib variant).</summary>
    internal static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }

    /// <summary>One complete discovery datagram around an XML payload.</summary>
    internal static byte[] BuildDiscovery(uint tid, string xml)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        Crypt(tid, payload);
        var pkt = new byte[20 + payload.Length];
        BitConverter.TryWriteBytes(pkt.AsSpan(0), MagicDiscovery);
        BitConverter.TryWriteBytes(pkt.AsSpan(4), (uint)payload.Length);
        BitConverter.TryWriteBytes(pkt.AsSpan(8), 1u);
        BitConverter.TryWriteBytes(pkt.AsSpan(12), tid);
        BitConverter.TryWriteBytes(pkt.AsSpan(16), Crc(payload));
        payload.CopyTo(pkt, 20);
        return pkt;
    }

    /// <summary>Parses a discovery datagram back into its XML (CRC-checked).
    /// A false return leaves a diagnosis in <paramref name="problem"/>.</summary>
    internal static bool TryParseDiscovery(ReadOnlySpan<byte> dgram,
        out uint tid, out string xml, out string? problem)
    {
        tid = 0;
        xml = "";
        problem = null;
        if (dgram.Length < 20) { problem = $"too short for a discovery header ({dgram.Length} bytes)"; return false; }
        if (BitConverter.ToUInt32(dgram[..4]) != MagicDiscovery) { problem = "not a discovery packet"; return false; }
        uint size = BitConverter.ToUInt32(dgram.Slice(4, 4));
        uint unknown = BitConverter.ToUInt32(dgram.Slice(8, 4));
        tid = BitConverter.ToUInt32(dgram.Slice(12, 4));
        uint crc = BitConverter.ToUInt32(dgram.Slice(16, 4));
        if (dgram.Length - 20 < size) { problem = $"header claims {size} payload bytes, only {dgram.Length - 20} present"; return false; }
        var payload = dgram.Slice(20, (int)size).ToArray();
        var actual = Crc(payload);
        if (actual != crc) { problem = $"CRC mismatch (header {crc:x8}, computed {actual:x8})"; return false; }
        if (unknown != 1) problem = $"unusual header field ({unknown} where 1 is expected) — continuing";
        Crypt(tid, payload); // symmetric
        xml = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        return true;
    }

    /// <summary>The client's "connect to this UID" hello, exactly as the
    /// official client shapes it.</summary>
    internal static string BuildC2dC(string uid, int clientPort, int cid, bool xmlDeclaration)
    {
        var body = $"<P2P><C2D_C><uid>{uid}</uid><cli><port>{clientPort}</port></cli>" +
                   $"<cid>{cid}</cid><mtu>1350</mtu><debug>false</debug><p>WIN</p></C2D_C></P2P>";
        return xmlDeclaration ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + body : body;
    }

    internal static string BuildC2dDisc(int cid, int did) =>
        $"<P2P><C2D_DISC><cid>{cid}</cid><did>{did}</did></C2D_DISC></P2P>";

    /// <summary>Client→device heartbeat XML (keeps the UDP session alive).</summary>
    internal static string BuildC2dHb(int cid, int did) =>
        $"<P2P><C2D_HB><cid>{cid}</cid><did>{did}</did></C2D_HB></P2P>";

    /// <summary>Client→device ACCEPT — the reply the camera requires to a D2C_T, or
    /// it recycles the (unconfirmed) session after a few seconds. Echoes the sid and
    /// conn the camera sent, plus our mtu.</summary>
    internal static string BuildC2dA(uint sid, string conn, int cid, int did, uint mtu) =>
        $"<P2P><C2D_A><sid>{sid}</sid><conn>{conn}</conn><cid>{cid}</cid><did>{did}</did><mtu>{mtu}</mtu></C2D_A></P2P>";

    /// <summary>
    /// Cheap liveness check: is a UDP camera answering discovery right now? Sends a
    /// few C2D_C hellos to the known IP and returns true on the first D2C_C_R,
    /// releasing the just-created session with a polite C2D_DISC (otherwise the
    /// camera would keep retrying D2C_C_R for ~9 s after every poll — battery cost,
    /// and a stale session sitting there right when the real connect follows).
    /// Silent and short — it is the wake-capture poll, run every few seconds while
    /// the camera sleeps. With <paramref name="logTag"/> set, every discovery reply
    /// is logged raw at Debug: some battery models (issue #44, the battery Video
    /// Doorbell) answer C2D_C from their low-power wake chip even while asleep,
    /// and the reply contents are the evidence needed to distinguish that from a
    /// genuinely awake camera.
    /// </summary>
    internal static async Task<bool> IsReachableAsync(string host, string uid, TimeSpan timeout, CancellationToken ct,
        string? logTag = null)
    {
        // UID-only camera (no address): the wake probe broadcasts like the connect
        // path does — the UID keys the reply, so a hit still means OUR camera is up.
        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(host))
        {
            ip = IPAddress.TryParse(host, out var lit) ? lit : null;
            if (ip == null)
            {
                try { ip = (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
                catch { return false; }
            }
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        }
        var targets = ip != null ? new[] { ip } : BroadcastTargets();
        if (targets.Length == 0) return false;

        UdpClient? udp = null;
        for (int i = 0; i < 4 && udp == null; i++)
        {
            try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, Random.Shared.Next(53500, 54000))); }
            catch (SocketException) { }
        }
        if (udp == null) return false;
        if (ip == null) udp.EnableBroadcast = true;

        using (udp)
        {
            int localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            uint tid = (uint)Random.Shared.Next(1, int.MaxValue);
            var xml = BuildC2dC(uid, localPort, Random.Shared.Next(1, int.MaxValue), xmlDeclaration: false);
            var hello = BuildDiscovery(tid, xml);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var sender = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var target in targets)
                    foreach (var port in CameraPorts)
                    {
                        try { await udp.SendAsync(hello,
                            new IPEndPoint(target, port), cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        catch (SocketException) { }
                    }
                    // 1 s between hello bursts, NOT the connect path's 300 ms: this
                    // probe runs against cameras we hope are ASLEEP, and a storm of
                    // hellos is itself enough to pull a wake chip out of deep sleep
                    // back into its answering light-sleep stage (measured on an
                    // Argus Solar: three dense probe windows re-lit its chip for
                    // minutes) — sabotaging the very silence the probe looks for.
                    try { await Task.Delay(1000, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }, cts.Token);

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { continue; }
                    if (!TryParseDiscovery(r.Buffer, out _, out var reply, out _))
                    {
                        if (logTag != null)
                            Log.Debug($"{logTag}: [wake-probe] unparseable {r.Buffer.Length}-byte " +
                                      $"datagram from {r.RemoteEndPoint}");
                        continue;
                    }
                    if (logTag != null)
                        Log.Debug($"{logTag}: [wake-probe] reply from {r.RemoteEndPoint}: " +
                                  MaskUid(Snippet(reply, 400), uid));
                    if (reply.Contains("D2C_C_R", StringComparison.Ordinal))
                    {
                        cts.Cancel();
                        try { await sender.ConfigureAwait(false); } catch { }
                        // An accepted reply means the camera opened a session for us;
                        // release it (same tid) — the probe only wanted the answer.
                        if (ParseReply(reply) is { Root: "D2C_C_R", Rsp: 0 } ok)
                        {
                            var disc = BuildDiscovery(tid, BuildC2dDisc(ok.Cid, ok.Did));
                            try { await udp.SendAsync(disc, r.RemoteEndPoint, CancellationToken.None).ConfigureAwait(false); }
                            catch { /* best effort */ }
                        }
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) { }
            return false;
        }
    }

    /// <summary>An established UDP session: the open socket, the endpoint the camera
    /// actually answered from, the two negotiated connection ids (cid = ours,
    /// did = the camera's), and the discovery tid the handshake ran under — the
    /// camera keys its session state to that tid, so every discovery-layer packet
    /// sent for the rest of the session (heartbeats, C2D_A, C2D_DISC) must carry
    /// it. The caller owns the socket from here on.</summary>
    internal sealed record Session(UdpClient Socket, IPEndPoint Camera, int ClientId, int DeviceId, uint Tid);

    /// <summary>
    /// Performs the discovery handshake and hands back a live session for the UDP
    /// transport to run on: send C2D_C, wait for an accepted D2C_C_R, keep the
    /// socket. Null on failure. With a known camera IP the hello goes straight to
    /// it; with <paramref name="ip"/> null (UID-only config, no address) it goes to
    /// every local subnet's directed broadcast instead — the UID in the hello keys
    /// the reply, so only the right camera answers, and the session binds to the
    /// endpoint the reply CAME from either way. This is the connect-path twin of
    /// <see cref="ProbeAsync"/> (which is the throwaway diagnostic).
    /// </summary>
    internal static async Task<Session?> EstablishAsync(IPAddress? ip, string uid, TimeSpan timeout,
        CancellationToken ct, string tag)
    {
        string P(string m) => $"{tag}: [udp] {MaskUid(m, uid)}";
        if (ip != null && ip.AddressFamily != AddressFamily.InterNetwork)
        {
            Log.Warn(P($"{ip} is not IPv4 — UDP connect needs an IPv4 address"));
            return null;
        }
        var targets = ip != null ? new[] { ip } : BroadcastTargets();
        if (targets.Length == 0)
        {
            Log.Warn(P("no address configured and no usable interface for broadcast discovery"));
            return null;
        }
        if (ip == null)
            Log.Info(P($"no address configured — broadcasting discovery for the UID on " +
                       $"{string.Join(", ", targets.AsEnumerable())}"));

        UdpClient? udp = null;
        for (int attempt = 0; attempt < 8 && udp == null; attempt++)
        {
            int lp = Random.Shared.Next(53500, 54000);
            try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, lp)); }
            catch (SocketException) { }
        }
        if (udp == null) { Log.Warn(P("could not bind a local UDP port")); return null; }
        if (ip == null) udp.EnableBroadcast = true;

        int localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        int cid = Random.Shared.Next(1, int.MaxValue);
        // ONE tid for the whole session. The camera keys its discovery state to the
        // tid of the C2D_C it accepted and ignores discovery packets carrying any
        // other tid — heartbeats sent under fresh random tids look like silence, so
        // the camera re-sends D2C_C_R every `def` ms and recycles the session with
        // a clean D2C_DISC after ~3 tries (the observed ~8 s disconnect). The
        // reference client uses its discovery tid for every packet that follows.
        uint tid = (uint)Random.Shared.Next(1, int.MaxValue);
        var xml = BuildC2dC(uid, localPort, cid, xmlDeclaration: false);
        var hello = BuildDiscovery(tid, xml);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(timeout);
        try
        {
            // Retransmit the hello until an accepted reply arrives or time runs out.
            var sender = Task.Run(async () =>
            {
                while (!handshakeCts.IsCancellationRequested)
                {
                    foreach (var target in targets)
                    foreach (var port in CameraPorts)
                    {
                        try { await udp.SendAsync(hello, new IPEndPoint(target, port), handshakeCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        catch (SocketException) { }
                    }
                    try { await Task.Delay(500, handshakeCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }, handshakeCts.Token);

            while (!handshakeCts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(handshakeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                if (!TryParseDiscovery(r.Buffer, out _, out var replyXml, out _)) continue;
                if (replyXml.Contains("<C2D_C>", StringComparison.Ordinal)) continue; // our own loopback
                var reply = ParseReply(replyXml);
                if (reply is { Root: "D2C_C_R", Rsp: 0 })
                {
                    handshakeCts.Cancel();
                    try { await sender.ConfigureAwait(false); } catch { }
                    Log.Info(P($"UDP session established — cid {reply.Cid}, did {reply.Did} " +
                               $"(camera at {r.RemoteEndPoint}" +
                               (reply.Timers != null ? $", timers {reply.Timers}" : "") + ")"));
                    Log.Debug(P($"D2C_C_R payload (tid {tid:x8}): {Condense(replyXml)}"));
                    return new Session(udp, (IPEndPoint)r.RemoteEndPoint, reply.Cid, reply.Did, tid);
                }
                if (reply is { Root: "D2C_C_R" })
                    Log.Warn(P($"camera refused the UDP handshake (rsp {reply.Rsp})"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(P($"UDP handshake error: {Log.Flatten(ex)}"));
        }

        udp.Dispose();
        if (!ct.IsCancellationRequested)
            Log.Warn(P($"UDP handshake timed out after {timeout.TotalSeconds:0}s (camera asleep or not reachable)"));
        return null;
    }

    /// <summary>
    /// The directed broadcast address of every up, non-loopback IPv4 interface
    /// (host bits all set: ip | ~mask). Best practice for LAN discovery: the
    /// limited broadcast 255.255.255.255 only egresses one interface on most
    /// stacks, and a guessed /24 misses any subnet that isn't a /24 — a directed
    /// broadcast per interface reaches a camera on any local subnet, out the
    /// right NIC (multi-homed hosts, VLANs, /23s and the like).
    /// </summary>
    /// <summary>Where a UID-only handshake sends its hellos: every local subnet's
    /// directed broadcast, with the limited broadcast as a last resort when the
    /// interface sweep found nothing (containers with odd network stacks).</summary>
    internal static IPAddress[] BroadcastTargets()
    {
        var targets = LocalDirectedBroadcasts().Distinct().ToArray();
        return targets.Length > 0 ? targets : new[] { IPAddress.Broadcast };
    }

    internal static IEnumerable<IPAddress> LocalDirectedBroadcasts()
    {
        foreach (var ni in SafeInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IEnumerable<UnicastIPAddressInformation> unicasts;
            try { unicasts = ni.GetIPProperties().UnicastAddresses; }
            catch { continue; }
            foreach (var ua in unicasts)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ab = ua.Address.GetAddressBytes();
                // Skip APIPA link-local (169.254/16): a camera never lives there.
                if (ab is [169, 254, ..]) continue;
                var mask = ua.IPv4Mask;
                if (mask == null || mask.GetAddressBytes() is not { Length: 4 } mb) continue;
                // A /32 (mask 255.255.255.255) has no broadcast; skip it.
                if (mb is [255, 255, 255, 255]) continue;
                yield return DirectedBroadcast(ua.Address, mask);
            }
        }
    }

    /// <summary>Directed broadcast of a subnet: host bits all set (ip | ~mask).
    /// Pure — a /23 like 10.0.0.5 / 255.255.254.0 yields 10.0.1.255, which a
    /// naive /24 guess (10.0.0.255) would miss.</summary>
    internal static IPAddress DirectedBroadcast(IPAddress ip, IPAddress mask)
    {
        var ipb = ip.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        var bc = new byte[4];
        for (int i = 0; i < 4; i++) bc[i] = (byte)(ipb[i] | (byte)~mb[i]);
        return new IPAddress(bc);
    }

    private static NetworkInterface[] SafeInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return Array.Empty<NetworkInterface>(); }
    }

    /// <summary>Delay that returns true if it completed, false if the token fired
    /// (the window closed) — lets the send schedule read as a plain while-loop.</summary>
    private static async Task<bool> QuietDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>The /24 directed broadcast derived from an address (fallback for
    /// when the camera sits on a subnet the host has no interface on).</summary>
    private static IPAddress? Slash24Broadcast(IPAddress ip)
    {
        var o = ip.GetAddressBytes();
        if (o.Length != 4 || o[3] == 255) return null;
        o[3] = 255;
        return new IPAddress(o);
    }

    /// <summary>UID-masked text, safe to log/paste: first four and last two
    /// characters survive, the middle is starred out.</summary>
    /// <summary>First <paramref name="max"/> chars, newlines flattened — keeps the
    /// wake-probe Debug lines single-line and bounded.</summary>
    private static string Snippet(string text, int max)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    internal static string MaskUid(string text, string uid)
    {
        if (uid.Length < 7) return text.Replace(uid, "«uid»", StringComparison.OrdinalIgnoreCase);
        var masked = uid[..4] + new string('*', uid.Length - 6) + uid[^2..];
        return text.Replace(uid, masked, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the probe

    /// <summary>How a UDP discovery attempt ended (the orchestrator folds this into
    /// the sweep's overall verdict).</summary>
    public enum UdpOutcome { Aborted, Accepted, Partial, Refused, Silent }

    /// <summary>
    /// Runs one full discovery attempt against a camera and logs everything:
    /// several rounds of C2D_C (two XML dialects, direct + broadcast, both
    /// camera ports) while listening ~20 s for any reply. On an accepted
    /// handshake it records the negotiated parameters and immediately sends a
    /// polite disconnect — this is a diagnostic, not a session.
    /// </summary>
    public static async Task<UdpOutcome> ProbeAsync(string tag, IPAddress? ip, string uid, CancellationToken ct)
    {
        string P(string msg) => $"{tag}: [discover/udp] {MaskUid(msg, uid)}";
        Log.Info(P($"Baichuan-over-UDP discovery (uid {uid}, target {ip?.ToString() ?? "(broadcast only)"}, " +
                   $"ports {string.Join("/", CameraPorts)})"));

        // The discovery socket is IPv4 (ports 2015/2018, limited broadcast).
        if (ip != null && ip.AddressFamily != AddressFamily.InterNetwork)
        {
            Log.Warn(P($"{ip} is not IPv4; UDP discovery is IPv4-only — skipped"));
            return UdpOutcome.Aborted;
        }

        // The official client binds an ephemeral port in this range.
        UdpClient? udp = null;
        int localPort = 0;
        for (int attempt = 0; attempt < 8 && udp == null; attempt++)
        {
            localPort = Random.Shared.Next(53500, 54000);
            try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort)); }
            catch (SocketException) { /* port taken — roll again */ }
        }
        if (udp == null)
        {
            Log.Warn(P("could not bind a local UDP port in 53500-53999 — skipped"));
            return UdpOutcome.Aborted;
        }

        using (udp)
        {
            udp.EnableBroadcast = true;

            int cid = Random.Shared.Next(1, int.MaxValue);
            uint tidPlain = (uint)Random.Shared.Next(1, int.MaxValue);
            uint tidDecl = (uint)Random.Shared.Next(1, int.MaxValue);
            var xmlPlain = BuildC2dC(uid, localPort, cid, xmlDeclaration: false);
            var xmlDecl = BuildC2dC(uid, localPort, cid, xmlDeclaration: true);
            var pktPlain = BuildDiscovery(tidPlain, xmlPlain);
            var pktDecl = BuildDiscovery(tidDecl, xmlDecl);

            // Best-practice target set, per camera port:
            //  · the camera directly (works even when broadcasts are dropped,
            //    e.g. a docker bridge network),
            //  · every local interface's directed broadcast (reaches a camera on
            //    any local subnet, out the correct NIC),
            //  · the limited broadcast 255.255.255.255 (belt-and-braces; egresses
            //    the primary interface on most stacks), and
            //  · the /24 derived from the camera's own address, in case it sits on
            //    a subnet this host has no interface on.
            // Broadcast destinations are de-duplicated.
            var bcasts = new HashSet<IPAddress>(LocalDirectedBroadcasts()) { IPAddress.Broadcast };
            if (ip != null && Slash24Broadcast(ip) is { } guess) bcasts.Add(guess);

            var targets = new List<IPEndPoint>();
            foreach (var port in CameraPorts)
            {
                if (ip != null) targets.Add(new IPEndPoint(ip, port));
                foreach (var b in bcasts) targets.Add(new IPEndPoint(b, port));
            }

            Log.Info(P($"local port {localPort}, cid {cid}, hello {pktPlain.Length}/{pktDecl.Length} bytes " +
                       $"(tid {tidPlain:x8}/{tidDecl:x8}), targets: {string.Join(", ", targets)}"));
            Log.Debug(P($"C2D_C payload: {xmlPlain}"));

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(20));

            int sent = 0, received = 0, refused = 0;

            bool answered = false, accepted = false;

            // Listen for the whole probe window; every datagram is evidence.
            var receiver = Task.Run(async () =>
            {
                while (!probeCts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(probeCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (SocketException ex)
                    {
                        // Windows surfaces ICMP port-unreachable as ConnectionReset:
                        // an endpoint actively refused a datagram. One such line is a
                        // clue; a flood is just a closed port answering every send, so
                        // log the first and tally the rest for the verdict.
                        if (refused++ == 0)
                            Log.Info(P($"socket signal: {ex.SocketErrorCode} — an endpoint sent ICMP unreachable (refusing UDP)"));
                        continue;
                    }
                    var d = r.Buffer;
                    uint magic = d.Length >= 4 ? BitConverter.ToUInt32(d, 0) : 0;

                    // Some stacks loop a limited broadcast back to the sender — our
                    // own C2D_C hello. Drop it before counting: treating it as a
                    // reply would falsely read as "something answered".
                    if (magic == MagicDiscovery
                        && TryParseDiscovery(d, out _, out var selfXml, out _)
                        && selfXml.Contains("<C2D_C>", StringComparison.Ordinal))
                        continue;

                    received++;
                    answered = true;

                    if (magic == MagicDiscovery)
                    {
                        if (TryParseDiscovery(d, out var rtid, out var xml, out var note))
                        {
                            if (note != null) Log.Info(P($"note: {note}"));
                            Log.Info(P($"discovery reply from {r.RemoteEndPoint} ({d.Length} bytes, tid {rtid:x8}): {Condense(xml)}"));
                            var reply = ParseReply(xml);
                            if (reply is { Root: "D2C_C_R" })
                            {
                                if (reply.Rsp == 0)
                                {
                                    accepted = true;
                                    Log.Info(P($"HANDSHAKE ACCEPTED — camera negotiated cid {reply.Cid}, did {reply.Did}" +
                                               (reply.Timers != null ? $", timers {reply.Timers}" : "")));
                                    // A diagnostic leaves no half-open session behind. The
                                    // disconnect must run under the session's tid (the camera
                                    // echoes the tid of the hello it accepted, and ignores
                                    // discovery packets under any other tid).
                                    var disc = BuildDiscovery(rtid,
                                        BuildC2dDisc(reply.Cid, reply.Did));
                                    try
                                    {
                                        await udp.SendAsync(disc, r.RemoteEndPoint, probeCts.Token).ConfigureAwait(false);
                                        Log.Info(P("sent C2D_DISC (polite disconnect)"));
                                    }
                                    catch { /* best effort */ }
                                    probeCts.Cancel();
                                    return; // ends the receiver task; accepted flag drives the verdict
                                }
                                Log.Info(P($"camera REJECTED the handshake: rsp {reply.Rsp}" +
                                           (reply.Rsp == -3 ? " (busy or refused — camera may allow a limited number of clients)" : "")));
                            }
                        }
                        else
                        {
                            TryParseDiscovery(d, out _, out _, out var why);
                            Log.Info(P($"discovery-magic packet from {r.RemoteEndPoint} ({d.Length} bytes) but {why}"));
                        }
                    }
                    else if (magic == MagicAck)
                    {
                        Log.Info(P($"unexpected transport ACK from {r.RemoteEndPoint} ({d.Length} bytes) during discovery"));
                    }
                    else if (magic == MagicData)
                    {
                        Log.Info(P($"unexpected transport DATA from {r.RemoteEndPoint} ({d.Length} bytes) during discovery"));
                    }
                    else
                    {
                        Log.Info(P($"unrecognized datagram from {r.RemoteEndPoint}: {d.Length} bytes, " +
                                   $"starts {Convert.ToHexString(d.AsSpan(0, Math.Min(16, d.Length)))}"));
                    }
                }
            }, probeCts.Token);

            // One burst = both XML dialects to every target. Returns false if the
            // window closed mid-burst.
            async Task<bool> BurstAsync()
            {
                foreach (var target in targets)
                    foreach (var pkt in new[] { pktPlain, pktDecl })
                    {
                        try { await udp.SendAsync(pkt, target, probeCts.Token).ConfigureAwait(false); sent++; }
                        catch (OperationCanceledException) { return false; }
                        catch (SocketException ex) { Log.Debug(P($"send to {target} failed: {ex.SocketErrorCode}")); }
                    }
                return true;
            }

            // Schedule tuned for battery cameras: an opening double-tap (t≈0 and
            // t≈0.4 s) catches a camera already awake within a second; steady 2 s
            // retransmits across the window catch one that wakes partway through
            // (roused by motion or the Reolink app). The ~20 s window bounds it.
            int bursts = 0;
            if (await BurstAsync().ConfigureAwait(false)) bursts++;
            if (await QuietDelayAsync(TimeSpan.FromMilliseconds(400), probeCts.Token).ConfigureAwait(false)
                && await BurstAsync().ConfigureAwait(false)) bursts++;
            while (await QuietDelayAsync(TimeSpan.FromSeconds(2), probeCts.Token).ConfigureAwait(false))
            {
                if (!await BurstAsync().ConfigureAwait(false)) break;
                bursts++;
            }
            Log.Info(P($"sent {sent} datagrams across {bursts} bursts to {targets.Count} targets"));

            try { await receiver.ConfigureAwait(false); } catch (OperationCanceledException) { }

            // Server shutting down (the parent token, not our success-cancel):
            // skip the verdict so a stopping service doesn't print a misleading
            // "SILENCE" line for a probe it interrupted.
            if (ct.IsCancellationRequested)
            {
                Log.Debug(P("interrupted by shutdown"));
                return UdpOutcome.Aborted;
            }

            // Section verdict — the orchestrator folds it into the sweep summary.
            var refusedNote = refused > 0 ? $", {refused} ICMP-refused" : "";
            if (accepted)
            {
                Log.Info(P($"UDP: ACCEPTED — the camera answered discovery and accepted the handshake " +
                           $"({sent} sent, {received} received{refusedNote}). It speaks Baichuan-over-UDP."));
                return UdpOutcome.Accepted;
            }
            if (answered)
            {
                Log.Info(P($"UDP: PARTIAL — something answered ({received} datagram(s) for {sent} sent{refusedNote}) " +
                           "but no accepted handshake."));
                return UdpOutcome.Partial;
            }
            if (refused > 0)
            {
                Log.Info(P($"UDP: REFUSED — {sent} sent, no reply, target sent ICMP port-unreachable {refused} time(s): " +
                           "something is at the address but nothing listens on the discovery ports (not a UDP-discovery " +
                           "camera, or asleep)."));
                return UdpOutcome.Refused;
            }
            Log.Info(P($"UDP: SILENCE — {sent} sent, nothing came back (wrong uid, a VLAN between server and camera, " +
                       "Docker without --network host so broadcasts stay in the container, or the camera only answers " +
                       "its P2P/cloud rendezvous)."));
            return UdpOutcome.Silent;
        }
    }

    private sealed record Reply(string Root, int Rsp, int Cid, int Did, string? Timers);

    /// <summary>Pulls the interesting fields out of a discovery reply; null when
    /// the payload isn't parseable XML (already logged verbatim by the caller).</summary>
    private static Reply? ParseReply(string xml)
    {
        try
        {
            var root = XElement.Parse(xml);
            var inner = root.Elements().FirstOrDefault() ?? root;
            int Get(string name) => int.TryParse(inner.Element(name)?.Value, out var v) ? v : 0;
            var timer = inner.Element("timer");
            var timers = timer == null ? null
                : string.Join("/", timer.Elements().Select(e => $"{e.Name}={e.Value}"));
            return new Reply(inner.Name.LocalName, Get("rsp"), Get("cid"), Get("did"), timers);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Single-line XML for the log.</summary>
    private static string Condense(string xml) =>
        string.Join("", xml.Split('\n', '\r').Select(s => s.Trim()));
}
