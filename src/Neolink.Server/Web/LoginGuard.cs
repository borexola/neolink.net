// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Neolink.Web;

/// <summary>Sign-in protection settings, persisted in users.json. Opt-in: an
/// upgrade must not start refusing valid passwords by itself.</summary>
public sealed class LoginGuardSettings
{
    public bool Enabled { get; set; }
    /// <summary>Failed sign-ins on one account before it locks.</summary>
    public int MaxAttempts { get; set; } = 10;
    /// <summary>How long a lock lasts, and the window failures are counted in.</summary>
    public int LockMinutes { get; set; } = 15;
}

/// <summary>
/// Brute-force protection for the web sign-in: repeated failures lock an
/// ACCOUNT, and an ADDRESS failing across many accounts (password spraying) is
/// blocked outright. Callers get one generic answer whether the account exists
/// or not, and the check runs before password verification, so a locked-out
/// attacker learns nothing and costs no PBKDF2 work.
///
/// State is in memory only: a restart clears every lock, which is also the
/// break-glass path out of an accidental lockout.
/// </summary>
public sealed class LoginGuard
{
    private sealed class Entry
    {
        public int Count;
        public DateTime WindowStart;
        public DateTime BlockedUntil;
    }

    /// <summary>An address gets this many times an account's allowance — room
    /// for a shared NAT, far below any useful spray rate.</summary>
    private const int IpFactor = 4;

    /// <summary>Tracking cap per map: beyond it new keys stop being tracked
    /// rather than evicting live state.</summary>
    private const int MaxTracked = 4096;

    /// <summary>Accounts are created with 1-32 characters, so this never touches
    /// a real name; it bounds what an anonymous caller can pin in memory.</summary>
    private const int MaxKeyChars = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _addresses = new();
    private readonly Func<LoginGuardSettings> _settings;

    /// <summary>Test seam; the selftest winds it forward.</summary>
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    public LoginGuard(Func<LoginGuardSettings> settings) => _settings = settings;

    private static TimeSpan LockSpan(LoginGuardSettings s) =>
        TimeSpan.FromMinutes(Math.Clamp(s.LockMinutes, 1, 24 * 60));

    /// <summary>The tracked/logged form of a submitted username. Control
    /// characters would forge log lines and unbounded length would pin memory;
    /// unknown names are still tracked exactly like real ones, which is what
    /// stops enumeration.</summary>
    public static string TrackKey(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(Math.Min(raw.Length, MaxKeyChars));
        foreach (var c in raw)
        {
            if (sb.Length >= MaxKeyChars) break;
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The address to hold responsible for an attempt. Behind a local proxy every
    /// caller shares its address, so the forwarded header names the real client.
    /// The LAST hop is taken, never the first: proxies append the peer they saw,
    /// so only the trailing entry is one this deployment's proxy vouched for —
    /// earlier entries may be caller-supplied. A peer that is not a local proxy
    /// is used as-is and its header ignored.
    /// </summary>
    public static string? ClientAddress(IPAddress? peer, string? forwardedFor)
    {
        if (peer == null) return null;
        if (IsLocalProxy(peer) && !string.IsNullOrWhiteSpace(forwardedFor))
        {
            var hops = forwardedFor.Split(',');
            for (int i = hops.Length - 1; i >= 0; i--)
                if (IPAddress.TryParse(hops[i].Trim(), out var real))
                    return Normalize(real);
        }
        return Normalize(peer);
    }

    /// <summary>Dual-stack listeners report IPv4 callers as ::ffff:a.b.c.d; fold
    /// those back so one client never occupies two tallies.</summary>
    private static string Normalize(IPAddress ip) =>
        (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    private static bool IsLocalProxy(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (ip.IsIPv6LinkLocal) return true;
        return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }

    /// <summary>Whether this attempt must be refused outright, and for how many
    /// more seconds. Must be checked before the password is verified.</summary>
    public bool Blocked(string account, string? address, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        var s = _settings();
        if (!s.Enabled) return false;
        var now = Clock();
        lock (_gate)
        {
            var until = DateTime.MinValue;
            if (_accounts.TryGetValue(account, out var a) && a.BlockedUntil > now)
                until = a.BlockedUntil;
            if (address != null && _addresses.TryGetValue(address, out var ip) && ip.BlockedUntil > now && ip.BlockedUntil > until)
                until = ip.BlockedUntil;
            if (until == DateTime.MinValue) return false;
            retryAfterSeconds = Math.Max(1, (int)(until - now).TotalSeconds);
            return true;
        }
    }

    /// <summary>A wrong password or unknown username landed. Unknown names lock
    /// no account but still burn the address's allowance, which is what catches
    /// enumeration and spraying.</summary>
    public void RecordFailure(string account, string? address)
    {
        var s = _settings();
        if (!s.Enabled) return;
        var now = Clock();
        var span = LockSpan(s);
        lock (_gate)
        {
            if (Bump(_accounts, account, now, span, Math.Max(1, s.MaxAttempts)))
                Log.Warn($"Sign-in protection: account '{account}' locked for {span.TotalMinutes:0} min " +
                         $"after {Math.Max(1, s.MaxAttempts)} failed sign-ins" +
                         (address == null ? "" : $" (last from {address})"));
            if (address != null && Bump(_addresses, address, now, span, Math.Max(1, s.MaxAttempts) * IpFactor))
                Log.Warn($"Sign-in protection: address {address} blocked for {span.TotalMinutes:0} min — " +
                         "repeated failures across accounts (intrusion attempt)");
        }
    }

    /// <summary>The address keeps its tally: one valid login must not launder a
    /// spray in progress.</summary>
    public void RecordSuccess(string account)
    {
        lock (_gate) _accounts.Remove(account);
    }

    /// <summary>True when this failure crossed the limit and started a block.</summary>
    private bool Bump(Dictionary<string, Entry> map, string key, DateTime now, TimeSpan span, int limit)
    {
        if (!map.TryGetValue(key, out var e))
        {
            if (map.Count >= MaxTracked)
            {
                Prune(map, now);
                if (map.Count >= MaxTracked) return false;
            }
            map[key] = e = new Entry { WindowStart = now };
        }
        if (e.BlockedUntil > now) return false;
        if (now - e.WindowStart > span)
        {
            e.Count = 0;
            e.WindowStart = now;
        }
        e.Count++;
        if (e.Count < limit) return false;
        e.BlockedUntil = now + span;
        e.Count = 0;
        return true;
    }

    private static void Prune(Dictionary<string, Entry> map, DateTime now)
    {
        foreach (var key in map.Where(kv => kv.Value.BlockedUntil <= now
                     && now - kv.Value.WindowStart > TimeSpan.FromHours(24))
                     .Select(kv => kv.Key).ToList())
            map.Remove(key);
    }

    public List<(string Name, int MinutesLeft)> LockedAccounts()
    {
        var now = Clock();
        lock (_gate)
            return _accounts.Where(kv => kv.Value.BlockedUntil > now)
                .Select(kv => (kv.Key, Math.Max(1, (int)Math.Ceiling((kv.Value.BlockedUntil - now).TotalMinutes))))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public int BlockedAddressCount()
    {
        var now = Clock();
        lock (_gate) return _addresses.Count(kv => kv.Value.BlockedUntil > now);
    }

    public void UnlockAll()
    {
        lock (_gate)
        {
            _accounts.Clear();
            _addresses.Clear();
        }
        Log.Info("Sign-in protection: all lockouts and address blocks cleared by an admin");
    }
}
