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

    /// <summary>The editable settings, read fresh from the file.
    /// <paramref name="encryption"/> is the caller's live key report (source,
    /// fingerprint, same-disk flag) — runtime state, not part of the file.</summary>
    public static object Describe(string path, object? encryption = null)
    {
        var cfg = NeolinkConfig.Load(path);
        // Load drops unknown keys, and the stash a "Disable recording" leaves
        // behind is one: read it raw so the UI can prefill the enable form.
        string? disabledPath = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) is JsonObject raw && TryGetSection(raw, "recording_disabled") is { } stash)
                disabledPath = GetString(stash, "path");
        }
        catch { /* a TOML config has no stash; the prefill is a nicety */ }
        return new
        {
            path = Path.GetFullPath(path),
            writable = IsWritable(path),
            encryption,
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
                    showBackgroundTasks = cfg.Ui.ShowBackgroundTasks,
                },
                recordingDisabledPath = disabledPath,
                recording = cfg.Recording == null ? null : new
                {
                    path = cfg.Recording.Path,
                    clipsPath = cfg.Recording.ClipsPath,
                    archivePath = cfg.Recording.ArchivePath,
                    retentionDays = cfg.Recording.RetentionDays,
                    preSeconds = cfg.Recording.PreSeconds,
                    postSeconds = cfg.Recording.PostSeconds,
                    maxClipSeconds = cfg.Recording.MaxClipSeconds,
                    stream = cfg.Recording.Stream,
                    segmentMinutes = cfg.Recording.SegmentMinutes,
                    maxSegmentSizeMb = cfg.Recording.MaxSegmentSizeMb,
                    continuousRetentionDays = cfg.Recording.ContinuousRetentionDays,
                    encrypt = cfg.Recording.Encrypt,
                },
                // Passwords never leave the server; hasPassword drives the
                // "stored — blank keeps it" placeholder client-side.
                mqtt = cfg.Mqtt == null ? null : new
                {
                    broker = cfg.Mqtt.Broker,
                    port = cfg.Mqtt.Port,
                    tls = cfg.Mqtt.Tls,
                    username = cfg.Mqtt.Username,
                    hasPassword = !string.IsNullOrEmpty(cfg.Mqtt.Password),
                    clientId = cfg.Mqtt.ClientId,
                    baseTopic = cfg.Mqtt.BaseTopic,
                    discovery = cfg.Mqtt.Discovery,
                    discoveryPrefix = cfg.Mqtt.DiscoveryPrefix,
                    keepAlive = cfg.Mqtt.KeepAliveSeconds,
                    maxPacketBytes = cfg.Mqtt.MaxPacketBytes,
                    statsInterval = cfg.Mqtt.StatsIntervalSeconds,
                },
                // The push-port list travels as display text ("443, 53") — the
                // editor is a text field and the API parses it back strictly.
                wakeHints = cfg.WakeHints == null ? null : new
                {
                    syslogPort = cfg.WakeHints.SyslogPort,
                    pushPorts = string.Join(", ", cfg.WakeHints.PushPorts),
                    bind = cfg.WakeHints.Bind,
                },
            },
        };
    }

    /// <summary>Parses a user-typed port list ("443, 53" — commas/spaces/semicolons).
    /// Empty text is an empty list. Throws <see cref="FormatException"/> on junk or
    /// out-of-range entries; duplicates collapse, order is kept, 16 ports max.</summary>
    public static List<int> ParsePortList(string text)
    {
        var ports = new List<int>();
        foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(tok, out var p) || p is < 1 or > 65535)
                throw new FormatException($"\"{tok}\" is not a valid port (1-65535)");
            if (!ports.Contains(p)) ports.Add(p);
        }
        if (ports.Count > 16)
            throw new FormatException("at most 16 ports");
        return ports;
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

            // strict: a camera the loader would merely SKIP at boot has to be refused
            // here, or the editor accepts it and it vanishes next start. But only ever
            // as strict as the file being REPLACED — a config that already holds an
            // unusable entry (the add-on can write one) would otherwise fail every
            // save, including the ones that would have removed it.
            bool strict = true;
            try { NeolinkConfig.Load(path, strict: true); }
            catch (FormatException) { strict = false; }
            catch { /* unreadable for another reason: the mutation below decides */ }

            mutate(root);

            var candidate = root.ToJsonString(WriteOpts);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, candidate);
            try
            {
                NeolinkConfig.Load(tmp, strict);
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
    /// <summary>Hides the password inside a "scheme://user:pass@host" URL before it
    /// goes to a browser. Named for the stream URLs it was written for; the ONVIF
    /// address takes the same shape and the same masking.</summary>
    public static string? MaskRtspPassword(string? url)
    {
        if (LoginSpan(url) is not { } s || s.PassStart < 0) return url;
        return url![..s.PassStart] + Mask + url[s.At..];
    }

    /// <summary>What a stored password is shown as.</summary>
    public const string Mask = "****";

    /// <summary>
    /// Undoes <see cref="MaskRtspPassword"/> on a value an admin has EDITED: wherever
    /// the edited value still carries the mask, the stored password goes back in its
    /// place. Without this, changing the host or path of a URL whose password is
    /// shown masked either stored the literal mask (destroying the password) or was
    /// quietly thrown away. Returns null when there is nothing to restore it from,
    /// so the caller keeps the stored value rather than writing a mask.
    /// </summary>
    public static string? UnmaskPassword(string edited, string? stored)
    {
        if (!edited.Contains(Mask, StringComparison.Ordinal)) return edited;
        if (LoginSpan(edited) is not { PassStart: >= 0 } e
            || edited[e.PassStart..e.At] != Mask
            || LoginSpan(stored) is not { PassStart: >= 0 } s)
            return null;
        return edited[..e.PassStart] + stored![s.PassStart..s.At] + edited[e.At..];
    }

    /// <summary>Where the password sits inside a "scheme://user:pass@host/path" or a
    /// bare "user:pass@host:port": PassStart is its first character (-1 when there
    /// is a user but no password), At is the '@' that ends the login. The login ends
    /// at the LAST '@' inside the authority — which is how a URL parser reads it, and
    /// so how the camera sees it — because an '@' in a password is common and people
    /// do not percent-escape it. Null when the value carries no login at all.</summary>
    private static (int PassStart, int At)? LoginSpan(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        int start = scheme < 0 ? 0 : scheme + 3;
        int end = url.IndexOf('/', start);
        if (end < 0) end = url.Length;
        if (end <= start) return null; // no authority to hold a login
        int at = url.LastIndexOf('@', end - 1, end - start);
        if (at < start) return null;
        int colon = url.IndexOf(':', start, at - start);
        return (colon < 0 ? -1 : colon + 1, at);
    }
}
