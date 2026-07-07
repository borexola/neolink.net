// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Xml.Linq;
using Neolink.Config;
using Neolink.Protocol;
using Neolink.Recording;
using Neolink.Streaming;
using Neolink.Web;

namespace Neolink.Mqtt;

/// <summary>
/// Bridges Neolink cameras to Home Assistant over MQTT, following HA's MQTT
/// Discovery convention so entities appear automatically.
///
/// Per camera it publishes (all retained, so HA repopulates after a restart):
///   • binary_sensor: motion, person, vehicle, animal   (from the alarm pushes)
///   • sensor:        battery                            (battery cameras)
///   • switch:        PIR sensor                         (PIR cameras)
///   • select:        night vision (auto/on/off)         (IR-capable cameras)
///   • light:         floodlight                         (cameras with a spotlight)
///   • button:        reboot, and PTZ steps              (per capability)
///   • camera:        latest snapshot                    (when the camera supports it)
///
/// Availability is two-level (HA marks entities unavailable when either is off):
/// a bridge-wide Last-Will topic and a per-camera online topic. Commands from HA
/// arrive on ".../{entity}/set" and are executed through <see cref="ICameraControl"/>.
/// </summary>
public sealed class HomeAssistantMqtt
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);
    /// <summary>How long a detection stays "on" after the last alarm push for it.</summary>
    private static readonly TimeSpan MotionOffDelay = TimeSpan.FromSeconds(20);

    internal static readonly string[] DetectionLabels = { "motion", "person", "vehicle", "animal" };

    private readonly MqttConfig _cfg;
    private readonly string _version;
    private readonly MqttClient _client;
    private readonly Dictionary<string, CameraBridge> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _availabilityTopic;

    public HomeAssistantMqtt(MqttConfig cfg, IReadOnlyList<WebCameraInfo> cameras, string version)
    {
        _cfg = cfg;
        _version = version;
        _availabilityTopic = $"{cfg.BaseTopic}/bridge/state";

        _client = new MqttClient(new MqttClientOptions
        {
            Host = cfg.Broker,
            Port = cfg.Port,
            Username = cfg.Username,
            Password = cfg.Password,
            ClientId = cfg.ClientId,
            KeepAliveSeconds = cfg.KeepAliveSeconds,
            Tls = cfg.Tls,
            WillTopic = _availabilityTopic,
            WillPayload = "offline",
            WillRetain = true,
        });

        foreach (var cam in cameras)
            _cameras[Sanitize(cam.Name)] = new CameraBridge(cam, this);

        _client.Connected += OnConnectedAsync;
        _client.MessageReceived += OnMessage;
    }

    internal MqttConfig Config => _cfg;
    internal string Version => _version;
    internal string AvailabilityTopic => _availabilityTopic;

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"MQTT: Home Assistant bridge enabled → {_cfg.Broker}:{_cfg.Port} " +
                 $"(base topic '{_cfg.BaseTopic}'{(_cfg.Discovery ? ", discovery on" : "")})");
        var refresh = Task.Run(() => RefreshLoopAsync(ct), CancellationToken.None);
        try
        {
            await _client.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { await refresh.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Feeds one camera's alarm push into its bridge (called from the camera connection).</summary>
    public void OnMotion(string cameraName, MotionPush push)
    {
        if (_cameras.TryGetValue(Sanitize(cameraName), out var cam))
            cam.OnMotion(push);
    }

    // ------------------------------------------------------------------ MQTT plumbing

    internal Task PublishAsync(string topic, string payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: true, ct);

    internal Task PublishAsync(string topic, byte[] payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: true, ct);

    private async Task OnConnectedAsync()
    {
        await _client.PublishAsync(_availabilityTopic, "online", retain: true, CancellationToken.None)
            .ConfigureAwait(false);
        // One wildcard per camera captures every ".../{entity}/set" command.
        var subs = _cameras.Values.Select(c => $"{_cfg.BaseTopic}/{c.Id}/+/set").ToList();
        await _client.SubscribeAsync(subs, CancellationToken.None).ConfigureAwait(false);
        foreach (var cam in _cameras.Values)
            await cam.AnnounceAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void OnMessage(string topic, byte[] payload)
    {
        // topic = {base}/{camId}/{entity}/set
        var prefix = _cfg.BaseTopic + "/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal)) return;
        var rest = topic[prefix.Length..].Split('/');
        if (rest.Length != 3 || rest[2] != "set") return;
        if (!_cameras.TryGetValue(rest[0], out var cam)) return;
        _ = cam.HandleCommandAsync(rest[1], System.Text.Encoding.UTF8.GetString(payload));
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RefreshInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!_client.IsConnected) continue;
            foreach (var cam in _cameras.Values)
            {
                try { await cam.RefreshAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug($"MQTT: refresh for '{cam.Id}' failed: {Log.Flatten(ex)}");
                }
            }
        }
    }

    internal static TimeSpan OffDelay => MotionOffDelay;

    /// <summary>Camera name → a topic/id-safe token ([a-z0-9_]).</summary>
    internal static string Sanitize(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
}

/// <summary>The Home Assistant surface of one camera: discovery, state and commands.</summary>
internal sealed class CameraBridge
{
    private readonly WebCameraInfo _cam;
    private readonly HomeAssistantMqtt _hub;
    private readonly ICameraControl _control;
    private readonly string _base;      // {baseTopic}/{id}
    private readonly Dictionary<string, DateTime> _activeUntil = new();
    private readonly HashSet<string> _sensorOn = new();

    private bool _featuresAnnounced;
    private bool _hasFloodlight, _hasIr, _hasSnapshot;
    private string? _model;
    private DateTime _lastSnapshot = DateTime.MinValue;
    private bool _lastOnline;

    public string Id { get; }

    public CameraBridge(WebCameraInfo cam, HomeAssistantMqtt hub)
    {
        _cam = cam;
        _hub = hub;
        _control = cam.Control;
        Id = HomeAssistantMqtt.Sanitize(cam.Name);
        _base = $"{hub.Config.BaseTopic}/{Id}";
    }

    private string StateTopic(string entity) => $"{_base}/{entity}";
    private string CommandTopic(string entity) => $"{_base}/{entity}/set";
    private string AvailabilityTopic => $"{_base}/state";

    // ------------------------------------------------------------------ discovery + announce

    /// <summary>
    /// (Re)publishes discovery for what is known so far. Called on every connect;
    /// the feature entities are added once capabilities have been probed.
    /// </summary>
    public async Task AnnounceAsync(CancellationToken ct)
    {
        await PublishAvailabilityAsync(ct).ConfigureAwait(false);

        if (!_cam.SupportsEvents)
        {
            // Generic RTSP camera: no detection pushes and no Baichuan commands —
            // motion sensors and a reboot button would be dead weight in HA. A
            // connectivity sensor is the honest surface (and gives the device an
            // entity, so it actually shows up).
            await AnnounceEntityAsync("binary_sensor", "status", ConnectivityConfig(), ct).ConfigureAwait(false);
            return;
        }

        // Detection sensors and reboot exist on every Baichuan camera — announce immediately.
        foreach (var label in HomeAssistantMqtt.DetectionLabels)
            await AnnounceEntityAsync("binary_sensor", label, BinarySensorConfig(label), ct).ConfigureAwait(false);
        await AnnounceEntityAsync("button", "reboot", ButtonConfig("Reboot", "reboot", "restart"), ct).ConfigureAwait(false);

        // Publish current detection states so HA doesn't show "unknown".
        foreach (var label in HomeAssistantMqtt.DetectionLabels)
            await _hub.PublishAsync(StateTopic(label), _sensorOn.Contains(label) ? "ON" : "OFF", ct).ConfigureAwait(false);

        if (_featuresAnnounced)
            await AnnounceFeaturesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Generic cameras: online/offline as a diagnostic connectivity sensor.</summary>
    private object ConnectivityConfig() => new
    {
        name = "Online",
        unique_id = $"neolink_{Id}_status",
        state_topic = AvailabilityTopic,
        payload_on = "online",
        payload_off = "offline",
        device_class = "connectivity",
        entity_category = "diagnostic",
        device = Device(),
        // Only the bridge's own liveness gates this one — the sensor itself IS the camera state.
        availability = new object[] { new { topic = _hub.AvailabilityTopic } },
        availability_mode = "all",
    };

    private async Task AnnounceFeaturesAsync(CancellationToken ct)
    {
        var caps = await TryGetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (caps?.Features is not { } f) return;

        if (f.Battery)
            await AnnounceEntityAsync("sensor", "battery", BatterySensorConfig(), ct).ConfigureAwait(false);
        if (_hasIr)
            await AnnounceEntityAsync("select", "ir", IrSelectConfig(), ct).ConfigureAwait(false);
        if (_hasFloodlight)
            await AnnounceEntityAsync("light", "floodlight", FloodlightConfig(), ct).ConfigureAwait(false);
        if (f.Pir)
            await AnnounceEntityAsync("switch", "pir", SwitchConfig("PIR sensor", "pir"), ct).ConfigureAwait(false);
        if (f.Ptz)
            foreach (var dir in new[] { "up", "down", "left", "right" })
                await AnnounceEntityAsync("button", $"ptz_{dir}",
                    ButtonConfig($"Pan {dir}", $"ptz_{dir}", null), ct).ConfigureAwait(false);
        if (_hasSnapshot)
            await AnnounceEntityAsync("camera", "snapshot", CameraConfig(), ct).ConfigureAwait(false);
    }

    private Task AnnounceEntityAsync(string component, string objectId, object config, CancellationToken ct)
    {
        if (!_hub.Config.Discovery) return Task.CompletedTask;
        var topic = $"{_hub.Config.DiscoveryPrefix}/{component}/neolink_{Id}/{objectId}/config";
        return _hub.PublishAsync(topic, JsonSerializer.Serialize(config), ct);
    }

    /// <summary>The device block shared by every entity so HA groups them under one device.</summary>
    private object Device() => new
    {
        identifiers = new[] { $"neolink_{Id}" },
        name = _cam.Name,
        manufacturer = _cam.SupportsEvents ? "Reolink (via Neolink.NET)" : "Neolink.NET",
        model = !_cam.SupportsEvents ? "Generic RTSP camera"
            : string.IsNullOrEmpty(_model) ? "Baichuan camera" : _model,
        sw_version = _hub.Version,
    };

    private object[] Availability() => new object[]
    {
        new { topic = _hub.AvailabilityTopic },   // bridge alive (Last Will)
        new { topic = AvailabilityTopic },        // this camera online
    };

    private object BinarySensorConfig(string label) => new
    {
        name = label switch { "motion" => "Motion", "person" => "Person", "vehicle" => "Vehicle", "animal" => "Animal", _ => label },
        unique_id = $"neolink_{Id}_{label}",
        state_topic = StateTopic(label),
        payload_on = "ON",
        payload_off = "OFF",
        device_class = "motion",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object BatterySensorConfig() => new
    {
        name = "Battery",
        unique_id = $"neolink_{Id}_battery",
        state_topic = StateTopic("battery"),
        json_attributes_topic = StateTopic("battery/attr"),
        device_class = "battery",
        unit_of_measurement = "%",
        state_class = "measurement",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object IrSelectConfig() => new
    {
        name = "Night vision",
        unique_id = $"neolink_{Id}_ir",
        state_topic = StateTopic("ir"),
        command_topic = CommandTopic("ir"),
        options = new[] { "auto", "on", "off" },
        icon = "mdi:light-flood-down",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object FloodlightConfig() => new
    {
        name = "Floodlight",
        unique_id = $"neolink_{Id}_floodlight",
        state_topic = StateTopic("floodlight"),
        command_topic = CommandTopic("floodlight"),
        payload_on = "ON",
        payload_off = "OFF",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object SwitchConfig(string name, string entity) => new
    {
        name,
        unique_id = $"neolink_{Id}_{entity}",
        state_topic = StateTopic(entity),
        command_topic = CommandTopic(entity),
        payload_on = "ON",
        payload_off = "OFF",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object ButtonConfig(string name, string entity, string? deviceClass) => new
    {
        name,
        unique_id = $"neolink_{Id}_{entity}",
        command_topic = CommandTopic(entity),
        payload_press = "PRESS",
        device_class = deviceClass,
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object CameraConfig() => new
    {
        name = "Snapshot",
        unique_id = $"neolink_{Id}_snapshot",
        topic = StateTopic("snapshot"),
        image_encoding = "b64",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    // ------------------------------------------------------------------ motion state

    public void OnMotion(MotionPush push)
    {
        var now = DateTime.UtcNow;
        var labels = EventRecorder.LabelsOf(push); // person/vehicle/animal/... or "motion"
        if (push.Active)
        {
            _activeUntil["motion"] = now + HomeAssistantMqtt.OffDelay;
            foreach (var l in labels)
                if (Array.IndexOf(HomeAssistantMqtt.DetectionLabels, l) >= 0)
                    _activeUntil[l] = now + HomeAssistantMqtt.OffDelay;
        }
        else
        {
            // All-clear: let the sensors fall to OFF shortly (a small grace avoids flapping).
            var soon = now + TimeSpan.FromSeconds(3);
            foreach (var l in HomeAssistantMqtt.DetectionLabels)
                if (_activeUntil.TryGetValue(l, out var until) && until > soon)
                    _activeUntil[l] = soon;
        }
        _ = PublishSensorEdgesAsync(CancellationToken.None);
    }

    /// <summary>Publishes ON/OFF only on transitions (edge-triggered), keeping traffic minimal.</summary>
    private async Task PublishSensorEdgesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var label in HomeAssistantMqtt.DetectionLabels)
        {
            bool on = _activeUntil.TryGetValue(label, out var until) && until > now;
            if (on && _sensorOn.Add(label))
                await _hub.PublishAsync(StateTopic(label), "ON", ct).ConfigureAwait(false);
            else if (!on && _sensorOn.Remove(label))
                await _hub.PublishAsync(StateTopic(label), "OFF", ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ periodic refresh

    public async Task RefreshAsync(CancellationToken ct)
    {
        await PublishSensorEdgesAsync(ct).ConfigureAwait(false); // time out stale detections
        await PublishAvailabilityAsync(ct).ConfigureAwait(false);
        if (!_control.Online) return;

        // First time online: probe capabilities, then announce the feature entities.
        if (!_featuresAnnounced)
        {
            var caps = await TryGetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (caps?.Features is { } f)
            {
                _model = caps.Version?.Model;
                if (f.Led) await ProbeLedAsync(ct).ConfigureAwait(false);
                _hasSnapshot = await ProbeSnapshotAsync(ct).ConfigureAwait(false);
                _featuresAnnounced = true;
                await AnnounceAsync(ct).ConfigureAwait(false);
            }
        }

        var caps2 = await TryGetCapabilitiesAsync(ct).ConfigureAwait(false);
        var feat = caps2?.Features;
        if (feat?.Battery == true) await PublishBatteryAsync(ct).ConfigureAwait(false);
        if (feat?.Led == true) await PublishLedAsync(ct).ConfigureAwait(false);
        if (feat?.Pir == true) await PublishPirAsync(ct).ConfigureAwait(false);
        if (_hasSnapshot && DateTime.UtcNow - _lastSnapshot > TimeSpan.FromMinutes(2))
            await PublishSnapshotAsync(ct).ConfigureAwait(false);
    }

    private async Task PublishAvailabilityAsync(CancellationToken ct)
    {
        bool online = _control.Online;
        if (online == _lastOnline && _lastSnapshot != DateTime.MinValue) { /* still publish periodically */ }
        _lastOnline = online;
        await _hub.PublishAsync(AvailabilityTopic, online ? "online" : "offline", ct).ConfigureAwait(false);
    }

    private async Task PublishBatteryAsync(CancellationToken ct)
    {
        var battery = await TryAsync(() => _control.GetBatteryInfoAsync(ct)).ConfigureAwait(false);
        if (battery == null) return;
        if (Num(battery, "batteryPercent") is { } pct)
            await _hub.PublishAsync(StateTopic("battery"), pct.ToString(), ct).ConfigureAwait(false);
        var attrs = new Dictionary<string, object?>
        {
            ["charge_status"] = Str(battery, "chargeStatus"),
            ["temperature"] = Num(battery, "temperature"),
        };
        await _hub.PublishAsync(StateTopic("battery/attr"), JsonSerializer.Serialize(attrs), ct).ConfigureAwait(false);
    }

    private async Task PublishLedAsync(CancellationToken ct)
    {
        var led = await TryAsync(() => _control.GetLedStateAsync(ct)).ConfigureAwait(false);
        if (led == null) return;
        if (_hasIr && Str(led, "state") is { Length: > 0 } ir)
            await _hub.PublishAsync(StateTopic("ir"), IrToHa(ir), ct).ConfigureAwait(false);
        if (_hasFloodlight && Str(led, "lightState") is { Length: > 0 } fl)
            await _hub.PublishAsync(StateTopic("floodlight"), fl == "open" ? "ON" : "OFF", ct).ConfigureAwait(false);
    }

    private async Task PublishPirAsync(CancellationToken ct)
    {
        var pir = await TryAsync(() => _control.GetPirStateAsync(ct)).ConfigureAwait(false);
        if (pir == null) return;
        await _hub.PublishAsync(StateTopic("pir"), Num(pir, "enable") == 1 ? "ON" : "OFF", ct).ConfigureAwait(false);
    }

    private async Task PublishSnapshotAsync(CancellationToken ct)
    {
        var jpeg = await TryAsync(() => _control.SnapshotAsync(ct)).ConfigureAwait(false);
        if (jpeg is { Length: > 100 })
        {
            _lastSnapshot = DateTime.UtcNow;
            // HA camera "image_encoding: b64" expects base64 text.
            await _hub.PublishAsync(StateTopic("snapshot"), Convert.ToBase64String(jpeg), ct).ConfigureAwait(false);
        }
    }

    private async Task ProbeLedAsync(CancellationToken ct)
    {
        var led = await TryAsync(() => _control.GetLedStateAsync(ct)).ConfigureAwait(false);
        if (led == null) return;
        _hasIr = Str(led, "state") is { Length: > 0 };
        _hasFloodlight = Str(led, "lightState") is { Length: > 0 };
    }

    private async Task<bool> ProbeSnapshotAsync(CancellationToken ct)
    {
        var jpeg = await TryAsync(() => _control.SnapshotAsync(ct)).ConfigureAwait(false);
        if (jpeg is not { Length: > 100 }) return false;
        _lastSnapshot = DateTime.UtcNow;
        await _hub.PublishAsync(StateTopic("snapshot"), Convert.ToBase64String(jpeg), ct).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ commands from HA

    public async Task HandleCommandAsync(string entity, string payload)
    {
        var ct = CancellationToken.None;
        try
        {
            switch (entity)
            {
                case "reboot" when payload == "PRESS":
                    await _control.RebootAsync(ct).ConfigureAwait(false);
                    break;
                case "ir":
                    await _control.SetLedStateAsync(HaToIr(payload), null, ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "floodlight":
                    await _control.SetLedStateAsync(null, payload == "ON" ? "open" : "close", ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "pir":
                    await _control.SetPirEnabledAsync(payload == "ON", ct).ConfigureAwait(false);
                    await PublishPirAsync(ct).ConfigureAwait(false);
                    break;
                case "ptz_up" or "ptz_down" or "ptz_left" or "ptz_right" when payload == "PRESS":
                    await PtzStepAsync(entity["ptz_".Length..], ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MQTT: command '{entity}' for '{Id}' failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>A momentary PTZ nudge: move for a short beat, then stop.</summary>
    private async Task PtzStepAsync(string direction, CancellationToken ct)
    {
        await _control.PtzAsync(direction, 32f, ct).ConfigureAwait(false);
        await Task.Delay(600, ct).ConfigureAwait(false);
        await _control.PtzAsync("stop", 32f, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<CameraCapabilities?> TryGetCapabilitiesAsync(CancellationToken ct)
    {
        if (!_control.Online) return null;
        try { return await _control.GetCapabilitiesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is CameraOfflineException or CameraCommandException or TimeoutException) { return null; }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> op) where T : class
    {
        try { return await op().ConfigureAwait(false); }
        catch (Exception ex) when (ex is CameraOfflineException or CameraCommandException or TimeoutException
                                    or System.IO.IOException) { return null; }
    }

    private static string IrToHa(string s) => s switch { "open" => "on", "close" => "off", _ => "auto" };
    private static string HaToIr(string s) => s switch { "on" => "open", "off" => "close", _ => "auto" };

    private static string? Str(XElement el, string name) => el.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;
    private static long? Num(XElement el, string name) =>
        long.TryParse(el.Element(name)?.Value.Trim(), out var n) ? n : null;
}
