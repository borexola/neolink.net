using System.Text.Json;

namespace Neolink.Recording;

/// <summary>
/// One camera's runtime recording switches. Immutable — readers always see a
/// consistent snapshot. Retention overrides are per recording type: null = use
/// the server-wide default, 0 = keep forever, otherwise days. RecordStream picks
/// which stream is taped ("mainStream"/"subStream"/"externStream"; null = the
/// server default from the recording config).
/// </summary>
public sealed record CameraRecordingSettings(bool Events, bool Continuous, List<string>? EventTypes,
    int? EventRetentionDays = null, int? ContinuousRetentionDays = null,
    string? RecordStream = null)
{
    /// <summary>Known detection labels (what the UI offers as event-type filters).</summary>
    public static readonly string[] KnownLabels = { "person", "vehicle", "animal", "package", "motion" };

    /// <summary>A null EventTypes list means every detection type is recorded.</summary>
    public bool AllowsLabel(string label) => EventTypes == null || EventTypes.Contains(label);
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
        string? recordStream = null, bool setRecordStream = false)
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
                setRecordStream ? recordStream : cur.RecordStream);
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
