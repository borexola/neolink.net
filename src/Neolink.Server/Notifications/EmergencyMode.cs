// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using Neolink.Protocol;
using Neolink.Streaming;

namespace Neolink.Notifications;

/// <summary>One camera's emergency overrides; null follows the all-cameras value.</summary>
public sealed class EmergencyCameraOptions
{
    public bool? Email { get; set; }
    public bool? Webhook { get; set; }
    public bool? Siren { get; set; }
    public bool? Lights { get; set; }

    public EmergencyCameraOptions Clone() =>
        new() { Email = Email, Webhook = Webhook, Siren = Siren, Lights = Lights };
}

/// <summary>A camera whose siren or light could not be set, and why.</summary>
public sealed record EmergencyIssue(string Camera, string Reason);

/// <summary>What emergency mode can reach on one camera. The deterrents ride on
/// <see cref="Control"/>; the rest are the runtime states it forces so nothing is
/// off, dark or dozing during an emergency. A null delegate means the camera has
/// no such state to override.</summary>
public sealed record EmergencyCamera(string Name, ICameraControl Control)
{
    /// <summary>Is the user holding this camera suspended (no connection)?</summary>
    public Func<bool>? Suspended { get; init; }
    /// <summary>Suspends/resumes at runtime; persisted.</summary>
    public Action<bool>? SetSuspended { get; init; }
    /// <summary>Privacy mode as last pushed — a "dark" camera showing nothing.</summary>
    public Func<bool?>? PrivacyOn { get; init; }
    /// <summary>Stops a battery camera dozing (and lets it doze again).</summary>
    public Action<bool>? SetHoldAwake { get; init; }
}

/// <summary>
/// Emergency mode (beta). While armed, detections notify through whichever
/// channels are configured regardless of the per-camera opt-ins and without the
/// cooldown, and the chosen cameras hold their siren and light on. Nothing here
/// writes to a camera's own settings: the overlay applies only while
/// <see cref="Enabled"/>, so disarming hands control straight back to them.
/// </summary>
public sealed class EmergencySettings
{
    public bool Enabled { get; set; }
    public bool Email { get; set; } = true;
    public bool Webhook { get; set; } = true;
    public bool Siren { get; set; }
    public bool Lights { get; set; }
    public Dictionary<string, EmergencyCameraOptions> Cameras { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When it was armed; null while off.</summary>
    public DateTime? ArmedUtc { get; set; }

    private EmergencyCameraOptions? Ov(string camera) =>
        Cameras.TryGetValue(camera, out var o) ? o : null;

    public bool EmailFor(string camera) => Ov(camera)?.Email ?? Email;
    public bool WebhookFor(string camera) => Ov(camera)?.Webhook ?? Webhook;
    public bool SirenFor(string camera) => Ov(camera)?.Siren ?? Siren;
    public bool LightsFor(string camera) => Ov(camera)?.Lights ?? Lights;

    public EmergencySettings Clone() => new()
    {
        Enabled = Enabled,
        Email = Email,
        Webhook = Webhook,
        Siren = Siren,
        Lights = Lights,
        ArmedUtc = ArmedUtc,
        Cameras = Cameras.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(),
            StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>emergency.json next to the other state files; unreadable = disarmed.
/// A sibling emergency-runtime.json records what is physically switched on, so
/// a restart cannot forget a siren it latched.</summary>
public sealed class EmergencyStore
{
    /// <summary>What this feature has switched on and has still to reach.</summary>
    public sealed class Runtime
    {
        public List<string> Latched { get; set; } = new();
        public Dictionary<string, string> PriorLight { get; set; } = new();
        public List<string> PendingSiren { get; set; } = new();
        public List<string> PendingLight { get; set; } = new();
        /// <summary>Cameras emergency mode resumed, held awake, or took out of
        /// privacy mode — the states to hand back when it is switched off.</summary>
        public List<string> Resumed { get; set; } = new();
        public List<string> HeldAwake { get; set; } = new();
        public List<string> Unhidden { get; set; } = new();
        public List<string> PendingPrivacy { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _file;
    private readonly string _runtimeFile;
    private readonly object _gate = new();
    private EmergencySettings _settings = new();

    public EmergencyStore(string stateDir)
    {
        _file = Path.Combine(stateDir, "emergency.json");
        _runtimeFile = Path.Combine(stateDir, "emergency-runtime.json");
        try
        {
            if (File.Exists(_file))
                _settings = JsonSerializer.Deserialize<EmergencySettings>(
                    File.ReadAllText(_file), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            // Fail DISARMED: a half-read file must never leave sirens latched on.
            _settings = new();
            Log.Warn($"Emergency mode settings unreadable ({ex.Message}); starting disarmed.");
        }
    }

    public EmergencySettings Snapshot()
    {
        lock (_gate) return _settings.Clone();
    }

    public void Save(EmergencySettings incoming)
    {
        lock (_gate)
        {
            // Cloned in: the caller keeps its own instance and must not be able to
            // mutate what is now the stored truth.
            _settings = incoming.Clone();
            WriteAtomic(_file, _settings, "Emergency mode settings");
        }
    }

    public Runtime LoadRuntime()
    {
        try
        {
            if (File.Exists(_runtimeFile))
                return JsonSerializer.Deserialize<Runtime>(File.ReadAllText(_runtimeFile), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Emergency mode runtime state unreadable ({ex.Message}); assuming nothing is latched.");
        }
        return new();
    }

    public void SaveRuntime(Runtime rt)
    {
        lock (_gate) WriteAtomic(_runtimeFile, rt, "Emergency mode runtime state");
    }

    private static void WriteAtomic<T>(string file, T value, string what)
    {
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} could not be saved: {ex.Message}");
        }
    }
}

/// <summary>
/// Applies emergency mode to the cameras. Siren and light are LATCHED while
/// armed, so disarming must always run: every camera call is best-effort and
/// logged, never thrown, or one unreachable camera would strand the rest.
/// The store write and the camera pass share one lock — split, two racing
/// requests can leave a siren sounding with the stored state disarmed.
/// A camera that answered and REFUSED is done with; anything else (offline,
/// dropped link, no acknowledgement) may or may not have happened, so the
/// state is kept and the sweep retries until the camera answers.
/// </summary>
public sealed class EmergencyMode
{
    private readonly EmergencyStore _store;
    private readonly Func<IReadOnlyList<EmergencyCamera>> _cameras;
    // Runtime states this feature forced, so switching it off hands each camera
    // back exactly what it had: suspended, dozing, in privacy mode.
    private readonly HashSet<string> _resumed;
    private readonly HashSet<string> _heldAwake;
    private readonly HashSet<string> _unhidden;
    private readonly HashSet<string> _pendingPrivacy;
    // What this feature switched on, so disarming touches nothing else: a camera
    // with no siren is never told to stop one, a light the user lit is left lit.
    private readonly HashSet<string> _latched;
    private readonly Dictionary<string, string> _priorLight;
    // Latches ASSUMED after a restart rather than observed: checked before they
    // are acted on, so a camera that never had a siren is not told to stop one.
    private readonly HashSet<string> _assumed = new(StringComparer.OrdinalIgnoreCase);
    // Commands that did not get through, one set per command: the siren and the
    // light on one camera fail and succeed independently.
    private readonly HashSet<string> _pendingSiren;
    private readonly HashSet<string> _pendingLight;
    private readonly Dictionary<string, string> _issues = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _state = new();
    private readonly SemaphoreSlim _apply = new(1, 1);
    private bool _firstPass = true;

    public EmergencyMode(EmergencyStore store, Func<IReadOnlyList<EmergencyCamera>> cameras)
    {
        _store = store;
        _cameras = cameras;
        var rt = store.LoadRuntime();
        _latched = new(rt.Latched, StringComparer.OrdinalIgnoreCase);
        _priorLight = new(rt.PriorLight, StringComparer.OrdinalIgnoreCase);
        _pendingSiren = new(rt.PendingSiren, StringComparer.OrdinalIgnoreCase);
        _pendingLight = new(rt.PendingLight, StringComparer.OrdinalIgnoreCase);
        _resumed = new(rt.Resumed, StringComparer.OrdinalIgnoreCase);
        _heldAwake = new(rt.HeldAwake, StringComparer.OrdinalIgnoreCase);
        _unhidden = new(rt.Unhidden, StringComparer.OrdinalIgnoreCase);
        _pendingPrivacy = new(rt.PendingPrivacy, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised after a change is applied, so the HA bridge can republish.</summary>
    public event Func<Task>? Changed;

    public EmergencySettings Snapshot() => _store.Snapshot();

    /// <summary>Cameras the last pass could not set, one line each — an armed
    /// siren that never sounded is worth saying out loud, not just logging.</summary>
    public IReadOnlyList<EmergencyIssue> Issues
    {
        get
        {
            lock (_state) return _issues.Select(kv => new EmergencyIssue(kv.Key, kv.Value)).ToList();
        }
    }

    public Task<EmergencySettings> ApplyAsync(EmergencySettings next, CancellationToken ct) =>
        ApplyAsync(_ => next, ct);

    /// <summary>Snapshots, mutates, saves and applies under the one lock, so two
    /// callers (the web UI and the HA switch) cannot overwrite each other.</summary>
    public async Task<EmergencySettings> ApplyAsync(Func<EmergencySettings, EmergencySettings> mutate,
        CancellationToken ct)
    {
        EmergencySettings saved;
        await _apply.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prev = _store.Snapshot();
            SeedIfRestartedLocked(prev);
            var next = mutate(prev.Clone());
            next.ArmedUtc = next.Enabled
                ? prev.Enabled ? prev.ArmedUtc ?? DateTime.UtcNow : DateTime.UtcNow
                : null;
            _store.Save(next);
            saved = _store.Snapshot();
            if (saved.Enabled != prev.Enabled)
                Log.Warn($"Emergency mode {(saved.Enabled ? "ARMED" : "disarmed")}");
            await PassLockedAsync(prev, saved, ct).ConfigureAwait(false);
        }
        finally { _apply.Release(); }
        // Outside the lock: a handler must never be able to re-enter and deadlock.
        await RaiseChangedAsync().ConfigureAwait(false);
        return saved;
    }

    /// <summary>Turns emergency mode on/off, keeping the stored options.</summary>
    public Task<EmergencySettings> SetEnabledAsync(bool on, CancellationToken ct) =>
        ApplyAsync(cur => { cur.Enabled = on; return cur; }, ct);

    /// <summary>After a restart: re-latches sirens and lights if it came back
    /// armed, or switches off whatever the previous run left on if it did not.</summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        await _apply.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the lock: the user may have disarmed while we waited.
            var cur = _store.Snapshot();
            SeedIfRestartedLocked(cur);
            if (cur.Enabled)
            {
                Log.Warn("Emergency mode is still ARMED from before the restart — re-applying sirens and lights");
                await PassLockedAsync(new EmergencySettings(), cur, ct).ConfigureAwait(false);
                return;
            }
            bool leftovers;
            lock (_state)
            {
                // Every kind of state the previous run may have forced, not just
                // the loud ones: a lifted privacy mode or a resumed camera is
                // just as much a change the user is owed back.
                leftovers = _latched.Count > 0 || _priorLight.Count > 0 || _unhidden.Count > 0
                            || _resumed.Count > 0 || _heldAwake.Count > 0;
                _pendingSiren.UnionWith(_latched);
                _pendingLight.UnionWith(_priorLight.Keys);
                _pendingPrivacy.UnionWith(_unhidden);
            }
            if (!leftovers) return;
            Log.Warn("Emergency mode: switching off what the previous run left on");
            await PassLockedAsync(cur, cur, ct).ConfigureAwait(false);
        }
        finally { _apply.Release(); }
    }

    /// <summary>Re-applies to cameras a previous pass could not reach; cheap no-op
    /// when everything stuck.</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        // Anything unreached, or anything still owed back — a privacy mode to
        // restore counts as much as a siren to silence.
        lock (_state)
            if (_pendingSiren.Count == 0 && _pendingLight.Count == 0 && _pendingPrivacy.Count == 0
                && _resumed.Count == 0 && _heldAwake.Count == 0 && _unhidden.Count == 0)
                return;
        await _apply.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // prev == next, so only the pending cameras are touched.
            var cur = _store.Snapshot();
            SeedIfRestartedLocked(cur);
            await PassLockedAsync(cur, cur, ct).ConfigureAwait(false);
        }
        finally { _apply.Release(); }
    }

    /// <summary>First pass of this process with the store ARMED: the cameras were
    /// forced before the restart. Whatever the runtime file did not record is
    /// assumed on — a siren wrongly assumed sounding costs one harmless off; one
    /// wrongly assumed silent could sound for good — and its light restores to
    /// off, the earlier state being unknowable. Must run before any camera is
    /// read, or the forced-on light would be captured as the state to go back to.</summary>
    private void SeedIfRestartedLocked(EmergencySettings cur)
    {
        if (!_firstPass) return;
        _firstPass = false;
        if (!cur.Enabled) return;
        lock (_state)
            foreach (var (name, _) in _cameras())
            {
                if (cur.SirenFor(name) && _latched.Add(name)) _assumed.Add(name);
                if (cur.LightsFor(name)) _priorLight.TryAdd(name, "close");
            }
    }

    private async Task RaiseChangedAsync()
    {
        if (Changed is not { } handler) return;
        try { await handler().ConfigureAwait(false); }
        catch (Exception ex) { Log.Debug($"Emergency mode: change notification failed: {ex.Message}"); }
    }

    /// <summary>The camera pass. Caller must hold <see cref="_apply"/>.</summary>
    private async Task PassLockedAsync(EmergencySettings prev, EmergencySettings next, CancellationToken ct)
    {
        // A refused command is not retried, so its issue must outlive later
        // passes that never touch that camera again.
        if (prev.Enabled != next.Enabled) lock (_state) _issues.Clear();
        foreach (var cam in _cameras())
        {
            var name = cam.Name;
            var control = cam.Control;
            bool retrySiren, retryLight, retryPrivacy;
            lock (_state)
            {
                retrySiren = _pendingSiren.Contains(name);
                retryLight = _pendingLight.Contains(name);
                retryPrivacy = _pendingPrivacy.Contains(name);
            }
            bool owed;
            lock (_state) owed = _resumed.Contains(name) || _heldAwake.Contains(name)
                                 || _unhidden.Contains(name);
            // Nothing may be switched off, dark or dozing during an emergency.
            // Re-asserted on every armed pass, so a camera the user suspends or
            // darkens WHILE armed is brought back; each step no-ops when the
            // camera is already where it needs to be.
            if (next.Enabled)
                await LiveAsync(cam, true, ct).ConfigureAwait(false);
            bool siren = next.Enabled && next.SirenFor(name);
            bool light = next.Enabled && next.LightsFor(name);
            bool doSiren = retrySiren || siren != (prev.Enabled && prev.SirenFor(name));
            bool doLight = retryLight || light != (prev.Enabled && prev.LightsFor(name));
            if (doSiren || doLight) lock (_state) _issues.Remove(name);
            if (doSiren) await SirenAsync(name, control, siren, ct).ConfigureAwait(false);
            if (doLight) await LightAsync(name, control, light, ct).ConfigureAwait(false);
            // Handing the camera back comes LAST: re-suspending drops the session,
            // and doing that before the siren was silenced would leave it sounding
            // with no way left to reach it.
            if (!next.Enabled && (owed || retryPrivacy || prev.Enabled != next.Enabled))
                await LiveAsync(cam, false, ct).ConfigureAwait(false);
            // Switched off with nothing of ours left on the camera: nothing to
            // retry and nothing worth reporting. Only ever while disarmed — while
            // armed, an unreached camera is outstanding work, not noise.
            if (!next.Enabled)
                lock (_state)
                    if (!_latched.Contains(name) && !_priorLight.ContainsKey(name)
                        && !_unhidden.Contains(name) && !_resumed.Contains(name)
                        && !_heldAwake.Contains(name))
                    {
                        _pendingSiren.Remove(name);
                        _pendingLight.Remove(name);
                        _pendingPrivacy.Remove(name);
                        _issues.Remove(name);
                    }
        }
        EmergencyStore.Runtime rt;
        lock (_state)
            rt = new()
            {
                Latched = _latched.ToList(),
                PriorLight = new(_priorLight),
                PendingSiren = _pendingSiren.ToList(),
                PendingLight = _pendingLight.ToList(),
                Resumed = _resumed.ToList(),
                HeldAwake = _heldAwake.ToList(),
                Unhidden = _unhidden.ToList(),
                PendingPrivacy = _pendingPrivacy.ToList(),
            };
        _store.SaveRuntime(rt);
    }

    /// <summary>Brings a camera fully live for the emergency, or hands back exactly
    /// what was changed. A camera the user suspended reconnects, a dozing battery
    /// camera stops dozing, and one sitting in privacy mode starts seeing again —
    /// each recorded, so nothing that was already live is "restored" into a state
    /// the user never chose.</summary>
    private async Task LiveAsync(EmergencyCamera cam, bool arming, CancellationToken ct)
    {
        var name = cam.Name;
        if (arming)
        {
            if (cam.SetSuspended is { } suspend && cam.Suspended?.Invoke() == true)
            {
                lock (_state) _resumed.Add(name);
                try
                {
                    suspend(false);
                    Log.Info($"Emergency mode: {name} resumed (was suspended)");
                }
                catch (Exception ex) { Log.Warn($"Emergency mode: {name} resume failed: {Log.Flatten(ex)}"); }
            }
            if (cam.SetHoldAwake is { } hold)
            {
                lock (_state) _heldAwake.Add(name);
                try
                {
                    hold(true);
                    Log.Info($"Emergency mode: {name} held awake (no dozing until this is switched off)");
                }
                catch (Exception ex) { Log.Warn($"Emergency mode: {name} hold-awake failed: {Log.Flatten(ex)}"); }
            }
            await PrivacyAsync(cam, on: false, ct).ConfigureAwait(false);
            return;
        }
        await PrivacyAsync(cam, on: true, ct).ConfigureAwait(false);
        bool unfinished;
        lock (_state) unfinished = _pendingSiren.Contains(name) || _pendingLight.Contains(name)
                                   || _pendingPrivacy.Contains(name);
        // Something is still switched on and the camera is out of reach: letting
        // it doze or suspending it would cut the only route back, so it stays
        // live (and held awake) until the sweep has finished with it.
        if (unfinished) return;
        bool wasResumed, wasHeld;
        lock (_state)
        {
            wasResumed = _resumed.Remove(name);
            wasHeld = _heldAwake.Remove(name);
        }
        if (wasHeld && cam.SetHoldAwake is { } release)
        {
            try { release(false); }
            catch (Exception ex) { Log.Warn($"Emergency mode: {name} sleep policy restore failed: {Log.Flatten(ex)}"); }
        }
        // Suspend last: it drops the session everything else still needs.
        if (wasResumed && cam.SetSuspended is { } resuspend)
        {
            try
            {
                resuspend(true);
                Log.Info($"Emergency mode: {name} suspended again");
            }
            catch (Exception ex) { Log.Warn($"Emergency mode: {name} suspend restore failed: {Log.Flatten(ex)}"); }
        }
    }

    /// <summary>Takes a camera out of privacy mode for the emergency, or puts it
    /// back. Only ever restores privacy this feature itself lifted.</summary>
    private async Task PrivacyAsync(EmergencyCamera cam, bool on, CancellationToken ct)
    {
        var name = cam.Name;
        if (on)
        {
            bool ours;
            lock (_state)
            {
                ours = _unhidden.Contains(name);
                if (!ours) _pendingPrivacy.Remove(name);
            }
            if (!ours) return;
        }
        else
        {
            // null is "the camera has not told us yet", which is exactly the
            // state a just-woken camera is in — treating that as "not dark" is
            // how a dark camera stays dark through the whole emergency. Keep it
            // queued and let the sweep look again once a push has arrived.
            var dark = cam.PrivacyOn?.Invoke();
            if (cam.PrivacyOn == null || dark == false)
            {
                lock (_state) _pendingPrivacy.Remove(name);
                return;
            }
            if (dark == null)
            {
                lock (_state) _pendingPrivacy.Add(name);
                return;
            }
        }
        try
        {
            await cam.Control.SetPrivacyModeAsync(on, ct).ConfigureAwait(false);
            lock (_state)
            {
                if (on) _unhidden.Remove(name); else _unhidden.Add(name);
                _pendingPrivacy.Remove(name);
                // The siren and light clear their own line before they run; this
                // path can run alone, so it clears its own — unless one of them
                // is still outstanding and the line belongs to that instead.
                if (!_pendingSiren.Contains(name) && !_pendingLight.Contains(name))
                    _issues.Remove(name);
            }
            Log.Info($"Emergency mode: {name} privacy mode {(on ? "restored" : "off — the camera can see again")}");
        }
        catch (Exception ex)
        {
            Failed(name, ex, on ? "privacy restore" : "privacy off", _pendingPrivacy);
            // Refused outright: it has no privacy mode to put back, and no amount
            // of retrying will change that.
            if (Refused(ex))
                lock (_state) { _unhidden.Remove(name); _pendingPrivacy.Remove(name); }
        }
    }

    /// <summary>The camera answered and said no. Everything else is "unknown".</summary>
    /// <remarks>Deliberately narrow. ObjectDisposedException and its kin are
    /// transport failures wearing an InvalidOperationException coat: taking one
    /// for a refusal would retire a siren that is still sounding.</remarks>
    private static bool Refused(Exception ex) =>
        ex is CameraCommandException or NotSupportedException or ArgumentException;

    private void Failed(string camera, Exception ex, string what, HashSet<string> pending)
    {
        lock (_state)
        {
            if (!Refused(ex)) pending.Add(camera);
            _issues[camera] = ex is CameraOfflineException ? "offline" : $"{what}: {ex.Message}";
        }
        Log.Warn($"Emergency mode: {camera} {what} failed: {Log.Flatten(ex)}");
    }

    private async Task SirenAsync(string name, ICameraControl control, bool on, CancellationToken ct)
    {
        bool ours, assumed;
        lock (_state)
        {
            ours = _latched.Contains(name);
            assumed = _assumed.Contains(name);
        }
        // Never tell a camera to stop a siren this feature did not start: one
        // without a siren would refuse, and be nagged about it every minute.
        if (!on && !ours) return;
        bool sent = false;
        try
        {
            // Arming probes. Disarming does not — a camera whose capabilities can no
            // longer be read must still be reached — unless the latch was only
            // assumed after a restart, in which case a probe that answers settles it.
            if (on || assumed)
            {
                bool hasSiren;
                try { hasSiren = (await control.GetCapabilitiesAsync(ct).ConfigureAwait(false)).Features.Siren; }
                catch when (!on) { hasSiren = true; } // unknown: fall through to the off
                if (!hasSiren)
                {
                    lock (_state) { _latched.Remove(name); _assumed.Remove(name); _pendingSiren.Remove(name); }
                    return;
                }
            }
            sent = true;
            await control.SirenAsync(on, ct).ConfigureAwait(false);
            lock (_state)
            {
                if (on) _latched.Add(name); else _latched.Remove(name);
                _assumed.Remove(name);
                _pendingSiren.Remove(name);
            }
            Log.Info($"Emergency mode: {name} siren {(on ? "ON" : "off")}");
        }
        catch (Exception ex)
        {
            Failed(name, ex, on ? "siren on" : "siren off", _pendingSiren);
            bool refused = Refused(ex);
            lock (_state)
            {
                // Sent but unacknowledged is not refused: the command may have run,
                // so the siren counts as sounding — a stray off is harmless, a
                // missed one is not. A probe that failed sent nothing.
                if (on && sent && !refused) _latched.Add(name);
                if (!on && refused) { _latched.Remove(name); _assumed.Remove(name); }
            }
        }
    }

    private async Task LightAsync(string name, ICameraControl control, bool on, CancellationToken ct)
    {
        if (!on)
        {
            bool ours;
            lock (_state) ours = _priorLight.ContainsKey(name);
            // Restore ONLY what this feature turned on: a light the user lit
            // themselves must not be switched off by disarming.
            if (!ours) return;
        }
        try
        {
            var f = (await control.GetCapabilitiesAsync(ct).ConfigureAwait(false)).Features;
            if (!f.Floodlight && !f.Spotlight && !f.WhiteLed)
            {
                // Nothing to drive — including a restore seeded by a restart.
                lock (_state) { _priorLight.Remove(name); _pendingLight.Remove(name); }
                return;
            }
            if (on)
            {
                var prior = (string?)(await control.GetLedStateAsync(ct).ConfigureAwait(false))
                    ?.Element("lightState");
                // TryAdd: a retry after a partial arm must keep the ORIGINAL reading,
                // not the forced-on state it is looking at now.
                lock (_state) _priorLight.TryAdd(name, prior ?? "close");
                await control.SetLedStateAsync(null, "open", null, null, ct).ConfigureAwait(false);
            }
            else
            {
                string? restore;
                lock (_state) _priorLight.TryGetValue(name, out restore);
                await control.SetLedStateAsync(null, restore ?? "close", null, null, ct).ConfigureAwait(false);
                lock (_state) _priorLight.Remove(name);
            }
            lock (_state) _pendingLight.Remove(name);
            Log.Info($"Emergency mode: {name} light {(on ? "ON" : "restored")}");
        }
        catch (Exception ex)
        {
            Failed(name, ex, on ? "light on" : "light restore", _pendingLight);
            // Refused outright: nothing was lit, or there is nothing to restore.
            if (Refused(ex)) lock (_state) _priorLight.Remove(name);
        }
    }
}
