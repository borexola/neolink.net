// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// Baichuan-over-UDP transport. The reliability layer (ordered delivery, acks and
// retransmission) derives from the QuantumEntangledAndy/neolink reverse
// engineering (crates/core/src/bcudp). The BC messages carried inside are byte-for
// -byte identical to the TCP path, so everything above this layer is shared.
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Neolink.Bc;

namespace Neolink.Protocol;

/// <summary>
/// A Baichuan connection carried over UDP — what battery-only cameras (Argus
/// family) speak instead of TCP. It performs the discovery handshake, then runs a
/// small reliable-ordered-datagram layer (sequence numbers, cumulative acks,
/// timed retransmission, heartbeats) beneath the <b>identical</b> BC framing: the
/// reassembled in-order byte stream is fed to <see cref="BcCodec.ReadMessageAsync"/>
/// through a pipe, and outbound BC messages are serialized exactly as for TCP and
/// chunked into data packets. Implements <see cref="IBcConnection"/>, so
/// <see cref="BcCamera"/> and every camera operation run over it unchanged.
/// </summary>
public sealed class BcUdpConnection : IBcConnection
{
    // Payload per data packet: the camera negotiated MTU 1350; stay well under it
    // (20-byte data header + IP/UDP overhead) so nothing fragments.
    private const int ChunkSize = 1024;
    private static readonly TimeSpan RetxAfter = TimeSpan.FromMilliseconds(500);
    // Match the official client EXACTLY: a 1 s heartbeat plus a continuous 10 ms ack.
    // The reference transport documents this precisely — the official app acks every
    // 10 ms, and a camera that stops seeing acks at that cadence decides the client
    // has a poor connection, re-offers the session (repeated D2C_C_R) and then closes
    // it cleanly (D2C_DISC) after ~3 tries (~8 s). We had been acking at 20 ms — half
    // the official rate — which the camera treated as a failing link. The ack timer
    // is decoupled from downstream delivery so the cadence never slips.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AckInterval = TimeSpan.FromMilliseconds(10);

    private readonly UdpClient _udp;
    private readonly IPEndPoint _camera;
    private readonly int _cid; // our id — inbound packets carry this
    private readonly int _did; // camera id — we stamp outbound with it
    // The discovery tid the handshake ran under. The camera keys its session to it
    // and ignores discovery-layer packets under any other tid — heartbeats sent
    // with fresh random tids read as silence, and the camera recycles the session
    // (D2C_C_R retries, then a clean D2C_DISC at ~8 s). Every discovery-layer
    // packet (C2D_HB, C2D_A, C2D_DISC) must carry this tid.
    private readonly uint _tid;
    private readonly string _tag;

    public EncryptionState Encryption { get; } = new();
    private readonly BcContext _context;

    private readonly Dictionary<uint, Channel<BcMessage>> _subscribers = new();
    private readonly HashSet<uint> _reportedUnhandled = new();
    private readonly object _subGate = new();

    private readonly CancellationTokenSource _cts;
    // A generous pause threshold so the receive loop's writes complete synchronously
    // under normal video rates — a blocked write would stall socket draining and,
    // with it, the ack timer's view of progress.
    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: 8 * 1024 * 1024, resumeWriterThreshold: 4 * 1024 * 1024,
        useSynchronizationContext: false));

    // Outbound reliability.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _rawGate = new(1, 1);
    private readonly object _txGate = new();
    private uint _txNext;
    private readonly SortedDictionary<uint, (byte[] Pkt, DateTime Sent)> _unacked = new();

    // Inbound reordering.
    private readonly object _rxGate = new();
    private uint _rxNext;
    private readonly SortedDictionary<uint, byte[]> _rxBuffer = new();
    // ---- Diagnostics (all Debug-level; silent unless NEOLINK_LOG=debug). The UDP
    // battery transport is experimental and hard to reproduce here, so the session
    // is heavily instrumented to pin down camera-initiated closes from one capture.
    private bool _confirmLogged, _connIdWarned;
    private readonly DateTime _t0 = DateTime.UtcNow;
    private long _rxData, _rxCtrl, _txAck, _txHb, _txC2dA, _txData, _rxKa, _txKa;
    private uint _rxLastPid, _rxMaxPid;
    // Duplicate-data forensics: when each pid FIRST arrived and how many copies came,
    // so the log can tell burst redundancy (copies µs..ms apart, deliberate) from
    // retransmit-because-unacked (copies a timer interval apart, our acks ignored).
    private long _rxDup;
    private readonly Dictionary<uint, (DateTime First, int Count)> _pidSeen = new();
    private int _dupLogged;
    private const int DupLogMax = 60;
    private bool _saturationLogged;
    private readonly HashSet<string> _rxEndpoints = new();
    private DateTime _lastRxUtc, _lastDataUtc, _lastCtrlUtc;
    // Per-session tally of the camera's control messages (D2C_T, D2C_HB, ...), so the
    // one-line D2C_DISC summary shows what the camera sent — no debug level needed.
    private readonly Dictionary<string, int> _ctrlTally = new();
    // First control messages in detail (root, did, arrival offset) — the tally says
    // WHAT arrived, this says WHEN and FOR WHICH SESSION, which is what finally
    // distinguishes "camera gave up on us" from "another session's paperwork".
    private readonly List<string> _ctrlDetail = new();
    private const int CtrlDetailMax = 12;
    // Duplicate camera-side sessions we have already dealt with (see the recv loop):
    // ones we released with a C2D_DISC, and foreign disconnects we logged.
    private readonly HashSet<int> _orphansReleased = new();
    private readonly HashSet<int> _foreignDiscLogged = new();
    private readonly Task _recvLoop, _readLoop, _hbLoop, _retxLoop, _ackLoop, _diagLoop;

    private string El => $"+{(DateTime.UtcNow - _t0).TotalMilliseconds:0}ms";
    private static bool Dbg => Log.Level <= LogLevel.Debug;

    private BcUdpConnection(UdpDiscovery.Session session, string tag, CancellationToken appCt)
    {
        _udp = session.Socket;
        _camera = session.Camera;
        _cid = session.ClientId;
        _did = session.DeviceId;
        _tid = session.Tid;
        _tag = tag;
        _context = new BcContext(Encryption);
        try { _udp.Client.ReceiveBufferSize = 1 << 20; } catch { } // 1 MB: absorb video bursts
        _cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _diagLoop = Task.Run(() => DiagLoopAsync(_cts.Token));
        _recvLoop = Task.Run(() => RecvLoopAsync(_cts.Token));
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _hbLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _retxLoop = Task.Run(() => RetxLoopAsync(_cts.Token));
        _ackLoop = Task.Run(() => AckLoopAsync(_cts.Token));
    }

    public static async Task<BcUdpConnection> ConnectAsync(string host, string uid, TimeSpan timeout,
        CancellationToken ct, string tag)
    {
        // No host = UID-only config: discovery broadcasts on the local subnets and
        // the camera whose UID matches answers with its own address.
        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(host))
            ip = await ResolveV4Async(host, ct).ConfigureAwait(false)
                ?? throw new IOException($"UDP: '{host}' has no IPv4 address");
        var session = await UdpDiscovery.EstablishAsync(ip, uid, timeout, ct, tag).ConfigureAwait(false)
            ?? throw new IOException(ip != null
                ? $"UDP handshake to {host} failed (camera asleep or unreachable)"
                : "UDP broadcast discovery found no camera with this UID (asleep, off-subnet, or broadcast blocked)");
        return new BcUdpConnection(session, tag, ct);
    }

    private static async Task<IPAddress?> ResolveV4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == AddressFamily.InterNetwork ? literal : null;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ IBcConnection

    public BcSubscription Subscribe(uint msgId)
    {
        var channel = Channel.CreateUnbounded<BcMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_subGate)
        {
            if (!_subscribers.TryAdd(msgId, channel))
                throw new InvalidOperationException($"Simultaneous subscription to message ID {msgId}");
        }
        return new BcSubscription(msgId, channel.Reader, () => Unsubscribe(msgId));
    }

    private void Unsubscribe(uint msgId)
    {
        lock (_subGate) { _subscribers.Remove(msgId); }
    }

    /// <summary>Serialize the BC message exactly as for TCP, then chunk the bytes
    /// into sequenced data packets tracked for retransmission until acked.</summary>
    public async Task SendAsync(BcMessage msg, CancellationToken ct)
    {
        var bytes = BcCodec.Serialize(msg, Encryption);
        if (Dbg) Log.Debug($"{_tag}: udp {El} → BC msgId={msg.Meta.MsgId} ({bytes.Length}B, " +
                           $"{(bytes.Length + ChunkSize - 1) / ChunkSize} data pkt)");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int off = 0; off < bytes.Length; off += ChunkSize)
            {
                int n = Math.Min(ChunkSize, bytes.Length - off);
                var chunk = bytes.AsSpan(off, n).ToArray();
                byte[] pkt;
                lock (_txGate)
                {
                    uint id = _txNext++;
                    pkt = BcUdp.BuildData(_did, id, chunk);
                    _unacked[id] = (pkt, DateTime.UtcNow);
                }
                await SendRawAsync(pkt, ct).ConfigureAwait(false);
                _txData++;
            }
        }
        finally { _sendLock.Release(); }
    }

    // ------------------------------------------------------------------ receive + reorder

    private async Task RecvLoopAsync(CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (SocketException se) // transient (e.g. ICMP) — keep listening
                {
                    if (Dbg) Log.Debug($"{_tag}: udp {El} recv socket signal {se.SocketErrorCode}");
                    continue;
                }
                _lastRxUtc = DateTime.UtcNow;
                var d = r.Buffer;
                // Endpoint forensics: if the camera sources data/acks from a DIFFERENT
                // port than the handshake endpoint we reply to, our acks are landing on
                // the wrong peer — log each distinct source once.
                if (Dbg && _rxEndpoints.Add($"{BcUdp.PeekKind(d)} {r.RemoteEndPoint}"))
                    Log.Debug($"{_tag}: udp {El} source {r.RemoteEndPoint} first {BcUdp.PeekKind(d)} " +
                              $"(session endpoint {_camera})");
                switch (BcUdp.PeekKind(d))
                {
                    case BcUdp.Kind.Data when BcUdp.TryParseData(d, out var dcid, out var pid, out var payload):
                        _rxData++;
                        _lastDataUtc = DateTime.UtcNow;
                        _rxLastPid = pid;
                        if (pid > _rxMaxPid) _rxMaxPid = pid;
                        // Data should be stamped with our cid; a mismatch is a red flag
                        // worth surfacing once, at Info (it may explain acks being ignored).
                        if (!_connIdWarned && dcid != _cid)
                        {
                            _connIdWarned = true;
                            Log.Info($"{_tag}: UDP note — camera stamps data with connId {dcid}, not our cid {_cid}");
                        }
                        if (Dbg && _rxData == 1)
                            Log.Debug($"{_tag}: udp {El} first DATA pid={pid} connId={dcid} ({payload.Length}B)");
                        await OnDataAsync(pid, payload, ct).ConfigureAwait(false);
                        break;
                    case BcUdp.Kind.Ack when BcUdp.TryParseAck(d, out var acid, out var gid, out var apid,
                        out var alat, out var atlen):
                        if (Dbg) Log.Debug($"{_tag}: udp {El} ← ACK connId={acid} group={(int)gid} pid={(int)apid} " +
                                           $"latency={alat} truth={atlen}B");
                        if (gid != BcUdp.NoneReceived && apid != BcUdp.NoneReceived) OnAck(apid);
                        break;
                    case BcUdp.Kind.Discovery
                        when UdpDiscovery.TryParseDiscovery(d, out _, out var xml, out _):
                        _rxCtrl++;
                        _lastCtrlUtc = DateTime.UtcNow;
                        var (root, _, ctrlDid) = ParseControl(xml);
                        _ctrlTally[root] = _ctrlTally.GetValueOrDefault(root) + 1;
                        if (_ctrlDetail.Count < CtrlDetailMax)
                            _ctrlDetail.Add($"{root}{(ctrlDid is int cd ? $"(did {cd})" : "")}@{(DateTime.UtcNow - _t0).TotalSeconds:0.0}s");
                        // Full per-message timeline stays at Debug (off by default).
                        if (Dbg) Log.Debug($"{_tag}: udp {El} ← CONTROL {Condense(xml)}");
                        if (root.Contains("DISC", StringComparison.Ordinal))
                        {
                            // The camera runs one session PER HELLO it answers, all
                            // sharing our socket — the handshake's retransmits (two
                            // ports, 500 ms) can leave duplicate sessions behind, and
                            // when one of those starves out (~3×def ms) its D2C_DISC
                            // lands here too. Only a disconnect addressed to OUR
                            // session (did match) may close us; a duplicate's death
                            // notice must not kill a healthy stream.
                            if (ctrlDid is int fdid && fdid != _did)
                            {
                                if (_foreignDiscLogged.Add(fdid))
                                    Log.Info($"{_tag}: ignoring D2C_DISC for another session (did {fdid}; ours is {_did}) — stream continues");
                                break;
                            }
                            var sinceData = _lastDataUtc == default ? -1 : (DateTime.UtcNow - _lastDataUtc).TotalMilliseconds;
                            // One compact Info line (no debug level needed): everything
                            // needed to see WHY the camera closed and what it sent.
                            Log.Info($"{_tag}: UDP camera closed the session (D2C_DISC, our did {_did}) after {(DateTime.UtcNow - _t0).TotalSeconds:0.0}s — " +
                                     $"rx {_rxData} data (last {sinceData:0}ms ago), camera sent [{TallyString()}]; " +
                                     $"we sent {_txAck} acks/{_txHb} hb/{_txC2dA} C2D_A; " +
                                     $"keepalive (msg 234): {_rxKa} from camera, {_txKa} answered; " +
                                     (_confirmLogged ? "session confirmed" : "camera never sent D2C_T"));
                            _cts.Cancel();
                            return;
                        }
                        // A connect-reply for a session that is not ours: the camera
                        // answered one of our handshake retransmits with a second
                        // session and keeps re-offering it. Release it (once) with a
                        // C2D_DISC so the camera drops it immediately instead of
                        // starving it out next to our live one.
                        if (root == "D2C_C_R" && ctrlDid is int odid && odid != _did)
                        {
                            if (_orphansReleased.Add(odid))
                            {
                                Log.Info($"{_tag}: releasing a duplicate handshake session the camera offered (did {odid}; ours is {_did})");
                                var rel = UdpDiscovery.BuildDiscovery(_tid, UdpDiscovery.BuildC2dDisc(_cid, odid));
                                await SendRawAsync(rel, ct).ConfigureAwait(false);
                            }
                            break;
                        }
                        // The camera confirms the session by sending D2C_T and waits
                        // for C2D_A; without the reply it recycles the session (~8 s).
                        if (xml.Contains("<D2C_T>", StringComparison.Ordinal))
                            await SendC2dAAsync(xml, ct).ConfigureAwait(false);
                        // Everything else (D2C_HB heartbeats) is a keep-alive; ignore.
                        break;
                    default:
                        if (Dbg) Log.Debug($"{_tag}: udp {El} ← UNRECOGNIZED {d.Length}B " +
                                           $"{Convert.ToHexString(d.AsSpan(0, Math.Min(16, d.Length)))}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { fault = ex; }
        finally { _pipe.Writer.Complete(fault); }
    }

    private async Task OnDataAsync(uint pid, byte[] payload, CancellationToken ct)
    {
        List<byte[]>? deliver = null;
        lock (_rxGate)
        {
            // Duplicate forensics: the arrival spacing of repeat copies separates
            // deliberate camera-side redundancy from "camera never registered our
            // acks and is retransmitting". Some battery firmwares (Argus MagiCam)
            // re-send EVERY data packet ~3x with backoff regardless of acks, which
            // eats their own radio budget — tracked here so the session can say so.
            {
                var now = DateTime.UtcNow;
                if (_pidSeen.TryGetValue(pid, out var seen))
                {
                    _pidSeen[pid] = (seen.First, seen.Count + 1);
                    _rxDup++;
                    if (Dbg && _dupLogged < DupLogMax)
                    {
                        _dupLogged++;
                        Log.Debug($"{_tag}: udp {El} dup DATA pid={pid} copy#{seen.Count + 1} " +
                                  $"+{(now - seen.First).TotalMilliseconds:0}ms after first sighting");
                    }
                }
                else
                {
                    _pidSeen[pid] = (now, 1);
                    if (_pidSeen.Count > 4096)
                        foreach (var k in _pidSeen.Keys.Where(k => k + 2048 < pid).ToList())
                            _pidSeen.Remove(k);
                }
                // One-time diagnosis, no debug level needed: over half the packets the
                // camera sends are retransmit copies while nothing is actually lost on
                // our side. The camera's transport is saturated — its encoder outruns
                // what its UDP path ships (single 1080p encoder + slow wake-chip radio
                // on some battery models), so video WILL lag and stutter on every
                // client, the official app included. Named here so a user reading the
                // log gets an answer instead of a mystery.
                if (!_saturationLogged && _rxData > 600 && _rxDup * 2 > _rxData)
                {
                    _saturationLogged = true;
                    Log.Info($"{_tag}: UDP transport saturated — {_rxDup} of {_rxData} data packets were " +
                             "retransmit copies (the camera re-sends everything ~3x). Its radio path ships " +
                             "less video than its encoder produces, so expect choppy playback on ALL clients " +
                             "including the Reolink app; this is camera firmware behavior, not a network or " +
                             "server problem. Known on: Argus MagiCam.");
                }
            }
            if (pid == _rxNext)
            {
                deliver = new List<byte[]> { payload };
                _rxNext++;
                while (_rxBuffer.TryGetValue(_rxNext, out var next))
                {
                    _rxBuffer.Remove(_rxNext);
                    deliver.Add(next);
                    _rxNext++;
                }
            }
            else if (pid > _rxNext)
            {
                _rxBuffer[pid] = payload; // out of order — hold until the gap fills
            }
            // pid < _rxNext: already delivered; drop. The ack timer re-acks anyway.
        }

        // Acking is NOT done here — a dedicated timer (AckLoopAsync) sends the
        // cumulative ack continuously, so the camera keeps seeing acks even if this
        // delivery write briefly stalls. The generous pipe threshold keeps the write
        // synchronous under normal video rates, so the receive loop keeps draining.
        if (deliver != null)
            foreach (var chunk in deliver)
                await _pipe.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);
    }

    /// <summary>Confirm the session: the camera sends D2C_T (with its sid and the
    /// connection type) and requires a C2D_A echoing them, or it recycles the
    /// session. The camera resends D2C_T until it sees the C2D_A, so replying on
    /// each one keeps it confirmed.</summary>
    private async Task SendC2dAAsync(string d2cTXml, CancellationToken ct)
    {
        uint sid = 0;
        string conn = "local";
        int cid = _cid, did = _did;
        try
        {
            var el = System.Xml.Linq.XElement.Parse(d2cTXml).Elements().FirstOrDefault();
            if (el != null)
            {
                if (uint.TryParse(el.Element("sid")?.Value, out var s)) sid = s;
                conn = el.Element("conn")?.Value is { Length: > 0 } c ? c : "local";
                // Echo the D2C_T's own ids — the camera may be confirming a session
                // other than the one we stream on, and the reply must match its offer.
                if (int.TryParse(el.Element("cid")?.Value, out var pc)) cid = pc;
                if (int.TryParse(el.Element("did")?.Value, out var pd)) did = pd;
            }
        }
        catch { /* fall back to defaults */ }

        _txC2dA++;
        if (!_confirmLogged)
        {
            _confirmLogged = true;
            Log.Debug($"{_tag}: udp {El} → C2D_A (confirm) sid {sid}, conn {conn}, did {did}");
        }
        var pkt = UdpDiscovery.BuildDiscovery(_tid,
            UdpDiscovery.BuildC2dA(sid, conn, cid, did, 1350));
        await SendRawAsync(pkt, ct).ConfigureAwait(false);
    }

    /// <summary>Once-a-second session heartbeat for the log: full traffic state so a
    /// capture shows the exact cadence and what went quiet before a D2C_DISC.</summary>
    private async Task DiagLoopAsync(CancellationToken ct)
    {
        if (!Dbg) return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                int unacked; lock (_txGate) unacked = _unacked.Count;
                uint rxNext; lock (_rxGate) rxNext = _rxNext;
                double dataAge = _lastDataUtc == default ? -1 : (DateTime.UtcNow - _lastDataUtc).TotalMilliseconds;
                double rxAge = _lastRxUtc == default ? -1 : (DateTime.UtcNow - _lastRxUtc).TotalMilliseconds;
                Log.Debug($"{_tag}: udp {El} state — rx {_rxData} data ({_rxDup} dup, next {rxNext}, maxPid {_rxMaxPid}, " +
                          $"lastData {dataAge:0}ms, lastRx {rxAge:0}ms), rxCtrl {_rxCtrl}; " +
                          $"tx {_txData} data ({unacked} unacked), {_txAck} acks, {_txHb} hb, {_txC2dA} C2D_A, " +
                          $"ka {_rxKa}rx/{_txKa}tx");
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Continuous cumulative ack — the flow-control + keepalive signal the camera
    /// expects (the official client sends it every ~10 ms regardless of new data).
    /// Sending it on its own timer, decoupled from delivery, is what stops the
    /// camera closing the session with a clean D2C_DISC after a few seconds.
    /// </summary>
    private async Task AckLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(AckInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                uint group, ackId;
                byte[] truth = Array.Empty<byte>();
                lock (_rxGate)
                {
                    if (_rxNext == 0) { group = BcUdp.NoneReceived; ackId = BcUdp.NoneReceived; }
                    else
                    {
                        group = 0; ackId = _rxNext - 1;
                        // Truth table: one byte per pid after ackId — 1 = held in the
                        // reorder buffer, 0 = missing. Without it, a single lost packet
                        // makes the camera retransmit its ENTIRE unacked window (it has
                        // no idea we already hold everything past the hole), which eats
                        // its send budget and collapses live video to a keyframe
                        // slideshow. With it, the camera resends just the holes.
                        if (_rxBuffer.Count > 0)
                        {
                            uint maxHeld = _rxBuffer.Keys.Last();
                            int span = (int)Math.Min(maxHeld - _rxNext + 1, 1024);
                            truth = new byte[span];
                            for (int i = 0; i < span; i++)
                                if (_rxBuffer.ContainsKey(_rxNext + (uint)i)) truth[i] = 1;
                        }
                    }
                }
                await SendRawAsync(BcUdp.BuildAck(_did, group, ackId, 0, truth), ct)
                    .ConfigureAwait(false);
                if (_txAck++ == 0) Log.Debug($"{_tag}: udp {El} → first ACK (connId=did {_did}, every {AckInterval.TotalMilliseconds:0}ms)");
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string Condense(string xml) =>
        string.Join("", xml.Split('\n', '\r').Select(s => s.Trim()));

    /// <summary>Inner element name of a control XML (D2C_T, D2C_HB, D2C_DISC, ...).</summary>
    private static (string Root, int? Cid, int? Did) ParseControl(string xml)
    {
        try
        {
            var el = System.Xml.Linq.XElement.Parse(xml).Elements().FirstOrDefault();
            if (el == null) return ("?", null, null);
            int? Get(string name) => int.TryParse(el.Element(name)?.Value, out var v) ? v : null;
            return (el.Name.LocalName, Get("cid"), Get("did"));
        }
        catch { return ("?", null, null); }
    }

    private string TallyString()
    {
        if (_ctrlTally.Count == 0) return "none";
        var tally = string.Join(" ", _ctrlTally.Select(kv => $"{kv.Key}×{kv.Value}"));
        var detail = string.Join(" ", _ctrlDetail);
        if (_rxCtrl > CtrlDetailMax) detail += $" +{_rxCtrl - CtrlDetailMax} more";
        return $"{tally}; timeline: {detail}";
    }

    private void OnAck(uint pid)
    {
        lock (_txGate)
        {
            // Cumulative: everything up to and including pid is confirmed.
            foreach (var k in _unacked.Keys.Where(k => k <= pid).ToList())
                _unacked.Remove(k);
        }
    }

    // ------------------------------------------------------------------ BC framing (shared)

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _pipe.Reader.AsStream();
        Exception? fault = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await BcCodec.ReadMessageAsync(stream, _context, ct).ConfigureAwait(false);
                if (Dbg)
                    Log.Debug($"{_tag}: udp {El} ← BC msgId={msg.Meta.MsgId} class=0x{msg.Meta.Class:x4} " +
                              $"msgNum={msg.Meta.MsgNum} resp=0x{msg.Meta.ResponseCode:x4}");
                // Msg 234 is the camera's UDP-session keepalive QUESTION, not a
                // push: it must be answered (echo the message, response 200) or a
                // battery camera decides the client is gone and recycles the
                // session with a clean D2C_DISC after ~8 s. The reference client
                // replies from a dedicated handler; we reply inline here.
                if (msg.Meta.MsgId == BcConstants.MsgIdUdpKeepAlive)
                {
                    if (_rxKa++ == 0)
                        Log.Info($"{_tag}: camera sent its UDP keepalive (msg 234) — replying " +
                                 "(unanswered, battery cameras drop the session after ~8s)");
                    var pong = new BcMessage
                    {
                        Meta = new BcMeta
                        {
                            MsgId = BcConstants.MsgIdUdpKeepAlive,
                            ChannelId = msg.Meta.ChannelId,
                            MsgNum = msg.Meta.MsgNum,
                            StreamType = msg.Meta.StreamType,
                            ResponseCode = 200,
                            Class = BcConstants.ClassModern,
                        },
                    };
                    try
                    {
                        await SendAsync(pong, ct).ConfigureAwait(false);
                        _txKa++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Debug($"{_tag}: keepalive reply failed: {ex.Message}");
                    }
                    continue;
                }
                Channel<BcMessage>? target;
                lock (_subGate) { _subscribers.TryGetValue(msg.Meta.MsgId, out target); }
                if (target != null)
                {
                    target.Writer.TryWrite(msg);
                }
                else
                {
                    bool first;
                    lock (_subGate) { first = _reportedUnhandled.Add(msg.Meta.MsgId); }
                    if (first) Log.Debug($"{_tag}: BC(udp) unhandled push msgId={msg.Meta.MsgId}{XmlPreview(msg)}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { fault = ex; }
        finally
        {
            lock (_subGate)
            {
                foreach (var ch in _subscribers.Values)
                    ch.Writer.TryComplete(fault ?? new EndOfStreamException("UDP connection closed"));
                _subscribers.Clear();
            }
        }
    }

    private static string XmlPreview(BcMessage msg)
    {
        if (msg.Xml == null)
            return msg.Binary is { Length: > 0 } b ? $" ({b.Length} binary bytes)" : "";
        var s = Encoding.UTF8.GetString(msg.Xml.Serialize()).Replace('\r', ' ').Replace('\n', ' ');
        return " xml=" + (s.Length > 400 ? s[..400] + "…" : s);
    }

    // ------------------------------------------------------------------ keep-alive + retransmit

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            // Send the first heartbeat immediately (the reference's tokio interval
            // fires on its first tick), then once a second, so the camera sees the
            // session confirmed the instant it answers.
            while (!ct.IsCancellationRequested)
            {
                var hb = UdpDiscovery.BuildDiscovery(_tid, UdpDiscovery.BuildC2dHb(_cid, _did));
                await SendRawAsync(hb, ct).ConfigureAwait(false);
                if (_txHb++ == 0) Log.Debug($"{_tag}: udp control → C2D_HB (heartbeat, {Heartbeat.TotalSeconds:0.#}s, tid {_tid:x8})");
                await Task.Delay(Heartbeat, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RetxLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var resend = new List<byte[]>();
                lock (_txGate)
                {
                    var now = DateTime.UtcNow;
                    foreach (var k in _unacked.Keys.ToList())
                    {
                        var (pkt, sent) = _unacked[k];
                        if (now - sent > RetxAfter)
                        {
                            resend.Add(pkt);
                            _unacked[k] = (pkt, now);
                        }
                    }
                }
                foreach (var pkt in resend)
                    await SendRawAsync(pkt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendRawAsync(byte[] pkt, CancellationToken ct)
    {
        await _rawGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _udp.SendAsync(pkt, _camera, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally { _rawGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Politely tell the camera we're leaving (best effort), then tear down.
        try
        {
            var disc = UdpDiscovery.BuildDiscovery(_tid, UdpDiscovery.BuildC2dDisc(_cid, _did));
            await _udp.SendAsync(disc, _camera, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _cts.Cancel();
        try { _udp.Close(); } catch { }
        foreach (var loop in new[] { _recvLoop, _readLoop, _hbLoop, _retxLoop, _ackLoop, _diagLoop })
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _udp.Dispose();
        _sendLock.Dispose();
        _rawGate.Dispose();
    }
}
