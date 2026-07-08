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
public sealed class BcConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
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
                var msg = await BcCodec.ReadMessageAsync(_stream, _context, ct).ConfigureAwait(false);
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
                    // The FIRST sighting of an unhandled push gets a visible line:
                    // new firmware features (doorbell buttons, extra sensors) tend
                    // to arrive on message ids nobody subscribed to yet, and the
                    // payload is exactly what's needed to map them.
                    bool first;
                    lock (_subGate) { first = _reportedUnhandled.Add(msg.Meta.MsgId); }
                    if (first)
                        Log.Info($"BC: unhandled push msgId={msg.Meta.MsgId}{XmlPreview(msg)} — if this " +
                                 "line appears right after a camera action (say, a doorbell press), please report it");
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
        return new BcSubscription(this, msgId, channel.Reader);
    }

    internal void Unsubscribe(uint msgId)
    {
        lock (_subGate) { _subscribers.Remove(msgId); }
    }

    public async Task SendAsync(BcMessage msg, CancellationToken ct)
    {
        var packet = BcCodec.Serialize(msg, Encryption);
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
    private readonly BcConnection _conn;
    private readonly uint _msgId;
    public ChannelReader<BcMessage> Messages { get; }

    internal BcSubscription(BcConnection conn, uint msgId, ChannelReader<BcMessage> reader)
    {
        _conn = conn;
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

    public void Dispose() => _conn.Unsubscribe(_msgId);
}
