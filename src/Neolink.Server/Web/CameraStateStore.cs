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

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsDefault => !Suspended && AiTypes == null && Doorbell == null;
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
                    return new Dictionary<string, CameraState>(parsed, StringComparer.OrdinalIgnoreCase);
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

    private void Save()
    {
        // Atomic replace, like the other state files: a crash mid-write must not
        // leave a truncated JSON behind.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
