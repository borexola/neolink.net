// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Runtime.InteropServices;

namespace Neolink.Desktop;

/// <summary>
/// The Start Menu shortcut that carries the app's AppUserModelID.
///
/// Windows will only hand an UNPACKAGED app the rich toast API if a Start Menu
/// shortcut stamped with that same AUMID exists — that shortcut is the app's
/// identity as far as the Action Center is concerned. The MSI writes one; this
/// class writes an equivalent for runs the installer never touched (a developer
/// build, an extracted copy), so notifications behave identically either way.
///
/// Everything here is best-effort: no shortcut simply means the toast path is
/// unavailable and <see cref="Toaster"/> falls back to tray balloons.
/// </summary>
internal static class AppShortcut
{
    /// <summary>Must match the Id the installer stamps on its shortcut, or the two
    /// would register as different apps and their notifications would not collapse.</summary>
    public const string AppUserModelId = "OluwaboriOlaleye.NeolinkNET.Desktop";

    public const string ShortcutName = "Neolink.NET Desktop.lnk";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Claims the AUMID for this process. Called before any window is
    /// created: after that Windows has already grouped the taskbar button.</summary>
    public static void ApplyToProcess()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* pre-Win7 shells only; nothing to do */ }
    }

    public static string UserShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", ShortcutName);

    public static string MachineShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        "Programs", ShortcutName);

    /// <summary>True when a shortcut this app can claim already exists — the
    /// installer's per-machine one counts.</summary>
    public static bool Exists() => File.Exists(MachineShortcutPath) || File.Exists(UserShortcutPath);

    /// <summary>
    /// Writes the per-user Start Menu shortcut with the AUMID attached, unless one
    /// already exists. Returns true when a usable shortcut is in place afterwards.
    /// </summary>
    public static bool EnsureExists(string exePath)
    {
        if (Exists()) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserShortcutPath)!);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetDescription("Live view, recordings and alerts for your Reolink cameras");
            link.SetIconLocation(exePath, 0);

            var store = (IPropertyStore)link;
            var key = PropertyKeyAppUserModelId;
            var value = new PropVariant(AppUserModelId);
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally { value.Clear(); }

            ((IPersistFile)link).Save(UserShortcutPath, true);
            return File.Exists(UserShortcutPath);
        }
        catch { return false; }
    }

    // ---- the minimum COM surface for "a .lnk with a property on it" --------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>System.AppUserModel.ID — the shell property that ties a shortcut to
    /// an app identity.</summary>
    private static PropertyKey PropertyKeyAppUserModelId => new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    /// <summary>Just enough PROPVARIANT for a string value.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] private ushort _vt;
        [FieldOffset(8)] private IntPtr _pointer;

        private const ushort VtLpwstr = 31;

        public PropVariant(string value)
        {
            _vt = VtLpwstr;
            _pointer = Marshal.StringToCoTaskMemUni(value);
        }

        public void Clear()
        {
            if (_pointer == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(_pointer);
            _pointer = IntPtr.Zero;
            _vt = 0;
        }
    }
}
