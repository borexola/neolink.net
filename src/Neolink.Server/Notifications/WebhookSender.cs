// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neolink.Notifications;

/// <summary>
/// Delivers an alert to the user-configured HTTP endpoint. Body modes:
/// "json" (full alert, snapshots base64), "text" (rendered template),
/// "snapshot" (first image as body, text in headers — the ntfy shape),
/// "multipart" (payload_json plus image files — the Discord shape).
/// Placeholders render in the template and in header values.
/// </summary>
internal static class WebhookSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Homelab endpoints commonly sit behind an internal CA the server has no
    // root for; opt-in per settings, never the default.
    private static readonly HttpClient HttpInsecure = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal const string DefaultTextTemplate = "{title}\n{message}";
    internal const string DefaultMultipartTemplate = "{\"content\":\"{title}\\n{message}\"}";

    public static async Task SendAsync(NotificationSettings s, string token, Alert alert,
        string serverName, CancellationToken ct)
    {
        using var req = BuildRequest(s, token, alert, serverName);
        using var res = await (s.WebhookInsecureTls ? HttpInsecure : Http)
            .SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = "";
            try { body = (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
            catch { }
            if (body.Length > 200) body = body[..200];
            throw new IOException($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}" +
                                  (body.Length > 0 ? $": {body}" : ""));
        }
    }

    internal static HttpRequestMessage BuildRequest(NotificationSettings s, string token,
        Alert alert, string serverName)
    {
        token = token.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();
        var method = string.Equals(s.WebhookMethod, "PUT", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Put : HttpMethod.Post;
        var req = new HttpRequestMessage(method, s.WebhookUrl.Trim());

        var mode = s.WebhookBodyMode;
        var first = alert.Attachments is { Count: > 0 } att ? att[0] : null;
        bool snapshotFallback = mode == "snapshot" && first == null;
        if (snapshotFallback) mode = "text";

        req.Content = mode switch
        {
            "text" => new StringContent(
                Render(Template(s.WebhookBodyTemplate, DefaultTextTemplate), alert, serverName),
                Encoding.UTF8),
            "snapshot" => ImageContent(first!),
            "multipart" => MultipartContent(s, alert, serverName),
            _ => JsonPayload(alert, serverName),
        };

        bool hasAuthLine = false;
        foreach (var (name, value) in ParseHeaderLines(s.WebhookHeaders))
        {
            // ntfy reads the message from X-Message only when the body is the
            // image; on the text fallback the body already carries it.
            if (snapshotFallback && name.Equals("X-Message", StringComparison.OrdinalIgnoreCase)) continue;
            if (snapshotFallback && name.Equals("X-Filename", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) hasAuthLine = true;
            var v = HeaderValue(Render(value, alert, serverName));
            if (!req.Headers.TryAddWithoutValidation(name, v))
            {
                req.Content.Headers.Remove(name);
                req.Content.Headers.TryAddWithoutValidation(name, v);
            }
        }
        // The stored token is the easy path; an explicit Authorization line is
        // the escape hatch for other schemes and must win.
        if (!hasAuthLine && token.Length > 0)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + HeaderValue(token));
        return req;
    }

    private static string Template(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    private static ByteArrayContent ImageContent(EmailAttachment a)
    {
        var content = new ByteArrayContent(a.Data);
        content.Headers.ContentType = new MediaTypeHeaderValue(a.ContentType);
        return content;
    }

    private static HttpContent JsonPayload(Alert alert, string serverName)
    {
        var e = alert.Event;
        var payload = new
        {
            type = e != null ? "event" : "alert",
            server = serverName,
            title = alert.Headline,
            message = alert.Body,
            camera = e?.Camera,
            labels = e?.Labels,
            time = (e?.StartUtc ?? DateTime.UtcNow).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            durationSeconds = e == null ? (double?)null : Math.Round(e.DurationSeconds),
            ongoing = e?.Ongoing,
            snapshots = alert.Attachments is { Count: > 0 } att
                ? att.Select(a => Convert.ToBase64String(a.Data)).ToList()
                : null,
        };
        return new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
    }

    private static HttpContent MultipartContent(NotificationSettings s, Alert alert, string serverName)
    {
        var mp = new MultipartFormDataContent();
        mp.Add(new StringContent(
            Render(Template(s.WebhookBodyTemplate, DefaultMultipartTemplate), alert, serverName),
            Encoding.UTF8, "application/json"), "payload_json");
        if (alert.Attachments != null)
            for (int i = 0; i < alert.Attachments.Count; i++)
            {
                var a = alert.Attachments[i];
                var part = new ByteArrayContent(a.Data);
                part.Headers.ContentType = new MediaTypeHeaderValue(a.ContentType);
                mp.Add(part, $"file{i + 1}", SmtpSender.HeaderSafeName(a.Name));
            }
        return mp;
    }

    /// <summary>Substitutes the documented placeholders; anything else in the
    /// template — JSON braces included — passes through untouched.</summary>
    internal static string Render(string template, Alert alert, string serverName)
    {
        var e = alert.Event;
        return template
            .Replace("{title}", alert.Headline)
            .Replace("{message}", alert.Body)
            .Replace("{subject}", alert.Subject)
            .Replace("{camera}", e?.Camera ?? "")
            .Replace("{labels}", e != null ? string.Join(" + ", e.Labels) : "")
            .Replace("{time}", (e?.StartUtc ?? DateTime.UtcNow).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{duration}", e != null
                ? Math.Round(e.DurationSeconds).ToString(CultureInfo.InvariantCulture) : "")
            .Replace("{status}", e == null ? "alert" : e.Ongoing ? "ongoing" : "ended")
            .Replace("{server}", serverName);
    }

    /// <summary>"Name: value" lines; blank or colon-less lines are skipped.</summary>
    internal static List<(string Name, string Value)> ParseHeaderLines(IEnumerable<string>? lines)
    {
        var result = new List<(string, string)>();
        foreach (var raw in lines ?? Enumerable.Empty<string>())
        {
            int i = raw.IndexOf(':');
            if (i <= 0) continue;
            var name = raw[..i].Trim();
            if (name.Length == 0) continue;
            result.Add((name, raw[(i + 1)..].Trim()));
        }
        return result;
    }

    /// <summary>Header values must be Latin-1-safe and free of control
    /// characters (injection); em-dashes fold to '-', other non-ASCII to '?'.</summary>
    internal static string HeaderValue(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (var c in v)
            sb.Append(c switch
            {
                '—' or '–' => '-',
                < ' ' => ' ',
                > '~' => '?',
                _ => c,
            });
        return sb.ToString();
    }
}
