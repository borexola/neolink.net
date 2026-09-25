// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Neolink.Streaming;

/// <summary>Who is watching live right now, over RTSP or the web UI, for the Monitor page. Recorders
/// and other internal taps never register, just as the viewer count leaves them out.</summary>
public sealed class ViewerRegistry
{
    /// <summary>One viewer: the camera and stream it watches, how (RTSP or Web), from which address, as whom.</summary>
    public sealed record Viewer(string Camera, string Stream, string Via, string? From, string? User, DateTime SinceUtc);

    private readonly ConcurrentDictionary<long, Viewer> _current = new();
    private long _next;

    /// <summary>Subscribes a viewer to <paramref name="hub"/> and lists it; disposing does both in reverse.</summary>
    public Watch Add(IStreamHub hub, string via, string? from, string? user)
    {
        var (camera, stream) = Split(hub.Name);
        var (subId, reader) = hub.Subscribe(viewer: true);
        var id = Interlocked.Increment(ref _next);
        _current[id] = new Viewer(camera, stream, via, from, user, DateTime.UtcNow);
        return new Watch(this, id, hub, subId, reader);
    }

    /// <summary>The viewers now, longest watching first.</summary>
    public IReadOnlyList<Viewer> Snapshot() => _current.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

    /// <summary>A hub is named "{camera} {stream}", and a camera name may itself hold spaces.</summary>
    internal static (string Camera, string Stream) Split(string hubName)
    {
        int space = hubName.LastIndexOf(' ');
        return space > 0 ? (hubName[..space], hubName[(space + 1)..]) : (hubName, "");
    }

    /// <summary>One viewer's hub subscription plus its place in the list.</summary>
    public sealed class Watch : IDisposable
    {
        private readonly ViewerRegistry _owner;
        private readonly long _id;
        private readonly IStreamHub _hub;
        private readonly Guid _subId;
        private int _done;

        internal Watch(ViewerRegistry owner, long id, IStreamHub hub, Guid subId, ChannelReader<HubPacket> reader)
        {
            _owner = owner;
            _id = id;
            _hub = hub;
            _subId = subId;
            Reader = reader;
        }

        public ChannelReader<HubPacket> Reader { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            _owner._current.TryRemove(_id, out _);
            _hub.Unsubscribe(_subId);
        }
    }
}
