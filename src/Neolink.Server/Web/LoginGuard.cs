// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Web;

/// <summary>Sign-in protection settings — opt-in, persisted in users.json next
/// to the accounts they protect. Off by default: a LAN server behind a firewall
/// gets no benefit from lockouts, and an accidental lockout there is pure
/// annoyance; an internet-facing server turns it on in one click.</summary>
public sealed class LoginGuardSettings
{
    public bool Enabled { get; set; }
    /// <summary>Failed sign-ins on one account before it locks.</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>How long an account lock (and an address block) lasts. Doubles
    /// as the rolling window in which failures are counted.</summary>
    public int LockMinutes { get; set; } = 15;
}

/// <summary>
/// Brute-force protection for the web sign-in, entirely in memory: repeated
/// failures on one ACCOUNT lock that account for a while, and an ADDRESS that
/// keeps failing across accounts — the password-spray / credential-stuffing
/// shape, where no single account ever reaches its own limit — is blocked
/// outright. Blocked callers get one generic answer whether the account exists
/// or not, so the guard never becomes a username oracle; the lock also
/// short-circuits BEFORE password verification, so a locked-out attacker cannot
/// keep burning PBKDF2 work or testing guesses against a slow clock.
///
/// State is deliberately not persisted: a restart clears every lock, which is
/// also the admin's break-glass path if they lock themselves out. Every lock
/// and block is written to the log with its source address, so fail2ban-style
/// tooling can act on the same signal.
/// </summary>
public sealed class LoginGuard
{
    private sealed class Entry
    {
        public int Count;
        public DateTime WindowStart;
        public DateTime BlockedUntil;
    }

    /// <summary>An address gets this many times an account's allowance before it
    /// is blocked — room for a shared NAT with several fat-fingered humans, but
    /// far below any useful spray rate.</summary>
    private const int IpFactor = 4;

    /// <summary>Tracking cap per map. An attacker inventing usernames (or a
    /// botnet of addresses) stops being TRACKED beyond this, never evicts live
    /// state, and per-account locking is unaffected — real accounts are few.</summary>
    private const int MaxTracked = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _addresses = new();
    private readonly Func<LoginGuardSettings> _settings;

    /// <summary>Testable time source; the selftest winds it forward.</summary>
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    public LoginGuard(Func<LoginGuardSettings> settings) => _settings = settings;

    private static TimeSpan LockSpan(LoginGuardSettings s) =>
        TimeSpan.FromMinutes(Math.Clamp(s.LockMinutes, 1, 24 * 60));

    /// <summary>Whether this attempt must be refused outright (locked account or
    /// blocked address), and for how many more seconds. Checked before the
    /// password is, so a lock always answers the same way regardless of the
    /// guess — and costs no hashing.</summary>
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

    /// <summary>A wrong password (or unknown username) landed. Counts against
    /// both the account name AND the source address — unknown names never lock
    /// an account (there is none), but they still burn the address's allowance,
    /// which is what catches enumeration and spraying.</summary>
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

    /// <summary>A correct sign-in clears the account's slate. The address keeps
    /// its tally: one valid login must not launder a spray in progress.</summary>
    public void RecordSuccess(string account)
    {
        lock (_gate) _accounts.Remove(account);
    }

    /// <summary>Returns true when this failure crossed the limit and started a block.</summary>
    private bool Bump(Dictionary<string, Entry> map, string key, DateTime now, TimeSpan span, int limit)
    {
        if (!map.TryGetValue(key, out var e))
        {
            if (map.Count >= MaxTracked)
            {
                Prune(map, now);
                if (map.Count >= MaxTracked) return false; // full of live state: stop tracking new keys
            }
            map[key] = e = new Entry { WindowStart = now };
        }
        if (e.BlockedUntil > now) return false;           // already blocked: nothing new to declare
        if (now - e.WindowStart > span)
        {
            e.Count = 0;                                   // stale window: start counting afresh
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

    /// <summary>Currently locked accounts, for the settings UI.</summary>
    public List<(string Name, int MinutesLeft)> LockedAccounts()
    {
        var now = Clock();
        lock (_gate)
            return _accounts.Where(kv => kv.Value.BlockedUntil > now)
                .Select(kv => (kv.Key, Math.Max(1, (int)Math.Ceiling((kv.Value.BlockedUntil - now).TotalMinutes))))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>How many source addresses are blocked right now.</summary>
    public int BlockedAddressCount()
    {
        var now = Clock();
        lock (_gate) return _addresses.Count(kv => kv.Value.BlockedUntil > now);
    }

    /// <summary>Admin lever: forgive everything at once — the fix for "I locked
    /// myself out of my own camera server while standing at the door".</summary>
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
