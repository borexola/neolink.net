// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Security.Cryptography;

namespace Neolink.Notifications;

/// <summary>
/// Encrypts small secrets (the SMTP password) at rest with AES-256-GCM
/// (authenticated encryption). A secret the app must REPLAY to a mail server
/// cannot be hashed the way a login password is — the original has to be
/// recoverable at send time — so this is encryption, not a one-way digest. It
/// protects the credential against casual disk / backup / config exposure, but
/// NOT against an attacker who already has full read access to the host (they
/// would have both the key and the ciphertext). That limit is inherent to any
/// self-hosted app that sends its own authenticated email.
///
/// The 256-bit key comes from:
///   1. the NEOLINK_SECRET_KEY environment variable (base64 or hex, 32 bytes) —
///      so the key can live only in the environment (Docker/systemd secret) and
///      never touch disk; the on-disk ciphertext is then useless on its own, or
///   2. an auto-generated <c>secret.key</c> in the state dir, written owner-only
///      (0600 on Unix), kept out of config.json so a config backup can't leak it.
///
/// Token layout (base64): [1-byte version][12-byte nonce][16-byte tag][ciphertext].
/// </summary>
public sealed class SecretProtector
{
    private const byte Version = 1;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    public const string KeyEnvVar = "NEOLINK_SECRET_KEY";
    public const string KeyFileName = "secret.key";

    private readonly byte[] _key;

    /// <summary>Where the active key came from: "env" (NEOLINK_SECRET_KEY),
    /// "file" (the state dir's secret.key) or "ephemeral" (unwritable state dir —
    /// nothing encrypted this run survives a restart).</summary>
    public string KeySource { get; private set; } = "file";

    /// <summary>Full path of the key file when <see cref="KeySource"/> is "file".</summary>
    public string? KeyFile { get; private set; }

    /// <summary>Short one-way identifier of the active key (first 12 hex chars of
    /// its SHA-256) — lets the admin tell WHICH key is in use and match it against
    /// a backup, without the key itself ever being shown.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_key))[..12].ToLowerInvariant();

    public SecretProtector(string stateDir) => _key = ResolveKey(stateDir);

    /// <summary>Test seam: use a fixed key without touching the filesystem.</summary>
    internal SecretProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
        _key = key;
        KeySource = "test";
    }

    /// <summary>Derives an independent 32-byte subkey from the master secret for
    /// <paramref name="purpose"/> (HKDF-SHA256), so different subsystems — footage
    /// encryption, secret tokens — never share the raw key material.</summary>
    public byte[] DeriveSubKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt: null,
            info: System.Text.Encoding.UTF8.GetBytes(purpose));

    /// <summary>Encrypts UTF-8 <paramref name="plaintext"/> to a base64 token.</summary>
    public string Protect(string plaintext)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[data.Length];
        var tag = new byte[TagLen];
        using (var aes = new AesGcm(_key, TagLen))
            aes.Encrypt(nonce, data, cipher, tag);

        var token = new byte[1 + NonceLen + TagLen + cipher.Length];
        token[0] = Version;
        Buffer.BlockCopy(nonce, 0, token, 1, NonceLen);
        Buffer.BlockCopy(tag, 0, token, 1 + NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, token, 1 + NonceLen + TagLen, cipher.Length);
        return Convert.ToBase64String(token);
    }

    /// <summary>Decrypts a token from <see cref="Protect"/>. Returns null on ANY
    /// problem — wrong key, tampering (GCM tag mismatch), truncation, garbage —
    /// so callers degrade to "no password" instead of throwing.</summary>
    public string? Unprotect(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var raw = Convert.FromBase64String(token);
            if (raw.Length < 1 + NonceLen + TagLen || raw[0] != Version) return null;
            var nonce = raw.AsSpan(1, NonceLen);
            var tag = raw.AsSpan(1 + NonceLen, TagLen);
            var cipher = raw.AsSpan(1 + NonceLen + TagLen);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, TagLen))
                aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private byte[] ResolveKey(string stateDir)
    {
        var env = Environment.GetEnvironmentVariable(KeyEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (TryParseKey(env, out var k))
            {
                KeySource = "env";
                return k;
            }
            Log.Warn($"{KeyEnvVar} is set but is not a 32-byte base64/hex key — falling back to the key file.");
        }

        var path = Path.Combine(stateDir, KeyFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32)
                {
                    KeySource = "file";
                    KeyFile = Path.GetFullPath(path);
                    return existing;
                }
                Log.Warn($"Secret key file {path} is malformed ({existing.Length} bytes) — regenerating.");
            }
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            KeySource = "file";
            KeyFile = Path.GetFullPath(path);
            return key;
        }
        catch (Exception ex)
        {
            // A read-only/unwritable state dir must not crash the server. Fall back
            // to an ephemeral key: encryption still works this run, but stored
            // secrets won't survive a restart (the user is told to fix the dir).
            Log.Warn($"Cannot persist the secret key at {path} ({ex.Message}); using an in-memory key " +
                     "(stored secrets will not survive a restart until the state dir is writable).");
            KeySource = "ephemeral";
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    private static bool TryParseKey(string s, out byte[] key)
    {
        s = s.Trim();
        try { key = Convert.FromBase64String(s); if (key.Length == 32) return true; } catch { }
        try
        {
            if (s.Length == 64 && s.All(Uri.IsHexDigit))
            {
                key = Convert.FromHexString(s);
                return key.Length == 32;
            }
        }
        catch { }
        key = Array.Empty<byte>();
        return false;
    }
}
