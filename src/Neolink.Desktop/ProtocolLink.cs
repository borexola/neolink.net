// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Microsoft.Win32;

namespace Neolink.Desktop;

/// <summary>
/// The neolink-desktop: URI scheme — how a notification clicked in the Action
/// Center reaches the app. An unpackaged app's in-process toast click event
/// only works while the banner is on screen; once the card slides into the
/// Action Center, Windows needs something it can LAUNCH. Protocol activation
/// is that something, with no COM registration and no packaging: the toast
/// carries neolink-desktop:/events?event=..., Windows runs the exe with that
/// URI, and the single-instance machinery hands the link to the window that
/// already exists (or a fresh start opens straight on it).
/// </summary>
internal static class ProtocolLink
{
    public const string Scheme = "neolink-desktop";

    /// <summary>Registered per-user on every launch, like the autostart entry:
    /// an upgrade can move the exe, and a stale command would launch nothing.</summary>
    public static void RepairRegistration(string exePath)
    {
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            root.SetValue("", "URL:Neolink.NET Desktop");
            root.SetValue("URL Protocol", "");
            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch { /* notifications still show; only Action Center clicks lose their target */ }
    }

    /// <summary>The deep link carried by a protocol launch, or null when the
    /// command line has none.</summary>
    public static string? FromArgs(string[] args)
    {
        var raw = args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        return raw == null ? null : Sanitize(raw[(Scheme.Length + 1)..]);
    }

    /// <summary>Anything on the machine may invoke a registered protocol, so the
    /// payload is held to the one shape a toast actually carries: a single
    /// in-app path. Anything else degrades to the dashboard rather than being
    /// trusted — never an absolute URL, never another origin.</summary>
    public static string Sanitize(string link)
    {
        link = link.Trim();
        if (link.Length is 0 or > 512) return "/";
        if (!link.StartsWith('/') || link.StartsWith("//")) return "/";
        if (link.Contains("://") || link.Contains('\\')) return "/";
        if (link.Any(c => c < ' ' || c > '~')) return "/";
        return link;
    }
}
