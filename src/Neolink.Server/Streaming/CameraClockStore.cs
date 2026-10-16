// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Streaming;

/// <summary>camera-clock.json beside the other state files: the opt-in for
/// writing camera clocks. Missing or unreadable means off.</summary>
public sealed class CameraClockStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _file;
    private readonly object _gate = new();
    private volatile bool _enabled;

    public CameraClockStore(string stateDir)
    {
        _file = Path.Combine(stateDir, "camera-clock.json");
        try
        {
            if (File.Exists(_file))
                _enabled = JsonSerializer.Deserialize<State>(File.ReadAllText(_file), JsonOpts)?.Enabled ?? false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Camera clock setting unreadable ({ex.Message}); correction stays off.");
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_gate)
            {
                _enabled = value;
                try
                {
                    var tmp = _file + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(new State(value), JsonOpts));
                    File.Move(tmp, _file, overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Camera clock setting could not be saved: {ex.Message}");
                }
            }
        }
    }

    private sealed record State(bool Enabled);
}
