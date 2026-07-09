// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using Microsoft.AspNetCore.Components;

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

/// <summary>GET /api/system/stats — one camera's availability over the observed window.
/// Runs are run-length transitions: [startUnixMs, 1|0(online)].</summary>
public sealed record ApiCamAvail(string Cam, bool On, double Pct, long Obs,
    int Outs, long Longest, long Since, List<long[]> Runs);

public sealed record ApiSystemStats(ApiSystemInfo? Info, List<ApiSystemSample> Samples,
    List<ApiCamAvail>? Avail = null);

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
        ("doorbell", "🔔", "Doorbell"),
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
            // A lone doorbell event is a button press, not a detection.
            if (names.Count == 1 && names[0] == "Doorbell") return "Doorbell pressed";
            return string.Join(" + ", names) + " detected";
        }
    }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// Flat, minimal inline-SVG icons (feather-style strokes, currentColor) used
/// across the UI instead of emoji/font glyphs, which render inconsistently
/// between platforms and clash with the theme.
/// </summary>
public static class UiIcon
{
    public static MarkupString Render(string name, int size = 15)
    {
        var body = name switch
        {
            "gear" => "<path d=\"M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>",
            "refresh" => "<polyline points=\"23 4 23 10 17 10\"/><path d=\"M20.49 15a9 9 0 1 1-2.12-9.36L23 10\"/>",
            "menu" => "<line x1=\"3\" y1=\"6\" x2=\"21\" y2=\"6\"/><line x1=\"3\" y1=\"12\" x2=\"21\" y2=\"12\"/><line x1=\"3\" y1=\"18\" x2=\"21\" y2=\"18\"/>",
            "layout" => "<rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" rx=\"2\"/><line x1=\"3\" y1=\"9\" x2=\"21\" y2=\"9\"/><line x1=\"9\" y1=\"21\" x2=\"9\" y2=\"9\"/>",
            "clock" => "<circle cx=\"12\" cy=\"12\" r=\"10\"/><polyline points=\"12 6 12 12 16 14\"/>",
            "film" => "<rect x=\"2\" y=\"2\" width=\"20\" height=\"20\" rx=\"2.18\"/><line x1=\"7\" y1=\"2\" x2=\"7\" y2=\"22\"/><line x1=\"17\" y1=\"2\" x2=\"17\" y2=\"22\"/><line x1=\"2\" y1=\"12\" x2=\"22\" y2=\"12\"/><line x1=\"2\" y1=\"7\" x2=\"7\" y2=\"7\"/><line x1=\"2\" y1=\"17\" x2=\"7\" y2=\"17\"/><line x1=\"17\" y1=\"17\" x2=\"22\" y2=\"17\"/><line x1=\"17\" y1=\"7\" x2=\"22\" y2=\"7\"/>",
            "activity" => "<polyline points=\"22 12 18 12 15 21 9 3 6 12 2 12\"/>",
            "logs" => "<path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z\"/><polyline points=\"14 2 14 8 20 8\"/><line x1=\"16\" y1=\"13\" x2=\"8\" y2=\"13\"/><line x1=\"16\" y1=\"17\" x2=\"8\" y2=\"17\"/>",
            "expand" => "<polyline points=\"15 3 21 3 21 9\"/><polyline points=\"9 21 3 21 3 15\"/><line x1=\"21\" y1=\"3\" x2=\"14\" y2=\"10\"/><line x1=\"3\" y1=\"21\" x2=\"10\" y2=\"14\"/>",
            "collapse" => "<polyline points=\"4 14 10 14 10 20\"/><polyline points=\"20 10 14 10 14 4\"/><line x1=\"14\" y1=\"10\" x2=\"21\" y2=\"3\"/><line x1=\"3\" y1=\"21\" x2=\"10\" y2=\"14\"/>",
            "fs-enter" => "<path d=\"M8 3H5a2 2 0 0 0-2 2v3\"/><path d=\"M21 8V5a2 2 0 0 0-2-2h-3\"/><path d=\"M3 16v3a2 2 0 0 0 2 2h3\"/><path d=\"M16 21h3a2 2 0 0 0 2-2v-3\"/>",
            "fs-exit" => "<path d=\"M8 3v3a2 2 0 0 1-2 2H3\"/><path d=\"M21 8h-3a2 2 0 0 1-2-2V3\"/><path d=\"M3 16h3a2 2 0 0 1 2 2v3\"/><path d=\"M16 21v-3a2 2 0 0 1 2-2h3\"/>",
            "shield" => "<path d=\"M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z\"/>",
            "user" => "<path d=\"M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2\"/><circle cx=\"12\" cy=\"7\" r=\"4\"/>",
            "x" => "<line x1=\"18\" y1=\"6\" x2=\"6\" y2=\"18\"/><line x1=\"6\" y1=\"6\" x2=\"18\" y2=\"18\"/>",
            _ => "",
        };
        return new MarkupString(
            $"<svg class=\"nl-icon\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" " +
            "stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" " +
            $"aria-hidden=\"true\">{body}</svg>");
    }
}

/// <summary>One grid slot: which camera/stream is shown there (or empty).</summary>
public sealed class ViewSlot
{
    public string? Camera { get; set; }
    public string? Kind { get; set; }
    public string? Path { get; set; }
    /// <summary>Grid-cell span (grid mode only), user-resizable via the corner grip.</summary>
    public int Cols { get; set; } = 1;
    public int Rows { get; set; } = 1;

    public ViewSlot Clone() => new() { Camera = Camera, Kind = Kind, Path = Path, Cols = Cols, Rows = Rows };
    public void Clear() { Camera = null; Kind = null; Path = null; Cols = 1; Rows = 1; }
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
    /// <summary>Stream kind the maximize flow placed on the card (what restore undoes).</summary>
    public string? MaxSetKind { get; set; }
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
