// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;

namespace Neolink.Media;

/// <summary>
/// Locates the optional ffmpeg binary once, for every feature that shells out
/// to it (AI pre-roll decode, frame shrinking, audio transcode). ffmpeg is
/// never a requirement: each feature quietly sits out when it is missing.
/// </summary>
public static class Ffmpeg
{
    private static readonly Lazy<string?> ExeLazy = new(() =>
        Locate(Environment.GetEnvironmentVariable("NEOLINK_FFMPEG"),
               Environment.GetEnvironmentVariable("PATH")));

    /// <summary>Full path of the ffmpeg binary, or null when none was found.</summary>
    public static string? ExePath => ExeLazy.Value;

    /// <summary>The NEOLINK_FFMPEG env var wins when it points at an existing
    /// file (the escape hatch for nonstandard installs); otherwise the PATH is
    /// scanned for ffmpeg(.exe). Null = no ffmpeg, features sit out.</summary>
    internal static string? Locate(string? envOverride, string? pathVar)
    {
        if (envOverride is { Length: > 0 })
        {
            try
            {
                if (File.Exists(envOverride)) return envOverride;
                Log.Warn($"NEOLINK_FFMPEG points at '{envOverride}', which does not " +
                         "exist — falling back to the PATH scan");
            }
            catch { /* unusable override: same fallback */ }
        }
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (pathVar ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* an unparsable PATH entry is somebody else's problem */ }
        }
        return null;
    }

    private static readonly Lazy<bool> OpusLazy = new(ProbeOpusEncoder);

    /// <summary>True when the located ffmpeg carries the libopus encoder. The
    /// standard builds (gyan.dev, BtbN, distro packages) all do; probing keeps a
    /// bare-bones build from being promised in the SDP and then failing.</summary>
    public static bool SupportsOpus => OpusLazy.Value;

    private static bool ProbeOpusEncoder()
    {
        if (ExePath is not { } exe) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-h");
            psi.ArgumentList.Add("encoder=libopus");
            using var p = Process.Start(psi);
            if (p == null) return false;
            // Both pipes are drained concurrently: draining one to the end while
            // the other fills its buffer deadlocks rather than running slowly.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { /* already gone */ }
                return false;
            }
            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
            var output = (stdout.IsCompletedSuccessfully ? stdout.Result : "")
                       + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
            return output.Contains("Encoder libopus", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
