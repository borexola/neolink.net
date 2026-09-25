// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Web;

/// <summary>
/// Per-camera runtime state the user toggles and that must survive a restart
/// (camera-state.json in the UI state directory). Today that is one flag: whether
/// the camera is SUSPENDED — Neolink holds no connection to it, so it can't be
/// viewed or recorded here, without editing the config or restarting. Nothing
/// here is secret, so it is plain JSON like settings.json.
/// </summary>
public sealed class CameraStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, CameraState> _state;

    public sealed class CameraState
    {
        public bool Suspended { get; set; }
        /// <summary>AI detection types the camera's per-type alarm probe last
        /// answered for (person/vehicle/dog_cat/package dialect) — cached so the
        /// settings dialog can filter its event-type chips IMMEDIATELY instead of
        /// showing everything and pruning when the live probe lands. Null = the
        /// camera was never probed. Refreshed on every successful probe; a
        /// firmware update or camera swap corrects it on the next panel open.</summary>
        public List<string>? AiTypes { get; set; }
        /// <summary>Whether the capability probe last saw a doorbell. Null = never probed.</summary>
        public bool? Doorbell { get; set; }

        /// <summary>Detection zones Neolink keeps on the camera's behalf, keyed by
        /// detection type ("md"). Only cameras that cannot store a zone THEMSELVES
        /// have entries here — a generic RTSP camera, or a Baichuan one whose
        /// firmware carries no grid. See <see cref="StoredZone"/>.</summary>
        public Dictionary<string, StoredZone>? Zones { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsDefault => !Suspended && AiTypes == null && Doorbell == null
                                 && Zones is not { Count: > 0 };
    }

    /// <summary>One locally-kept detection zone: Table is Cols*Rows characters, row
    /// by row from the top-left, '1' = watched and '0' = ignored — the same shape a
    /// camera reports, so everything downstream reads it identically.</summary>
    public sealed class StoredZone
    {
        public int Cols { get; set; }
        public int Rows { get; set; }
        public string Table { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWellFormed => Cols > 0 && Rows > 0 && Table is { } t && (long)t.Length == (long)Cols * Rows
                                    && t.All(ch => ch is '0' or '1');
    }

    public CameraStateStore(string stateDir)
    {
        _path = Path.Combine(stateDir, "camera-state.json");
        _state = Load(_path);
    }

    private static Dictionary<string, CameraState> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, CameraState>>(
                    File.ReadAllText(path), Json);
                if (parsed != null)
                {
                    // Detection-type keys are matched the same way camera names are:
                    // the JSON deserializer builds the nested maps case-sensitively,
                    // and a hand-edited "MD" must still find its grid. Tolerant of a hand-edited
                    // file throughout, because the alternative is resetting every camera's state.
                    var state = new Dictionary<string, CameraState>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, s) in parsed)
                    {
                        if (s == null || state.ContainsKey(name)) continue;
                        if (s.Zones != null)
                        {
                            var zones = new Dictionary<string, StoredZone>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (type, zone) in s.Zones)
                                if (zone != null && !zones.ContainsKey(type)) zones[type] = zone;
                            s.Zones = zones;
                        }
                        state[name] = s;
                    }
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt state file must not take the server down — cameras just
            // start un-suspended (the safe default: they stream and record).
            Log.Warn($"camera-state.json unreadable ({ex.Message}); camera runtime state reset");
        }
        return new Dictionary<string, CameraState>(StringComparer.OrdinalIgnoreCase);
    }

    public bool Suspended(string camera)
    {
        lock (_gate)
        {
            return _state.TryGetValue(camera, out var s) && s.Suspended;
        }
    }

    public void SetSuspended(string camera, bool suspended)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(camera, out var s))
                _state[camera] = s = new CameraState();
            s.Suspended = suspended;
            if (s.IsDefault) // keep the file minimal — only non-default state persists
                _state.Remove(camera);
            Save();
        }
    }

    /// <summary>The cached detection-capability signals for a camera (see
    /// <see cref="CameraState.AiTypes"/>); (null, null) when never probed.</summary>
    public (IReadOnlyList<string>? AiTypes, bool? Doorbell) DetectionCaps(string camera)
    {
        lock (_gate)
        {
            return _state.TryGetValue(camera, out var s) ? (s.AiTypes, s.Doorbell) : (null, null);
        }
    }

    /// <summary>Updates the cached signals from a live probe. A null argument means
    /// "that signal wasn't probed this time — keep what's cached". Only an actual
    /// change touches the disk (panels re-probe on every open).</summary>
    public void SetDetectionCaps(string camera, IReadOnlyList<string>? aiTypes = null, bool? doorbell = null)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(camera, out var s))
            {
                if (aiTypes == null && doorbell == null) return;
                _state[camera] = s = new CameraState();
            }
            bool changed = false;
            if (aiTypes != null && (s.AiTypes == null || !s.AiTypes.SequenceEqual(aiTypes, StringComparer.Ordinal)))
            {
                s.AiTypes = aiTypes.ToList();
                changed = true;
            }
            if (doorbell != null && s.Doorbell != doorbell)
            {
                s.Doorbell = doorbell;
                changed = true;
            }
            if (changed) Save();
        }
    }

    /// <summary>The grid to offer a camera that keeps no zone of its own. Cells are
    /// square-ish over the picture: the rows are fixed and the columns follow the
    /// stream's aspect, so a 16:9 camera gets 32x18 and a 4:3 one 24x18. An unknown
    /// shape (nothing streaming yet) assumes 16:9, which almost every camera is.
    /// The clamp keeps an ultra-wide panorama from producing a grid so fine that
    /// each cell is a few pixels.</summary>
    public static (int Cols, int Rows) DefaultZoneGrid(uint width, uint height)
    {
        const int rows = 18;
        if (width == 0 || height == 0) return (32, rows);
        var cols = (int)Math.Round(rows * (double)width / height);
        return (Math.Clamp(cols, 12, 48), rows);
    }

    /// <summary>Whether a zone for <paramref name="type"/> belongs to Neolink rather
    /// than to the camera. The whole safety of the feature is in this one predicate,
    /// so it is a pure function of three facts and is pinned by a test.
    ///
    /// <paramref name="cameraHoldsZone"/> is <see cref="ICameraControl.CameraHoldsZone"/>:
    /// only a definite FALSE — the camera provably has no grid — moves a zone here.
    /// Null ("not asked yet") and true both mean the camera owns it, so a read that
    /// merely failed can never migrate a Reolink camera's zone onto the server.
    /// Only the shared "md" grid is ever kept locally, and only on a camera that
    /// reports no per-type grids of its own.</summary>
    public static bool ZoneIsLocal(bool? cameraHoldsZone, string type, int zoneTypeCount) =>
        type == "md" && cameraHoldsZone == false && zoneTypeCount == 1;

    /// <summary>The zone Neolink keeps for this camera and type, or null when it
    /// keeps none. A stored grid whose shape no longer adds up is treated as absent
    /// — a hand-edited file must not put a scrambled grid on screen.</summary>
    public StoredZone? Zone(string camera, string type)
    {
        lock (_gate)
        {
            if (_state.TryGetValue(camera, out var s) && s.Zones != null
                && s.Zones.TryGetValue(type, out var z) && z.IsWellFormed)
                return new StoredZone { Cols = z.Cols, Rows = z.Rows, Table = z.Table };
            return null;
        }
    }

    /// <summary>Stores a zone for a camera that cannot keep one itself. Throws on a
    /// grid that does not add up, so a malformed table can never reach the file.</summary>
    public void SetZone(string camera, string type, int cols, int rows, string table)
    {
        var zone = new StoredZone { Cols = cols, Rows = rows, Table = table };
        if (!zone.IsWellFormed)
            throw new ArgumentException($"table must be {cols}x{rows} = {cols * rows} cells of '0'/'1'");
        lock (_gate)
        {
            if (!_state.TryGetValue(camera, out var s))
                _state[camera] = s = new CameraState();
            s.Zones ??= new Dictionary<string, StoredZone>(StringComparer.OrdinalIgnoreCase);
            s.Zones[type] = zone;
            Save();
        }
    }

    /// <summary>Drops the zones kept for a camera that has been deleted, so a new
    /// camera given the same name does not inherit a grid drawn for another view.
    /// Only the zones: every other value is left exactly as it always was.</summary>
    public void Forget(string camera)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(camera, out var s) || s.Zones == null) return;
            s.Zones = null;
            if (s.IsDefault) _state.Remove(camera);
            Save();
        }
    }

    /// <summary>Follows a camera that was renamed in the web UI with the zones
    /// Neolink keeps for it — the grid someone drew is worth keeping. ONLY the
    /// zones move: the suspend flag and cached capabilities behave exactly as they
    /// always have on a rename (they stay under the old name), so a rename does
    /// nothing new to a camera that has no stored zone, which is every Reolink
    /// that keeps its own.</summary>
    public void Rename(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            if (!_state.TryGetValue(from, out var s) || s.Zones is not { Count: > 0 } zones) return;
            s.Zones = null;
            if (s.IsDefault) _state.Remove(from);
            if (!_state.TryGetValue(to, out var dest))
                _state[to] = dest = new CameraState();
            dest.Zones = zones;
            Save();
        }
    }

    private void Save()
    {
        // Atomic replace, like the other state files: a crash mid-write must not
        // leave a truncated JSON behind.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
