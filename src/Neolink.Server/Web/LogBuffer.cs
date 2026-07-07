// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Threading.Channels;

namespace Neolink.Web;

/// <summary>One captured log line. Seq lets a reconnecting client skip what it already has.</summary>
public sealed record LogEntry(long Seq, long UnixMs, string Level, string Message);

/// <summary>
/// In-memory tap on the console logger for the web UI's live log stream: a ring
/// of recent lines (the backlog a client gets on connect) plus bounded fan-out
/// channels for live tailing. Publish is called from <see cref="Log.Tap"/> on
/// arbitrary threads and must never throw or log.
/// </summary>
public sealed class LogBuffer
{
    private const int Capacity = 500;          // backlog depth
    private const int SubscriberQueue = 256;   // per-client buffer; slowpokes lose oldest lines

    private readonly object _gate = new();
    private readonly LogEntry[] _ring = new LogEntry[Capacity];
    private readonly Dictionary<Guid, Channel<LogEntry>> _subscribers = new();
    private long _seq;
    private int _count;
    private int _next;

    public void Publish(LogLevel level, string message)
    {
        LogEntry entry;
        List<Channel<LogEntry>>? fanout = null;
        lock (_gate)
        {
            entry = new LogEntry(++_seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Log.LevelTag(level), message);
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            if (_subscribers.Count > 0)
                fanout = _subscribers.Values.ToList();
        }
        if (fanout == null) return;
        foreach (var ch in fanout)
            ch.Writer.TryWrite(entry); // bounded DropOldest: a stuck client can't grow memory
    }

    /// <summary>The current backlog, oldest first.</summary>
    public List<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<LogEntry>(_count);
            for (int i = 0; i < _count; i++)
                result.Add(_ring[(_next - _count + i + Capacity) % Capacity]);
            return result;
        }
    }

    public (Guid Id, ChannelReader<LogEntry> Reader) Subscribe()
    {
        var ch = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(SubscriberQueue)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        lock (_gate) { _subscribers[id] = ch; }
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        lock (_gate) { _subscribers.Remove(id); }
    }
}
