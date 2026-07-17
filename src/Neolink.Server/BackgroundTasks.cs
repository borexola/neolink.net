// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.

namespace Neolink;

/// <summary>
/// Process-wide registry of long-running background jobs the admin should know
/// about (footage archiving, and whatever comes next). A job calls
/// <see cref="Begin"/>, updates its handle as it progresses, and disposes it when
/// done; the web UI polls <see cref="Active"/> (via the admin-only
/// /api/background endpoint) and shows a progress strip while anything runs.
/// Deliberately in-memory only: these are live activities, not history — a
/// restart means nothing is running.
/// </summary>
public static class BackgroundTasks
{
    /// <summary>A running job: <paramref name="Percent"/> is 0–100, or null while
    /// the job cannot estimate (the UI shows an indeterminate bar).</summary>
    public sealed record Info(string Id, string Name, string? Detail, double? Percent, DateTime StartedUtc);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Name, string? Detail, double? Percent, DateTime Started)> Tasks = new();

    /// <summary>Registers a job and returns the handle it reports progress on.
    /// Always dispose the handle (use <c>using</c>) — a leaked entry would show a
    /// forever-running job to the admin.</summary>
    public static Handle Begin(string name, string? detail = null, double? percent = null)
    {
        var id = Guid.NewGuid().ToString("n");
        lock (Gate) Tasks[id] = (name, detail, percent, DateTime.UtcNow);
        return new Handle(id);
    }

    /// <summary>Snapshot of everything currently running, oldest first.</summary>
    public static IReadOnlyList<Info> Active()
    {
        lock (Gate)
            return Tasks
                .Select(kv => new Info(kv.Key, kv.Value.Name, kv.Value.Detail, kv.Value.Percent, kv.Value.Started))
                .OrderBy(t => t.StartedUtc)
                .ToList();
    }

    public sealed class Handle : IDisposable
    {
        private readonly string _id;
        internal Handle(string id) => _id = id;

        /// <summary>Updates the job's detail line and/or progress percentage
        /// (clamped to 0–100; null keeps the bar indeterminate).</summary>
        public void Report(string? detail = null, double? percent = null)
        {
            lock (Gate)
            {
                if (!Tasks.TryGetValue(_id, out var t)) return;
                Tasks[_id] = (t.Name, detail ?? t.Detail,
                    percent is double p ? Math.Clamp(p, 0, 100) : t.Percent, t.Started);
            }
        }

        public void Dispose()
        {
            lock (Gate) Tasks.Remove(_id);
        }
    }
}
