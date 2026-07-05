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
        string stream = "both";
        byte channelId = 0;
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

        return BuildCamera(name, username, password, address, uid, stream, channelId, permitted, httpAddress);
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
        };

        if (MiniToml.GetString(root, "certificate") != null)
            WarnTls();

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
                MiniToml.GetString(c, "http_address")));
        }
        return config;
    }

    // ------------------------------------------------------------------ shared

    private static void WarnTls() =>
        Log.Warn("TLS (certificate) is not supported by Neolink.NET yet; serving plain RTSP. " +
                 "Consider a TLS-terminating proxy if you need rtsps://");

    private static CameraConfig BuildCamera(string? name, string? username, string? password,
        string? address, string? uid, string stream, byte channelId, List<string>? permitted,
        string? httpAddress = null)
    {
        if (name == null) throw new FormatException("camera entry missing \"name\"");
        if (username == null) throw new FormatException($"Camera \"{name}\" missing \"username\"");
        if (uid != null && address == null)
            throw new FormatException(
                $"Camera \"{name}\": UID (relay/battery) connections are not supported by Neolink.NET; " +
                "please use a direct \"address\" instead");
        if (address == null)
            throw new FormatException($"Camera \"{name}\" needs an \"address\" (host or host:port)");

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

        if (Cameras.Count == 0)
            throw new FormatException("No cameras defined in the config file");

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

public sealed class CameraConfig
{
    public required string Name { get; init; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9000;
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string Stream { get; init; } = "both";
    public byte ChannelId { get; init; }
    public List<string>? PermittedUsers { get; init; }
    /// <summary>
    /// The camera's Reolink HTTP(S) API ("host", "host:port" or full URL), used for
    /// the settings Baichuan has no verified write path for (stream encode profiles).
    /// </summary>
    public string? HttpAddress { get; init; }
}
