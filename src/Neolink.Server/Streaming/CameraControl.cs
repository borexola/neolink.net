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
/// <see cref="ImageSettings.AntiFlickerValues"/>.</summary>
public sealed record ImageSettings(int? Bright, int? Contrast, int? Saturation, int? Hue, int? Sharpen,
    string? DayNight, string? AntiFlicker, bool? Flip, bool? Mirror)
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

/// <summary>Everything readable over the camera's HTTP API in one round: a null
/// member means that feature is absent (or the camera rejected the query).</summary>
public sealed record HttpFeatures(ImageSettings? Image, int? Volume, int? WifiSignal,
    IReadOnlyList<PtzPresetInfo>? PtzPresets, IReadOnlyList<QuickReplyFile>? QuickReplies,
    bool? AutoTrack, IReadOnlyList<SdCardInfo>? SdCards);

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
        IReadOnlyList<ILiveCameraSource>? allSources = null)
    {
        _source = source;
        _sources = allSources is { Count: > 0 } ? allSources : new[] { source };
        _httpApi = httpApi;
    }

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
        if (_httpApi != null
            && await HttpTryAsync<byte[]?>(async c =>
                   await _httpApi!.SnapAsync(640, 360, c).ConfigureAwait(false), ct).ConfigureAwait(false)
               is { } small)
        {
            Log.Debug($"{CameraName}: small snapshot via HTTP Snap ({small.Length / 1024} KB)");
            return small;
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

    private async Task<T?> HttpTryAsync<T>(Func<CancellationToken, Task<T?>> op, CancellationToken ct)
    {
        if (_httpApi == null || DateTime.UtcNow < _httpRetryAt) return default;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpCallTimeout);
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
            if (!_httpUnreachableWarned)
            {
                _httpUnreachableWarned = true;
                Log.Warn($"{CameraName}: the camera's HTTP API is unreachable ({Log.Flatten(ex)}). " +
                         "Picture settings, volume, Wi-Fi signal, PTZ presets and scaled snapshots are " +
                         "unavailable until it answers. Many cameras ship with HTTP disabled — enable it in " +
                         "the Reolink app (Settings > Network > Advanced > Port Settings), or set " +
                         "'http_address' if the API lives on another host/port.");
            }
            else
            {
                Log.Debug($"{CameraName}: HTTP API still unreachable ({ex.Message}) — skipping HTTP reads for 60s");
            }
            return default;
        }
    }

    private void NoteHttpReachable()
    {
        _httpRetryAt = default;
        _httpLoginWarned = false;
        if (_httpUnreachableWarned)
        {
            _httpUnreachableWarned = false;
            Log.Info($"{CameraName}: the camera's HTTP API is reachable again — HTTP-backed features restored");
        }
    }

    public async Task<HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct)
    {
        if (_httpApi == null) return null;
        // Sequential on purpose: the HTTP client serializes requests anyway, and the
        // first transport failure arms the backoff so the rest return immediately.
        var image = await GetImageSettingsAsync(ct).ConfigureAwait(false);
        var volume = await GetVolumeAsync(ct).ConfigureAwait(false);
        var wifi = await GetWifiSignalAsync(ct).ConfigureAwait(false);
        var presets = await GetPtzPresetsAsync(ct).ConfigureAwait(false);
        var replies = await GetQuickRepliesAsync(ct).ConfigureAwait(false);
        var autoTrack = await GetAutoTrackAsync(ct).ConfigureAwait(false);
        var sdCards = await GetSdCardsAsync(ct).ConfigureAwait(false);
        return new HttpFeatures(image, volume, wifi, presets, replies, autoTrack, sdCards);
    }

    public async Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct)
    {
        var img = await HttpTryAsync<JsonObject?>(async c => await _httpApi!.GetImageAsync(c).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        if (img == null) return null;
        // The ISP half (day/night, flip, ...) is optional — picture sliders alone
        // are still worth showing if a firmware rejects GetIsp.
        var isp = await HttpTryAsync<JsonObject?>(async c => await _httpApi!.GetIspAsync(c).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        return ParseImageSettings(img, isp);
    }

    internal static ImageSettings ParseImageSettings(JsonObject img, JsonObject? isp) => new(
        Bright: (int?)img["bright"], Contrast: (int?)img["contrast"],
        Saturation: (int?)img["saturation"], Hue: (int?)img["hue"], Sharpen: (int?)img["sharpen"],
        DayNight: (string?)isp?["dayNight"],
        AntiFlicker: (string?)isp?["antiFlicker"],
        Flip: isp?["rotation"] is { } rot ? (int?)rot != 0 : null,
        Mirror: isp?["mirroring"] is { } mir ? (int?)mir != 0 : null);

    // MINIMAL write payloads ({"channel": n} + only the changed fields), matching
    // what Reolink's own clients send. Round-tripping the full Get object breaks:
    // it carries nested read-only structures (gain, shutter, ...) that firmwares
    // reject wholesale (observed on the Elite WiFi line).
    public async Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue, int? sharpen,
        string? dayNight, string? antiFlicker, bool? flip, bool? mirror, CancellationToken ct)
    {
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

    /// <summary>Cached GetAbility verdict for auto-tracking — the ability table is
    /// static per device, so one HTTP read decides it for this camera's lifetime.</summary>
    private bool? _aiTrackSupported;

    /// <summary>The authoritative gate: GetAbility's supportAITrack version. Config
    /// reads are NOT trusted for this — firmwares include an aiTrack field on models
    /// without the feature (observed on the Elite WiFi line).</summary>
    private async Task<bool> AiTrackSupportedAsync(CancellationToken ct)
    {
        if (_aiTrackSupported is { } known) return known;
        var ability = await _httpApi!.GetAbilityAsync(ct).ConfigureAwait(false);
        bool supported = SupportsAiTrack(ability, _httpApi.ChannelId);
        _aiTrackSupported = supported;
        return supported;
    }

    /// <summary>supportAITrack ver &gt; 0 in the channel's ability block (falling back
    /// to the first block — standalone cameras have exactly one).</summary>
    internal static bool SupportsAiTrack(JsonObject ability, int channel)
    {
        var chn = ability["abilityChn"] as JsonArray;
        var entry = (chn?.ElementAtOrDefault(channel) ?? chn?.OfType<JsonObject>().FirstOrDefault()) as JsonObject;
        return (int?)entry?["supportAITrack"]?["ver"] is > 0;
    }

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
