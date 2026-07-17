// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Recording;

/// <summary>
/// Answers "when does this disk fill up?" for every configured storage location.
/// Free space is sampled every 15 minutes and the samples persisted in the state
/// dir (storage-trend.json), so the trend survives restarts and can genuinely
/// cover a week. The forecast is a least-squares fit over up to 7 days of
/// samples — NET change, not gross write rate, so once retention starts deleting
/// as fast as the cameras record, the honest answer becomes "steady" instead of
/// a fictional fill date. No verdict is given until 6 hours of history exist.
/// </summary>
public sealed class StorageForecast
{
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    /// <summary>History span below which the verdict is "measuring": a fit over
    /// minutes would confidently extrapolate noise.</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromHours(6);
    /// <summary>Beyond this many days out, a fill date is weather, not forecast.</summary>
    private const double SteadyDays = 3650;

    private sealed record Point(long T, long Free);   // unix ms, free bytes

    private readonly StorageLocations _storage;
    private readonly string _file;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private Dictionary<string, List<Point>> _series = new(StringComparer.OrdinalIgnoreCase);

    public StorageForecast(StorageLocations storage, string stateDir, Func<DateTime>? clock = null)
    {
        _storage = storage;
        _file = Path.Combine(stateDir, "storage-trend.json");
        _now = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>Samples every location now and persists. Offline volumes are
    /// skipped, never recorded as zeros. Paths no longer configured are pruned.</summary>
    public void SampleNow()
    {
        var statuses = _storage.Sample();
        var nowMs = new DateTimeOffset(_now(), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var cutoff = nowMs - (long)Window.TotalMilliseconds;
        lock (_gate)
        {
            var keep = new Dictionary<string, List<Point>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in statuses)
            {
                var list = _series.TryGetValue(s.Path, out var old) ? old : new List<Point>();
                list.RemoveAll(p => p.T < cutoff);
                if (s.Online && s.TotalBytes > 0)
                    list.Add(new Point(nowMs, s.FreeBytes));
                keep[s.Path] = list;
            }
            _series = keep;
            Save();
        }
    }

    /// <summary>Samples on the fixed cadence until cancelled (one sample immediately,
    /// so a fresh install starts building history the moment it boots).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { SampleNow(); }
            catch (Exception ex) { Log.Warn($"Storage forecast: sampling failed: {ex.Message}"); }
            try { await Task.Delay(SamplePeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The verdict for one location (by path):
    /// ("measuring", null) — under 6 h of history, no honest answer yet;
    /// ("steady", null)    — not shrinking (retention keeping up, or freeing);
    /// ("filling", days)   — shrinking; days until the volume runs out at this rate.</summary>
    public (string State, double? Days) Forecast(string path)
    {
        List<Point>? pts;
        lock (_gate)
        {
            if (!_series.TryGetValue(path, out pts) || pts.Count < 2)
                return ("measuring", null);
            pts = new List<Point>(pts); // fit outside the lock
        }
        double spanMs = pts[^1].T - pts[0].T;
        if (spanMs < MinSpan.TotalMilliseconds)
            return ("measuring", null);

        // Least-squares slope of free bytes over time — one big retention purge
        // mid-window nudges the fit instead of dominating an endpoint delta.
        double n = pts.Count, sx = 0, sy = 0, sxy = 0, sxx = 0;
        foreach (var p in pts)
        {
            double x = (p.T - pts[0].T) / 86400000.0; // days, small numbers keep the math stable
            sx += x; sy += p.Free; sxy += x * p.Free; sxx += x * x;
        }
        double denom = n * sxx - sx * sx;
        if (denom <= 0)
            return ("measuring", null);
        double bytesPerDay = (n * sxy - sx * sy) / denom;
        if (bytesPerDay >= 0)
            return ("steady", null);
        // Days until the FULL threshold (recording halts there, not at zero bytes).
        double usable = Math.Max(0, pts[^1].Free - StorageLocations.MinFreeBytes);
        double days = usable / -bytesPerDay;
        return days > SteadyDays ? ("steady", null) : ("filling", days);
    }

    // ------------------------------------------------------------------ persistence

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
                return;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<Point>>>(File.ReadAllText(_file));
            if (data != null)
                _series = new Dictionary<string, List<Point>>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"Storage forecast: cannot read {_file}: {ex.Message} — starting fresh");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(_series));
        }
        catch (Exception ex)
        {
            Log.Warn($"Storage forecast: cannot write {_file}: {ex.Message}");
        }
    }
}
