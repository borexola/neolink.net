// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Microsoft.Win32;

namespace Neolink.Desktop;

/// <summary>
/// "Start with Windows", as the per-user Run key.
///
/// HKCU rather than HKLM or a scheduled task on purpose: it needs no
/// administrator rights, so the toggle in the app works without a UAC prompt,
/// and it uninstalls with the user's profile. The value is rewritten from the
/// current executable path every time the app starts with the toggle on, so an
/// upgrade that moves the binary cannot leave a Run entry pointing at a file
/// that is no longer there.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Neolink.NET Desktop";

    /// <summary>The command Windows should run at logon: the shell, told to come
    /// up in the tray rather than throwing a window at a user who just logged in.</summary>
    private static string Command(string exePath) => $"\"{exePath}\" --minimized";

    public static bool IsEnabled(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Equals(Command(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Any entry at all, even one pointing at an old install path — what
    /// "should I be starting with Windows?" really means across an upgrade.</summary>
    public static bool IsPresent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Sets or clears the entry. Returns true when the registry now says
    /// what was asked for.</summary>
    public static bool Set(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return false;
            if (enabled) key.SetValue(ValueName, Command(exePath), RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Rewrites a stale entry after an upgrade moved the executable.
    /// Silent no-op when autostart is off or already correct.</summary>
    public static void RepairIfStale(bool wanted, string exePath)
    {
        if (!wanted)
        {
            if (IsPresent()) Set(false, exePath);
            return;
        }
        if (!IsEnabled(exePath)) Set(true, exePath);
    }
}
