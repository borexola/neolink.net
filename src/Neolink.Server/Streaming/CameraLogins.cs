using System.Text.Json.Nodes;
using Neolink.Bc;
using Neolink.Config;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>How a camera password change ended; Entries are the camera entries that now use it.</summary>
public sealed record PasswordChange(bool Ok, string Message, IReadOnlyList<string> Entries);

/// <summary>The running pieces that hold each Reolink entry's login, so a new camera password reaches all of them.</summary>
public sealed class CameraLogins
{
    private sealed record Entry(CameraConfig Config, IReadOnlyList<CameraService> Services, CameraControl Control,
        ReolinkHttpApi? Http, OnvifClient? Onvif);

    /// <summary>One change in flight: the entries it covers and the password each had before.</summary>
    private sealed record Change(IReadOnlyList<Entry> Same, IReadOnlyList<string?> Before, string Password,
        IReadOnlyList<string> Names)
    {
        public void UseNew() { foreach (var e in Same) e.Config.Password = Password; }
        public void UseOld() { for (int i = 0; i < Same.Count; i++) Same[i].Config.Password = Before[i]; }
    }

    private static readonly TimeSpan LoginWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ApplyPause = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SettlePause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleFor = TimeSpan.FromMinutes(15);
    private readonly string _configPath;
    private readonly List<Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Task? _settling; // an unconfirmed change still being followed up

    public CameraLogins(string configPath) => _configPath = configPath;

    public void Add(CameraConfig config, IReadOnlyList<CameraService> services, CameraControl control,
        ReolinkHttpApi? http, OnvifClient? onvif) => _entries.Add(new(config, services, control, http, onvif));

    /// <summary>Why a password cannot be sent to a camera, or null. Reolink takes 6 to 31 characters.</summary>
    public static string? PasswordError(string? password) =>
        password is not { Length: >= 6 and <= 31 } ? "the password must be 6 to 31 characters long"
        : password.Any(ch => ch is < '!' or > '~') ? "the password may only use letters, digits and symbols, without spaces"
        : null;

    /// <summary>Changes the login password on the camera behind <paramref name="name"/> and saves it once the
    /// camera has accepted a login with it. A refused password costs one session one failed login.</summary>
    public async Task<PasswordChange> ChangeAsync(string name, string password, CancellationToken ct)
    {
        static PasswordChange Fail(string why) => new(false, why, Array.Empty<string>());
        if (_entries.FirstOrDefault(e => string.Equals(e.Config.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } entry)
            return Fail("only a running Reolink camera can be given a new password (restart Neolink after adding one)");
        if (PasswordError(password) is { } rule) return Fail(rule);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_settling is { IsCompleted: false })
                return Fail("an earlier password change is still being confirmed; the log says when it has ended");
            if (password == entry.Config.Password) return Fail("that is already this camera's password");

            // Every entry on the same device that signs in as the same account (a second lens) changes with it.
            var same = _entries.Where(e => e.Config.SameDevice(entry.Config)
                                           && e.Config.Username == entry.Config.Username).ToList();
            // The account list belongs to the device: it is written over the first lens's session when that is up.
            var writer = same.FirstOrDefault(e => e.Config.ChannelId == 0 && e.Control.Online) ?? entry;
            if (!writer.Control.Online)
                return Fail("the camera is not connected: it has to be online to take a new password");
            var change = new Change(same, same.Select(e => e.Config.Password).ToList(), password,
                same.Select(e => e.Config.Name).ToList());
            // Everything the save needs is checked first: the write to the camera cannot be taken back.
            if (SaveProblem(change.Names) is { } unsavable)
                return Fail($"config.json could not take the new password ({unsavable}), so the camera was left alone");
            // One session answers for the camera, so a refused password is one failed login, not one per stream.
            var probe = writer.Services.FirstOrDefault(s => s.LiveCamera != null)
                        ?? same.SelectMany(e => e.Services).FirstOrDefault(s => s.LiveCamera != null)
                        ?? writer.Services[0];

            try
            {
                // The entries switch just before the write: a session the camera drops as it applies the
                // change comes back with the new password.
                await writer.Control.SetAccountPasswordAsync(entry.Config.Username, password, change.UseNew, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                change.UseOld(); // shutting down: leave the entries as config.json has them
                throw;
            }
            catch (Exception ex) when (ex is CameraCommandException or BcProtocolException or CameraOfflineException)
            {
                change.UseOld();
                return Fail(ex is CameraCommandException refused
                    ? $"the camera refused the change (code {refused.ResponseCode})"
                    : ex.Message);
            }
            catch (Exception ex)
            {
                // The write may have reached the camera (it can close the session as it applies it): the login below tells.
                Log.Debug($"{entry.Config.Name}: the session failed during the password change ({ex.Message})");
            }
            Log.Warn($"{entry.Config.Name}: a new login password was sent to the camera; it is saved once a login with it succeeds");

            bool? accepted = await ProbeAsync(probe, ct).ConfigureAwait(false);
            if (accepted == false)
            {
                // A camera may acknowledge the change a moment before it applies it: ask once more.
                await Task.Delay(ApplyPause, ct).ConfigureAwait(false);
                accepted = await ProbeAsync(probe, ct).ConfigureAwait(false);
            }
            if (accepted == true) return Accept(change, probe);
            if (accepted == false)
            {
                change.UseOld();
                if (await ProbeAsync(probe, ct).ConfigureAwait(false) == true)
                    return Fail("the camera did not take the new password; Neolink is still signed in with the old one");
            }
            // Neither password has been accepted yet: keep asking, slowly, away from this request.
            _settling = Task.Run(() => SettleAsync(change, probe, ct), CancellationToken.None);
            return Fail("the change is not confirmed yet: the camera has not accepted a login with either password. " +
                        "Neolink keeps trying both and saves the new one if the camera takes it; the log says how it ends");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The camera took the new password: move the HTTP and ONVIF clients and the other sessions to it, then save it.</summary>
    private PasswordChange Accept(Change change, CameraService probe)
    {
        change.UseNew();
        foreach (var e in change.Same)
        {
            e.Http?.SetPassword(change.Password);
            e.Onvif?.SetPassword(change.Password);
            e.Control.HttpLoginChanged();
            foreach (var s in e.Services)
                if (s != probe) _ = s.ReloginAsync();
        }
        return Save(change.Names, change.Password) is { } problem
            ? new(false, "the camera and the running server use the new password, but config.json was not updated " +
                         $"({problem}). Put the new password in config.json before the next restart", change.Names)
            : new(true, "the camera accepted the new password", change.Names);
    }

    /// <summary>Asks the camera with the new and the old password in turn until it accepts one, and keeps that one.</summary>
    private async Task SettleAsync(Change change, CameraService probe, CancellationToken ct)
    {
        string name = change.Names[0];
        try
        {
            var until = DateTime.UtcNow + SettleFor;
            for (bool tryNew = true; DateTime.UtcNow < until; tryNew = !tryNew)
            {
                if (tryNew) change.UseNew(); else change.UseOld();
                if (await ProbeAsync(probe, ct).ConfigureAwait(false) == true)
                {
                    if (!tryNew)
                        Log.Warn($"{name}: the camera still takes its old login password; the change did not happen");
                    else if (Accept(change, probe) is { Ok: false } unsaved)
                        Log.Error($"{name}: {unsaved.Message}");
                    else
                        Log.Warn($"{name}: the camera accepted its new login password; config.json updated " +
                                 $"(entries: {string.Join(", ", change.Names)})");
                    return;
                }
                await Task.Delay(SettlePause, ct).ConfigureAwait(false);
            }
            change.UseOld();
            Log.Error($"{name}: after {SettleFor.TotalMinutes:0} min the camera has accepted neither its old nor its new " +
                      "login password. Neolink is back on the one in config.json; if logins keep failing, enter the new " +
                      "password in this camera's settings");
        }
        catch (OperationCanceledException)
        {
            change.UseOld(); // shutting down
        }
        catch (Exception ex)
        {
            change.UseOld();
            Log.Error($"{name}: confirming the password change failed: {ex.Message}");
        }
    }

    /// <summary>Logs one session in again with its entry's current password: true when the camera accepts it,
    /// false when it refuses, null when no login finished in time.</summary>
    private static async Task<bool?> ProbeAsync(CameraService probe, CancellationToken ct)
    {
        probe.HoldAwakeFor(LoginWait); // a battery camera must not doze off instead of logging in again
        var login = probe.ReloginAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(login, Task.Delay(LoginWait, stop.Token)).ConfigureAwait(false);
        stop.Cancel();
        ct.ThrowIfCancellationRequested();
        return done == login && login.IsCompletedSuccessfully ? login.Result : null;
    }

    /// <summary>The file's entries for these cameras, plus any other entry on the same camera and account:
    /// a lens added since the last restart is not running yet but signs in the same way.</summary>
    private static List<JsonObject> FileEntries(JsonObject root, IReadOnlyList<string> names, List<string> missing)
    {
        var cams = ConfigEditor.Cameras(root).OfType<JsonObject>().ToList();
        var named = new List<JsonObject>();
        foreach (var name in names)
        {
            if (ConfigEditor.FindCamera(ConfigEditor.Cameras(root), name) is { } cam) named.Add(cam);
            else missing.Add(name);
        }
        static string Field(JsonObject cam, string key) => (ConfigEditor.GetString(cam, key) ?? "").Trim();
        static bool Same(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return cams.Where(c => named.Contains(c) || named.Any(n =>
            Field(n, "username") == Field(c, "username")
            && (Same(Field(n, "address"), Field(c, "address"))
                || (Field(n, "address").Length == 0 && Same(Field(n, "uid"), Field(c, "uid")))))).ToList();
    }

    /// <summary>Why a new password could not be saved for these entries right now, or null.</summary>
    private string? SaveProblem(IReadOnlyList<string> names)
    {
        if (ConfigEditor.WriteProblem(_configPath) is { } problem) return problem;
        try
        {
            var missing = new List<string>();
            FileEntries(ConfigEditor.Read(_configPath), names, missing);
            return missing.Count == 0 ? null
                : $"it has no camera named {string.Join(", ", missing)}: restart Neolink first if one was renamed";
        }
        catch (Exception ex)
        {
            return $"it cannot be edited: {ex.Message}";
        }
    }

    /// <summary>Writes the password into config.json for these entries; why it could not, or null.</summary>
    private string? Save(IReadOnlyList<string> names, string password)
    {
        try
        {
            var missing = new List<string>();
            ConfigEditor.Apply(_configPath, root =>
            {
                foreach (var cam in FileEntries(root, names, missing)) ConfigEditor.Set(cam, "password", password);
            });
            return missing.Count == 0 ? null : $"it has no camera named {string.Join(", ", missing)}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
