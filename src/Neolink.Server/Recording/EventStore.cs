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
/// rule. Layout: everything for one camera-day lives under one date folder:
///   {root}/{camera}/{yyyy-MM-dd}/detections/{HHmmss}-{suffix}/   event.json, clip.mp4, thumb.jpg, preview.mp4
///   {root}/{camera}/{yyyy-MM-dd}/continuous/{HH-mm-ss}.mp4       24/7 segment files
/// Retention works per day AND per type: an expired day loses its detections or
/// its continuous footage independently, and the date folder disappears once both
/// are gone. An in-memory index (loaded by scanning at startup) serves queries;
/// JSON files are the source of truth across restarts. Pre-existing storage in the
/// old layout ({camera}/{date}/{event} and {camera}/continuous/{date}) is migrated
/// by rename on load — instant even for terabytes, and idempotent.
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
        try { MigrateLegacyLayout(); }
        catch (Exception ex) { Log.Warn($"Recordings: layout migration pass failed: {ex.Message}"); }
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
        var dir = Path.Combine(_root, SafeName(camera), $"{local:yyyy-MM-dd}", "detections", folder);
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
        var dir = Path.Combine(_root, SafeName(camera), $"{local:yyyy-MM-dd}", "continuous");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{local:HH-mm-ss}.mp4");
    }

    /// <summary>Days with continuous footage for a camera, newest first ("yyyy-MM-dd").</summary>
    public List<string> ListContinuousDays(string camera)
    {
        var camDir = Path.Combine(_root, SafeName(camera));
        if (!Directory.Exists(camDir)) return new List<string>();
        return Directory.EnumerateDirectories(camDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(d => IsDayName(d) && Directory.Exists(Path.Combine(camDir, d, "continuous")))
            .OrderByDescending(d => d)
            .ToList();
    }

    /// <summary>Segment files of one day, oldest first: (file name, size in bytes).</summary>
    public List<(string File, long Size)> ListSegments(string camera, string date)
    {
        if (!IsDayName(date)) return new List<(string, long)>();
        var dir = Path.Combine(_root, SafeName(camera), date, "continuous");
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
        var path = Path.Combine(_root, SafeName(camera), date, "continuous", file);
        return File.Exists(path) ? path : null;
    }

    private static bool IsDayName(string s) =>
        s.Length == 10 && DateTime.TryParseExact(s, "yyyy-MM-dd",
            null, System.Globalization.DateTimeStyles.None, out _);

    private static bool IsSegmentName(string s) =>
        s.Length == 12 && s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && s[..8].Replace("-", "").All(char.IsAsciiDigit) && s[2] == '-' && s[5] == '-';

    // ------------------------------------------------------------------ retention

    /// <summary>Same retention window for every camera (used by tests and simple hosts).</summary>
    public void Cleanup(int retentionDays, int continuousRetentionDays) =>
        Cleanup(_ => (retentionDays, continuousRetentionDays));

    /// <summary>
    /// Deletes detections and continuous footage older than their retention windows.
    /// The two live side by side in each date folder, so expiry is per type; the
    /// date folder itself goes once both halves are gone.
    /// <paramref name="retentionFor"/> maps a camera storage-directory name to its
    /// (event days, continuous days) — 0 in either slot means keep forever.
    /// </summary>
    public void Cleanup(Func<string, (int EventDays, int ContinuousDays)> retentionFor)
    {
        int events = 0, segments = 0;
        foreach (var cameraDir in Directory.EnumerateDirectories(_root))
        {
            var (retentionDays, continuousRetentionDays) = retentionFor(Path.GetFileName(cameraDir)!);
            // Clamp to 100 years: an absurd day count (hand-edited settings/config)
            // would overflow AddDays and abort the whole pass for every camera.
            retentionDays = Math.Min(retentionDays, 36500);
            continuousRetentionDays = Math.Min(continuousRetentionDays, 36500);
            var eventCutoff = DateTime.Now.Date.AddDays(-retentionDays);
            var contCutoff = DateTime.Now.Date.AddDays(-continuousRetentionDays);

            foreach (var dayDir in Directory.EnumerateDirectories(cameraDir))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd",
                        null, System.Globalization.DateTimeStyles.None, out var day))
                    continue; // e.g. a pre-migration "continuous" tree, swept below
                if (retentionDays > 0 && day < eventCutoff)
                    events += DeleteEventEntries(dayDir);
                if (continuousRetentionDays > 0 && day < contCutoff
                    && DeleteTree(Path.Combine(dayDir, "continuous")))
                    segments++;
                TryDeleteIfEmpty(dayDir); // both halves expired: the day is gone
            }

            // Pre-migration leftovers ({camera}/continuous/{date}) — only present
            // when a migration pass could not complete; expire them all the same.
            var legacyCont = Path.Combine(cameraDir, "continuous");
            if (continuousRetentionDays > 0 && Directory.Exists(legacyCont))
            {
                foreach (var dateDir in Directory.EnumerateDirectories(legacyCont))
                {
                    if (DateTime.TryParseExact(Path.GetFileName(dateDir), "yyyy-MM-dd",
                            null, System.Globalization.DateTimeStyles.None, out var day)
                        && day < contCutoff && DeleteTree(dateDir))
                        segments++;
                }
                TryDeleteIfEmpty(legacyCont);
            }
        }
        if (events > 0)
            Log.Info($"Events: retention removed {events} expired detection folder(s)");
        if (segments > 0)
            Log.Info($"Recordings: retention removed {segments} expired continuous day folder(s)");
    }

    /// <summary>
    /// Deletes a day's event storage — the detections tree plus any pre-migration
    /// event folders (everything except "continuous") — dropping the index entries
    /// that pointed inside each successfully removed tree.
    /// </summary>
    private int DeleteEventEntries(string dayDir)
    {
        int removed = 0;
        foreach (var entry in Directory.EnumerateDirectories(dayDir))
        {
            if (Path.GetFileName(entry)!.Equals("continuous", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!DeleteTree(entry)) continue;
            removed++;
            lock (_gate)
            {
                var prefix = entry + Path.DirectorySeparatorChar;
                foreach (var id in _byId
                             .Where(kv => kv.Value.Dir.Equals(entry, StringComparison.OrdinalIgnoreCase)
                                       || kv.Value.Dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                             .Select(kv => kv.Key).ToList())
                    _byId.Remove(id);
            }
        }
        return removed;
    }

    /// <summary>Recursively deletes a directory; true only when it is actually gone.</summary>
    private static bool DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Retention: cannot delete expired {dir}: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* somebody is writing into it — fine, it is not empty then */ }
    }

    // ------------------------------------------------------------------ layout migration

    /// <summary>
    /// One-time move from the old layout to {camera}/{date}/{detections|continuous}:
    ///   {camera}/{date}/{HHmmss-x}/       → {camera}/{date}/detections/{HHmmss-x}/
    ///   {camera}/continuous/{date}/*.mp4  → {camera}/{date}/continuous/
    /// Directory renames on the same volume — instant regardless of footage size.
    /// Idempotent: runs at every load, retrying anything a previous pass missed.
    /// </summary>
    private void MigrateLegacyLayout()
    {
        int moved = 0;
        foreach (var cameraDir in Directory.EnumerateDirectories(_root))
        {
            // Continuous first, so its date folders exist before the event pass.
            var legacyCont = Path.Combine(cameraDir, "continuous");
            if (Directory.Exists(legacyCont))
            {
                foreach (var dateDir in Directory.EnumerateDirectories(legacyCont))
                {
                    var date = Path.GetFileName(dateDir)!;
                    if (!IsDayName(date)) continue;
                    var target = Path.Combine(cameraDir, date, "continuous");
                    try
                    {
                        if (!Directory.Exists(target))
                        {
                            Directory.CreateDirectory(Path.Combine(cameraDir, date));
                            Directory.Move(dateDir, target);
                            moved++;
                        }
                        else
                        {
                            // Both layouts hold footage for this day: merge file by file.
                            foreach (var file in Directory.EnumerateFiles(dateDir))
                            {
                                var dst = Path.Combine(target, Path.GetFileName(file));
                                if (!File.Exists(dst)) { File.Move(file, dst); moved++; }
                            }
                            TryDeleteIfEmpty(dateDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Recordings: cannot migrate {dateDir}: {ex.Message}");
                    }
                }
                TryDeleteIfEmpty(legacyCont);
            }

            foreach (var dayDir in Directory.EnumerateDirectories(cameraDir))
            {
                if (!IsDayName(Path.GetFileName(dayDir)!)) continue;
                foreach (var entry in Directory.EnumerateDirectories(dayDir))
                {
                    var name = Path.GetFileName(entry)!;
                    if (name is "detections" or "continuous") continue;
                    // Only recognizable event folders are touched; anything else stays put.
                    if (!File.Exists(Path.Combine(entry, "event.json"))) continue;
                    var target = Path.Combine(dayDir, "detections", name);
                    try
                    {
                        Directory.CreateDirectory(Path.Combine(dayDir, "detections"));
                        if (!Directory.Exists(target))
                        {
                            Directory.Move(entry, target);
                            moved++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Events: cannot migrate {entry}: {ex.Message}");
                    }
                }
            }
        }
        if (moved > 0)
            Log.Info($"Recordings: migrated {moved} folder(s)/file(s) to the " +
                     "{camera}/{date}/{detections|continuous} layout");
    }

    /// <summary>Runs retention cleanup once at start and then hourly.</summary>
    public async Task RunRetentionAsync(Func<string, (int EventDays, int ContinuousDays)> retentionFor,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Cleanup(retentionFor); }
            catch (Exception ex) { Log.Warn($"Events: retention pass failed: {Log.Flatten(ex)}"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Camera name → its storage-directory name (invalid filename chars replaced).</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
