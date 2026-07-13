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
    bool Zoom = false, bool Siren = false, bool Floodlight = false, bool Privacy = false);

/// <summary>One stream's current encode selection (Baichuan stream naming).</summary>
public sealed record StreamEncSetting(string Stream, uint Width, uint Height, uint Framerate, uint Bitrate);

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
    Task<XElement?> GetLedStateAsync(CancellationToken ct);
    Task SetLedStateAsync(string? state, string? lightState, CancellationToken ct);
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
    private readonly ReolinkHttpApi? _httpApi;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Capabilities are cached for the lifetime of one camera session; a reconnect
    // (new IBcCamera instance) invalidates the cache.
    private IBcCamera? _capsSession;
    private CameraCapabilities? _caps;

    public CameraControl(ILiveCameraSource source, ReolinkHttpApi? httpApi = null)
    {
        _source = source;
        _httpApi = httpApi;
    }

    public string CameraName => _source.Name;
    public bool Online => _source.LiveCamera != null;

    private async Task<T> WithCameraAsync<T>(Func<IBcCamera, Task<T>> op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var camera = _source.LiveCamera ?? throw new CameraOfflineException(CameraName);
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
            await Task.WhenAll(ledTask, pirTask, batteryTask, talkTask, zoomTask, floodTask, privacyTask).ConfigureAwait(false);
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

            _caps = new CameraCapabilities(version, support, new CameraFeatures(
                ptz, led, pir, battery, talk, zoom, siren, floodlight, privacy));
            _capsSession = camera;
            Log.Info($"{CameraName}: capabilities discovered " +
                     $"(ptz={ptz}, led={led}, pir={pir}, battery={battery}, talk={talk}" +
                     $", zoom={zoom}, siren={siren}, floodlight={floodlight}, privacy={privacy}" +
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

    public Task<XElement?> GetLedStateAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetLedStateAsync(ct: ct), ct);

    public Task SetLedStateAsync(string? state, string? lightState, CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            // Read-modify-write: only touch the requested fields, keep the rest verbatim.
            var led = await camera.GetLedStateAsync(ct: ct).ConfigureAwait(false)
                ?? throw new NotSupportedException($"{CameraName} does not expose LED state");
            if (state != null) SetChild(led, "state", state);
            if (lightState != null) SetChild(led, "lightState", lightState);
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
            var camera = _source.LiveCamera ?? throw new CameraOfflineException(CameraName);

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
