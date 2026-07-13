// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Threading.Channels;

namespace Neolink.Notifications;

/// <summary>One alert to email. <paramref name="Key"/> is the dedup identity
/// (e.g. "storage", "camera:Driveway"); <paramref name="Recovery"/> marks the
/// paired "resolved" message.</summary>
public sealed record Alert(string Key, bool Recovery, string Subject, string Headline,
    string Body, string? Context = null);

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
    private readonly Channel<Alert> _queue = Channel.CreateBounded<Alert>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, DateTime> _active = new();  // key -> last emailed (UTC)
    private readonly object _gate = new();

    public Notifier(NotificationStore store, string serverName)
    {
        _store = store;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "Neolink.NET server" : serverName;
    }

    public NotificationStore Store => _store;

    /// <summary>Runs the background send loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await DeliverAsync(alert, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Warn($"Notification email failed ({alert.Subject}): {ex.Message}"); }
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
        if (!_store.Snapshot().Enabled) return;
        Alert? toSend = null;
        lock (_gate)
        {
            bool known = _active.TryGetValue(key, out var last);
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
        if (toSend != null) _queue.Writer.TryWrite(toSend);       // non-blocking; dropped if flooded
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

    private async Task DeliverAsync(Alert alert, CancellationToken ct)
    {
        var s = _store.Snapshot();
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Recipient) || string.IsNullOrWhiteSpace(s.SmtpHost))
            return; // config went away between enqueue and send — drop quietly
        var (html, text) = NotificationTemplate.Render(alert, _serverName);
        await SmtpSender.SendAsync(s, _store.SmtpPassword(), alert.Subject, html, text, ct).ConfigureAwait(false);
        Log.Info($"Notification emailed to {s.Recipient}: {alert.Subject}");
    }
}
