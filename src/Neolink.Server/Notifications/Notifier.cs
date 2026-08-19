// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Threading.Channels;

namespace Neolink.Notifications;

/// <summary>Delivery channels an alert goes to; the source decides, delivery
/// re-checks readiness.</summary>
[Flags]
public enum AlertChannels { None = 0, Email = 1, Webhook = 2, All = Email | Webhook }

/// <summary>Structured facts of a detection event, for webhook payloads and
/// template placeholders. Null on server alerts. <paramref name="Id"/> is the
/// event-store id, the anchor for deep links ("" when unknown).</summary>
public sealed record EventInfo(string Camera, IReadOnlyList<string> Labels,
    DateTime StartUtc, double DurationSeconds, bool Ongoing, string Id = "");

/// <summary>One alert to deliver. <paramref name="Key"/> is the dedup identity
/// (e.g. "storage", "camera:Driveway"); <paramref name="Recovery"/> marks the
/// paired "resolved" message. <paramref name="Brief"/> is the push-length text
/// a webhook prefers over <paramref name="Body"/> — a phone notification wants
/// "Person on Driveway at 14:32", not the email's story.</summary>
public sealed record Alert(string Key, bool Recovery, string Subject, string Headline,
    string Body, string? Context = null, IReadOnlyList<EmailAttachment>? Attachments = null,
    AlertChannels Channels = AlertChannels.All, EventInfo? Event = null, string? Brief = null);

/// <summary>A file riding along with an email (event snapshots).</summary>
public sealed record EmailAttachment(string Name, string ContentType, byte[] Data);

/// <summary>
/// Sends the app's critical alerts as email, in complete isolation from the rest
/// of the server: a bounded background queue, all sends wrapped so an unreachable
/// or misconfigured mail server can never throw into recording, streaming or MQTT
/// — worst case an alert is logged and dropped. Alerts are edge-detected here, so
/// callers just <see cref="Report"/> the CURRENT state each poll: the first time a
/// condition goes bad emails once, it re-reminds at most every few hours while it
/// persists, and clearing it emails a one-line "resolved".
/// </summary>
public sealed class Notifier
{
    private static readonly TimeSpan RemindEvery = TimeSpan.FromHours(6);

    private readonly NotificationStore _store;
    private readonly string _serverName;
    // Wait mode: TryWrite reports a full queue as false (the Drop* modes discard
    // the item and still return true, which silently defeats every drop check).
    private readonly Channel<Alert> _queue = Channel.CreateBounded<Alert>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, DateTime> _active = new();  // key -> last emailed (UTC)
    private readonly object _gate = new();

    public Notifier(NotificationStore store, string serverName)
    {
        _store = store;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "Neolink.NET server" : serverName;
    }

    public NotificationStore Store => _store;

    internal static bool EmailReady(NotificationSettings s) =>
        s.Enabled && !string.IsNullOrWhiteSpace(s.Recipient) && !string.IsNullOrWhiteSpace(s.SmtpHost);

    internal static bool WebhookReady(NotificationSettings s) =>
        s.WebhookEnabled && !string.IsNullOrWhiteSpace(s.WebhookUrl);

    private static AlertChannels ReadyChannels(NotificationSettings s) =>
        (EmailReady(s) ? AlertChannels.Email : AlertChannels.None)
        | (WebhookReady(s) ? AlertChannels.Webhook : AlertChannels.None);

    /// <summary>Runs the background send loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await DeliverAsync(alert, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Log.Warn($"Notification delivery failed ({alert.Subject}): {Log.Flatten(ex)} " +
                             "— check Server settings → Notifications; nothing else is affected");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Level-triggered report of a condition. <paramref name="active"/>
    /// true = the problem is happening now. Emails the problem once on the leading
    /// edge (and re-reminds every few hours while active); emails
    /// <paramref name="recovery"/> when it clears — but only if the problem was
    /// actually emailed. Enabled/toggle checks happen at the source, plus a master
    /// Enabled guard here.</summary>
    public void Report(string key, bool active, Func<Alert> problem, Func<Alert> recovery)
    {
        var s = _store.Snapshot();
        var ch = (EmailReady(s) ? AlertChannels.Email : AlertChannels.None)
            | (WebhookReady(s) && s.WebhookServerAlerts ? AlertChannels.Webhook : AlertChannels.None);
        if (ch == AlertChannels.None) return;
        Alert? toSend = null;
        bool known;
        DateTime last;
        lock (_gate)
        {
            known = _active.TryGetValue(key, out last);
            if (active)
            {
                if (!known || DateTime.UtcNow - last >= RemindEvery)
                {
                    _active[key] = DateTime.UtcNow;
                    var a = problem();
                    toSend = known ? a with { Subject = "[Reminder] " + a.Subject } : a;
                }
            }
            else if (known)
            {
                _active.Remove(key);
                toSend = recovery();
            }
        }
        if (toSend == null || _queue.Writer.TryWrite(toSend with { Channels = ch })) return;
        Log.Warn($"Notification queue is full — dropped: {toSend.Subject}");
        // Un-stamp so the next poll retries instead of going quiet for hours
        // (or, for a dropped recovery, forgetting the problem was ever emailed).
        lock (_gate)
        {
            if (known) _active[key] = last;
            else _active.Remove(key);
        }
    }

    /// <summary>One-shot send, no edge detection — detection-event emails: each
    /// event either mails or it doesn't (the cooldown decides at the source).
    /// Same bounded queue and the same "never throws into the caller" contract.
    /// Returns whether the alert was queued (a full queue drops it, logged).</summary>
    public bool Send(Alert alert)
    {
        if ((alert.Channels & ReadyChannels(_store.Snapshot())) == AlertChannels.None) return false;
        if (_queue.Writer.TryWrite(alert)) return true;
        Log.Warn($"Notification queue is full — dropped: {alert.Subject}");
        return false;
    }

    /// <summary>Sends a test email with the given (possibly unsaved) settings and
    /// an optional new password. Returns null on success, else a short error to
    /// show the user. Never throws.</summary>
    public async Task<string?> SendTestAsync(NotificationSettings settings, string? password, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.Recipient)) return "Set a recipient email first.";
            if (string.IsNullOrWhiteSpace(settings.SmtpHost)) return "Set the SMTP server host first.";
            var pw = password ?? _store.SmtpPassword();
            var alert = new Alert("test", false, $"{_serverName}: test notification",
                "Test notification",
                "This is a test from Neolink.NET. If it reached you, email alerts are configured correctly.");
            var (html, text) = NotificationTemplate.Render(alert, _serverName);
            await SmtpSender.SendAsync(settings, pw, alert.Subject, html, text, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Sends a test webhook with the given (possibly unsaved) settings;
    /// <paramref name="token"/> null = use the stored one. Returns null on
    /// success, else a short error to show the user. Never throws.</summary>
    public async Task<string?> SendTestWebhookAsync(NotificationSettings settings, string? token,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.WebhookUrl)) return "Set the webhook URL first.";
            var alert = new Alert("test", false, $"{_serverName}: test notification",
                "Test notification",
                "This is a test from Neolink.NET. If it reached you, the webhook is configured correctly.",
                Channels: AlertChannels.Webhook);
            await WebhookSender.SendAsync(settings, token ?? _store.WebhookToken(), alert, _serverName, ct)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            // Flattened: "The SSL connection could not be established, see
            // inner exception" without the inner exception diagnoses nothing.
            return Log.Flatten(ex);
        }
    }

    // One channel failing must not cost the other its delivery.
    private async Task DeliverAsync(Alert alert, CancellationToken ct)
    {
        var s = _store.Snapshot();
        if (alert.Channels.HasFlag(AlertChannels.Email) && EmailReady(s))
        {
            try
            {
                var (html, text) = NotificationTemplate.Render(alert, _serverName);
                await SmtpSender.SendAsync(s, _store.SmtpPassword(), alert.Subject, html, text, ct, alert.Attachments)
                    .ConfigureAwait(false);
                Log.Info($"Notification emailed to {s.Recipient}: {alert.Subject}" +
                         (alert.Attachments is { Count: > 0 } att ? $" ({att.Count} snapshot(s))" : ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Notification email failed ({alert.Subject}) via " +
                         $"{s.SmtpHost}:{s.SmtpPort} [{s.Security}]: {Log.Flatten(ex)} " +
                         "— check Server settings → Notifications; nothing else is affected");
            }
        }
        if (alert.Channels.HasFlag(AlertChannels.Webhook) && WebhookReady(s))
        {
            try
            {
                await WebhookSender.SendAsync(s, _store.WebhookToken(), alert, _serverName, ct)
                    .ConfigureAwait(false);
                Log.Info($"Notification webhooked: {alert.Subject}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Notification webhook failed ({alert.Subject}) via {s.WebhookUrl}: " +
                         $"{Log.Flatten(ex)} — check Server settings → Notifications; nothing else is affected");
            }
        }
    }
}
