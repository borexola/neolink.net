// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Text;

namespace Neolink.Notifications;

/// <summary>
/// The standard alert email: a small, table-based HTML layout with everything
/// inline (email clients strip &lt;style&gt; and rarely do flexbox), plus a plain-text
/// alternative for text-only clients. Red banner for a problem, green for a
/// "resolved" follow-up. Deliberately self-contained — no remote images.
/// </summary>
internal static class NotificationTemplate
{
    public static (string Html, string Text) Render(Alert a, string serverName)
    {
        var accent = a.Recovery ? "#16a34a" : "#dc2626";       // green / red
        var badge = a.Recovery ? "RESOLVED" : "ALERT";
        var when = DateTimeOffset.Now.ToString("ddd d MMM yyyy · HH:mm zzz");

        string H(string s) => WebUtility.HtmlEncode(s);
        var contextRow = string.IsNullOrEmpty(a.Context) ? "" :
            $"<tr><td style=\"padding:2px 0;color:#6b7280;\">Details</td><td style=\"padding:2px 0;\">{H(a.Context!)}</td></tr>";

        var html = $$"""
            <!doctype html><html><body style="margin:0;background:#f3f4f6;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f3f4f6;padding:24px 12px;">
            <tr><td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#ffffff;border-radius:12px;overflow:hidden;border:1px solid #e5e7eb;">
                <tr><td style="background:{{accent}};padding:14px 20px;color:#ffffff;font-weight:700;letter-spacing:1px;font-size:12px;">
                  {{badge}} · NEOLINK.NET
                </td></tr>
                <tr><td style="padding:22px 20px 6px;">
                  <div style="font-size:19px;font-weight:700;color:#111827;">{{H(a.Headline)}}</div>
                </td></tr>
                <tr><td style="padding:6px 20px 16px;color:#374151;font-size:14px;line-height:1.5;">
                  {{H(a.Body)}}
                </td></tr>
                <tr><td style="padding:0 20px 20px;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:13px;border-top:1px solid #e5e7eb;padding-top:12px;">
                    <tr><td style="padding:2px 0;color:#6b7280;width:90px;">Server</td><td style="padding:2px 0;color:#111827;">{{H(serverName)}}</td></tr>
                    <tr><td style="padding:2px 0;color:#6b7280;">Time</td><td style="padding:2px 0;color:#111827;">{{H(when)}}</td></tr>
                    {{contextRow}}
                  </table>
                </td></tr>
                <tr><td style="padding:12px 20px;background:#f9fafb;color:#9ca3af;font-size:11px;border-top:1px solid #e5e7eb;">
                  Automated alert from Neolink.NET. Manage which alerts you receive in the web UI under Server settings → Notifications.
                </td></tr>
              </table>
            </td></tr></table>
            </body></html>
            """;

        var text = new StringBuilder()
            .Append('[').Append(badge).Append("] ").Append(a.Headline).Append("\r\n\r\n")
            .Append(a.Body).Append("\r\n\r\n")
            .Append("Server: ").Append(serverName).Append("\r\n")
            .Append("Time:   ").Append(when).Append("\r\n")
            .Append(string.IsNullOrEmpty(a.Context) ? "" : "Details: " + a.Context + "\r\n")
            .Append("\r\n-- \r\nAutomated alert from Neolink.NET. Manage alerts in Server settings → Notifications.")
            .ToString();

        return (html, text);
    }
}
