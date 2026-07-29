// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>One event from GET /api/events. Only the fields the shell's alerting
/// actually reads — the WebView renders the rest, and unknown JSON members are
/// skipped by the deserializer, so these records stay minimal on purpose.</summary>
internal sealed record ApiEvent(
    string Id, string Camera, DateTime Start, DateTime End,
    List<string> Labels, bool Reviewed = false, bool Ongoing = false, bool HasThumb = false)
{
    /// <summary>Firmware label dialects folded into the chip vocabulary. Kept in
    /// step with the web UI's NormAlertLabel — an alert rule saved in the browser
    /// has to match the same events here.</summary>
    public static string NormLabel(string l) => l.ToLowerInvariant() switch
    {
        "crossline" or "cross_line" or "tripwire" => "line-crossing",
        "intrude" or "region" or "perimeter" => "intrusion",
        "linger" or "loiter" => "loitering",
        "visitor" => "doorbell",
        var x => x,
    };

    private static readonly (string Label, string Name)[] Known =
    {
        ("person", "Human"), ("vehicle", "Vehicle"), ("animal", "Animal"),
        ("package", "Package"), ("doorbell", "Doorbell"), ("crying", "Crying"),
        ("line-crossing", "Line crossing"), ("intrusion", "Intrusion"),
        ("loitering", "Loitering"), ("external", "External"), ("motion", "Motion"),
    };

    /// <summary>"Human detected", "Human + Vehicle detected" — the same sentence
    /// the web UI puts on its notifications.</summary>
    public string Title
    {
        get
        {
            var names = Known.Where(k => Labels.Contains(k.Label)).Select(k => k.Name)
                .Concat(Labels.Where(l => Known.All(k => k.Label != l)).Select(Cap))
                .Distinct().ToList();
            if (names.Count == 0) names.Add("Motion");
            if (names.Count == 1 && names[0] == "Doorbell") return "Doorbell pressed";
            if (names.Count == 1 && names[0] == "External") return "Externally triggered";
            return string.Join(" + ", names) + " detected";
        }
    }

    /// <summary>The alert vocabulary this event matches against — an event with no
    /// labels at all is plain motion, exactly as the web UI treats it.</summary>
    public List<string> AlertLabels =>
        Labels.Count > 0
            ? Labels.Select(NormLabel).Distinct().ToList()
            : new List<string> { "motion" };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>The slice of GET /api/features the shell watches for server-condition
/// alerts.</summary>
internal sealed record ApiFeatures(
    ApiStorage? Storage = null, bool Overload = false, bool WriteFailure = false);

/// <summary>Worst storage tier in /api/features; null when every tier is healthy.</summary>
internal sealed record ApiStorage(string Label = "", double UsedPercent = 0, bool Full = false);

/// <summary>One camera from GET /api/cameras — the fields the offline alert needs.
/// A dozing battery camera is <c>Asleep</c>, not offline, and a suspended one was
/// switched off on purpose: neither is a fault.</summary>
internal sealed record ApiCamera(
    string Name, bool Online = false, bool Asleep = false, bool Suspended = false);

/// <summary>GET /api/auth/status — whether accounts exist decides if the connect
/// dialog demands credentials.</summary>
internal sealed record ApiAuthStatus(bool Enabled = false);

/// <summary>POST /api/auth/login.</summary>
internal sealed record ApiLogin(string? Token = null, string? Error = null);
