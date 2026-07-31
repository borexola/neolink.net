// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Neolink.Desktop;

/// <summary>
/// Puts an <see cref="Alert"/> on screen, preferring a real Windows toast (it
/// carries the camera's thumbnail and survives in the Action Center) and falling
/// back to a tray balloon wherever that is refused — an unpackaged app only gets
/// toasts once an AUMID shortcut exists, and there is no reason to lose
/// notifications over it.
///
/// Click-to-open toasts activate through the neolink-desktop: protocol, so a
/// click works from the Action Center too — even hours later, even if the app
/// quit meanwhile. <see cref="Activated"/> still carries balloon clicks and
/// the no-deep-link foreground case.
/// </summary>
internal sealed class Toaster : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly bool _rich;
    private string? _pendingBalloonLink;

    /// <summary>Deep link of the clicked notification (may be null when the alert
    /// had none). Raised on the UI thread.</summary>
    public event Action<string?>? Activated;

    /// <param name="ensureShortcut">Whether to CREATE the AUMID shortcut when none
    /// exists. The app proper says yes; the selftest says no — a test run must not
    /// leave a Start Menu entry behind as a side effect.</param>
    public Toaster(NotifyIcon tray, string exePath, bool ensureShortcut = true)
    {
        _tray = tray;
        AppShortcut.ApplyToProcess();
        _rich = (ensureShortcut ? AppShortcut.EnsureExists(exePath) : AppShortcut.Exists())
                && ProbeRichToasts();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var link = _pendingBalloonLink;
            _pendingBalloonLink = null;
            Activated?.Invoke(link);
        };
    }

    /// <summary>True when notifications go out as real toasts rather than tray
    /// balloons. Shown on the notifications page so the difference is not a
    /// mystery.</summary>
    public bool RichToasts => _rich;

    /// <summary>Ask the notification manager for a notifier before relying on it:
    /// a missing or unregistered AUMID fails here, once, instead of on every
    /// alert.</summary>
    private static bool ProbeRichToasts()
    {
        try
        {
            _ = ToastNotificationManager.CreateToastNotifier(AppShortcut.AppUserModelId);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Shows an alert. <paramref name="imageFile"/> is a local path to the event
    /// thumbnail (already downloaded) or null; remote URLs are not an option for
    /// an unpackaged app, which is why the caller fetches it first.
    /// </summary>
    public void Show(Alert alert, DesktopSettings settings, string? imageFile)
    {
        if (_rich && TryShowToast(alert, settings, imageFile)) return;
        DesktopLog.Write(_rich
            ? $"alert {alert.Tag}: toast REFUSED by Windows — falling back to a tray balloon"
            : $"alert {alert.Tag}: rich toasts unavailable (no AUMID shortcut) — tray balloon");
        ShowBalloon(alert, settings);
    }

    private bool TryShowToast(Alert alert, DesktopSettings settings, string? imageFile)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(BuildToastXml(alert, settings, imageFile));
            var toast = new ToastNotification(xml)
            {
                // Same tag as the web UI's notifications: a re-alert on the same
                // camera replaces the previous card instead of stacking.
                Tag = SanitizeTag(alert.Tag),
                Group = "neolink",
            };
            toast.Activated += (_, args) =>
            {
                var link = (args as ToastActivatedEventArgs)?.Arguments;
                Activated?.Invoke(string.IsNullOrEmpty(link) ? alert.DeepLink : link);
            };
            ToastNotificationManager.CreateToastNotifier(AppShortcut.AppUserModelId).Show(toast);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The toast payload. Kept as hand-built XML rather than a builder
    /// library — it is twenty lines and it costs no dependency.</summary>
    internal static string BuildToastXml(Alert alert, DesktopSettings settings, string? imageFile)
    {
        var sb = new StringBuilder();
        sb.Append("<toast");
        // Protocol activation, not "foreground": the in-process click event only
        // reaches a toast while its banner is up. A card clicked later in the
        // Action Center needs Windows to LAUNCH something — the neolink-desktop:
        // URI — and the single-instance machinery routes it to the live window.
        if (settings.ClickOpensEvent && alert.DeepLink != null)
            sb.Append(" launch=\"").Append(Escape(ProtocolLink.Scheme + ":" + alert.DeepLink))
              .Append("\" activationType=\"protocol\"");
        else
            sb.Append(" activationType=\"foreground\"");
        sb.Append("><visual><binding template=\"ToastGeneric\">");
        sb.Append("<text>").Append(Escape(alert.Title)).Append("</text>");
        sb.Append("<text>").Append(Escape(alert.Body)).Append("</text>");
        if (settings.ShowThumbnail && imageFile != null)
            sb.Append("<image src=\"").Append(Escape(new Uri(imageFile).AbsoluteUri)).Append("\"/>");
        sb.Append("</binding></visual>");
        sb.Append(settings.Sound
            ? "<audio src=\"ms-winsoundevent:Notification.Default\"/>"
            : "<audio silent=\"true\"/>");
        sb.Append("</toast>");
        return sb.ToString();
    }

    private void ShowBalloon(Alert alert, DesktopSettings settings)
    {
        _pendingBalloonLink = settings.ClickOpensEvent ? alert.DeepLink : null;
        try
        {
            // Windows caps the visible time itself; the argument is a floor that
            // modern shells ignore, and the balloon lands in the Action Center.
            _tray.ShowBalloonTip(5000, alert.Title, alert.Body, ToolTipIcon.Info);
        }
        catch { /* the shell refuses balloons while another is showing; drop it */ }
    }

    /// <summary>Toast tags are limited to 64 characters and must not carry markup.</summary>
    internal static string SanitizeTag(string tag)
    {
        var clean = new string(tag.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        if (clean.Length == 0) clean = "neolink";
        return clean.Length <= 64 ? clean : clean[^64..];
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>How many of this app's toasts Windows is currently holding in the
    /// Action Center. Used by --test-notification to tell "Windows accepted it and
    /// you missed it" apart from "Windows refused it" — the second is a settings
    /// problem, the first is not. -1 when there is no history to ask about.</summary>
    public int HistoryCount()
    {
        if (!_rich) return -1;
        try { return ToastNotificationManager.History.GetHistory(AppShortcut.AppUserModelId).Count; }
        catch { return -1; }
    }

    /// <summary>Clears this app's notifications from the Action Center — used on
    /// exit so a quit app leaves no live cards behind.</summary>
    public void ClearHistory()
    {
        if (!_rich) return;
        try { ToastNotificationManager.History.Clear(AppShortcut.AppUserModelId); }
        catch { /* nothing to clear, or no identity: both fine */ }
    }

    public void Dispose() => ClearHistory();
}
