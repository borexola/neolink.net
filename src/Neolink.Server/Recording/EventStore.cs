// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
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
    private readonly string _clipsRoot;   // where NEW event folders go (== _root unless tiered)
    private readonly string? _archiveRoot; // aged footage moves here instead of being deleted
    private readonly object _gate = new();
    private readonly Dictionary<string, (EventRecord Record, string Dir)> _byId = new();

    public EventStore(string root, string? clipsRoot = null, string? archiveRoot = null)
    {
        _root = Path.GetFullPath(root);
        _clipsRoot = clipsRoot == null ? _root : Path.GetFullPath(clipsRoot);
        _archiveRoot = archiveRoot == null ? null : Path.GetFullPath(archiveRoot);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_clipsRoot);
        if (_archiveRoot != null) Directory.CreateDirectory(_archiveRoot);
    }

    public string Root => _root;

    /// <summary>Every distinct root that can hold footage, main first (then the
    /// clips tier and the archive, when configured and distinct).</summary>
    private IEnumerable<string> Roots
    {
        get
        {
            yield return _root;
            if (!SamePath(_clipsRoot, _root)) yield return _clipsRoot;
            if (_archiveRoot != null && !SamePath(_archiveRoot, _root) && !SamePath(_archiveRoot, _clipsRoot))
                yield return _archiveRoot;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Scans every storage root and rebuilds the in-memory index.</summary>
    public void Load()
    {
        try { MigrateLegacyLayout(); }
        catch (Exception ex) { Log.Warn($"Recordings: layout migration pass failed: {ex.Message}"); }
        int loaded = 0;
        foreach (var root in Roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "event.json", SearchOption.AllDirectories))
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
        }
        if (loaded > 0)
            Log.Info($"Events: indexed {loaded} stored event(s) under {string.Join(" + ", Roots)}");
    }

    /// <summary>Creates a new event and its directory, and persists the initial record.</summary>
    public EventRecord Create(string camera, DateTime startUtc, IEnumerable<string> labels)
    {
        var local = startUtc.ToLocalTime();
        var suffix = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..2]).ToLowerInvariant();
        var folder = $"{local:HHmmss}-{suffix}";
        var id = $"{camera}~{local:yyyy-MM-dd}~{folder}";
        // New detections land on the clips tier (the fast/SSD path when configured).
        var dir = Path.Combine(_clipsRoot, SafeName(camera), $"{local:yyyy-MM-dd}", "detections", folder);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            // A dead/full clips volume must not kill the event: it still registers
            // (in memory, so the UI and HA see it) — only its artifacts are lost,
            // and every artifact write logs its own failure.
            Log.Error($"{camera}: cannot create event folder {dir}: {ex.Message}");
        }

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
        catch (Exception ex)
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

    /// <summary>The on-disk size of one event's stored artifacts (clip, thumb,
    /// preview, event.json) in bytes — 0 when the event is unknown or its folder
    /// is already gone. Used to tell the user how much a deletion will free.</summary>
    public long EventSize(string id)
    {
        string? dir;
        lock (_gate) { dir = _byId.TryGetValue(id, out var e) ? e.Dir : null; }
        return dir == null ? 0 : DirSize(dir);
    }

    /// <summary>
    /// Permanently deletes one event: its folder and every artifact under it, and
    /// its index entry. Refuses an event still being recorded (Ongoing) — deleting
    /// under the writer would race it. Empty parent day/camera folders are pruned
    /// so the tree does not accumulate hollow directories. Returns false when the
    /// event is unknown, ongoing, or the folder could not be removed (logged).
    /// </summary>
    public bool DeleteEvent(string id)
    {
        string dir;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var e)) return false;
            if (e.Record.Ongoing) return false; // still being written — never delete under the recorder
            dir = e.Dir;
        }
        if (!DeleteTree(dir)) return false;
        lock (_gate) { _byId.Remove(id); }
        // {root}/{camera}/{date}/detections/{event} → prune detections, then the day.
        try
        {
            var detections = Path.GetDirectoryName(dir);   // .../detections
            TryDeleteIfEmpty(detections!);
            var dayDir = Path.GetDirectoryName(detections!); // .../{date}
            TryDeleteIfEmpty(dayDir!);
        }
        catch { /* pruning is best-effort; the event itself is already gone */ }
        return true;
    }

    /// <summary>Newest-first event listing with optional filters. <paramref name="localDate"/> matches the server-local calendar day.</summary>
    public List<EventRecord> List(string? camera = null, bool? reviewed = null, int limit = 200,
        DateTime? localDate = null)
    {
        lock (_gate)
        {
            return _byId.Values.Select(e => e.Record)
                .Where(r => camera == null || string.Equals(r.Camera, camera, StringComparison.OrdinalIgnoreCase))
                .Where(r => reviewed == null || r.Reviewed == reviewed)
                .Where(r => localDate == null || r.StartUtc.ToLocalTime().Date == localDate.Value.Date)
                .OrderByDescending(r => r.StartUtc)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();
        }
    }

    /// <summary>
    /// Every local day ("yyyy-MM-dd") that holds any recorded content — detection
    /// events or continuous footage, for any camera — newest first. Feeds the
    /// timeline's calendar so days with footage can be highlighted at a glance.
    /// </summary>
    public List<string> ListContentDays()
    {
        var days = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var camDir in Directory.EnumerateDirectories(root))
                foreach (var dayDir in Directory.EnumerateDirectories(camDir))
                {
                    var name = Path.GetFileName(dayDir)!;
                    if (IsDayName(name)) days.Add(name);
                }
        }
        return days.OrderByDescending(d => d).ToList();
    }

    // ------------------------------------------------------------------ continuous recordings

    /// <summary>Path for a new continuous-recording segment starting now (creates the day dir).</summary>
    public string NewSegmentPath(string camera, DateTime local)
    {
        var dir = Path.Combine(_root, SafeName(camera), $"{local:yyyy-MM-dd}", "continuous");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{local:HH-mm-ss}.mp4");
    }

    /// <summary>The live root plus the archive (when configured): everywhere
    /// continuous footage can live. The timeline reads both transparently.</summary>
    private IEnumerable<string> ContinuousRoots
    {
        get
        {
            yield return _root;
            if (_archiveRoot != null && !SamePath(_archiveRoot, _root)) yield return _archiveRoot;
        }
    }

    /// <summary>Days with continuous footage for a camera (live or archived), newest first ("yyyy-MM-dd").</summary>
    public List<string> ListContinuousDays(string camera)
    {
        var days = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in ContinuousRoots)
        {
            var camDir = Path.Combine(root, SafeName(camera));
            if (!Directory.Exists(camDir)) continue;
            foreach (var d in Directory.EnumerateDirectories(camDir).Select(d => Path.GetFileName(d)!))
                if (IsDayName(d) && Directory.Exists(Path.Combine(camDir, d, "continuous")))
                    days.Add(d);
        }
        return days.OrderByDescending(d => d).ToList();
    }

    /// <summary>Segment files of one day (live and archived merged), oldest first:
    /// (file name, size in bytes, seconds of media). The duration comes from the
    /// file's mtime minus the start encoded in its name — free during enumeration,
    /// preserved by the archive mover, and it keeps growing for the file still
    /// being written. The timeline uses it to size coverage truthfully: a segment
    /// cut short (camera suspended/offline) must not claim minutes it doesn't
    /// have, or the cursor is sent past the end of the media.</summary>
    public List<(string File, long Size, double Seconds)> ListSegments(string camera, string date)
    {
        if (!IsDayName(date)) return new List<(string, long, double)>();
        bool haveDay = DateTime.TryParseExact(date, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out var day);
        var seen = new Dictionary<string, (long Size, double Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ContinuousRoots)
        {
            var dir = Path.Combine(root, SafeName(camera), date, "continuous");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.mp4").Select(f => new FileInfo(f)))
            {
                if (!IsSegmentName(f.Name) || seen.ContainsKey(f.Name)) continue;
                double seconds = 0;
                if (haveDay && TimeSpan.TryParseExact(Path.GetFileNameWithoutExtension(f.Name),
                        @"hh\-mm\-ss", null, out var start))
                    seconds = Math.Max(0, (f.LastWriteTime - (day + start)).TotalSeconds);
                seen[f.Name] = (f.Length, seconds);
            }
        }
        return seen.OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.Size, kv.Value.Seconds)).ToList();
    }

    /// <summary>Absolute path of one segment (live first, then archive); strict name validation (no traversal).</summary>
    public string? SegmentPath(string camera, string date, string file)
    {
        if (!IsDayName(date) || !IsSegmentName(file)) return null;
        foreach (var root in ContinuousRoots)
        {
            var path = Path.Combine(root, SafeName(camera), date, "continuous", file);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static bool IsDayName(string s) =>
        s.Length == 10 && DateTime.TryParseExact(s, "yyyy-MM-dd",
            null, System.Globalization.DateTimeStyles.None, out _);

    private static bool IsSegmentName(string s) =>
        s.Length == 12 && s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && s[..8].Replace("-", "").All(char.IsAsciiDigit) && s[2] == '-' && s[5] == '-';

    // ------------------------------------------------------------------ retention

    /// <summary>
    /// One camera's storage lifecycle. Archiving is the per-camera, per-type
    /// opt-in: with <paramref name="ArchiveEvents"/>/<paramref name="ArchiveContinuous"/>
    /// (and an archive root configured), footage whose live retention window
    /// (EventDays/ContinuousDays) expires is MOVED to the archive instead of
    /// deleted, then finally deleted from the archive after
    /// <paramref name="ArchiveDeleteDays"/> (0 = never). With both flags off,
    /// EventDays/ContinuousDays delete exactly as they always have.
    /// </summary>
    public sealed record CameraStoragePolicy(int EventDays, int ContinuousDays,
        bool ArchiveEvents = false, bool ArchiveContinuous = false, int ArchiveDeleteDays = 0);

    /// <summary>Same retention window for every camera (used by tests and simple hosts).</summary>
    public void Cleanup(int retentionDays, int continuousRetentionDays) =>
        Cleanup(_ => (retentionDays, continuousRetentionDays));

    /// <summary>Delete-only cleanup (no archiving) — the pre-archive signature.</summary>
    public void Cleanup(Func<string, (int EventDays, int ContinuousDays)> retentionFor) =>
        Cleanup(cam =>
        {
            var (e, c) = retentionFor(cam);
            return new CameraStoragePolicy(e, c);
        });

    /// <summary>
    /// Applies each camera's storage lifecycle: expired footage is deleted — or,
    /// for cameras with archiving enabled, moved to the archive tier and deleted
    /// from there once past the archive window. Detections and continuous footage
    /// expire independently; a date folder disappears once both halves are gone.
    /// <paramref name="policyFor"/> maps a camera storage-directory name to its policy.
    /// </summary>
    public void Cleanup(Func<string, CameraStoragePolicy> policyFor)
    {
        int events = 0, segments = 0, archivedHalves = 0, archiveDeleted = 0;

        // Measure the archive work up front (read-only pass over the same
        // conditions), so the admin's progress strip can show a real percentage
        // while folders move — a cross-volume archive of a day's footage is the
        // one retention step slow enough to watch. No archive work, no strip.
        var (planHalves, planBytes) = MeasureArchivePlan(policyFor);
        using var archTask = planHalves > 0
            ? BackgroundTasks.Begin("Archiving footage",
                $"moving {planHalves} folder(s), {SizeText(planBytes)}, to the archive", 0)
            : null;
        long movedBytes = 0;
        double lastPct = -1;
        string curItem = "";
        void OnArchiveBytes(long b)
        {
            movedBytes += b;
            double pct = planBytes > 0 ? Math.Min(100, movedBytes * 100.0 / planBytes) : 0;
            if (pct - lastPct >= 0.5) // throttle: one report per half-percent
            {
                lastPct = pct;
                archTask?.Report($"{curItem} — {SizeText(movedBytes)} of {SizeText(planBytes)}", pct);
            }
        }
        void Announce(string cam, string day)
        {
            curItem = $"{cam} · {day}";
            archTask?.Report($"{curItem} — {SizeText(movedBytes)} of {SizeText(planBytes)}");
        }

        // The live pass covers every root that holds fresh footage (main +
        // the clips tier when distinct); the archive root is handled after.
        foreach (var root in Roots.Where(r => _archiveRoot == null || !SamePath(r, _archiveRoot)))
        {
            foreach (var cameraDir in Directory.EnumerateDirectories(root))
            {
                var camName = Path.GetFileName(cameraDir)!;
                var policy = policyFor(camName);
                // Clamp to 100 years: an absurd day count (hand-edited settings/config)
                // would overflow AddDays and abort the whole pass for every camera.
                int retentionDays = Math.Min(policy.EventDays, 36500);
                int continuousRetentionDays = Math.Min(policy.ContinuousDays, 36500);
                // Archiving changes what happens at the SAME moment: when a
                // type's retention expires, its footage moves to the archive
                // instead of being deleted (retention 0 = forever = nothing
                // ever expires, so nothing archives either).
                bool archiveEvents = policy.ArchiveEvents && _archiveRoot != null;
                bool archiveCont = policy.ArchiveContinuous && _archiveRoot != null;

                var eventCutoff = DateTime.Now.Date.AddDays(-retentionDays);
                var contCutoff = DateTime.Now.Date.AddDays(-continuousRetentionDays);
                bool eventActs = retentionDays > 0;
                bool contActs = continuousRetentionDays > 0;

                foreach (var dayDir in Directory.EnumerateDirectories(cameraDir))
                {
                    var dayName = Path.GetFileName(dayDir)!;
                    if (!DateTime.TryParseExact(dayName, "yyyy-MM-dd",
                            null, System.Globalization.DateTimeStyles.None, out var day))
                        continue; // e.g. a pre-migration "continuous" tree, swept below
                    if (eventActs && day < eventCutoff)
                    {
                        if (archiveEvents)
                        {
                            Announce(camName, dayName);
                            archivedHalves += ArchiveEventEntries(dayDir, camName, dayName, OnArchiveBytes) ? 1 : 0;
                        }
                        else
                            events += DeleteEventEntries(dayDir);
                    }
                    if (contActs && day < contCutoff)
                    {
                        var contDir = Path.Combine(dayDir, "continuous");
                        if (archiveCont)
                        {
                            if (Directory.Exists(contDir)) Announce(camName, dayName);
                            if (Directory.Exists(contDir) && MoveTree(contDir,
                                    Path.Combine(_archiveRoot!, camName, dayName, "continuous"), OnArchiveBytes))
                                archivedHalves++;
                        }
                        else if (DeleteTree(contDir))
                        {
                            segments++;
                        }
                    }
                    TryDeleteIfEmpty(dayDir); // both halves gone: the day is done here
                }

                // Pre-migration leftovers ({camera}/continuous/{date}) — only present
                // when a migration pass could not complete; expire them all the same
                // (delete-only: anything this old predates archiving entirely).
                var legacyCont = Path.Combine(cameraDir, "continuous");
                if (!archiveCont && continuousRetentionDays > 0 && Directory.Exists(legacyCont))
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
        }

        archiveDeleted = CleanupArchive(policyFor);

        if (events > 0)
            Log.Info($"Events: retention removed {events} expired detection folder(s)");
        if (segments > 0)
            Log.Info($"Recordings: retention removed {segments} expired continuous day folder(s)");
        if (archivedHalves > 0)
            Log.Info($"Recordings: retention archived {archivedHalves} aged folder(s) to {_archiveRoot}");
        if (archiveDeleted > 0)
            Log.Info($"Recordings: archive retention removed {archiveDeleted} expired day folder(s)");
    }

    /// <summary>
    /// Read-only twin of the archive branches in <see cref="Cleanup"/>: counts the
    /// folder halves that WILL move to the archive this pass and sums their bytes,
    /// so the move loop can report a real percentage. Keep its conditions in
    /// lockstep with the archiving branches above.
    /// </summary>
    internal (int Halves, long Bytes) MeasureArchivePlan(Func<string, CameraStoragePolicy> policyFor)
    {
        if (_archiveRoot == null) return (0, 0);
        int halves = 0;
        long bytes = 0;
        try
        {
            foreach (var root in Roots.Where(r => !SamePath(r, _archiveRoot)))
            {
                foreach (var cameraDir in Directory.EnumerateDirectories(root))
                {
                    var policy = policyFor(Path.GetFileName(cameraDir)!);
                    int retentionDays = Math.Min(policy.EventDays, 36500);
                    int continuousRetentionDays = Math.Min(policy.ContinuousDays, 36500);
                    bool archiveEvents = policy.ArchiveEvents;
                    bool archiveCont = policy.ArchiveContinuous;
                    if (!archiveEvents && !archiveCont) continue;
                    var eventCutoff = DateTime.Now.Date.AddDays(-retentionDays);
                    var contCutoff = DateTime.Now.Date.AddDays(-continuousRetentionDays);

                    foreach (var dayDir in Directory.EnumerateDirectories(cameraDir))
                    {
                        if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd",
                                null, System.Globalization.DateTimeStyles.None, out var day))
                            continue;
                        if (archiveEvents && retentionDays > 0 && day < eventCutoff)
                        {
                            long b = Directory.EnumerateDirectories(dayDir)
                                .Where(e => !Path.GetFileName(e)!.Equals("continuous", StringComparison.OrdinalIgnoreCase))
                                .Sum(TreeSize);
                            if (b > 0 || Directory.EnumerateDirectories(dayDir)
                                    .Any(e => !Path.GetFileName(e)!.Equals("continuous", StringComparison.OrdinalIgnoreCase)))
                            {
                                halves++;
                                bytes += b;
                            }
                        }
                        var contDir = Path.Combine(dayDir, "continuous");
                        if (archiveCont && continuousRetentionDays > 0 && day < contCutoff
                            && Directory.Exists(contDir))
                        {
                            halves++;
                            bytes += TreeSize(contDir);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Archive: cannot measure the pending work: {ex.Message}");
        }
        return (halves, bytes);
    }

    /// <summary>"512 MB" / "2.4 GB" for the progress strip.</summary>
    internal static string SizeText(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes >> 20} MB"
        : $"{Math.Max(1, bytes >> 10)} KB";

    /// <summary>Deletes archived days that have outlived their camera's archive window.</summary>
    private int CleanupArchive(Func<string, CameraStoragePolicy> policyFor)
    {
        if (_archiveRoot == null || !Directory.Exists(_archiveRoot)) return 0;
        int removed = 0;
        foreach (var cameraDir in Directory.EnumerateDirectories(_archiveRoot))
        {
            var policy = policyFor(Path.GetFileName(cameraDir)!);
            // The archive window applies even if the user has since switched
            // archiving off — footage they chose to expire should still expire.
            int deleteDays = Math.Min(policy.ArchiveDeleteDays, 36500);
            if (deleteDays <= 0) continue; // keep forever
            var cutoff = DateTime.Now.Date.AddDays(-deleteDays);
            foreach (var dayDir in Directory.EnumerateDirectories(cameraDir))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd",
                        null, System.Globalization.DateTimeStyles.None, out var day) || day >= cutoff)
                    continue;
                DropIndexEntriesUnder(dayDir);
                if (DeleteTree(dayDir)) removed++;
            }
            TryDeleteIfEmpty(cameraDir);
        }
        return removed;
    }

    /// <summary>Moves a day's event storage into the archive, keeping the index
    /// pointed at the new location so archived events stay playable.</summary>
    private bool ArchiveEventEntries(string dayDir, string camName, string dayName, Action<long>? onBytes = null)
    {
        bool any = false;
        foreach (var entry in Directory.EnumerateDirectories(dayDir))
        {
            var half = Path.GetFileName(entry)!;
            if (half.Equals("continuous", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(_archiveRoot!, camName, dayName, half);
            if (!MoveTree(entry, target, onBytes)) continue;
            any = true;
            lock (_gate)
            {
                var srcPrefix = entry + Path.DirectorySeparatorChar;
                foreach (var id in _byId.Keys.ToList())
                {
                    var dir = _byId[id].Dir;
                    if (dir.Equals(entry, StringComparison.OrdinalIgnoreCase))
                        _byId[id] = (_byId[id].Record, target);
                    else if (dir.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase))
                        _byId[id] = (_byId[id].Record, Path.Combine(target, dir[srcPrefix.Length..]));
                }
            }
        }
        return any;
    }

    /// <summary>Forgets every indexed event living under a directory (used before
    /// an archive day is finally deleted).</summary>
    private void DropIndexEntriesUnder(string dir)
    {
        lock (_gate)
        {
            var prefix = dir + Path.DirectorySeparatorChar;
            foreach (var id in _byId
                         .Where(kv => kv.Value.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)
                                   || kv.Value.Dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key).ToList())
                _byId.Remove(id);
        }
    }

    /// <summary>
    /// Moves a directory tree, surviving both cross-volume moves (Directory.Move
    /// can't cross filesystems — files are moved one by one instead, and File.Move
    /// handles the copy+delete) and an existing target (contents merge). True when
    /// the source is gone afterwards.
    /// </summary>
    internal static bool MoveTree(string src, string dst, Action<long>? onBytes = null)
    {
        if (!Directory.Exists(src)) return false;
        try
        {
            if (!Directory.Exists(dst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try
                {
                    Directory.Move(src, dst); // same volume: instant rename
                    onBytes?.Invoke(TreeSize(dst));
                    return true;
                }
                catch (IOException) { /* cross-volume or racing writer: merge below */ }
            }
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.EnumerateDirectories(src))
                MoveTree(dir, Path.Combine(dst, Path.GetFileName(dir)!), onBytes);
            foreach (var file in Directory.EnumerateFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(file)!);
                long len = 0;
                try { len = new FileInfo(file).Length; } catch { }
                if (File.Exists(target)) File.Delete(file); // already archived earlier
                else File.Move(file, target);                // cross-volume safe
                onBytes?.Invoke(len); // per file: real progress on slow cross-volume copies
            }
            TryDeleteIfEmpty(src);
            return !Directory.Exists(src);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Archive: cannot move {src} -> {dst}: {ex.Message} (will retry next pass)");
            return false;
        }
    }

    /// <summary>Total bytes of all files under a directory (best effort — errors count 0).</summary>
    internal static long TreeSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
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

    /// <summary>Total size in bytes of every file under a directory (0 on error).</summary>
    private static long DirSize(string dir)
    {
        try
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
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
    public async Task RunRetentionAsync(Func<string, CameraStoragePolicy> policyFor, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Cleanup(policyFor); }
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
