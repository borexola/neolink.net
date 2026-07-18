// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net.Security;
using System.Net.Sockets;

namespace Neolink.Mqtt;

/// <summary>Connection options for <see cref="MqttClient"/> (a "last will" is required for HA availability).</summary>
public sealed class MqttClientOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string ClientId { get; init; }
    public int KeepAliveSeconds { get; init; } = 30;
    public bool Tls { get; init; }
    public string? WillTopic { get; init; }
    public string? WillPayload { get; init; }
    public bool WillRetain { get; init; } = true;
}

/// <summary>
/// A minimal, resilient MQTT 3.1.1 client: one background task owns the socket,
/// runs the keep-alive ping loop and the read loop, and reconnects with backoff
/// on any failure. Publishing and subscribing are thread-safe and simply no-op
/// while disconnected — the owner republishes state on <see cref="Connected"/>.
/// Only QoS 0 is used (sufficient, with retained state, for Home Assistant).
/// </summary>
public sealed class MqttClient
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttClientOptions _opt;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile Stream? _stream;
    private int _packetId;
    private DateTime _lastRxUtc;

    public MqttClient(MqttClientOptions options) => _opt = options;

    /// <summary>Raised (on the read-loop thread) for every PUBLISH the broker delivers.</summary>
    public event Action<string, byte[]>? MessageReceived;

    /// <summary>Test hook: wraps the raw connection stream. The selftest injects a
    /// stallable stream to exercise write-failure handling deterministically —
    /// kernel send buffering makes a real stalled socket write unreproducible.</summary>
    internal Func<Stream, Stream>? StreamWrapper { get; set; }

    /// <summary>Raised after each successful CONNACK — the owner (re)announces and subscribes here.</summary>
    public event Func<Task>? Connected;

    public bool IsConnected => _stream != null;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(ct).ConfigureAwait(false);
                backoff = MinBackoff; // a clean session end resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"MQTT: {_opt.Host}:{_opt.Port} — {Log.Flatten(ex)}; retrying in {backoff.TotalSeconds:0}s");
            }
            finally
            {
                _stream = null;
            }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        // Own connect timeout: a wrong/absent broker host must fail fast and log,
        // not sit in the OS connect timeout (20s+) between retry warnings.
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await tcp.ConnectAsync(_opt.Host, _opt.Port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"broker did not answer within 10s — is '{_opt.Host}' the right address?");
            }
        }
        Stream stream = tcp.GetStream();
        if (StreamWrapper != null) stream = StreamWrapper(stream);
        if (_opt.Tls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true); // LAN brokers use self-signed certs
            await ssl.AuthenticateAsClientAsync(_opt.Host).ConfigureAwait(false);
            stream = ssl;
        }

        // CONNECT → CONNACK handshake.
        var connect = MqttPacket.BuildConnect(_opt.ClientId, _opt.Username, _opt.Password,
            (ushort)_opt.KeepAliveSeconds, _opt.WillTopic, _opt.WillPayload, _opt.WillRetain);
        await stream.WriteAsync(connect, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var (type, body) = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
        if ((type & 0xF0) != MqttPacket.ConnAck)
            throw new IOException($"expected CONNACK, got 0x{type:X2}");
        if (body.Length < 2 || body[1] != 0x00)
            throw new IOException($"broker refused the connection (CONNACK code {(body.Length >= 2 ? body[1] : -1)})");

        _lastRxUtc = DateTime.UtcNow;
        _stream = stream;
        Log.Info($"MQTT: connected to {_opt.Host}:{_opt.Port} as '{_opt.ClientId}'");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = Task.Run(() => PingLoopAsync(stream, linked.Token), CancellationToken.None);
        try
        {
            if (Connected != null)
                await Connected.Invoke().ConfigureAwait(false);
            await ReadLoopAsync(stream, ct).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            try { await ping.ConfigureAwait(false); } catch { }
        }
    }

    // ------------------------------------------------------------------ publish / subscribe

    public Task<bool> PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        PublishAsync(topic, System.Text.Encoding.UTF8.GetBytes(payload), retain, ct);

    public async Task<bool> PublishAsync(string topic, byte[] payload, bool retain, CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null) return false;
        var packet = MqttPacket.BuildPublish(topic, payload, retain);
        return await SendAsync(stream, packet, ct).ConfigureAwait(false);
    }

    public async Task<bool> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null || topics.Count == 0) return false;
        var id = (ushort)(Interlocked.Increment(ref _packetId) & 0xFFFF);
        var packet = MqttPacket.BuildSubscribe(id == 0 ? (ushort)1 : id, topics);
        return await SendAsync(stream, packet, ct).ConfigureAwait(false);
    }

    /// <summary>Cap on a single socket write. Only a genuinely wedged broker takes
    /// this long; hitting it closes the connection. Internal so the selftest can
    /// shrink it.</summary>
    internal TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private async Task<bool> SendAsync(Stream stream, byte[] packet, CancellationToken ct)
    {
        // The caller's token only gates the wait for the gate — cancelling there is
        // harmless because nothing was written yet.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Once a frame starts, it must complete: callers (a camera session
            // winding down, a timed-out probe) must never abort a write partway,
            // or the frame boundary is lost and every later packet lands mid-frame —
            // the broker absorbs the garbage and eventually kills the client with
            // "oversize packet". So the write runs under its own watchdog, not the
            // caller's token; one flaky camera can't disturb the shared connection.
            using var writeCts = new CancellationTokenSource(WriteTimeout);
            await stream.WriteAsync(packet, writeCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // A failed (or watchdog-expired) write may have put part of the packet
            // on the wire; the only safe move is to close the socket and reconnect.
            _stream = null;
            try { stream.Close(); } catch { }
            if (ex is OperationCanceledException)
                Log.Warn($"MQTT: a {packet.Length}-byte send stalled for {WriteTimeout.TotalSeconds:0}s — closing the connection to reconnect cleanly");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ------------------------------------------------------------------ loops

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, body) = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            _lastRxUtc = DateTime.UtcNow;
            switch (type & 0xF0)
            {
                case MqttPacket.Publish:
                    var msg = MqttPacket.ParsePublish(type, body);
                    try { MessageReceived?.Invoke(msg.Topic, msg.Payload); }
                    catch (Exception ex) { Log.Warn($"MQTT: message handler for '{msg.Topic}' threw: {ex.Message}"); }
                    break;
                // PINGRESP / SUBACK / PUBACK: nothing to do beyond noting activity.
            }
        }
    }

    private async Task PingLoopAsync(Stream stream, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _opt.KeepAliveSeconds * 0.75));
        var deadline = TimeSpan.FromSeconds(_opt.KeepAliveSeconds * 2);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                if (DateTime.UtcNow - _lastRxUtc > deadline)
                {
                    _stream = null;
                    stream.Close(); // unblock the read loop → reconnect
                    return;
                }
                await SendAsync(stream, MqttPacket.BuildPingReq(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ frame reader

    /// <summary>Reads one control packet: its header byte and the "remaining length" body.</summary>
    private static async Task<(byte type, byte[] body)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        await ReadExactAsync(stream, one, ct).ConfigureAwait(false);
        byte type = one[0];

        // Remaining-length varint (up to 4 bytes).
        int length = 0, mult = 1, count = 0;
        byte digit;
        do
        {
            await ReadExactAsync(stream, one, ct).ConfigureAwait(false);
            digit = one[0];
            length += (digit & 0x7F) * mult;
            mult <<= 7;
            if (++count > 4) throw new IOException("malformed MQTT remaining-length");
        } while ((digit & 0x80) != 0);

        if (length > 64 * 1024 * 1024) throw new IOException($"implausible MQTT packet length {length}");
        var body = new byte[length];
        if (length > 0) await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        return (type, body);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(off), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("MQTT connection closed by broker");
            off += n;
        }
    }
}
