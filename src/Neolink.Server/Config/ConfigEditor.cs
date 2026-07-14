// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neolink.Config;

/// <summary>
/// Read-modify-write editing of config.json for the admin settings UI.
/// The raw file is the source of truth: unknown fields survive untouched, every
/// candidate is validated through the normal <see cref="NeolinkConfig.Load"/>
/// before it replaces the file (atomically, keeping a .bak of the previous
/// version). Comments do NOT survive a rewrite — the UI says so.
/// RTSP users are deliberately not editable here (credentials in a list deserve
/// a text editor); cameras ARE editable via the camera helpers below, with
/// passwords handled write-only by the API layer.
/// </summary>
public static class ConfigEditor
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static bool IsWritable(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The editable settings, read fresh from the file.</summary>
    public static object Describe(string path)
    {
        var cfg = NeolinkConfig.Load(path);
        return new
        {
            path = Path.GetFullPath(path),
            writable = IsWritable(path),
            settings = new
            {
                bind = cfg.BindAddr,
                bindPort = cfg.BindPort,
                webPort = cfg.WebPort,
                webBind = cfg.WebBind,
                webUi = cfg.WebUi,
                ui = new
                {
                    trickleSpeed = cfg.Ui.TrickleSpeed,
                    stateDir = cfg.Ui.StateDir,
                    resetAdminPassword = cfg.EffectiveResetAdminPassword,
                    talk = cfg.Ui.Talk,
                },
                recording = cfg.Recording == null ? null : new
                {
                    path = cfg.Recording.Path,
                    retentionDays = cfg.Recording.RetentionDays,
                    preSeconds = cfg.Recording.PreSeconds,
                    postSeconds = cfg.Recording.PostSeconds,
                    maxClipSeconds = cfg.Recording.MaxClipSeconds,
                    stream = cfg.Recording.Stream,
                    segmentMinutes = cfg.Recording.SegmentMinutes,
                    continuousRetentionDays = cfg.Recording.ContinuousRetentionDays,
                },
                // Broker/credentials stay file-only; the UI adjusts the cadence.
                mqtt = cfg.Mqtt == null ? null : new
                {
                    statsInterval = cfg.Mqtt.StatsIntervalSeconds,
                },
            },
        };
    }

    /// <summary>
    /// Applies a mutation to the raw config document, validates the result and
    /// atomically replaces the file. Throws <see cref="FormatException"/> when the
    /// candidate does not validate, IO exceptions when the file cannot be written.
    /// </summary>
    public static void Apply(string path, Action<JsonObject> mutate)
    {
        lock (Gate)
        {
            var text = File.ReadAllText(path);
            var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new FormatException("config root must be a JSON object");

            mutate(root);

            // Validate the candidate exactly the way startup would.
            var candidate = root.ToJsonString(WriteOpts);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, candidate);
            try
            {
                NeolinkConfig.Load(tmp); // throws FormatException on anything invalid
            }
            catch
            {
                File.Delete(tmp);
                throw;
            }

            try { File.Copy(path, path + ".bak", overwrite: true); }
            catch (IOException) { /* backup is best-effort */ }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Sets or removes a key on an object node (null removes).</summary>
    public static void Set(JsonObject obj, string key, JsonNode? value)
    {
        // The loader accepts any casing/underscore variant; normalize to the
        // canonical snake_case spelling and drop other spellings of the same key.
        string Normalized(string k) => k.Replace("_", "").Replace("-", "").ToLowerInvariant();
        var target = Normalized(key);
        foreach (var existing in obj.Where(kv => Normalized(kv.Key) == target).Select(kv => kv.Key).ToList())
            obj.Remove(existing);
        if (value != null)
            obj[key] = value;
    }

    /// <summary>The (possibly differently-spelled) child object for a section, created on demand.</summary>
    public static JsonObject Section(JsonObject root, string key)
    {
        if (TryGetSection(root, key) is { } existing) return existing;
        var section = new JsonObject();
        root[key] = section;
        return section;
    }

    /// <summary>Like <see cref="Section"/>, but never creates — for sections that
    /// are only meaningful with fields the UI doesn't edit (an mqtt block without
    /// a broker would just fail validation).</summary>
    public static JsonObject? TryGetSection(JsonObject root, string key)
    {
        string Normalized(string k) => k.Replace("_", "").Replace("-", "").ToLowerInvariant();
        var target = Normalized(key);
        foreach (var kv in root)
        {
            if (Normalized(kv.Key) == target && kv.Value is JsonObject existing)
                return existing;
        }
        return null;
    }

    // ------------------------------------------------------------------ cameras

    /// <summary>The cameras array (any key spelling), created on demand.</summary>
    public static JsonArray Cameras(JsonObject root)
    {
        string Normalized(string k) => k.Replace("_", "").Replace("-", "").ToLowerInvariant();
        foreach (var kv in root)
        {
            if (Normalized(kv.Key) == "cameras" && kv.Value is JsonArray existing)
                return existing;
        }
        var cams = new JsonArray();
        root["cameras"] = cams;
        return cams;
    }

    /// <summary>One camera entry by name (case-insensitive, any key spelling).</summary>
    public static JsonObject? FindCamera(JsonArray cameras, string name) =>
        cameras.OfType<JsonObject>().FirstOrDefault(c =>
            string.Equals(GetString(c, "name"), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A string field of a camera entry, tolerant of key spellings.</summary>
    public static string? GetString(JsonObject obj, string key)
    {
        string Normalized(string k) => k.Replace("_", "").Replace("-", "").ToLowerInvariant();
        var target = Normalized(key);
        foreach (var kv in obj)
        {
            if (Normalized(kv.Key) == target && kv.Value is JsonValue v && v.TryGetValue<string>(out var s))
                return s;
        }
        return null;
    }

    /// <summary>Validates a "host" or "host:port" address: null = fine, otherwise
    /// the reason it is not.</summary>
    public static string? HostPortError(string address)
    {
        address = address.Trim();
        if (address.Length == 0) return "address is required";
        if (address.Contains("://")) return "address must be a bare host or host:port, not a URL";
        if (address.Any(char.IsWhiteSpace)) return "address must not contain spaces";
        string host = address;
        int colon = address.LastIndexOf(':');
        if (colon > address.LastIndexOf(']')) // tolerate [IPv6]:port
        {
            host = address[..colon];
            if (!int.TryParse(address[(colon + 1)..], out var p) || p is < 1 or > 65535)
                return "port must be 1-65535";
        }
        host = host.Trim('[', ']');
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return $"\"{host}\" is not a valid host name or IP address";
        return null;
    }

    /// <summary>An RTSP URL safe to show in the UI: the userinfo password (if any)
    /// is replaced with ****. Round-trip contract: a client that sends the masked
    /// value back means "keep the stored one".</summary>
    public static string? MaskRtspPassword(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        int at = url.IndexOf('@');
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (at < 0 || scheme < 0 || at < scheme) return url;
        int colon = url.IndexOf(':', scheme + 3);
        if (colon < 0 || colon > at) return url; // no password part
        return url[..(colon + 1)] + "****" + url[at..];
    }
}
