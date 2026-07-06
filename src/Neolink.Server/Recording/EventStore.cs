using System.Text.Json;

namespace Neolink.Recording;

/// <summary>One detection event: what was seen, when, and what was captured.</summary>
public sealed class EventRecord
{
    public required string Id { get; init; }
    public required string Camera { get; init; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>Normalized labels: "person", "vehicle", "animal", "motion".</summary>
    public List<string> Labels { get; set; } = new();
    /// <summary>Set once the user has looked at the event (dismissed from the review bar).</summary>
    public bool Reviewed { get; set; }
    /// <summary>Still being extended by ongoing detections.</summary>
    public bool Ongoing { get; set; }
    public bool HasClip { get; set; }
    public bool HasThumb { get; set; }
    /// <summary>A low-res sub-stream twin of the clip exists (preview.mp4), for strip previews.</summary>
    public bool HasPreview { get; set; }
}

/// <summary>
/// File-based event storage — deliberately no database, keeping the zero-dependency
/// rule. Layout: {root}/{camera}/{yyyy-MM-dd}/{HHmmss}-{suffix}/ holding event.json,
/// clip.mp4 and thumb.jpg. The day directory is the retention unit: expiring events
/// means deleting old day directories. An in-memory index (loaded by scanning at
/// startup) serves queries; JSON files are the source of truth across restarts.
/// </summary>
public sealed class EventStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, (EventRecord Record, string Dir)> _byId = new();

    public EventStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Scans the storage directory and rebuilds the in-memory index.</summary>
    public void Load()
    {
        int loaded = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "event.json", SearchOption.AllDirectories))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<EventRecord>(File.ReadAllText(file), JsonOpts);
                if (rec == null) continue;
                rec.Ongoing = false; // whatever was ongoing did not survive the restart
                lock (_gate) { _byId[rec.Id] = (rec, Path.GetDirectoryName(file)!); }
                loaded++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Events: skipping unreadable {file}: {ex.Message}");
            }
        }
        if (loaded > 0)
            Log.Info($"Events: indexed {loaded} stored event(s) under {_root}");
    }

    /// <summary>Creates a new event and its directory, and persists the initial record.</summary>
    public EventRecord Create(string camera, DateTime startUtc, IEnumerable<string> labels)
    {
        var local = startUtc.ToLocalTime();
        var suffix = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..2]).ToLowerInvariant();
        var folder = $"{local:HHmmss}-{suffix}";
        var id = $"{camera}~{local:yyyy-MM-dd}~{folder}";
        var dir = Path.Combine(_root, SafeName(camera), $"{local:yyyy-MM-dd}", folder);
        Directory.CreateDirectory(dir);

        var rec = new EventRecord
        {
            Id = id,
            Camera = camera,
            StartUtc = startUtc,
            EndUtc = startUtc,
            Labels = labels.Distinct().ToList(),
            Ongoing = true,
        };
        lock (_gate) { _byId[id] = (rec, dir); }
        Save(rec);
        return rec;
    }

    /// <summary>Persists the record (atomic replace so a crash can't corrupt it).</summary>
    public void Save(EventRecord rec)
    {
        string dir;
        lock (_gate)
        {
            if (!_byId.TryGetValue(rec.Id, out var entry)) return; // expired mid-write
            dir = entry.Dir;
        }
        try
        {
            var tmp = Path.Combine(dir, "event.json.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec, JsonOpts));
            File.Move(tmp, Path.Combine(dir, "event.json"), overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"Events: cannot persist {rec.Id}: {ex.Message}");
        }
    }

    public string EventDir(EventRecord rec)
    {
        lock (_gate) { return _byId[rec.Id].Dir; }
    }

    public EventRecord? Find(string id)
    {
        lock (_gate) { return _byId.TryGetValue(id, out var e) ? e.Record : null; }
    }

    /// <summary>Absolute path of a stored artifact ("clip.mp4"/"thumb.jpg"), or null.</summary>
    public string? ArtifactPath(string id, string fileName)
    {
        string? dir;
        lock (_gate) { dir = _byId.TryGetValue(id, out var e) ? e.Dir : null; }
        if (dir == null) return null;
        var path = Path.Combine(dir, fileName);
        return File.Exists(path) ? path : null;
    }

    public bool SetReviewed(string id, bool reviewed)
    {
        EventRecord? rec;
        lock (_gate) { rec = _byId.TryGetValue(id, out var e) ? e.Record : null; }
        if (rec == null) return false;
        rec.Reviewed = reviewed;
        Save(rec);
        return true;
    }

    /// <summary>Newest-first event listing with optional filters.</summary>
    public List<EventRecord> List(string? camera = null, bool? reviewed = null, int limit = 200)
    {
        lock (_gate)
        {
            return _byId.Values.Select(e => e.Record)
                .Where(r => camera == null || string.Equals(r.Camera, camera, StringComparison.OrdinalIgnoreCase))
                .Where(r => reviewed == null || r.Reviewed == reviewed)
                .OrderByDescending(r => r.StartUtc)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();
        }
    }

    // ------------------------------------------------------------------ continuous recordings

    /// <summary>Path for a new continuous-recording segment starting now (creates the day dir).</summary>
    public string NewSegmentPath(string camera, DateTime local)
    {
        var dir = Path.Combine(_root, SafeName(camera), "continuous", $"{local:yyyy-MM-dd}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{local:HH-mm-ss}.mp4");
    }

    /// <summary>Days with continuous footage for a camera, newest first ("yyyy-MM-dd").</summary>
    public List<string> ListContinuousDays(string camera)
    {
        var dir = Path.Combine(_root, SafeName(camera), "continuous");
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateDirectories(dir)
            .Select(d => Path.GetFileName(d)!)
            .Where(IsDayName)
            .OrderByDescending(d => d)
            .ToList();
    }

    /// <summary>Segment files of one day, oldest first: (file name, size in bytes).</summary>
    public List<(string File, long Size)> ListSegments(string camera, string date)
    {
        if (!IsDayName(date)) return new List<(string, long)>();
        var dir = Path.Combine(_root, SafeName(camera), "continuous", date);
        if (!Directory.Exists(dir)) return new List<(string, long)>();
        return Directory.EnumerateFiles(dir, "*.mp4")
            .Select(f => new FileInfo(f))
            .Where(f => IsSegmentName(f.Name))
            .OrderBy(f => f.Name)
            .Select(f => (f.Name, f.Length))
            .ToList();
    }

    /// <summary>Absolute path of one segment; strict name validation (no traversal).</summary>
    public string? SegmentPath(string camera, string date, string file)
    {
        if (!IsDayName(date) || !IsSegmentName(file)) return null;
        var path = Path.Combine(_root, SafeName(camera), "continuous", date, file);
        return File.Exists(path) ? path : null;
    }

    private static bool IsDayName(string s) =>
        s.Length == 10 && DateTime.TryParseExact(s, "yyyy-MM-dd",
            null, System.Globalization.DateTimeStyles.None, out _);

    private static bool IsSegmentName(string s) =>
        s.Length == 12 && s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && s[..8].Replace("-", "").All(char.IsAsciiDigit) && s[2] == '-' && s[5] == '-';

    // ------------------------------------------------------------------ retention

    /// <summary>Deletes event and continuous day directories older than their retention windows.</summary>
    public void Cleanup(int retentionDays, int continuousRetentionDays)
    {
        int events = 0, segments = 0;
        foreach (var cameraDir in Directory.EnumerateDirectories(_root))
        {
            if (retentionDays > 0)
                events += CleanupDayDirs(cameraDir, DateTime.Now.Date.AddDays(-retentionDays), dropIndex: true);

            var contDir = Path.Combine(cameraDir, "continuous");
            if (continuousRetentionDays > 0 && Directory.Exists(contDir))
                segments += CleanupDayDirs(contDir, DateTime.Now.Date.AddDays(-continuousRetentionDays), dropIndex: false);
        }
        if (events > 0)
            Log.Info($"Events: retention removed {events} expired day folder(s) (older than {retentionDays}d)");
        if (segments > 0)
            Log.Info($"Recordings: retention removed {segments} expired day folder(s) (older than {continuousRetentionDays}d)");
    }

    private int CleanupDayDirs(string parent, DateTime cutoff, bool dropIndex)
    {
        int removed = 0;
        foreach (var dayDir in Directory.EnumerateDirectories(parent))
        {
            // Non-date entries (e.g. the "continuous" folder itself) are skipped here.
            if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd",
                    null, System.Globalization.DateTimeStyles.None, out var day)
                || day >= cutoff)
                continue;
            try
            {
                Directory.Delete(dayDir, recursive: true);
                removed++;
            }
            catch (IOException ex)
            {
                Log.Warn($"Retention: cannot delete expired {dayDir}: {ex.Message}");
                continue;
            }
            if (dropIndex)
            {
                lock (_gate)
                {
                    foreach (var id in _byId.Where(kv => kv.Value.Dir.StartsWith(dayDir, StringComparison.OrdinalIgnoreCase))
                                 .Select(kv => kv.Key).ToList())
                        _byId.Remove(id);
                }
            }
        }
        return removed;
    }

    /// <summary>Runs retention cleanup once at start and then hourly.</summary>
    public async Task RunRetentionAsync(int retentionDays, int continuousRetentionDays, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Cleanup(retentionDays, continuousRetentionDays); }
            catch (Exception ex) { Log.Warn($"Events: retention pass failed: {Log.Flatten(ex)}"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
