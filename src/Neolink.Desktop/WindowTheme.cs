// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Runtime.InteropServices;

namespace Neolink.Desktop;

/// <summary>
/// Title-bar theming: Windows never darkens a title bar on its own — the app
/// must ask per window (DWMWA_USE_IMMERSIVE_DARK_MODE). Follows the system
/// "choose your default app mode" setting, so a dark Windows gets dark chrome
/// and a light Windows stays light, whatever the page inside looks like.
/// </summary>
internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Applies the system theme to the form's title bar. Call once the
    /// handle exists; harmless to repeat (WM_SETTINGCHANGE re-applies live).</summary>
    public static void Apply(Form form)
    {
        if (!form.IsHandleCreated) return;
        int dark = SystemPrefersDark() ? 1 : 0;
        if (DwmSetWindowAttribute(form.Handle, UseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, UseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
    }

    /// <summary>Wires a form to adopt the theme on creation. The main window also
    /// re-applies on the fly via its WndProc; dialogs live too briefly to care.</summary>
    public static void Attach(Form form) =>
        form.HandleCreated += (_, _) => Apply(form);

    /// <summary>WM_SETTINGCHANGE("ImmersiveColorSet") — the user flipped the
    /// Windows theme while the app runs. Call from WndProc before base.</summary>
    public static void OnSettingChange(Form form, ref Message m)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (m.Msg == WM_SETTINGCHANGE
            && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet")
            Apply(form);
    }
}
