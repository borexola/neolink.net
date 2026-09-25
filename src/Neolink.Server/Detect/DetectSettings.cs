// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Detect;

/// <summary>
/// Live object boxes: the browser outlines what it recognises on the camera you
/// are watching. One server-wide switch, because it is the browser that does the
/// work — there is nothing to turn on per camera, and a setting stored here is the
/// same on every device you sign in from. Boxes are drawn on the SINGLE-camera
/// view only (/cameras/{name}); a wall of tiles would mean one detector per tile.
/// </summary>
public sealed class DetectSettings
{
    public bool Enabled { get; set; }

    /// <summary>How sure the model has to be before a box is drawn, in percent.
    /// Below about 30 a driveway sprouts boxes for shrubs; above about 70 a person
    /// at the far end of it stops being outlined.</summary>
    public int MinConfidence { get; set; } = 45;

    /// <summary>Which groups of things get outlined (see <see cref="KnownGroups"/>);
    /// null = <see cref="DefaultGroups"/>. The model knows 80 everyday object
    /// classes, which is 77 more than a camera view has any use for.</summary>
    public List<string>? Groups { get; set; }

    /// <summary>Frames put through the model per second. The tile keeps playing at
    /// its own frame rate either way — this is only how often the boxes are
    /// recomputed, and it is the whole cost of the feature.</summary>
    public int Fps { get; set; } = 5;

    /// <summary>Keep the larger model available (another 29 MB here). Not a promise
    /// that it is used: it needs a working GPU in the browser, and a device without
    /// one quietly stays on the small model.</summary>
    public bool Detailed { get; set; }

    public static readonly string[] KnownGroups = { "people", "vehicles", "animals", "other" };

    public static readonly string[] DefaultGroups = { "people", "vehicles", "animals" };

    public IReadOnlyList<string> EffectiveGroups =>
        Groups is { Count: > 0 } g ? g : DefaultGroups;

    public DetectSettings Clone() => new()
    {
        Enabled = Enabled,
        MinConfidence = MinConfidence,
        Groups = Groups?.ToList(),
        Fps = Fps,
        Detailed = Detailed,
    };

    /// <summary>Clamps whatever arrived from the API into the ranges the page can
    /// actually work with, and drops group names the client invented.</summary>
    public DetectSettings Sanitized()
    {
        var copy = Clone();
        copy.MinConfidence = Math.Clamp(copy.MinConfidence, 10, 90);
        copy.Fps = Math.Clamp(copy.Fps, 1, 15);
        copy.Groups = copy.Groups?.Where(KnownGroups.Contains).Distinct().ToList();
        return copy;
    }
}

/// <summary>detect.json next to the other state files; unreadable = switched off.</summary>
public sealed class DetectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _file;
    private readonly object _gate = new();
    private DetectSettings _settings = new();

    public DetectStore(string stateDir)
    {
        _file = Path.Combine(stateDir, "detect.json");
        try
        {
            if (File.Exists(_file))
                _settings = JsonSerializer.Deserialize<DetectSettings>(File.ReadAllText(_file), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            _settings = new();
            Log.Warn($"Live object boxes settings unreadable ({ex.Message}); starting switched off.");
        }
    }

    public DetectSettings Snapshot()
    {
        lock (_gate) return _settings.Clone();
    }

    public void Save(DetectSettings incoming)
    {
        lock (_gate)
        {
            _settings = incoming.Sanitized();
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, JsonOpts));
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Live object boxes settings could not be saved: {ex.Message}");
            }
        }
    }
}
