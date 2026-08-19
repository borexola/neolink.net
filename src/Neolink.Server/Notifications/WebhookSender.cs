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
    // The clients' own timeout is only a backstop — the real budget is
    // per-request (TimeoutFor), scaled to what the body carries.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // Homelab endpoints commonly sit behind an internal CA the server has no
    // root for; opt-in per settings, never the default.
    private static readonly HttpClient HttpInsecure = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    }) { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Per-request budget: a fixed window plus upload time by
    /// attachment size (base64 in json mode makes the payload 4/3 the raw
    /// bytes), sized for a ~256 kbit/s worst-case uplink, capped at ten
    /// minutes — a fixed window can never deliver a snapshot-heavy body on a
    /// slow uplink.</summary>
    internal static TimeSpan TimeoutFor(Alert alert)
    {
        long bytes = 0;
        if (alert.Attachments != null) foreach (var a in alert.Attachments) bytes += a.Data.LongLength;
        var total = TimeSpan.FromSeconds(15) + TimeSpan.FromSeconds(bytes * 4.0 / 3.0 / 32_000);
        return total > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : total;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal const string DefaultTextTemplate = "{title}\n{message}\n{link}";
    internal const string DefaultMultipartTemplate = "{\"content\":\"{title}\\n{message}\\n{link}\"}";

    public static async Task SendAsync(NotificationSettings s, string token, Alert alert,
        string serverName, CancellationToken ct)
    {
        using var req = BuildRequest(s, token, alert, serverName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutFor(alert));
        using var res = await (s.WebhookInsecureTls ? HttpInsecure : Http)
            .SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = "";
            try { body = (await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)).Trim(); }
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

        // A JSON-shaped template needs its substituted values escaped, or one
        // quote in a camera name 400s every delivery. Text mode is JSON exactly
        // when the user's Content-Type header says so (the Slack/Gotify presets).
        bool jsonBody = ParseHeaderLines(s.WebhookHeaders).Any(h =>
            h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            && h.Value.Contains("json", StringComparison.OrdinalIgnoreCase));
        var link = Link(s, alert);

        req.Content = mode switch
        {
            "text" => new StringContent(
                Render(Template(s.WebhookBodyTemplate, DefaultTextTemplate), alert, serverName, jsonBody, link),
                Encoding.UTF8),
            "snapshot" => ImageContent(first!),
            "multipart" => MultipartContent(s, alert, serverName),
            _ => JsonPayload(s, alert, serverName),
        };

        bool hasAuthLine = false, hasClickLine = false;
        foreach (var (name, value) in ParseHeaderLines(s.WebhookHeaders))
        {
            // ntfy reads the message from X-Message only when the body is the
            // image; on the text fallback the body already carries it.
            if (snapshotFallback && name.Equals("X-Message", StringComparison.OrdinalIgnoreCase)) continue;
            if (snapshotFallback && name.Equals("X-Filename", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) hasAuthLine = true;
            if (name.Equals("Click", StringComparison.OrdinalIgnoreCase)
                || name.Equals("X-Click", StringComparison.OrdinalIgnoreCase)) hasClickLine = true;
            var v = HeaderValue(Render(value, alert, serverName, jsonEscape: false, link));
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
        // Snapshot mode is the ntfy shape: a tap should open the event, so the
        // deep link rides the native Click header (an explicit line wins).
        if (mode == "snapshot" && !hasClickLine && link.Length > 0)
            req.Headers.TryAddWithoutValidation("X-Click", HeaderValue(link));
        return req;
    }

    /// <summary>The deep link that opens this event in the web UI, or "" when
    /// the server has no PublicUrl configured or the alert is not an event.</summary>
    internal static string Link(NotificationSettings s, Alert alert)
    {
        var baseUrl = s.PublicUrl.Trim().TrimEnd('/');
        return baseUrl.Length == 0 || alert.Event is not { Id.Length: > 0 } e
            ? ""
            : $"{baseUrl}/events?event={Uri.EscapeDataString(e.Id)}";
    }

    private static string Template(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    private static ByteArrayContent ImageContent(EmailAttachment a)
    {
        var content = new ByteArrayContent(a.Data);
        content.Headers.ContentType = new MediaTypeHeaderValue(a.ContentType);
        return content;
    }

    private static HttpContent JsonPayload(NotificationSettings s, Alert alert, string serverName)
    {
        var e = alert.Event;
        var payload = new
        {
            type = e != null ? "event" : "alert",
            server = serverName,
            title = alert.Headline,
            message = alert.Brief ?? alert.Body,
            detail = alert.Brief != null ? alert.Body : null,
            link = Link(s, alert) is { Length: > 0 } l ? l : null,
            camera = e?.Camera,
            labels = e?.Labels,
            time = (e?.StartUtc ?? DateTime.UtcNow).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
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
            Render(Template(s.WebhookBodyTemplate, DefaultMultipartTemplate), alert, serverName,
                jsonEscape: true, Link(s, alert)),
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
    /// template — JSON braces included — passes through untouched. With
    /// <paramref name="jsonEscape"/> the substituted VALUES are JSON-string
    /// escaped (the template's own syntax is the user's business). {message} is
    /// the push-length Brief when the alert carries one; {detail} is always the
    /// full body.</summary>
    internal static string Render(string template, Alert alert, string serverName,
        bool jsonEscape = false, string link = "")
    {
        string V(string v) => jsonEscape ? JsonEncodedText.Encode(v).ToString() : v;
        var e = alert.Event;
        return template
            .Replace("{title}", V(alert.Headline))
            .Replace("{message}", V(alert.Brief ?? alert.Body))
            .Replace("{detail}", V(alert.Body))
            .Replace("{link}", V(link))
            .Replace("{subject}", V(alert.Subject))
            .Replace("{camera}", V(e?.Camera ?? ""))
            .Replace("{labels}", V(e != null ? string.Join(" + ", e.Labels) : ""))
            .Replace("{time}", (e?.StartUtc ?? DateTime.UtcNow).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("{duration}", e != null
                ? Math.Round(e.DurationSeconds).ToString(CultureInfo.InvariantCulture) : "")
            .Replace("{status}", e == null ? "alert" : e.Ongoing ? "ongoing" : "ended")
            .Replace("{server}", V(serverName));
    }

    /// <summary>"Name: value" lines; blank lines, colon-less lines and lines
    /// whose name is not an RFC 7230 token are skipped — one saved typo like
    /// "X Title: x" must not abort every delivery at request-build time.</summary>
    internal static List<(string Name, string Value)> ParseHeaderLines(IEnumerable<string>? lines)
    {
        var result = new List<(string, string)>();
        foreach (var raw in lines ?? Enumerable.Empty<string>())
        {
            int i = raw.IndexOf(':');
            if (i <= 0) continue;
            var name = raw[..i].Trim();
            if (name.Length == 0 || !name.All(IsTokenChar)) continue;
            result.Add((name, raw[(i + 1)..].Trim()));
        }
        return result;
    }

    private static bool IsTokenChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
            or '^' or '_' or '`' or '|' or '~';

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
