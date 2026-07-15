// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neolink.Web;

/// <summary>One web-UI account. Settings holds the user's UI state as raw JSON.</summary>
public sealed class UserRecord
{
    public required string Name { get; init; }
    /// <summary>"pbkdf2-sha256${iterations}${saltB64}${hashB64}"</summary>
    public string Hash { get; set; } = "";
    public bool Admin { get; set; }
    public string? Settings { get; set; }
    /// <summary>Additional per-page settings blobs (raw JSON), keyed by page name
    /// (e.g. "timeline") — so each page owns its state without racing the main blob.</summary>
    public Dictionary<string, string>? Pages { get; set; }
}

/// <summary>
/// Web-UI accounts, deliberately database-free: users.json next to the config
/// file holds the accounts and a server secret. Authentication stays DISABLED
/// until the first account exists — creating it (the admin) turns it on.
///
/// Passwords are stored as PBKDF2-SHA256 (210k iterations, 16-byte random salt,
/// constant-time comparison) — the file is safe to check into a password-manager
/// era world, nothing recoverable. Sessions are stateless signed tokens:
/// base64url(name|expiry|pwTag).base64url(HMAC-SHA256(secret, payload)); the
/// pwTag is derived from the current password hash, so changing a password
/// instantly invalidates that user's outstanding tokens.
/// </summary>
public sealed class UserStore
{
    private const int Iterations = 210_000;
    private const int MaxSettingsBytes = 256 * 1024;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    private readonly string _file;
    private readonly object _gate = new();
    private byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private List<UserRecord> _users = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class FileModel
    {
        public string Secret { get; set; } = "";
        public List<UserRecord> Users { get; set; } = new();
    }

    public UserStore(string stateDir, string? legacyDir = null)
    {
        _file = Path.Combine(stateDir, "users.json");
        try
        {
            // A relocated state_dir picks the accounts up from the old location once.
            var source = _file;
            if (!File.Exists(source) && legacyDir != null
                && File.Exists(Path.Combine(legacyDir, "users.json")))
            {
                source = Path.Combine(legacyDir, "users.json");
                Log.Info($"UI accounts: migrating {source} -> {_file}");
            }
            if (File.Exists(source))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(source), JsonOpts);
                if (model != null)
                {
                    if (model.Secret.Length > 0) _secret = Convert.FromBase64String(model.Secret);
                    _users = model.Users;
                }
                if (source != _file)
                    lock (_gate) { SaveLocked(); }
            }
        }
        catch (Exception ex)
        {
            // Refuse half-read credentials: better to run open (and log loudly)
            // than to lock people out with a broken account list.
            Log.Error($"users.json unreadable ({ex.Message}); web-UI authentication is OFF until it is fixed or removed");
            _users = new List<UserRecord>();
        }
    }

    /// <summary>Authentication is enforced once any account exists.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _users.Count > 0; }
    }

    // ------------------------------------------------------------------ passwords

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // ------------------------------------------------------------------ accounts

    /// <summary>Login check. Null on unknown user or wrong password (constant-time).</summary>
    public UserRecord? Verify(string name, string password)
    {
        UserRecord? user;
        lock (_gate) { user = Find(name); }
        if (user == null)
        {
            // Burn comparable CPU for unknown users so timing can't enumerate names.
            VerifyPassword(password, HashPassword("timing-equalizer"));
            return null;
        }
        return VerifyPassword(password, user.Hash) ? user : null;
    }

    /// <summary>Creates an account. The very first one becomes the admin.</summary>
    public UserRecord Add(string name, string password, bool admin)
    {
        ValidateName(name);
        ValidatePassword(password);
        lock (_gate)
        {
            if (Find(name) != null)
                throw new ArgumentException($"user '{name}' already exists");
            var user = new UserRecord { Name = name, Hash = HashPassword(password), Admin = admin };
            _users.Add(user);
            SaveLocked();
            return user;
        }
    }

    public bool SetPassword(string name, string password)
    {
        ValidatePassword(password);
        lock (_gate)
        {
            var user = Find(name);
            if (user == null) return false;
            user.Hash = HashPassword(password); // also invalidates the user's tokens
            SaveLocked();
            return true;
        }
    }

    /// <summary>Deletes a normal user; admins cannot be deleted (there is exactly one).</summary>
    public bool Delete(string name)
    {
        lock (_gate)
        {
            var user = Find(name);
            if (user == null || user.Admin) return false;
            _users.Remove(user);
            SaveLocked();
            return true;
        }
    }

    public List<(string Name, bool Admin)> List()
    {
        lock (_gate) return _users.Select(u => (u.Name, u.Admin)).ToList();
    }

    /// <summary>The admin account (the first user ever created).</summary>
    public UserRecord? AdminUser()
    {
        lock (_gate) return _users.FirstOrDefault(u => u.Admin);
    }

    // ------------------------------------------------------------------ per-user UI settings

    public string GetSettings(string name)
    {
        lock (_gate) return Find(name)?.Settings ?? "{}";
    }

    public bool SetSettings(string name, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxSettingsBytes)
            throw new ArgumentException("settings blob too large");
        using (JsonDocument.Parse(json)) { } // must be valid JSON
        lock (_gate)
        {
            var user = Find(name);
            if (user == null) return false;
            user.Settings = json;
            SaveLocked();
            return true;
        }
    }

    public string GetPageSettings(string name, string page)
    {
        lock (_gate)
            return Find(name)?.Pages?.GetValueOrDefault(page) ?? "{}";
    }

    public bool SetPageSettings(string name, string page, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxSettingsBytes)
            throw new ArgumentException("settings blob too large");
        using (JsonDocument.Parse(json)) { } // must be valid JSON
        lock (_gate)
        {
            var user = Find(name);
            if (user == null) return false;
            (user.Pages ??= new Dictionary<string, string>())[page] = json;
            SaveLocked();
            return true;
        }
    }

    // ------------------------------------------------------------------ session tokens

    public string IssueToken(UserRecord user)
    {
        long expiry = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
        var payload = $"{user.Name}|{expiry}|{PasswordTag(user.Hash)}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(_secret, payloadBytes);
        return $"{Base64Url(payloadBytes)}.{Base64Url(signature)}";
    }

    /// <summary>Returns the token's user when the signature, expiry and password generation all check out.</summary>
    public UserRecord? ValidateToken(string token)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0) return null;
        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = FromBase64Url(token[..dot]);
            signature = FromBase64Url(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
        var expected = HMACSHA256.HashData(_secret, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
            return null;

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var expiry))
            return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
            return null;

        lock (_gate)
        {
            var user = Find(parts[0]);
            return user != null && PasswordTag(user.Hash) == parts[2] ? user : null;
        }
    }

    /// <summary>Short fingerprint of the password hash: rotating the password kills old tokens.</summary>
    private static string PasswordTag(string hash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash)))[..12];

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }

    // ------------------------------------------------------------------ plumbing

    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("username must be 1-32 characters: letters, digits, _ - .");
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 8)
            throw new ArgumentException("password must be at least 8 characters");
    }

    private UserRecord? Find(string name) =>
        _users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    private void SaveLocked()
    {
        var model = new FileModel { Secret = Convert.ToBase64String(_secret), Users = _users };
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOpts));
        // The file holds the token-signing secret: owner-only where the OS supports it.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _file, overwrite: true);
    }
}
