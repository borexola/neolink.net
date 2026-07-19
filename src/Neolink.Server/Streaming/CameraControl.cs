// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Xml.Linq;
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
public sealed record CameraFeatures(bool Ptz, bool Led, bool Pir, bool Battery, bool Talk,
    bool Zoom = false, bool Siren = false, bool Floodlight = false, bool Privacy = false,
    bool WhiteLed = false, bool Spotlight = false, bool Doorbell = false);

/// <summary>White-LED / spotlight state read over the HTTP API (brightness 0-100,
/// on/off, and the auto mode: 0 off, 1 night-auto, 2 always-on, 3 schedule).</summary>
public sealed record WhiteLedState(int Bright, bool On, int Mode);

/// <summary>One stream's current encode selection (Baichuan stream naming).</summary>
public sealed record StreamEncSetting(string Stream, uint Width, uint Height, uint Framerate, uint Bitrate);

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

/// <summary>Everything readable over the camera's HTTP API in one round: a null
/// member means that feature is absent (or the camera rejected the query).</summary>
public sealed record HttpFeatures(ImageSettings? Image, int? Volume, int? WifiSignal,
    IReadOnlyList<PtzPresetInfo>? PtzPresets, IReadOnlyList<QuickReplyFile>? QuickReplies,
    bool? AutoTrack, IReadOnlyList<SdCardInfo>? SdCards,
    int? MdSensitivity = null, IReadOnlyList<AiSensitivity>? AiSensitivities = null,
    OsdSettings? Osd = null);

/// <summary>Discovered camera capabilities: identity, advertised support flags, probed features.</summary>
public sealed record CameraCapabilities(VersionInfoXml? Version, XElement? Support, CameraFeatures Features);

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
    Task RebootAsync(CancellationToken ct);

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

    /// <summary>The camera's Wi-Fi signal reading (bars 0-4 or dBm, firmware-dependent), or null.</summary>
    Task<int?> GetWifiSignalAsync(CancellationToken ct);

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

    /// <summary>The SD-card recordings of one (camera-local) day, or null.</summary>
    Task<IReadOnlyList<SdRecording>?> GetSdRecordingsAsync(DateOnly day, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SdRecording>?>(null);

    /// <summary>Opens a streaming download of one SD-card recording by its Search name.</summary>
    Task<ReolinkHttpApi.SdDownload> OpenSdRecordingAsync(string fileName, CancellationToken ct) =>
        throw new NotSupportedException("SD-card playback is not available for this camera");

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

    // Capabilities are cached for the lifetime of one camera session; a reconnect
    // (new IBcCamera instance) invalidates the cache.
    private IBcCamera? _capsSession;
    private CameraCapabilities? _caps;

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
            if (_caps != null && ReferenceEquals(_capsSession, camera))
                return _caps;

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
            var floodTask = TryAsync(() => camera.GetFloodlightTasksAsync(ProbeTimeout, ct));
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
            await Task.WhenAll(ledTask, pirTask, batteryTask, talkTask, zoomTask, floodTask, privacyTask, whiteLedTask).ConfigureAwait(false);
            bool led = ledTask.Result;
            bool pir = pirTask.Result;
            bool battery = batteryTask.Result;
            bool talk = talkTask.Result != null
                && talkTask.Result.AudioType.Equals("adpcm", StringComparison.OrdinalIgnoreCase);
            // A real zoom lens reports a usable range; fixed-lens cameras answer
            // with max 0 (or not at all) and get no zoom UI.
            bool zoom = ZoomMax(zoomTask.Result) > 0;
            bool floodlight = floodTask.Result != null;
            bool privacy = privacyTask.Result != null; // camera answered the sleep query
            // A physical white spotlight is advertised by ledCtrl bit 2 in Support —
            // this picks out the Lumus/Elite lines and leaves status-LED-only models
            // (E1 Pro) and the doorbell out. Its ON/OFF rides the Baichuan lightState
            // toggle; brightness is only reachable when the HTTP read also succeeded.
            bool spotlight = !floodlight
                && (ChannelSupportValue(support, camera.ChannelId, "ledCtrl") & 4) != 0;
            bool whiteLed = spotlight && whiteLedTask.Result != null;
            // A real video doorbell advertises doorbellVersion in its Support block.
            // Needed as a gate because some non-doorbells (the RLC "Elite" WiFi line)
            // report a doorbellLightState field in their LedState anyway.
            bool doorbell = ChannelSupportValue(support, camera.ChannelId, "doorbellVersion") > 0;

            _caps = new CameraCapabilities(version, support, new CameraFeatures(
                ptz, led, pir, battery, talk, zoom, siren, floodlight, privacy, whiteLed, spotlight, doorbell));
            _capsSession = camera;
            Log.Info($"{CameraName}: capabilities discovered " +
                     $"(ptz={ptz}, led={led}, pir={pir}, battery={battery}, talk={talk}" +
                     $", zoom={zoom}, siren={siren}, floodlight={floodlight}, privacy={privacy}" +
                     $", spotlight={spotlight}, whiteLed={whiteLed}, doorbell={doorbell}" +
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
    internal static long ZoomMax(XElement? zoomFocus) =>
        long.TryParse(zoomFocus?.Element("zoom")?.Element("maxPos")?.Value.Trim(), out var v) ? v : 0;

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

    public Task PtzAsync(string command, float speed, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            await camera.PtzAsync(command, speed, ct).ConfigureAwait(false);
            return null;
        }, ct);

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
        WithCameraAsync(camera => camera.GetFloodlightTasksAsync(ct: ct), ct);

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
        if (_httpApi == null || (!force && DateTime.UtcNow < _httpRetryAt)) return default;
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
                Log.Warn($"{CameraName}: the camera's HTTP API is not answering ({reason}). " +
                         "Picture settings, volume, Wi-Fi signal, PTZ presets and scaled snapshots are " +
                         "unavailable until it does. " + (slow
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
            // No Reolink HTTP API. If this camera has an ONVIF imaging fallback
            // (Lumus and other HTTP-less models), surface just the picture settings
            // it can provide; everything else stays null. Otherwise nothing.
            if (_onvif == null) return null;
            var onvifImage = await GetOnvifImageSettingsAsync(ct).ConfigureAwait(false);
            return onvifImage == null ? null
                : new HttpFeatures(onvifImage, null, null, null, null, null, null);
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
        var image = await Step<ImageSettings>(GetImageSettingsAsync).ConfigureAwait(false);
        var volume = await Step<int?>(GetVolumeAsync).ConfigureAwait(false);
        var wifi = await Step<int?>(GetWifiSignalAsync).ConfigureAwait(false);
        var presets = await Step<IReadOnlyList<PtzPresetInfo>>(GetPtzPresetsAsync).ConfigureAwait(false);
        var replies = await Step<IReadOnlyList<QuickReplyFile>>(GetQuickRepliesAsync).ConfigureAwait(false);
        var autoTrack = await Step<bool?>(GetAutoTrackAsync).ConfigureAwait(false);
        var sdCards = await Step<IReadOnlyList<SdCardInfo>>(GetSdCardsAsync).ConfigureAwait(false);
        var mdSens = await Step<int?>(GetMdSensitivityAsync).ConfigureAwait(false);
        var aiSens = await Step<IReadOnlyList<AiSensitivity>>(GetAiSensitivitiesAsync).ConfigureAwait(false);
        var osd = await Step<OsdSettings>(GetOsdSettingsAsync).ConfigureAwait(false);
        var fresh = new HttpFeatures(image, volume, wifi, presets, replies, autoTrack, sdCards, mdSens, aiSens, osd);

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
            fresh.Osd ?? c.Osd);
        bool freshContent = fresh.Image != null || fresh.Volume != null || fresh.WifiSignal != null
            || fresh.PtzPresets != null || fresh.QuickReplies != null || fresh.AutoTrack != null
            || fresh.SdCards != null || fresh.MdSensitivity != null || fresh.AiSensitivities != null
            || fresh.Osd != null;
        bool hasContent = freshContent
            || merged.Image != null || merged.Volume != null || merged.WifiSignal != null
            || merged.PtzPresets != null || merged.QuickReplies != null || merged.AutoTrack != null
            || merged.SdCards != null || merged.MdSensitivity != null || merged.AiSensitivities != null
            || merged.Osd != null;
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
                     "volume, OSD, Wi-Fi signal, PTZ presets and detection sensitivity stay unavailable. " +
                     "The camera streams fine over Baichuan, so this is the HTTP API specifically: it may be " +
                     "disabled on the camera (enable it in the Reolink app, or set 'http_address'), or " +
                     "something between Neolink and the camera is dropping HTTP traffic " +
                     "(firewall/VLAN rules, Docker networking). Reads resume automatically when it recovers.");
        }
        return merged;
    }

    public async Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct)
    {
        var img = await HttpTryAsync<JsonObject?>(async c => await _httpApi!.GetImageAsync(c).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        // HTTP had nothing (no CGI API, or it failed) — fall back to ONVIF, the
        // only picture-settings path on models like the Lumus. A healthy HTTP
        // camera never reaches this: img is non-null and ONVIF stays untouched.
        if (img == null)
            return _onvif != null ? await GetOnvifImageSettingsAsync(ct).ConfigureAwait(false) : null;
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
        if (_httpApi == null)
        {
            // No Reolink HTTP API — write over ONVIF instead, if this camera has it.
            // ONVIF covers brightness/contrast/saturation/sharpness + day/night; the
            // fields it can't do (hue, anti-flicker, flip/mirror) aren't offered in
            // the panel for an ONVIF camera, so a request for them is a real error.
            if (_onvif == null)
                throw new NotSupportedException($"picture settings need the camera's HTTP API ('{CameraName}' has none)");
            if (hue != null || antiFlicker != null || flip != null || mirror != null)
                throw new NotSupportedException(
                    $"{CameraName} exposes picture settings over ONVIF, which can't set hue, anti-flicker or flip/mirror");
            await _onvif.SetImagingAsync(bright, contrast, saturation, sharpen,
                DayNightToIrCut(dayNight), wideDynamicRange: null, ct).ConfigureAwait(false);
            Log.Info($"{CameraName}: picture settings changed over ONVIF");
            return;
        }
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
        // The write is CONFIRMED — fold it into the cache: flip/mirror restart
        // the camera's video pipeline, and a re-read racing that restart can
        // still echo the OLD value; the cache must not resurrect pre-write state
        // when the next sweep's fresh read fails.
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
        Log.Info($"{CameraName}: picture settings changed" +
                 $"{(flip != null || mirror != null ? " (flip/mirror restarts the video stream)" : "")}");
    }

    public Task<int?> GetVolumeAsync(CancellationToken ct) =>
        HttpTryAsync<int?>(async c => (int?)(await _httpApi!.GetAudioCfgAsync(c).ConfigureAwait(false))["volume"], ct);

    public async Task SetVolumeAsync(int volume, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"the speaker volume needs the camera's HTTP API ('{CameraName}' has none)");
        int vol = Math.Clamp(volume, 0, 100);
        // Minimal payload, like the picture writes — see SetImageSettingsAsync.
        var cfg = new JsonObject { ["channel"] = _httpApi.ChannelId, ["volume"] = vol };
        await _httpApi.SetAudioCfgAsync(cfg, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: speaker volume set to {vol}");
    }

    public Task<int?> GetWifiSignalAsync(CancellationToken ct) =>
        HttpTryAsync<int?>(async c => await _httpApi!.GetWifiSignalAsync(c).ConfigureAwait(false), ct);

    public Task<IReadOnlyList<PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct) =>
        HttpTryAsync<IReadOnlyList<PtzPresetInfo>?>(
            async c => ParsePtzPresets(await _httpApi!.GetPtzPresetsAsync(c).ConfigureAwait(false)), ct);

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
        if (_httpApi == null)
            throw new NotSupportedException($"PTZ presets need the camera's HTTP API ('{CameraName}' has none)");
        await _httpApi.PtzToPresetAsync(id, speed: 32, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: moving to PTZ preset {id}");
    }

    public async Task SavePtzPresetAsync(int id, string name, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"PTZ presets need the camera's HTTP API ('{CameraName}' has none)");
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
                TotalMb: (long?)s["capacity"] ?? 0,
                FreeMb: (long?)s["size"] ?? 0,
                Formatted: ((int?)s["format"] ?? 0) != 0,
                Mounted: ((int?)s["mount"] ?? 0) != 0))
            .ToList();

    // ------------------------------------------- detection sensitivity (HTTP, beta)

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
        var (cfg, isMdAlarm) = await _httpApi.GetMdConfigAsync(ct).ConfigureAwait(false);
        if (!ApplyMdSensitivity(cfg, isMdAlarm, v))
            throw new NotSupportedException($"{CameraName} reports no motion-sensitivity table");
        await _httpApi.SetMdConfigAsync(cfg, isMdAlarm, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: motion sensitivity set to {v}/50");
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

    public async Task<IReadOnlyList<AiSensitivity>?> GetAiSensitivitiesAsync(CancellationToken ct)
    {
        if (_httpApi == null) return null;
        var list = new List<AiSensitivity>();
        foreach (var type in _aiAlarmTypes ?? AiAlarmTypes)
        {
            var one = await HttpTryAsync<AiSensitivity?>(async c =>
                ParseAiSensitivity(type, await _httpApi!.GetAiAlarmAsync(type, c).ConfigureAwait(false)), ct)
                .ConfigureAwait(false);
            if (one != null) list.Add(one);
            // Transport backoff armed mid-sweep — the rest would no-op anyway.
            if (DateTime.UtcNow < _httpRetryAt) return list.Count > 0 ? list : null;
        }
        if (_aiAlarmTypes == null && list.Count > 0)
            _aiAlarmTypes = list.Select(a => a.Type).ToList();
        return list.Count > 0 ? list : null;
    }

    internal static AiSensitivity? ParseAiSensitivity(string type, JsonObject cfg) =>
        (int?)cfg["sensitivity"] is { } sens
            ? new AiSensitivity(type, Math.Clamp(sens, 0, 100), (int?)cfg["stay_time"])
            : null;

    public async Task SetAiSensitivityAsync(string aiType, int sensitivity, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"AI sensitivity needs the camera's HTTP API ('{CameraName}' has none)");
        if (!AiAlarmTypes.Contains(aiType))
            throw new ArgumentException($"unknown AI type '{aiType}'");
        // Read-modify-write of the full AiAlarm object: it carries channel/ai_type
        // and the target-size bounds the firmware expects to see again.
        var cfg = await _httpApi.GetAiAlarmAsync(aiType, ct).ConfigureAwait(false);
        cfg["sensitivity"] = Math.Clamp(sensitivity, 0, 100);
        await _httpApi.SetAiAlarmAsync(cfg, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: {aiType} detection sensitivity set to {Math.Clamp(sensitivity, 0, 100)}/100");
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

    // -------------------------------------------- SD-card recordings (HTTP, beta)

    /// <summary>SD searches walk the card's file table — give them the roomy
    /// snapshot budget, not the 6s config-read cap.</summary>
    public Task<IReadOnlyList<int>?> GetSdRecordingDaysAsync(int year, int month, CancellationToken ct) =>
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

    public Task<IReadOnlyList<SdRecording>?> GetSdRecordingsAsync(DateOnly day, CancellationToken ct) =>
        HttpTryAsync<IReadOnlyList<SdRecording>?>(async c =>
        {
            // The camera records whichever stream its own settings say — usually
            // main; older/battery firmwares list sub. Ask main first, sub when
            // main has nothing — and a stream the firmware REJECTS searching
            // must not abort the other one.
            bool anyWindowFailed = false, anyWindowWorked = false;
            foreach (var streamType in new[] { "main", "sub" })
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
                SizeBytes: (long?)f["size"] ?? 0,
                StreamType: (string?)f["type"] ?? streamType))
            .Where(f => f.Name.Length > 0)
            .OrderBy(f => f.Start)
            .ToList();

    private static DateTime SearchTime(JsonObject? t) => t == null
        ? default
        : new DateTime((int?)t["year"] ?? 1, (int?)t["mon"] ?? 1, (int?)t["day"] ?? 1,
            (int?)t["hour"] ?? 0, (int?)t["min"] ?? 0, (int?)t["sec"] ?? 0);

    public async Task<ReolinkHttpApi.SdDownload> OpenSdRecordingAsync(string fileName, CancellationToken ct)
    {
        if (_httpApi == null)
            throw new NotSupportedException($"SD-card playback needs the camera's HTTP API ('{CameraName}' has none)");
        var download = await _httpApi.DownloadAsync(fileName, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: streaming SD-card recording '{fileName}'" +
                 $"{(download.Length is { } len ? $" ({len / 1024 / 1024} MB)" : "")}");
        return download;
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
