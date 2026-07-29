// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neolink.Desktop;

/// <summary>
/// Everything the shell itself remembers, in
/// %APPDATA%\Neolink.NET\desktop.json. Deliberately small: this is a client, so
/// anything that describes CAMERAS or ALERT RULES belongs to the server account
/// (see <see cref="AlertPrefs"/>) and is only mirrored here so a server that is
/// briefly unreachable does not silence notifications.
/// The password and session token are DPAPI blobs — see <see cref="Dpapi"/>.
/// </summary>
internal sealed class DesktopSettings
{
    // ---- connection -------------------------------------------------------

    /// <summary>Server base URL, e.g. "http://10.1.0.60:8000". No trailing slash.</summary>
    public string ServerUrl { get; set; } = "";

    public string? Username { get; set; }

    /// <summary>DPAPI blob. Kept (opt-in) so an unattended boot can re-authenticate
    /// by itself when the session token has expired — a tray app that silently
    /// stops alerting because nobody typed a password is worse than useless.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>DPAPI blob of the last session token, so a restart resumes without
    /// a round trip.</summary>
    public string? ProtectedToken { get; set; }

    /// <summary>Store the password at all. Off = the shell keeps only the token
    /// and asks for the password again when that stops working.</summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>Accept a TLS certificate that does not validate. Off by default and
    /// only ever set from the connect dialog, where it says what it costs: a LAN
    /// server behind a self-signed certificate is otherwise unreachable, but this
    /// turns off the check that would catch someone impersonating it.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    // ---- window and startup ----------------------------------------------

    public bool StartWithWindows { get; set; }

    /// <summary>Start into the tray with no window — the point of autostart.</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>Closing the window hides it instead of quitting, so alerts survive
    /// the ✕. Turn it off and ✕ means quit.</summary>
    public bool CloseToTray { get; set; } = true;

    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1360;
    public int WindowHeight { get; set; } = 880;
    public bool WindowMaximized { get; set; }

    // ---- notifications (the desktop-only half) ----------------------------

    /// <summary>Master switch for shell-raised notifications. The per-camera and
    /// per-label rules live in <see cref="AlertPrefs"/> on the server account, so
    /// they stay in step with the web UI; this is the "not on THIS machine" switch.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Seconds between alert polls. The web UI uses 10; slower is kinder
    /// to a battery laptop, faster is pointless (events settle over seconds).</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Play the system notification sound. Off = the toast appears silently.</summary>
    public bool Sound { get; set; } = true;

    /// <summary>Show the event thumbnail on the toast (rich toasts only).</summary>
    public bool ShowThumbnail { get; set; } = true;

    /// <summary>Quiet hours, "HH:mm" local, inclusive start to exclusive end.
    /// Wraps midnight when From > To. Null/blank on either = no quiet hours.</summary>
    public string? QuietFrom { get; set; }
    public string? QuietTo { get; set; }

    /// <summary>Quiet hours suppress everything, including camera-offline and
    /// server-condition alerts. Off = only detection alerts go quiet, so a disk
    /// filling up at 3am still reaches you.</summary>
    public bool QuietSilencesSystem { get; set; }

    /// <summary>Clicking a toast brings the window up on that event. Off = the
    /// toast is informational and clicking it does nothing.</summary>
    public bool ClickOpensEvent { get; set; } = true;

    /// <summary>Last known good copy of the account's alert rules, used when the
    /// server cannot be reached at startup and on no-auth servers (which have no
    /// account to store them against).</summary>
    public AlertPrefs? CachedAlertPrefs { get; set; }

    // ---- persistence ------------------------------------------------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>%APPDATA%\Neolink.NET — per user, roams with the profile, and
    /// survives an MSI upgrade because the installer never touches it.</summary>
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Neolink.NET");

    public static string FilePath => Path.Combine(Dir, "desktop.json");

    /// <summary>Reads the settings file. Anything unreadable yields defaults: the
    /// shell must always start, and the connect dialog can rebuild the rest.</summary>
    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), Opts)
                       ?? new DesktopSettings();
        }
        catch { /* fall through to defaults */ }
        return new DesktopSettings();
    }

    private static readonly object SaveGate = new();

    /// <summary>Writes via a temp file and a rename, so a crash mid-save cannot
    /// leave a half-written settings file behind. Serialized under a gate: the
    /// alert engine saves from its poll thread while the UI saves from clicks,
    /// and two writers racing the same temp file would corrupt exactly the file
    /// that holds the credentials.</summary>
    public void Save()
    {
        try
        {
            lock (SaveGate)
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch { /* a settings write failing must never take the app down */ }
    }

    // ---- derived helpers --------------------------------------------------

    // JsonIgnore is load-bearing on these two: they are the DECRYPTED view of the
    // Protected* blobs, and without it the serializer writes the plaintext password
    // and token into desktop.json right next to the DPAPI blobs they exist to protect.
    [JsonIgnore]
    public string? Password
    {
        get => Dpapi.Unprotect(ProtectedPassword);
        set => ProtectedPassword = value == null ? null : Dpapi.Protect(value);
    }

    [JsonIgnore]
    public string? Token
    {
        get => Dpapi.Unprotect(ProtectedToken);
        set => ProtectedToken = value == null ? null : Dpapi.Protect(value);
    }

    [JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(ServerUrl);

    /// <summary>
    /// Accepts what a person actually types — "10.1.0.60:8000", "neolink.lan",
    /// "https://cams.example.com/" — and returns a base URL, or null when there is
    /// no reading that makes sense. A bare host defaults to http, because that is
    /// what a LAN server is.
    /// </summary>
    public static string? NormalizeUrl(string? typed)
    {
        var s = typed?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "http" && uri.Scheme != "https") return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        // Keep a non-root path (reverse proxies mount the UI under /neolink), drop
        // query and fragment, and never leave a trailing slash for callers to
        // double up on.
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>True when local time falls inside the configured quiet hours.
    /// Parsing failures mean "no quiet hours" — a broken time string must not
    /// silence alerts forever.</summary>
    public bool InQuietHours(DateTime localNow)
    {
        if (!TimeOnly.TryParse(QuietFrom, out var from) || !TimeOnly.TryParse(QuietTo, out var to))
            return false;
        if (from == to) return false;                     // an empty window, not a whole day
        var now = TimeOnly.FromDateTime(localNow);
        return from < to
            ? now >= from && now < to                     // 13:00 -> 17:00
            : now >= from || now < to;                    // 22:00 -> 07:00, across midnight
    }
}
