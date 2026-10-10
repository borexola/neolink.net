// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Collections.Concurrent;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>Keeps camera clocks right: models without a backup clock (Lumus) lose the date
/// at every power cut and can't recover it when their NTP is blocked.</summary>
internal static class CameraClock
{
    private static readonly ConcurrentDictionary<string, DateTime> LastCheck = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>Checks the camera's clock at most every 12 h per camera and corrects it when it is off.</summary>
    public static async Task MaybeSyncAsync(string camera, IBcCamera session, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (LastCheck.TryGetValue(camera, out var last) && now - last < Interval) return;
        LastCheck[camera] = now; // claims the round for the camera's other streams
        bool read = false;
        try
        {
            var general = await session.GetSystemGeneralAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (general == null || BcCameraCommands.ParseTime(general) is not { } cameraTime
                || !int.TryParse(general.Element("timeZone")?.Value.Trim(), out var timeZone))
                return;
            read = true;
            var fix = Correction(cameraTime, DateTime.UtcNow, timeZone);
            if (fix == null) return;
            foreach (var (name, value) in new[] { ("year", fix.Value.Year), ("month", fix.Value.Month),
                         ("day", fix.Value.Day), ("hour", fix.Value.Hour), ("minute", fix.Value.Minute),
                         ("second", fix.Value.Second) })
                general.SetElementValue(name, value);
            await session.SetSystemGeneralAsync(general, ct).ConfigureAwait(false);
            Log.Info($"{camera}: camera clock read {cameraTime:yyyy-MM-dd HH:mm:ss}; set to {fix:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            // Fire-and-forget beside the session: a dropped link or a refusal just skips this round.
            Log.Debug($"{camera}: clock check skipped: {ex.Message}");
        }
        finally
        {
            if (!read) LastCheck.TryRemove(camera, out _); // try again on the next connect
        }
    }

    /// <summary>The wall time to write, or null when fine. timeZone is seconds west of UTC; whole-hour
    /// offsets (the camera's DST) are kept, drift over 10 s is fixed, a lost date is reset.</summary>
    internal static DateTime? Correction(DateTime cameraLocal, DateTime utcNow, int timeZoneSeconds)
    {
        var expected = utcNow.AddSeconds(-timeZoneSeconds);
        var delta = cameraLocal - expected;
        if (cameraLocal.Year < 2020) return expected;
        int hours = (int)Math.Round(delta.TotalHours);
        if (Math.Abs(hours) > 2) return null;
        var drift = delta - TimeSpan.FromHours(hours);
        return Math.Abs(drift.TotalSeconds) <= 10 ? null : expected.AddHours(hours);
    }
}
