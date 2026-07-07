// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
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

/// <param name="Recording">24/7 footage is being written for this camera right now.</param>
public sealed record ApiCamera(string Name, bool Online, List<ApiStream> Streams, bool Recording = false);

// ------------------------------------------------------------ camera control API

public sealed record ApiVersion(string? Name, string? Model, string? Serial, string? Firmware,
    string? Hardware, string? Build);

public sealed record ApiFeatures(bool Ptz, bool Led, bool Pir, bool Battery,
    bool StreamSettings = false, bool Reboot = true);

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

/// <summary>
/// The server's own loopback API address, injected by the host. Blazor circuits
/// run SERVER-side: their HTTP calls must not go out through a reverse proxy's
/// public URL (TLS/hairpin problems) when the target is this very server — they
/// use this instead. Browser-side URLs (video WS, media elements) keep the page
/// origin so they DO flow through the proxy.
/// </summary>
public sealed record LocalApiInfo(string BaseUrl);

/// <summary>Server capability flags (GET /api/features).</summary>
public sealed record ApiFeaturesInfo(bool Events, bool Continuous, double TrickleSpeed = 4,
    string? Version = null, string? LatestVersion = null, string? RepoUrl = null);

/// <summary>GET /api/admin/config — the editable server settings.</summary>
public sealed record ApiAdminConfig(string Path, bool Writable, JsonElement Settings);

/// <summary>GET /api/auth/status — whether/how the UI must authenticate.</summary>
public sealed record ApiAuthStatus(bool Enabled, bool SetupRequired, bool ResetAvailable,
    string? User, bool Admin);

/// <summary>POST /api/auth/login|setup reply.</summary>
public sealed record ApiAuthToken(string Token, string User, bool Admin);

/// <summary>GET /api/users — one account row.</summary>
public sealed record ApiUserInfo(string Name, bool Admin);

/// <summary>GET/POST /api/cameras/{name}/recording — runtime recording switches.</summary>
public sealed record ApiRecordingSettings(bool Events, bool Continuous,
    List<string>? EventTypes, List<string>? KnownTypes, bool ContinuousAvailable = false,
    int? EventRetentionDays = null, int? ContinuousRetentionDays = null,
    int DefaultEventRetentionDays = 7, int DefaultContinuousRetentionDays = 7,
    bool EventsAvailable = true, string? RecordStream = null,
    string? DefaultRecordStream = null, List<string>? AvailableStreams = null)
{
    /// <summary>null EventTypes = every detection type is recorded.</summary>
    public bool TypeEnabled(string label) => EventTypes == null || EventTypes.Contains(label);
}

/// <summary>GET /api/system/stats — static facts about the server process/host.</summary>
public sealed record ApiSystemInfo(string? Os, string? Arch, string? Runtime, int Processors,
    long MachineMemory, long StartUtcMs, string? Disk, long SamplePeriodMs = 2000);

/// <summary>GET /api/system/stats — one resource sample (compact keys keep the 2s poll light).</summary>
public sealed record ApiSystemSample(long T, double Cpu, long Ws, long Heap, double Alloc,
    int Thr, int Fd, long DTot, long DFree, long Rec, int View,
    int RecCams = 0, double WMb = 0, long WFiles = 0);

public sealed record ApiSystemStats(ApiSystemInfo? Info, List<ApiSystemSample> Samples);

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
    List<string> Labels, bool Reviewed, bool Ongoing, bool HasClip, bool HasThumb,
    bool HasPreview = false)
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

/// <summary>
/// A named, user-saved layout: which cameras sit in which tiles (and, for the
/// freeform mode, the exact tile geometry). Loading one restores the view
/// exactly as it was saved.
/// </summary>
public sealed class SavedLayout
{
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "grid";
    public int Count { get; set; } = 4;
    public List<ViewSlot> Slots { get; set; } = new();
    /// <summary>Freeform tile positions/sizes (the JS "neolink.freegeo" JSON), when saved from free mode.</summary>
    public string? FreeGeometry { get; set; }
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
    /// <summary>Set while a tile is maximized: the view to restore. Survives reloads.
    /// Legacy (pre in-place maximize); still honored when loading old state.</summary>
    public ViewSnapshot? Backup { get; set; }
    /// <summary>Tile maximized in place, if any — the others stay live but hidden.</summary>
    public int? MaxIndex { get; set; }
    /// <summary>Stream the card showed before maximize auto-switched it to main.</summary>
    public string? MaxPrevKind { get; set; }
    public string? MaxPrevPath { get; set; }
    public string? MaxPrevCamera { get; set; }
    /// <summary>Review-strip filter: event types hidden from the top bar.</summary>
    public List<string> HiddenTypes { get; set; } = new();
    /// <summary>Review-strip filter: cameras hidden from the top bar.</summary>
    public List<string> HiddenCams { get; set; } = new();
    /// <summary>Height of the review strip in px (user-resizable by dragging its bottom edge).</summary>
    public int StripHeight { get; set; } = 160;
    /// <summary>Named layouts the user saved (per account when signed in).</summary>
    public List<SavedLayout> Layouts { get; set; } = new();
    /// <summary>Name of the layout currently loaded, if any — drives the "update or save as new" choice.</summary>
    public string? ActiveLayout { get; set; }
}
