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
///   • binary_sensor: package, line crossing, intrusion,
///                    loitering                          (lazily, on first detection)
///   • binary_sensor: siren                              (from the status pushes)
///   • event:         doorbell press                     (video doorbells; not retained)
///   • binary_sensor: visitor                            (doorbell press pulse; HA
///                                                        auto-clears it via off_delay)
///   • sensor:        battery                            (battery cameras)
///   • sensor:        Wi-Fi signal, diagnostic           (from the status pushes)
///   • switch:        PIR sensor                         (PIR cameras)
///   • select:        night vision (auto/on/off)         (IR-capable cameras)
///   • light:         floodlight                         (cameras with a spotlight)
///   • button:        reboot, and PTZ steps              (per capability)
///   • camera:        latest snapshot                    (when the camera supports it)
///
/// The SERVER also reports itself: a "Neolink.NET Server" device with health
/// sensors straight off the monitor page (CPU, memory, disk, recordings size,
/// write rate, viewers, cameras online/recording, start time), published every
/// mqtt.stats_interval seconds (default 60; 0 turns the device off).
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

    /// <summary>Every detection label that gets a binary sensor. The classic four
    /// are announced on every camera; the smart labels (package + the perimeter
    /// events set up in the Reolink app) announce lazily on their first push, so
    /// cameras without those features never grow dead entities in HA.</summary>
    internal static readonly string[] DetectionLabels =
        { "motion", "person", "vehicle", "animal", "package", "line-crossing", "intrusion", "loitering" };
    internal static readonly string[] CoreLabels = { "motion", "person", "vehicle", "animal" };

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

    /// <summary>Resource sampler feeding the server's own HA device; when null
    /// (or stats_interval is 0) the server device is not announced.</summary>
    public Web.SystemMonitor? Monitor { get; init; }

    private bool ServerStatsEnabled => Monitor != null && _cfg.StatsIntervalSeconds > 0;

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"MQTT: Home Assistant bridge enabled → {_cfg.Broker}:{_cfg.Port} " +
                 $"(base topic '{_cfg.BaseTopic}'{(_cfg.Discovery ? ", discovery on" : "")}" +
                 $"{(ServerStatsEnabled ? $", server health every {_cfg.StatsIntervalSeconds}s" : "")})");
        var refresh = Task.Run(() => RefreshLoopAsync(ct), CancellationToken.None);
        var stats = ServerStatsEnabled
            ? Task.Run(() => ServerStatsLoopAsync(ct), CancellationToken.None)
            : Task.CompletedTask;
        try
        {
            await _client.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { await refresh.ConfigureAwait(false); } catch { }
            try { await stats.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Feeds one camera's alarm push into its bridge (called from the camera connection).</summary>
    public void OnMotion(string cameraName, MotionPush push)
    {
        if (_cameras.TryGetValue(Sanitize(cameraName), out var cam))
            cam.OnMotion(push);
    }

    /// <summary>Feeds one camera's status push (Wi-Fi signal, siren, floodlight, ...) into its bridge.</summary>
    public void OnStatus(string cameraName, StatusPush push)
    {
        if (_cameras.TryGetValue(Sanitize(cameraName), out var cam))
            cam.OnStatus(push);
    }

    // ------------------------------------------------------------------ MQTT plumbing

    internal Task PublishAsync(string topic, string payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: true, ct);

    internal Task PublishAsync(string topic, byte[] payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: true, ct);

    /// <summary>Publish WITHOUT retain — HA event entities must never replay a
    /// stale occurrence from the broker after a restart.</summary>
    internal Task PublishTransientAsync(string topic, string payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: false, ct);

    private async Task OnConnectedAsync()
    {
        await _client.PublishAsync(_availabilityTopic, "online", retain: true, CancellationToken.None)
            .ConfigureAwait(false);
        // One wildcard per camera captures every ".../{entity}/set" command.
        var subs = _cameras.Values.Select(c => $"{_cfg.BaseTopic}/{c.Id}/+/set").ToList();
        await _client.SubscribeAsync(subs, CancellationToken.None).ConfigureAwait(false);
        foreach (var cam in _cameras.Values)
            await cam.AnnounceAsync(CancellationToken.None).ConfigureAwait(false);
        if (ServerStatsEnabled)
        {
            await AnnounceServerAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishServerStatsAsync(CancellationToken.None).ConfigureAwait(false);
        }
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

    // ------------------------------------------------------------------ server health device

    /// <summary>The server's own sensors: key (topic leaf), display name, unit,
    /// device class, icon, and whether HA should file it under diagnostics.
    /// Internal so the self-tests can pin the contract.</summary>
    internal static readonly (string Key, string Name, string? Unit, string? DeviceClass, string? Icon, bool Diagnostic)[]
        ServerSensors =
    {
        ("cpu", "CPU usage", "%", null, "mdi:cpu-64-bit", false),
        ("memory", "Memory", "MB", null, "mdi:memory", false),
        ("disk_free", "Storage free", "GB", null, "mdi:harddisk", false),
        ("disk_used_pct", "Storage used", "%", null, "mdi:harddisk", false),
        ("recordings_size", "Recordings size", "GB", null, "mdi:filmstrip-box-multiple", false),
        ("write_rate", "Recording write rate", "MB/s", null, "mdi:content-save-move", false),
        ("viewers", "Viewers", null, null, "mdi:eye", false),
        ("cameras_online", "Cameras online", null, null, "mdi:cctv", false),
        ("cameras_recording", "Cameras recording", null, null, "mdi:record-rec", false),
        ("started", "Started", null, "timestamp", null, true),
    };

    /// <summary>One monitor sample → the state payloads to publish. Sensors whose
    /// source is unavailable (no disk volume, recording off) are simply absent.
    /// Internal/static so the self-tests can verify values and skip rules.</summary>
    internal static List<(string Key, string Value)> ServerStatePayloads(Web.SystemSample s, int camerasOnline)
    {
        const double MB = 1024.0 * 1024.0, GB = MB * 1024.0;
        var list = new List<(string, string)>
        {
            ("cpu", s.CpuPercent.ToString("0.#")),
            ("memory", (s.WorkingSetBytes / MB).ToString("0")),
            ("write_rate", s.StorageMbPerSec.ToString("0.##")),
            ("viewers", s.Viewers.ToString()),
            ("cameras_online", camerasOnline.ToString()),
            ("cameras_recording", s.RecordingCameras.ToString()),
        };
        if (s.DiskTotalBytes > 0)
        {
            list.Add(("disk_free", (s.DiskFreeBytes / GB).ToString("0.#")));
            list.Add(("disk_used_pct",
                ((1 - (double)s.DiskFreeBytes / s.DiskTotalBytes) * 100).ToString("0.#")));
        }
        if (s.RecordingsBytes >= 0)
            list.Add(("recordings_size", (s.RecordingsBytes / GB).ToString("0.##")));
        return list;
    }

    private string ServerTopic(string key) => $"{_cfg.BaseTopic}/server/{key}";

    private object ServerDevice() => new
    {
        identifiers = new[] { "neolink_server" },
        name = "Neolink.NET Server",
        manufacturer = "Neolink.NET",
        model = "Camera bridge / NVR",
        sw_version = _version,
    };

    private async Task AnnounceServerAsync(CancellationToken ct)
    {
        if (!_cfg.Discovery) return;
        foreach (var s in ServerSensors)
        {
            var config = new
            {
                name = s.Name,
                unique_id = $"neolink_server_{s.Key}",
                state_topic = ServerTopic(s.Key),
                unit_of_measurement = s.Unit,
                device_class = s.DeviceClass,
                // Numeric gauges chart as measurements; the timestamp doesn't.
                state_class = s.DeviceClass == "timestamp" ? null : "measurement",
                icon = s.Icon,
                entity_category = s.Diagnostic ? "diagnostic" : null,
                device = ServerDevice(),
                availability = new object[] { new { topic = _availabilityTopic } },
                availability_mode = "all",
            };
            await PublishAsync($"{_cfg.DiscoveryPrefix}/sensor/neolink_server/{s.Key}/config",
                JsonSerializer.Serialize(config), ct).ConfigureAwait(false);
        }
        await PublishAsync(ServerTopic("started"), Web.SystemMonitor.Started.ToString("o"), ct)
            .ConfigureAwait(false);
    }

    private async Task PublishServerStatsAsync(CancellationToken ct)
    {
        if (Monitor?.Latest() is not { } sample) return;
        int online = _cameras.Values.Count(c => c.Online);
        foreach (var (key, value) in ServerStatePayloads(sample, online))
            await PublishAsync(ServerTopic(key), value, ct).ConfigureAwait(false);
    }

    private async Task ServerStatsLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_cfg.StatsIntervalSeconds, 5, 86400));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!_client.IsConnected) continue;
            try
            {
                await PublishServerStatsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"MQTT: server stats publish failed: {Log.Flatten(ex)}");
            }
        }
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
    /// <summary>Smart detection labels seen at least once (announced lazily).</summary>
    private readonly HashSet<string> _smartAnnounced = new();

    private bool _featuresAnnounced;
    private bool _doorbellAnnounced;
    private DateTime _lastDoorbellPress = DateTime.MinValue;
    private bool _wifiAnnounced, _sirenAnnounced;  // lazily, on the first status push
    private int? _wifiDbm;
    private bool? _sirenOn, _floodlightOn;
    private bool _hasFloodlight, _hasIr, _hasSnapshot;
    private string? _model;
    private DateTime _lastSnapshot = DateTime.MinValue;
    private bool _lastOnline;

    public string Id { get; }

    /// <summary>Live camera reachability (feeds the server device's online count).</summary>
    internal bool Online => _control.Online;

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

        // Detection sensors and reboot exist on every Baichuan camera — announce
        // immediately (plus any lazily-discovered smart labels seen so far).
        var labels = HomeAssistantMqtt.CoreLabels.Concat(_smartAnnounced).ToList();
        foreach (var label in labels)
            await AnnounceEntityAsync("binary_sensor", label, BinarySensorConfig(label), ct).ConfigureAwait(false);
        await AnnounceEntityAsync("button", "reboot", ButtonConfig("Reboot", "reboot", "restart"), ct).ConfigureAwait(false);

        // Publish current detection states so HA doesn't show "unknown".
        foreach (var label in labels)
            await _hub.PublishAsync(StateTopic(label), _sensorOn.Contains(label) ? "ON" : "OFF", ct).ConfigureAwait(false);

        if (_featuresAnnounced)
            await AnnounceFeaturesAsync(ct).ConfigureAwait(false);

        // A broker restart wipes retained discovery configs; re-announce the
        // doorbell (lazily created on first press) so it survives, like the rest.
        if (_doorbellAnnounced)
        {
            await AnnounceEntityAsync("event", "doorbell", DoorbellEventConfig(), ct).ConfigureAwait(false);
            await AnnounceEntityAsync("binary_sensor", "visitor", VisitorConfig(), ct).ConfigureAwait(false);
        }
        // Same for the entities created lazily on the first status push.
        if (_wifiAnnounced)
            await AnnounceEntityAsync("sensor", "wifi_signal", WifiSignalConfig(), ct).ConfigureAwait(false);
        if (_sirenAnnounced)
            await AnnounceEntityAsync("binary_sensor", "siren", SirenConfig(), ct).ConfigureAwait(false);
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
        name = label switch
        {
            "motion" => "Motion",
            "person" => "Person",
            "vehicle" => "Vehicle",
            "animal" => "Animal",
            "package" => "Package",
            "line-crossing" => "Line crossing",
            "intrusion" => "Intrusion",
            "loitering" => "Loitering",
            _ => label,
        },
        unique_id = $"neolink_{Id}_{label}",
        state_topic = StateTopic(label),
        payload_on = "ON",
        payload_off = "OFF",
        device_class = "motion",
        // The smart labels aren't plain motion — give them a telling icon.
        icon = label switch
        {
            "package" => "mdi:package-variant-closed",
            "line-crossing" => "mdi:vector-line",
            "intrusion" => "mdi:shield-alert-outline",
            "loitering" => "mdi:account-clock-outline",
            _ => (string?)null,
        },
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
        if (push.Active && labels.Contains("doorbell"))
        {
            // Cameras re-push "visitor" while the chime is ringing unanswered;
            // without a grace window every re-push looks like another press.
            if (now - _lastDoorbellPress > TimeSpan.FromSeconds(3))
            {
                _lastDoorbellPress = now;
                _ = PublishDoorbellPressAsync(CancellationToken.None);
            }
        }
        if (push.Active)
        {
            _activeUntil["motion"] = now + HomeAssistantMqtt.OffDelay;
            foreach (var l in labels)
                if (Array.IndexOf(HomeAssistantMqtt.DetectionLabels, l) >= 0)
                {
                    _activeUntil[l] = now + HomeAssistantMqtt.OffDelay;
                    // Smart labels announce on first sight (retained state follows
                    // right after, so HA shows a value as soon as the entity exists).
                    if (Array.IndexOf(HomeAssistantMqtt.CoreLabels, l) < 0 && _smartAnnounced.Add(l))
                        _ = AnnounceEntityAsync("binary_sensor", l, BinarySensorConfig(l), CancellationToken.None);
                }
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

    /// <summary>
    /// A doorbell button press becomes an HA EVENT entity (device_class doorbell) —
    /// the natural trigger for ring automations — plus a momentary "visitor"
    /// binary sensor that HA clears by itself (off_delay), so dashboards show a
    /// short "pressed" pulse instead of a state stuck until the next ring.
    /// Both are announced lazily on the FIRST press, so ordinary cameras never
    /// grow dead doorbell entities. (HA may need a beat to create the entities,
    /// so the very first ring can be discovery-only.)
    /// </summary>
    private async Task PublishDoorbellPressAsync(CancellationToken ct)
    {
        try
        {
            if (!_doorbellAnnounced)
            {
                _doorbellAnnounced = true;
                await AnnounceEntityAsync("event", "doorbell", DoorbellEventConfig(), ct).ConfigureAwait(false);
                await AnnounceEntityAsync("binary_sensor", "visitor", VisitorConfig(), ct).ConfigureAwait(false);
            }
            await _hub.PublishTransientAsync(StateTopic("doorbell"), "{\"event_type\":\"press\"}", ct)
                .ConfigureAwait(false);
            // Not retained: a stale ON must never replay into HA after a restart
            // (off_delay only starts counting from when HA receives the message).
            await _hub.PublishTransientAsync(StateTopic("visitor"), "ON", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"{_cam.Name}: doorbell publish failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>The momentary press state: ON on the ring, auto-reset by HA a few
    /// seconds later — no OFF publish needed (and none ever comes from the camera).</summary>
    private object VisitorConfig() => new
    {
        name = "Visitor",
        unique_id = $"neolink_{Id}_visitor",
        state_topic = StateTopic("visitor"),
        payload_on = "ON",
        off_delay = 5,
        icon = "mdi:doorbell",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object DoorbellEventConfig() => new
    {
        name = "Doorbell",
        unique_id = $"neolink_{Id}_doorbell",
        state_topic = StateTopic("doorbell"),
        event_types = new[] { "press" },
        device_class = "doorbell",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    // ------------------------------------------------------------------ status pushes

    public void OnStatus(StatusPush push) => _ = PublishStatusAsync(push, CancellationToken.None);

    /// <summary>
    /// Unsolicited status pushes keep entities live without polling. All publishes
    /// are edge-triggered (only on a changed value); the Wi-Fi and siren entities
    /// are announced lazily on their first push, so cameras that never send one
    /// don't grow dead entities in HA.
    /// </summary>
    private async Task PublishStatusAsync(StatusPush push, CancellationToken ct)
    {
        try
        {
            switch (push)
            {
                case WifiSignalPush wifi:
                    if (!_wifiAnnounced)
                    {
                        _wifiAnnounced = true;
                        await AnnounceEntityAsync("sensor", "wifi_signal", WifiSignalConfig(), ct).ConfigureAwait(false);
                    }
                    if (wifi.SignalDbm != _wifiDbm)
                    {
                        _wifiDbm = wifi.SignalDbm;
                        await _hub.PublishAsync(StateTopic("wifi_signal"), wifi.SignalDbm.ToString(), ct).ConfigureAwait(false);
                    }
                    break;

                case SirenStatusPush siren:
                    if (!_sirenAnnounced)
                    {
                        _sirenAnnounced = true;
                        await AnnounceEntityAsync("binary_sensor", "siren", SirenConfig(), ct).ConfigureAwait(false);
                    }
                    if (siren.On != _sirenOn)
                    {
                        _sirenOn = siren.On;
                        await _hub.PublishAsync(StateTopic("siren"), siren.On ? "ON" : "OFF", ct).ConfigureAwait(false);
                    }
                    break;

                case FloodlightStatusPush fl:
                    // The floodlight entity itself is announced from the LedState
                    // probe; the push just keeps its state fresh between polls.
                    if (_hasFloodlight && fl.On != _floodlightOn)
                    {
                        _floodlightOn = fl.On;
                        await _hub.PublishAsync(StateTopic("floodlight"), fl.On ? "ON" : "OFF", ct).ConfigureAwait(false);
                    }
                    break;

                // SleepStatusPush: parsed for the log, no HA surface yet.
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"{_cam.Name}: status publish failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Wi-Fi signal strength as a diagnostic sensor (RSSI in dBm, from msg 464 pushes).</summary>
    private object WifiSignalConfig() => new
    {
        name = "Wi-Fi signal",
        unique_id = $"neolink_{Id}_wifi_signal",
        state_topic = StateTopic("wifi_signal"),
        device_class = "signal_strength",
        unit_of_measurement = "dBm",
        state_class = "measurement",
        entity_category = "diagnostic",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object SirenConfig() => new
    {
        name = "Siren",
        unique_id = $"neolink_{Id}_siren",
        state_topic = StateTopic("siren"),
        payload_on = "ON",
        payload_off = "OFF",
        device_class = "sound",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

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
        {
            _floodlightOn = fl == "open"; // keep the push edge-detector in sync with the poll
            await _hub.PublishAsync(StateTopic("floodlight"), _floodlightOn.Value ? "ON" : "OFF", ct).ConfigureAwait(false);
        }
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
