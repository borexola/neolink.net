// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Web;

/// <summary>
/// Per-camera online/offline history kept as run-length transitions, fed by the
/// SystemMonitor's sampling tick. Powers the monitor page's availability score
/// and status ribbon. In-memory by design: observation starts at process start
/// and the history is capped to the last 24 hours — an honest "what did this
/// process actually see", not a synthetic lifetime SLA.
/// </summary>
public sealed class CameraAvailability
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private sealed class Track
    {
        public readonly List<(long StartMs, bool Online)> Runs = new();
        public bool? Last;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feed the camera's current state; appends a run only on transitions.</summary>
    public void Update(string camera, bool online, long nowMs)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(camera, out var t))
                _tracks[camera] = t = new Track();
            if (t.Last != online)
            {
                t.Runs.Add((nowMs, online));
                t.Last = online;
            }
            // Trim runs that ended before the window; the first kept run may
            // still START before it (it's clamped when reporting).
            long cutoff = nowMs - (long)Window.TotalMilliseconds;
            while (t.Runs.Count > 1 && t.Runs[1].StartMs <= cutoff)
                t.Runs.RemoveAt(0);
        }
    }

    /// <param name="Runs">Transitions inside the window: [startUnixMs, 1|0].</param>
    public sealed record Snapshot(string Camera, bool Online, double UptimePct, long ObservedMs,
        int Outages, long LongestOutageMs, long CurrentSinceMs, List<long[]> Runs);

    public List<Snapshot> Snapshots(long nowMs)
    {
        lock (_gate)
        {
            var result = new List<Snapshot>(_tracks.Count);
            foreach (var (name, t) in _tracks)
            {
                if (t.Runs.Count == 0 || t.Last is not bool online) continue;
                long windowStart = Math.Max(t.Runs[0].StartMs, nowMs - (long)Window.TotalMilliseconds);
                long observed = Math.Max(1, nowMs - windowStart);
                long up = 0, longestOut = 0;
                int outages = 0;
                for (int i = 0; i < t.Runs.Count; i++)
                {
                    long s = Math.Max(t.Runs[i].StartMs, windowStart);
                    long e = i + 1 < t.Runs.Count ? t.Runs[i + 1].StartMs : nowMs;
                    if (e <= s) continue;
                    if (t.Runs[i].Online)
                    {
                        up += e - s;
                    }
                    else
                    {
                        outages++;
                        longestOut = Math.Max(longestOut, e - s);
                    }
                }
                result.Add(new Snapshot(name, online,
                    Math.Round(100.0 * up / observed, 2), observed, outages, longestOut,
                    t.Runs[^1].StartMs,
                    t.Runs.Select(r => new[] { Math.Max(r.StartMs, windowStart), r.Online ? 1L : 0L })
                        .ToList()));
            }
            result.Sort((a, b) => string.Compare(a.Camera, b.Camera, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
