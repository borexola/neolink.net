// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// Minimal thread-safe console logger (no external dependencies).
/// Level is controlled with the NEOLINK_LOG environment variable
/// (trace|debug|info|warn|error) or the --verbose flag.
/// </summary>
public static class Log
{
    public static LogLevel Level = LogLevel.Info;
    private static readonly object Gate = new();

    /// <summary>
    /// Optional tap receiving every emitted line (feeds the web UI's live log
    /// stream). MUST be non-throwing and must never log (that would recurse).
    /// </summary>
    public static Action<LogLevel, string>? Tap;

    static Log()
    {
        var env = Environment.GetEnvironmentVariable("NEOLINK_LOG");
        if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse<LogLevel>(env, true, out var lvl))
            Level = lvl;
    }

    public static void Trace(string msg) => Write(LogLevel.Trace, msg);
    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static void Error(string msg, Exception ex)
    {
        Write(LogLevel.Error, $"{msg}: {Flatten(ex)}");
        if (Level <= LogLevel.Debug) Write(LogLevel.Debug, ex.ToString());
    }

    public static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" -> ", parts);
    }

    private static void Write(LogLevel level, string msg)
    {
        if (level < Level) return;
        try { Tap?.Invoke(level, msg); } catch { /* the tap must never break logging */ }
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelTag(level)}] {msg}";
        lock (Gate)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Trace => ConsoleColor.DarkGray,
                _ => prev,
            };
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }
    }

    /// <summary>Three-letter console tag for a level ("INF", "WRN", ...).</summary>
    public static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };
}
