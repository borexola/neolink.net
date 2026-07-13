// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Neolink.Notifications;

/// <summary>
/// A minimal, dependency-free SMTP client — enough of RFC 5321 to submit one
/// multipart (text + HTML) message with authentication. Hand-rolled (like the
/// project's MQTT client) so there's no NuGet dependency and both transports are
/// supported: implicit TLS on 465 and STARTTLS on 587 — the latter is what the
/// built-in System.Net.Mail.SmtpClient can't do reliably. Every call is bounded
/// by an overall timeout and throws on any protocol error; the caller
/// (<see cref="Notifier"/>) swallows and logs, so a bad mail server never affects
/// anything else.
/// </summary>
internal static class SmtpSender
{
    private static readonly TimeSpan Overall = TimeSpan.FromSeconds(25);

    public static async Task SendAsync(NotificationSettings s, string password,
        string subject, string htmlBody, string textBody, CancellationToken outerCt)
    {
        using var timeout = new CancellationTokenSource(Overall);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, timeout.Token);
        var ct = linked.Token;

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(s.SmtpHost, s.SmtpPort, ct).ConfigureAwait(false);

        Stream stream = tcp.GetStream();
        if (s.Security == SmtpSecurity.Ssl)
            stream = await UpgradeAsync(stream, s.SmtpHost, ct).ConfigureAwait(false);

        var buf = new byte[4096];
        await ExpectAsync(stream, buf, 220, ct).ConfigureAwait(false);           // greeting

        var ehloHost = string.IsNullOrWhiteSpace(s.EffectiveFrom) ? "neolink.local"
            : (s.EffectiveFrom.Split('@').LastOrDefault() ?? "neolink.local");
        var ehlo = await CommandAsync(stream, buf, $"EHLO {ehloHost}", ct).ConfigureAwait(false);
        Check(ehlo, 250, "EHLO");

        if (s.Security == SmtpSecurity.StartTls)
        {
            Check(await CommandAsync(stream, buf, "STARTTLS", ct).ConfigureAwait(false), 220, "STARTTLS");
            stream = await UpgradeAsync(stream, s.SmtpHost, ct).ConfigureAwait(false);
            // Re-EHLO over the secured channel (required after upgrade).
            Check(await CommandAsync(stream, buf, $"EHLO {ehloHost}", ct).ConfigureAwait(false), 250, "EHLO(tls)");
        }

        if (!string.IsNullOrEmpty(s.Username))
        {
            Check(await CommandAsync(stream, buf, "AUTH LOGIN", ct).ConfigureAwait(false), 334, "AUTH");
            Check(await CommandAsync(stream, buf, B64(s.Username), ct).ConfigureAwait(false), 334, "AUTH user");
            Check(await CommandAsync(stream, buf, B64(password), ct).ConfigureAwait(false), 235, "AUTH pass");
        }

        Check(await CommandAsync(stream, buf, $"MAIL FROM:<{s.EffectiveFrom}>", ct).ConfigureAwait(false), 250, "MAIL FROM");
        Check(await CommandAsync(stream, buf, $"RCPT TO:<{s.Recipient}>", ct).ConfigureAwait(false), 250, "RCPT TO");
        Check(await CommandAsync(stream, buf, "DATA", ct).ConfigureAwait(false), 354, "DATA");

        var message = BuildMessage(s, subject, htmlBody, textBody);
        await WriteAsync(stream, message, ct).ConfigureAwait(false);
        await WriteAsync(stream, "\r\n.\r\n", ct).ConfigureAwait(false);          // end of DATA
        Check(await ReadReplyAsync(stream, buf, ct).ConfigureAwait(false), 250, "message body");

        try { await CommandAsync(stream, buf, "QUIT", ct).ConfigureAwait(false); } catch { /* best effort */ }
    }

    private static async Task<SslStream> UpgradeAsync(Stream inner, string host, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct)
            .ConfigureAwait(false);
        return ssl;
    }

    /// <summary>MIME multipart/alternative: plain text first, HTML second (clients
    /// pick the richest they render). Headers are ASCII; bodies are UTF-8 base64
    /// so any character survives, avoiding line-length and encoding pitfalls.</summary>
    private static string BuildMessage(NotificationSettings s, string subject, string html, string text)
    {
        var boundary = "nlk_" + Guid.NewGuid().ToString("N");
        var fromName = string.IsNullOrWhiteSpace(s.FromName) ? "Neolink.NET" : s.FromName;
        var sb = new StringBuilder();
        sb.Append("From: ").Append(EncodeHeader(fromName)).Append(" <").Append(s.EffectiveFrom).Append(">\r\n");
        sb.Append("To: <").Append(s.Recipient).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
        sb.Append("Date: ").Append(DateTimeOffset.Now.ToString("r")).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(
            s.EffectiveFrom.Split('@').LastOrDefault() ?? "neolink.local").Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        AppendPart(sb, boundary, "text/plain; charset=utf-8", text);
        AppendPart(sb, boundary, "text/html; charset=utf-8", html);
        sb.Append("--").Append(boundary).Append("--\r\n");
        return sb.ToString();
    }

    private static void AppendPart(StringBuilder sb, string boundary, string contentType, string body)
    {
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        for (int i = 0; i < b64.Length; i += 76)                                  // RFC 2045 line length
            sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        sb.Append("\r\n");
    }

    // RFC 2047 encoded-word for non-ASCII header text (subject / display name).
    private static string EncodeHeader(string value) =>
        value.All(c => c is >= ' ' and < (char)127)
            ? value
            : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static async Task<string> CommandAsync(Stream stream, byte[] buf, string line, CancellationToken ct)
    {
        await WriteAsync(stream, line + "\r\n", ct).ConfigureAwait(false);
        return await ReadReplyAsync(stream, buf, ct).ConfigureAwait(false);
    }

    private static Task WriteAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    /// <summary>Reads a full SMTP reply (handles multi-line "250-...\r\n250 ..." by
    /// reading until a line whose 4th char is a space).</summary>
    private static async Task<string> ReadReplyAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("SMTP connection closed by server");
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            var text = sb.ToString();
            // The last complete line ends the reply when its 4th char is ' ' (not '-').
            int lastLineStart = text.LastIndexOf('\n', Math.Max(0, text.Length - 2)) is var idx && idx >= 0 ? idx + 1 : 0;
            var lastLine = text[lastLineStart..].TrimEnd('\r', '\n');
            if (text.EndsWith('\n') && lastLine.Length >= 4 && lastLine[3] == ' ')
                return text;
        }
    }

    private static async Task ExpectAsync(Stream stream, byte[] buf, int code, CancellationToken ct) =>
        Check(await ReadReplyAsync(stream, buf, ct).ConfigureAwait(false), code, "greeting");

    private static void Check(string reply, int expected, string stage)
    {
        if (!reply.StartsWith(expected.ToString(), StringComparison.Ordinal))
            throw new IOException($"SMTP {stage} rejected: {reply.TrimEnd('\r', '\n')}");
    }
}
