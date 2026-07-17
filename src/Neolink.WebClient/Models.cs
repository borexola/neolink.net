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
/// <param name="OnDemand">On-demand clip capture state; null when the camera can't record events.</param>
public sealed record ApiCamera(string Name, bool Online, List<ApiStream> Streams, bool Recording = false,
    bool Asleep = false, ApiBattery? Battery = null, ApiOnDemand? OnDemand = null, bool Privacy = false,
    bool Suspended = false, bool CanSuspend = false, string? Address = null);

/// <summary>On-demand clip capture (the tile record button / HA Record switch):
/// one clip, stopped automatically at MaxSeconds.</summary>
public sealed record ApiOnDemand(bool Active, double RemainingSeconds, int MaxSeconds);

/// <summary>Battery reading of a battery-powered camera (null on mains-powered ones).</summary>
public sealed record ApiBattery(int Percent, bool Charging);

// ------------------------------------------------------------ camera control API

public sealed record ApiVersion(string? Name, string? Model, string? Serial, string? Firmware,
    string? Hardware, string? Build);

public sealed record ApiFeatures(bool Ptz, bool Led, bool Pir, bool Battery,
    bool StreamSettings = false, bool Reboot = true,
    bool Zoom = false, bool Siren = false, bool Floodlight = false, bool Privacy = false,
    bool WhiteLed = false, bool Spotlight = false, bool Doorbell = false);

/// <summary>GET/POST /api/cameras/{name}/whiteled — spotlight brightness (0-100),
/// on/off and auto mode, over the camera's HTTP API.</summary>
public sealed record ApiWhiteLed(int Bright, bool On, int Mode);

/// <summary>GET /api/cameras/{name}/httpfeatures — the HTTP-API extras (beta).
/// Null members = that feature is absent on this camera.</summary>
public sealed record ApiHttpFeatures(ApiImageSettings? Image, int? Volume, int? WifiSignal,
    List<ApiPtzPreset>? PtzPresets, List<ApiQuickReply>? QuickReplies, bool? AutoTrack,
    List<ApiSdCard>? SdCards);

/// <summary>Picture adjustments (0-255, 128 neutral) + ISP config; null = not reported.</summary>
public sealed record ApiImageSettings(int? Bright, int? Contrast, int? Saturation, int? Hue, int? Sharpen,
    string? DayNight, string? AntiFlicker, bool? Flip, bool? Mirror);

/// <summary>One PTZ preset slot; disabled slots are free for saving.</summary>
public sealed record ApiPtzPreset(int Id, string Name, bool Enabled);

/// <summary>One doorbell quick-reply audio file.</summary>
public sealed record ApiQuickReply(int Id, string Name);

/// <summary>One SD-card slot on the camera (sizes in MB).</summary>
public sealed record ApiSdCard(int Id, long TotalMb, long FreeMb, bool Formatted, bool Mounted);

/// <summary>GET /api/cameras/{name}/zoomfocus — absolute positions with their ranges.</summary>
public sealed record ApiZoomPos(long Cur, long Min, long Max);
public sealed record ApiZoomFocus(ApiZoomPos? Zoom, ApiZoomPos? Focus);

/// <summary>GET/POST /api/cameras/{name}/floodlight — the spotlight's behavior settings.</summary>
public sealed record ApiFloodlight(long Brightness, long BrightnessMin, long BrightnessMax, bool Auto);

/// <summary>GET /api/cameras/{name}/settings/stream — one stream's CURRENT encode selection.</summary>
public sealed record ApiStreamEnc(string Stream, uint Width, uint Height, uint Framerate, uint Bitrate);

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
    string? Version = null, string? LatestVersion = null, string? RepoUrl = null,
    ApiStorageState? Storage = null, bool Encrypted = false, bool ShowBackgroundTasks = true);

/// <summary>The worst storage tier's state (in /api/features); null = all healthy.</summary>
public sealed record ApiStorageState(string Label, double UsedPercent, bool Full);

/// <summary>GET /api/storage — one configured storage location and its capacity.
/// ForecastState is "measuring" | "steady" | "filling" (ForecastDays set only when
/// filling); both null when talking to an older server.</summary>
public sealed record ApiStorageLocation(string Role, string Label, string Path,
    long TotalBytes, long FreeBytes, double UsedPercent, bool Online, bool Warn, bool Full,
    string? ForecastState = null, double? ForecastDays = null);

/// <summary>GET /api/admin/config — the editable server settings, plus the live
/// footage-encryption key report (source, one-way fingerprint — never the key).</summary>
public sealed record ApiAdminConfig(string Path, bool Writable, JsonElement Settings,
    ApiKeyInfo? Encryption = null);

/// <summary>The running server's secret-key report: where the key comes from
/// ("env"/"file"/"ephemeral"), its SHA-256 fingerprint prefix, the file path when
/// file-based, and whether that file sits on the same disk as the footage.</summary>
public sealed record ApiKeyInfo(bool Enabled, string Source, string Fingerprint,
    string? File, bool OnFootageDisk);

/// <summary>GET /api/admin/cameras — configured cameras for the settings editor.
/// Passwords are never included (only HasPassword); RTSP URLs come masked.</summary>
public sealed record ApiAdminCamera(string Name, string Type, string? Address, string? Username,
    bool HasPassword, int ChannelId, string? HttpAddress, string? RtspMain, string? RtspSub);
public sealed record ApiAdminCameras(bool Writable, List<ApiAdminCamera> Cameras);

/// <summary>GET/PUT /api/admin/notifications — email alert settings. The SMTP
/// password is never returned (only HasPassword); it is sent write-only on PUT.</summary>
public sealed class ApiNotifications
{
    public bool Enabled { get; set; }
    public string Recipient { get; set; } = "";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string Security { get; set; } = "starttls";
    public string Username { get; set; } = "";
    public bool HasPassword { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Neolink.NET";
    public bool AlertStorage { get; set; } = true;
    public bool AlertOverload { get; set; }
    public bool AlertCameraOffline { get; set; }
    public bool AlertWriteFailure { get; set; }
    public int OfflineThresholdMinutes { get; set; } = 10;
    public Dictionary<string, int> CameraOfflineOverrides { get; set; } = new();
    public List<string> Cameras { get; set; } = new();
}

/// <summary>GET /api/auth/status — whether/how the UI must authenticate.</summary>
public sealed record ApiAuthStatus(bool Enabled, bool SetupRequired, bool ResetAvailable,
    string? User, bool Admin);

/// <summary>POST /api/auth/login|setup reply.</summary>
public sealed record ApiAuthToken(string Token, string User, bool Admin);

/// <summary>GET /api/users — one account row.</summary>
public sealed record ApiUserInfo(string Name, bool Admin);

/// <summary>GET/POST /api/cameras/{name}/recording — runtime recording switches.
/// The capture schedule arrives in effective form: ScheduleDays is the full day
/// list (never null in practice), Schedule times are "HH:mm" or "" (= midnight).
/// It only applies while ScheduleEnabled (opt-in; off = capture always).</summary>
public sealed record ApiRecordingSettings(bool Events, bool Continuous,
    List<string>? EventTypes, List<string>? KnownTypes, bool ContinuousAvailable = false,
    int? EventRetentionDays = null, int? ContinuousRetentionDays = null,
    int DefaultEventRetentionDays = 7, int DefaultContinuousRetentionDays = 7,
    bool EventsAvailable = true, string? RecordStream = null,
    string? DefaultRecordStream = null, List<string>? AvailableStreams = null,
    List<string>? ScheduleDays = null, string? ScheduleStart = null, string? ScheduleEnd = null,
    bool ScheduleEnabled = false,
    bool ArchiveAvailable = false, bool ArchiveEvents = false,
    bool ArchiveContinuous = false, int? ArchiveRetentionDays = null)
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

/// <summary>GET /api/recordings/{camera}/{date} — one continuous-recording segment.
/// Seconds = media length (0 from servers that predate it); the timeline sizes
/// coverage with it so a cut-short segment doesn't claim minutes it lacks.</summary>
public sealed record ApiSegment(string File, long Size, double Seconds = 0, bool Live = false)
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
        // Crying-sound detection (indoor cams listen through the mic)
        ("crying", "😢", "Crying"),
        // Perimeter protection (line/zone crossing configured in the Reolink app)
        ("line-crossing", "🚧", "Line crossing"),
        ("intrusion", "🚷", "Intrusion"),
        ("loitering", "🕒", "Loitering"),
        // Recording held open from outside (the Home Assistant "Record" switch).
        ("external", "⏺", "External"),
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
            // A lone external event was commanded (HA's Record switch), not detected.
            if (names.Count == 1 && names[0] == "External") return "Externally triggered";
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
            "lock" => "<rect x=\"3\" y=\"11\" width=\"18\" height=\"11\" rx=\"2\"/><path d=\"M7 11V7a5 5 0 0 1 10 0v4\"/>",
            "user" => "<path d=\"M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2\"/><circle cx=\"12\" cy=\"7\" r=\"4\"/>",
            "x" => "<line x1=\"18\" y1=\"6\" x2=\"6\" y2=\"18\"/><line x1=\"6\" y1=\"6\" x2=\"18\" y2=\"18\"/>",
            "mic" => "<path d=\"M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z\"/><path d=\"M19 10v2a7 7 0 0 1-14 0v-2\"/><line x1=\"12\" y1=\"19\" x2=\"12\" y2=\"23\"/><line x1=\"8\" y1=\"23\" x2=\"16\" y2=\"23\"/>",
            "battery" => "<rect x=\"1\" y=\"6\" width=\"18\" height=\"12\" rx=\"2\"/><line x1=\"23\" y1=\"11\" x2=\"23\" y2=\"13\"/>",
            "bolt" => "<polygon points=\"13 2 3 14 12 14 11 22 21 10 12 10 13 2\"/>",
            "moon" => "<path d=\"M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z\"/>",
            // Transport controls are FILLED so they read as playback, not navigation.
            "play" => "<polygon points=\"6 4 20 12 6 20 6 4\" fill=\"currentColor\"/>",
            "pause" => "<rect x=\"5\" y=\"4\" width=\"5\" height=\"16\" rx=\"1\" fill=\"currentColor\"/><rect x=\"14\" y=\"4\" width=\"5\" height=\"16\" rx=\"1\" fill=\"currentColor\"/>",
            "chev-left" => "<polyline points=\"15 18 9 12 15 6\"/>",
            "chev-right" => "<polyline points=\"9 18 15 12 9 6\"/>",
            "chev-down" => "<polyline points=\"6 9 12 15 18 9\"/>",
            "calendar" => "<rect x=\"3\" y=\"4\" width=\"18\" height=\"18\" rx=\"2\"/><line x1=\"16\" y1=\"2\" x2=\"16\" y2=\"6\"/><line x1=\"8\" y1=\"2\" x2=\"8\" y2=\"6\"/><line x1=\"3\" y1=\"10\" x2=\"21\" y2=\"10\"/>",
            "camera" => "<path d=\"M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z\"/><circle cx=\"12\" cy=\"13\" r=\"4\"/>",
            "download" => "<path d=\"M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4\"/><polyline points=\"7 10 12 15 17 10\"/><line x1=\"12\" y1=\"15\" x2=\"12\" y2=\"3\"/>",
            "check" => "<polyline points=\"20 6 9 17 4 12\"/>",
            "trash" => "<polyline points=\"3 6 5 6 21 6\"/><path d=\"M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2\"/><line x1=\"10\" y1=\"11\" x2=\"10\" y2=\"17\"/><line x1=\"14\" y1=\"11\" x2=\"14\" y2=\"17\"/>",
            "skip-back" => "<polygon points=\"19 20 9 12 19 4 19 20\" fill=\"currentColor\"/><line x1=\"5\" y1=\"19\" x2=\"5\" y2=\"5\"/>",
            "skip-fwd" => "<polygon points=\"5 4 15 12 5 20 5 4\" fill=\"currentColor\"/><line x1=\"19\" y1=\"5\" x2=\"19\" y2=\"19\"/>",
            "zoom" => "<circle cx=\"11\" cy=\"11\" r=\"7\"/><line x1=\"21\" y1=\"21\" x2=\"16\" y2=\"16\"/>",
            // Record: ring with a filled core — the classic ⏺ shape.
            "rec" => "<circle cx=\"12\" cy=\"12\" r=\"9\"/><circle cx=\"12\" cy=\"12\" r=\"4.5\" fill=\"currentColor\" stroke=\"none\"/>",
            // Camera-panel section markers.
            "info" => "<circle cx=\"12\" cy=\"12\" r=\"10\"/><line x1=\"12\" y1=\"16\" x2=\"12\" y2=\"12\"/><line x1=\"12\" y1=\"8\" x2=\"12.01\" y2=\"8\"/>",
            "move" => "<polyline points=\"5 9 2 12 5 15\"/><polyline points=\"9 5 12 2 15 5\"/><polyline points=\"15 19 12 22 9 19\"/><polyline points=\"19 9 22 12 19 15\"/><line x1=\"2\" y1=\"12\" x2=\"22\" y2=\"12\"/><line x1=\"12\" y1=\"2\" x2=\"12\" y2=\"22\"/>",
            "sun" => "<circle cx=\"12\" cy=\"12\" r=\"5\"/><line x1=\"12\" y1=\"1\" x2=\"12\" y2=\"3\"/><line x1=\"12\" y1=\"21\" x2=\"12\" y2=\"23\"/><line x1=\"4.22\" y1=\"4.22\" x2=\"5.64\" y2=\"5.64\"/><line x1=\"18.36\" y1=\"18.36\" x2=\"19.78\" y2=\"19.78\"/><line x1=\"1\" y1=\"12\" x2=\"3\" y2=\"12\"/><line x1=\"21\" y1=\"12\" x2=\"23\" y2=\"12\"/><line x1=\"4.22\" y1=\"19.78\" x2=\"5.64\" y2=\"18.36\"/><line x1=\"18.36\" y1=\"5.64\" x2=\"19.78\" y2=\"4.22\"/>",
            "siren" => "<polygon points=\"11 5 6 9 2 9 2 15 6 15 11 19 11 5\"/><path d=\"M15.54 8.46a5 5 0 0 1 0 7.07\"/><path d=\"M19.07 4.93a10 10 0 0 1 0 14.14\"/>",
            "eye-off" => "<path d=\"M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94\"/><path d=\"M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19\"/><line x1=\"1\" y1=\"1\" x2=\"23\" y2=\"23\"/>",
            "wrench" => "<path d=\"M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z\"/>",
            "archive" => "<polyline points=\"21 8 21 21 3 21 3 8\"/><rect x=\"1\" y=\"3\" width=\"22\" height=\"5\"/><line x1=\"10\" y1=\"12\" x2=\"14\" y2=\"12\"/>",
            "bell" => "<path d=\"M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9\"/><path d=\"M13.73 21a2 2 0 0 1-3.46 0\"/>",
            _ => "",
        };
        return new MarkupString(
            $"<svg class=\"nl-icon\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" " +
            "stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" " +
            $"aria-hidden=\"true\">{body}</svg>");
    }

    /// <summary>The battery outline with a fill bar matching the charge level, so
    /// the icon itself reads the percentage at a glance (13 = the inner width of
    /// the "battery" glyph's body minus padding).</summary>
    public static MarkupString RenderBattery(int percent, int size = 15)
    {
        double w = Math.Clamp(percent, 0, 100) / 100.0 * 13;
        var fill = w <= 0 ? "" :
            $"<rect x=\"3.5\" y=\"8.5\" width=\"{w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}\" " +
            "height=\"7\" rx=\"1\" fill=\"currentColor\" stroke=\"none\"/>";
        return new MarkupString(
            $"<svg class=\"nl-icon\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" " +
            "stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" " +
            "aria-hidden=\"true\"><rect x=\"1\" y=\"6\" width=\"18\" height=\"12\" rx=\"2\"/>" +
            $"<line x1=\"23\" y1=\"11\" x2=\"23\" y2=\"13\"/>{fill}</svg>");
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
    /// <summary>Sidebar camera order (names, first = top). Cameras not listed —
    /// new ones, typically — follow in server order. Empty = server order.</summary>
    public List<string> CamOrder { get; set; } = new();
    /// <summary>Name of the layout currently loaded, if any — drives the "update or save as new" choice.</summary>
    public string? ActiveLayout { get; set; }
}
