// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>What kind of thing happened — the shell uses this to decide whether
/// quiet hours may swallow it.</summary>
internal enum AlertKind
{
    Detection,
    CameraOffline,
    ServerCondition,
}

/// <summary>One notification, decided but not yet shown.</summary>
/// <param name="Tag">Collapse key: a second alert with the same tag replaces the
/// first rather than stacking (matches the web UI's notification tags).</param>
/// <param name="DeepLink">App-relative page to open on click, or null.</param>
/// <param name="ThumbPath">API path of the event thumbnail, or null.</param>
internal sealed record Alert(
    AlertKind Kind, string Tag, string Title, string Body,
    string? DeepLink = null, string? ThumbPath = null);

/// <summary>
/// The alerting decision, with no clock, no network and no UI in it — every
/// input arrives as an argument, so the whole thing is exercised by the self
/// test. <see cref="AlertEngine"/> owns the timer and the toasts; this owns
/// what counts as news.
///
/// The rules match the web UI's Home page deliberately, down to the debounce
/// counts and the wording, because both read the same saved rules: the same
/// event must not alert differently depending on which client saw it.
/// </summary>
internal sealed class AlertRules
{
    /// <summary>Events older than this on arrival are catch-up, not news — a
    /// laptop waking from sleep must not fire an hour of backlog.</summary>
    public static readonly TimeSpan MaxEventAge = TimeSpan.FromMinutes(3);

    /// <summary>Consecutive polls a camera must look faulted before it alerts —
    /// ~20s at the default cadence, which rides out a reconnect blip.</summary>
    public const int OfflinePollsToAlert = 2;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _eventsSeeded;
    private readonly Dictionary<string, DateTime> _lastFired = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _offlineStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offlineFired = new(StringComparer.OrdinalIgnoreCase);
    private bool _camsSeeded;

    private bool _sysSeeded;
    private bool _prevStorageFull, _prevOverload, _prevWriteFailure;

    /// <summary>True once a first poll has established the baseline. Until then
    /// nothing fires — a fresh start is not a burst of alerts.</summary>
    public bool Seeded => _eventsSeeded;

    /// <summary>Forgets the baseline, so the next poll seeds again. Used when the
    /// connection drops for long enough that "new since last poll" stops meaning
    /// anything.</summary>
    public void Reset()
    {
        _seen.Clear();
        _eventsSeeded = false;
        _camsSeeded = false;
        _sysSeeded = false;
        _offlineStreak.Clear();
        _offlineFired.Clear();
        _lastFired.Clear();
    }

    /// <summary>
    /// Everything one poll should raise, in the order it happened. Advancing the
    /// state is this method's job whether or not the caller goes on to show the
    /// alerts — a suppressed alert (quiet hours) must not fire late.
    /// </summary>
    /// <param name="trace">Receives one line per decision about a NEW event —
    /// alerted or why not. Null in tests; the app feeds the diagnostic log.</param>
    public List<Alert> Evaluate(AlertPrefs prefs, IReadOnlyList<ApiEvent>? events,
        IReadOnlyList<ApiCamera>? cameras, ApiFeatures? features, DateTime utcNow,
        Action<string>? trace = null)
    {
        var outp = new List<Alert>();
        if (events != null) EvaluateEvents(prefs, events, utcNow, outp, trace);
        if (cameras != null) EvaluateCameras(prefs, cameras, outp);
        if (features != null) EvaluateFeatures(prefs, features, outp);
        return outp;
    }

    // ---- detections -------------------------------------------------------

    private void EvaluateEvents(AlertPrefs prefs, IReadOnlyList<ApiEvent> events,
        DateTime utcNow, List<Alert> outp, Action<string>? trace)
    {
        // The first fetch is history, not news.
        if (!_eventsSeeded)
        {
            foreach (var e in events) _seen.Add(e.Id);
            _eventsSeeded = true;
            trace?.Invoke($"events: baseline seeded with {events.Count} existing event(s) — those never alert");
            return;
        }

        // Oldest first, so a poll that catches several events notifies in order.
        foreach (var ev in events.OrderBy(e => e.Start))
        {
            if (!_seen.Add(ev.Id)) continue;
            if (!prefs.Enabled)
            {
                trace?.Invoke($"event {ev.Id} ({ev.Camera}): skipped — alerts are OFF in the account rules");
                continue;
            }
            if (utcNow - ev.Start > MaxEventAge)
            {
                trace?.Invoke($"event {ev.Id} ({ev.Camera}): skipped — {(utcNow - ev.Start).TotalMinutes:0.0} min old, catch-up not news");
                continue;
            }
            var hits = ev.AlertLabels
                .Where(l => prefs.WantsLabel(ev.Camera, l))
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();
            if (hits.Count == 0)
            {
                trace?.Invoke($"event {ev.Id} ({ev.Camera}): skipped — labels [{string.Join(", ", ev.AlertLabels)}] " +
                              "not selected for this camera in the alert rules");
                continue;
            }

            var key = $"{ev.Camera}|{string.Join(",", hits)}";
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, prefs.CooldownSeconds));
            if (_lastFired.TryGetValue(key, out var last) && utcNow - last < cooldown)
            {
                trace?.Invoke($"event {ev.Id} ({ev.Camera}): skipped — within the {cooldown.TotalSeconds:0}s cooldown for {key}");
                continue;
            }
            _lastFired[key] = utcNow;
            trace?.Invoke($"event {ev.Id} ({ev.Camera}): ALERT — {string.Join("+", hits)}");

            outp.Add(new Alert(AlertKind.Detection, ev.Id, ev.Title,
                $"{ev.Camera} · {ev.Start.ToLocalTime():HH:mm:ss}",
                DeepLink: $"/events?event={Uri.EscapeDataString(ev.Id)}",
                ThumbPath: ev.HasThumb ? $"/api/events/{Uri.EscapeDataString(ev.Id)}/thumb" : null));
        }

        // The seen-set only grows within a session; cap it against the current
        // window so a machine left running for a month does not hoard ids.
        if (_seen.Count > 2000)
        {
            var keep = events.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            _seen.RemoveWhere(id => !keep.Contains(id));
        }
    }

    // ---- cameras dropping off ---------------------------------------------

    private void EvaluateCameras(AlertPrefs prefs, IReadOnlyList<ApiCamera> cameras, List<Alert> outp)
    {
        foreach (var cam in cameras)
        {
            // Dozing battery cameras and suspended ones are off on purpose.
            bool faulted = !cam.Online && !cam.Asleep && !cam.Suspended;

            if (!_camsSeeded)
            {
                // Remember an already-down camera as known: only a NEW drop alerts,
                // but it can still say "back online" when it heals.
                if (faulted) _offlineFired.Add(cam.Name);
                continue;
            }

            if (!faulted)
            {
                _offlineStreak.Remove(cam.Name);
                if (_offlineFired.Remove(cam.Name) && prefs.Enabled && prefs.WantsOffline(cam.Name) && cam.Online)
                    outp.Add(new Alert(AlertKind.CameraOffline, $"offline-{cam.Name}",
                        $"{cam.Name} back online", "The camera has reconnected.", DeepLink: "/"));
                continue;
            }

            int n = (_offlineStreak.TryGetValue(cam.Name, out var c) ? c : 0) + 1;
            _offlineStreak[cam.Name] = n;
            if (n >= OfflinePollsToAlert && _offlineFired.Add(cam.Name)
                && prefs.Enabled && prefs.WantsOffline(cam.Name))
                outp.Add(new Alert(AlertKind.CameraOffline, $"offline-{cam.Name}",
                    $"{cam.Name} offline",
                    "The camera stopped responding. Check its power and network.", DeepLink: "/"));
        }
        _camsSeeded = true;
    }

    // ---- server conditions -------------------------------------------------

    private void EvaluateFeatures(AlertPrefs prefs, ApiFeatures f, List<Alert> outp)
    {
        bool storageFull = f.Storage?.Full ?? false;
        if (!_sysSeeded)
        {
            _prevStorageFull = storageFull;
            _prevOverload = f.Overload;
            _prevWriteFailure = f.WriteFailure;
            _sysSeeded = true;
            return;
        }

        if (prefs.Enabled)
        {
            if (prefs.SysStorage && storageFull != _prevStorageFull)
                outp.Add(new Alert(AlertKind.ServerCondition, "sys-storage",
                    storageFull ? "Storage full" : "Storage recovered",
                    storageFull ? "Recording has stopped — free space or adjust retention/archive."
                                : "Storage is no longer full.", DeepLink: "/"));
            if (prefs.SysOverload && f.Overload != _prevOverload)
                outp.Add(new Alert(AlertKind.ServerCondition, "sys-overload",
                    f.Overload ? "Server overloaded" : "Server load back to normal",
                    f.Overload ? "CPU has stayed near maximum for several minutes — streams or recording may lag."
                               : "CPU usage has returned to normal.", DeepLink: "/"));
            if (prefs.SysWriteFailure && f.WriteFailure != _prevWriteFailure)
                outp.Add(new Alert(AlertKind.ServerCondition, "sys-write",
                    f.WriteFailure ? "Recording write failures" : "Recording writes recovered",
                    f.WriteFailure ? "The server is failing to write footage to disk — check the drive."
                                   : "Footage is writing to disk again.", DeepLink: "/"));
        }

        _prevStorageFull = storageFull;
        _prevOverload = f.Overload;
        _prevWriteFailure = f.WriteFailure;
    }

    /// <summary>Whether quiet hours may swallow this one. Detections always can;
    /// faults only when the user said so, because a disk filling up at 3am is
    /// exactly the thing worth waking for.</summary>
    public static bool QuietCanSuppress(AlertKind kind, bool quietSilencesSystem) =>
        kind == AlertKind.Detection || quietSilencesSystem;
}
