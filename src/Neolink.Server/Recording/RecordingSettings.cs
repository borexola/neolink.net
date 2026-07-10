// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Recording;

/// <summary>
/// One camera's runtime recording switches. Immutable — readers always see a
/// consistent snapshot. Retention overrides are per recording type: null = use
/// the server-wide default, 0 = keep forever, otherwise days. RecordStream picks
/// which stream is taped ("mainStream"/"subStream"/"externStream"; null = the
/// server default from the recording config). The capture schedule gates event
/// recording by local wall-clock time, but ONLY while ScheduleEnabled — the
/// explicit opt-in keeps "capture always" the safe default and lets a schedule
/// be switched off without losing it. ScheduleDays lists the enabled days
/// ("mon".."sun", null = every day) and ScheduleStart/ScheduleEnd bound the
/// time of day ("HH:mm"; null = midnight, so both null = all day).
/// </summary>
public sealed record CameraRecordingSettings(bool Events, bool Continuous, List<string>? EventTypes,
    int? EventRetentionDays = null, int? ContinuousRetentionDays = null,
    string? RecordStream = null,
    List<string>? ScheduleDays = null, string? ScheduleStart = null, string? ScheduleEnd = null,
    bool ScheduleEnabled = false)
{
    /// <summary>Known detection labels (what the UI offers as event-type filters).</summary>
    public static readonly string[] KnownLabels =
    {
        "person", "vehicle", "animal", "package", "doorbell",
        // Perimeter protection (line/zone crossing set up in the Reolink app):
        // record on these INSTEAD of the plain detections, no non-detection
        // zones needed — untick person/vehicle and keep these.
        "line-crossing", "intrusion", "loitering",
        "motion",
    };

    /// <summary>
    /// What records when the user never touched the filter (EventTypes null).
    /// The perimeter labels are OPT-IN: until ticked they'd only duplicate the
    /// plain detections (a crossing is also motion+person), so an untouched
    /// setup keeps recording exactly what it recorded before they existed.
    /// </summary>
    public static readonly string[] DefaultLabels =
        { "person", "vehicle", "animal", "package", "doorbell", "motion" };

    /// <summary>A null EventTypes list means the default set (perimeter labels are opt-in).</summary>
    public bool AllowsLabel(string label) =>
        EventTypes != null ? EventTypes.Contains(label) : DefaultLabels.Contains(label);

    /// <summary>Capture-schedule day tokens, in display order.</summary>
    public static readonly string[] WeekDays = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

    /// <summary>
    /// True when the capture schedule admits events at this LOCAL wall-clock
    /// instant; always true while the schedule is switched off (the default).
    /// Start > end wraps past midnight (22:00–06:00); the day check applies
    /// to the day the event actually occurs on, so the small hours of an
    /// overnight window belong to the following day. Start inclusive, end
    /// exclusive; a degenerate window (start == end) means all day.
    /// </summary>
    public bool ScheduleAllows(DateTime local)
    {
        if (!ScheduleEnabled) return true;
        if (ScheduleDays is { Count: > 0 } days && !days.Contains(DayToken(local.DayOfWeek)))
            return false;
        int s = ParseMinutes(ScheduleStart) ?? 0, e = ParseMinutes(ScheduleEnd) ?? 0;
        if (s == e) return true;
        int t = local.Hour * 60 + local.Minute;
        return s < e ? t >= s && t < e : t >= s || t < e;
    }

    public static string DayToken(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        _ => "sun",
    };

    /// <summary>"HH:mm" → minutes since midnight; null on anything else.</summary>
    public static int? ParseMinutes(string? hhmm) =>
        TimeOnly.TryParseExact(hhmm, "HH\\:mm", out var t) ? t.Hour * 60 + t.Minute : null;
}

/// <summary>
/// Per-camera recording switches changeable at runtime from the web UI, persisted
/// as settings.json NEXT TO THE CONFIG FILE (in Docker: the /config mount), so
/// static config and runtime settings live together. The config file only provides
/// each camera's initial defaults; once a user flips a switch, this file wins.
/// </summary>
public sealed class RecordingSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, CameraRecordingSettings> _cameras = new(StringComparer.OrdinalIgnoreCase);

    public RecordingSettings(string stateDir, params string?[] legacyDirs)
    {
        _file = Path.Combine(stateDir, "settings.json");
        try
        {
            // Older versions kept settings.json in the config dir or the recordings
            // root; a relocated state_dir migrates from whichever exists, once.
            var source = _file;
            foreach (var legacy in legacyDirs)
            {
                if (File.Exists(source) || legacy == null) break;
                var candidate = Path.Combine(legacy, "settings.json");
                if (File.Exists(candidate))
                {
                    source = candidate;
                    Log.Info($"Recording settings: migrating {source} -> {_file}");
                }
            }
            if (File.Exists(source))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CameraRecordingSettings>>(
                    File.ReadAllText(source), JsonOpts);
                if (loaded != null)
                    _cameras = new Dictionary<string, CameraRecordingSettings>(loaded, StringComparer.OrdinalIgnoreCase);
                if (!ReferenceEquals(source, _file) && source != _file)
                    lock (_gate) { SaveLocked(); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Recording settings unreadable ({ex.Message}); starting from config defaults");
        }
    }

    /// <summary>Registers a camera's config defaults without touching stored user choices.</summary>
    public void Seed(string camera, bool eventsDefault)
    {
        lock (_gate)
        {
            if (!_cameras.ContainsKey(camera))
                _cameras[camera] = new CameraRecordingSettings(eventsDefault, Continuous: false, EventTypes: null);
        }
    }

    public CameraRecordingSettings Get(string camera)
    {
        lock (_gate)
        {
            return _cameras.TryGetValue(camera, out var s)
                ? s
                : new CameraRecordingSettings(Events: true, Continuous: false, EventTypes: null);
        }
    }

    /// <summary>
    /// Applies a partial update (null = leave unchanged; for the type filter and
    /// the retention overrides, the matching set* flag distinguishes "set" — even
    /// to null, meaning back to the server default — from "unchanged") and
    /// persists the result.
    /// </summary>
    public CameraRecordingSettings Update(string camera, bool? events, bool? continuous,
        List<string>? eventTypes, bool setEventTypes,
        int? eventRetentionDays = null, bool setEventRetention = false,
        int? continuousRetentionDays = null, bool setContinuousRetention = false,
        string? recordStream = null, bool setRecordStream = false,
        List<string>? scheduleDays = null, bool setScheduleDays = false,
        string? scheduleStart = null, bool setScheduleStart = false,
        string? scheduleEnd = null, bool setScheduleEnd = false,
        bool? scheduleEnabled = null)
    {
        lock (_gate)
        {
            var cur = _cameras.TryGetValue(camera, out var s)
                ? s
                : new CameraRecordingSettings(Events: true, Continuous: false, EventTypes: null);
            var next = new CameraRecordingSettings(
                events ?? cur.Events,
                continuous ?? cur.Continuous,
                setEventTypes ? eventTypes : cur.EventTypes,
                setEventRetention ? eventRetentionDays : cur.EventRetentionDays,
                setContinuousRetention ? continuousRetentionDays : cur.ContinuousRetentionDays,
                setRecordStream ? recordStream : cur.RecordStream,
                setScheduleDays ? scheduleDays : cur.ScheduleDays,
                setScheduleStart ? scheduleStart : cur.ScheduleStart,
                setScheduleEnd ? scheduleEnd : cur.ScheduleEnd,
                scheduleEnabled ?? cur.ScheduleEnabled);
            _cameras[camera] = next;
            SaveLocked();
            return next;
        }
    }

    private void SaveLocked()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cameras, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"Cannot persist recording settings: {ex.Message}");
        }
    }
}
