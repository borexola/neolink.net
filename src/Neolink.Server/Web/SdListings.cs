// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using Neolink.Streaming;

namespace Neolink.Web;

/// <summary>The last card listing fetched per camera and day, kept on disk so the timeline's
/// strip survives restarts and a sleeping camera's card can still be shown.</summary>
internal static class SdListings
{
    private sealed record Entry(DateTime At, List<SdRecording> List);

    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, Entry>> _all = new(StringComparer.OrdinalIgnoreCase);
    private static string? _path;
    private const int KeepDays = 14;

    public static void Configure(string stateDir)
    {
        _path = Path.Combine(stateDir, "sd-listings.json");
        try
        {
            if (File.Exists(_path))
                _all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(_path))
                       ?? _all;
        }
        catch (Exception ex) { Log.Warn($"sd-listings.json unreadable ({ex.Message}); card listings start empty"); }
    }

    public static void Remember(string camera, DateOnly day, IReadOnlyList<SdRecording> list)
    {
        lock (Gate)
        {
            if (!_all.TryGetValue(camera, out var days))
                _all[camera] = days = new Dictionary<string, Entry>();
            days[day.ToString("yyyy-MM-dd")] = new Entry(DateTime.UtcNow, list.ToList());
            var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(-KeepDays).ToString("yyyy-MM-dd");
            foreach (var old in days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) days.Remove(old);
            if (_path == null) return;
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_all));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Log.Debug($"sd-listings.json not written: {ex.Message}"); }
        }
    }

    public static (DateTime At, IReadOnlyList<SdRecording> List)? Last(string camera, DateOnly day)
    {
        lock (Gate)
        {
            return _all.TryGetValue(camera, out var days) && days.TryGetValue(day.ToString("yyyy-MM-dd"), out var e)
                ? (e.At, e.List) : null;
        }
    }
}
