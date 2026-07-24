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
///   • binary_sensor: motion, person, vehicle, animal,
///                    package, line crossing, intrusion,
///                    loitering                          (from the alarm pushes)
///   • binary_sensor: siren sounding                     (from the status pushes)
///   • switch:        siren — ON sounds until OFF        (audio-alarm cameras)
///   • switch:        privacy mode — camera dark         (cameras answering msg 574)
///   • event:         doorbell press                     (video doorbells; not retained)
///   • binary_sensor: visitor                            (doorbell press pulse; HA
///                                                        auto-clears it via off_delay)
///   • sensor:        battery                            (battery cameras)
///   • sensor:        Wi-Fi signal, diagnostic           (from the status pushes)
///   • switch:        PIR sensor                         (PIR cameras)
///   • switch:        Suspend (beta) — ON = Neolink holds no connection to the
///                    camera (not viewed or recorded here); bridge-only availability
///                    so it stays usable while the camera is intentionally offline
///   • switch:        Record on demand — records ONE clip, no camera detection
///                    involved; stops by itself at recording.max_clip_seconds
///                    (the switch flips back OFF) or when switched off early.
///                    Shares its session with the web UI's record button;
///                    announced when the camera has an event recorder
///   • switch:        Detection events — the web UI's per-camera event-capture
///                    master toggle; OFF stops the server recording event clips
///                    for this camera (and on-demand). The camera keeps detecting,
///                    so the detection sensors above still report. Bridge-only
///                    availability, like Suspend; announced with the event recorder
///   • switch:        Continuous recording — the web UI's 24/7 "record around the
///                    clock" toggle; ON tapes continuously (retention still applies).
///                    Bridge-only availability, like Suspend; announced whenever a
///                    continuous recorder runs (Baichuan or generic RTSP)
///   • binary_sensor: recording — ON while the server is writing this camera's
///                    footage (an event clip, whatever triggered it, or a
///                    continuous segment)
///   • sensor:        last event — the id of the newest detection event,
///                    published (retained) the instant the event starts, so a
///                    motion automation can deep-link to the exact clip via
///                    {web-ui}/events?event={id}
///   • select:        night vision (auto/on/off)         (IR-capable cameras)
///   • light:         floodlight                         (cameras with a spotlight)
///   • button:        reboot and PTZ steps               (per capability)
///   • camera:        latest snapshot                    (when the camera supports it)
///   • number:        speaker volume 0-100, beta         (via the camera's HTTP API)
///   • number:        motion/AI detection sensitivity    (via the camera's HTTP API)
///   • select:        HDR off/on or off/low/high         (via the camera's HTTP API)
///   • binary_sensor: firmware update available          (read-only; never installs)
///   • switch:        auto-tracking, beta                (GetAbility-gated, HTTP API)
///   • select:        PTZ preset — picking one moves the
///                    camera there, beta                 (via the camera's HTTP API)
///   • light:         spotlight (on/off + brightness), beta (white-LED cameras
///                    without a FloodlightTask: Lumus/Elite)
///   • number:        IR brightness 0-100, beta          (cameras reporting it)
///   • switch:        status LED                         (cameras whose lightState
///                    is the little status LED — i.e. no floodlight, no spotlight)
///   • switch:        doorbell light, beta               (video doorbells)
///   • select:        play quick reply, beta             (video doorbells; picking
///                                                        an option plays it)
///   • number/select/switch: picture settings, beta      (brightness/contrast/
///                    saturation/hue/sharpness, day-night, anti-flicker, flip,
///                    mirror — per what the camera reports; config category)
///
/// The SERVER also reports itself: a "Neolink.NET Server" device with health
/// sensors straight off the monitor page (CPU, memory, disk, recordings size,
/// write rate, viewers, cameras online/recording, start time), published every
/// mqtt.stats_interval seconds (default 60; 0 turns the device off). With split
/// storage configured it also carries per-tier free/used sensors (clips/archive,
/// only for tiers that exist) and a "Storage full" problem binary_sensor.
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

    /// <summary>Discovery JSON must OMIT unset fields: Home Assistant validates
    /// every key it sees, and an explicit null ("icon": null) fails that
    /// validation — the whole entity is then discarded with only an HA-side log
    /// to show for it. Configs here are anonymous objects whose optional members
    /// default to null, so every discovery publish must serialize with this.</summary>
    internal static readonly JsonSerializerOptions DiscoveryJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Every detection label that gets a binary sensor — ALL announced up
    /// front on every Baichuan camera, so automations can be written in HA before
    /// the first package/perimeter event ever fires. A type the camera never
    /// pushes (feature absent, or not configured in the Reolink app) simply stays
    /// Clear — visible-but-idle beats invisible-until-it-happens.</summary>
    internal static readonly string[] DetectionLabels =
        { "motion", "person", "vehicle", "animal", "package", "crying", "line-crossing", "intrusion", "loitering" };

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
            MaxPacketBytes = cfg.MaxPacketBytes,
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

    /// <summary>Configured storage tiers, for the per-tier HA sensors. Only the
    /// tiers that actually exist are published (a plain single-folder install
    /// gets none of the clips/archive sensors).</summary>
    public Recording.StorageLocations? Storage { get; init; }

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

    /// <summary>Re-publishes a camera's volatile HA states immediately — invoked when
    /// one of its settings changed via the web UI/API, so Home Assistant reflects it
    /// within a moment instead of after the periodic refresh, keeping automations from
    /// acting on a stale switch. No-op for an unknown camera / when MQTT is disabled.</summary>
    public Task RepublishCameraAsync(string cameraName) =>
        _cameras.TryGetValue(Sanitize(cameraName), out var cam)
            ? cam.RepublishAsync(CancellationToken.None)
            : Task.CompletedTask;

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

    /// <summary>An AI description (and threat level) landed on a finished event —
    /// mirror it onto the camera's "AI description"/"AI threat level" sensors.</summary>
    public void OnAiDescribed(Neolink.Recording.EventRecord rec)
    {
        if (_cameras.TryGetValue(Sanitize(rec.Camera), out var cam))
            cam.OnAiDescribed(rec);
    }

    // ------------------------------------------------------------------ MQTT plumbing

    /// <summary>Test seam: observe every string publish without standing up a broker.</summary>
    internal Action<string, string>? PublishObserver;

    // Last retained payload per topic. Retained state topics are re-published by the
    // periodic refresh whether or not anything changed — the broker treats identical
    // retained publishes as no-ops, but SENDING them is not free: the 20 s refresh
    // burst was the dominant idle-CPU cost of the whole process (issue report:
    // periodic spikes to ~30% of a core on small Docker hosts). Deduplicate here, at
    // the one choke point, so every state publisher stays simple and idempotent.
    // Cleared on every (re)connect: a fresh broker may have lost its retained state.
    private readonly Dictionary<string, string> _lastRetained = new();
    private readonly object _retainedGate = new();

    internal Task PublishAsync(string topic, string payload, CancellationToken ct = default)
    {
        lock (_retainedGate)
        {
            if (_lastRetained.TryGetValue(topic, out var prev) && prev == payload)
                return Task.CompletedTask;
            _lastRetained[topic] = payload;
        }
        PublishObserver?.Invoke(topic, payload);
        return _client.PublishAsync(topic, payload, retain: true, ct);
    }

    internal Task PublishAsync(string topic, byte[] payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: true, ct);

    /// <summary>Publish WITHOUT retain — HA event entities must never replay a
    /// stale occurrence from the broker after a restart.</summary>
    internal Task PublishTransientAsync(string topic, string payload, CancellationToken ct = default) =>
        _client.PublishAsync(topic, payload, retain: false, ct);

    private async Task OnConnectedAsync()
    {
        // Fresh session: forget what we think the broker holds — it may have
        // restarted without persistence, so everything must be re-publishable.
        lock (_retainedGate) _lastRetained.Clear();
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

    /// <summary>The OPTIONAL storage tiers configured beyond the main recordings
    /// volume (which is already the disk_free/disk_used_pct sensors). "clips"
    /// and/or "archive" — nothing for a plain single-folder install, so HA never
    /// shows sensors for storage the user doesn't actually have. Internal/static
    /// so the self-tests can pin the "only what exists" contract.</summary>
    internal static IEnumerable<(string Key, string Label)> StorageTierKeys(Recording.StorageLocations? storage)
    {
        if (storage == null) yield break;
        if (storage.HasClipsTier) yield return ("clips", "Clips");
        if (storage.HasArchiveTier) yield return ("archive", "Archive");
    }

    private IEnumerable<(string Key, string Label)> StorageTiers() => StorageTierKeys(Storage);

    private Task AnnounceServerSensorAsync(string key, string name, string? unit,
        string? deviceClass, string? icon, bool diagnostic, CancellationToken ct)
    {
        var config = new
        {
            name,
            unique_id = $"neolink_server_{key}",
            state_topic = ServerTopic(key),
            unit_of_measurement = unit,
            device_class = deviceClass,
            // Numeric gauges chart as measurements; the timestamp doesn't.
            state_class = deviceClass == "timestamp" ? null : "measurement",
            icon,
            entity_category = diagnostic ? "diagnostic" : (string?)null,
            device = ServerDevice(),
            availability = new object[] { new { topic = _availabilityTopic } },
            availability_mode = "all",
        };
        return PublishAsync($"{_cfg.DiscoveryPrefix}/sensor/neolink_server/{key}/config",
            JsonSerializer.Serialize(config, DiscoveryJson), ct);
    }

    private async Task AnnounceServerAsync(CancellationToken ct)
    {
        if (!_cfg.Discovery) return;
        foreach (var s in ServerSensors)
            await AnnounceServerSensorAsync(s.Key, s.Name, s.Unit, s.DeviceClass, s.Icon, s.Diagnostic, ct)
                .ConfigureAwait(false);

        // Per-tier storage sensors — only the tiers that actually exist.
        foreach (var (key, label) in StorageTiers())
        {
            await AnnounceServerSensorAsync($"{key}_free", $"{label} free", "GB", null, "mdi:harddisk", false, ct)
                .ConfigureAwait(false);
            await AnnounceServerSensorAsync($"{key}_used_pct", $"{label} used", "%", null, "mdi:harddisk", false, ct)
                .ConfigureAwait(false);
        }
        // Aggregate "any recording drive is out of space" alarm — the unattended
        // hook so an automation can notify even when nobody has the web UI open.
        if (Storage != null)
        {
            var full = new
            {
                name = "Storage full",
                unique_id = "neolink_server_storage_full",
                state_topic = ServerTopic("storage_full"),
                device_class = "problem",
                payload_on = "ON",
                payload_off = "OFF",
                icon = "mdi:harddisk-remove",
                device = ServerDevice(),
                availability = new object[] { new { topic = _availabilityTopic } },
                availability_mode = "all",
            };
            await PublishAsync($"{_cfg.DiscoveryPrefix}/binary_sensor/neolink_server/storage_full/config",
                JsonSerializer.Serialize(full, DiscoveryJson), ct).ConfigureAwait(false);
        }

        // Remove retained discovery for optional storage entities that are NOT
        // configured right now, so dropping a tier (or turning recording off)
        // never leaves a phantom sensor behind in HA.
        var present = StorageTiers().Select(t => t.Key).ToHashSet();
        foreach (var tier in new[] { "clips", "archive" })
        {
            if (present.Contains(tier)) continue;
            await ClearServerEntityAsync("sensor", $"{tier}_free", ct).ConfigureAwait(false);
            await ClearServerEntityAsync("sensor", $"{tier}_used_pct", ct).ConfigureAwait(false);
        }
        if (Storage == null)
            await ClearServerEntityAsync("binary_sensor", "storage_full", ct).ConfigureAwait(false);

        await PublishAsync(ServerTopic("started"), Web.SystemMonitor.Started.ToString("o"), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Deletes a retained server-device discovery config (empty payload).</summary>
    private Task ClearServerEntityAsync(string component, string key, CancellationToken ct) =>
        PublishAsync($"{_cfg.DiscoveryPrefix}/{component}/neolink_server/{key}/config", "", ct);

    private async Task PublishServerStatsAsync(CancellationToken ct)
    {
        if (Monitor?.Latest() is not { } sample) return;
        int online = _cameras.Values.Count(c => c.Online);
        foreach (var (key, value) in ServerStatePayloads(sample, online))
            await PublishAsync(ServerTopic(key), value, ct).ConfigureAwait(false);
        await PublishStorageTiersAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Publishes free/used for each optional tier and the aggregate
    /// storage-full flag. Only configured, readable tiers get values.</summary>
    private async Task PublishStorageTiersAsync(CancellationToken ct)
    {
        if (Storage == null) return;
        const double GB = 1024.0 * 1024 * 1024;
        var sample = Storage.Sample();
        await PublishAsync(ServerTopic("storage_full"), sample.Any(s => s.Full) ? "ON" : "OFF", ct)
            .ConfigureAwait(false);
        foreach (var st in sample)
        {
            // Main is the disk_free/disk_used_pct sensors already.
            var key = st.Role.ToString().ToLowerInvariant();
            if (key == "main" || !st.Online || st.TotalBytes <= 0) continue;
            await PublishAsync(ServerTopic($"{key}_free"), (st.FreeBytes / GB).ToString("0.#"), ct)
                .ConfigureAwait(false);
            await PublishAsync(ServerTopic($"{key}_used_pct"), st.UsedPercent.ToString("0.#"), ct)
                .ConfigureAwait(false);
        }
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

    private bool _featuresAnnounced;
    private bool _doorbellAnnounced;
    private DateTime _lastDoorbellPress = DateTime.MinValue;

    private bool _wifiAnnounced, _sirenAnnounced;  // lazily, on the first status push
    private int? _wifiDbm;
    private bool? _sirenOn, _floodlightOn;
    private bool _hasFloodlight, _hasIr, _hasSnapshot;
    private bool _isBattery; // from the capability probe — gates the Asleep sensor
    // HTTP-API extras (beta): probed once when the camera first comes online.
    private bool _hasVolume, _hasAutoTrack;
    private IReadOnlyList<PtzPresetInfo>? _presets;
    private IReadOnlyList<QuickReplyFile>? _quickReplies;
    private ImageSettings? _imgCaps;      // which picture fields this camera reports
    private bool _hasSpotlight, _hasWhiteLedBrightness, _isDoorbell, _ptzCapable;
    private bool _hasIrBrightness, _hasDoorbellLight, _hasLightState; // from the LedState probe

    /// <summary>Whether this camera gets the plain "Status LED" switch. LedState's
    /// lightState is shared: it drives the floodlight on floodlight cams and the
    /// white spotlight on Lumus/Elite, and is only the little status LED when the
    /// camera has neither. Gating on that keeps exactly ONE entity owning
    /// lightState, so the three can never fight over the same field.</summary>
    private bool HasStatusLed => _hasLightState && !_hasFloodlight && !_hasSpotlight;
    private bool _hasMdSens;              // motion-detection sensitivity (1-50)
    private string[]? _aiSensTypes;       // AI types the camera answered GetAiAlarm for
    private bool _hasHdr;                 // ISP hdr field present
    private int _hdrMax = 1;              // 1 = on/off, 2 = off/low/high
    private bool _fwCheckable;            // the camera answers CheckFirmware
    /// <summary>The extras probe saw a working HTTP API. False keeps it retrying on
    /// the slow cadence — some cameras' HTTP side boots later than Baichuan, and one
    /// missed probe must not hide the entities until the bridge restarts.</summary>
    private bool _httpExtrasKnown;
    private DateTime _lastHttpPublish = DateTime.MinValue;
    private string? _model;
    private DateTime _lastSnapshot = DateTime.MinValue;
    // Last battery/LED/PIR poll — these are camera round-trips, not cached reads,
    // so they run once a minute instead of every 20 s refresh tick.
    private DateTime _lastStatePoll = DateTime.MinValue;
    private bool _lastOnline;
    private bool _everOnline;
    private DateTime? _offlineSince;

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
        // The Record switch mirrors the camera's on-demand session, whoever drives
        // it (this switch, the web UI, the cap auto-stop) — one source of truth.
        // The Recording sensor mirrors actual capture (any event, any trigger).
        if (cam.EventRecorder is { } rec)
        {
            rec.OnDemandChanged += session => _ = PublishRecordStateAsync(session != null);
            rec.RecordingChanged += on => _ = PublishRecordingStateAsync(force: false);
            rec.EventStarted += ev => _ = PublishLastEventAsync(ev);
        }
    }

    /// <summary>The new event's id lands (retained) the instant the event starts —
    /// together with the motion trigger — so an automation can build a link to the
    /// exact clip: {web-ui}/events?event={{ states('sensor.X_last_event') }}.</summary>
    /// <summary>An event start time as HA's timestamp device class wants it: RFC3339
    /// UTC with a trailing Z and NO fractional seconds. SpecifyKind guards a
    /// StartUtc that deserialized as Unspecified (without a forced Z, HA rejects
    /// the value and the sensor sticks at "unknown").</summary>
    internal static string FormatEventTime(DateTime startUtc) =>
        DateTime.SpecifyKind(startUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private Task PublishLastEventAsync(EventRecord ev) => PublishLastEventAsync(ev, CancellationToken.None);

    private async Task PublishLastEventAsync(EventRecord ev, CancellationToken ct)
    {
        try
        {
            await _hub.PublishAsync(StateTopic("last_event"), ev.Id, ct).ConfigureAwait(false);
            await _hub.PublishAsync(StateTopic("last_event/attr"),
                JsonSerializer.Serialize(new { labels = ev.Labels, started = ev.StartUtc }), ct)
                .ConfigureAwait(false);
            // The start moment again as its own timestamp sensor — HA renders it as
            // "n minutes ago" and automations compare it without templating the id
            // sensor's attribute out. See FormatEventTime for the format contract.
            await _hub.PublishAsync(StateTopic("last_event_time"), FormatEventTime(ev.StartUtc), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Broker unreachable — the next event republishes; the topic is retained.
        }
    }

    private async Task PublishRecordStateAsync(bool on)
    {
        try
        {
            await _hub.PublishAsync(StateTopic("record"), on ? "ON" : "OFF", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Broker unreachable right now — the next announce republishes the truth.
        }
    }

    /// <summary>Does this camera get a "Recording" status sensor at all?</summary>
    private bool HasRecordingSensor => _cam.EventRecorder != null || _cam.ContinuousActive != null;

    /// <summary>Footage being written for this camera RIGHT NOW: an event clip
    /// (camera detection or on-demand) or a continuous (24/7) segment.</summary>
    private bool RecordingNow =>
        (_cam.EventRecorder?.EventInProgress ?? false) || (_cam.ContinuousActive?.Invoke() ?? false);

    private bool? _recordingPublished;

    private async Task PublishRecordingStateAsync(bool force)
    {
        if (!HasRecordingSensor) return;
        bool on = RecordingNow;
        if (!force && _recordingPublished == on) return;
        _recordingPublished = on;
        try
        {
            await _hub.PublishAsync(StateTopic("recording"), on ? "ON" : "OFF", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Broker unreachable — the periodic refresh or next announce heals it.
        }
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

        // Suspend switch (beta) — universal, so announced for every camera (Baichuan
        // and generic RTSP) before the events/no-events split. Its availability is
        // the BRIDGE only, never this camera's online topic: a suspended camera reads
        // offline, and the switch must stay controllable to un-suspend it.
        if (_cam.SetSuspended != null)
        {
            await AnnounceEntityAsync("switch", "suspend", SuspendSwitchConfig(), ct).ConfigureAwait(false);
            await PublishSuspendStateAsync(ct).ConfigureAwait(false);
        }

        // 24/7 recording switch — universal like suspend (any camera that records
        // continuously, Baichuan or generic RTSP), so announced before the split.
        if (_cam.SetContinuousEnabled != null)
        {
            await AnnounceEntityAsync("switch", "continuous", ContinuousSwitchConfig(), ct).ConfigureAwait(false);
            await PublishContinuousStateAsync(ct).ConfigureAwait(false);
        }

        if (!_cam.SupportsEvents)
        {
            // Generic RTSP camera: no detection pushes and no Baichuan commands —
            // motion sensors and a reboot button would be dead weight in HA. A
            // connectivity sensor is the honest surface (and gives the device an
            // entity, so it actually shows up).
            await AnnounceEntityAsync("binary_sensor", "status", ConnectivityConfig(), ct).ConfigureAwait(false);
            if (HasRecordingSensor) // continuous (24/7) recording can still run here
            {
                await AnnounceEntityAsync("binary_sensor", "recording", RecordingSensorConfig(), ct).ConfigureAwait(false);
                await PublishRecordingStateAsync(force: true).ConfigureAwait(false);
            }
            return;
        }

        // Every detection sensor announces immediately — package and the
        // perimeter types included — so they exist in HA before their first
        // event ever fires. Types the camera never pushes just stay Clear.
        var labels = HomeAssistantMqtt.DetectionLabels;
        foreach (var label in labels)
            await AnnounceEntityAsync("binary_sensor", label, BinarySensorConfig(label), ct).ConfigureAwait(false);
        await AnnounceEntityAsync("button", "reboot", ButtonConfig("Reboot", "reboot", "restart"), ct).ConfigureAwait(false);

        // The Record switch: capture one clip on demand from HA — no camera
        // detection involved ("record while the door is open").
        if (_cam.EventRecorder is { } recorder)
        {
            await AnnounceEntityAsync("switch", "record", RecordSwitchConfig(), ct).ConfigureAwait(false);
            await _hub.PublishAsync(StateTopic("record"), recorder.OnDemand != null ? "ON" : "OFF", ct)
                .ConfigureAwait(false);
            // Detection events master switch — the same per-camera setting as the
            // web UI's Events toggle, so event-clip capture can be paused from an
            // automation (this pauses recording; it does not silence the sensors).
            await AnnounceEntityAsync("switch", "detect", DetectSwitchConfig(), ct).ConfigureAwait(false);
            await PublishDetectStateAsync(ct).ConfigureAwait(false);
            // Last event id + start time. The retained topics normally carry the
            // newest event across restarts, but a NEWLY-ADDED sensor has never been
            // published, so its topic holds nothing and HA shows "unknown" until the
            // next detection. Backfill from the most recent stored event so both
            // sensors are populated the moment they appear.
            await AnnounceEntityAsync("sensor", "last_event", LastEventConfig(), ct).ConfigureAwait(false);
            await AnnounceEntityAsync("sensor", "last_event_time", LastEventTimeConfig(), ct).ConfigureAwait(false);
            if (recorder.MostRecentEvent() is { } lastEvent)
                await PublishLastEventAsync(lastEvent, ct).ConfigureAwait(false);
            // AI event descriptions: announced for every recording camera whether
            // or not the feature is on for it (entities are never pruned by
            // enablement — that filtering is web-UI only). Cameras with AI off
            // simply keep an unknown state; both topics are retained.
            await AnnounceEntityAsync("sensor", "ai_description", AiDescriptionConfig(), ct).ConfigureAwait(false);
            await AnnounceEntityAsync("sensor", "ai_threat", AiThreatConfig(), ct).ConfigureAwait(false);
        }

        // Recording status: is footage for this camera being written right now
        // (event clip — detection or on-demand — or a continuous segment)?
        if (HasRecordingSensor)
        {
            await AnnounceEntityAsync("binary_sensor", "recording", RecordingSensorConfig(), ct).ConfigureAwait(false);
            await PublishRecordingStateAsync(force: true).ConfigureAwait(false);
        }

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

    /// <summary>Suspend switch (beta): ON = Neolink holds no connection to the camera
    /// (not viewed or recorded here). Bridge-only availability so it stays usable
    /// while the camera is (intentionally) offline — otherwise you couldn't resume it.</summary>
    // Internal (not private) so the selftest can assert the bridge-only
    // availability — the property that keeps the switch usable, and the camera
    // RESUMABLE from Home Assistant, while the camera itself reads unavailable.
    internal object SuspendSwitchConfig() => new
    {
        name = "Suspend",
        unique_id = $"neolink_{Id}_suspend",
        state_topic = StateTopic("suspend"),
        command_topic = CommandTopic("suspend"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:pause-octagon-outline",
        device = Device(),
        availability = new object[] { new { topic = _hub.AvailabilityTopic } },
        availability_mode = "all",
    };

    private Task PublishSuspendStateAsync(CancellationToken ct) =>
        _cam.Suspended == null
            ? Task.CompletedTask
            : _hub.PublishAsync(StateTopic("suspend"), _cam.Suspended() ? "ON" : "OFF", ct);

    /// <summary>Asleep (battery cameras): ON while every stream is parked on purpose
    /// so the camera can doze — as opposed to streaming (OFF) or genuinely offline
    /// (the camera reads unavailable). Bridge-only availability, like Suspend, so
    /// the sensor itself stays readable while the camera naps.</summary>
    // Internal (not private) so the selftest can assert the bridge-only availability.
    internal object AsleepSensorConfig() => new
    {
        name = "Asleep",
        unique_id = $"neolink_{Id}_asleep",
        state_topic = StateTopic("asleep"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:sleep",
        entity_category = "diagnostic",
        device = Device(),
        availability = new object[] { new { topic = _hub.AvailabilityTopic } },
        availability_mode = "all",
    };

    /// <summary>Continuous (24/7) recording — the web UI's "Record around the clock"
    /// toggle. ON tapes continuously (retention still applies); it is the same
    /// persisted server setting the UI flips, and the recorder reads it live, so the
    /// switch takes effect at once. Availability is bridge-only, like Suspend, so it
    /// stays controllable while the camera is offline or asleep.</summary>
    // Internal (not private) so the selftest can assert the bridge-only availability.
    internal object ContinuousSwitchConfig() => new
    {
        name = "Continuous recording",
        unique_id = $"neolink_{Id}_continuous",
        state_topic = StateTopic("continuous"),
        command_topic = CommandTopic("continuous"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:record-circle-outline",
        device = Device(),
        availability = new object[] { new { topic = _hub.AvailabilityTopic } },
        availability_mode = "all",
    };

    private Task PublishContinuousStateAsync(CancellationToken ct) =>
        _cam.ContinuousEnabled == null
            ? Task.CompletedTask
            : _hub.PublishAsync(StateTopic("continuous"), _cam.ContinuousEnabled() ? "ON" : "OFF", ct);

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
        {
            _isBattery = true;
            await AnnounceEntityAsync("sensor", "battery", BatterySensorConfig(), ct).ConfigureAwait(false);
            // Asleep sensor: only battery cameras nap, so only they get the
            // entity — a wired camera must not sprout a permanently-OFF sensor.
            await AnnounceEntityAsync("binary_sensor", "asleep", AsleepSensorConfig(), ct).ConfigureAwait(false);
        }
        // The siren is a SWITCH: ON sounds until OFF. Its state topic is the same
        // one the camera's own status pushes (msg 547) feed, so HA tracks reality
        // even when the siren was set off by the camera's own detection rules.
        if (f.Siren)
        {
            await AnnounceEntityAsync("switch", "siren_switch", SirenSwitchConfig(), ct).ConfigureAwait(false);
            // Legacy cleanup: an early build exposed the siren as a momentary
            // BUTTON. It is a latched switch now; delete the stale retained
            // discovery config so HA drops the dead "Siren (Press)" button.
            await ClearEntityAsync("button", "siren", ct).ConfigureAwait(false);
        }
        if (f.Privacy)
            await AnnounceEntityAsync("switch", "privacy_mode", PrivacySwitchConfig(), ct).ConfigureAwait(false);
        else
            // Only cameras that actually support privacy mode (DeviceInfo <sleep> AND
            // the channel's remoteAbility flag) get the switch. Clear a stale one an
            // over-detecting build announced (e.g. the RLC Elite WiFi line) so HA
            // drops the lingering switch instead of keeping it retained.
            await ClearEntityAsync("switch", "privacy_mode", ct).ConfigureAwait(false);
        if (_hasIr)
            await AnnounceEntityAsync("select", "ir", IrSelectConfig(), ct).ConfigureAwait(false);
        if (f.Floodlight)
            await AnnounceEntityAsync("light", "floodlight", FloodlightConfig(), ct).ConfigureAwait(false);
        else
            // Not every camera has a spotlight (e.g. the E1 Pro reports a status-LED
            // lightState but no real floodlight). Clear any floodlight a looser build
            // announced so HA drops the dead entity instead of keeping it retained.
            await ClearEntityAsync("light", "floodlight", ct).ConfigureAwait(false);
        if (f.Pir)
            await AnnounceEntityAsync("switch", "pir", SwitchConfig("PIR sensor", "pir"), ct).ConfigureAwait(false);
        if (f.Ptz)
            foreach (var dir in new[] { "up", "down", "left", "right" })
                await AnnounceEntityAsync("button", $"ptz_{dir}",
                    ButtonConfig($"Pan {dir}", $"ptz_{dir}", null), ct).ConfigureAwait(false);
        if (_hasSnapshot)
            await AnnounceEntityAsync("camera", "snapshot", CameraConfig(), ct).ConfigureAwait(false);
        // HTTP-API extras (beta), gated on the one-time probe. Unsupported ones are
        // actively CLEARED: an earlier build (or a config change) may have left a
        // retained discovery config on the broker, and HA would keep the dead entity.
        if (_hasVolume)
            await AnnounceEntityAsync("number", "volume", NumberConfig("Volume", "volume", 0, 100, "mdi:volume-high"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("number", "volume", ct).ConfigureAwait(false);
        if (_hasAutoTrack)
            await AnnounceEntityAsync("switch", "auto_track", SwitchConfig("Auto-tracking", "auto_track"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("switch", "auto_track", ct).ConfigureAwait(false);
        // Gated on real PTZ ability too — never trust the preset list alone.
        if (f.Ptz && _presets?.Any(p => p.Enabled) == true)
            await AnnounceEntityAsync("select", "ptz_preset", PtzPresetSelectConfig(), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("select", "ptz_preset", ct).ConfigureAwait(false);
        // The white spotlight (Lumus/Elite — no FloodlightTask): on/off rides the
        // Baichuan light state; brightness rides the HTTP white LED when available.
        if (_hasSpotlight)
            await AnnounceEntityAsync("light", "spotlight", SpotlightConfig(), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("light", "spotlight", ct).ConfigureAwait(false);
        // The little status/power LED on cameras with no floodlight or spotlight —
        // the same lightState toggle those use, so only one of the three is ever
        // announced. Config category: it's a device setting, not a room light.
        if (HasStatusLed)
            await AnnounceEntityAsync("switch", "status_led",
                SwitchConfig("Status LED", "status_led", "mdi:led-on", "config"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("switch", "status_led", ct).ConfigureAwait(false);
        if (_hasIrBrightness)
            await AnnounceEntityAsync("number", "ir_brightness",
                NumberConfig("IR brightness", "ir_brightness", 0, 100, "mdi:brightness-6"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("number", "ir_brightness", ct).ConfigureAwait(false);
        if (_isDoorbell && _hasDoorbellLight)
            await AnnounceEntityAsync("switch", "doorbell_light",
                SwitchConfig("Doorbell light", "doorbell_light", "mdi:alarm-light-outline"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("switch", "doorbell_light", ct).ConfigureAwait(false);
        if (_isDoorbell && _quickReplies is { Count: > 0 })
            await AnnounceEntityAsync("select", "quick_reply", QuickReplySelectConfig(), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("select", "quick_reply", ct).ConfigureAwait(false);
        // Retired: an earlier beta announced a "default reply" pair here; the
        // clears drop the dead entities from brokers that still retain them.
        await ClearEntityAsync("select", "auto_reply", ct).ConfigureAwait(false);
        await ClearEntityAsync("number", "auto_reply_time", ct).ConfigureAwait(false);
        // Picture settings: one entity per field the camera reports.
        foreach (var (key, label, cur) in PictureSliders())
        {
            if (cur != null)
                await AnnounceEntityAsync("number", key,
                    NumberConfig(label, key, 0, 255, "mdi:image-edit-outline"), ct).ConfigureAwait(false);
            else
                await ClearEntityAsync("number", key, ct).ConfigureAwait(false);
        }
        if (_imgCaps?.DayNight != null)
            await AnnounceEntityAsync("select", "day_night", SelectConfig("Day / night mode", "day_night",
                new[] { "Auto", "Color", "Black&White" }, "mdi:theme-light-dark"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("select", "day_night", ct).ConfigureAwait(false);
        if (_imgCaps?.AntiFlicker != null)
        {
            // Announce every value any firmware is known to use — indoor models
            // report "Off" — and keep whatever this camera currently reports even
            // if it's novel, or HA rejects every state publish as an invalid option.
            var flickerOptions = ImageSettings.AntiFlickerValues;
            if (_imgCaps.AntiFlicker is { Length: > 0 } cur && !flickerOptions.Contains(cur))
                flickerOptions = flickerOptions.Append(cur).ToArray();
            await AnnounceEntityAsync("select", "anti_flicker", SelectConfig("Anti-flicker", "anti_flicker",
                flickerOptions, "mdi:sine-wave"), ct).ConfigureAwait(false);
        }
        else
            await ClearEntityAsync("select", "anti_flicker", ct).ConfigureAwait(false);
        if (_imgCaps?.Flip != null)
            await AnnounceEntityAsync("switch", "img_flip",
                SwitchConfig("Flip image", "img_flip", "mdi:flip-vertical", "config"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("switch", "img_flip", ct).ConfigureAwait(false);
        if (_imgCaps?.Mirror != null)
            await AnnounceEntityAsync("switch", "img_mirror",
                SwitchConfig("Mirror image", "img_mirror", "mdi:flip-horizontal", "config"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("switch", "img_mirror", ct).ConfigureAwait(false);
        // Detection sensitivity (beta): the camera's own thresholds — higher = more
        // sensitive on both scales (MD is normalized server-side).
        if (_hasMdSens)
            await AnnounceEntityAsync("number", "md_sensitivity",
                NumberConfig("Motion sensitivity", "md_sensitivity", 1, 50, "mdi:motion-sensor"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("number", "md_sensitivity", ct).ConfigureAwait(false);
        foreach (var aiType in CameraControl.AiAlarmTypes)
        {
            if (_aiSensTypes?.Contains(aiType) == true)
                await AnnounceEntityAsync("number", $"ai_sens_{aiType}",
                    NumberConfig($"{AiTypeLabel(aiType)} sensitivity", $"ai_sens_{aiType}", 0, 100,
                        "mdi:motion-sensor"), ct).ConfigureAwait(false);
            else
                await ClearEntityAsync("number", $"ai_sens_{aiType}", ct).ConfigureAwait(false);
        }
        // HDR (beta): the range table decides on/off vs off/low/high.
        if (_hasHdr)
            await AnnounceEntityAsync("select", "hdr", SelectConfig("HDR", "hdr",
                HdrOptions(), "mdi:hdr"), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("select", "hdr", ct).ConfigureAwait(false);
        // Firmware update (read-only diagnostic): ON = Reolink offers a newer
        // firmware. Neolink.NET never installs anything.
        if (_fwCheckable)
            await AnnounceEntityAsync("binary_sensor", "firmware_update", FirmwareUpdateConfig(), ct).ConfigureAwait(false);
        else
            await ClearEntityAsync("binary_sensor", "firmware_update", ct).ConfigureAwait(false);
    }

    /// <summary>HA-friendly names for the GetAiAlarm ai_type tokens.</summary>
    private static string AiTypeLabel(string aiType) => aiType switch
    {
        "people" => "Person",
        "vehicle" => "Vehicle",
        "dog_cat" => "Animal",
        "face" => "Face",
        "package" => "Package",
        _ => aiType,
    };

    private string[] HdrOptions() => _hdrMax >= 2
        ? new[] { "off", "low", "high" }
        : new[] { "off", "on" };

    /// <summary>hdr wire value → select option (values above the announced range
    /// clamp to the top option so HA never rejects a state).</summary>
    private string HdrLabel(int value) => _hdrMax >= 2
        ? value <= 0 ? "off" : value == 1 ? "low" : "high"
        : value <= 0 ? "off" : "on";

    /// <summary>select option → hdr wire value; null = not an option we announced.</summary>
    private int? HdrValue(string payload) => payload switch
    {
        "off" => 0,
        "on" when _hdrMax < 2 => 1,
        "low" when _hdrMax >= 2 => 1,
        "high" when _hdrMax >= 2 => 2,
        _ => null,
    };

    private object FirmwareUpdateConfig() => new
    {
        name = "Firmware update",
        unique_id = $"neolink_{Id}_firmware_update",
        state_topic = StateTopic("firmware_update"),
        payload_on = "ON",
        payload_off = "OFF",
        device_class = "update",
        entity_category = "diagnostic",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>The picture sliders and whether this camera reports each (null = absent).</summary>
    private (string Key, string Label, int? Cur)[] PictureSliders() => new[]
    {
        ("img_bright", "Image brightness", _imgCaps?.Bright),
        ("img_contrast", "Image contrast", _imgCaps?.Contrast),
        ("img_saturation", "Image saturation", _imgCaps?.Saturation),
        ("img_hue", "Image hue", _imgCaps?.Hue),
        ("img_sharpen", "Image sharpness", _imgCaps?.Sharpen),
    };

    private Task AnnounceEntityAsync(string component, string objectId, object config, CancellationToken ct)
    {
        if (!_hub.Config.Discovery) return Task.CompletedTask;
        var topic = $"{_hub.Config.DiscoveryPrefix}/{component}/neolink_{Id}/{objectId}/config";
        return _hub.PublishAsync(topic, JsonSerializer.Serialize(config, HomeAssistantMqtt.DiscoveryJson), ct);
    }

    /// <summary>Removes an entity HA still has from an older version of this bridge:
    /// an empty RETAINED payload on its discovery-config topic tells HA to delete
    /// it. No-op when the topic was never published (nothing retained there).</summary>
    private Task ClearEntityAsync(string component, string objectId, CancellationToken ct)
    {
        if (!_hub.Config.Discovery) return Task.CompletedTask;
        var topic = $"{_hub.Config.DiscoveryPrefix}/{component}/neolink_{Id}/{objectId}/config";
        return _hub.PublishAsync(topic, "", ct);
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
            "crying" => "Crying",
            "line-crossing" => "Line crossing",
            "intrusion" => "Intrusion",
            "loitering" => "Loitering",
            _ => label,
        },
        unique_id = $"neolink_{Id}_{label}",
        state_topic = StateTopic(label),
        payload_on = "ON",
        payload_off = "OFF",
        // Crying is heard, not seen — HA's "sound" class fits it exactly.
        device_class = label == "crying" ? "sound" : "motion",
        // The smart labels aren't plain motion — give them a telling icon.
        // (motion/person keep the device_class default, which reads as a person.)
        icon = label switch
        {
            "vehicle" => "mdi:car",
            "animal" => "mdi:paw",
            "package" => "mdi:package-variant-closed",
            "crying" => "mdi:emoticon-cry-outline",
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

    // The display name says what it IS — "Record" alone reads like a recording
    // master switch next to a camera that may already be recording continuously.
    // unique_id and topics stay "record": existing HA entities keep their
    // identity (and entity_id) across the rename.
    private object RecordSwitchConfig() => new
    {
        name = "Record on demand",
        unique_id = $"neolink_{Id}_record",
        state_topic = StateTopic("record"),
        command_topic = CommandTopic("record"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:record-rec",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>Detection events: the same per-camera master toggle as the web UI's
    /// camera settings. OFF stops the server capturing event clips for this camera
    /// (and on-demand recording) until switched back on; the camera keeps detecting,
    /// so the detection binary_sensors are unaffected — this pauses recording, it is
    /// not a sensor disarm. The setting lives on the SERVER (settings.json), so
    /// availability is bridge-only like Suspend: it must stay controllable while the
    /// camera itself is offline or asleep.</summary>
    // Internal (not private) so the selftest can assert the bridge-only availability.
    internal object DetectSwitchConfig() => new
    {
        name = "Detection events",
        unique_id = $"neolink_{Id}_detect",
        state_topic = StateTopic("detect"),
        command_topic = CommandTopic("detect"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:motion-sensor",
        device = Device(),
        availability = new object[] { new { topic = _hub.AvailabilityTopic } },
        availability_mode = "all",
    };

    private Task PublishDetectStateAsync(CancellationToken ct) =>
        _cam.EventRecorder is not { } rec
            ? Task.CompletedTask
            : _hub.PublishAsync(StateTopic("detect"), rec.EventsEnabled ? "ON" : "OFF", ct);

    /// <summary>
    /// The HA "Record" switch drives the camera's shared on-demand session (the
    /// same one behind the web UI's record button): ON records one clip capped at
    /// recording.max_clip_seconds — the OnDemandChanged subscription flips the
    /// switch back OFF when the cap lands — and OFF stops it early. The clip shows
    /// up in the timeline/review strip labeled "external"; retention applies.
    /// </summary>
    private async Task SetRecordAsync(bool on)
    {
        if (_cam.EventRecorder is not { } rec) return;
        bool changed = on ? rec.StartOnDemand() : rec.StopOnDemand();
        // A no-op command (already running / already off / events disabled) still
        // gets the truth republished, so HA's optimistic toggle snaps back.
        if (!changed)
            await _hub.PublishAsync(StateTopic("record"), rec.OnDemand != null ? "ON" : "OFF",
                CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The id of the camera's most recent detection event, updated the
    /// moment the event starts. Made for notification deep links: append the
    /// state to "{web-ui}/events?event=" and the tap opens that exact clip.
    /// Labels and start time ride along as attributes.</summary>
    private object LastEventConfig() => new
    {
        name = "Last event",
        unique_id = $"neolink_{Id}_last_event",
        state_topic = StateTopic("last_event"),
        json_attributes_topic = StateTopic("last_event/attr"),
        icon = "mdi:motion-play-outline",
        entity_category = "diagnostic",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>When the camera's most recent detection event started, as a
    /// timestamp-class sensor: HA shows it as "n minutes ago" out of the box and
    /// automations can compare it directly (e.g. "no event for 24 h").</summary>
    private object LastEventTimeConfig() => new
    {
        name = "Last event time",
        unique_id = $"neolink_{Id}_last_event_time",
        state_topic = StateTopic("last_event_time"),
        device_class = "timestamp",
        entity_category = "diagnostic",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>The LLM's description of the camera's most recent detection event
    /// (AI event descriptions, opt-in per camera). HA caps a state at 255 chars,
    /// so the state is truncated; the full text and the metadata ride attributes.</summary>
    private object AiDescriptionConfig() => new
    {
        name = "Last AI description",
        unique_id = $"neolink_{Id}_ai_description",
        state_topic = StateTopic("ai_description"),
        json_attributes_topic = StateTopic("ai_description/attr"),
        icon = "mdi:message-text-outline",
        entity_category = "diagnostic",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>The LLM's threat classification of that event: green (routine),
    /// yellow (suspicious) or red (danger) — the automation hook ("when red, …").</summary>
    private object AiThreatConfig() => new
    {
        name = "AI threat level",
        unique_id = $"neolink_{Id}_ai_threat",
        state_topic = StateTopic("ai_threat"),
        icon = "mdi:shield-alert-outline",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    public void OnAiDescribed(EventRecord rec) => _ = PublishAiDescribedAsync(rec);

    private async Task PublishAiDescribedAsync(EventRecord rec)
    {
        try
        {
            var desc = rec.AiDescription ?? "";
            await _hub.PublishAsync(StateTopic("ai_description"),
                desc.Length <= 255 ? desc : desc[..252] + "…", CancellationToken.None).ConfigureAwait(false);
            await _hub.PublishAsync(StateTopic("ai_description/attr"),
                JsonSerializer.Serialize(new
                {
                    description = desc,
                    level = rec.AiLevel,
                    @event = rec.Id,
                    labels = rec.Labels,
                    started = rec.StartUtc,
                    model = rec.AiModel,
                }), CancellationToken.None).ConfigureAwait(false);
            await _hub.PublishAsync(StateTopic("ai_threat"), rec.AiLevel ?? "unknown", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Broker unreachable — the topics are retained; the next description republishes.
        }
    }

    private object RecordingSensorConfig() => new
    {
        name = "Recording",
        unique_id = $"neolink_{Id}_recording",
        state_topic = StateTopic("recording"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:record-rec",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>A config-category slider number entity (beta HTTP-API controls).</summary>
    private object NumberConfig(string name, string entity, int min, int max, string? icon) => new
    {
        name,
        unique_id = $"neolink_{Id}_{entity}",
        state_topic = StateTopic(entity),
        command_topic = CommandTopic(entity),
        min,
        max,
        step = 1,
        mode = "slider",
        icon,
        entity_category = "config",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>A config-category select whose options are the camera's own API values
    /// — published and commanded verbatim, so no mapping can drift.</summary>
    private object SelectConfig(string name, string entity, string[] options, string? icon) => new
    {
        name,
        unique_id = $"neolink_{Id}_{entity}",
        state_topic = StateTopic(entity),
        command_topic = CommandTopic(entity),
        options,
        icon,
        entity_category = "config",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>Saved PTZ positions as a select; picking one drives the camera there (beta).</summary>
    private object PtzPresetSelectConfig() => new
    {
        name = "PTZ preset",
        unique_id = $"neolink_{Id}_ptz_preset",
        state_topic = StateTopic("ptz_preset"),
        command_topic = CommandTopic("ptz_preset"),
        options = _presets!.Where(p => p.Enabled).Select(p => p.Name).ToArray(),
        icon = "mdi:crosshairs-gps",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>The doorbell's quick replies; picking one PLAYS it on the speaker (beta).</summary>
    private object QuickReplySelectConfig() => new
    {
        name = "Play quick reply",
        unique_id = $"neolink_{Id}_quick_reply",
        state_topic = StateTopic("quick_reply"),
        command_topic = CommandTopic("quick_reply"),
        options = _quickReplies!.Select(q => q.Name).ToArray(),
        icon = "mdi:message-reply-text-outline",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>The white spotlight (Lumus/Elite): on/off rides the Baichuan light
    /// state, brightness (when the camera has the HTTP white LED) 0-100 (beta).</summary>
    private object SpotlightConfig() => new
    {
        name = "Spotlight",
        unique_id = $"neolink_{Id}_spotlight",
        state_topic = StateTopic("spotlight"),
        command_topic = CommandTopic("spotlight"),
        payload_on = "ON",
        payload_off = "OFF",
        brightness_state_topic = _hasWhiteLedBrightness ? StateTopic("spotlight_brightness") : null,
        brightness_command_topic = _hasWhiteLedBrightness ? CommandTopic("spotlight_brightness") : null,
        brightness_scale = _hasWhiteLedBrightness ? 100 : (int?)null,
        icon = "mdi:spotlight-beam",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    private object SwitchConfig(string name, string entity, string? icon = null, string? category = null) => new
    {
        name,
        unique_id = $"neolink_{Id}_{entity}",
        state_topic = StateTopic(entity),
        command_topic = CommandTopic(entity),
        payload_on = "ON",
        payload_off = "OFF",
        icon,
        entity_category = category,
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
                    // A wired camera pushes NetInfo with no signal: there is no
                    // dBm sensor to announce or publish for it.
                    if (wifi.SignalDbm is not { } dbm) break;
                    if (!_wifiAnnounced)
                    {
                        _wifiAnnounced = true;
                        await AnnounceEntityAsync("sensor", "wifi_signal", WifiSignalConfig(), ct).ConfigureAwait(false);
                    }
                    if (dbm != _wifiDbm)
                    {
                        _wifiDbm = dbm;
                        await _hub.PublishAsync(StateTopic("wifi_signal"), dbm.ToString(), ct).ConfigureAwait(false);
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

                case SleepStatusPush sleep:
                    // Feeds the privacy-mode switch's state topic; the switch
                    // itself is announced from the capability probe.
                    await _hub.PublishAsync(StateTopic("privacy_mode"), sleep.Sleeping ? "ON" : "OFF", ct).ConfigureAwait(false);
                    break;
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
        // "sounding" (not "Siren"): the SWITCH below carries the plain name.
        name = "Siren sounding",
        unique_id = $"neolink_{Id}_siren",
        state_topic = StateTopic("siren"),
        payload_on = "ON",
        payload_off = "OFF",
        device_class = "sound",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>ON = sound until switched OFF. State rides the same topic the
    /// camera's own siren pushes feed, so the switch reflects reality.</summary>
    private object SirenSwitchConfig() => new
    {
        name = "Siren",
        unique_id = $"neolink_{Id}_siren_switch",
        state_topic = StateTopic("siren"),
        command_topic = CommandTopic("siren"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:alarm-bell",
        device = Device(),
        availability = Availability(),
        availability_mode = "all",
    };

    /// <summary>Privacy mode: ON = camera dark (no video, no detections).</summary>
    private object PrivacySwitchConfig() => new
    {
        name = "Privacy mode",
        unique_id = $"neolink_{Id}_privacy_mode",
        state_topic = StateTopic("privacy_mode"),
        command_topic = CommandTopic("privacy_mode"),
        payload_on = "ON",
        payload_off = "OFF",
        icon = "mdi:eye-off",
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
        // The suspend flag can flip from the web UI too — keep HA's switch in sync
        // (cheap: publishing a retained ON/OFF is a no-op when unchanged). The switch
        // is announced on connect for every camera that supports suspend.
        await PublishSuspendStateAsync(ct).ConfigureAwait(false);
        // Same for the Detection events master toggle (shared with the web UI).
        await PublishDetectStateAsync(ct).ConfigureAwait(false);
        // ...and the 24/7 recording switch (also flippable from the web UI).
        await PublishContinuousStateAsync(ct).ConfigureAwait(false);
        // Continuous segments start/stop without an event to ride on — the
        // periodic sweep keeps the Recording sensor honest for them (and heals
        // a publish that failed mid-outage). Runs regardless of online state:
        // a camera can go offline mid-event and the OFF still has to land.
        await PublishRecordingStateAsync(force: false).ConfigureAwait(false);
        if (!_control.Online) return;

        // First time online: probe capabilities, then announce the feature entities.
        if (!_featuresAnnounced)
        {
            var caps = await TryGetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (caps?.Features is { } f)
            {
                _model = caps.Version?.Model;
                if (f.Led) await ProbeLedAsync(ct).ConfigureAwait(false);
                // The real spotlight capability (FloodlightTask answered), not the
                // status-LED lightState — gates both discovery and state publishing.
                _hasFloodlight = f.Floodlight;
                _hasSnapshot = await ProbeSnapshotAsync(ct).ConfigureAwait(false);
                // HTTP-API extras (beta): one combined probe decides which of the
                // volume/auto-track/preset/picture/quick-reply entities exist here.
                await ProbeHttpExtrasAsync(f, ct).ConfigureAwait(false);
                _featuresAnnounced = true;
                await AnnounceAsync(ct).ConfigureAwait(false);
                await PublishHttpExtrasAsync(ct).ConfigureAwait(false);
            }
        }

        var caps2 = await TryGetCapabilitiesAsync(ct).ConfigureAwait(false);
        var feat = caps2?.Features;
        // Battery/LED/PIR are POLLS — real Baichuan round-trips plus XML parsing,
        // so they don't need to ride every 20 s tick (which also kept the camera
        // busier than necessary). Changes made through HA or the web UI re-publish
        // immediately (HandleCommandAsync / RepublishAsync); this cadence exists
        // only to catch changes made in the Reolink app, so a minute of staleness
        // is the ceiling — battery itself drains ~2%/hour and would tolerate far
        // less, but the queries are small and share the one poll clock.
        if (DateTime.UtcNow - _lastStatePoll > TimeSpan.FromMinutes(1))
        {
            _lastStatePoll = DateTime.UtcNow;
            if (feat?.Battery == true) await PublishBatteryAsync(ct).ConfigureAwait(false);
            if (feat?.Led == true) await PublishLedAsync(ct).ConfigureAwait(false);
            if (feat?.Pir == true) await PublishPirAsync(ct).ConfigureAwait(false);
        }
        if (_hasSnapshot && DateTime.UtcNow - _lastSnapshot > TimeSpan.FromMinutes(2))
            await PublishSnapshotAsync(ct).ConfigureAwait(false);
        // The HTTP-API states change rarely and cost a few camera HTTP calls, so
        // they refresh on a slower cadence (commands re-publish immediately).
        if (DateTime.UtcNow - _lastHttpPublish > TimeSpan.FromMinutes(5))
        {
            if (!_httpExtrasKnown && feat is { } f2)
            {
                // The first probe ran while the camera's HTTP API was still down —
                // keep retrying on this cadence and announce once it answers.
                _lastHttpPublish = DateTime.UtcNow;
                if (await ProbeHttpExtrasAsync(f2, ct).ConfigureAwait(false))
                {
                    await AnnounceFeaturesAsync(ct).ConfigureAwait(false);
                    await PublishHttpExtrasAsync(ct).ConfigureAwait(false);
                }
            }
            else if (_hasVolume || _hasAutoTrack || _imgCaps != null
                     || _hasWhiteLedBrightness || _presets != null)
            {
                await PublishHttpExtrasAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Immediately re-publishes the states a web-UI/API change can move, so
    /// Home Assistant doesn't wait for the periodic refresh (an automation must not
    /// act on a stale switch). The cheap server-side switches (suspend, detection
    /// events, 24/7 recording, on-demand record) always; the camera-side states
    /// (night vision, floodlight/spotlight, PIR, volume, auto-track, picture) only
    /// when the camera is online and its features are already known. Never throws —
    /// a failed publish heals on the next periodic refresh.</summary>
    public async Task RepublishAsync(CancellationToken ct)
    {
        try
        {
            await PublishSuspendStateAsync(ct).ConfigureAwait(false);
            await PublishDetectStateAsync(ct).ConfigureAwait(false);
            await PublishContinuousStateAsync(ct).ConfigureAwait(false);
            await PublishRecordingStateAsync(force: true).ConfigureAwait(false);
            if (_cam.EventRecorder is { } rec)
                await _hub.PublishAsync(StateTopic("record"), rec.OnDemand != null ? "ON" : "OFF", ct)
                    .ConfigureAwait(false);

            if (!_control.Online || !_featuresAnnounced) return;
            var caps = await TryGetCapabilitiesAsync(ct).ConfigureAwait(false);
            var feat = caps?.Features;
            if (feat?.Led == true) await PublishLedAsync(ct).ConfigureAwait(false);
            if (feat?.Pir == true) await PublishPirAsync(ct).ConfigureAwait(false);
            if (_httpExtrasKnown)
            {
                _lastHttpPublish = DateTime.UtcNow; // this publish counts; don't double up on the next refresh
                await PublishHttpExtrasAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"MQTT: immediate re-publish for '{Id}' failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Probes the HTTP-API extras once and records which entities this
    /// camera gets. Returns true when the probe saw a working HTTP API at all.</summary>
    private async Task<bool> ProbeHttpExtrasAsync(CameraFeatures f, CancellationToken ct)
    {
        var extras = await TryAsync(() => _control.GetHttpFeaturesAsync(ct)).ConfigureAwait(false);
        _hasVolume = extras?.Volume != null;
        _hasAutoTrack = extras?.AutoTrack != null;
        _presets = extras?.PtzPresets;
        _quickReplies = extras?.QuickReplies;
        _imgCaps = extras?.Image;
        _hasMdSens = extras?.MdSensitivity != null;
        _aiSensTypes = extras?.AiSensitivities?.Select(a => a.Type).ToArray();
        _hasHdr = extras?.Image?.Hdr != null;
        _hdrMax = Math.Clamp(extras?.Image?.HdrMax ?? 1, 1, 2);
        // One (camera-cached) update check decides whether the sensor exists at all.
        _fwCheckable = await TryAsync(() => _control.CheckFirmwareAsync(ct)).ConfigureAwait(false) != null;
        _hasSpotlight = f.Spotlight && !f.Floodlight;
        _hasWhiteLedBrightness = f.WhiteLed;
        _isDoorbell = f.Doorbell;
        _ptzCapable = f.Ptz;
        _httpExtrasKnown = extras != null && (extras.Volume != null || extras.AutoTrack != null
            || extras.Image != null || extras.PtzPresets != null || extras.QuickReplies != null
            || extras.WifiSignal != null || extras.SdCards != null);
        return _httpExtrasKnown;
    }

    /// <summary>Publishes the volume / auto-track / picture / spotlight-brightness
    /// states read over the HTTP API.</summary>
    private async Task PublishHttpExtrasAsync(CancellationToken ct)
    {
        _lastHttpPublish = DateTime.UtcNow;
        try
        {
            if (_hasVolume && await _control.GetVolumeAsync(ct).ConfigureAwait(false) is { } vol)
                await _hub.PublishAsync(StateTopic("volume"), vol.ToString(), ct).ConfigureAwait(false);
            if (_hasAutoTrack && await _control.GetAutoTrackAsync(ct).ConfigureAwait(false) is { } track)
                await _hub.PublishAsync(StateTopic("auto_track"), track ? "ON" : "OFF", ct).ConfigureAwait(false);
            if (_imgCaps != null && await _control.GetImageSettingsAsync(ct).ConfigureAwait(false) is { } img)
            {
                foreach (var (key, value) in new (string, int?)[]
                {
                    ("img_bright", img.Bright), ("img_contrast", img.Contrast),
                    ("img_saturation", img.Saturation), ("img_hue", img.Hue),
                    ("img_sharpen", img.Sharpen),
                })
                    if (value is { } v)
                        await _hub.PublishAsync(StateTopic(key), v.ToString(), ct).ConfigureAwait(false);
                if (img.DayNight is { Length: > 0 } dn)
                    await _hub.PublishAsync(StateTopic("day_night"), dn, ct).ConfigureAwait(false);
                if (img.AntiFlicker is { Length: > 0 } af)
                    await _hub.PublishAsync(StateTopic("anti_flicker"), af, ct).ConfigureAwait(false);
                if (img.Flip is { } flip)
                    await _hub.PublishAsync(StateTopic("img_flip"), flip ? "ON" : "OFF", ct).ConfigureAwait(false);
                if (img.Mirror is { } mirror)
                    await _hub.PublishAsync(StateTopic("img_mirror"), mirror ? "ON" : "OFF", ct).ConfigureAwait(false);
                if (_hasHdr && img.Hdr is { } hdr)
                    await _hub.PublishAsync(StateTopic("hdr"), HdrLabel(hdr), ct).ConfigureAwait(false);
            }
            if (_hasMdSens && await _control.GetMdSensitivityAsync(ct).ConfigureAwait(false) is { } mdSens)
                await _hub.PublishAsync(StateTopic("md_sensitivity"), mdSens.ToString(), ct).ConfigureAwait(false);
            if (_aiSensTypes is { Length: > 0 }
                && await _control.GetAiSensitivitiesAsync(ct).ConfigureAwait(false) is { } aiSens)
                foreach (var ai in aiSens)
                    await _hub.PublishAsync(StateTopic($"ai_sens_{ai.Type}"), ai.Sensitivity.ToString(), ct).ConfigureAwait(false);
            // Served from the control layer's hours-long cache — no cloud chatter.
            if (_fwCheckable && await _control.CheckFirmwareAsync(ct).ConfigureAwait(false) is { } fw)
                await _hub.PublishAsync(StateTopic("firmware_update"), fw.UpdateAvailable ? "ON" : "OFF", ct).ConfigureAwait(false);
            if (_hasWhiteLedBrightness && await _control.GetWhiteLedAsync(ct).ConfigureAwait(false) is { } wl)
                await _hub.PublishAsync(StateTopic("spotlight_brightness"), wl.Bright.ToString(), ct).ConfigureAwait(false);
            // Preset names can change (the panel saves new ones) — refresh the list
            // and re-announce the select so its options never go stale.
            if (_presets != null && await _control.GetPtzPresetsAsync(ct).ConfigureAwait(false) is { } fresh)
            {
                bool changed = !fresh.Where(p => p.Enabled).Select(p => p.Name)
                    .SequenceEqual(_presets.Where(p => p.Enabled).Select(p => p.Name));
                _presets = fresh;
                if (changed)
                {
                    if (_ptzCapable && fresh.Any(p => p.Enabled))
                        await AnnounceEntityAsync("select", "ptz_preset", PtzPresetSelectConfig(), ct).ConfigureAwait(false);
                    else
                        await ClearEntityAsync("select", "ptz_preset", ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"MQTT: HTTP-extra state read for '{Id}' failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>How long a dropped connection is tolerated before HA is told the
    /// camera is Unavailable. Cameras drop the connection briefly on a privacy-mode
    /// toggle (and some, like the E1 Pro, close it every ~30s while dark); a
    /// reconnect lands within a few seconds, so without this grace every entity on
    /// the camera flaps to Unavailable and back on each blip.</summary>
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(45);

    /// <summary>Debounced availability: online while the camera is live, and for
    /// <paramref name="grace"/> after a previously-live camera drops. A camera that
    /// has never connected reports offline immediately (no false "available").</summary>
    internal static bool AvailabilityOnline(bool live, bool everOnline, ref DateTime? offlineSince,
        DateTime nowUtc, TimeSpan grace)
    {
        if (live) { offlineSince = null; return true; }
        offlineSince ??= nowUtc;
        return everOnline && nowUtc - offlineSince.Value < grace;
    }

    /// <summary>The availability POLICY: a battery camera parked on purpose (dozing
    /// between viewers) counts as alive — its retained states stay meaningful in HA.
    /// Suspend does not (deliberately offline), and a camera we WANT but cannot
    /// reach is not parked, so it still reads offline after the grace.</summary>
    internal static bool AliveByPolicy(bool live, bool asleep, bool suspended) =>
        live || (asleep && !suspended);

    private async Task PublishAvailabilityAsync(CancellationToken ct)
    {
        bool live = _control.Online;
        bool suspended = _cam.Suspended?.Invoke() ?? false;
        bool asleep = !suspended && (_cam.Asleep?.Invoke() ?? false);
        if (live) _everOnline = true;
        bool online = AvailabilityOnline(AliveByPolicy(live, asleep, suspended),
            _everOnline, ref _offlineSince, DateTime.UtcNow, OfflineGrace);
        _lastOnline = online;
        // Entering the nap with a detection still latched would freeze a phantom
        // "Detected" on dashboards for hours; a sleeping camera detects nothing,
        // so clear them. No pushes can race this: the connection is down.
        if (!live && asleep && _sensorOn.Count > 0)
        {
            foreach (var label in _sensorOn.ToList())
                await _hub.PublishAsync(StateTopic(label), "OFF", ct).ConfigureAwait(false);
            _sensorOn.Clear();
        }
        // Battery cameras only (from the capability probe): wired cameras never
        // nap, and must not get an asleep topic/entity at all.
        if (_isBattery && _cam.Asleep != null)
            await _hub.PublishAsync(StateTopic("asleep"), !live && asleep ? "ON" : "OFF", ct).ConfigureAwait(false);
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
        // The beta LedState-backed entities (spotlight cams have no floodlight, so
        // lightState is free to feed the spotlight here).
        if (_hasSpotlight && Str(led, "lightState") is { Length: > 0 } sp)
            await _hub.PublishAsync(StateTopic("spotlight"), sp == "open" ? "ON" : "OFF", ct).ConfigureAwait(false);
        if (HasStatusLed && Str(led, "lightState") is { Length: > 0 } sl)
            await _hub.PublishAsync(StateTopic("status_led"), sl == "open" ? "ON" : "OFF", ct).ConfigureAwait(false);
        if (_hasIrBrightness && Num(led, "IRLedBrightness") is { } irb)
            await _hub.PublishAsync(StateTopic("ir_brightness"), irb.ToString(), ct).ConfigureAwait(false);
        if (_isDoorbell && _hasDoorbellLight && Str(led, "doorbellLightState") is { Length: > 0 } dbl)
            await _hub.PublishAsync(StateTopic("doorbell_light"), dbl == "open" ? "ON" : "OFF", ct).ConfigureAwait(false);
    }

    private async Task PublishPirAsync(CancellationToken ct)
    {
        var pir = await TryAsync(() => _control.GetPirStateAsync(ct)).ConfigureAwait(false);
        if (pir == null) return;
        await _hub.PublishAsync(StateTopic("pir"), Num(pir, "enable") == 1 ? "ON" : "OFF", ct).ConfigureAwait(false);
    }

    private async Task PublishSnapshotAsync(CancellationToken ct)
    {
        // Small variant: brokers cap packet size (Mosquitto 2.1+ at 2 MB) and
        // disconnect over it — a full dual-lens/4K snapshot doesn't fit.
        var jpeg = await TryAsync(() => _control.SnapshotSmallAsync(ct)).ConfigureAwait(false);
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
        // _hasFloodlight is NOT inferred from lightState here — a status LED reports
        // it too. It's set from the FloodlightTask capability by the caller; this
        // records only that the field EXISTS, and HasStatusLed decides who owns it.
        _hasLightState = Str(led, "lightState") is { Length: > 0 };
        _hasIrBrightness = Num(led, "IRLedBrightness") != null;
        // The field alone doesn't gate the entity — the Elite reports one without
        // having the light; the announce also requires the doorbell capability.
        _hasDoorbellLight = Str(led, "doorbellLightState") is { Length: > 0 };
    }

    private async Task<bool> ProbeSnapshotAsync(CancellationToken ct)
    {
        var jpeg = await TryAsync(() => _control.SnapshotSmallAsync(ct)).ConfigureAwait(false);
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
                case "siren":
                    await _control.SirenAsync(payload == "ON", ct).ConfigureAwait(false);
                    // Optimistic state so HA's toggle doesn't snap back; the
                    // camera's own siren push confirms (or corrects) shortly.
                    await _hub.PublishAsync(StateTopic("siren"), payload == "ON" ? "ON" : "OFF", ct).ConfigureAwait(false);
                    break;
                case "privacy_mode":
                    await _control.SetPrivacyModeAsync(payload == "ON", ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("privacy_mode"), payload == "ON" ? "ON" : "OFF", ct).ConfigureAwait(false);
                    break;
                case "ir":
                    await _control.SetLedStateAsync(HaToIr(payload), null, null, null, ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "floodlight":
                    await _control.SetLedStateAsync(null, payload == "ON" ? "open" : "close", null, null, ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "pir":
                    await _control.SetPirEnabledAsync(payload == "ON", ct).ConfigureAwait(false);
                    await PublishPirAsync(ct).ConfigureAwait(false);
                    break;
                case "record":
                    await SetRecordAsync(payload == "ON").ConfigureAwait(false);
                    break;
                case "detect":
                    if (_cam.EventRecorder is { } eventsRecorder)
                    {
                        eventsRecorder.SetEventsEnabled(payload == "ON");
                        await _hub.PublishAsync(StateTopic("detect"), payload == "ON" ? "ON" : "OFF", ct).ConfigureAwait(false);
                    }
                    break;
                case "suspend":
                    if (_cam.SetSuspended is { } setSuspend)
                    {
                        setSuspend(payload == "ON");
                        await _hub.PublishAsync(StateTopic("suspend"), payload == "ON" ? "ON" : "OFF", ct).ConfigureAwait(false);
                    }
                    break;
                case "continuous":
                    if (_cam.SetContinuousEnabled is { } setContinuous)
                    {
                        setContinuous(payload == "ON");
                        await _hub.PublishAsync(StateTopic("continuous"), payload == "ON" ? "ON" : "OFF", ct).ConfigureAwait(false);
                    }
                    break;
                case "ptz_up" or "ptz_down" or "ptz_left" or "ptz_right" when payload == "PRESS":
                    await PtzStepAsync(entity["ptz_".Length..], ct).ConfigureAwait(false);
                    break;
                case "volume" when int.TryParse(payload, out var vol):
                    vol = Math.Clamp(vol, 0, 100);
                    await _control.SetVolumeAsync(vol, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("volume"), vol.ToString(), ct).ConfigureAwait(false);
                    break;
                case "auto_track":
                    await _control.SetAutoTrackAsync(payload == "ON", ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("auto_track"), payload == "ON" ? "ON" : "OFF", ct).ConfigureAwait(false);
                    break;
                case "ptz_preset":
                    if (_presets?.FirstOrDefault(p => p.Enabled && p.Name == payload) is { } preset)
                    {
                        await _control.PtzToPresetAsync(preset.Id, ct).ConfigureAwait(false);
                        await _hub.PublishAsync(StateTopic("ptz_preset"), preset.Name, ct).ConfigureAwait(false);
                    }
                    break;
                case "spotlight":
                    await _control.SetLedStateAsync(null, payload == "ON" ? "open" : "close", null, null, ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "status_led":
                    // Same lightState toggle as the spotlight/floodlight — only one of
                    // the three is ever announced, so this can't collide with them.
                    await _control.SetLedStateAsync(null, payload == "ON" ? "open" : "close", null, null, ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "spotlight_brightness" when int.TryParse(payload, out var sb):
                    sb = Math.Clamp(sb, 0, 100);
                    await _control.SetWhiteLedAsync(sb, null, null, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("spotlight_brightness"), sb.ToString(), ct).ConfigureAwait(false);
                    break;
                case "ir_brightness" when int.TryParse(payload, out var irb):
                    await _control.SetLedStateAsync(null, null, null, Math.Clamp(irb, 0, 100), ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "doorbell_light":
                    await _control.SetLedStateAsync(null, null, payload == "ON" ? "open" : "close", null, ct).ConfigureAwait(false);
                    await PublishLedAsync(ct).ConfigureAwait(false);
                    break;
                case "quick_reply":
                    if (_quickReplies?.FirstOrDefault(q => q.Name == payload) is { } reply)
                    {
                        await _control.PlayQuickReplyAsync(reply.Id, ct).ConfigureAwait(false);
                        await _hub.PublishAsync(StateTopic("quick_reply"), reply.Name, ct).ConfigureAwait(false);
                    }
                    break;
                case "img_bright" or "img_contrast" or "img_saturation" or "img_hue" or "img_sharpen"
                    when int.TryParse(payload, out var iv):
                    iv = Math.Clamp(iv, 0, 255);
                    await _control.SetImageSettingsAsync(
                        entity == "img_bright" ? iv : null,
                        entity == "img_contrast" ? iv : null,
                        entity == "img_saturation" ? iv : null,
                        entity == "img_hue" ? iv : null,
                        entity == "img_sharpen" ? iv : null,
                        null, null, null, null, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic(entity), iv.ToString(), ct).ConfigureAwait(false);
                    break;
                case "day_night" when payload is "Auto" or "Color" or "Black&White":
                    await _control.SetImageSettingsAsync(null, null, null, null, null,
                        payload, null, null, null, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("day_night"), payload, ct).ConfigureAwait(false);
                    break;
                case "anti_flicker" when ImageSettings.AntiFlickerValues.Contains(payload):
                    await _control.SetImageSettingsAsync(null, null, null, null, null,
                        null, payload, null, null, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("anti_flicker"), payload, ct).ConfigureAwait(false);
                    break;
                case "img_flip" or "img_mirror":
                    bool flipOn = payload == "ON";
                    await _control.SetImageSettingsAsync(null, null, null, null, null, null, null,
                        entity == "img_flip" ? flipOn : null,
                        entity == "img_mirror" ? flipOn : null, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic(entity), flipOn ? "ON" : "OFF", ct).ConfigureAwait(false);
                    break;
                case "md_sensitivity" when int.TryParse(payload, out var mds):
                    mds = Math.Clamp(mds, 1, 50);
                    await _control.SetMdSensitivityAsync(mds, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("md_sensitivity"), mds.ToString(), ct).ConfigureAwait(false);
                    break;
                case var aiEntity when aiEntity.StartsWith("ai_sens_", StringComparison.Ordinal)
                    && int.TryParse(payload, out var aiv):
                    var aiType = aiEntity["ai_sens_".Length..];
                    if (_aiSensTypes?.Contains(aiType) != true) break;
                    aiv = Math.Clamp(aiv, 0, 100);
                    await _control.SetAiSensitivityAsync(aiType, aiv, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic(aiEntity), aiv.ToString(), ct).ConfigureAwait(false);
                    break;
                case "hdr" when HdrValue(payload) is { } hdrValue:
                    await _control.SetHdrAsync(hdrValue, ct).ConfigureAwait(false);
                    await _hub.PublishAsync(StateTopic("hdr"), payload, ct).ConfigureAwait(false);
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
