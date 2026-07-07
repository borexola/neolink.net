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
/// Cameras and RTSP users are deliberately not editable here; they are lists
/// with credentials and deserve a text editor.
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
        string Normalized(string k) => k.Replace("_", "").Replace("-", "").ToLowerInvariant();
        var target = Normalized(key);
        foreach (var kv in root)
        {
            if (Normalized(kv.Key) == target && kv.Value is JsonObject existing)
                return existing;
        }
        var section = new JsonObject();
        root[key] = section;
        return section;
    }
}
