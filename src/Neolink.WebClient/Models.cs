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

public sealed record ApiFeatures(bool Ptz, bool Led, bool Pir, bool Battery);

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
}
