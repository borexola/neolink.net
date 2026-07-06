using System.Text.Json;

namespace Neolink.Recording;

/// <summary>
/// One camera's runtime recording switches. Immutable — readers always see a
/// consistent snapshot.
/// </summary>
public sealed record CameraRecordingSettings(bool Events, bool Continuous, List<string>? EventTypes)
{
    /// <summary>Known detection labels (what the UI offers as event-type filters).</summary>
    public static readonly string[] KnownLabels = { "person", "vehicle", "animal", "package", "motion" };

    /// <summary>A null EventTypes list means every detection type is recorded.</summary>
    public bool AllowsLabel(string label) => EventTypes == null || EventTypes.Contains(label);
}

/// <summary>
/// Per-camera recording switches changeable at runtime from the web UI, persisted
/// as settings.json in the storage root — so they live on the same (Docker) volume
/// as the footage and survive restarts. The config file only provides each
/// camera's initial defaults; once a user flips a switch, this file wins.
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

    public RecordingSettings(string root)
    {
        _file = Path.Combine(root, "settings.json");
        try
        {
            if (File.Exists(_file))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CameraRecordingSettings>>(
                    File.ReadAllText(_file), JsonOpts);
                if (loaded != null)
                    _cameras = new Dictionary<string, CameraRecordingSettings>(loaded, StringComparer.OrdinalIgnoreCase);
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
    /// Applies a partial update (null = leave unchanged; for the type filter,
    /// <paramref name="setEventTypes"/> distinguishes "set" from "unchanged")
    /// and persists the result.
    /// </summary>
    public CameraRecordingSettings Update(string camera, bool? events, bool? continuous,
        List<string>? eventTypes, bool setEventTypes)
    {
        lock (_gate)
        {
            var cur = _cameras.TryGetValue(camera, out var s)
                ? s
                : new CameraRecordingSettings(Events: true, Continuous: false, EventTypes: null);
            var next = new CameraRecordingSettings(
                events ?? cur.Events,
                continuous ?? cur.Continuous,
                setEventTypes ? eventTypes : cur.EventTypes);
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
