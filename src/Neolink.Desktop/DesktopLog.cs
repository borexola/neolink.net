// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>
/// The shell's diagnostic trail — why an alert did or did not appear. A missed
/// notification is invisible by nature; this file is the only place the answer
/// can live. Best-effort: logging must never break alerting.
/// </summary>
internal static class DesktopLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Neolink.NET", "desktop.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = path + ".old";
                    try { File.Delete(old); } catch { }
                    try { File.Move(path, old); } catch { }
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
