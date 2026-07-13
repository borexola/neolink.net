// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neolink.Notifications;

/// <summary>How the SMTP session is secured.</summary>
public enum SmtpSecurity { StartTls, Ssl, None }

/// <summary>
/// User-configured email-notification settings, persisted as notifications.json
/// in the state dir. The SMTP password is stored ONLY as an AES-GCM token
/// (<see cref="PasswordEnc"/>) via <see cref="SecretProtector"/> — never in
/// plaintext, and never returned to the UI (write-only).
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>Master opt-in. Off = no email is ever sent, whatever else is set.</summary>
    public bool Enabled { get; set; }

    /// <summary>The single address that receives every alert.</summary>
    public string Recipient { get; set; } = "";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string Username { get; set; } = "";

    /// <summary>AES-GCM token of the SMTP password (see SecretProtector); "" = none.</summary>
    public string PasswordEnc { get; set; } = "";

    /// <summary>From address; falls back to the username when blank.</summary>
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Neolink.NET";

    // Per-alert switches. Storage-full is the only one on by default (it needs no
    // per-site tuning); the noisier alerts start off so the user opts into each.
    public bool AlertStorage { get; set; } = true;
    public bool AlertOverload { get; set; }
    public bool AlertCameraOffline { get; set; }
    public bool AlertWriteFailure { get; set; }

    /// <summary>Default minutes a camera must stay unreachable before it alerts.</summary>
    public int OfflineThresholdMinutes { get; set; } = 10;

    /// <summary>Per-camera overrides of <see cref="OfflineThresholdMinutes"/>
    /// (0 = never alert for that camera). Absent = use the default.</summary>
    public Dictionary<string, int> CameraOfflineOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The effective offline threshold for a camera (minutes); 0 = off.</summary>
    public int OfflineMinutesFor(string camera) =>
        CameraOfflineOverrides.TryGetValue(camera, out var m) ? m : OfflineThresholdMinutes;

    /// <summary>The From address actually used (username when From is blank).</summary>
    public string EffectiveFrom => string.IsNullOrWhiteSpace(From) ? Username : From;

    public NotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Recipient = Recipient,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        Security = Security,
        Username = Username,
        PasswordEnc = PasswordEnc,
        From = From,
        FromName = FromName,
        AlertStorage = AlertStorage,
        AlertOverload = AlertOverload,
        AlertCameraOffline = AlertCameraOffline,
        AlertWriteFailure = AlertWriteFailure,
        OfflineThresholdMinutes = OfflineThresholdMinutes,
        CameraOfflineOverrides = new(CameraOfflineOverrides, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// Loads/saves <see cref="NotificationSettings"/> (notifications.json next to the
/// other UI state, owner-only). The plaintext SMTP password never leaves this
/// class: it is encrypted on the way in and only decrypted for the mail sender.
/// </summary>
public sealed class NotificationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _file;
    private readonly SecretProtector _protector;
    private readonly object _gate = new();
    private NotificationSettings _settings = new();

    public NotificationStore(string stateDir, SecretProtector protector)
    {
        _file = Path.Combine(stateDir, "notifications.json");
        _protector = protector;
        try
        {
            if (File.Exists(_file))
                _settings = JsonSerializer.Deserialize<NotificationSettings>(File.ReadAllText(_file), JsonOpts)
                            ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Notification settings unreadable ({ex.Message}); notifications start disabled.");
        }
    }

    /// <summary>A private copy of the current settings (password stays encrypted).</summary>
    public NotificationSettings Snapshot()
    {
        lock (_gate) return _settings.Clone();
    }

    /// <summary>The decrypted SMTP password for the mail sender; "" when none/unreadable.</summary>
    public string SmtpPassword()
    {
        string enc;
        lock (_gate) enc = _settings.PasswordEnc;
        return _protector.Unprotect(enc) ?? "";
    }

    /// <summary>True once a password has been stored (so the UI can show "set").</summary>
    public bool HasPassword
    {
        get { lock (_gate) return _settings.PasswordEnc.Length > 0; }
    }

    /// <summary>Replaces the settings. <paramref name="newPassword"/> is write-only:
    /// null keeps the stored password, non-null re-encrypts (""=clear it).</summary>
    public void Save(NotificationSettings incoming, string? newPassword)
    {
        lock (_gate)
        {
            incoming.PasswordEnc = newPassword switch
            {
                null => _settings.PasswordEnc,      // unchanged
                "" => "",                            // cleared
                _ => _protector.Protect(newPassword) // set
            };
            _settings = incoming;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, JsonOpts));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot persist notification settings: {ex.Message}");
        }
    }
}
