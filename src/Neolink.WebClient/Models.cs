using System.Text.Json;

namespace Neolink.WebClient;

/// <summary>One stream of a camera as reported by the Neolink.Server web API.</summary>
public sealed record ApiStream(string Kind, string Path, bool Ready, string? Codec, uint Width, uint Height, int RtspPort)
{
    public string Label => Kind switch
    {
        "mainStream" => "Main",
        "subStream" => "Sub",
        "externStream" => "Ext",
        _ => Kind,
    };
}

public sealed record ApiCamera(string Name, bool Online, List<ApiStream> Streams);

// ------------------------------------------------------------ camera control API

public sealed record ApiVersion(string? Name, string? Model, string? Serial, string? Firmware,
    string? Hardware, string? Build);

public sealed record ApiFeatures(bool Ptz, bool Led, bool Pir, bool Battery,
    bool StreamSettings = false);

/// <summary>GET /api/cameras/{name}/capabilities — discovered device info and features.</summary>
public sealed record ApiCapabilities(bool Online, ApiVersion? Version, ApiFeatures? Features, JsonElement Support);

public sealed record ApiEncodeProfile(string Type, uint Width, uint Height,
    uint DefaultFramerate, uint DefaultBitrate, List<uint> Framerates, List<uint> Bitrates)
{
    public string Label => Type switch
    {
        "mainStream" => "Main",
        "subStream" => "Sub",
        "externStream" => "Ext",
        _ => Type,
    };
}

/// <summary>GET /api/cameras/{name}/streaminfo — the encode profiles of each stream.</summary>
public sealed record ApiStreamProfiles(List<ApiEncodeProfile> Profiles);

/// <summary>Server capability flags (GET /api/features).</summary>
public sealed record ApiFeaturesInfo(bool Events, bool Continuous);

/// <summary>GET /api/auth/status — whether/how the UI must authenticate.</summary>
public sealed record ApiAuthStatus(bool Enabled, bool SetupRequired, bool ResetAvailable,
    string? User, bool Admin);

/// <summary>POST /api/auth/login|setup reply.</summary>
public sealed record ApiAuthToken(string Token, string User, bool Admin);

/// <summary>GET /api/users — one account row.</summary>
public sealed record ApiUserInfo(string Name, bool Admin);

/// <summary>GET/POST /api/cameras/{name}/recording — runtime recording switches.</summary>
public sealed record ApiRecordingSettings(bool Events, bool Continuous,
    List<string>? EventTypes, List<string>? KnownTypes, bool ContinuousAvailable = false)
{
    /// <summary>null EventTypes = every detection type is recorded.</summary>
    public bool TypeEnabled(string label) => EventTypes == null || EventTypes.Contains(label);
}

/// <summary>GET /api/recordings/{camera}/{date} — one continuous-recording segment.</summary>
public sealed record ApiSegment(string File, long Size)
{
    /// <summary>"HH-mm-ss.mp4" → "HH:mm:ss".</summary>
    public string TimeLabel => File.Length >= 8 ? File[..8].Replace('-', ':') : File;
    public string SizeLabel => Size >= 1024 * 1024
        ? $"{Size / (1024.0 * 1024):0} MB"
        : $"{Size / 1024.0:0} KB";
}

// ------------------------------------------------------------ recorded events

/// <summary>GET /api/events — one recorded detection event.</summary>
public sealed record ApiEvent(string Id, string Camera, DateTime Start, DateTime End,
    List<string> Labels, bool Reviewed, bool Ongoing, bool HasClip, bool HasThumb)
{
    private static readonly (string Label, string Icon, string Name)[] Known =
    {
        ("person", "🧍", "Human"),
        ("vehicle", "🚗", "Vehicle"),
        ("animal", "🐾", "Animal"),
        ("package", "📦", "Package"),
        ("motion", "👁", "Motion"),
    };

    /// <summary>Leading icon: the most specific detection wins over plain motion.</summary>
    public string Icon =>
        Known.FirstOrDefault(k => Labels.Contains(k.Label)).Icon ?? "👁";

    /// <summary>"Human detected", "Human + Vehicle detected", ...</summary>
    public string Title
    {
        get
        {
            var names = Known.Where(k => Labels.Contains(k.Label)).Select(k => k.Name)
                .Concat(Labels.Where(l => Known.All(k => k.Label != l)).Select(Cap))
                .Distinct().ToList();
            if (names.Count == 0) names.Add("Motion");
            return string.Join(" + ", names) + " detected";
        }
    }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>One grid slot: which camera/stream is shown there (or empty).</summary>
public sealed class ViewSlot
{
    public string? Camera { get; set; }
    public string? Kind { get; set; }
    public string? Path { get; set; }

    public ViewSlot Clone() => new() { Camera = Camera, Kind = Kind, Path = Path };
    public void Clear() { Camera = null; Kind = null; Path = null; }
}

/// <summary>A snapshot of the view, used to restore after maximizing a tile.</summary>
public sealed class ViewSnapshot
{
    public string Mode { get; set; } = "grid";
    public int Count { get; set; } = 4;
    public List<ViewSlot> Slots { get; set; } = new();
}

/// <summary>Everything persisted to localStorage.</summary>
public sealed class PersistedView
{
    public string? Server { get; set; }
    /// <summary>Credentials for control actions (camera settings/PTZ/reboot), when the server has users configured.</summary>
    public string? User { get; set; }
    public string? Pass { get; set; }
    public string Mode { get; set; } = "grid";   // grid | focus | mosaic | theater | free
    public int Count { get; set; } = 4;
    public List<ViewSlot> Slots { get; set; } = new();
    /// <summary>Set while a tile is maximized: the view to restore. Survives reloads.</summary>
    public ViewSnapshot? Backup { get; set; }
    /// <summary>Review-strip filter: event types hidden from the top bar.</summary>
    public List<string> HiddenTypes { get; set; } = new();
    /// <summary>Review-strip filter: cameras hidden from the top bar.</summary>
    public List<string> HiddenCams { get; set; } = new();
    /// <summary>Height of the review strip in px (user-resizable by dragging its bottom edge).</summary>
    public int StripHeight { get; set; } = 160;
}
