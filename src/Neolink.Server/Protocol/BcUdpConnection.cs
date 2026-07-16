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
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2);

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
    private readonly Pipe _pipe = new();

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

    private readonly Task _recvLoop, _readLoop, _hbLoop, _retxLoop;

    private BcUdpConnection(UdpDiscovery.Session session, string tag, CancellationToken appCt)
    {
        _udp = session.Socket;
        _camera = session.Camera;
        _cid = session.ClientId;
        _did = session.DeviceId;
        _tag = tag;
        _context = new BcContext(Encryption);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _recvLoop = Task.Run(() => RecvLoopAsync(_cts.Token));
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _hbLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _retxLoop = Task.Run(() => RetxLoopAsync(_cts.Token));
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
        if (Log.Level <= LogLevel.Debug)
            Log.Debug($"{_tag}: BC(udp) send msgId={msg.Meta.MsgId} bytes={bytes.Length}");
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
                catch (SocketException) { continue; } // transient (e.g. ICMP) — keep listening
                var d = r.Buffer;
                switch (BcUdp.PeekKind(d))
                {
                    case BcUdp.Kind.Data when BcUdp.TryParseData(d, out _, out var pid, out var payload):
                        await OnDataAsync(pid, payload, ct).ConfigureAwait(false);
                        break;
                    case BcUdp.Kind.Ack when BcUdp.TryParseAck(d, out _, out var gid, out var apid):
                        if (gid != BcUdp.NoneReceived && apid != BcUdp.NoneReceived) OnAck(apid);
                        break;
                    case BcUdp.Kind.Discovery
                        when UdpDiscovery.TryParseDiscovery(d, out _, out var xml, out _)
                             && xml.Contains("_DISC", StringComparison.Ordinal):
                        Log.Info($"{_tag}: UDP camera closed the session (D2C_DISC)");
                        _cts.Cancel();
                        return;
                    // Other discovery packets (heartbeat replies) are keep-alives; ignore.
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
        uint group, ackId;
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
            // pid < _rxNext: already delivered; drop but still re-ack below.

            if (_rxNext == 0) { group = BcUdp.NoneReceived; ackId = BcUdp.NoneReceived; }
            else { group = 0; ackId = _rxNext - 1; }
        }

        if (deliver != null)
            foreach (var chunk in deliver)
                await _pipe.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);

        await SendRawAsync(BcUdp.BuildAck(_did, group, ackId, 0, ReadOnlySpan<byte>.Empty), ct).ConfigureAwait(false);
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
                if (Log.Level <= LogLevel.Debug)
                    Log.Debug($"{_tag}: BC(udp) recv msgId={msg.Meta.MsgId} class=0x{msg.Meta.Class:x4} msgNum={msg.Meta.MsgNum}");
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
        foreach (var loop in new[] { _recvLoop, _readLoop, _hbLoop, _retxLoop })
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _udp.Dispose();
        _sendLock.Dispose();
        _rawGate.Dispose();
    }
}
