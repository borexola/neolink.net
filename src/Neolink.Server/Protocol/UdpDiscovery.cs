using System.Net;
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
    private static readonly int[] CameraPorts = { 2015, 2018 };

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

    private static string BuildC2dDisc(int cid, int did) =>
        $"<P2P><C2D_DISC><cid>{cid}</cid><did>{did}</did></C2D_DISC></P2P>";

    /// <summary>UID-masked text, safe to log/paste: first four and last two
    /// characters survive, the middle is starred out.</summary>
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
    public static async Task<UdpOutcome> ProbeAsync(string tag, IPAddress ip, string uid, CancellationToken ct)
    {
        string P(string msg) => $"{tag}: [discover/udp] {MaskUid(msg, uid)}";
        Log.Info(P($"Baichuan-over-UDP discovery (uid {uid}, target {ip}, ports {string.Join("/", CameraPorts)})"));

        // The discovery socket is IPv4 (ports 2015/2018, limited broadcast).
        if (ip.AddressFamily != AddressFamily.InterNetwork)
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

            // Direct to the camera, limited broadcast, and (heuristic /24)
            // subnet broadcast — docker bridge networks drop the broadcasts,
            // the direct sends still get through.
            var targets = new List<IPEndPoint>();
            foreach (var port in CameraPorts)
            {
                targets.Add(new IPEndPoint(ip, port));
                targets.Add(new IPEndPoint(IPAddress.Broadcast, port));
                var octets = ip.GetAddressBytes();
                if (octets.Length == 4 && octets[3] != 255)
                {
                    octets[3] = 255;
                    targets.Add(new IPEndPoint(new IPAddress(octets), port));
                }
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
                                    // A diagnostic leaves no half-open session behind.
                                    var disc = BuildDiscovery((uint)Random.Shared.Next(1, int.MaxValue),
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

            // Five send rounds, 3 s apart, both XML dialects to every target.
            for (int round = 1; round <= 5 && !probeCts.IsCancellationRequested; round++)
            {
                foreach (var target in targets)
                {
                    foreach (var pkt in new[] { pktPlain, pktDecl })
                    {
                        try
                        {
                            await udp.SendAsync(pkt, target, probeCts.Token).ConfigureAwait(false);
                            sent++;
                        }
                        catch (OperationCanceledException) { break; }
                        catch (SocketException ex)
                        {
                            Log.Debug(P($"send to {target} failed: {ex.SocketErrorCode}"));
                        }
                    }
                }
                Log.Info(P($"round {round}/5 sent ({sent} datagrams so far)"));
                try { await Task.Delay(TimeSpan.FromSeconds(3), probeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

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
