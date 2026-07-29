// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>
/// The account's alert rules — the SAME blob the web UI keeps at
/// GET/PUT /api/me/settings/notifications. Field names and defaults mirror the
/// web UI's AlertPrefs exactly, so turning "person" on for the front door in the
/// browser turns it on in the desktop app and the other way round; there is one
/// set of rules per account, not one per client.
///
/// The web UI writes this with the default (PascalCase) naming policy and reads
/// it case-insensitively, so this class must serialize PascalCase too — see
/// <see cref="ServerLink"/>. Unknown members are skipped by default, which is
/// what keeps a newer web UI from losing keys it does not know about here.
/// </summary>
internal sealed class AlertPrefs
{
    /// <summary>The account's master switch (mirrors the web UI's toggle). The
    /// per-machine switch is DesktopSettings.NotificationsEnabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds before the same camera+labels combination may alert again.</summary>
    public int CooldownSeconds { get; set; } = 60;

    private Dictionary<string, List<string>> _cameras = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>camera name -> the detection labels that should alert. A camera
    /// absent from this map never raises a detection alert.
    ///
    /// The setter rebuilds the dictionary case-insensitively rather than taking
    /// the caller's: deserialization hands over a plain Dictionary with the
    /// default comparer, and camera names arriving from the API with different
    /// casing than the saved rule would then silently stop matching.</summary>
    public Dictionary<string, List<string>> Cameras
    {
        get => _cameras;
        set
        {
            var rebuilt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in value ?? new Dictionary<string, List<string>>())
                rebuilt[kv.Key] = kv.Value ?? new List<string>();   // last spelling wins
            _cameras = rebuilt;
        }
    }

    public bool SysStorage { get; set; } = true;
    public bool SysOverload { get; set; } = true;
    public bool SysWriteFailure { get; set; } = true;

    /// <summary>Cameras that alert when they drop offline and when they return.</summary>
    public List<string> Offline { get; set; } = new();

    /// <summary>The label vocabulary the UI offers — identical to the web UI's list.</summary>
    public static readonly string[] Labels =
    {
        "person", "vehicle", "animal", "package", "doorbell", "crying",
        "line-crossing", "intrusion", "loitering", "motion",
    };

    public AlertPrefs Clone() => new()
    {
        Enabled = Enabled,
        CooldownSeconds = CooldownSeconds,
        Cameras = Cameras.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        SysStorage = SysStorage,
        SysOverload = SysOverload,
        SysWriteFailure = SysWriteFailure,
        Offline = Offline.ToList(),
    };

    public bool WantsLabel(string camera, string label) =>
        Cameras.TryGetValue(camera, out var wanted)
        && wanted.Contains(label, StringComparer.OrdinalIgnoreCase);

    public bool WantsOffline(string camera) =>
        Offline.Contains(camera, StringComparer.OrdinalIgnoreCase);
}
