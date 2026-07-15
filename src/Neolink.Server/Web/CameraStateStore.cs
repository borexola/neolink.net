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
            if (!s.Suspended) // keep the file minimal — only non-default state persists
                _state.Remove(camera);
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
