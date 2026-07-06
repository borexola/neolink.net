using System.Text.Json.Nodes;
using System.Xml.Linq;
using Neolink.Bc.Xml;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>Thrown when a control command is issued while the camera is disconnected.</summary>
public sealed class CameraOfflineException : Exception
{
    public CameraOfflineException(string name) : base($"Camera '{name}' is offline (reconnecting)") { }
}

/// <summary>Features a camera was found to support, discovered by probing.</summary>
public sealed record CameraFeatures(bool Ptz, bool Led, bool Pir, bool Battery);

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
            // rejects it (non-200) or stays silent.
            bool ptz = SupportFlag(support, "ptzMode") || SupportFlag(support, "ptzCfg");
            bool led = await ProbeAsync(() => camera.GetLedStateAsync(ProbeTimeout, ct)).ConfigureAwait(false);
            bool pir = await ProbeAsync(() => camera.GetPirStateAsync(ProbeTimeout, ct)).ConfigureAwait(false);
            bool battery = await ProbeAsync(() => camera.GetBatteryInfoAsync(ProbeTimeout, ct)).ConfigureAwait(false);

            _caps = new CameraCapabilities(version, support, new CameraFeatures(ptz, led, pir, battery));
            _capsSession = camera;
            Log.Info($"{CameraName}: capabilities discovered " +
                     $"(ptz={ptz}, led={led}, pir={pir}, battery={battery}" +
                     $"{(version != null && version.Model.Length > 0 ? $", model={version.Model}" : "")})");
            return _caps;
        }, ct);

    public Task<StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct) =>
        WithCameraAsync(camera => camera.GetStreamInfoAsync(ct: ct), ct);

    public bool CanSetStreamSettings => _httpApi != null;

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

    public Task RebootAsync(CancellationToken ct) =>
        WithCameraAsync<object?>(async camera =>
        {
            Log.Warn($"{CameraName}: reboot requested via web API");
            await camera.RebootAsync(ct).ConfigureAwait(false);
            return null;
        }, ct);

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
