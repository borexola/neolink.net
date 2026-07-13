// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Recording;

/// <summary>
/// Process-wide record of recent recording WRITE failures (a disk error mid-write:
/// a failing/disconnected drive or a permissions problem — distinct from the
/// volume being full, which the free-space guard handles). Recorders stamp a
/// camera on each fault; the alert monitor reads the recent set. Thread-safe and
/// dependency-free so it can be shared without coupling the recorders to email.
/// </summary>
public sealed class RecordingHealth
{
    private readonly Dictionary<string, DateTime> _lastError = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>A recorder hit a write error for this camera just now.</summary>
    public void MarkWriteError(string camera)
    {
        lock (_gate) _lastError[camera] = DateTime.UtcNow;
    }

    /// <summary>Cameras that had a write error within <paramref name="within"/>.</summary>
    public IReadOnlyList<string> CamerasWithRecentErrors(TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        lock (_gate)
            return _lastError.Where(kv => kv.Value >= cutoff).Select(kv => kv.Key)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
