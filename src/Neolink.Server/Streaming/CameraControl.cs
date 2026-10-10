// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Xml.Linq;
using Neolink.Bc;
using Neolink.Bc.Xml;
using Neolink.Media;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>Thrown when a control command is issued while the camera is disconnected.</summary>
public sealed class CameraOfflineException : Exception
{
    public CameraOfflineException(string name) : base($"Camera '{name}' is offline (reconnecting)") { }
}

/// <summary>Features a camera was found to support, discovered by probing.</summary>
/// <param name="SpotlightTasks">A spotlight camera whose brightness and auto mode answer over
/// Baichuan (FloodlightTask, msg 289) — the path for spotlights with no HTTP API.</param>
public sealed record CameraFeatures(bool Ptz, bool Led, bool Pir, bool Battery, bool Talk,
    bool Zoom = false, bool Siren = false, bool Floodlight = false, bool Privacy = false,
    bool WhiteLed = false, bool Spotlight = false, bool Doorbell = false, bool SpotlightTasks = false);

/// <summary>White-LED / spotlight state read over the HTTP API (brightness 0-100,
/// on/off, and the auto mode: 0 off, 1 night-auto, 2 always-on, 3 schedule).</summary>
public sealed record WhiteLedState(int Bright, bool On, int Mode);

/// <summary>One stream's current encode selection (Baichuan stream naming).</summary>
public sealed record StreamEncSetting(string Stream, uint Width, uint Height, uint Framerate, uint Bitrate);

/// <summary>One camera-side service from the live port table (Baichuan msg 37).
/// Service is "server" (Baichuan itself), "http", "https", "rtsp", "rtmp" or
/// "onvif"; Enabled is null when the firmware reports no enable flag for it
/// (it cannot be toggled — typically it is simply always on).</summary>
public sealed record ServicePortState(string Service, int? Port, bool? Enabled);

/// <summary>Picture settings read over the HTTP API. The five adjustments are
/// 0-255 (128 = neutral); a null field means the camera doesn't report it.
/// DayNight is "Auto"|"Color"|"Black&amp;White", AntiFlicker one of
/// <see cref="ImageSettings.AntiFlickerValues"/>. Hdr is the ISP hdr value on
/// firmwares that have one (0 = off; HdrMax says whether it's 2-state or 3-state).</summary>
public sealed record ImageSettings(int? Bright, int? Contrast, int? Saturation, int? Hue, int? Sharpen,
    string? DayNight, string? AntiFlicker, bool? Flip, bool? Mirror,
    int? Hdr = null, int? HdrMax = null)
{
    /// <summary>Every antiFlicker value seen across firmwares. Indoor models
    /// (E1 line) report and accept "Off"; outdoor models omit it.</summary>
    public static readonly string[] AntiFlickerValues = { "Off", "Outdoor", "50HZ", "60HZ" };
}

/// <summary>One saved PTZ preset slot (HTTP API). Disabled slots are free.</summary>
public sealed record PtzPresetInfo(int Id, string Name, bool Enabled);

/// <summary>One quick-reply audio file on a doorbell (HTTP API).</summary>
public sealed record QuickReplyFile(int Id, string Name);

/// <summary>The doorbell's auto-reply: the quick-reply file it plays by itself when
/// a ring goes unanswered (FileId -1 = off) and how many seconds it waits first.</summary>
public sealed record AutoReplyState(int FileId, int TimeoutSeconds);

/// <summary>One SD-card slot of the camera (HTTP API); sizes are megabytes.</summary>
public sealed record SdCardInfo(int Id, long TotalMb, long FreeMb, bool Formatted, bool Mounted);

/// <summary>One AI detection type's alarm tuning (HTTP API). Sensitivity is 0-100
/// (higher = more sensitive); StayTime — seconds a target must linger before the
/// alarm fires — is null when the firmware doesn't report one for the type.</summary>
public sealed record AiSensitivity(string Type, int Sensitivity, int? StayTime);

/// <summary>One detection type's zone grid (the HTTP API's "scope"): Table is
/// Cols*Rows characters, row by row from the top-left — '1' = the camera watches
/// that cell, '0' = detections there are ignored. Type is "md" (motion) or an
/// <see cref="CameraControl.AiAlarmTypes"/> entry.</summary>
public sealed record DetectionZone(string Type, int Cols, int Rows, string Table);

/// <summary>The on-screen-display overlay config (HTTP API): camera-name and
/// timestamp visibility + position and the Reolink watermark. PosOptions is the
/// firmware's own list of valid positions (empty when it didn't provide one).</summary>
public sealed record OsdSettings(bool ShowName, string? Name, string? NamePos,
    bool ShowTime, string? TimePos, bool? Watermark, IReadOnlyList<string> PosOptions);

/// <summary>A firmware-update check verdict (read-only — nothing is ever installed).</summary>
public sealed record FirmwareStatus(bool UpdateAvailable, string? NewVersion);

/// <summary>One recording stored on the camera's own SD card, as listed by the HTTP
/// API's Search. Times are camera-local; Name is the handle Download expects.</summary>
public sealed record SdRecording(string Name, DateTime Start, DateTime End, long SizeBytes, string StreamType);

public sealed record AutoRebootState(bool Enabled, string WeekDay, int Hour, int Minute);

/// <param name="Valid">A guard position has been saved.</param>
/// <param name="Timeout">Seconds idle before returning to it, as the camera reports it.</param>
public sealed record GuardState(bool Enabled, bool Valid, int? Timeout);

public sealed record PatrolInfo(int Id, string Name, bool Enabled);

public sealed record PrivacyMaskState(bool Enabled, int Count);

/// <param name="Volume">0-4.</param>
/// <param name="SilentSeconds">Time left silenced; null when the doorbell doesn't say.</param>
public sealed record ChimeInfo(int Id, string Name, bool Online, int? Volume, bool? Led, int? SilentSeconds = null);

/// <param name="Type">crossline, intrusion, loitering, object-left or object-taken.</param>
/// <param name="Seconds">stayTime (intrusion, loitering) or timeThresh (object rules).</param>
public sealed record SmartRule(string Type, int Index, string Name, string AiType, int? Sensitivity,
    int? Seconds, string? Direction);

/// <summary>Device settings read over Baichuan; a null member is absent on this camera.</summary>
public sealed record DeviceExtras(bool? SdRecording, AutoRebootState? AutoReboot, GuardState? Guard,
    IReadOnlyList<PatrolInfo>? Patrols, PrivacyMaskState? PrivacyMasks, IReadOnlyList<ChimeInfo>? Chimes,
    IReadOnlyList<SmartRule>? SmartRules, IReadOnlyList<SdCardInfo>? SdCards);

/// <summary>The scale a camera reported its Wi-Fi strength in.</summary>
public enum WifiUnit
{
    /// <summary>RSSI in dBm — always negative (Baichuan, and some HTTP firmwares).</summary>
    Dbm,
    /// <summary>Signal bars, 0-4 (what the Reolink HTTP API normally answers).</summary>
    Bars,
    /// <summary>A 0-100 signal-quality percentage (a few firmwares).</summary>
    Percent,
}

/// <summary>
/// One Wi-Fi reading, carrying the UNIT it was reported in. The unit is recorded
/// where the value is read and never re-inferred from its range afterwards: the
/// same integer means different things per source (a 3 is three bars from the
/// HTTP API but 3% from a percentage firmware, and a 5 is full signal on a 0-5
/// scale yet nearly nothing as a percentage), so range-guessing at render time
/// produced badly wrong icons — including full signal drawn as empty.
/// </summary>
/// <param name="Raw">The number the camera reported, unmodified.</param>
/// <param name="Unit">The scale <paramref name="Raw"/> is expressed in.</param>
public sealed record WifiReading(int Raw, WifiUnit Unit)
{
    /// <summary>The 0-4 level the icon draws. dBm bands follow Reolink's own
    /// wording (better than -60 excellent, -70 good, -80 fair, then poor) — the
    /// old bands were stricter and made every camera look worse than in the app.</summary>
    public int Level => Unit switch
    {
        WifiUnit.Dbm => Raw >= -60 ? 4 : Raw >= -70 ? 3 : Raw >= -80 ? 2 : Raw >= -88 ? 1 : 0,
        WifiUnit.Bars => Math.Clamp(Raw, 0, 4),
        _ => Raw >= 80 ? 4 : Raw >= 60 ? 3 : Raw >= 40 ? 2 : Raw >= 20 ? 1 : 0,
    };

    /// <summary>The reading spelled out for a tooltip, in its own unit.</summary>
    public string Label => Unit switch
    {
        WifiUnit.Dbm => $"{Raw} dBm",
        WifiUnit.Bars => $"{Math.Clamp(Raw, 0, 4)} / 4 bars",
        _ => $"{Raw}%",
    };

    /// <summary>A Baichuan reading — that protocol always answers in dBm.</summary>
    public static WifiReading FromDbm(int raw) => new(raw, WifiUnit.Dbm);

    /// <summary>Classifies a reading from the Reolink HTTP API, whose firmwares
    /// disagree: negative is RSSI, 0-4 is the documented bars scale, and anything
    /// above that can only sensibly be a percentage.</summary>
    public static WifiReading FromHttp(int raw) =>
        new(raw, raw < 0 ? WifiUnit.Dbm : raw <= 4 ? WifiUnit.Bars : WifiUnit.Percent);
}

/// <summary>Camera-side audio settings beyond the speaker volume — every member
/// is null when that particular camera doesn't expose it (they vary per model).
/// RecordAudio is the encode settings' audio flag: whether the microphone goes
/// into the streams (and thus recordings) at all. TalkVolume is AudioCfg's
/// "talkAndReplyVolume" (two-way talk and quick replies); VisitorVolume is
/// "visitorVolume" (a doorbell's voice prompts toward the visitor).</summary>
public sealed record AudioState(bool? RecordAudio, int? TalkVolume, int? VisitorVolume);

/// <summary>Everything readable over the camera's HTTP API in one round: a null
/// member means that feature is absent (or the camera rejected the query).</summary>
public sealed record HttpFeatures(ImageSettings? Image, int? Volume, WifiReading? WifiSignal,
    IReadOnlyList<PtzPresetInfo>? PtzPresets, IReadOnlyList<QuickReplyFile>? QuickReplies,
    bool? AutoTrack, IReadOnlyList<SdCardInfo>? SdCards,
    int? MdSensitivity = null, IReadOnlyList<AiSensitivity>? AiSensitivities = null,
    OsdSettings? Osd = null, AudioState? Audio = null);

/// <summary>Discovered camera capabilities: identity, advertised support flags, probed features.</summary>
/// <param name="Provisional">The camera has not answered yet: a consumer that settles on
/// its features once (the Home Assistant bridge) must not settle on these.</param>
public sealed record CameraCapabilities(VersionInfoXml? Version, XElement? Support, CameraFeatures Features,
    bool Provisional = false);

/// <summary>
/// The control surface of one camera, as consumed by the web API. Get/set XML
/// payloads are exposed raw (XElement); shaping them for clients is the API's job.
/// </summary>
public interface ICameraControl
{
    string CameraName { get; }
    bool Online { get; }

    /// <summary>Discovers (and caches per connection) what the camera can do.</summary>
    Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct);

    Task<StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct);

    /// <summary>Whether stream encode settings can be written (the camera's HTTP API is configured).</summary>
    bool CanSetStreamSettings { get; }

    /// <summary>Whether picture settings can be read/written over ONVIF as a fallback
    /// (for models with no Reolink HTTP CGI API). Defaults false for control surfaces
    /// that have no ONVIF path (generic RTSP cameras, test doubles).</summary>
    bool HasImagingFallback => false;

    /// <summary>Whether EVERY setting this camera offers comes from ONVIF — a
    /// non-Reolink camera. The standard covers less than Reolink's own API does, so
    /// the panel uses this to leave out controls ONVIF cannot honour (switching an
    /// overlay off rather than moving it) and to say where a setting is going.</summary>
    bool OnvifOnly => false;

    /// <summary>Whether the camera can be told to restart. A Baichuan camera always
    /// can; a non-Reolink one only through ONVIF.</summary>
    bool CanReboot => true;

    /// <summary>
    /// The CURRENT encode selection of each stream (what the Reolink app shows),
    /// read via the camera's HTTP API — null when no http_address is configured.
    /// </summary>
    Task<IReadOnlyList<StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct);

    /// <summary>
    /// Changes one stream's encode settings via the camera's Reolink HTTP API.
    /// The camera restarts the affected stream to apply them.
    /// </summary>
    Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
        uint? framerate, uint? bitrate, CancellationToken ct);

    Task<XElement?> GetBatteryInfoAsync(CancellationToken ct);

    /// <summary>A JPEG snapshot from the camera, or null if unsupported.</summary>
    Task<byte[]?> SnapshotAsync(CancellationToken ct);

    /// <summary>Whether the camera has a snapshot command of its own. False means
    /// the ONLY still it can have is one decoded from the stream it is sending —
    /// which is worth the decode. True means a failed snapshot is a camera that is
    /// offline, asleep or busy, and the answer to that is the same as it always was
    /// (serve the last frame, or say so), NOT an ffmpeg process per poll.</summary>
    bool HasSnapshot => true;

    /// <summary>A SMALL JPEG snapshot for size-limited consumers (the MQTT camera
    /// entity — brokers cap packet size and disconnect over it). Cameras with an
    /// HTTP API scale the image themselves; everything else falls back to the
    /// regular snapshot, so the caller must still bound the size.</summary>
    Task<byte[]?> SnapshotSmallAsync(CancellationToken ct) => SnapshotAsync(ct);
    Task<XElement?> GetLedStateAsync(CancellationToken ct);

    /// <summary>Read-modify-write of the LedState: null fields stay untouched.
    /// doorbellLightState and irBrightness only exist on cameras whose LedState
    /// reports them (doorbells; IR-brightness models like the Elite).</summary>
    Task SetLedStateAsync(string? state, string? lightState,
        string? doorbellLightState, int? irBrightness, CancellationToken ct);
    Task<XElement?> GetPirStateAsync(CancellationToken ct);
    Task SetPirEnabledAsync(bool enabled, CancellationToken ct);
    Task PtzAsync(string command, float speed, CancellationToken ct);

    /// <summary>Raised with the command ("up"… "stop", or "preset") after each PTZ command the camera
    /// accepted, whoever sent it.</summary>
    event Action<string>? PtzCommandSent { add { } remove { } }

    Task RebootAsync(CancellationToken ct);

    /// <summary>The camera's own service-port table — Baichuan, HTTP, HTTPS, RTSP,
    /// RTMP, ONVIF — asked of the camera LIVE (Baichuan msg 37) on every call.
    /// Deliberately never cached: the UI shows the camera's actual state, not a
    /// remembered one. Null when the camera has no Baichuan channel (generic
    /// RTSP) or doesn't answer the query.</summary>
    Task<IReadOnlyList<ServicePortState>?> GetServicePortsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ServicePortState>?>(null);

    /// <summary>Turns one camera-side service ("http", "https", "onvif", "rtsp",
    /// "rtmp") on or off via Baichuan msg 36 — the same read-modify-write the
    /// Reolink app's Port Settings screen performs. NEVER touches the Baichuan
    /// server port itself (port 9000 carries this very connection); that request
    /// is refused no matter who asks.</summary>
    Task SetServicePortEnabledAsync(string service, bool on, CancellationToken ct)
        => Task.FromException(new NotSupportedException("this camera has no Baichuan channel"));

    /// <summary>Raw &lt;PtzZoomFocus&gt; XML (zoom/focus positions + ranges), or null if unsupported.</summary>
    Task<XElement?> GetZoomFocusAsync(CancellationToken ct);

    /// <summary>Drives optical zoom ("zoomPos") or focus ("focusPos") to an absolute position.</summary>
    Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct);

    /// <summary>Latches the siren: true = sounds until stopped, false = stop. Null = one burst.</summary>
    Task SirenAsync(bool? on, CancellationToken ct);

    /// <summary>Whether privacy mode (camera dark, no video) is on; null when unsupported.</summary>
    Task<bool?> GetPrivacyModeAsync(CancellationToken ct);

    /// <summary>Turns privacy mode on or off.</summary>
    Task SetPrivacyModeAsync(bool on, CancellationToken ct);

    /// <summary>Raw &lt;FloodlightTask&gt; XML (brightness, auto mode, schedule), or null if unsupported.</summary>
    Task<XElement?> GetFloodlightTasksAsync(CancellationToken ct);

    /// <summary>Writes back a (modified) &lt;FloodlightTask&gt; from <see cref="GetFloodlightTasksAsync"/>.</summary>
    Task SetFloodlightTasksAsync(XElement task, CancellationToken ct);

    /// <summary>The white-LED / spotlight state over the HTTP API, or null when the
    /// camera has no white LED or its HTTP API is unreachable.</summary>
    Task<WhiteLedState?> GetWhiteLedAsync(CancellationToken ct);

    /// <summary>Sets the white-LED brightness (0-100), on/off and/or auto mode; a null
    /// field is left unchanged. Preserves the camera's schedule and AI-detect config.</summary>
    Task SetWhiteLedAsync(int? bright, bool? on, int? mode, CancellationToken ct);

    /// <summary>One combined read of every HTTP-API feature (picture, volume, Wi-Fi,
    /// presets, quick replies, auto-track, SD cards). Null when the camera has no
    /// HTTP API (or it is unreachable); individual members are null when absent.</summary>
    Task<HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct);

    /// <summary>Picture adjustments + ISP config over the HTTP API, or null.</summary>
    Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct);

    /// <summary>Writes the given picture/ISP fields; null fields stay untouched.</summary>
    Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue, int? sharpen,
        string? dayNight, string? antiFlicker, bool? flip, bool? mirror, CancellationToken ct);

    /// <summary>The camera's speaker volume (0-100) over the HTTP API, or null.</summary>
    Task<int?> GetVolumeAsync(CancellationToken ct);

    /// <summary>Sets the camera's speaker volume (0-100).</summary>
    Task SetVolumeAsync(int volume, CancellationToken ct);

    /// <summary>The audio settings beyond the speaker volume (record-audio flag,
    /// per-model extra volumes), or null when the camera has none. Default: none.</summary>
    Task<AudioState?> GetAudioStateAsync(CancellationToken ct) => Task.FromResult<AudioState?>(null);

    /// <summary>Camera-side record-audio switch (the encode settings' audio flag,
    /// set on every stream): whether the microphone goes into the streams and
    /// recordings at all. Default: not supported.</summary>
    Task SetRecordAudioAsync(bool on, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("this camera has no record-audio switch"));

    /// <summary>Writes the extra AudioCfg volumes some models expose (0-100 each;
    /// null = leave untouched): talkAndReplyVolume / visitorVolume. Default: not
    /// supported.</summary>
    Task SetAudioVolumesAsync(int? talkVolume, int? visitorVolume, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("this camera has no audio volume settings"));

    /// <summary>The camera's Wi-Fi signal reading with the unit it came in, or null.</summary>
    Task<WifiReading?> GetWifiSignalAsync(CancellationToken ct);

    /// <summary>The last Wi-Fi signal read, or null. A CHEAP cached accessor for the
    /// camera-list sidebar — never does I/O.</summary>
    WifiReading? CachedWifiSignal => null;

    /// <summary>Refreshes <see cref="CachedWifiSignal"/>, throttled so it is safe to
    /// fire on every camera-list poll. No-op on cameras with no Wi-Fi source.</summary>
    Task WarmWifiSignalAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Drops the cached Wi-Fi reading (the camera is unreachable, so the
    /// value is no longer evidence of anything).</summary>
    void ForgetWifiSignal() { }

    /// <summary>True when the camera reports it is running on a CABLE, false when it
    /// reports Wi-Fi, null when it hasn't said. A Wi-Fi-capable camera on ethernet
    /// answers "wired" — it has no signal to report, and asking for one is pointless.</summary>
    bool? CachedWired => null;

    /// <summary>The camera's PTZ preset slots, or null when unsupported.</summary>
    Task<IReadOnlyList<PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct);

    /// <summary>Drives the camera to a saved preset position.</summary>
    Task PtzToPresetAsync(int id, CancellationToken ct);

    /// <summary>Saves the camera's current position as preset <paramref name="id"/>.</summary>
    Task SavePtzPresetAsync(int id, string name, CancellationToken ct);

    /// <summary>The doorbell's quick-reply audio files, or null when unsupported.</summary>
    Task<IReadOnlyList<QuickReplyFile>?> GetQuickRepliesAsync(CancellationToken ct);

    /// <summary>Plays a quick-reply file through the camera's speaker.</summary>
    Task PlayQuickReplyAsync(int id, CancellationToken ct);

    /// <summary>The doorbell's auto-reply (default message) config, or null.</summary>
    Task<AutoReplyState?> GetAutoReplyAsync(CancellationToken ct);

    /// <summary>Sets the auto-reply file (-1 = off) and/or its wait time in seconds.</summary>
    Task SetAutoReplyAsync(int? fileId, int? timeoutSeconds, CancellationToken ct);

    /// <summary>Whether AI auto-tracking is on; null when the camera has none.</summary>
    Task<bool?> GetAutoTrackAsync(CancellationToken ct);

    /// <summary>Turns AI auto-tracking on or off.</summary>
    Task SetAutoTrackAsync(bool on, CancellationToken ct);

    /// <summary>The camera's SD-card slots, or null when unsupported.</summary>
    Task<IReadOnlyList<SdCardInfo>?> GetSdCardsAsync(CancellationToken ct);

    // The members below default to "not available" so control surfaces without a
    // Reolink HTTP API (generic RTSP cameras, test doubles) need no boilerplate.

    /// <summary>Motion-detection sensitivity normalized to 1-50 (higher = more
    /// sensitive) across firmware dialects, or null when unsupported.</summary>
    Task<int?> GetMdSensitivityAsync(CancellationToken ct) => Task.FromResult<int?>(null);

    /// <summary>Sets the motion-detection sensitivity (1-50, higher = more sensitive).</summary>
    Task SetMdSensitivityAsync(int sensitivity, CancellationToken ct) =>
        throw new NotSupportedException("motion sensitivity is not available for this camera");

    /// <summary>Per-type AI detection sensitivities, or null when the camera has none.</summary>
    Task<IReadOnlyList<AiSensitivity>?> GetAiSensitivitiesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AiSensitivity>?>(null);

    /// <summary>Sets one AI type's sensitivity (0-100, higher = more sensitive).</summary>
    Task SetAiSensitivityAsync(string aiType, int sensitivity, CancellationToken ct) =>
        throw new NotSupportedException("AI sensitivity is not available for this camera");

    /// <summary>The camera's HTTP API cannot be asked at this moment — the transport
    /// backoff is armed after a failure, or the camera is parked asleep. Distinguishes
    /// "this camera has no such feature" (a lasting answer) from "ask again shortly".</summary>
    bool HttpPaused => false;

    /// <summary>The detection types with a separately editable zone. Most cameras
    /// govern every type with ONE zone and answer just "md"; a camera with per-type
    /// grids lists those too.</summary>
    IReadOnlyList<string> ZoneTypes() => new[] { "md" };

    /// <summary>Whether the CAMERA holds its own detection zone — the question that
    /// decides whether a zone is written to the camera or kept by Neolink on its
    /// behalf. True = it has answered with a grid; false = it provably has none;
    /// null = not yet known, so nobody may assume either way.
    ///
    /// It is a LASTING property, never a per-request read: deciding it from whether
    /// a read happened to succeed would quietly move a Reolink camera's zone onto
    /// the server the first time its HTTP API hiccuped. Control surfaces with no
    /// camera-side zone at all answer false, which is the default here.</summary>
    bool? CameraHoldsZone => false;

    /// <summary>Whether this camera is known to keep no zone WITHOUT asking (no path to a
    /// camera-side grid at all), so a save to Neolink needs no word from the editor.</summary>
    bool ZoneNeverOnCamera => CameraHoldsZone == false;

    /// <summary>The zone grid for "md" or an AI type, or null when the camera has none.</summary>
    Task<DetectionZone?> GetDetectionZoneAsync(string type, CancellationToken ct) =>
        Task.FromResult<DetectionZone?>(null);

    /// <summary>Writes a zone grid; Table dimensions must match what
    /// <see cref="GetDetectionZoneAsync"/> reported.</summary>
    Task SetDetectionZoneAsync(string type, string table, CancellationToken ct) =>
        throw new NotSupportedException("detection zones are not available for this camera");

    /// <summary>Sets the ISP HDR value (0 = off; the camera's range caps it).</summary>
    Task SetHdrAsync(int value, CancellationToken ct) =>
        throw new NotSupportedException("HDR is not available for this camera");

    /// <summary>The OSD overlay config, or null when unsupported.</summary>
    Task<OsdSettings?> GetOsdSettingsAsync(CancellationToken ct) => Task.FromResult<OsdSettings?>(null);

    /// <summary>Read-modify-write of the OSD overlay: null fields stay untouched.</summary>
    Task SetOsdSettingsAsync(bool? showName, string? namePos, bool? showTime, string? timePos,
        bool? watermark, CancellationToken ct) =>
        throw new NotSupportedException("OSD settings are not available for this camera");

    /// <summary>Asks the camera (which asks Reolink's servers) whether newer firmware
    /// exists. Read-only and cached; null when unsupported/offline.</summary>
    Task<FirmwareStatus?> CheckFirmwareAsync(CancellationToken ct) => Task.FromResult<FirmwareStatus?>(null);

    /// <summary>Days of the given month with recordings on the camera's SD card,
    /// or null when the camera can't be searched.</summary>
    Task<IReadOnlyList<int>?> GetSdRecordingDaysAsync(int year, int month, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>?>(null);

    /// <summary>The SD-card recordings of one (camera-local) day, or null. <paramref name="stream"/>
    /// "main"/"sub" lists that stream only; null lists main, or sub when main has nothing.</summary>
    Task<IReadOnlyList<SdRecording>?> GetSdRecordingsAsync(DateOnly day, CancellationToken ct, string? stream = null,
        bool preempt = true) =>
        Task.FromResult<IReadOnlyList<SdRecording>?>(null);

    /// <summary>Why the last SD-card search failed, in words for the UI; null when it didn't.</summary>
    string? SdFailure => null;

    /// <summary>Opens a streaming download of one SD-card recording by its Search name.</summary>
    /// <paramref name="yield"/>: a background fetch that must never pre-empt a viewer's transfer.
    Task<ReolinkHttpApi.SdDownload> OpenSdRecordingAsync(string fileName, CancellationToken ct, bool yield = false) =>
        throw new NotSupportedException("SD-card playback is not available for this camera");

    /// <summary>The password of encrypted SD recordings, held in memory only; null clears it.</summary>
    void SetSdPassword(string? password) { }

    /// <summary>Device settings over Baichuan (SD recording switch, auto-reboot, PTZ guard and
    /// patrols, privacy masks, chimes, smart rules, SD cards), or null. Default: none.</summary>
    Task<DeviceExtras?> GetDeviceExtrasAsync(CancellationToken ct) => Task.FromResult<DeviceExtras?>(null);

    /// <summary>Whether a card is mounted and the camera records to it (two small reads); null when unknown.</summary>
    Task<(bool Mounted, bool Recording)?> GetSdStateAsync(CancellationToken ct) =>
        Task.FromResult<(bool, bool)?>(null);

    /// <summary>The last card listing fetched for a day (any caller), with when; null when never fetched.</summary>
    (DateTime At, IReadOnlyList<SdRecording> List)? LastSdRecordings(DateOnly day, string? stream = null) => null;

    /// <summary>Server time minus the camera's clock, to the second (what to add to card times); null when unknown.</summary>
    Task<TimeSpan?> GetClockOffsetAsync(CancellationToken ct) => Task.FromResult<TimeSpan?>(null);

    /// <summary>The camera's own SD recording on or off.</summary>
    Task SetSdRecordingAsync(bool on, CancellationToken ct) => NotHere("SD recording");

    /// <summary>Scheduled reboot; null fields stay as they are.</summary>
    Task SetAutoRebootAsync(bool? enabled, string? weekDay, int? hour, int? minute, CancellationToken ct) =>
        NotHere("auto-reboot");

    /// <summary>Guard position: return-to-guard on/off and its idle timeout (seconds), or an
    /// action — "set" saves the current position, "go" drives there.</summary>
    Task SetGuardAsync(bool? enabled, int? timeout, string? action, CancellationToken ct) => NotHere("PTZ guard");

    /// <summary>Starts or stops a saved patrol.</summary>
    Task SetPatrolAsync(int id, bool run, CancellationToken ct) => NotHere("PTZ patrol");

    /// <summary>Privacy masks (drawn in the Reolink app) on or off.</summary>
    Task SetPrivacyMasksAsync(bool on, CancellationToken ct) => NotHere("privacy masks");

    /// <summary>A paired chime's volume and/or LED; null fields stay as they are.</summary>
    Task SetChimeAsync(int id, int? volume, bool? led, CancellationToken ct) => NotHere("chime control");

    /// <summary>Rings a paired chime once.</summary>
    Task RingChimeAsync(int id, CancellationToken ct) => NotHere("chime control");

    /// <summary>Silences a chime for <paramref name="seconds"/>; 0 ends the silence.</summary>
    Task SetChimeSilentAsync(int id, int seconds, CancellationToken ct) => NotHere("chime silent mode");

    /// <summary>Edits (sensitivity, seconds) or deletes one smart-detection rule.</summary>
    Task SetSmartRuleAsync(string type, int index, int? sensitivity, int? seconds, bool delete, CancellationToken ct) =>
        NotHere("smart detection rules");

    private static Task NotHere(string what) =>
        Task.FromException(new NotSupportedException($"{what} is not available for this camera"));

    /// <summary>
    /// Two-way talk: streams 16-bit LE mono PCM chunks at <paramref name="sampleRate"/>
    /// to the camera's speaker until the channel completes or <paramref name="ct"/>
    /// fires. One session per camera; a second concurrent call throws
    /// <see cref="TalkBusyException"/>.
    /// </summary>
    Task TalkAsync(int sampleRate, ChannelReader<byte[]> pcm, CancellationToken ct);
}

/// <summary>A talk session was requested while another one is active on the same camera.</summary>
public sealed class TalkBusyException : Exception
{
    public TalkBusyException(string name) : base($"Camera '{name}' already has an active talk session") { }
}

/// <summary>
/// Control commands for one camera, riding the primary stream's connection.
/// All commands are serialized through one gate: the BC connection allows only
/// one outstanding request per message ID, and cameras are generally happier
/// answering control commands one at a time.
/// </summary>
public sealed class CameraControl : ICameraControl
{
    /// <summary>Probes for optional features must fail fast, not hold the gate for 15s.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly ILiveCameraSource _source;
    private readonly IReadOnlyList<ILiveCameraSource> _sources;
    private readonly ReolinkHttpApi? _httpApi;
    private readonly OnvifClient? _onvif;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Which transport last served picture settings: true = the Reolink HTTP
    /// API answered, false = it didn't and ONVIF stood in, null = not read yet. A
    /// WRITE routes the same way — the Lumus has a (dead) HTTP API derived from its
    /// host, so "_httpApi != null" alone can't decide where a write should go.</summary>
    private bool? _httpImagingWorks;

    // Capabilities are cached for the lifetime of one camera session; a reconnect
    // (new IBcCamera instance) invalidates the cache.
    private IBcCamera? _capsSession;
    private CameraCapabilities? _caps;
    private uint _floodlightReadId = BcConstants.MsgIdFloodlightTasksGet;

    /// <param name="allSources">Every stream service of this camera. The camera is
    /// ONE device with up to three connections (main/sub/extern): it is online if
    /// ANY of them is live, and commands ride whichever session exists — otherwise
    /// a viewer watching only the sub stream leaves the "camera" reading offline
    /// (and HA unavailable) while its video plays. Commands still prefer
    /// <paramref name="source"/> (the primary) when it is connected.</param>
    public CameraControl(ILiveCameraSource source, ReolinkHttpApi? httpApi = null,
        IReadOnlyList<ILiveCameraSource>? allSources = null, OnvifClient? onvif = null)
    {
        _source = source;
        _sources = allSources is { Count: > 0 } ? allSources : new[] { source };
        _httpApi = httpApi;
        _onvif = onvif;
    }

    /// <summary>Whether picture settings can be read/written over ONVIF when the
    /// Reolink HTTP CGI API isn't there (Lumus and other HTTP-less models). Only a
    /// fallback: an HTTP camera never routes through it.</summary>
    public bool HasImagingFallback => _onvif != null;

    public string CameraName => _source.Name;
    public bool Online => _sources.Any(s => s.LiveCamera != null);

    /// <summary>The camera is deliberately offline so its battery can sleep (every
    /// stream parked, sleep-friendly). Background HTTP/ONVIF traffic is suppressed
    /// while this holds: nothing can answer, and the packets themselves keep the
    /// camera's radio out of power-save — which the wake scan then misreads as the
    /// camera waking (seen live, 2026-07-22: ONVIF retries and HTTP feature reads
    /// against a parked Argus Solar faked its ping pattern flat).</summary>
    private bool SleepingOnPurpose => _sources.All(s => s.NetworkQuiet);

    /// <summary>The primary stream's session when connected, else any live one.</summary>
    private IBcCamera? AnyLive()
    {
        if (_source.LiveCamera is { } primary) return primary;
        foreach (var s in _sources)
            if (s.LiveCamera is { } c)
                return c;
        return null;
    }

    private async Task<T> WithCameraAsync<T>(Func<IBcCamera, Task<T>> op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var camera = AnyLive() ?? throw new CameraOfflineException(CameraName);
            return await op(camera).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        WithCameraAsync(async camera =>
        {
            // Fresh session, cached caps: re-discover for wired cameras (cheap, and
            // it picks up firmware/config changes), but NOT for battery cameras —
            // they reconnect on every wake-capture event, and re-running the probe
            // fan-out each time held the camera awake for extra seconds per event.
            // Hardware features don't change between naps; a restart rediscovers.
            if (_caps != null && (ReferenceEquals(_capsSession, camera) || _caps.Features.Battery))
            {
                _capsSession = camera; // don't pin the previous session object
                return _caps;
            }

            var support = await TryAsync(() => camera.GetSupportAsync(ProbeTimeout, ct)).ConfigureAwait(false);
            var version = await TryAsync(() => camera.GetVersionAsync(ProbeTimeout, ct)).ConfigureAwait(false);

            // Feature discovery. PTZ is advertised in the Support xml; the rest are
            // probed with their harmless "get" command — a camera without the feature
            // rejects it (non-200) or stays silent. The probes run IN PARALLEL:
            // each uses a distinct message id (the connection routes replies per id,
            // and the command gate is held, so nothing else interleaves), and run
            // sequentially the silent ones stack up to 4s each — long enough for the
            // UI's HTTP timeout to abort the first panel open after a reconnect.
            bool ptz = SupportFlag(support, "ptzMode") || SupportFlag(support, "ptzCfg");
            // Siren comes from the Support flags alone — its only "probe" command
            // PLAYS the siren, which is not something discovery may ever do.
            bool siren = SupportFlag(support, "supportAudioAlarm")
                         || SupportFlag(support, "audioAlarm")
                         || SupportFlag(support, "supportAudioAlarmEnable");
            var ledTask = ProbeAsync(() => camera.GetLedStateAsync(ProbeTimeout, ct));
            var pirTask = ProbeAsync(() => camera.GetPirStateAsync(ProbeTimeout, ct));
            var batteryTask = ProbeAsync(() => camera.GetBatteryInfoAsync(ProbeTimeout, ct));
            var talkTask = TryAsync(() => camera.GetTalkAbilityAsync(ProbeTimeout, ct));
            var zoomTask = TryAsync(() => camera.GetZoomFocusAsync(ProbeTimeout, ct));
            // FloodlightTask: 289 is in every shared IPC binary, so only a camera advertising
            // a light is asked; 438 (reference neolink's id) stays for firmware that answers it.
            uint ledCtrl = ChannelSupportValue(support, camera.ChannelId, "ledCtrl");
            bool lightAdvertised = (ledCtrl & 4) != 0
                                   || ChannelSupportValue(support, camera.ChannelId, "lightType") > 0;
            var floodGetTask = lightAdvertised
                ? TryAsync(() => camera.GetFloodlightTasksAsync(ProbeTimeout, ct, BcConstants.MsgIdFloodlightTasksGet))
                : Task.FromResult<XElement?>(null);
            var floodTask = TryAsync(() => camera.GetFloodlightTasksAsync(ProbeTimeout, ct, BcConstants.MsgIdFloodlightTasksRead));
            // White-LED / spotlight over the HTTP API (Lumus, Elite, ... — cameras
            // that don't answer the Baichuan FloodlightTask). Runs in parallel with
            // the BC probes and shares their timeout, so an unreachable HTTP port
            // (port 80 closed) doesn't slow discovery.
            var whiteLedTask = _httpApi == null
                ? Task.FromResult<WhiteLedState?>(null)
                : ProbeWhiteLedAsync(ct);
            // Privacy mode needs BOTH of Reolink's support signals (mirroring
            // reolink_aio, which is what Home Assistant ships):
            //   1. the login DeviceInfo advertises a <sleep> element, AND
            //   2. this channel's Support flags carry remoteAbility > 0.
            // Either alone over-detects: some non-battery firmwares (e.g. the
            // RLC "Elite" WiFi line) include <sleep> in DeviceInfo as a status
            // field and answer the 574 state query — but ignore writes. Only
            // cameras passing both gates get the (beta) privacy section.
            bool sleepAd = camera.DeviceInfo?.HasSleep == true;
            bool remoteAbility = ChannelSupportFlag(support, camera.ChannelId, "remoteAbility");
            var privacyTask = sleepAd && remoteAbility
                ? ProbeValueAsync(() => camera.GetPrivacyModeAsync(ProbeTimeout, ct))
                : Task.FromResult<bool?>(null);
            await Task.WhenAll(ledTask, pirTask, batteryTask, talkTask, zoomTask, floodTask, floodGetTask,
                privacyTask, whiteLedTask).ConfigureAwait(false);
            bool led = ledTask.Result;
            bool pir = pirTask.Result;
            bool battery = batteryTask.Result;
            bool talk = talkTask.Result != null
                && talkTask.Result.AudioType.Equals("adpcm", StringComparison.OrdinalIgnoreCase);
            // A real zoom lens reports a usable range; fixed-lens cameras answer
            // with max 0 (or not at all) and get no zoom UI.
            bool zoom = ZoomMax(zoomTask.Result) > 0;
            bool spotlightBit = (ledCtrl & 4) != 0;
            var (floodlight, spotlightTasks, floodReadId) =
                ClassifyFloodlight(floodTask.Result != null, floodGetTask.Result != null, spotlightBit);
            _floodlightReadId = floodReadId;
            bool privacy = privacyTask.Result != null; // camera answered the sleep query
            // A physical white spotlight is advertised by ledCtrl bit 2 in Support —
            // this picks out the Lumus/Elite lines and leaves status-LED-only models
            // (E1 Pro) and the doorbell out. Its ON/OFF rides the Baichuan lightState
            // toggle; brightness rides HTTP when it answers, else FloodlightTask 289.
            bool spotlight = !floodlight && spotlightBit;
            bool whiteLed = spotlight && whiteLedTask.Result != null;
            // A real video doorbell advertises doorbellVersion in its Support block.
            // Needed as a gate because some non-doorbells (the RLC "Elite" WiFi line)
            // report a doorbellLightState field in their LedState anyway.
            bool doorbell = ChannelSupportValue(support, camera.ChannelId, "doorbellVersion") > 0;

            _caps = new CameraCapabilities(version, support, new CameraFeatures(
                ptz, led, pir, battery, talk, zoom, siren, floodlight, privacy, whiteLed, spotlight, doorbell,
                SpotlightTasks: spotlight && spotlightTasks));
            _capsSession = camera;
            // This sweep probes with a longer budget than the stream service's one
            // short login-time battery query, so it is often the first path to prove
            // a slow battery camera IS one. Tell every stream service: their idle
            // grace and sleep policy hang off that flag.
            if (battery)
                foreach (var s in _sources)
                    s.BatteryDetected();
            Log.Info($"{CameraName}: capabilities discovered " +
                     $"(ptz={ptz}, led={led}, pir={pir}, battery={battery}, talk={talk}" +
                     $", zoom={zoom}, siren={siren}, floodlight={floodlight}, privacy={privacy}" +
                     $", spotlight={spotlight}, whiteLed={whiteLed}, doorbell={doorbell}" +
                     $"{(floodlight || spotlightTasks ? $", floodlightTaskId={floodReadId}" : "")}" +
                     $"{(version != null && version.Model.Length > 0 ? $", model={version.Model}" : "")})");
            if (sleepAd != remoteAbility)
                Log.Debug($"{CameraName}: privacy gate — DeviceInfo<sleep>={sleepAd}, " +
                          $"remoteAbility={remoteAbility} (both required; mismatch means privacy stays off)");
            return _caps;
        }, ct);

    /// <summary>Probe returning the value itself (null = feature absent/silent).</summary>
    private static async Task<T?> ProbeValueAsync<T>(Func<Task<T?>> op) where T : struct
    {
        try { return await op().ConfigureAwait(false); }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException or IOException) { return null; }
    }

    /// <summary>The zoom range's maxPos from a &lt;PtzZoomFocus&gt; reply (0 = none/fixed lens).</summary>
    internal static long ZoomMax(XElement? zoomFocus) => ZoomPosition(zoomFocus)?.Max ?? 0;

    /// <summary>The zoom range and where the lens is, from a &lt;PtzZoomFocus&gt; reply; null for a fixed lens.</summary>
    internal static (long Min, long Max, long Cur)? ZoomPosition(XElement? zoomFocus)
    {
        var zoom = zoomFocus?.Element("zoom");
        static long? Read(XElement? e) => long.TryParse(e?.Value.Trim(), out var v) ? v : null;
        if (Read(zoom?.Element("maxPos")) is not { } max || max <= 0) return null;
        var min = Math.Min(Read(zoom!.Element("minPos")) ?? 0, max);
        return (min, max, Math.Clamp(Read(zoom.Element("curPos")) ?? min, min, max));
    }

    public Task<StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetStreamInfoAsync(ct: ct), ct);

    public bool CanSetStreamSettings => _httpApi != null;

    public async Task<IReadOnlyList<StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct)
    {
        if (_httpApi == null) return null;
        var enc = await _httpApi.GetEncAsync(ct).ConfigureAwait(false);
        var list = new List<StreamEncSetting>();
        // HTTP API key → Baichuan stream kind (the names differ for the third stream).
        foreach (var (key, kind) in new[] { ("mainStream", "mainStream"), ("subStream", "subStream"), ("extStream", "externStream") })
        {
            if (enc[key] is not System.Text.Json.Nodes.JsonObject s) continue;
            var size = ((string?)s["size"] ?? "").Split('*');
            uint w = size.Length == 2 && uint.TryParse(size[0], out var pw) ? pw : 0;
            uint h = size.Length == 2 && uint.TryParse(size[1], out var ph) ? ph : 0;
            list.Add(new StreamEncSetting(kind, w, h, (uint?)s["frameRate"] ?? 0, (uint?)s["bitRate"] ?? 0));
        }
        return list;
    }

    // Rides the camera's HTTP API, not the BC connection (no verified BC setter
    // exists), so it works even while the BC session is mid-reconnect and does
    // not take the command gate. Read-modify-write, like the LED/PIR setters.
    public async Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
        uint? framerate, uint? bitrate, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException(
                $"changing stream settings requires the camera's HTTP API; set \"http_address\" for '{CameraName}' in the config");

        // Baichuan and the HTTP API name the third stream differently.
        string key = stream == "externStream" ? "extStream" : stream;
        var enc = await _httpApi.GetEncAsync(ct).ConfigureAwait(false);
        if (enc[key] is not JsonObject encStream)
            throw new NotSupportedException($"{CameraName} does not expose '{stream}' encode settings over its HTTP API");

        if (width != null && height != null) encStream["size"] = $"{width}*{height}";
        if (framerate != null) encStream["frameRate"] = framerate.Value;
        if (bitrate != null) encStream["bitRate"] = bitrate.Value;
        await _httpApi.SetEncAsync(enc, ct).ConfigureAwait(false);

        Log.Info($"{CameraName}: {stream} encode settings changed" +
                 $"{(width != null ? $" to {width}x{height}" : "")}" +
                 $"{(framerate != null ? $" @{framerate}fps" : "")}" +
                 $"{(bitrate != null ? $" {bitrate}kbps" : "")}" +
                 " — the camera restarts the stream to apply");
    }

    public Task<XElement?> GetBatteryInfoAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetBatteryInfoAsync(ct: ct), ct);

    public Task<IReadOnlyList<ServicePortState>?> GetServicePortsAsync(CancellationToken ct) =>
        WithCameraAsync<IReadOnlyList<ServicePortState>?>(async camera =>
        {
            var els = await camera.GetServicePortsAsync(ct: ct).ConfigureAwait(false);
            return els == null ? null : MapServicePorts(els);
        }, ct);

    /// <summary>Raw msg-37 elements → states. "HttpPort" carries its number in a
    /// camelCase child of the same name ("httpPort") plus an optional "enable".</summary>
    internal static List<ServicePortState> MapServicePorts(IEnumerable<XElement> elements)
    {
        var list = new List<ServicePortState>();
        foreach (var el in elements)
        {
            var name = el.Name.LocalName;
            if (!name.EndsWith("Port", StringComparison.Ordinal)) continue;
            var portEl = el.Elements().FirstOrDefault(c =>
                c.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Add(new ServicePortState(
                name[..^4].ToLowerInvariant(),
                int.TryParse(portEl?.Value, out var p) ? p : null,
                el.Element("enable")?.Value switch { "1" => true, "0" => false, _ => null }));
        }
        return list;
    }

    public Task SetServicePortEnabledAsync(string service, bool on, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            // NON-NEGOTIABLE: the Baichuan port (9000) carries this very
            // connection — it is never enabled, disabled or changed from here,
            // regardless of what any caller asks for.
            if (service.Equals("server", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    "refusing to touch the Baichuan port — it carries this very connection");
            string verb = on ? "enabl" : "disabl";
            var els = await camera.GetServicePortsAsync(ct: ct).ConfigureAwait(false)
                ?? throw new NotSupportedException($"{CameraName} did not answer the service-port query");
            var el = els.FirstOrDefault(e =>
                    e.Name.LocalName.Equals(service + "Port", StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException($"{CameraName} does not report a {service} service");
            var enable = el.Element("enable")
                ?? throw new NotSupportedException(
                    $"{CameraName}'s firmware does not allow toggling {service}");
            string want = on ? "1" : "0";
            if (enable.Value == want) return null; // already there — nothing to write
            Log.Info($"{CameraName}: {verb}ing the {service} service on the camera " +
                     "(admin request — the same switch as the app's Port Settings)");
            enable.Value = want;
            await camera.SetServicePortAsync(el, ct).ConfigureAwait(false);
            // Some firmwares accept the set silently — the re-read is the truth,
            // and it keeps the UI honest about the camera's ACTUAL state.
            var after = await camera.GetServicePortsAsync(ct: ct).ConfigureAwait(false);
            var check = after?.FirstOrDefault(e =>
                e.Name.LocalName.Equals(service + "Port", StringComparison.OrdinalIgnoreCase));
            if (check?.Element("enable")?.Value != want)
            {
                Log.Warn($"{CameraName}: the {service} {verb}e was sent but the camera still " +
                         $"reports it {(on ? "disabled" : "enabled")} — firmware may not accept " +
                         "msg 36 from third-party clients; use the Reolink app's Port Settings " +
                         "screen instead");
                throw new InvalidOperationException(
                    $"{CameraName} did not accept {verb}ing {service} " +
                    $"(it still reads {(on ? "disabled" : "enabled")})");
            }
            var state = MapServicePorts(new[] { check }).FirstOrDefault();
            Log.Info($"{CameraName}: {service} service {(on ? "ENABLED" : "DISABLED")} on the camera" +
                     $"{(state?.Port is { } port ? $" (port {port})" : "")}");
            return null;
        }, ct);

    public Task<byte[]?> SnapshotAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.SnapAsync(ct), ct);

    /// <summary>The Baichuan snap requests subStream, but some firmwares (dual-lens
    /// Duos among them) ignore that and return the full panorama — megabytes of
    /// JPEG. The HTTP API's Snap honors explicit scaling, so prefer it here.</summary>
    public async Task<byte[]?> SnapshotSmallAsync(CancellationToken ct)
    {
        // Smallest stream tier first: on a dual-lens camera even the SUB snapshot
        // is a multi-megabyte panorama, but the extern ("ext") tier is genuinely
        // small. Firmwares without the tier answer with an error → next rung.
        if (_httpApi != null)
        {
            foreach (var tier in new[] { "ext", "sub" })
            {
                if (await HttpTryAsync<byte[]?>(async c =>
                        await _httpApi!.SnapAsync(tier, 640, 360, c).ConfigureAwait(false), ct, HttpSnapTimeout)
                        .ConfigureAwait(false) is { } jpeg)
                {
                    Log.Debug($"{CameraName}: small snapshot via HTTP Snap {tier} ({jpeg.Length / 1024} KB)");
                    return jpeg;
                }
            }
        }
        Log.Debug($"{CameraName}: HTTP small snapshot unavailable — using the Baichuan snap");
        return await SnapshotAsync(ct).ConfigureAwait(false);
    }

    public Task<XElement?> GetLedStateAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetLedStateAsync(ct: ct), ct);

    public Task SetLedStateAsync(string? state, string? lightState,
        string? doorbellLightState, int? irBrightness, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            // Read-modify-write: only touch the requested fields, keep the rest verbatim.
            var led = await camera.GetLedStateAsync(ct: ct).ConfigureAwait(false)
                ?? throw new NotSupportedException($"{CameraName} does not expose LED state");
            if (state != null) SetChild(led, "state", state);
            if (lightState != null) SetChild(led, "lightState", lightState);
            // These two only exist on cameras that report them; writing the element
            // into a LedState that lacks it would be guesswork, so that's refused.
            if (doorbellLightState != null)
            {
                if (led.Element("doorbellLightState") == null)
                    throw new NotSupportedException($"{CameraName} has no doorbell light");
                SetChild(led, "doorbellLightState", doorbellLightState);
            }
            if (irBrightness is { } irb)
            {
                if (led.Element("IRLedBrightness") == null)
                    throw new NotSupportedException($"{CameraName} does not report an adjustable IR brightness");
                SetChild(led, "IRLedBrightness", Math.Clamp(irb, 0, 100).ToString());
            }
            await camera.SetLedStateAsync(led, ct).ConfigureAwait(false);
            return null;
        }, ct);

    public Task<XElement?> GetPirStateAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetPirStateAsync(ct: ct), ct);

    public Task SetPirEnabledAsync(bool enabled, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            var pir = await camera.GetPirStateAsync(ct: ct).ConfigureAwait(false)
                ?? throw new NotSupportedException($"{CameraName} does not expose PIR settings");
            SetChild(pir, "enable", enabled ? "1" : "0");
            await camera.SetPirStateAsync(pir, ct).ConfigureAwait(false);
            return null;
        }, ct);

    public async Task PtzAsync(string command, float speed, CancellationToken ct)
    {
        await WithCameraAsync<object?>(async camera =>
        {
            await camera.PtzAsync(command, speed, ct).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
        PtzCommandSent?.Invoke(command);
    }

    public event Action<string>? PtzCommandSent;

    public Task<XElement?> GetZoomFocusAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetZoomFocusAsync(ct: ct), ct);

    public Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SetZoomFocusAsync(command, movePos, ct).ConfigureAwait(false);
            return null;
        }, ct);

    public Task SirenAsync(bool? on, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            if (on is { } latch) await camera.SirenManualAsync(latch, ct).ConfigureAwait(false);
            else await camera.SirenBurstAsync(ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: 🔊 siren {(on == null ? "burst" : on == true ? "ON (until stopped)" : "off")}");
            return null;
        }, ct);

    public Task<bool?> GetPrivacyModeAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetPrivacyModeAsync(ct: ct), ct);

    public Task SetPrivacyModeAsync(bool on, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SetPrivacyModeAsync(on, ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: privacy mode {(on ? "ON — camera going dark" : "off")}");
            return null;
        }, ct);

    public Task<XElement?> GetFloodlightTasksAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetFloodlightTasksAsync(ct: ct, msgId: _floodlightReadId), ct);

    /// <summary>438 wins when answered, else 289. A 289 answer makes a floodlight only without
    /// the spotlight bit, so spotlights keep their Home Assistant entities.</summary>
    internal static (bool Floodlight, bool SpotlightTasks, uint ReadId) ClassifyFloodlight(
        bool answered438, bool answered289, bool spotlightBit)
    {
        if (answered438) return (true, false, BcConstants.MsgIdFloodlightTasksRead);
        if (!answered289) return (false, false, BcConstants.MsgIdFloodlightTasksGet);
        return spotlightBit
            ? (false, true, BcConstants.MsgIdFloodlightTasksGet)
            : (true, false, BcConstants.MsgIdFloodlightTasksGet);
    }

    public Task SetFloodlightTasksAsync(XElement task, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SetFloodlightTasksAsync(task, ct).ConfigureAwait(false);
            return null;
        }, ct);

    // ------------------------------------------------------------ white LED (HTTP)

    /// <summary>4-second-capped probe (the BC probes' budget) so a closed HTTP port
    /// doesn't stall capability discovery.</summary>
    private async Task<WhiteLedState?> ProbeWhiteLedAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try { return ParseWhiteLed(await _httpApi!.GetWhiteLedAsync(cts.Token).ConfigureAwait(false)); }
        catch { return null; }
    }

    private static WhiteLedState ParseWhiteLed(System.Text.Json.Nodes.JsonObject wl) => new(
        Bright: Math.Clamp((int?)wl["bright"] ?? 0, 0, 100),
        On: ((int?)wl["state"] ?? 0) != 0,
        Mode: (int?)wl["mode"] ?? 0);

    public async Task<WhiteLedState?> GetWhiteLedAsync(CancellationToken ct)
    {
        if (_httpApi == null) return null;
        try { return ParseWhiteLed(await _httpApi.GetWhiteLedAsync(ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is ReolinkApiException or IOException or TimeoutException) { return null; }
    }

    public async Task SetWhiteLedAsync(int? bright, bool? on, int? mode, CancellationToken ct)
    {
        if (_httpApi == null) throw new InvalidOperationException("camera has no HTTP API for the white LED");
        // Read-modify-write so the camera's schedule and AI-detect config ride along.
        var wl = await _httpApi.GetWhiteLedAsync(ct).ConfigureAwait(false);
        if (bright is { } b) wl["bright"] = Math.Clamp(b, 0, 100);
        if (on is { } o) wl["state"] = o ? 1 : 0;
        if (mode is { } m) wl["mode"] = m;
        await _httpApi.SetWhiteLedAsync(wl, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: white LED set (bright={bright}, on={on}, mode={mode})");
    }

    // ------------------------------------------------- HTTP-API extras (beta)
    // Everything below rides the camera's Reolink HTTP API. Reads are best-effort:
    // an unsupported command (the API answered but rejected it) or an unreachable
    // API turns into null, never an error — the UI simply hides that section.
    // Writes go direct and let failures surface.

    /// <summary>Cap for best-effort HTTP reads, so one dead HTTP port can't stall a
    /// combined feature read for the client's whole HTTP timeout.</summary>
    private static readonly TimeSpan HttpCallTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Roomier cap for the snapshot fetch: it pays for a login AND an image
    /// download over the camera's Wi-Fi — 6s cancels perfectly healthy cameras.</summary>
    private static readonly TimeSpan HttpSnapTimeout = TimeSpan.FromSeconds(20);

    /// <summary>After a transport-level failure, HTTP reads are skipped until this
    /// time — a camera with port 80 closed shouldn't be re-probed on every panel open.</summary>
    private DateTime _httpRetryAt;

    /// <summary>Whether the one-time "HTTP API unreachable" warning fired for the
    /// current outage. Reset on the first successful call, so a later outage warns
    /// again — users otherwise never learn WHY HTTP-backed features are missing
    /// (a Duo shipped with its HTTP port disabled cost hours of MQTT debugging).</summary>
    private bool _httpUnreachableWarned;

    /// <summary>Same idea for a login the camera answered but rejected.</summary>
    private bool _httpLoginWarned;

    /// <summary>Consecutive transport failures. The unreachable warning waits for a
    /// streak: a single timeout during startup (every camera juggling logins and
    /// stream starts at once) is routine, not an outage.</summary>
    private int _httpFailStreak;

    /// <summary>A marginal HTTP server (Wi-Fi camera under load) flaps between
    /// working and stalled; without a cooldown every flap would re-warn.</summary>
    private DateTime _httpWarnCooldownUntil;

    private async Task<T?> HttpTryAsync<T>(Func<CancellationToken, Task<T?>> op, CancellationToken ct,
        TimeSpan? timeout = null, bool force = false)
    {
        // force = an explicit user action (SD-card browse): it gets its try even
        // while the transport backoff is armed — the user pressed refresh NOW,
        // and a no-op that quietly returns "nothing" reads as data loss.
        // A camera sleeping on purpose is left in radio silence: best-effort reads
        // return "nothing" without sending a packet (see SleepingOnPurpose).
        if (_httpApi == null || (!force && (DateTime.UtcNow < _httpRetryAt || SleepingOnPurpose)))
            return default;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A call without a live session token pays for a LOGIN round-trip before
        // the command — on a Wi-Fi camera under streaming load (dual-lens Duos)
        // that alone can eat most of the 6s config budget, so every first read
        // timed out, armed the backoff, and HTTP features "vanished" while the
        // camera's API was actually fine. Give login-bearing calls the roomy
        // snapshot budget; token-riding calls keep the tight one.
        cts.CancelAfter(timeout ?? (_httpApi.HasLiveToken ? HttpCallTimeout : HttpSnapTimeout));
        try
        {
            var result = await op(cts.Token).ConfigureAwait(false);
            NoteHttpReachable();
            return result;
        }
        catch (ReolinkApiException ex)
        {
            if (ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                // A REJECTED login must back way off: Reolink temporarily locks the
                // account after a handful of failures, so the usual retry cadence
                // would feed the lockout counter forever.
                _httpRetryAt = DateTime.UtcNow + TimeSpan.FromMinutes(15);
                if (!_httpLoginWarned)
                {
                    _httpLoginWarned = true;
                    Log.Warn($"{CameraName}: the camera answered on HTTP but REJECTED the login ({ex.Message}). " +
                             "HTTP-backed features stay unavailable. Verify the credentials work in the camera's " +
                             "own web page; Reolink locks the account temporarily after repeated failures, so " +
                             "retries are paused for 15 minutes.");
                }
                return default;
            }
            // The API is reachable, this camera just doesn't do the command.
            NoteHttpReachable();
            Log.Debug($"{CameraName}: HTTP API call failed: {ex.Message}");
            return default;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is IOException or TimeoutException or OperationCanceledException
                  or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
        {
            _httpRetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            _httpFailStreak++;
            // A slow/stalling server and a closed port are different stories and
            // deserve different advice — telling someone with HTTP enabled to
            // enable HTTP just gaslights them.
            bool slow = ex is OperationCanceledException or TimeoutException;
            var reason = ex is OperationCanceledException
                ? $"did not answer within {(timeout ?? HttpCallTimeout).TotalSeconds:0}s"
                : Log.Flatten(ex);
            if (_httpFailStreak >= 3 && !_httpUnreachableWarned && DateTime.UtcNow >= _httpWarnCooldownUntil)
            {
                _httpUnreachableWarned = true;
                _httpWarnCooldownUntil = DateTime.UtcNow + TimeSpan.FromMinutes(30);
                if (_sources.Any(s => s.SleepFriendly))
                {
                    // A battery model (Argus family) usually exposes no HTTP API at
                    // all — for it, silence is the normal state, not an outage, and
                    // the overload/firewall advice below would just mislead. Say it
                    // once per run, informationally, and stop asking the model to
                    // be something it isn't.
                    _httpWarnCooldownUntil = DateTime.MaxValue;
                    Log.Info($"{CameraName}: no HTTP API answered ({reason}) — battery models usually " +
                             "don't have one, so this is expected. Picture settings, Wi-Fi detail and " +
                             "scaled snapshots stay unavailable; streams, events, PIR, battery readings, " +
                             "volume and presets (Baichuan) are unaffected. If this model does expose " +
                             "HTTP, set 'http_address' explicitly.");
                    return default;
                }
                Log.Warn($"{CameraName}: the camera's HTTP API is not answering ({reason}). " +
                         "Picture settings, Wi-Fi signal and scaled snapshots are unavailable until it " +
                         "does (volume, PTZ presets and AI sensitivity fall back to Baichuan). " + (slow
                             // A no-reply timeout can't tell a slow camera from silently
                             // dropped packets — don't claim "the port is open".
                             ? "Either the camera is overloaded (Wi-Fi camera under streaming load), or " +
                               "something between Neolink and the camera is dropping HTTP traffic " +
                               "(firewall/VLAN rules, Docker networking). Reads resume automatically " +
                               "when it recovers."
                             : "Many cameras ship with HTTP disabled — enable it in the Reolink app " +
                               "(Settings > Network > Advanced > Port Settings), or set 'http_address' " +
                               "if the API lives on another host/port."));
            }
            else
            {
                Log.Debug($"{CameraName}: HTTP API not answering ({reason}) — skipping HTTP reads for 60s");
            }
            return default;
        }
    }

    private void NoteHttpReachable()
    {
        _httpRetryAt = default;
        _httpLoginWarned = false;
        _httpFailStreak = 0;
        // An answer proves this camera HAS an HTTP API — undo the "battery models
        // don't have one" PERMANENT quiet, so a real later outage warns properly.
        // The ordinary 30-minute flap cooldown stays: a marginal Wi-Fi camera
        // bouncing between working and stalled must not re-warn on every flap.
        if (_httpWarnCooldownUntil == DateTime.MaxValue) _httpWarnCooldownUntil = default;
        if (_httpUnreachableWarned)
        {
            _httpUnreachableWarned = false;
            Log.Info($"{CameraName}: the camera's HTTP API is reachable again — HTTP-backed features restored");
        }
    }

    /// <summary>The last sweep's non-empty result. Panels used to load "limited"
    /// at random: one slow answer mid-sweep armed the 60s transport backoff,
    /// blanking every remaining section AND the whole next open. Cached values
    /// fill whatever a sweep couldn't read, so a hiccup costs freshness (of
    /// near-static config), not whole panel sections.</summary>
    private HttpFeatures? _httpFeaturesCache;

    public async Task<HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct)
    {
        if (_httpApi == null)
        {
            // No Reolink HTTP API: picture settings from ONVIF (Lumus and other
            // HTTP-less models) and what Baichuan carries; everything else stays null.
            var onvifImage = await HttpOrOnvifImageAsync(ct).ConfigureAwait(false);
            var bare = await WithBcStandInsAsync(new HttpFeatures(onvifImage, null, null, null, null, null, null), ct)
                .ConfigureAwait(false);
            return bare is { Image: null, Volume: null, PtzPresets: null, AiSensitivities: null } ? null : bare;
        }
        // Sequential on purpose (the HTTP client serializes requests anyway) —
        // but ONE slow answer must not blank the rest of the panel. A mid-sweep
        // transport failure arms the 60s backoff to protect unrelated callers;
        // within THIS sweep the first two failures are forgiven (the pre-step
        // backoff is restored so the remaining sections still get their try).
        // The third strike leaves it armed and the rest no-op — and forgiveness
        // needs a PROVEN API (a login has succeeded): on a camera with no HTTP
        // at all, retrying just stacks login timeouts until the panel's own
        // request gives up.
        //
        // The whole sweep also lives inside one overall budget: the panel waits
        // 30s at most, and a sweep that answers late answers nobody. Steps the
        // budget cuts off read as null and fill from the cache below.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        int forgiven = 0;
        bool anyTransportFail = false;
        async Task<T?> Step<T>(Func<CancellationToken, Task<T?>> read)
        {
            var before = _httpRetryAt;
            try
            {
                var v = await read(budget.Token).ConfigureAwait(false);
                if (_httpRetryAt > before)
                {
                    // This step armed the transport backoff — a real reach failure.
                    anyTransportFail = true;
                    if (forgiven < 2 && _httpApi!.HasLiveToken)
                    {
                        forgiven++;
                        _httpRetryAt = before;
                    }
                }
                return v;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                anyTransportFail = true;
                return default; // sweep budget spent — the cache stands in
            }
        }
        var image = await Step<ImageSettings>(HttpOrOnvifImageAsync).ConfigureAwait(false);
        var volume = await Step<int?>(HttpVolumeAsync).ConfigureAwait(false);
        var wifi = await Step<WifiReading>(GetWifiSignalAsync).ConfigureAwait(false);
        var presets = await Step<IReadOnlyList<PtzPresetInfo>>(HttpPresetsAsync).ConfigureAwait(false);
        var replies = await Step<IReadOnlyList<QuickReplyFile>>(GetQuickRepliesAsync).ConfigureAwait(false);
        var autoTrack = await Step<bool?>(GetAutoTrackAsync).ConfigureAwait(false);
        var sdCards = await Step<IReadOnlyList<SdCardInfo>>(GetSdCardsAsync).ConfigureAwait(false);
        var mdSens = await Step<int?>(GetMdSensitivityAsync).ConfigureAwait(false);
        var aiSens = await Step<IReadOnlyList<AiSensitivity>>(HttpAiSensitivitiesAsync).ConfigureAwait(false);
        var osd = await Step<OsdSettings>(GetOsdSettingsAsync).ConfigureAwait(false);
        var audio = await Step<AudioState>(GetAudioStateAsync).ConfigureAwait(false);
        var fresh = new HttpFeatures(image, volume, wifi, presets, replies, autoTrack, sdCards, mdSens, aiSens, osd, audio);

        // Fill the holes from the last good sweep — live values always win; the
        // cache only stands in for sections this sweep couldn't read (including
        // ALL of them, when the backoff was already armed by an earlier failure
        // and every read no-opped: the panel then shows the last-known state
        // instead of going limited for no visible reason).
        var c = _httpFeaturesCache;
        var merged = c == null ? fresh : new HttpFeatures(
            fresh.Image ?? c.Image, fresh.Volume ?? c.Volume, fresh.WifiSignal ?? c.WifiSignal,
            fresh.PtzPresets ?? c.PtzPresets, fresh.QuickReplies ?? c.QuickReplies,
            fresh.AutoTrack ?? c.AutoTrack, fresh.SdCards ?? c.SdCards,
            fresh.MdSensitivity ?? c.MdSensitivity, fresh.AiSensitivities ?? c.AiSensitivities,
            fresh.Osd ?? c.Osd, fresh.Audio ?? c.Audio);
        bool freshContent = fresh.Image != null || fresh.Volume != null || fresh.WifiSignal != null
            || fresh.PtzPresets != null || fresh.QuickReplies != null || fresh.AutoTrack != null
            || fresh.SdCards != null || fresh.MdSensitivity != null || fresh.AiSensitivities != null
            || fresh.Osd != null || fresh.Audio != null;
        bool hasContent = freshContent
            || merged.Image != null || merged.Volume != null || merged.WifiSignal != null
            || merged.PtzPresets != null || merged.QuickReplies != null || merged.AutoTrack != null
            || merged.SdCards != null || merged.MdSensitivity != null || merged.AiSensitivities != null
            || merged.Osd != null || merged.Audio != null;
        if (freshContent) _httpFeaturesCache = merged;

        // Never fail silently. The panel shows every HTTP-backed section (picture,
        // volume, OSD, sensitivity…) blank when the sweep reads nothing — and with
        // fail-fast (backoff armed on the first step, the rest no-op) the streak
        // that HttpTryAsync warns on never climbs, so the user gets "no settings,
        // no error" and no idea why. If the whole sweep came back empty on a real
        // transport failure and no more specific warning already fired (a rejected
        // login explains itself; the streak warning covers slow flapping), say so
        // once — with the same cooldown, so a flapping camera doesn't spam.
        if (!freshContent && anyTransportFail && !_httpLoginWarned && !_httpUnreachableWarned
            && DateTime.UtcNow >= _httpWarnCooldownUntil)
        {
            _httpUnreachableWarned = true;
            _httpWarnCooldownUntil = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            Log.Warn($"{CameraName}: none of the camera's HTTP-API features answered — picture settings, " +
                     "OSD, Wi-Fi signal and motion sensitivity stay unavailable (volume, PTZ presets and AI " +
                     "sensitivity fall back to Baichuan). " +
                     "The camera streams fine over Baichuan, so this is the HTTP API specifically: it may be " +
                     "disabled on the camera (enable it in the Reolink app, or set 'http_address'), or " +
                     "something between Neolink and the camera is dropping HTTP traffic " +
                     "(firewall/VLAN rules, Docker networking). Reads resume automatically when it recovers." +
                     (_onvif != null
                         ? " (A separate ONVIF imaging fallback was also tried for the picture settings — " +
                           "see this camera's ONVIF log line for whether that route is available.)"
                         : ""));
        }
        return await WithBcStandInsAsync(merged, ct, budget.Token).ConfigureAwait(false);
    }

    public async Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct) =>
        await HttpOrOnvifImageAsync(ct).ConfigureAwait(false) ?? await BcImageAsync(ct).ConfigureAwait(false);

    private async Task<ImageSettings?> HttpOrOnvifImageAsync(CancellationToken ct)
    {
        var img = await HttpTryAsync<JsonObject?>(async c => await _httpApi!.GetImageAsync(c).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        // HTTP had nothing (no CGI API, or it failed) — fall back to ONVIF, the
        // only picture-settings path on models like the Lumus. A healthy HTTP
        // camera never reaches this: img is non-null and ONVIF stays untouched.
        if (img == null)
        {
            if (_onvif == null) return null;
            var onvif = await GetOnvifImageSettingsAsync(ct).ConfigureAwait(false);
            // Remember which transport actually served imaging, so a WRITE follows
            // the same road: the Lumus HAS an _httpApi (derived from its host) but
            // it doesn't answer, and a write to it would just fail.
            if (onvif != null)
            {
                _httpImagingWorks = false;
                _imageViaBc = false;
            }
            return onvif;
        }
        _httpImagingWorks = true;
        _imageViaBc = false;
        // The ISP half (day/night, flip, ...) is optional — picture sliders alone
        // are still worth showing if a firmware rejects GetIsp.
        var isp = await HttpTryAsync<JsonObject?>(async c => await _httpApi!.GetIspAsync(c).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        // The (one-time, cached) range read is the camera's own capability list
        // for ISP fields: it decides whether HDR is 2- or 3-state, and whether
        // flip/mirror exist at all — firmwares echo rotation/mirroring VALUES in
        // the config even on models that don't support them (Elite WiFi), so key
        // presence alone shows dead toggles.
        JsonObject? ispRange = isp != null
            ? await HttpTryAsync<JsonObject?>(async c => await IspRangeAsync(c).ConfigureAwait(false), ct).ConfigureAwait(false)
            : null;
        // The ability table outranks the range heuristic for flip/mirror: some
        // firmwares list rotation/mirroring in the RANGE too on models that can't
        // do either (field report: dead toggles on a camera the range gate was
        // built to fix), but their GetAbility carries ispFlip/ispMirror ver 0.
        JsonObject? ability = isp != null
            ? await HttpTryAsync<JsonObject?>(async c => await AbilityAsync(c).ConfigureAwait(false), ct).ConfigureAwait(false)
            : null;
        int channel = _httpApi!.ChannelId;
        return ParseImageSettings(img, isp, ispRange,
            AbilityFlag(ability, channel, "ispFlip"), AbilityFlag(ability, channel, "ispMirror"));
    }

    /// <summary>Picture settings from ONVIF, mapped into the same shape the HTTP path
    /// produces. ONVIF's imaging service covers brightness/contrast/saturation/
    /// sharpness, the IR-cut (day/night) filter and wide-dynamic-range; it has no hue,
    /// anti-flicker or flip/mirror, so those stay null (the panel hides absent
    /// fields). Never throws — null just means ONVIF had nothing.</summary>
    private async Task<ImageSettings?> GetOnvifImageSettingsAsync(CancellationToken ct)
    {
        // Radio silence for a camera sleeping on purpose — an ONVIF discovery
        // retry against a parked battery camera is exactly the traffic that
        // faked its wake pattern (2026-07-22).
        if (SleepingOnPurpose) return null;
        var o = await _onvif!.TryGetImagingAsync(ct).ConfigureAwait(false);
        if (o == null) return null;
        // HDR (WDR) is deliberately left null: its WRITE rides a separate endpoint
        // (SetHdrAsync) that has no ONVIF path yet, so surfacing it would show a
        // toggle that can't save. Brightness/contrast/saturation/sharpness and
        // day/night all round-trip through SetImageSettingsAsync → ONVIF.
        return new ImageSettings(
            Bright: o.Brightness, Contrast: o.Contrast, Saturation: o.Saturation,
            Hue: null, Sharpen: o.Sharpness,
            DayNight: IrCutToDayNight(o.IrCutFilter),
            AntiFlicker: null, Flip: null, Mirror: null,
            Hdr: null, HdrMax: null);
    }

    /// <summary>ONVIF IR-cut-filter enum → this app's day/night vocabulary. The filter
    /// engaged (ON) blocks IR for daytime colour; OFF lets IR through for night.</summary>
    internal static string? IrCutToDayNight(string? irCutFilter) => irCutFilter?.ToUpperInvariant() switch
    {
        "AUTO" => "Auto",
        "ON" => "Color",
        "OFF" => "Black&White",
        _ => null,
    };

    /// <summary>The reverse mapping, for an ONVIF write.</summary>
    internal static string? DayNightToIrCut(string? dayNight) => dayNight switch
    {
        "Auto" => "AUTO",
        "Color" => "ON",
        "Black&White" => "OFF",
        _ => null,
    };

    /// <summary>The GetIsp RANGE table, fetched once per camera lifetime (it's static
    /// per model): which optional ISP fields exist and the values they accept.</summary>
    private JsonObject? _ispRange;
    private bool _ispRangeLoaded;

    private async Task<JsonObject?> IspRangeAsync(CancellationToken ct)
    {
        if (_ispRangeLoaded) return _ispRange;
        var (_, range) = await _httpApi!.GetIspWithRangeAsync(ct).ConfigureAwait(false);
        _ispRange = range;
        _ispRangeLoaded = true;
        return range;
    }

    internal static ImageSettings ParseImageSettings(JsonObject img, JsonObject? isp, JsonObject? ispRange = null,
        bool? flipAbility = null, bool? mirrorAbility = null) => new(
        Bright: (int?)img["bright"], Contrast: (int?)img["contrast"],
        Saturation: (int?)img["saturation"], Hue: (int?)img["hue"], Sharpen: (int?)img["sharpen"],
        DayNight: (string?)isp?["dayNight"],
        AntiFlicker: (string?)isp?["antiFlicker"],
        Flip: IspFlag(isp, ispRange, "rotation", flipAbility),
        Mirror: IspFlag(isp, ispRange, "mirroring", mirrorAbility),
        Hdr: (int?)isp?["hdr"],
        HdrMax: HdrRangeMax(ispRange));

    /// <summary>An optional on/off ISP field, gated by capability. The ability
    /// table's verdict (ispFlip/ispMirror) is final when it answered — false hides
    /// the toggle no matter what the config echoes, true shows it. Without an
    /// ability verdict, the range table decides: when it was read, the field must
    /// appear IN it — firmwares echo rotation and mirroring values in the config
    /// on models that can't actually flip, and a toggle that silently no-ops is
    /// worse than none. Without either (older firmware, reads failed) value
    /// presence decides, as before.</summary>
    internal static bool? IspFlag(JsonObject? isp, JsonObject? ispRange, string key, bool? ability = null) =>
        ability == false ? null
        : isp?[key] is { } v && (ability == true || ispRange == null || ispRange[key] != null) ? (int?)v != 0 : null;

    /// <summary>The hdr field's maximum from an ISP range table: {"min":0,"max":N}
    /// on most firmwares, a bare option array on some. Null = range didn't say
    /// (callers treat the control as a plain on/off).</summary>
    internal static int? HdrRangeMax(JsonObject? ispRange) => ispRange?["hdr"] switch
    {
        JsonObject r => (int?)r["max"],
        JsonArray a => a.Count > 0 ? a.OfType<JsonValue>().Max(v => (int?)v ?? 0) : null,
        _ => null,
    };

    // MINIMAL write payloads ({"channel": n} + only the changed fields), matching
    // what Reolink's own clients send. Round-tripping the full Get object breaks:
    // it carries nested read-only structures (gain, shutter, ...) that firmwares
    // reject wholesale (observed on the Elite WiFi line).
    public async Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue, int? sharpen,
        string? dayNight, string? antiFlicker, bool? flip, bool? mirror, CancellationToken ct)
    {
        // Route the write down whichever transport served the read. ONVIF when the
        // camera has no HTTP API OR its HTTP API isn't answering imaging (the Lumus
        // has a dead HTTP API derived from its host) — provided ONVIF is available.
        bool useOnvif = _onvif != null && (_httpApi == null || _httpImagingWorks == false);
        // The write is CONFIRMED — fold it into the cache: flip/mirror restart
        // the camera's video pipeline, and a re-read racing that restart can
        // still echo the OLD value; the cache must not resurrect pre-write state
        // when the next sweep's fresh read fails.
        void FoldIntoCache()
        {
            if (_httpFeaturesCache?.Image is { } ci)
                _httpFeaturesCache = _httpFeaturesCache with
                {
                    Image = ci with
                    {
                        Bright = bright ?? ci.Bright, Contrast = contrast ?? ci.Contrast,
                        Saturation = saturation ?? ci.Saturation, Hue = hue ?? ci.Hue,
                        Sharpen = sharpen ?? ci.Sharpen,
                        DayNight = dayNight ?? ci.DayNight, AntiFlicker = antiFlicker ?? ci.AntiFlicker,
                        Flip = flip ?? ci.Flip, Mirror = mirror ?? ci.Mirror,
                    },
                };
        }
        if (_imageViaBc)
        {
            if (dayNight != null || antiFlicker != null || flip != null || mirror != null)
                throw new NotSupportedException($"{CameraName} takes only the picture sliders over Baichuan");
            await ModifyAsync(BcConstants.MsgIdGetVideoInput, BcConstants.MsgIdSetVideoInput, "VideoInput", el =>
            {
                foreach (var (name, value) in new[] { ("bright", bright), ("contrast", contrast),
                             ("saturation", saturation), ("hue", hue), ("sharpen", sharpen) })
                    if (value is { } v) SetChild(el, name, Math.Clamp(v, 0, 255).ToString());
                return el;
            }, "picture settings changed over Baichuan", ct).ConfigureAwait(false);
            FoldIntoCache();
            return;
        }
        if (useOnvif)
        {
            // ONVIF covers brightness/contrast/saturation/sharpness + day/night; the
            // fields it can't do (hue, anti-flicker, flip/mirror) aren't offered in
            // the panel for an ONVIF camera, so a request for them is a real error.
            if (hue != null || antiFlicker != null || flip != null || mirror != null)
                throw new NotSupportedException(
                    $"{CameraName} exposes picture settings over ONVIF, which can't set hue, anti-flicker or flip/mirror");
            await _onvif!.SetImagingAsync(bright, contrast, saturation, sharpen,
                DayNightToIrCut(dayNight), wideDynamicRange: null, ct).ConfigureAwait(false);
            FoldIntoCache();
            Log.Info($"{CameraName}: picture settings changed over ONVIF");
            return;
        }
        if (_httpApi == null)
            throw new NotSupportedException($"picture settings need the camera's HTTP API ('{CameraName}' has none)");
        if (bright != null || contrast != null || saturation != null || hue != null || sharpen != null)
        {
            static int Clamp(int v) => Math.Clamp(v, 0, 255);
            var img = new JsonObject { ["channel"] = _httpApi.ChannelId };
            if (bright is { } b) img["bright"] = Clamp(b);
            if (contrast is { } c) img["contrast"] = Clamp(c);
            if (saturation is { } s) img["saturation"] = Clamp(s);
            if (hue is { } h) img["hue"] = Clamp(h);
            if (sharpen is { } sh) img["sharpen"] = Clamp(sh);
            await _httpApi.SetImageAsync(img, ct).ConfigureAwait(false);
        }
        if (dayNight != null || antiFlicker != null || flip != null || mirror != null)
        {
            var isp = new JsonObject { ["channel"] = _httpApi.ChannelId };
            if (dayNight != null) isp["dayNight"] = dayNight;
            if (antiFlicker != null) isp["antiFlicker"] = antiFlicker;
            if (flip is { } fl) isp["rotation"] = fl ? 1 : 0;
            if (mirror is { } mi) isp["mirroring"] = mi ? 1 : 0;
            await _httpApi.SetIspAsync(isp, ct).ConfigureAwait(false);
        }
        FoldIntoCache();
        Log.Info($"{CameraName}: picture settings changed" +
                 $"{(flip != null || mirror != null ? " (flip/mirror restarts the video stream)" : "")}");
    }

    public async Task<int?> GetVolumeAsync(CancellationToken ct) =>
        await HttpVolumeAsync(ct).ConfigureAwait(false) ?? await BcVolumeAsync(ct).ConfigureAwait(false);

    private async Task<int?> HttpVolumeAsync(CancellationToken ct)
    {
        var v = await HttpTryAsync<int?>(async c =>
            (int?)(await _httpApi!.GetAudioCfgAsync(c).ConfigureAwait(false))["volume"], ct).ConfigureAwait(false);
        if (v != null) _volumeViaBc = false;
        return v;
    }

    /// <summary>The audio settings BEYOND the speaker volume, which models expose
    /// unevenly: the encode settings' record-audio flag, and whichever extra
    /// AudioCfg volumes this firmware carries. Absent members read as null and the
    /// UI simply doesn't draw them — the camera decides what's on offer.</summary>
    public Task<AudioState?> GetAudioStateAsync(CancellationToken ct) =>
        HttpTryAsync<AudioState>(async c =>
        {
            bool? rec = null;
            try { rec = EncRecordAudio(await _httpApi!.GetEncAsync(c).ConfigureAwait(false)); }
            catch (ReolinkApiException) { /* no Enc surface: flag stays hidden */ }
            int? talk = null, visitor = null;
            try
            {
                var cfg = await _httpApi!.GetAudioCfgAsync(c).ConfigureAwait(false);
                talk = (int?)cfg["talkAndReplyVolume"];
                visitor = (int?)cfg["visitorVolume"];
            }
            catch (ReolinkApiException) { /* no AudioCfg: volumes stay hidden */ }
            return rec == null && talk == null && visitor == null ? null : new AudioState(rec, talk, visitor);
        }, ct);

    /// <summary>The record-audio state from the encode settings. The flag lives at
    /// the TOP of the Enc object (one switch for the camera — what the app shows);
    /// a per-stream fallback covers firmwares that nest it instead. Null = this
    /// firmware has no audio flag at all.</summary>
    internal static bool? EncRecordAudio(JsonObject enc)
    {
        if (enc["audio"] is { } flag) return ((int?)flag ?? 0) != 0;
        bool? any = null;
        foreach (var key in new[] { "mainStream", "subStream", "extStream" })
        {
            if (enc[key] is not JsonObject s || s["audio"] is not { } sFlag) continue;
            any = (any ?? false) || ((int?)sFlag ?? 0) != 0;
        }
        return any;
    }

    /// <summary>Camera-side record-audio switch: puts the microphone into (or
    /// strips it from) every stream the camera encodes — which is what lands in
    /// recordings here, in the Reolink app and on the SD card alike.</summary>
    public async Task SetRecordAudioAsync(bool on, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"record audio needs the camera's HTTP API ('{CameraName}' has none)");
        var enc = await _httpApi.GetEncAsync(ct).ConfigureAwait(false);
        bool touched = false;
        if (enc["audio"] is not null) // the flag's documented home: top of Enc
        {
            enc["audio"] = on ? 1 : 0;
            touched = true;
        }
        foreach (var key in new[] { "mainStream", "subStream", "extStream" })
        {
            if (enc[key] is not JsonObject s || s["audio"] is null) continue;
            s["audio"] = on ? 1 : 0;
            touched = true;
        }
        if (!touched)
            throw new NotSupportedException($"{CameraName} exposes no record-audio flag in its encode settings");
        await _httpApi.SetEncAsync(enc, ct).ConfigureAwait(false);
        if (_httpFeaturesCache?.Audio is { } ca)
            _httpFeaturesCache = _httpFeaturesCache with { Audio = ca with { RecordAudio = on } };
        Log.Info($"{CameraName}: record audio {(on ? "enabled" : "disabled")} on every stream — " +
                 "the camera may briefly restart its streams to apply");
    }

    /// <summary>Writes whichever extra AudioCfg volumes were staged (0–100 each);
    /// minimal payload like the picture writes — see SetImageSettingsAsync.</summary>
    public async Task SetAudioVolumesAsync(int? talkVolume, int? visitorVolume, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"audio volumes need the camera's HTTP API ('{CameraName}' has none)");
        var cfg = new JsonObject { ["channel"] = _httpApi.ChannelId };
        if (talkVolume is { } tv) cfg["talkAndReplyVolume"] = Math.Clamp(tv, 0, 100);
        if (visitorVolume is { } vv) cfg["visitorVolume"] = Math.Clamp(vv, 0, 100);
        if (cfg.Count == 1) return; // nothing staged
        await _httpApi.SetAudioCfgAsync(cfg, ct).ConfigureAwait(false);
        if (_httpFeaturesCache?.Audio is { } ca)
            _httpFeaturesCache = _httpFeaturesCache with
            {
                Audio = ca with { TalkVolume = talkVolume ?? ca.TalkVolume, VisitorVolume = visitorVolume ?? ca.VisitorVolume },
            };
        Log.Info($"{CameraName}: audio volumes changed" +
                 $"{(talkVolume != null ? $" talk={Math.Clamp(talkVolume.Value, 0, 100)}" : "")}" +
                 $"{(visitorVolume != null ? $" visitor={Math.Clamp(visitorVolume.Value, 0, 100)}" : "")}");
    }

    public async Task SetVolumeAsync(int volume, CancellationToken ct)
    {
        int vol = Math.Clamp(volume, 0, 100);
        if (_httpApi == null || _volumeViaBc)
        {
            await ModifyAsync(BcConstants.MsgIdGetAudioCfg, BcConstants.MsgIdSetAudioCfg, "audioCfg",
                el => { SetChild(el, "volume", vol.ToString()); return el; },
                $"speaker volume set to {vol}", ct).ConfigureAwait(false);
            return;
        }
        // Minimal payload, like the picture writes — see SetImageSettingsAsync.
        var cfg = new JsonObject { ["channel"] = _httpApi.ChannelId, ["volume"] = vol };
        await _httpApi.SetAudioCfgAsync(cfg, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: speaker volume set to {vol}");
    }

    public async Task<WifiReading?> GetWifiSignalAsync(CancellationToken ct)
    {
        if (_httpApi != null
            && await HttpTryAsync<int?>(async c => await _httpApi!.GetWifiSignalAsync(c).ConfigureAwait(false), ct)
                .ConfigureAwait(false) is { } viaHttp)
        {
            var http = WifiReading.FromHttp(viaHttp);
            Log.Debug($"{CameraName}: Wi-Fi via HTTP API — raw {viaHttp} read as {http.Unit} ({http.Label}), level {http.Level}/4");
            return http;
        }
        // Baichuan msg 115 <WifiSignal>: the protocol's own query, always in dBm —
        // the source for cameras without the HTTP API. Rides the live connection
        // only; a parked battery camera reads as offline and is never woken for it.
        // Wired-ethernet cameras answer with nothing, so the query retires after a
        // few misses rather than asking forever.
        //
        // The give-up counter describes THIS session's firmware behaviour, so a new
        // session clears it: a camera that merely happened to be reconnecting during
        // those misses used to be written off for the life of the process.
        if (AnyLive() is { } live)
        {
            if (!(_bcWifiTriedOn?.TryGetTarget(out var last) == true && ReferenceEquals(last, live)))
            {
                _bcWifiTriedOn = new WeakReference<IBcCamera>(live);
                _bcWifiMisses = 0;
            }
        }
        if (_bcWifiMisses >= BcWifiGiveUp) return null;
        try
        {
            var el = await WithCameraAsync(c => c.GetWifiSignalAsync(ct: ct), ct).ConfigureAwait(false);
            var push = el == null ? null : BcCamera.ParseNetInfo(el);
            if (push?.SignalDbm is { } dbm)
            {
                _bcWifiMisses = 0;
                var bc = WifiReading.FromDbm(dbm);
                Log.Debug($"{CameraName}: Wi-Fi via Baichuan msg 115 — {bc.Label}, level {bc.Level}/4");
                return bc;
            }
            // The camera answered, just not with a signal: if it named its link
            // type, that settles it — a wired camera is not a Wi-Fi camera with a
            // missing reading, and it should never be asked again.
            var kind = LinkKindOf(push?.NetType);
            if (kind != 0)
            {
                _linkKind = kind;
                Log.Debug($"{CameraName}: link type from Baichuan msg 115 — '{push!.NetType}'");
                return null;
            }
            _bcWifiMisses++;
            Log.Debug($"{CameraName}: Wi-Fi — Baichuan msg 115 returned no signal " +
                      $"({_bcWifiMisses}/{BcWifiGiveUp} before this session stops asking)");
            return null;
        }
        catch (CameraOfflineException)
        {
            return null; // parked/reconnecting — not evidence the query is unsupported
        }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException or IOException)
        {
            _bcWifiMisses++;
            Log.Debug($"{CameraName}: Wi-Fi — Baichuan msg 115 failed: {ex.Message} " +
                      $"({_bcWifiMisses}/{BcWifiGiveUp})");
            return null;
        }
    }

    // Consecutive Baichuan Wi-Fi query misses on the CURRENT session; at
    // BcWifiGiveUp the camera is taken as wired (or the firmware lacks msg 115)
    // and the query stops until the next connection.
    private int _bcWifiMisses;
    private WeakReference<IBcCamera>? _bcWifiTriedOn;
    private const int BcWifiGiveUp = 3;

    // Cheap Wi-Fi cache for the camera-list sidebar. The list poll must not do a
    // per-camera round-trip, so it reads _cachedWifi — falling back to whatever the
    // last full HTTP-features sweep saw — and fires a throttled background refresh.
    private volatile WifiReading? _cachedWifi;
    private DateTime _wifiWarmedAt;
    private static readonly TimeSpan WifiWarmInterval = TimeSpan.FromSeconds(30);

    public WifiReading? CachedWifiSignal => _cachedWifi ?? _httpFeaturesCache?.WifiSignal;

    // Which link the camera is on: 0 unknown, 1 wired, 2 Wi-Fi. Learned from the
    // camera's own answer (GetLocalLink) or implied by a real Wi-Fi reading.
    private volatile int _linkKind;
    public bool? CachedWired => _linkKind == 0 ? null : _linkKind == 1;

    /// <summary>Drops the cached reading — called when the camera's session ends, so
    /// an unreachable camera can't keep showing hours-old signal as if it were live.
    /// The link TYPE survives: a camera doesn't change cable for Wi-Fi while offline,
    /// and re-learning it costs a round-trip.</summary>
    public void ForgetWifiSignal()
    {
        _cachedWifi = null;
        _wifiWarmedAt = default; // read again as soon as it is back
    }

    public async Task WarmWifiSignalAsync(CancellationToken ct)
    {
        // A dozing battery camera keeps its last reading — warming it would put
        // packets on the air at the exact moment the wake scan needs radio quiet.
        if (SleepingOnPurpose) return;
        // Throttle: one read per interval regardless of how often the list is polled.
        if (DateTime.UtcNow - _wifiWarmedAt < WifiWarmInterval) return;
        _wifiWarmedAt = DateTime.UtcNow;
        try
        {
            // Ask which link is active before asking about signal strength: a camera
            // on a cable has no Wi-Fi reading to give, so the UI shows it as wired
            // instead of blank, and we stop pestering it for a signal.
            if (_linkKind == 0 && _httpApi != null
                && await HttpTryAsync<string?>(c => _httpApi!.GetActiveLinkAsync(c), ct).ConfigureAwait(false)
                    is { Length: > 0 } active)
            {
                _linkKind = LinkKindOf(active);
                Log.Debug($"{CameraName}: active network link reported as '{active}'");
            }
            if (_linkKind == 1) { _cachedWifi = null; return; }

            if (await GetWifiSignalAsync(ct).ConfigureAwait(false) is { } signal)
            {
                _cachedWifi = signal;
                _linkKind = 2; // it answered with a signal: it is on Wi-Fi
            }
        }
        catch { /* best-effort UI value — a failed read just leaves the last one */ }
    }

    /// <summary>Maps a camera's link wording to 1 (wired) or 2 (Wi-Fi); 0 when it is
    /// neither recognisable form. Reolink says "LAN"/"Wifi" over HTTP and
    /// "ethernet"/"wifi" in Baichuan NetInfo pushes, with firmware-dependent case.</summary>
    internal static int LinkKindOf(string? s)
    {
        // Matched as PREFIXES of the whole value, not substrings: these fields are
        // enum-like ("LAN", "Wifi", "eth0", "wlan0"), and a loose contains-match
        // reads "eth" out of ordinary words and would label a camera wired on the
        // strength of a coincidence. Wi-Fi is tested first — "wlan" contains "lan".
        var v = s?.Trim();
        if (string.IsNullOrEmpty(v)) return 0;
        bool Starts(params string[] prefixes) =>
            prefixes.Any(p => v.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (Starts("wifi", "wi-fi", "wlan", "wireless")) return 2;
        if (Starts("lan", "eth", "wired", "cable")) return 1;
        return 0;
    }

    public async Task<IReadOnlyList<PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct) =>
        await HttpPresetsAsync(ct).ConfigureAwait(false) ?? await BcPresetsAsync(ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<PtzPresetInfo>?> HttpPresetsAsync(CancellationToken ct)
    {
        var list = await HttpTryAsync<IReadOnlyList<PtzPresetInfo>?>(
            async c => ParsePtzPresets(await _httpApi!.GetPtzPresetsAsync(c).ConfigureAwait(false)), ct).ConfigureAwait(false);
        if (list != null) _presetsViaBc = false;
        return list;
    }

    internal static IReadOnlyList<PtzPresetInfo> ParsePtzPresets(JsonArray presets) =>
        presets.OfType<JsonObject>()
            .Where(p => (int?)p["id"] is >= 0)
            .Select(p => new PtzPresetInfo(
                Id: (int)p["id"]!,
                Name: (string?)p["name"] ?? $"preset {(int)p["id"]!}",
                Enabled: ((int?)p["enable"] ?? 0) != 0))
            .OrderBy(p => p.Id)
            .ToList();

    public async Task PtzToPresetAsync(int id, CancellationToken ct)
    {
        if (_httpApi == null || _presetsViaBc)
            await BcPresetCommandAsync(id, "toPos", null, ct).ConfigureAwait(false);
        else
            await _httpApi.PtzToPresetAsync(id, speed: 32, ct).ConfigureAwait(false);
        PtzCommandSent?.Invoke("preset");
        Log.Info($"{CameraName}: moving to PTZ preset {id}");
    }

    public async Task SavePtzPresetAsync(int id, string name, CancellationToken ct)
    {
        if (_httpApi == null || _presetsViaBc)
            await BcPresetCommandAsync(id, "setPos", name, ct).ConfigureAwait(false);
        else
            await _httpApi.SetPtzPresetAsync(id, name, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: current position saved as PTZ preset {id} (\"{name}\")");
    }

    public Task<IReadOnlyList<QuickReplyFile>?> GetQuickRepliesAsync(CancellationToken ct) =>
        HttpTryAsync<IReadOnlyList<QuickReplyFile>?>(
            async c => ParseQuickReplies(await _httpApi!.GetAudioFileListAsync(c).ConfigureAwait(false)), ct);

    internal static IReadOnlyList<QuickReplyFile> ParseQuickReplies(JsonArray files) =>
        files.OfType<JsonObject>()
            .Where(f => (int?)f["id"] is >= 0 && !string.IsNullOrWhiteSpace((string?)f["fileName"]))
            .Select(f => new QuickReplyFile((int)f["id"]!, ((string)f["fileName"]!).Trim()))
            .ToList();

    public async Task PlayQuickReplyAsync(int id, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"quick replies need the camera's HTTP API ('{CameraName}' has none)");
        await _httpApi.QuickReplyPlayAsync(id, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: playing quick reply {id}");
    }

    public Task<AutoReplyState?> GetAutoReplyAsync(CancellationToken ct) =>
        HttpTryAsync<AutoReplyState?>(
            async c => ParseAutoReply(await _httpApi!.GetAutoReplyAsync(c).ConfigureAwait(false)), ct);

    internal static AutoReplyState? ParseAutoReply(JsonObject ar) =>
        (int?)ar["fileId"] is { } fileId
            ? new AutoReplyState(fileId, (int?)ar["timeout"] ?? 0)
            : null;

    public async Task SetAutoReplyAsync(int? fileId, int? timeoutSeconds, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"the auto-reply needs the camera's HTTP API ('{CameraName}' has none)");
        // Read-modify-write: the object is small and flat, and keeping the camera's
        // other fields (enable et al.) verbatim is exactly what we want here.
        var ar = await _httpApi.GetAutoReplyAsync(ct).ConfigureAwait(false);
        if (fileId is { } f)
        {
            ar["fileId"] = f;
            // Firmwares that carry a separate enable flag expect it to follow.
            if (ar["enable"] != null) ar["enable"] = f >= 0 ? 1 : 0;
        }
        if (timeoutSeconds is { } t) ar["timeout"] = Math.Clamp(t, 1, 60);
        await _httpApi.SetAutoReplyAsync(ar, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: auto-reply set (fileId={fileId}, timeout={timeoutSeconds}s)");
    }

    /// <summary>The camera's GetAbility table, fetched once per camera lifetime —
    /// it is static per device+account, and it is the authoritative "does this
    /// model actually have the feature" source. Config reads are NOT trusted for
    /// capability: firmwares echo fields (aiTrack, rotation, mirroring) on models
    /// without the feature (observed on the Elite WiFi line).</summary>
    private JsonObject? _ability;
    private bool _abilityLoaded;

    private async Task<JsonObject?> AbilityAsync(CancellationToken ct)
    {
        if (_abilityLoaded) return _ability;
        var ability = await _httpApi!.GetAbilityAsync(ct).ConfigureAwait(false);
        _ability = ability;
        _abilityLoaded = true;
        return ability;
    }

    private async Task<bool> AiTrackSupportedAsync(CancellationToken ct)
    {
        var ability = await AbilityAsync(ct).ConfigureAwait(false);
        return ability != null && SupportsAiTrack(ability, _httpApi!.ChannelId);
    }

    /// <summary>supportAITrack ver &gt; 0 in the channel's ability block (falling back
    /// to the first block — standalone cameras have exactly one).</summary>
    internal static bool SupportsAiTrack(JsonObject ability, int channel)
    {
        return (int?)AbilityChannel(ability, channel)?["supportAITrack"]?["ver"] is > 0;
    }

    private static JsonObject? AbilityChannel(JsonObject ability, int channel)
    {
        var chn = ability["abilityChn"] as JsonArray;
        return (chn?.ElementAtOrDefault(channel) ?? chn?.OfType<JsonObject>().FirstOrDefault()) as JsonObject;
    }

    /// <summary>A tri-state feature verdict from the ability table: true/false when
    /// the channel's block answered (a missing key means "not this model" — the
    /// table lists every feature the firmware knows), null when there is no table
    /// to ask (ability read failed) and the caller must fall back to heuristics.</summary>
    internal static bool? AbilityFlag(JsonObject? ability, int channel, string key) =>
        ability == null ? null
        : AbilityChannel(ability, channel) is { } entry ? (int?)entry[key]?["ver"] is > 0
        : null;

    public Task<bool?> GetAutoTrackAsync(CancellationToken ct) =>
        HttpTryAsync<bool?>(async c =>
        {
            if (!await AiTrackSupportedAsync(c).ConfigureAwait(false)) return null;
            var (cfg, _) = await _httpApi!.GetAiCfgAsync(c).ConfigureAwait(false);
            return AutoTrackValue(cfg);
        }, ct);

    /// <summary>The auto-track flag from an AiCfg — firmwares name it "aiTrack"
    /// or "bSmartTrack"; null when the config carries neither (no tracking).</summary>
    internal static bool? AutoTrackValue(JsonObject cfg) =>
        (int?)cfg["aiTrack"] is { } v1 ? v1 != 0
        : (int?)cfg["bSmartTrack"] is { } v2 ? v2 != 0
        : null;

    public async Task SetAutoTrackAsync(bool on, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"auto-tracking needs the camera's HTTP API ('{CameraName}' has none)");
        // The same ability gate as the read: never write a tracking config to a
        // camera whose ability table doesn't advertise the feature.
        if (!await AiTrackSupportedAsync(ct).ConfigureAwait(false))
            throw new NotSupportedException($"{CameraName} does not support auto-tracking");
        // Read-modify-write, toggling whichever key THIS firmware uses.
        var (cfg, wrapped) = await _httpApi.GetAiCfgAsync(ct).ConfigureAwait(false);
        if (cfg["aiTrack"] != null) cfg["aiTrack"] = on ? 1 : 0;
        else if (cfg["bSmartTrack"] != null) cfg["bSmartTrack"] = on ? 1 : 0;
        else throw new NotSupportedException($"{CameraName} reports no auto-tracking setting");
        await _httpApi.SetAiCfgAsync(cfg, wrapped, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: auto-tracking {(on ? "on" : "off")}");
    }

    public Task<IReadOnlyList<SdCardInfo>?> GetSdCardsAsync(CancellationToken ct) =>
        HttpTryAsync<IReadOnlyList<SdCardInfo>?>(
            async c => ParseSdCards(await _httpApi!.GetHddInfoAsync(c).ConfigureAwait(false)), ct);

    /// <summary>GetHddInfo entries: "capacity" = total MB, "size" = remaining MB.</summary>
    internal static IReadOnlyList<SdCardInfo> ParseSdCards(JsonArray slots) =>
        slots.OfType<JsonObject>()
            .Select((s, i) => new SdCardInfo(
                Id: (int?)s["id"] ?? i,
                // Tolerant reads: the same firmwares that quote SD "size" (below) may
                // quote these too.
                TotalMb: AsLong(s["capacity"]),
                FreeMb: AsLong(s["size"]),
                Formatted: ((int?)s["format"] ?? 0) != 0,
                Mounted: ((int?)s["mount"] ?? 0) != 0))
            .ToList();

    // ------------------------------------------- detection sensitivity (HTTP)

    public Task<int?> GetMdSensitivityAsync(CancellationToken ct) =>
        HttpTryAsync<int?>(async c =>
        {
            var (cfg, isMdAlarm) = await _httpApi!.GetMdConfigAsync(c).ConfigureAwait(false);
            return MdSensitivityValue(cfg, isMdAlarm);
        }, ct);

    /// <summary>Normalizes the two firmware dialects to 1-50, higher = more sensitive.
    /// MdAlarm/newSens carries the user-facing value; the old sens tables carry the
    /// INVERSE (wire 1 = most sensitive), hence the 51 - x.</summary>
    internal static int? MdSensitivityValue(JsonObject cfg, bool isMdAlarm)
    {
        if (isMdAlarm && ((int?)cfg["useNewSens"] ?? 0) != 0 && cfg["newSens"] is JsonObject ns)
            return (int?)ns["sensDef"] is { } def ? Math.Clamp(def, 1, 50) : null;
        var first = (cfg["sens"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        return (int?)first?["sensitivity"] is { } inv ? Math.Clamp(51 - inv, 1, 50) : null;
    }

    public async Task SetMdSensitivityAsync(int sensitivity, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"motion sensitivity needs the camera's HTTP API ('{CameraName}' has none)");
        int v = Math.Clamp(sensitivity, 1, 50);
        // Read-modify-write in whichever dialect the camera speaks; every time-slot
        // entry follows the new value, matching what the Reolink app does.
        if (_mdWriteMode != MdWriteMode.Legacy)
        {
            var (cfg, isMdAlarm) = await _httpApi.GetMdConfigAsync(ct).ConfigureAwait(false);
            if (!ApplyMdSensitivity(cfg, isMdAlarm, v))
                throw new NotSupportedException($"{CameraName} reports no motion-sensitivity table");
            if (!isMdAlarm)
            {
                // The read already fell back to the legacy object; there is no
                // second dialect below it, so a rejection here is the answer.
                await _httpApi.SetMdConfigAsync(cfg, isMdAlarm: false, ct).ConfigureAwait(false);
                Log.Info($"{CameraName}: motion sensitivity set to {v}/50");
                return;
            }
            if (_mdWriteMode != MdWriteMode.Minimal)
            {
                try
                {
                    await _httpApi.SetMdConfigAsync(cfg, isMdAlarm: true, ct).ConfigureAwait(false);
                    _mdWriteMode = MdWriteMode.Full;
                    Log.Info($"{CameraName}: motion sensitivity set to {v}/50");
                    return;
                }
                catch (ReolinkApiException ex)
                {
                    // Some firmwares answer GetMdAlarm but refuse their own config
                    // written back whole (the Video Doorbell line answers "param
                    // error"; others cannot re-parse it, likely over the zone
                    // table's size). The Reolink app writes a PARTIAL object.
                    Log.Info($"{CameraName}: SetMdAlarm rejected ({ex.Message}) — trying the app-style minimal object");
                }
            }
            try
            {
                await _httpApi.SetMdConfigAsync(
                    MinimalMdWrite(cfg, "useNewSens", "newSens", "sens"), isMdAlarm: true, ct).ConfigureAwait(false);
                _mdWriteMode = MdWriteMode.Minimal;
                Log.Info($"{CameraName}: motion sensitivity set to {v}/50 (minimal write)");
                return;
            }
            catch (ReolinkApiException ex)
            {
                Log.Info($"{CameraName}: minimal SetMdAlarm rejected too ({ex.Message}) — trying the legacy Alarm dialect");
            }
        }
        var legacy = await _httpApi.TryGetLegacyMdConfigAsync(ct).ConfigureAwait(false)
            ?? throw new NotSupportedException($"{CameraName} rejects the motion-config write in every firmware dialect");
        if (!ApplyMdSensitivity(legacy, isMdAlarm: false, v))
            throw new NotSupportedException($"{CameraName} reports no motion-sensitivity table");
        await _httpApi.SetMdConfigAsync(legacy, isMdAlarm: false, ct).ConfigureAwait(false);
        _mdWriteMode = MdWriteMode.Legacy;
        Log.Info($"{CameraName}: motion sensitivity set to {v}/50 (legacy dialect)");
    }

    /// <summary>The app-style partial write: channel plus only the named sections,
    /// deep-cloned from the (modified) full config. Firmwares that reject their own
    /// config round-tripped whole accept this shape — it is what the app sends.</summary>
    internal static JsonObject MinimalMdWrite(JsonObject cfg, params string[] sections)
    {
        var o = new JsonObject();
        if (cfg["channel"] is { } ch) o["channel"] = ch.DeepClone();
        foreach (var key in sections)
            if (cfg[key] is { } n) o[key] = n.DeepClone();
        return o;
    }

    internal static bool ApplyMdSensitivity(JsonObject cfg, bool isMdAlarm, int value)
    {
        if (isMdAlarm && ((int?)cfg["useNewSens"] ?? 0) != 0 && cfg["newSens"] is JsonObject ns)
        {
            ns["sensDef"] = value;
            if (ns["sens"] is JsonArray slots)
                foreach (var s in slots.OfType<JsonObject>()) s["sensitivity"] = value;
            return true;
        }
        if (cfg["sens"] is JsonArray old && old.OfType<JsonObject>().Any())
        {
            foreach (var s in old.OfType<JsonObject>()) s["sensitivity"] = 51 - value;
            return true;
        }
        return false;
    }

    /// <summary>Every ai_type any firmware is known to answer GetAiAlarm for.</summary>
    internal static readonly string[] AiAlarmTypes = { "people", "vehicle", "dog_cat", "face", "package" };

    /// <summary>Types THIS camera answered for, discovered on the first full sweep —
    /// later reads skip the rejected ones instead of re-asking every panel open.</summary>
    private IReadOnlyList<string>? _aiAlarmTypes;

    public async Task<IReadOnlyList<AiSensitivity>?> GetAiSensitivitiesAsync(CancellationToken ct) =>
        await HttpAiSensitivitiesAsync(ct).ConfigureAwait(false) ?? await BcAiSensitivitiesAsync(ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<AiSensitivity>?> HttpAiSensitivitiesAsync(CancellationToken ct)
    {
        if (_httpApi == null) return null;
        var list = new List<AiSensitivity>();
        foreach (var type in _aiAlarmTypes ?? AiAlarmTypes)
        {
            var one = await HttpTryAsync<AiSensitivity?>(async c =>
            {
                var cfg = await _httpApi!.GetAiAlarmAsync(type, c).ConfigureAwait(false);
                // The same reply says whether this type carries its OWN grid. Most
                // cameras keep one zone for everything, so noting it here — free,
                // in a sweep that already ran — is what stops the editor offering
                // a per-type tab that would only ever report "no zone".
                NoteAiZoneSupport(type, ParseZone(type, cfg) != null);
                return ParseAiSensitivity(type, cfg);
            }, ct).ConfigureAwait(false);
            if (one != null) list.Add(one);
            // Transport backoff armed mid-sweep — the rest would no-op anyway.
            if (DateTime.UtcNow < _httpRetryAt) return list.Count > 0 ? list : null;
        }
        if (list.Count == 0) return null;
        _aiAlarmTypes ??= list.Select(a => a.Type).ToList();
        _aiViaBc = false;
        return list;
    }

    internal static AiSensitivity? ParseAiSensitivity(string type, JsonObject cfg) =>
        (int?)cfg["sensitivity"] is { } sens
            ? new AiSensitivity(type, Math.Clamp(sens, 0, 100), (int?)cfg["stay_time"])
            : null;

    public async Task SetAiSensitivityAsync(string aiType, int sensitivity, CancellationToken ct)
    {
        if (!AiAlarmTypes.Contains(aiType))
            throw new ArgumentException($"unknown AI type '{aiType}'");
        int v = Math.Clamp(sensitivity, 0, 100);
        if (_httpApi == null || _aiViaBc)
        {
            await SetBcAiSensitivityAsync(aiType, v, ct).ConfigureAwait(false);
            return;
        }
        // Read-modify-write of the full AiAlarm object: it carries channel/ai_type
        // and the target-size bounds the firmware expects to see again.
        var cfg = await _httpApi.GetAiAlarmAsync(aiType, ct).ConfigureAwait(false);
        cfg["sensitivity"] = v;
        await _httpApi.SetAiAlarmAsync(cfg, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: {aiType} detection sensitivity set to {v}/100");
    }

    // ------------------------------------------- Baichuan stand-ins for HTTP settings
    // Picture (26/25), presets (190/19), volume (264/265) and AI sensitivity (342/343),
    // for a camera whose HTTP API (and, for the picture, ONVIF) never served them.

    /// <summary>Set when Baichuan served the section, so its writes go the same way.</summary>
    private volatile bool _imageViaBc, _presetsViaBc, _volumeViaBc, _aiViaBc;

    /// <summary>Stand-in reads the current session refused or ignored; a new session asks again.</summary>
    private readonly HashSet<string> _bcRefused = new(StringComparer.Ordinal);
    private IBcCamera? _bcRefusedBy;

    /// <summary>AI types the firmware's AiDetectCfg parser recognises.</summary>
    internal static readonly string[] BcAiTypes = { "people", "vehicle", "dog_cat", "face" };

    /// <summary>Fills the sections HTTP left empty from Baichuan, within <paramref name="limit"/>.</summary>
    private async Task<HttpFeatures> WithBcStandInsAsync(HttpFeatures f, CancellationToken ct,
        CancellationToken limit = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, limit);
        try
        {
            if (f.Image == null && await BcImageAsync(cts.Token).ConfigureAwait(false) is { } i)
                f = f with { Image = i };
            if (f.Volume == null && await BcVolumeAsync(cts.Token).ConfigureAwait(false) is { } v)
                f = f with { Volume = v };
            if (f.PtzPresets == null && await BcPresetsAsync(cts.Token).ConfigureAwait(false) is { } p)
                f = f with { PtzPresets = p };
            if (f.AiSensitivities == null && await BcAiSensitivitiesAsync(cts.Token).ConfigureAwait(false) is { } a)
                f = f with { AiSensitivities = a };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The sweep's budget ran out; what was read stands.
        }
        return f;
    }

    /// <summary>One stand-in read on the live session, or null. A refusal retires <paramref name="key"/>
    /// for the session; silence retires its whole family (the part before ':').</summary>
    private async Task<XElement?> BcStandInAsync(uint msgId, string root, string key,
        Func<CameraCapabilities, IBcCamera, bool> supported, Func<byte, XElement?>? body, CancellationToken ct)
    {
        if (AnyLive() == null) return null;
        CameraCapabilities caps;
        try { caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException) { return null; }
        var family = key.Split(':')[0];
        try
        {
            return await WithCameraAsync<XElement?>(async camera =>
            {
                if (!ReferenceEquals(_bcRefusedBy, camera))
                {
                    _bcRefused.Clear();
                    _bcRefusedBy = camera;
                }
                if (_bcRefused.Contains(key) || _bcRefused.Contains(family) || !supported(caps, camera)) return null;
                try
                {
                    var el = await camera.GetRawAsync(msgId, root, body?.Invoke(camera.ChannelId), ExtrasTimeout, ct)
                        .ConfigureAwait(false);
                    if (el == null) _bcRefused.Add(key);
                    return el;
                }
                catch (CameraCommandException)
                {
                    _bcRefused.Add(key);
                    return null;
                }
                catch (TimeoutException)
                {
                    _bcRefused.Add(family);
                    return null;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && ex is CameraOfflineException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task<ImageSettings?> BcImageAsync(CancellationToken ct)
    {
        var vi = await BcStandInAsync(BcConstants.MsgIdGetVideoInput, "VideoInput", "image",
            (_, _) => true, null, ct).ConfigureAwait(false);
        if (vi == null || ParseVideoInput(vi) is not { } image) return null;
        _imageViaBc = true;
        return image;
    }

    /// <summary>&lt;VideoInput&gt;: the five picture sliders (0-255); the ISP half isn't in it.</summary>
    internal static ImageSettings? ParseVideoInput(XElement vi) =>
        XInt(vi, "bright") is { } b
            ? new ImageSettings(b, XInt(vi, "contrast"), XInt(vi, "saturation"), XInt(vi, "hue"), XInt(vi, "sharpen"),
                DayNight: null, AntiFlicker: null, Flip: null, Mirror: null)
            : null;

    private async Task<int?> BcVolumeAsync(CancellationToken ct)
    {
        var cfg = await BcStandInAsync(BcConstants.MsgIdGetAudioCfg, "audioCfg", "volume",
            (caps, cam) => !ChannelSupportFlag(caps.Support, cam.ChannelId, "noAudio")
                           && (caps.Support?.Element("audioCfg") == null || SupportFlag(caps.Support, "audioCfg")),
            null, ct).ConfigureAwait(false);
        if (XInt(cfg, "volume") is not { } v) return null;
        _volumeViaBc = true;
        return Math.Clamp(v, 0, 100);
    }

    private async Task<IReadOnlyList<PtzPresetInfo>?> BcPresetsAsync(CancellationToken ct)
    {
        var el = await BcStandInAsync(BcConstants.MsgIdGetPtzPreset, "PtzPreset", "presets",
            (caps, cam) => caps.Features.Ptz || ChannelSupportValue(caps.Support, cam.ChannelId, "ptzPreset") > 0,
            null, ct).ConfigureAwait(false);
        if (el == null) return null;
        _presetsViaBc = true;
        return ParseBcPresets(el);
    }

    private async Task<IReadOnlyList<AiSensitivity>?> BcAiSensitivitiesAsync(CancellationToken ct)
    {
        var list = new List<AiSensitivity>();
        foreach (var type in BcAiTypes)
        {
            var cfg = await BcStandInAsync(BcConstants.MsgIdGetAiDetectCfg, "AiDetectCfg", "ai:" + type,
                (caps, cam) => ChannelSupportValue(caps.Support, cam.ChannelId, "aitype") > 0,
                ch => BuildAiDetectRead(ch, type), ct).ConfigureAwait(false);
            if (cfg != null && ParseBcAiSensitivity(type, cfg) is { } one) list.Add(one);
        }
        if (list.Count == 0) return null;
        _aiViaBc = true;
        return list;
    }

    private Task BcPresetCommandAsync(int id, string command, string? name, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SendCommandAsync(BcConstants.MsgIdPtzPreset,
                BcXmlBody.FromRaw(BuildPresetCommand(camera.ChannelId, id, command, name)),
                new ExtensionXml { ChannelId = camera.ChannelId }, ct: ct).ConfigureAwait(false);
            return null;
        }, ct);

    /// <summary>AiDetectCfg is read per type and written back whole: it carries the zone grid.</summary>
    private Task SetBcAiSensitivityAsync(string aiType, int sensitivity, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            var cfg = await camera.GetRawAsync(BcConstants.MsgIdGetAiDetectCfg, "AiDetectCfg",
                          BuildAiDetectRead(camera.ChannelId, aiType), ExtrasTimeout, ct).ConfigureAwait(false)
                      ?? throw new NotSupportedException($"the camera reports no {aiType} detection settings");
            SetChild(cfg, "sensitivity", sensitivity.ToString());
            await camera.SetRawAsync(BcConstants.MsgIdSetAiDetectCfg, cfg, ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: {aiType} detection sensitivity set to {sensitivity}/100");
            return null;
        }, ct);

    /// <summary>Msg 19 &lt;PtzPreset&gt;: toPos drives to a slot, setPos saves the current view there.</summary>
    internal static XElement BuildPresetCommand(byte channelId, int id, string command, string? name)
    {
        var preset = new XElement("preset", new XElement("id", id), new XElement("command", command));
        if (name != null) preset.Add(new XElement("name", name));
        return new XElement("PtzPreset", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", channelId), new XElement("presetList", preset));
    }

    /// <summary>The msg 190 reply lists saved slots only; the free ones up to maxPresetNum are added.</summary>
    internal static IReadOnlyList<PtzPresetInfo> ParseBcPresets(XElement reply)
    {
        var saved = reply.Descendants("preset")
            .Where(p => XInt(p, "id") is >= 0)
            .GroupBy(p => XInt(p, "id")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Element("name")?.Value.Trim() ?? "");
        int slots = Math.Clamp(XInt(reply, "maxPresetNum") ?? 64, 1, 128);
        return Enumerable.Range(0, Math.Max(slots, saved.Count == 0 ? 0 : saved.Keys.Max() + 1))
            .Select(id => saved.TryGetValue(id, out var n)
                ? new PtzPresetInfo(id, n.Length > 0 ? n : $"preset {id}", true)
                : new PtzPresetInfo(id, $"preset {id}", false))
            .ToList();
    }

    internal static XElement BuildAiDetectRead(byte channelId, string type) =>
        new("AiDetectCfg", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("chn", channelId), new XElement("type", type));

    /// <summary>Null when the reply lacks a sensitivity or answers for another type.</summary>
    internal static AiSensitivity? ParseBcAiSensitivity(string type, XElement cfg) =>
        XInt(cfg, "sensitivity") is { } s
        && (cfg.Element("type")?.Value.Trim() is not { Length: > 0 } t || t.Equals(type, StringComparison.OrdinalIgnoreCase))
            ? new AiSensitivity(type, Math.Clamp(s, 0, 100), XInt(cfg, "stayTime"))
            : null;

    // ------------------------------------------------- detection zones (HTTP)

    public bool HttpPaused => _httpApi != null && (DateTime.UtcNow < _httpRetryAt || SleepingOnPurpose);

    /// <summary>The last grid read for each type. A zone is near-static config, and
    /// the panel re-reads it on every tab click: without this, one slow answer arms
    /// the 60s transport backoff and a grid the user was looking at a second ago
    /// reads back as "this camera has no zone" — the same trap the HTTP feature
    /// sweep already keeps a cache for.</summary>
    private readonly Dictionary<string, DetectionZone> _zoneCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the md grid lives in the LEGACY Alarm object (doorbells), so
    /// later reads and the write go straight there instead of paying for the probe.</summary>
    private bool? _mdZoneIsLegacy;

    /// <summary>How this camera's firmware accepts motion-config writes, learned
    /// from its rejections: the full object as read (preserves every sibling
    /// field), the app-style minimal object (channel + changed section only), or
    /// the legacy SetAlarm dialect. Separate from the zone-read flag — reads and
    /// writes can split differently.</summary>
    private enum MdWriteMode { Unknown, Full, Minimal, Legacy }

    private MdWriteMode _mdWriteMode;

    /// <summary>AI types this camera reports a zone of their OWN for. Most cameras
    /// keep a single zone that governs every detection type, and answer per-type
    /// alarm queries with sensitivities but no grid — for those this stays empty and
    /// the editor offers one zone rather than a tab per type.</summary>
    private readonly HashSet<string> _aiZoneTypes = new(StringComparer.OrdinalIgnoreCase);

    private void NoteAiZoneSupport(string type, bool hasOwnZone)
    {
        lock (_aiZoneTypes)
        {
            if (hasOwnZone) _aiZoneTypes.Add(type);
            else _aiZoneTypes.Remove(type);
        }
    }

    /// <summary>The detection types with a zone the user can edit separately. "md" is
    /// always offered — it is the camera's zone, shared or not.</summary>
    public IReadOnlyList<string> ZoneTypes()
    {
        lock (_aiZoneTypes)
            return _aiZoneTypes.Count == 0
                ? new[] { "md" }
                : new[] { "md" }.Concat(AiAlarmTypes.Where(_aiZoneTypes.Contains)).ToArray();
    }

    public async Task<DetectionZone?> GetDetectionZoneAsync(string type, CancellationToken ct)
    {
        // A type this camera already rejected costs a doomed round trip, and a
        // failed one arms the backoff that blanks everything else.
        if (type != "md" && _aiAlarmTypes != null && !_aiAlarmTypes.Contains(type))
            return null;
        // Opening the editor is the user asking NOW, so an armed transport backoff
        // must not silently answer "nothing" — but a battery camera parked asleep
        // still gets radio silence: forcing packets at it would fake a wake edge.
        var answeredWithoutGrid = false;
        var fresh = await HttpTryAsync<DetectionZone?>(async c =>
        {
            if (type != "md")
                return ParseZone(type, await _httpApi!.GetAiAlarmAsync(type, c).ConfigureAwait(false));
            // Newer firmware (the Video Doorbell line) answers GetMdAlarm for
            // sensitivity but keeps the zone grid — ONE grid, shared by every
            // detection type — only in the legacy Alarm object. Which object holds
            // it is remembered, so this costs one round trip after the first read.
            if (_mdZoneIsLegacy == true)
                return await _httpApi!.TryGetLegacyMdConfigAsync(c).ConfigureAwait(false) is { } known
                    ? ParseZone(type, known) : null;
            var (cfg, isMdAlarm, settled) = await _httpApi!.ReadMdConfigAsync(c).ConfigureAwait(false);
            if (ParseZone(type, cfg) is { } zone)
            {
                _mdZoneIsLegacy = false;
                return zone;
            }
            var (legacy, legacyRejected) = isMdAlarm
                ? await ProbeLegacyMdAsync(c).ConfigureAwait(false)
                : (null, false);
            if (legacy != null && ParseZone(type, legacy) is { } shared)
            {
                _mdZoneIsLegacy = true;
                return shared;
            }
            // No grid in any dialect the camera answered or refused for good; a silence, or a GetMdAlarm
            // failure that may pass, settles nothing. This read's answer only (see CameraHoldsZone).
            if ((!isMdAlarm && settled) || legacy != null || legacyRejected) answeredWithoutGrid = true;
            LogZoneShapeOnce(cfg, legacy);
            return null;
        }, ct, force: !SleepingOnPurpose).ConfigureAwait(false);

        // Whether the zone is Neolink's is decided by the LATEST read, never
        // remembered: an HTTP error can look exactly like "no grid" (a refused
        // legacy command, a fallback taken after a failed GetMdAlarm), and a verdict
        // kept for the run would then hide the camera's real zone until a restart.
        // The camera is asked on every read, as it always was, so one bad answer
        // costs one read.
        if (type == "md") _zoneAbsent = fresh == null && answeredWithoutGrid;

        if (fresh != null)
        {
            if (type == "md") _zoneSeen = true;
            lock (_zoneCache) _zoneCache[type] = fresh;
            return fresh;
        }
        // Nothing came back. While the camera cannot be asked at all, the last
        // known grid beats claiming it has none — the caller reports staleness.
        if (HttpPaused)
            lock (_zoneCache)
                if (_zoneCache.TryGetValue(type, out var cached)) return cached;
        return null;
    }

    /// <summary>The speculative look for a zone in the legacy Alarm object. It runs
    /// only after GetMdAlarm already answered, so the API is known reachable — and a
    /// firmware that stalls on this ONE command must not arm the transport backoff
    /// that would then blank every other HTTP-backed panel section for a minute.</summary>
    private async Task<(JsonObject? Alarm, bool Rejected)> ProbeLegacyMdAsync(CancellationToken ct)
    {
        try
        {
            return await _httpApi!.ReadLegacyMdConfigAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is IOException or TimeoutException or OperationCanceledException
                  or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
        {
            Log.Debug($"{CameraName}: legacy motion-config probe did not answer ({ex.GetType().Name})");
            return (null, false);
        }
    }

    /// <summary>Neither motion object carried a grid: name their fields once, so a
    /// firmware whose zone hides under yet another shape can be reported and added
    /// instead of dead-ending at "no zone".</summary>
    private bool _zoneShapeLogged;

    /// <summary>_zoneSeen: the camera has produced a grid this run — kept, because
    /// a camera that showed its zone has one, whatever a later read says.
    /// _zoneAbsent: the LATEST md read reached the camera and it answered with no
    /// grid — re-decided on every read, never kept.</summary>
    private volatile bool _zoneSeen, _zoneAbsent;

    /// <inheritdoc/>
    /// <remarks>On this surface only "holds" is lasting. "Holds none" is what the
    /// most recent read said, and the web API asks this camera on every zone read
    /// (exactly as it did before zones could be kept on Neolink), so a read that
    /// went wrong is corrected by the next one.</remarks>
    public bool? CameraHoldsZone =>
        // No HTTP API is no zone path at all — that is knowable without asking.
        _httpApi == null ? false
        : _zoneSeen ? true
        : _zoneAbsent ? false
        : null;

    /// <inheritdoc/>
    public bool ZoneNeverOnCamera => _httpApi == null;

    private void LogZoneShapeOnce(JsonObject cfg, JsonObject? legacy)
    {
        if (_zoneShapeLogged) return;
        _zoneShapeLogged = true;
        static string Keys(JsonObject? o) => o == null ? "(rejected)" : string.Join(",", o.Select(kv => kv.Key));
        Log.Info($"{CameraName}: no zone grid in the motion config — MdAlarm carries [{Keys(cfg)}], " +
                 $"legacy Alarm carries [{Keys(legacy)}]. If the Reolink app shows a detection zone " +
                 "for this camera, report these field names so the dialect can be added.");
    }

    internal static DetectionZone? ParseZone(string type, JsonObject cfg)
    {
        if (cfg["scope"] is not JsonObject scope) return null;
        int cols = (int?)scope["cols"] ?? 0, rows = (int?)scope["rows"] ?? 0;
        var table = (string?)scope["table"];
        if (cols <= 0 || rows <= 0 || table == null || table.Length != cols * rows
            || table.Any(ch => ch is not ('0' or '1')))
            return null;
        return new DetectionZone(type, cols, rows, table);
    }

    public async Task SetDetectionZoneAsync(string type, string table, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"detection zones need the camera's HTTP API ('{CameraName}' has none)");
        if (type == "md")
        {
            // Read-modify-write like the sensitivity paths: the surrounding config
            // (schedules, sensitivities, target sizes) goes back untouched. The
            // write goes out the same dialect the grid was READ from — a doorbell
            // keeps it only in the legacy Alarm object (see GetDetectionZoneAsync).
            var (cfg, isMdAlarm) = _mdZoneIsLegacy == true
                ? (null, true)
                : await _httpApi.GetMdConfigAsync(ct).ConfigureAwait(false);
            if (cfg != null && ParseZone(type, cfg) != null)
            {
                ApplyZoneChecked(type, cfg, table);
                try
                {
                    if (_mdWriteMode is MdWriteMode.Minimal or MdWriteMode.Legacy)
                        await _httpApi.SetMdConfigAsync(MinimalMdWrite(cfg, "scope"), isMdAlarm: true, ct).ConfigureAwait(false);
                    else
                        await _httpApi.SetMdConfigAsync(cfg, isMdAlarm, ct).ConfigureAwait(false);
                }
                catch (ReolinkApiException ex) when (isMdAlarm)
                {
                    // Same firmware split as the sensitivity write: a grid READ from
                    // MdAlarm can still be refused on write. Step down the same
                    // ladder — the app-style minimal object first, the legacy Alarm
                    // object (when it carries the grid) after that.
                    try
                    {
                        Log.Info($"{CameraName}: SetMdAlarm rejected ({ex.Message}) — trying the app-style minimal object");
                        await _httpApi.SetMdConfigAsync(MinimalMdWrite(cfg, "scope"), isMdAlarm: true, ct).ConfigureAwait(false);
                        if (_mdWriteMode != MdWriteMode.Legacy) _mdWriteMode = MdWriteMode.Minimal;
                    }
                    catch (ReolinkApiException ex2)
                    {
                        var lg = await _httpApi.TryGetLegacyMdConfigAsync(ct).ConfigureAwait(false);
                        if (lg == null || ParseZone(type, lg) == null)
                        {
                            // Without this line a dead-end fallback is indistinguishable
                            // in the log from code that never tried one.
                            Log.Info($"{CameraName}: minimal SetMdAlarm rejected too ({ex2.Message}) and the legacy Alarm object " +
                                (lg == null ? "was refused" : $"has no grid (carries [{string.Join(",", lg.Select(kv => kv.Key))}])") +
                                " — every dialect exhausted");
                            throw;
                        }
                        Log.Info($"{CameraName}: minimal SetMdAlarm rejected too ({ex2.Message}) — writing the zone through the legacy Alarm dialect");
                        ApplyZoneChecked(type, lg, table);
                        await _httpApi.SetMdConfigAsync(lg, isMdAlarm: false, ct).ConfigureAwait(false);
                        _mdZoneIsLegacy = true;
                        _mdWriteMode = MdWriteMode.Legacy;
                    }
                }
            }
            else if (isMdAlarm
                && await _httpApi.TryGetLegacyMdConfigAsync(ct).ConfigureAwait(false) is { } legacy
                && ParseZone(type, legacy) != null)
            {
                ApplyZoneChecked(type, legacy, table);
                await _httpApi.SetMdConfigAsync(legacy, isMdAlarm: false, ct).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException($"{CameraName} reports no {type} detection zone");
            }
            lock (_zoneCache) _zoneCache.Remove(type);
        }
        else
        {
            if (!AiAlarmTypes.Contains(type))
                throw new ArgumentException($"unknown AI type '{type}'");
            var cfg = await _httpApi.GetAiAlarmAsync(type, ct).ConfigureAwait(false);
            ApplyZoneChecked(type, cfg, table);
            await _httpApi.SetAiAlarmAsync(cfg, ct).ConfigureAwait(false);
        }
        Log.Info($"{CameraName}: {type} detection zone set " +
                 $"({table.Count(ch => ch == '0')} of {table.Length} cells ignored)");
    }

    private void ApplyZoneChecked(string type, JsonObject cfg, string table)
    {
        if (ParseZone(type, cfg) is not { } zone)
            throw new NotSupportedException($"{CameraName} reports no {type} detection zone");
        // Dimensions come from the camera, never the client — a stale editor
        // (resolution change, firmware update) must fail, not scramble the grid.
        if (!ApplyZone(cfg, table))
            throw new ArgumentException(
                $"table must be {zone.Cols}x{zone.Rows} = {zone.Cols * zone.Rows} cells of '0'/'1'");
    }

    internal static bool ApplyZone(JsonObject cfg, string table)
    {
        if (ParseZone("md", cfg) is not { } zone || table.Length != zone.Cols * zone.Rows
            || table.Any(ch => ch is not ('0' or '1')))
            return false;
        ((JsonObject)cfg["scope"]!)["table"] = table;
        return true;
    }

    // ------------------------------------------------------- HDR + OSD (HTTP, beta)

    public async Task SetHdrAsync(int value, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"HDR needs the camera's HTTP API ('{CameraName}' has none)");
        // Minimal payload like the other ISP writes; the camera rejects out-of-range.
        var isp = new JsonObject { ["channel"] = _httpApi.ChannelId, ["hdr"] = Math.Max(0, value) };
        await _httpApi.SetIspAsync(isp, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: HDR set to {value}");
    }

    public Task<OsdSettings?> GetOsdSettingsAsync(CancellationToken ct) =>
        HttpTryAsync<OsdSettings?>(async c =>
        {
            var (osd, range) = await _httpApi!.GetOsdAsync(c).ConfigureAwait(false);
            return ParseOsd(osd, range);
        }, ct);

    internal static OsdSettings ParseOsd(JsonObject osd, JsonObject? range)
    {
        var name = osd["osdChannel"] as JsonObject;
        var time = osd["osdTime"] as JsonObject;
        var options = (range?["osdChannel"]?["pos"] as JsonArray ?? range?["osdTime"]?["pos"] as JsonArray)
            ?.Select(v => (string?)v).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
            ?? new List<string>();
        return new OsdSettings(
            ShowName: ((int?)name?["enable"] ?? 0) != 0,
            Name: (string?)name?["name"],
            NamePos: (string?)name?["pos"],
            ShowTime: ((int?)time?["enable"] ?? 0) != 0,
            TimePos: (string?)time?["pos"],
            Watermark: osd["watermark"] is { } wm ? (int?)wm != 0 : null,
            PosOptions: options);
    }

    public async Task SetOsdSettingsAsync(bool? showName, string? namePos, bool? showTime, string? timePos,
        bool? watermark, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"OSD settings need the camera's HTTP API ('{CameraName}' has none)");
        // Read-modify-write of the whole Osd object — SetOsd expects the complete
        // structure (both overlay blocks) back, unlike the flat Image/Isp writes.
        var (osd, _) = await _httpApi.GetOsdAsync(ct).ConfigureAwait(false);
        if (showName != null || namePos != null)
        {
            if (osd["osdChannel"] is not JsonObject name)
                throw new NotSupportedException($"{CameraName} reports no name overlay");
            if (showName is { } sn) name["enable"] = sn ? 1 : 0;
            if (namePos != null) name["pos"] = namePos;
        }
        if (showTime != null || timePos != null)
        {
            if (osd["osdTime"] is not JsonObject time)
                throw new NotSupportedException($"{CameraName} reports no timestamp overlay");
            if (showTime is { } st) time["enable"] = st ? 1 : 0;
            if (timePos != null) time["pos"] = timePos;
        }
        if (watermark is { } w)
        {
            if (osd["watermark"] == null)
                throw new NotSupportedException($"{CameraName} reports no watermark setting");
            osd["watermark"] = w ? 1 : 0;
        }
        await _httpApi.SetOsdAsync(osd, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: OSD changed (name={showName}/{namePos}, time={showTime}/{timePos}, watermark={watermark})");
    }

    // ------------------------------------------------- firmware check (HTTP, beta)

    /// <summary>CheckFirmware makes the CAMERA call Reolink's servers — cache the
    /// verdict so panel opens don't turn into cloud traffic.</summary>
    private FirmwareStatus? _fwStatus;
    private DateTime _fwCheckedAt;
    private static readonly TimeSpan FirmwareCacheFor = TimeSpan.FromHours(6);

    public async Task<FirmwareStatus?> CheckFirmwareAsync(CancellationToken ct)
    {
        if (_httpApi == null) return null;
        if (_fwStatus != null && DateTime.UtcNow - _fwCheckedAt < FirmwareCacheFor) return _fwStatus;
        var status = await HttpTryAsync<FirmwareStatus?>(async c =>
            ParseFirmware(await _httpApi!.CheckFirmwareAsync(c).ConfigureAwait(false)), ct).ConfigureAwait(false);
        if (status != null)
        {
            if (status.UpdateAvailable && _fwStatus?.UpdateAvailable != true)
                Log.Info($"{CameraName}: a newer camera firmware is available" +
                         $"{(status.NewVersion != null ? $" ({status.NewVersion})" : "")} — " +
                         "update it from the Reolink app/web page when convenient");
            _fwStatus = status;
            _fwCheckedAt = DateTime.UtcNow;
        }
        return status ?? _fwStatus;
    }

    /// <summary>newFirmware comes back as 0/1, a version string, or an info object
    /// depending on firmware generation; null = the camera didn't say.</summary>
    internal static FirmwareStatus? ParseFirmware(JsonNode? newFirmware)
    {
        switch (newFirmware)
        {
            case null:
                return null;
            case JsonObject info:
                return new FirmwareStatus(true,
                    (string?)info["firmVer"] ?? (string?)info["newFirmVer"] ?? (string?)info["version"]);
            case JsonValue v when v.TryGetValue<int>(out var flag):
                return new FirmwareStatus(flag != 0, null);
            case JsonValue v when v.TryGetValue<string>(out var s):
                return string.IsNullOrEmpty(s) || s == "0"
                    ? new FirmwareStatus(false, null)
                    : new FirmwareStatus(true, s == "1" ? null : s);
            default:
                return null;
        }
    }

    // -------------------------------------------- device settings (Baichuan, beta)
    // Message ids, roots, fields and values come from the firmware's own command
    // tables and parsers (firmware-analysis/field-notes.md).

    private static readonly TimeSpan ExtrasTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Smart-rule types: read id (write = read + 1), root, item element, seconds field.</summary>
    internal static readonly (string Type, uint GetId, string Root, string Item, string? SecondsField)[] SmartRuleKinds =
    {
        ("crossline", BcConstants.MsgIdGetCrossline, "CrosslineDetect", "crosslineDetectItem", null),
        ("intrusion", BcConstants.MsgIdGetIntrusion, "IntrusionDetect", "intrusionDetectItem", "stayTime"),
        ("loitering", BcConstants.MsgIdGetLoitering, "LoiteringDetect", "loiteringDetectItem", "stayTime"),
        ("object-left", BcConstants.MsgIdGetLegacy, "LegacyDetect", "legacyDetectItem", "timeThresh"),
        ("object-taken", BcConstants.MsgIdGetLoss, "LossDetect", "lossDetectItem", "timeThresh"),
    };

    /// <summary>Auto-reboot days the firmware accepts (matched without regard to case).</summary>
    internal static readonly string[] RebootDays =
        { "everyday", "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    /// <summary>Chime volume steps (the doorbell reports 0-4).</summary>
    internal const int ChimeVolumeMax = 4;

    public Task<(bool Mounted, bool Recording)?> GetSdStateAsync(CancellationToken ct) =>
        WithCameraAsync<(bool, bool)?>(async camera =>
        {
            var hdd = TryAsync(() => camera.GetRawAsync(BcConstants.MsgIdGetHdd, "HddInfoList", timeout: ExtrasTimeout, ct: ct));
            var record = TryAsync(() => camera.GetRawAsync(BcConstants.MsgIdGetRecordEnable, "Record", timeout: ExtrasTimeout, ct: ct));
            await Task.WhenAll(hdd, record).ConfigureAwait(false);
            if (hdd.Result == null && record.Result == null) return null;
            bool mounted = ParseHdd(hdd.Result)?.Any(c => c.Mounted) == true;
            bool recording = XInt(record.Result, "enable") is { } en ? en != 0 : mounted;
            return (mounted, recording);
        }, ct);

    public Task<TimeSpan?> GetClockOffsetAsync(CancellationToken ct) =>
        WithCameraAsync<TimeSpan?>(async camera =>
        {
            var general = await TryAsync(() => camera.GetSystemGeneralAsync(ExtrasTimeout, ct)).ConfigureAwait(false);
            return BcCameraCommands.ParseTime(general) is { } cameraLocal ? ClockOffset(DateTime.Now, cameraLocal) : null;
        }, ct);

    /// <summary>Whole seconds to add to the camera's times; null when the clocks are hours apart (a lost date).</summary>
    internal static TimeSpan? ClockOffset(DateTime serverLocal, DateTime cameraLocal)
    {
        var offset = TimeSpan.FromSeconds(Math.Round((serverLocal - cameraLocal).TotalSeconds));
        return Math.Abs(offset.TotalHours) < 3 ? offset : null;
    }

    public async Task<DeviceExtras?> GetDeviceExtrasAsync(CancellationToken ct)
    {
        var caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var f = caps.Features;
        return await WithCameraAsync<DeviceExtras?>(async camera =>
        {
            bool smartAi = ChannelSupportValue(caps.Support, camera.ChannelId, "smartAI") > 0;
            // Distinct message ids, so the reads run side by side under the gate.
            Task<XElement?> Get(uint id, string root, bool when = true) => when
                ? TryAsync(() => camera.GetRawAsync(id, root, timeout: ExtrasTimeout, ct: ct))
                : Task.FromResult<XElement?>(null);
            var record = Get(BcConstants.MsgIdGetRecordEnable, "Record");
            var reboot = Get(BcConstants.MsgIdGetAutoReboot, "AutoReboot");
            var guard = Get(BcConstants.MsgIdGetGuard, "PtzGuard", f.Ptz);
            var cruise = Get(BcConstants.MsgIdGetPtzCruise, "PtzCruise", f.Ptz);
            var shelter = Get(BcConstants.MsgIdGetShelter, "Shelter");
            var chimes = Get(BcConstants.MsgIdDingdongList, "dingdongList", f.Doorbell);
            var hdd = Get(BcConstants.MsgIdGetHdd, "HddInfoList");
            var rules = SmartRuleKinds.Select(k => Get(k.GetId, k.Root, smartAi)).ToArray();
            await Task.WhenAll(new[] { record, reboot, guard, cruise, shelter, chimes, hdd }.Concat(rules))
                .ConfigureAwait(false);

            List<ChimeInfo>? chimeList = null;
            if (chimes.Result is { } list)
            {
                chimeList = new List<ChimeInfo>();
                foreach (var info in list.Descendants("dingdongDeviceInfo"))
                {
                    if (XInt(info, "id") is not { } id) continue;
                    var opt = await TryAsync(() => camera.GetRawAsync(BcConstants.MsgIdDingdongDeviceOpt,
                        "dingdongDeviceOpt", BuildDingdongOpt(id, "getParam"), ExtrasTimeout, ct)).ConfigureAwait(false);
                    var silent = await TryAsync(() => camera.GetRawAsync(BcConstants.MsgIdGetDingdongSilent,
                        "dingdongSilentMode", BuildDingdongSilent(id, null), ExtrasTimeout, ct)).ConfigureAwait(false);
                    chimeList.Add(ParseChime(info, opt, silent));
                }
            }
            List<SmartRule>? smartRules = null;
            for (int i = 0; i < SmartRuleKinds.Length; i++)
                if (rules[i].Result is { } r)
                    (smartRules ??= new List<SmartRule>()).AddRange(ParseSmartRules(SmartRuleKinds[i].Type, r));

            return new DeviceExtras(
                SdRecording: XInt(record.Result, "enable") is { } en ? en != 0 : null,
                AutoReboot: ParseAutoReboot(reboot.Result),
                Guard: ParseGuard(guard.Result),
                Patrols: ParsePatrols(cruise.Result),
                PrivacyMasks: ParseMasks(shelter.Result),
                Chimes: chimeList,
                SmartRules: smartRules,
                SdCards: ParseHdd(hdd.Result));
        }, ct).ConfigureAwait(false);
    }

    public Task SetSdRecordingAsync(bool on, CancellationToken ct) =>
        ModifyAsync(BcConstants.MsgIdGetRecordEnable, BcConstants.MsgIdSetRecordEnable, "Record",
            el => { SetChild(el, "enable", on ? "1" : "0"); return el; },
            $"SD recording turned {(on ? "on" : "off")}", ct);

    public Task SetAutoRebootAsync(bool? enabled, string? weekDay, int? hour, int? minute, CancellationToken ct)
    {
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            throw new ArgumentException("hour must be 0-23 and minute 0-59");
        if (weekDay != null && !RebootDays.Contains(weekDay.ToLowerInvariant()))
            throw new ArgumentException($"weekDay must be one of: {string.Join(", ", RebootDays)}");
        return ModifyAsync(BcConstants.MsgIdGetAutoReboot, BcConstants.MsgIdSetAutoReboot, "AutoReboot", el =>
        {
            if (enabled is { } e) SetChild(el, "enable", e ? "1" : "0");
            if (weekDay != null) SetChild(el, "weekDay", MatchCase(weekDay.ToLowerInvariant(), el.Element("weekDay")?.Value));
            if (hour is { } h) SetChild(el, "hour", h.ToString());
            if (minute is { } m) SetChild(el, "minute", m.ToString());
            return el;
        }, $"auto-reboot set (enabled={enabled}, day={weekDay}, {hour}:{minute})", ct);
    }

    public Task SetGuardAsync(bool? enabled, int? timeout, string? action, CancellationToken ct)
    {
        if (action is not (null or "set" or "go"))
            throw new ArgumentException("action must be \"set\" or \"go\"");
        if (timeout is < 10 or > 300)
            throw new ArgumentException("timeout must be 10-300 seconds");
        // The camera answers only after its PTZ module does (up to 8 s), refusals included.
        return ModifyAsync(BcConstants.MsgIdGetGuard, BcConstants.MsgIdSetGuard, "PtzGuard",
            el => BuildGuard(el, enabled, timeout, action),
            $"PTZ guard (enabled={enabled}, timeout={timeout}, action={action})", ct, TimeSpan.FromSeconds(10));
    }

    public Task SetPatrolAsync(int id, bool run, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SendCommandAsync(BcConstants.MsgIdPtzControl, BcXmlBody.FromRaw(BuildPatrolControl(camera.ChannelId, id, run)),
                new ExtensionXml { ChannelId = camera.ChannelId }, ct: ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: patrol {id} {(run ? "started" : "stopped")}");
            return null;
        }, ct);

    public Task SetPrivacyMasksAsync(bool on, CancellationToken ct) =>
        ModifyAsync(BcConstants.MsgIdGetShelter, BcConstants.MsgIdSetShelter, "Shelter",
            el => { SetChild(el, "enable", on ? "1" : "0"); return el; },
            $"privacy masks turned {(on ? "on" : "off")}", ct);

    public Task SetChimeAsync(int id, int? volume, bool? led, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            var opt = await camera.GetRawAsync(BcConstants.MsgIdDingdongDeviceOpt, "dingdongDeviceOpt",
                BuildDingdongOpt(id, "getParam"), ExtrasTimeout, ct).ConfigureAwait(false)
                ?? throw new NotSupportedException($"chime {id} didn't answer");
            SetChild(opt, "id", id.ToString());
            SetChild(opt, "opt", "setParam");
            if (volume is { } v) SetChild(opt, "volLevel", Math.Clamp(v, 0, ChimeVolumeMax).ToString());
            if (led is { } l) SetChild(opt, "ledState", l ? "1" : "0");
            await camera.SetRawAsync(BcConstants.MsgIdDingdongDeviceOpt, opt, ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: chime {id} set (volume={volume}, led={led})");
            return null;
        }, ct);

    public Task RingChimeAsync(int id, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SetRawAsync(BcConstants.MsgIdDingdongDeviceOpt, BuildDingdongOpt(id, "ringWithMusic"), ct)
                .ConfigureAwait(false);
            return null;
        }, ct);

    public Task SetChimeSilentAsync(int id, int seconds, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.SetRawAsync(BcConstants.MsgIdSetDingdongSilent, BuildDingdongSilent(id, Math.Max(0, seconds)), ct)
                .ConfigureAwait(false);
            Log.Info($"{CameraName}: chime {id} {(seconds > 0 ? $"silenced for {seconds / 60} min" : "unsilenced")}");
            return null;
        }, ct);

    public Task SetSmartRuleAsync(string type, int index, int? sensitivity, int? seconds, bool delete, CancellationToken ct)
    {
        var kind = SmartRuleKinds.FirstOrDefault(k => k.Type == type);
        if (kind.Root == null) throw new ArgumentException($"unknown smart-rule type '{type}'");
        return ModifyAsync(kind.GetId, kind.GetId + 1, kind.Root,
            el => BuildSmartRuleEdit(el, kind.Item, kind.SecondsField, index, sensitivity, seconds, delete)
                  ?? throw new ArgumentException($"no {type} rule {index} on this camera"),
            $"{type} rule {index} {(delete ? "deleted" : $"set (sensitivity={sensitivity}, seconds={seconds})")}", ct);
    }

    /// <summary>Reads <paramref name="root"/>, builds the write from it, sends it with <paramref name="setId"/>.</summary>
    private Task ModifyAsync(uint getId, uint setId, string root, Func<XElement, XElement> build, string what,
        CancellationToken ct, TimeSpan? replyTimeout = null) =>
        WithCameraAsync<object?>(async camera =>
        {
            var el = await camera.GetRawAsync(getId, root, timeout: ExtrasTimeout, ct: ct).ConfigureAwait(false)
                     ?? throw new NotSupportedException($"the camera reports no {root} settings");
            await camera.SetRawAsync(setId, build(el), ct, replyTimeout).ConfigureAwait(false);
            Log.Info($"{CameraName}: {what}");
            return null;
        }, ct);

    internal static XElement BuildDingdongOpt(int id, string opt) =>
        new("dingdongDeviceOpt", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("id", id), new XElement("opt", opt));

    /// <summary>&lt;dingdongSilentMode&gt;: a read names the chime; a write silences it for
    /// <paramref name="seconds"/> (type 1) or ends silence (type 0).</summary>
    internal static XElement BuildDingdongSilent(int id, int? seconds)
    {
        var el = new XElement("dingdongSilentMode", new XAttribute("version", BcXmlBody.XmlVersion), new XElement("id", id));
        if (seconds is { } s)
        {
            el.Add(new XElement("time", s));
            el.Add(new XElement("type", s > 0 ? 1 : 0));
        }
        return el;
    }

    /// <summary>PTZ control (msg 18) running or stopping a saved patrol.</summary>
    internal static XElement BuildPatrolControl(byte channelId, int patrolId, bool run) =>
        new("PtzControl", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", channelId),
            new XElement("command", run ? "startPatrol" : "stopPatrol"),
            new XElement("patrolId", patrolId));

    private static int? XInt(XElement? el, string name) =>
        int.TryParse(el?.Element(name)?.Value.Trim(), out var v) ? v : null;

    private static bool IsOn(string? v) => v?.Trim().ToLowerInvariant() is "1" or "open" or "on" or "true";

    /// <summary>Gives <paramref name="value"/> the capitalisation of the camera's own value.</summary>
    internal static string MatchCase(string value, string? current) =>
        current is { Length: > 0 } c && char.IsUpper(c[0]) && value.Length > 0
            ? char.ToUpperInvariant(value[0]) + value[1..]
            : value;

    internal static AutoRebootState? ParseAutoReboot(XElement? a) => a == null ? null : new AutoRebootState(
        IsOn(a.Element("enable")?.Value), (a.Element("weekDay")?.Value.Trim() ?? "everyday").ToLowerInvariant(),
        XInt(a, "hour") ?? 0, XInt(a, "minute") ?? 0);

    internal static GuardState? ParseGuard(XElement? g) => g == null ? null :
        new GuardState(IsOn(g.Element("benable")?.Value), IsOn(g.Element("bvalid")?.Value), XInt(g, "timeout"));

    /// <summary>The guard write. The camera applies settings only under setGrd, saving the current
    /// position when needSetPos is 1; toGrd drives to the saved one.</summary>
    internal static XElement BuildGuard(XElement guard, bool? enabled, int? timeout, string? action)
    {
        var g = new XElement(guard);
        if (enabled is { } e) SetChild(g, "benable", e ? "1" : "0");
        if (timeout is { } t) SetChild(g, "timeout", t.ToString());
        SetChild(g, "needSetPos", action == "set" ? "1" : "0");
        SetChild(g, "command", action == "go" ? "toGrd" : "setGrd");
        return g;
    }

    /// <summary>Configured patrols only; empty firmware slots (no name, no key positions) are skipped.</summary>
    internal static IReadOnlyList<PatrolInfo>? ParsePatrols(XElement? cruise) => cruise?.Descendants("cruise")
        .Where(c => XInt(c, "patrolId") != null
                    && (IsOn(c.Element("enable")?.Value) || (c.Element("name")?.Value.Trim().Length ?? 0) > 0
                        || c.Descendants("presetId").Any()))
        .Select(c => new PatrolInfo(XInt(c, "patrolId")!.Value, c.Element("name")?.Value.Trim() ?? "",
            IsOn(c.Element("enable")?.Value)))
        .ToList();

    internal static PrivacyMaskState? ParseMasks(XElement? shelter) => shelter == null ? null :
        new PrivacyMaskState(IsOn(shelter.Element("enable")?.Value),
            shelter.Element("shelterList")?.Elements("Shelter").Count(m => XInt(m, "width") is not (null or 0)) ?? 0);

    internal static ChimeInfo ParseChime(XElement info, XElement? opt, XElement? silent = null) => new(
        XInt(info, "id") ?? 0,
        (opt?.Element("name")?.Value.Trim() is { Length: > 0 } n ? n : info.Element("name")?.Value.Trim()) ?? "",
        IsOn(info.Element("netstate")?.Value) || info.Element("netstate")?.Value.Trim().ToLowerInvariant() == "online",
        XInt(opt, "volLevel"),
        opt?.Element("ledState") is { } l ? IsOn(l.Value) : null,
        silent == null ? null : Math.Max(0, XInt(silent, "remainTime") ?? 0));

    internal static IEnumerable<SmartRule> ParseSmartRules(string type, XElement root)
    {
        var kind = SmartRuleKinds.First(k => k.Type == type);
        foreach (var item in root.Elements(kind.Item))
        {
            if (XInt(item, "index") is not { } index) continue;
            yield return new SmartRule(type, index, item.Element("name")?.Value.Trim() ?? "",
                item.Element("aiType")?.Value.Trim() ?? "", XInt(item, "sesensitivity"),
                kind.SecondsField == null ? null : XInt(item, kind.SecondsField),
                item.Element("direction")?.Value.Trim());
        }
    }

    /// <summary>The write for one rule: the firmware applies a top-level op (modify or delete)
    /// to the items sent, matched by index — so only the edited item goes. Null = no such rule.</summary>
    internal static XElement? BuildSmartRuleEdit(XElement current, string itemName, string? secondsField, int index,
        int? sensitivity, int? seconds, bool delete)
    {
        var item = current.Elements(itemName).FirstOrDefault(i => XInt(i, "index") == index);
        if (item == null) return null;
        var edited = new XElement(item);
        if (!delete)
        {
            if (sensitivity is { } s) SetChild(edited, "sesensitivity", Math.Clamp(s, 0, 100).ToString());
            if (seconds is { } sec && secondsField != null) SetChild(edited, secondsField, Math.Max(0, sec).ToString());
        }
        return new XElement(current.Name, new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", current.Element("channelId")?.Value.Trim() ?? "0"),
            new XElement("op", delete ? "delete" : "modify"),
            edited);
    }

    /// <summary>SD cards from &lt;HddInfoList&gt; (msg 102): the firmware splits sizes into whole
    /// GB (capacity, remainSize) and the leftover MB (capacityM, remainSizeM).</summary>
    internal static IReadOnlyList<SdCardInfo>? ParseHdd(XElement? hdd) => hdd?.Elements("HddInfo")
        .Select(h =>
        {
            long total = XLong(h, "capacity") * 1024 + XLong(h, "capacityM");
            long free = XLong(h, "remainSize") * 1024 + XLong(h, "remainSizeM");
            if (total == 0) { total = XLong(h, "capacityV2"); free = XLong(h, "remainSizeV2"); }
            return new SdCardInfo(XInt(h, "number") ?? 0, total, free,
                IsOn(h.Element("format")?.Value), IsOn(h.Element("mount")?.Value));
        })
        .Where(c => c.TotalMb > 0)
        .ToList();

    private static long XLong(XElement el, string name) =>
        long.TryParse(el.Element(name)?.Value.Trim(), out var v) ? v : 0;

    // -------------------------------------------- SD-card recordings (HTTP, beta)

    /// <summary>SD searches walk the card's file table — give them the roomy
    /// snapshot budget, not the 6s config-read cap.</summary>
    public async Task<IReadOnlyList<int>?> GetSdRecordingDaysAsync(int year, int month, CancellationToken ct)
    {
        PreemptFillTransfer();
        return (HttpAbsent ? null : await HttpSdDaysAsync(year, month, ct).ConfigureAwait(false))
               ?? await BcSdDaysAsync(year, month, ct).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<int>?> HttpSdDaysAsync(int year, int month, CancellationToken ct) =>
        HttpTryAsync<IReadOnlyList<int>?>(async c =>
        {
            var start = new DateTime(year, month, 1);
            var result = await _httpApi!.SearchAsync("main", start, start.AddMonths(1).AddSeconds(-1),
                onlyStatus: true, c).ConfigureAwait(false);
            return ParseSdCalendar(result, year, month);
        }, ct, HttpSnapTimeout, force: true);

    /// <summary>Status[].table is one digit per day of the month ('1' = recordings).</summary>
    internal static IReadOnlyList<int> ParseSdCalendar(JsonObject searchResult, int year, int month)
    {
        var days = new List<int>();
        foreach (var status in (searchResult["Status"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if ((int?)status["year"] != year || (int?)status["mon"] != month) continue;
            var table = (string?)status["table"] ?? "";
            for (int i = 0; i < table.Length; i++)
                if (table[i] != '0')
                    days.Add(i + 1);
        }
        return days;
    }

    /// <summary>Overall budget for one SD FILE search (all windows, both streams) —
    /// kept UNDER the UI's own 95s wait so a slow day answers (partial or the
    /// error banner) instead of the client cancelling first.</summary>
    private static readonly TimeSpan SdSearchTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Budget for ONE search window. The camera walks its card's file
    /// table per query; an event-heavy doorbell that is also encoding streams
    /// can't walk a whole day inside any sane budget (field report: calendar
    /// answered, file search never did), so the day is paged into short walks.</summary>
    private static readonly TimeSpan SdWindowTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<SdRecording>?> GetSdRecordingsAsync(DateOnly day, CancellationToken ct, string? stream = null,
        bool preempt = true)
    {
        if (preempt) PreemptFillTransfer();
        var list = (HttpAbsent ? null : await HttpSdRecordingsAsync(day, ct, stream).ConfigureAwait(false))
                   ?? await BcSdRecordingsAsync(day, ct, stream).ConfigureAwait(false);
        if (list != null && stream == null) Web.SdListings.Remember(CameraName, day, list);
        return list;
    }

    public (DateTime At, IReadOnlyList<SdRecording> List)? LastSdRecordings(DateOnly day, string? stream = null) =>
        stream == null ? Web.SdListings.Last(CameraName, day) : null;

    /// <summary>A viewer's card request outranks a background fill's transfer, which the camera
    /// would otherwise serve first and leave the search unanswered; the fill retries later.</summary>
    private void PreemptFillTransfer()
    {
        if (_sdCurrentYields && _sdCurrent is { } fill)
        {
            Log.Info($"{CameraName}: a viewer is browsing the card; the background copy transfer yields");
            try { fill.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private Task<IReadOnlyList<SdRecording>?> HttpSdRecordingsAsync(DateOnly day, CancellationToken ct, string? stream = null) =>
        HttpTryAsync<IReadOnlyList<SdRecording>?>(async c =>
        {
            // The camera records whichever stream its own settings say — usually
            // main; older/battery firmwares list sub. Ask main first, sub when
            // main has nothing — and a stream the firmware REJECTS searching
            // must not abort the other one.
            bool anyWindowFailed = false, anyWindowWorked = false;
            foreach (var streamType in stream == null ? new[] { "main", "sub" } : new[] { stream })
            {
                var files = new List<SdRecording>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool rejected = false;
                int consecutiveFails = 0, rawTotal = 0;
                JsonObject? lastResult = null;
                // Four 6-hour windows instead of one whole-day query: each walk
                // stays short (Reolink's own clients page the search too), and a
                // window that fails costs a gap, not the day.
                for (int h = 0; h < 24; h += 6)
                {
                    var start = day.ToDateTime(new TimeOnly(h, 0, 0));
                    var end = h + 6 >= 24 ? day.ToDateTime(new TimeOnly(23, 59, 59))
                                          : day.ToDateTime(new TimeOnly(h + 5, 59, 59));
                    using var wcts = CancellationTokenSource.CreateLinkedTokenSource(c);
                    wcts.CancelAfter(SdWindowTimeout);
                    try
                    {
                        var result = await _httpApi!.SearchAsync(streamType, start, end, onlyStatus: false, wcts.Token)
                            .ConfigureAwait(false);
                        lastResult = result;
                        rawTotal += (result["File"] as JsonArray)?.Count ?? 0;
                        foreach (var f in ParseSdRecordings(result, streamType))
                            if (seen.Add(f.Name)) // a boundary-spanning file lists in both windows
                                files.Add(f);
                        consecutiveFails = 0;
                        anyWindowWorked = true;
                    }
                    catch (ReolinkApiException ex)
                    {
                        // The firmware doesn't do this stream's search — next stream.
                        Log.Debug($"{CameraName}: SD file search ({streamType}, {day:yyyy-MM-dd}) rejected: {ex.Message}");
                        rejected = true;
                        break;
                    }
                    catch (Exception ex) when (!c.IsCancellationRequested)
                    {
                        // A slow or dropped window (camera busy streaming): keep what
                        // we have, try the rest, give up on this stream after two
                        // misses in a row. Info, not Debug — the UI's error banner
                        // sends people to this log expecting details.
                        anyWindowFailed = true;
                        Log.Info($"{CameraName}: SD file search ({streamType}, {day:yyyy-MM-dd} " +
                                 $"{h:00}:00-{Math.Min(h + 6, 24):00}:00) failed: {Log.Flatten(ex)}");
                        // No session token and nothing answered yet: there is no
                        // working HTTP API here (some models simply have none) —
                        // one failed login says it all, and walking the remaining
                        // windows would stack more of the same timeout.
                        if (!_httpApi!.HasLiveToken && !anyWindowWorked) return null;
                        if (++consecutiveFails >= 2) break;
                    }
                }
                if (files.Count > 0)
                {
                    if (anyWindowFailed)
                        Log.Info($"{CameraName}: SD file search ({streamType}, {day:yyyy-MM-dd}) shows a PARTIAL " +
                                 $"day ({files.Count} recordings) — some windows failed; refresh fills the gaps");
                    if (_httpSdFiles.Count > 5000) _httpSdFiles.Clear();
                    foreach (var f in files) _httpSdFiles[f.Name] = f;
                    return files;
                }
                if (rejected || lastResult == null) continue;
                // Zero usable files answers the "the app shows footage but neolink
                // doesn't" question ONLY if we can see what the camera actually
                // said — log the raw shape (dropped entries mean an unmapped
                // firmware dialect; an absent File[] means a genuinely empty day).
                Log.Info($"{CameraName}: SD file search ({streamType}, {day:yyyy-MM-dd}) returned " +
                         (rawTotal <= 0 ? $"no File list (keys: {string.Join(",", lastResult.Select(k => k.Key))})"
                                        : $"{rawTotal} entries but none usable — first entry: " +
                                          Truncate((lastResult["File"] as JsonArray)?[0]?.ToJsonString() ?? "?", 400)));
            }
            // Nothing usable. An empty day the camera confirmed is an empty list;
            // any failed window with zero results is null — the UI's error banner —
            // returned WITHOUT arming the transport backoff, so the refresh the
            // banner suggests actually reaches the camera instead of no-opping
            // for 60 s.
            return anyWindowFailed ? null : new List<SdRecording>();
        }, ct, SdSearchTimeout, force: true);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal static IReadOnlyList<SdRecording> ParseSdRecordings(JsonObject searchResult, string streamType) =>
        (searchResult["File"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(f => new SdRecording(
                Name: (string?)f["name"] ?? "",
                Start: SearchTime(f["StartTime"] as JsonObject),
                End: SearchTime(f["EndTime"] as JsonObject),
                // "size" comes back as a NUMBER on most firmwares but a quoted STRING
                // on some (the Video Doorbell WiFi) — a bare (long?) cast throws on the
                // string, which failed the ENTIRE day's parse and read as "none usable".
                SizeBytes: AsLong(f["size"]),
                StreamType: (string?)f["type"] ?? streamType))
            .Where(f => f.Name.Length > 0)
            .OrderBy(f => f.Start)
            .ToList();

    /// <summary>A long from a JSON value that may be a number OR a numeric string
    /// (firmwares differ on whether they quote "size"); 0 when it's neither.</summary>
    internal static long AsLong(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed) ? parsed : 0;
    }

    private static DateTime SearchTime(JsonObject? t) => t == null
        ? default
        : new DateTime((int?)t["year"] ?? 1, (int?)t["mon"] ?? 1, (int?)t["day"] ?? 1,
            (int?)t["hour"] ?? 0, (int?)t["min"] ?? 0, (int?)t["sec"] ?? 0);

    /// <summary>After Playback failed or answered FLV once, this camera goes straight to Download.</summary>
    private bool _sdPreferDownload;
    /// <summary>Null = not asked yet; false = the firmware has no CheckDownload.</summary>
    private bool? _sdHasCheckDownload;
    private string? _sdPassword;
    /// <summary>HTTP served neither Playback nor Download, so recordings come over Baichuan.</summary>
    private bool _sdViaBc;

    public void SetSdPassword(string? password) =>
        _sdPassword = string.IsNullOrEmpty(password) ? null : password;

    /// <summary>Fetches one recording as the camera's web UI does: CheckDownload first (it flags
    /// encryption), then uncapped Playback, then Download (1 MB/s); an FLV Playback comes last.</summary>
    public async Task<ReolinkHttpApi.SdDownload> OpenSdRecordingAsync(string fileName, CancellationToken ct, bool yield = false)
    {
        if (_bcSdFiles.TryGetValue(fileName, out var bcEntry))
            return await OpenBcSdRecordingAsync(fileName, bcEntry, ct, yield).ConfigureAwait(false);
        if (_sdViaBc && AnyLive() != null)
            return await OpenBcTwinAsync(fileName, ct, yield).ConfigureAwait(false);
        if (_httpApi == null)
            throw new NotSupportedException($"SD-card playback needs the camera's HTTP API ('{CameraName}' has none)");

        var source = fileName;
        ReolinkHttpApi.DownloadCheck? check = null;
        if (_sdHasCheckDownload != false)
        {
            check = await _httpApi.CheckDownloadAsync(fileName, ct).ConfigureAwait(false);
            _sdHasCheckDownload = check != null;
            if (check is { Encrypted: true })
            {
                if (_sdPassword is not { } pw)
                    throw new ReolinkApiException("this recording is encrypted on the camera; enter its recording password" +
                                                  (check.Prompt is { Length: > 0 } hint ? $" (hint: {hint})" : ""));
                source = check.FileName is { Length: > 0 } unlocked ? unlocked : fileName;
                if (!await _httpApi.SetRecDecryptKeyAsync(source, pw, ct).ConfigureAwait(false))
                    throw new ReolinkApiException("the camera did not accept the recording password");
            }
        }

        var errors = new List<string>();
        bool flvSeen = false;
        foreach (var method in _sdPreferDownload ? new[] { "Download", "Playback" } : new[] { "Playback", "Download" })
        {
            try
            {
                var download = method == "Playback"
                    ? await _httpApi.PlaybackAsync(source, ct).ConfigureAwait(false)
                    : await _httpApi.DownloadAsync(source, ct).ConfigureAwait(false);
                if (download.Flv)
                {
                    // Elite / Video Doorbell: Playback is a real-time FLV stream for their own player.
                    download.Dispose();
                    flvSeen = true;
                    _sdPreferDownload = true;
                    errors.Add("Playback: FLV stream");
                    continue;
                }
                Log.Info($"{CameraName}: streaming SD-card recording '{fileName}' ({method}" +
                         $"{(download.Length is { } len ? $", {len / 1024 / 1024} MB" : "")})");
                return download;
            }
            catch (ReolinkApiException ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{method}: {ex.Message}");
                if (method == "Playback") _sdPreferDownload = true;
            }
        }
        if (flvSeen)
        {
            var flv = await _httpApi.PlaybackAsync(source, ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: SD-card recording '{fileName}' only came as FLV; remuxing it");
            return flv;
        }
        // Doorbells hand both to their RTMP service, which is off unless RTMP is enabled;
        // the same recording is fetched over Baichuan instead.
        if (AnyLive() != null && check is not { Encrypted: true })
        {
            Log.Info($"{CameraName}: HTTP served neither Playback nor Download ({string.Join("; ", errors)}); " +
                     "fetching SD recordings over Baichuan from now on (enabling RTMP in the Ports tab restores HTTP)");
            _sdViaBc = true;
            return await OpenBcTwinAsync(fileName, ct, yield).ConfigureAwait(false);
        }
        throw new ReolinkApiException("the camera served this recording by neither Playback nor Download; " +
                                      "on a Video Doorbell, enable RTMP in the Ports tab: " + string.Join("; ", errors));
    }

    // ------------------------------------- SD-card recordings (Baichuan, beta)
    // For cameras with no working HTTP API (the Lumus ships without one). Shapes follow
    // the firmware's parsers; the first reply is logged to settle the details.

    /// <summary>Search entries by name, so a download can echo the camera's own entry back.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, XElement> _bcSdFiles =
        new(StringComparer.Ordinal);
    /// <summary>HTTP-listed recordings by name: their start time finds the Baichuan twin.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SdRecording> _httpSdFiles =
        new(StringComparer.Ordinal);

    /// <summary>Fetches an HTTP-listed recording over Baichuan: the Baichuan search names files
    /// differently, so the day is searched and the entry with the same start time is used.</summary>
    private async Task<ReolinkHttpApi.SdDownload> OpenBcTwinAsync(string httpName, CancellationToken ct, bool yield = false)
    {
        var start = _httpSdFiles.TryGetValue(httpName, out var listed) ? listed.Start
            : SdStartFromName(httpName)
              ?? throw new NotSupportedException($"'{httpName}' is not in the current SD listing; refresh the day and retry");
        var day = DateOnly.FromDateTime(start);
        var stream = listed?.StreamType ?? ReolinkHttpApi.StreamOfFile(httpName) ?? "main";
        var twins = await BcSdRecordingsAsync(day, ct, stream).ConfigureAwait(false)
                    ?? throw new NotSupportedException("the camera did not answer the Baichuan file search");
        var twin = twins.Where(t => Math.Abs((t.Start - start).TotalSeconds) <= 5)
            .OrderBy(t => Math.Abs((t.Start - start).TotalSeconds)).FirstOrDefault()
            ?? throw new NotSupportedException($"the Baichuan search lists no recording starting {start:HH:mm:ss} " +
                                               $"({twins.Count} that day)");
        if (!_bcSdFiles.TryGetValue(twin.Name, out var entry))
            throw new NotSupportedException($"no search entry for '{twin.Name}'");
        return await OpenBcSdRecordingAsync(twin.Name, entry, ct, yield).ConfigureAwait(false);
    }

    /// <summary>The start time in a Reolink recording name (…_YYYYMMDD_HHMMSS_…), or null.</summary>
    internal static DateTime? SdStartFromName(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"_(\d{8})_(\d{6})_");
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                   System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? t : null;
    }
    /// <summary>One Baichuan transfer per camera at a time; the running one, so a newer pick can cancel it.</summary>
    private readonly SemaphoreSlim _sdGate = new(1, 1);
    /// <summary>Whether the transfer in flight is a background fill (a viewer may cut it short).</summary>
    private volatile bool _sdCurrentYields;
    private volatile CancellationTokenSource? _sdCurrent;
    private volatile string? _sdCurrentName;
    /// <summary>Why the last SD-card search over Baichuan failed, for the UI; null after success.</summary>
    private volatile string? _sdFailure;
    public string? SdFailure => _sdFailure;
    private readonly HashSet<string> _bcSdLogged = new();

    /// <summary>A battery model that never answered HTTP: its SD card is asked over Baichuan only.</summary>
    private bool HttpAbsent => _httpApi == null || _httpWarnCooldownUntil == DateTime.MaxValue;
    private static readonly TimeSpan SdBcTimeout = TimeSpan.FromSeconds(10);

    private async Task<IReadOnlyList<int>?> BcSdDaysAsync(int year, int month, CancellationToken ct)
    {
        try
        {
            return await WithCameraAsync<IReadOnlyList<int>?>(async camera =>
            {
                var start = new DateTime(year, month, 1);
                var body = BcCameraCommands.BuildDayRecords(camera.ChannelId, start, start.AddMonths(1).AddSeconds(-1));
                var reply = await camera.SendCommandAsync(BcConstants.MsgIdGetDayRecords, BcXmlBody.FromRaw(body),
                    new ExtensionXml { ChannelId = camera.ChannelId }, SdBcTimeout, ct: ct).ConfigureAwait(false);
                var days = reply?.Xml?.RawElement("DayRecords");
                LogBcSdOnce("calendar", days);
                if (days == null)
                {
                    // The reply's shape is the only clue to an unmapped firmware dialect.
                    _sdFailure = "the camera answered the SD calendar with " + (reply?.Xml?.Raw is { Count: > 0 } raw
                        ? "<" + string.Join(">, <", raw.Select(e => e.Name.LocalName)) + "> instead of <DayRecords>"
                        : "no XML");
                    Log.Info($"{CameraName}: {_sdFailure}" + (reply?.Xml?.Raw is { Count: > 0 } r
                        ? ": " + Truncate(string.Concat(r.Select(e => e.ToString(SaveOptions.DisableFormatting))), 600) : ""));
                    return null;
                }
                _sdFailure = null;
                return BcCameraCommands.ParseDayRecords(days, year, month);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException or CameraOfflineException)
        {
            _sdFailure = ex switch
            {
                CameraOfflineException => "Neolink has no live connection to the camera right now",
                TimeoutException => "the camera did not answer the SD calendar (msg 142) in time",
                _ => $"the camera rejected the SD calendar (msg 142): {ex.Message}",
            };
            Log.Info($"{CameraName}: SD calendar over Baichuan failed: {_sdFailure}");
            return null;
        }
    }

    private async Task<IReadOnlyList<SdRecording>?> BcSdRecordingsAsync(DateOnly day, CancellationToken ct, string? wanted = null)
    {
        try
        {
            return await WithCameraAsync<IReadOnlyList<SdRecording>?>(async camera =>
            {
                var start = day.ToDateTime(TimeOnly.MinValue);
                var end = day.ToDateTime(new TimeOnly(23, 59, 59));
                bool answered = false;
                var streams = wanted == null ? new[] { "mainStream", "subStream" }
                    : new[] { wanted == "sub" ? "subStream" : "mainStream" };
                foreach (var stream in streams)
                {
                    XElement? open;
                    try
                    {
                        open = await camera.GetRawAsync(BcConstants.MsgIdSearchOpen, "FileInfoList",
                            BcCameraCommands.BuildFileSearch(camera.ChannelId, stream, start, end), SdBcTimeout, ct)
                            .ConfigureAwait(false);
                    }
                    catch (CameraCommandException) { continue; } // this stream isn't searchable
                    answered = true;
                    LogBcSdOnce("file search", open);
                    var files = new List<SdRecording>();
                    AddBcSdFiles(open, stream, files);
                    if (BcCameraCommands.SearchHandle(open) is { } handle)
                    {
                        try
                        {
                            for (int page = 0; page < 100; page++)
                            {
                                XElement? more;
                                try
                                {
                                    more = await camera.GetRawAsync(BcConstants.MsgIdSearchFile, "FileInfoList",
                                        BcCameraCommands.BuildFileHandle(camera.ChannelId, handle), SdBcTimeout, ct)
                                        .ConfigureAwait(false);
                                }
                                catch (CameraCommandException) { break; } // past the last page
                                int before = files.Count;
                                AddBcSdFiles(more, stream, files);
                                if (files.Count == before) break;
                            }
                        }
                        finally
                        {
                            try { await camera.CloseFileSearchAsync(handle, ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is CameraCommandException or TimeoutException) { }
                        }
                    }
                    Log.Info($"{CameraName}: SD file search over Baichuan ({stream}, {day:yyyy-MM-dd}): {files.Count} recording(s)");
                    if (files.Count > 0)
                        return files.OrderBy(f => f.Start).ToList();
                }
                return answered ? new List<SdRecording>() : null;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or CameraOfflineException)
        {
            _sdFailure = ex is CameraOfflineException
                ? "Neolink has no live connection to the camera right now"
                : "the camera did not answer the SD file search (msg 14) in time";
            Log.Info($"{CameraName}: SD file search over Baichuan failed: {_sdFailure}");
            return null;
        }
    }

    private void AddBcSdFiles(XElement? list, string stream, List<SdRecording> into)
    {
        if (_bcSdFiles.Count > 5000) _bcSdFiles.Clear();
        foreach (var f in BcCameraCommands.ParseFileInfos(list))
        {
            if (into.Any(r => r.Name == f.Name)) continue;
            _bcSdFiles[f.Name] = f.Raw;
            into.Add(new SdRecording(f.Name, f.Start, f.End, f.Size, stream == "subStream" ? "sub" : "main"));
        }
    }

    private void LogBcSdOnce(string what, XElement? reply)
    {
        if (reply == null || !_bcSdLogged.Add(what)) return;
        Log.Info($"{CameraName}: SD card over Baichuan, first {what} reply: " +
                 Truncate(reply.ToString(SaveOptions.DisableFormatting), 600));
    }

    private async Task<ReolinkHttpApi.SdDownload> OpenBcSdRecordingAsync(string fileName, XElement entry,
        CancellationToken ct, bool yield = false)
    {
        var camera = AnyLive() ?? throw new CameraOfflineException(CameraName);
        // Latest wins: a clip still loading is abandoned for a different one just picked.
        if (!yield && _sdCurrentName != fileName)
            try { _sdCurrent?.Cancel(); } catch (ObjectDisposedException) { }
        await _sdGate.WaitAsync(ct).ConfigureAwait(false);
        long size = BcCameraCommands.FileInfoSize(entry); // the camera sends exactly this many bytes
        var pipe = new System.IO.Pipelines.Pipe();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _sdCurrent = cts;
        _sdCurrentName = fileName;
        _sdCurrentYields = yield;
        var writer = new FirstWriteStream(pipe.Writer.AsStream());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            try
            {
                long got;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        got = await camera.DownloadFileAsync(BcCameraCommands.BuildDownload(entry), size, writer, cts.Token)
                            .ConfigureAwait(false);
                        break;
                    }
                    // A 400 right after a reconnect: the camera is still closing the transfer the
                    // dropped session left behind. Nothing was written yet, so ask again shortly.
                    catch (CameraCommandException ex) when (ex.ResponseCode == 400 && attempt < 3 && writer.FirstWriteAt == null)
                    {
                        Log.Info($"{CameraName}: the camera refused the SD transfer of '{fileName}' (busy); asking again in 3 s");
                        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
                        camera = AnyLive() ?? throw new CameraOfflineException(CameraName);
                    }
                }
                var firstByte = writer.FirstWriteAt ?? clock.Elapsed;
                var transfer = clock.Elapsed - firstByte;
                Log.Info($"{CameraName}: SD recording '{fileName}' fetched over Baichuan ({got / 1024} KB" +
                         $"{(size > 0 ? $" of {size / 1024} KB" : "")}; first byte after {firstByte.TotalSeconds:0.0} s, " +
                         $"then {transfer.TotalSeconds:0.0} s at {got / 1024.0 / Math.Max(0.1, transfer.TotalSeconds):0} KB/s)");
                await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info(cts.IsCancellationRequested
                    ? $"{CameraName}: SD download over Baichuan of '{fileName}' abandoned (another clip was picked, or the viewer left)"
                    : $"{CameraName}: SD download over Baichuan failed for '{fileName}': {Log.Flatten(ex)}");
                await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_sdCurrent, cts)) _sdCurrent = null;
                _sdGate.Release();
                cts.Dispose();
            }
        }, CancellationToken.None);
        return new ReolinkHttpApi.SdDownload(pipe.Reader.AsStream(), null,
            owner: new CancelOnDispose(cts), viaBaichuan: true, expectedBytes: size > 0 ? size : null);
    }

    /// <summary>Notes when the first bytes arrive (the camera's reply latency for the fetch log).</summary>
    private sealed class FirstWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan? FirstWriteAt { get; private set; }
        public FirstWriteStream(Stream inner) => _inner = inner;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            FirstWriteAt ??= _clock.Elapsed;
            return _inner.WriteAsync(buffer, ct);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            FirstWriteAt ??= _clock.Elapsed;
            _inner.Write(buffer, offset, count);
        }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Stops a background transfer when its reader goes away.</summary>
    private sealed class CancelOnDispose : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public CancelOnDispose(CancellationTokenSource cts) => _cts = cts;
        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public Task RebootAsync(CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            Log.Warn($"{CameraName}: reboot requested via web API");
            await camera.RebootAsync(ct).ConfigureAwait(false);
            return null;
        }, ct);

    // Talk sessions are long-lived, so they must not hold the command gate (PTZ,
    // snapshots etc. keep working while talking); msg ids 10/201/202/11 are used
    // by nothing else. A dedicated gate limits it to one session per camera.
    private readonly SemaphoreSlim _talkGate = new(1, 1);

    public async Task TalkAsync(int sampleRate, ChannelReader<byte[]> pcm, CancellationToken ct)
    {
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentException($"implausible talk sample rate {sampleRate}");
        if (!await _talkGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new TalkBusyException(CameraName);
        try
        {
            var camera = AnyLive() ?? throw new CameraOfflineException(CameraName);

            // The ability query is a one-shot command: take the command gate for
            // it like every other command, then stream without holding anything.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            TalkAbilityXml? ability;
            try
            {
                ability = await TryAsync(() => camera.GetTalkAbilityAsync(ProbeTimeout, ct)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
            if (ability == null || !ability.AudioType.Equals("adpcm", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{CameraName} does not support two-way talk");

            Log.Info($"{CameraName}: talk session started ({ability.SampleRate} Hz " +
                     $"{ability.AudioType}, {ability.LengthPerEncoder} samples/block, mic at {sampleRate} Hz)");

            // PCM chunks → resample/encode/frame → BcMedia frames → camera.
            var frames = Channel.CreateUnbounded<byte[]>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var encoder = new TalkFrameEncoder(sampleRate, (int)ability.SampleRate, (int)ability.LengthPerEncoder);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in pcm.ReadAllAsync(pumpCts.Token).ConfigureAwait(false))
                        foreach (var frame in encoder.Feed(chunk))
                            frames.Writer.TryWrite(frame);
                    frames.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    frames.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);

            try
            {
                await camera.TalkAsync(ability, frames.Reader, ct).ConfigureAwait(false);
            }
            finally
            {
                // If the camera side bailed first (config rejected, connection
                // dropped), don't sit waiting for more microphone data.
                pumpCts.Cancel();
                await pump.ConfigureAwait(false);
                Log.Info($"{CameraName}: talk session ended");
            }
        }
        finally
        {
            _talkGate.Release();
        }
    }

    /// <summary>
    /// Interprets a Support flag. Values vary by model/firmware: numeric (0 = off),
    /// or strings like "none" vs. "pt"/"ptz" — e.g. the E1 Pro reports ptzMode="pt".
    /// </summary>
    internal static bool SupportFlag(XElement? support, string name)
    {
        var text = support?.Element(name)?.Value.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        return !uint.TryParse(text, out var value) || value != 0;
    }

    /// <summary>A per-channel Support flag: reads the channel's &lt;item&gt; block
    /// (matched by &lt;chnID&gt;) first, then falls back to the host-level element.
    /// Standalone cameras keep their per-channel flags in item 0; NVRs carry one
    /// item per attached camera.</summary>
    /// <summary>The integer value of a channel Support flag (0 when absent), for
    /// bitmask abilities like ledCtrl.</summary>
    internal static uint ChannelSupportValue(XElement? support, int channel, string name)
    {
        if (support == null) return 0;
        var item = support.Elements("item").FirstOrDefault(i => (int?)i.Element("chnID") == channel);
        var text = (item?.Element(name) ?? support.Element(name))?.Value.Trim();
        return uint.TryParse(text, out var v) ? v : 0;
    }

    internal static bool ChannelSupportFlag(XElement? support, int channel, string name)
    {
        if (support == null) return false;
        var item = support.Elements("item")
            .FirstOrDefault(i => (int?)i.Element("chnID") == channel);
        var el = item?.Element(name) ?? support.Element(name);
        var text = el?.Value.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        return !uint.TryParse(text, out var value) || value != 0;
    }

    private static void SetChild(XElement parent, string name, string value)
    {
        var el = parent.Element(name);
        if (el != null) el.Value = value;
        else parent.Add(new XElement(name, value));
    }

    /// <summary>Best-effort query: unsupported/unanswered means null, not an error.</summary>
    private static async Task<T?> TryAsync<T>(Func<Task<T?>> op) where T : class
    {
        try { return await op().ConfigureAwait(false); }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException) { return null; }
    }

    private static async Task<bool> ProbeAsync(Func<Task<XElement?>> op)
    {
        try { return await op().ConfigureAwait(false) != null; }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException) { return false; }
    }
}
