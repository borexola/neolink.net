// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Config;

public sealed class NeolinkConfig
{
    public string BindAddr { get; set; } = "0.0.0.0";
    // Defaults chosen to never clash with Frigate/go2rtc (8554 RTSP, 8555 WebRTC).
    public int BindPort { get; set; } = 8654;
    /// <summary>HTTP/WebSocket API port for web clients (camera list + live fMP4); 0 disables it.</summary>
    public int WebPort { get; set; } = 8655;
    /// <summary>Bind address for the web API; defaults to the RTSP bind address.</summary>
    public string? WebBind { get; set; }
    /// <summary>Serve the browser UI on the web port (in addition to the API).</summary>
    public bool WebUi { get; set; } = true;
    /// <summary>Event recording (motion/AI detections + clips); null = disabled.</summary>
    public RecordingConfig? Recording { get; set; }
    /// <summary>Web-UI specific settings ("ui" section).</summary>
    public UiConfig Ui { get; set; } = new();
    /// <summary>MQTT / Home Assistant integration ("mqtt" section); null = disabled.</summary>
    public MqttConfig? Mqtt { get; set; }
    /// <summary>Recovery switch (legacy top-level spelling; "ui.reset_admin_password" preferred).</summary>
    public bool ResetAdminPassword { get; set; }

    /// <summary>Either spelling of the admin-password recovery switch.</summary>
    public bool EffectiveResetAdminPassword => ResetAdminPassword || Ui.ResetAdminPassword;
    public List<UserConfig> Users { get; } = new();
    public List<CameraConfig> Cameras { get; } = new();

    private static readonly string[] ValidStreams = { "mainStream", "subStream", "externStream", "both", "all" };
    private static readonly string[] ReservedNames = { "anyone", "anonymous" };

    /// <summary>
    /// Loads a config file. JSON (recommended) and TOML (compatible with the
    /// original Rust neolink) are both supported; the format is detected from
    /// the file extension or content.
    /// </summary>
    public static NeolinkConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        bool isJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                      || text.TrimStart().StartsWith('{');
        var config = isJson ? LoadJson(text) : LoadToml(text);
        config.Validate();
        return config;
    }

    // ------------------------------------------------------------------ JSON

    private static NeolinkConfig LoadJson(string text)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(text, options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Config root must be a JSON object");

        var config = new NeolinkConfig();

        foreach (var prop in root.EnumerateObject())
        {
            switch (Key(prop.Name))
            {
                case "bind":
                    config.BindAddr = prop.Value.GetString() ?? "0.0.0.0";
                    break;
                case "bindport":
                    config.BindPort = prop.Value.GetInt32();
                    break;
                case "webport":
                    config.WebPort = prop.Value.GetInt32();
                    break;
                case "webbind":
                    config.WebBind = prop.Value.GetString();
                    break;
                case "webui":
                    config.WebUi = prop.Value.GetBoolean();
                    break;
                case "certificate":
                    WarnTls();
                    break;
                case "recording":
                    config.Recording = ParseJsonRecording(prop.Value);
                    break;
                case "resetadminpassword":
                    config.ResetAdminPassword = prop.Value.GetBoolean();
                    break;
                case "ui":
                    ParseJsonUi(prop.Value, config);
                    break;
                case "mqtt":
                    config.Mqtt = ParseJsonMqtt(prop.Value);
                    break;
                case "users":
                    foreach (var u in prop.Value.EnumerateArray())
                        config.Users.Add(ParseJsonUser(u));
                    break;
                case "cameras":
                    foreach (var c in prop.Value.EnumerateArray())
                        config.Cameras.Add(ParseJsonCamera(c));
                    break;
                default:
                    Log.Warn($"Config: ignoring unknown option '{prop.Name}'");
                    break;
            }
        }
        return config;
    }

    private static UserConfig ParseJsonUser(JsonElement el)
    {
        string? name = null, pass = null;
        foreach (var prop in el.EnumerateObject())
        {
            switch (Key(prop.Name))
            {
                case "name" or "username": name = prop.Value.GetString(); break;
                case "pass" or "password": pass = prop.Value.GetString(); break;
            }
        }
        return new UserConfig
        {
            Name = name ?? throw new FormatException("users[] entry missing \"name\""),
            Pass = pass ?? throw new FormatException($"user \"{name}\" missing \"pass\""),
        };
    }

    private static CameraConfig ParseJsonCamera(JsonElement el)
    {
        string? name = null, username = null, password = null, address = null, uid = null, httpAddress = null;
        string? rtspMain = null, rtspSub = null;
        string stream = "both";
        byte channelId = 0;
        bool record = true;
        bool udpProbe = false;
        bool udp = false;
        bool wakeCapture = false;
        bool? alwaysOn = null;
        List<string>? permitted = null;

        foreach (var prop in el.EnumerateObject())
        {
            switch (Key(prop.Name))
            {
                case "name": name = prop.Value.GetString(); break;
                case "username": username = prop.Value.GetString(); break;
                case "password": password = prop.Value.GetString(); break;
                case "address": address = prop.Value.GetString(); break;
                case "httpaddress": httpAddress = prop.Value.GetString(); break;
                case "uid": uid = prop.Value.GetString(); break;
                case "stream": stream = prop.Value.GetString() ?? "both"; break;
                case "channelid": channelId = prop.Value.GetByte(); break;
                case "record": record = prop.Value.GetBoolean(); break;
                case "alwayson": alwaysOn = prop.Value.GetBoolean(); break;
                case "udpprobe": udpProbe = prop.Value.GetBoolean(); break;
                case "udp": udp = prop.Value.GetBoolean(); break;
                case "wakecapture": wakeCapture = prop.Value.GetBoolean(); break;
                // Generic (non-Reolink) camera: pull these RTSP URLs directly.
                case "rtsp" or "rtspmain": rtspMain = prop.Value.GetString(); break;
                case "rtspsub": rtspSub = prop.Value.GetString(); break;
                case "permittedusers":
                    permitted = prop.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    break;
                case "format":
                    Log.Warn($"Camera '{name}': the 'format' option was removed in favour of auto detection.");
                    break;
                default:
                    Log.Warn($"Config: ignoring unknown camera option '{prop.Name}'");
                    break;
            }
        }

        return BuildCamera(name, username, password, address, uid, stream, channelId, permitted, httpAddress,
            record, rtspMain, rtspSub, alwaysOn, udpProbe, udp, wakeCapture);
    }

    private static RecordingConfig ParseJsonRecording(JsonElement el)
    {
        string? path = null;
        var rec = new RecordingConfig();
        foreach (var prop in el.EnumerateObject())
        {
            switch (Key(prop.Name))
            {
                case "path": path = prop.Value.GetString(); break;
                case "clipspath": rec.ClipsPath = prop.Value.GetString() ?? ""; break;
                case "archivepath": rec.ArchivePath = prop.Value.GetString() ?? ""; break;
                case "retentiondays": rec.RetentionDays = prop.Value.GetInt32(); break;
                case "preseconds": rec.PreSeconds = prop.Value.GetInt32(); break;
                case "postseconds": rec.PostSeconds = prop.Value.GetInt32(); break;
                case "maxclipseconds": rec.MaxClipSeconds = prop.Value.GetInt32(); break;
                case "stream": rec.Stream = prop.Value.GetString() ?? "auto"; break;
                case "segmentminutes": rec.SegmentMinutes = prop.Value.GetInt32(); break;
                case "maxsegmentsizemb": rec.MaxSegmentSizeMb = prop.Value.GetInt32(); break;
                case "continuousretentiondays": rec.ContinuousRetentionDays = prop.Value.GetInt32(); break;
                default:
                    Log.Warn($"Config: ignoring unknown recording option '{prop.Name}'");
                    break;
            }
        }
        rec.Path = path ?? throw new FormatException("\"recording\" needs a \"path\" (the storage directory)");
        return rec;
    }

    private static MqttConfig ParseJsonMqtt(JsonElement el)
    {
        var mqtt = new MqttConfig();
        string? host = null;
        foreach (var prop in el.EnumerateObject())
        {
            switch (Key(prop.Name))
            {
                case "broker" or "host" or "server": host = prop.Value.GetString(); break;
                case "port": mqtt.Port = prop.Value.GetInt32(); break;
                case "username" or "user": mqtt.Username = prop.Value.GetString(); break;
                case "password" or "pass": mqtt.Password = prop.Value.GetString(); break;
                case "clientid": mqtt.ClientId = prop.Value.GetString() ?? mqtt.ClientId; break;
                case "basetopic" or "topic": mqtt.BaseTopic = prop.Value.GetString() ?? mqtt.BaseTopic; break;
                case "discovery": mqtt.Discovery = prop.Value.GetBoolean(); break;
                case "discoveryprefix": mqtt.DiscoveryPrefix = prop.Value.GetString() ?? mqtt.DiscoveryPrefix; break;
                case "keepalive": mqtt.KeepAliveSeconds = prop.Value.GetInt32(); break;
                case "tls" or "ssl": mqtt.Tls = prop.Value.GetBoolean(); break;
                case "statsinterval" or "statsintervalseconds":
                    mqtt.StatsIntervalSeconds = prop.Value.GetInt32(); break;
                default:
                    Log.Warn($"Config: ignoring unknown mqtt option '{prop.Name}'");
                    break;
            }
        }
        mqtt.Broker = host ?? throw new FormatException("\"mqtt\" needs a \"broker\" (the MQTT server host)");
        return mqtt;
    }

    private static void ParseJsonUi(JsonElement el, NeolinkConfig config)
    {
        foreach (var prop in el.EnumerateObject())
        {
            switch (Key(prop.Name))
            {
                // Grouped aliases of the top-level web options
                case "enabled": config.WebUi = prop.Value.GetBoolean(); break;
                case "port": config.WebPort = prop.Value.GetInt32(); break;
                case "bind": config.WebBind = prop.Value.GetString(); break;
                // UI-only settings
                case "statedir": config.Ui.StateDir = prop.Value.GetString(); break;
                case "resetadminpassword": config.Ui.ResetAdminPassword = prop.Value.GetBoolean(); break;
                case "tricklespeed": config.Ui.TrickleSpeed = prop.Value.GetDouble(); break;
                case "talk": config.Ui.Talk = prop.Value.GetBoolean(); break;
                default:
                    Log.Warn($"Config: ignoring unknown ui option '{prop.Name}'");
                    break;
            }
        }
    }

    /// <summary>Normalizes JSON keys: case-insensitive, tolerates snake_case and kebab-case.</summary>
    private static string Key(string name) =>
        name.Replace("_", "").Replace("-", "").ToLowerInvariant();

    // ------------------------------------------------------------------ TOML (legacy)

    private static NeolinkConfig LoadToml(string text)
    {
        var root = MiniToml.Parse(text);
        var config = new NeolinkConfig
        {
            BindAddr = MiniToml.GetString(root, "bind") ?? "0.0.0.0",
            BindPort = (int)(MiniToml.GetInt(root, "bind_port") ?? 8654),
            WebPort = (int)(MiniToml.GetInt(root, "web_port") ?? 8655),
            WebBind = MiniToml.GetString(root, "web_bind"),
            WebUi = MiniToml.GetBool(root, "web_ui") ?? MiniToml.GetBool(root, "webui") ?? true,
            ResetAdminPassword = MiniToml.GetBool(root, "reset_admin_password") ?? false,
        };

        if (MiniToml.GetString(root, "certificate") != null)
            WarnTls();

        if (MiniToml.GetTable(root, "mqtt") is { } mqtt)
        {
            config.Mqtt = new MqttConfig
            {
                Broker = MiniToml.GetString(mqtt, "broker") ?? MiniToml.GetString(mqtt, "host")
                    ?? throw new FormatException("[mqtt] needs a 'broker' (the MQTT server host)"),
                Port = (int)(MiniToml.GetInt(mqtt, "port") ?? 1883),
                Username = MiniToml.GetString(mqtt, "username"),
                Password = MiniToml.GetString(mqtt, "password"),
                ClientId = MiniToml.GetString(mqtt, "client_id") ?? "neolink",
                BaseTopic = MiniToml.GetString(mqtt, "base_topic") ?? "neolink",
                Discovery = MiniToml.GetBool(mqtt, "discovery") ?? true,
                DiscoveryPrefix = MiniToml.GetString(mqtt, "discovery_prefix") ?? "homeassistant",
                KeepAliveSeconds = (int)(MiniToml.GetInt(mqtt, "keepalive") ?? 30),
                Tls = MiniToml.GetBool(mqtt, "tls") ?? false,
                StatsIntervalSeconds = (int)(MiniToml.GetInt(mqtt, "stats_interval") ?? 60),
            };
        }

        if (MiniToml.GetTable(root, "ui") is { } ui)
        {
            if (MiniToml.GetBool(ui, "enabled") is { } en) config.WebUi = en;
            if (MiniToml.GetInt(ui, "port") is { } p) config.WebPort = (int)p;
            config.WebBind = MiniToml.GetString(ui, "bind") ?? config.WebBind;
            config.Ui.StateDir = MiniToml.GetString(ui, "state_dir");
            config.Ui.ResetAdminPassword = MiniToml.GetBool(ui, "reset_admin_password") ?? false;
            if (MiniToml.GetInt(ui, "trickle_speed") is { } ts) config.Ui.TrickleSpeed = ts;
            config.Ui.Talk = MiniToml.GetBool(ui, "talk") ?? false;
        }

        if (MiniToml.GetTable(root, "recording") is { } rec)
        {
            config.Recording = new RecordingConfig
            {
                Path = MiniToml.GetString(rec, "path")
                    ?? throw new FormatException("[recording] needs a 'path' (the storage directory)"),
                ClipsPath = MiniToml.GetString(rec, "clips_path") ?? "",
                ArchivePath = MiniToml.GetString(rec, "archive_path") ?? "",
                RetentionDays = (int)(MiniToml.GetInt(rec, "retention_days") ?? 7),
                PreSeconds = (int)(MiniToml.GetInt(rec, "pre_seconds") ?? 5),
                PostSeconds = (int)(MiniToml.GetInt(rec, "post_seconds") ?? 8),
                MaxClipSeconds = (int)(MiniToml.GetInt(rec, "max_clip_seconds") ?? 120),
                Stream = MiniToml.GetString(rec, "stream") ?? "auto",
                SegmentMinutes = (int)(MiniToml.GetInt(rec, "segment_minutes") ?? 10),
                MaxSegmentSizeMb = (int)(MiniToml.GetInt(rec, "max_segment_size_mb") ?? 256),
                ContinuousRetentionDays = (int?)MiniToml.GetInt(rec, "continuous_retention_days"),
            };
        }

        foreach (var u in MiniToml.GetTables(root, "users"))
        {
            var name = MiniToml.GetString(u, "name") ?? MiniToml.GetString(u, "username")
                ?? throw new FormatException("[[users]] entry missing 'name'");
            var pass = MiniToml.GetString(u, "pass") ?? MiniToml.GetString(u, "password")
                ?? throw new FormatException($"[[users]] entry '{name}' missing 'pass'");
            config.Users.Add(new UserConfig { Name = name, Pass = pass });
        }

        foreach (var c in MiniToml.GetTables(root, "cameras"))
        {
            if (MiniToml.GetString(c, "format") != null)
                Log.Warn("The 'format' option was removed in favour of auto detection.");
            config.Cameras.Add(BuildCamera(
                MiniToml.GetString(c, "name"),
                MiniToml.GetString(c, "username"),
                MiniToml.GetString(c, "password"),
                MiniToml.GetString(c, "address"),
                MiniToml.GetString(c, "uid"),
                MiniToml.GetString(c, "stream") ?? "both",
                (byte)(MiniToml.GetInt(c, "channel_id") ?? 0),
                MiniToml.GetStringList(c, "permitted_users"),
                MiniToml.GetString(c, "http_address"),
                MiniToml.GetBool(c, "record") ?? true,
                MiniToml.GetString(c, "rtsp_main") ?? MiniToml.GetString(c, "rtsp"),
                MiniToml.GetString(c, "rtsp_sub"),
                MiniToml.GetBool(c, "always_on"),
                MiniToml.GetBool(c, "udp_probe") ?? false,
                MiniToml.GetBool(c, "udp") ?? false,
                MiniToml.GetBool(c, "wake_capture") ?? false));
        }
        return config;
    }

    // ------------------------------------------------------------------ shared

    private static void WarnTls() =>
        Log.Warn("TLS (certificate) is not supported by Neolink.NET yet; serving plain RTSP. " +
                 "Consider a TLS-terminating proxy if you need rtsps://");

    private static CameraConfig BuildCamera(string? name, string? username, string? password,
        string? address, string? uid, string stream, byte channelId, List<string>? permitted,
        string? httpAddress = null, bool record = true, string? rtspMain = null, string? rtspSub = null,
        bool? alwaysOn = null, bool udpProbe = false, bool udp = false, bool wakeCapture = false)
    {
        if (name == null) throw new FormatException("camera entry missing \"name\"");

        // Generic (non-Reolink) camera: RTSP URLs stand in for address/credentials
        // (put the login inside the URL: rtsp://user:pass@host/path).
        if (rtspMain != null || rtspSub != null)
        {
            foreach (var url in new[] { rtspMain, rtspSub })
            {
                if (url != null && !url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Camera \"{name}\": RTSP URLs must start with rtsp:// (got \"{url}\")");
            }
            if (address != null || username != null)
                throw new FormatException(
                    $"Camera \"{name}\": use EITHER address/username (Reolink) OR rtsp_main/rtsp_sub (generic), not both");
            return new CameraConfig
            {
                Name = name,
                Username = "",
                RtspMain = rtspMain,
                RtspSub = rtspSub,
                Stream = stream,
                PermittedUsers = permitted,
                Record = record,
            };
        }

        if (username == null) throw new FormatException($"Camera \"{name}\" missing \"username\"");
        if (uid != null && address == null)
            throw new FormatException(
                $"Camera \"{name}\": UID-only (relay/battery) connections are not supported by Neolink.NET yet; " +
                "give the camera a direct \"address\" too (\"uid\" + \"udp\": true connects over UDP)");
        if (address == null)
            throw new FormatException($"Camera \"{name}\" needs an \"address\" (host or host:port)");
        if (udp && string.IsNullOrWhiteSpace(uid))
            throw new FormatException($"Camera \"{name}\": \"udp\": true needs a \"uid\" (from the Reolink app or the sticker)");

        var (host, port) = SplitHostPort(address);
        return new CameraConfig
        {
            Name = name,
            Username = username,
            Password = password,
            Host = host,
            Port = port,
            Stream = stream,
            ChannelId = channelId,
            PermittedUsers = permitted,
            HttpAddress = string.IsNullOrWhiteSpace(httpAddress) ? null : httpAddress.Trim(),
            Record = record,
            AlwaysOn = alwaysOn,
            Uid = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim(),
            UdpProbe = udpProbe,
            Udp = udp,
            WakeCapture = wakeCapture,
        };
    }

    private void Validate()
    {
        if (BindPort is < 0 or > 65535)
            throw new FormatException($"Invalid bind_port {BindPort}");
        if (WebPort is < 0 or > 65535)
            throw new FormatException($"Invalid web_port {WebPort}");

        foreach (var u in Users)
        {
            if (string.IsNullOrWhiteSpace(u.Name) || ReservedNames.Contains(u.Name))
                throw new FormatException($"Invalid or reserved username \"{u.Name}\"");
        }

        // Zero cameras is allowed, not fatal: a fresh install boots to the web UI
        // (empty wall) so the user can set up and add cameras to the config,
        // rather than the process crash-looping on a first-run config.
        if (Cameras.Count == 0)
            Log.Warn("No cameras configured yet — the web UI will run but show no cameras. " +
                     "Add at least one camera to the config file and restart.");

        foreach (var cam in Cameras)
        {
            if (!ValidStreams.Contains(cam.Stream))
                throw new FormatException(
                    $"Camera \"{cam.Name}\": invalid stream \"{cam.Stream}\" " +
                    $"(expected one of: {string.Join(", ", ValidStreams)})");
        }

        var dupes = Cameras.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
            throw new FormatException($"Duplicate camera names: {string.Join(", ", dupes.Select(g => g.Key))}");

        var missing = Cameras
            .SelectMany(c => c.PermittedUsers ?? new List<string>())
            .Where(p => p is not ("anyone" or "anonymous") && Users.All(u => u.Name != p))
            .Distinct().ToList();
        if (missing.Count > 0)
            throw new FormatException($"permitted_users reference undefined users: {string.Join(", ", missing)}");

        if (Recording != null)
        {
            if (Recording.RetentionDays < 0)
                throw new FormatException("recording.retention_days must be >= 0 (0 = keep forever)");
            if (Recording.PreSeconds is < 0 or > 30)
                throw new FormatException("recording.pre_seconds must be 0..30");
            if (Recording.PostSeconds is < 1 or > 120)
                throw new FormatException("recording.post_seconds must be 1..120");
            if (Recording.MaxClipSeconds is < 10 or > 3600)
                throw new FormatException("recording.max_clip_seconds must be 10..3600");
            if (Recording.Stream is not ("auto" or "mainStream" or "subStream" or "externStream"))
                throw new FormatException("recording.stream must be auto, mainStream, subStream or externStream");
            if (Recording.SegmentMinutes is < 1 or > 120)
                throw new FormatException("recording.segment_minutes must be 1..120");
            if (Recording.MaxSegmentSizeMb is < 8 or > 8192)
                throw new FormatException("recording.max_segment_size_mb must be 8..8192");
            if (Recording.ContinuousRetentionDays is < 0)
                throw new FormatException("recording.continuous_retention_days must be >= 0 (0 = keep forever)");
        }

        if (Ui.TrickleSpeed is < 0.25 or > 16)
            throw new FormatException("ui.trickle_speed must be 0.25..16");
        if (Ui.StateDir is { Length: 0 })
            throw new FormatException("ui.state_dir must not be empty when set");

        if (Mqtt != null)
        {
            if (string.IsNullOrWhiteSpace(Mqtt.Broker))
                throw new FormatException("mqtt.broker must not be empty");
            if (Mqtt.Port is < 1 or > 65535)
                throw new FormatException($"Invalid mqtt.port {Mqtt.Port}");
            if (Mqtt.KeepAliveSeconds is < 5 or > 3600)
                throw new FormatException("mqtt.keepalive must be 5..3600");
            if (string.IsNullOrWhiteSpace(Mqtt.BaseTopic))
                throw new FormatException("mqtt.base_topic must not be empty");
            if (Mqtt.StatsIntervalSeconds is not 0 and (< 5 or > 86400))
                throw new FormatException("mqtt.stats_interval must be 0 (off) or 5..86400 seconds");
        }
    }

    private static (string host, int port) SplitHostPort(string address)
    {
        int colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var port))
            return (address[..colon], port);
        return (address, 9000);
    }

    /// <summary>Resolves the set of users allowed on a camera mount (null = anonymous access).</summary>
    public HashSet<string>? PermittedUsersFor(CameraConfig cam)
    {
        if (Users.Count == 0)
            return null; // no users configured: open access

        if (cam.PermittedUsers == null || cam.PermittedUsers.Contains("anyone"))
            return Users.Select(u => u.Name).ToHashSet();

        if (cam.PermittedUsers.Contains("anonymous"))
            return null;

        return cam.PermittedUsers.ToHashSet();
    }
}

public sealed class UserConfig
{
    public required string Name { get; init; }
    public required string Pass { get; init; }
}

/// <summary>
/// Web-UI settings ("ui" in the config). Note: UI accounts (sign-in) are NOT
/// configured here — they live in users.json under <see cref="StateDir"/> and
/// are managed from the UI itself; the top-level "users" list is RTSP-only.
/// </summary>
public sealed class UiConfig
{
    /// <summary>
    /// Where the UI's server-side state persists (users.json, settings.json).
    /// Defaults to the config file's directory — point it at a persistent volume
    /// if the config is mounted read-only or replaced on deployments.
    /// </summary>
    public string? StateDir { get; set; }
    /// <summary>Recovery: while true, the login screen allows setting a new admin password.</summary>
    public bool ResetAdminPassword { get; set; }
    /// <summary>Playback rate of the review-strip's ambient clip previews.</summary>
    public double TrickleSpeed { get; set; } = 4;
    /// <summary>Beta: two-way talk (browser microphone → camera speaker). Off by default.</summary>
    public bool Talk { get; set; }
}

/// <summary>MQTT / Home Assistant integration settings ("mqtt" in the config).</summary>
public sealed class MqttConfig
{
    /// <summary>MQTT broker host (e.g. the Home Assistant / Mosquitto address).</summary>
    public string Broker { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary>Client id presented to the broker (must be unique per connection).</summary>
    public string ClientId { get; set; } = "neolink";
    /// <summary>Root of all state/command topics.</summary>
    public string BaseTopic { get; set; } = "neolink";
    /// <summary>Publish Home Assistant MQTT-discovery config so entities appear automatically.</summary>
    public bool Discovery { get; set; } = true;
    /// <summary>Home Assistant's discovery prefix (matches its MQTT integration setting).</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public int KeepAliveSeconds { get; set; } = 30;
    /// <summary>Connect with TLS (broker port is usually 8883). Certificates are not validated.</summary>
    public bool Tls { get; set; }
    /// <summary>How often the server publishes its own health (CPU, memory, disk,
    /// viewers, …) as sensors on a "Neolink.NET Server" device in Home Assistant.
    /// 60 s covers dashboards comfortably; lower it for near-live gauges.
    /// 0 disables the server device entirely.</summary>
    public int StatsIntervalSeconds { get; set; } = 60;
}

/// <summary>Event recording settings ("recording" in the config).</summary>
public sealed class RecordingConfig
{
    /// <summary>
    /// Kill switch for continuous (24/7) recording, kept for emergencies.
    /// When false: no ContinuousRecorder runs, the /api/recordings endpoints are
    /// absent, the per-camera switch is hidden, and the timeline page explains
    /// itself. Event recording is unaffected either way.
    /// </summary>
    public static bool ContinuousEnabled => true;

    /// <summary>Storage directory for clips/thumbnails/event metadata (mount a volume here in Docker).</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional fast tier: event clips/thumbnails/metadata are written here
    /// instead of <see cref="Path"/> (continuous 24/7 footage stays on Path). Point
    /// it at an SSD for quick event review. Empty = keep everything under Path.</summary>
    public string ClipsPath { get; set; } = "";

    /// <summary>Optional archive tier: aged footage is MOVED here instead of being
    /// deleted — but only for cameras whose Archive switch is turned ON in the web
    /// UI (per-camera move/delete ages live there too). Point this at a DIFFERENT
    /// drive than <see cref="Path"/> (in Docker, map a second volume). Empty =
    /// no archive tier; expired footage is deleted as always.</summary>
    public string ArchivePath { get; set; } = "";

    /// <summary>Days to keep events; 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 7;
    /// <summary>Seconds of video kept from before the detection.</summary>
    public int PreSeconds { get; set; } = 5;
    /// <summary>Seconds of quiet after the last detection before an event closes.</summary>
    public int PostSeconds { get; set; } = 8;
    /// <summary>Hard cap on one event/clip; ongoing activity beyond it starts a new event.</summary>
    public int MaxClipSeconds { get; set; } = 120;
    /// <summary>Stream to record: "auto" (main if served, else first), or an explicit stream name.</summary>
    public string Stream { get; set; } = "auto";
    /// <summary>Time limit for one continuous-recording segment file, in minutes.</summary>
    public int SegmentMinutes { get; set; } = 10;
    /// <summary>Size cap for one continuous-recording segment file, in MB (rolls whichever limit hits first).</summary>
    public int MaxSegmentSizeMb { get; set; } = 256;
    /// <summary>Days to keep continuous footage; null = same as RetentionDays.</summary>
    public int? ContinuousRetentionDays { get; set; }

    public int EffectiveContinuousRetentionDays => ContinuousRetentionDays ?? RetentionDays;
}

public sealed class CameraConfig
{
    public required string Name { get; init; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9000;
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string Stream { get; init; } = "both";
    /// <summary>Generic (non-Reolink) camera: pull this RTSP URL as the main stream.</summary>
    public string? RtspMain { get; init; }
    /// <summary>Generic (non-Reolink) camera: pull this RTSP URL as the sub stream.</summary>
    public string? RtspSub { get; init; }
    /// <summary>True when this entry is a plain RTSP camera instead of a Baichuan (Reolink) one.</summary>
    public bool IsGenericRtsp => RtspMain != null || RtspSub != null;
    public byte ChannelId { get; init; }
    public List<string>? PermittedUsers { get; init; }
    /// <summary>
    /// The camera's Reolink HTTP(S) API ("host", "host:port" or full URL), used for
    /// the settings Baichuan has no verified write path for (stream encode profiles).
    /// </summary>
    public string? HttpAddress { get; init; }
    /// <summary>Record detection events for this camera (when recording is configured).</summary>
    public bool Record { get; init; } = true;
    /// <summary>
    /// Battery cameras: true holds the connection (and the camera awake) around the
    /// clock; false lets it sleep, connecting only while someone watches. Unset =
    /// auto — cameras that report a battery sleep, everything else stays always-on.
    /// </summary>
    public bool? AlwaysOn { get; init; }
    /// <summary>The camera's UID (Reolink app → device info, or the sticker; e.g.
    /// "95270000ABCDEFGH"). Used by the UDP discovery probe — UDP-only battery
    /// models (Argus family) answer discovery keyed on it.</summary>
    public string? Uid { get; init; }
    /// <summary>Diagnostic (opt-in): when the camera cannot be reached over TCP,
    /// probe Baichuan-over-UDP discovery — what battery-only models speak instead
    /// of TCP — and log the exchange comprehensively (UID masked, no credentials).
    /// Requires "uid".</summary>
    public bool UdpProbe { get; init; }
    /// <summary>Experimental (opt-in): connect to this camera over Baichuan-over-UDP
    /// instead of TCP — for battery-only models (Argus family) that never listen on
    /// TCP. Requires "uid". The default (false) is the unchanged TCP path.</summary>
    public bool Udp { get; init; }
    /// <summary>Battery cameras (opt-in): while sleep-friendly and unwatched, keep a
    /// cheap liveness poll running so Neolink connects the moment the camera wakes
    /// itself (motion) and captures the event — instead of only connecting on viewer
    /// demand. Default false = the classic park-until-viewer behavior. No effect with
    /// always_on (never sleeps) or on non-battery cameras.</summary>
    public bool WakeCapture { get; init; }
}
