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
    // Match the official client: a 1 s heartbeat plus a continuous ~20 ms ack that
    // doubles as flow control and keepalive — the camera closes the session (clean
    // D2C_DISC, ~8 s) if it stops seeing acks, so acking must never be gated behind
    // downstream delivery.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AckInterval = TimeSpan.FromMilliseconds(20);

    private readonly UdpClient _udp;
    private readonly IPEndPoint _camera;
    private readonly int _cid; // our id — inbound packets carry this
    private readonly int _did; // camera id — we stamp outbound with it
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
    private long _rxData, _rxCtrl, _txAck, _txHb, _txC2dA, _txData;
    private uint _rxLastPid, _rxMaxPid;
    private DateTime _lastRxUtc, _lastDataUtc, _lastCtrlUtc;
    // Per-session tally of the camera's control messages (D2C_T, D2C_HB, ...), so the
    // one-line D2C_DISC summary shows what the camera sent — no debug level needed.
    private readonly Dictionary<string, int> _ctrlTally = new();
    private readonly Task _recvLoop, _readLoop, _hbLoop, _retxLoop, _ackLoop, _diagLoop;

    private string El => $"+{(DateTime.UtcNow - _t0).TotalMilliseconds:0}ms";
    private static bool Dbg => Log.Level <= LogLevel.Debug;

    private BcUdpConnection(UdpDiscovery.Session session, string tag, CancellationToken appCt)
    {
        _udp = session.Socket;
        _camera = session.Camera;
        _cid = session.ClientId;
        _did = session.DeviceId;
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
        var ip = await ResolveV4Async(host, ct).ConfigureAwait(false)
            ?? throw new IOException($"UDP: '{host}' has no IPv4 address");
        var session = await UdpDiscovery.EstablishAsync(ip, uid, timeout, ct, tag).ConfigureAwait(false)
            ?? throw new IOException($"UDP handshake to {host} failed (camera asleep or unreachable)");
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
                    case BcUdp.Kind.Ack when BcUdp.TryParseAck(d, out var acid, out var gid, out var apid):
                        if (Dbg) Log.Debug($"{_tag}: udp {El} ← ACK connId={acid} group={(int)gid} pid={(int)apid}");
                        if (gid != BcUdp.NoneReceived && apid != BcUdp.NoneReceived) OnAck(apid);
                        break;
                    case BcUdp.Kind.Discovery
                        when UdpDiscovery.TryParseDiscovery(d, out _, out var xml, out _):
                        _rxCtrl++;
                        _lastCtrlUtc = DateTime.UtcNow;
                        var root = ControlRoot(xml);
                        _ctrlTally[root] = _ctrlTally.GetValueOrDefault(root) + 1;
                        // Full per-message timeline stays at Debug (off by default).
                        if (Dbg) Log.Debug($"{_tag}: udp {El} ← CONTROL {Condense(xml)}");
                        if (root.Contains("DISC", StringComparison.Ordinal))
                        {
                            var sinceData = _lastDataUtc == default ? -1 : (DateTime.UtcNow - _lastDataUtc).TotalMilliseconds;
                            // One compact Info line (no debug level needed): everything
                            // needed to see WHY the camera closed and what it sent.
                            Log.Info($"{_tag}: UDP camera closed the session (D2C_DISC) after {(DateTime.UtcNow - _t0).TotalSeconds:0.0}s — " +
                                     $"rx {_rxData} data (last {sinceData:0}ms ago), camera sent [{TallyString()}]; " +
                                     $"we sent {_txAck} acks/{_txHb} hb/{_txC2dA} C2D_A; " +
                                     (_confirmLogged ? "session confirmed" : "camera never sent D2C_T"));
                            _cts.Cancel();
                            return;
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
        try
        {
            var el = System.Xml.Linq.XElement.Parse(d2cTXml).Elements().FirstOrDefault();
            if (el != null)
            {
                if (uint.TryParse(el.Element("sid")?.Value, out var s)) sid = s;
                conn = el.Element("conn")?.Value is { Length: > 0 } c ? c : "local";
            }
        }
        catch { /* fall back to defaults */ }

        _txC2dA++;
        if (!_confirmLogged)
        {
            _confirmLogged = true;
            Log.Debug($"{_tag}: udp {El} → C2D_A (confirm) sid {sid}, conn {conn}");
        }
        var pkt = UdpDiscovery.BuildDiscovery((uint)Random.Shared.Next(1, int.MaxValue),
            UdpDiscovery.BuildC2dA(sid, conn, _cid, _did, 1350));
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
                Log.Debug($"{_tag}: udp {El} state — rx {_rxData} data (next {rxNext}, maxPid {_rxMaxPid}, " +
                          $"lastData {dataAge:0}ms, lastRx {rxAge:0}ms), rxCtrl {_rxCtrl}; " +
                          $"tx {_txData} data ({unacked} unacked), {_txAck} acks, {_txHb} hb, {_txC2dA} C2D_A");
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
                lock (_rxGate)
                {
                    if (_rxNext == 0) { group = BcUdp.NoneReceived; ackId = BcUdp.NoneReceived; }
                    else { group = 0; ackId = _rxNext - 1; }
                }
                await SendRawAsync(BcUdp.BuildAck(_did, group, ackId, 0, ReadOnlySpan<byte>.Empty), ct)
                    .ConfigureAwait(false);
                if (_txAck++ == 0) Log.Debug($"{_tag}: udp {El} → first ACK (connId=did {_did}, every {AckInterval.TotalMilliseconds:0}ms)");
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string Condense(string xml) =>
        string.Join("", xml.Split('\n', '\r').Select(s => s.Trim()));

    /// <summary>Inner element name of a control XML (D2C_T, D2C_HB, D2C_DISC, ...).</summary>
    private static string ControlRoot(string xml)
    {
        try { return System.Xml.Linq.XElement.Parse(xml).Elements().FirstOrDefault()?.Name.LocalName ?? "?"; }
        catch { return "?"; }
    }

    private string TallyString() =>
        _ctrlTally.Count == 0 ? "none" : string.Join(" ", _ctrlTally.Select(kv => $"{kv.Key}×{kv.Value}"));

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
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Heartbeat, ct).ConfigureAwait(false);
                var hb = UdpDiscovery.BuildDiscovery((uint)Random.Shared.Next(1, int.MaxValue),
                    UdpDiscovery.BuildC2dHb(_cid, _did));
                await SendRawAsync(hb, ct).ConfigureAwait(false);
                if (_txHb++ == 0) Log.Debug($"{_tag}: udp control → C2D_HB (heartbeat, {Heartbeat.TotalSeconds:0.#}s)");
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
            var disc = UdpDiscovery.BuildDiscovery((uint)Random.Shared.Next(1, int.MaxValue),
                UdpDiscovery.BuildC2dDisc(_cid, _did));
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
