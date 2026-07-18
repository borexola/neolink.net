// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Neolink.Bc;

namespace Neolink.Protocol;

/// <summary>
/// A TCP connection to a camera. Handles framing and routes incoming messages to
/// subscribers by message ID. One subscriber per message ID at a time.
/// </summary>
public sealed class BcConnection : IBcConnection
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly BufferedStream _readStream; // read side only; writes go to _stream directly
    private readonly Dictionary<uint, Channel<BcMessage>> _subscribers = new();
    private readonly HashSet<uint> _reportedUnhandled = new();
    private readonly object _subGate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts;
    private readonly Task _readLoop;

    public EncryptionState Encryption { get; } = new();
    private readonly BcContext _context;

    private BcConnection(TcpClient tcp, CancellationToken appCt)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        // The read loop parses a 20-byte header, then the body, per message —
        // hundreds of messages/s on a streaming camera. Unbuffered, every one of
        // those small reads is its own recv() syscall (expensive under
        // virtualization); buffering turns them into few large recvs. Reads only:
        // the send path keeps writing the raw stream, so nothing is ever queued
        // in this buffer on the way out.
        _readStream = new BufferedStream(_stream, 128 * 1024);
        _context = new BcContext(Encryption);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public static async Task<BcConnection> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Timed out connecting to {host}:{port}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new BcConnection(tcp, ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await BcCodec.ReadMessageAsync(_readStream, _context, ct).ConfigureAwait(false);
                // Hot path: media arrives as one message per chunk, so guard the
                // level BEFORE building the string — otherwise every video chunk
                // pays for an interpolation that Info-level runs then discard.
                if (Log.Level <= LogLevel.Debug)
                    Log.Debug($"BC recv: msgId={msg.Meta.MsgId} class=0x{msg.Meta.Class:x4} msgNum={msg.Meta.MsgNum} channel={msg.Meta.ChannelId} stream={msg.Meta.StreamType} response=0x{msg.Meta.ResponseCode:x4}");
                Channel<BcMessage>? target = null;
                lock (_subGate)
                {
                    _subscribers.TryGetValue(msg.Meta.MsgId, out target);
                }
                if (target != null)
                {
                    // Unbounded channel: never blocks. Video frames are consumed promptly downstream.
                    target.Writer.TryWrite(msg);
                }
                else
                {
                    // The first sighting of an unhandled push logs its payload —
                    // that's how new firmware features (doorbell buttons, extra
                    // sensors) get mapped. Debug level on purpose: cameras
                    // broadcast a burst of routine status pushes (VideoInput,
                    // Serial, sleepStatus, ...) on every login, and at Info that
                    // drowned real logs. Set NEOLINK_LOG=debug when hunting.
                    bool first;
                    lock (_subGate) { first = _reportedUnhandled.Add(msg.Meta.MsgId); }
                    if (first)
                        Log.Debug($"BC: unhandled push msgId={msg.Meta.MsgId}{XmlPreview(msg)}");
                    else
                        Log.Trace($"Ignoring uninteresting message ID {msg.Meta.MsgId}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            lock (_subGate)
            {
                foreach (var ch in _subscribers.Values)
                    ch.Writer.TryComplete(fault ?? new EndOfStreamException("connection closed"));
                _subscribers.Clear();
            }
        }
    }

    /// <summary>Compact one-line body preview for diagnostics of unknown pushes.</summary>
    private static string XmlPreview(BcMessage msg)
    {
        if (msg.Xml == null)
            return msg.Binary is { Length: > 0 } b ? $" ({b.Length} binary bytes)" : "";
        var s = Encoding.UTF8.GetString(msg.Xml.Serialize()).Replace('\r', ' ').Replace('\n', ' ');
        return " xml=" + (s.Length > 400 ? s[..400] + "…" : s);
    }

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

    internal void Unsubscribe(uint msgId)
    {
        lock (_subGate) { _subscribers.Remove(msgId); }
    }

    public async Task SendAsync(BcMessage msg, CancellationToken ct)
    {
        var packet = BcCodec.Serialize(msg, Encryption);
        // Guarded like the read loop: talk audio sends ~30 messages/s.
        if (Log.Level <= LogLevel.Debug)
            Log.Debug($"BC send: msgId={msg.Meta.MsgId} class=0x{msg.Meta.Class:x4} msgNum={msg.Meta.MsgNum} channel={msg.Meta.ChannelId} stream={msg.Meta.StreamType} response=0x{msg.Meta.ResponseCode:x4} bytes={packet.Length}");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(packet, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _tcp.Close(); } catch { }
        try { await _readLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _tcp.Dispose();
    }
}

public sealed class BcSubscription : IDisposable
{
    private readonly Action _unsubscribe;
    private readonly uint _msgId;
    public ChannelReader<BcMessage> Messages { get; }

    // Holds an unsubscribe delegate rather than a concrete connection, so the same
    // subscription type serves both the TCP and UDP transports.
    internal BcSubscription(uint msgId, ChannelReader<BcMessage> reader, Action unsubscribe)
    {
        _unsubscribe = unsubscribe;
        _msgId = msgId;
        Messages = reader;
    }

    public async Task<BcMessage> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await Messages.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for reply to message ID {_msgId}");
        }
        catch (ChannelClosedException ex)
        {
            throw new IOException($"Connection closed while waiting for message ID {_msgId}", ex.InnerException);
        }
    }

    public void Dispose() => _unsubscribe();
}
