// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>
/// Detections from a non-Reolink camera, over ONVIF's event service.
///
/// A Reolink camera pushes its alarms down the Baichuan connection that is already
/// open. The standard has no equivalent: a client asks the camera to create a
/// "pull point" and then long-polls it, so this is a service of its own that holds
/// one subscription per camera and keeps it alive.
///
/// What comes out is <see cref="MotionPush"/> — the same shape a Baichuan push
/// produces — so everything downstream (the event recorder, notifications, the MQTT
/// bridge and Home Assistant) works on it without knowing which kind of camera it
/// came from. Ongoing detections are also re-emitted every few seconds, as a
/// Baichuan push would be, since ONVIF reports a state only when it changes.
/// </summary>
public sealed class OnvifEventService
{
    /// <summary>How long a subscription is asked to live, and how long before that
    /// it is renewed. Short enough that a camera reclaims it soon after Neolink
    /// stops, long enough that renewal is not most of the traffic. A refused Renew
    /// is not fatal: the polling itself keeps a conformant pull point alive.</summary>
    private static readonly TimeSpan Termination = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RenewAt = TimeSpan.FromSeconds(30);

    /// <summary>How long the camera is asked to hold each poll open. This is the
    /// latency floor for a detection only if the camera batches; a camera that
    /// answers the moment something happens delivers immediately.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);

    /// <summary>The floor between polls, for cameras that answer "nothing happened"
    /// immediately instead of holding the request. Without it those cameras would be
    /// polled as fast as the network allows, which is a busy loop at both ends. It
    /// is also the detection latency ceiling on such a camera, so it is kept short.</summary>
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a ONE-SHOT detection counts as current. A notification that
    /// carries no state ("this happened") will never be followed by an end, so it
    /// has to lapse on its own — each on its own clock, refreshed whenever the
    /// camera reports it again. Stateful detections are never timed out here: they
    /// last until the camera says they ended, exactly as it promised to.</summary>
    private static readonly TimeSpan OneShotHold = TimeSpan.FromSeconds(20);

    /// <summary>How often an ongoing detection is pushed again, as a Baichuan camera
    /// does. Well inside the bridge's 20s sensor drop and the recorder's clip cap.</summary>
    private static readonly TimeSpan Repush = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before asking a camera whose ONVIF answered with no
    /// event service at all. A lasting answer, re-checked occasionally only because
    /// someone may turn the service on in the camera's own settings.</summary>
    private static readonly TimeSpan NoServiceRecheck = TimeSpan.FromMinutes(5);

    private readonly string _camera;
    private readonly OnvifClient _onvif;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<string>?>>? _ownSources;
    private readonly object _gate = new();

    /// <summary>One active detection per speaker (<see cref="OnvifNotification.Key"/>), so
    /// one rule ending cannot end another's; one-shots lapse after <see cref="OneShotHold"/>.</summary>
    private readonly Dictionary<string, (IReadOnlyList<string> Labels, bool Stateful, DateTime LastReport)> _active =
        new(StringComparer.Ordinal);

    private bool _unknownLogged;
    private DateTime _lastEmit = DateTime.MinValue;
    private IReadOnlyCollection<string>? _sources;
    private bool _sourcesKnown;
    private DateTime _sourcesAskedAt = DateTime.MinValue;
    private CancellationTokenSource? _poll;

    /// <param name="ownSources">This camera's own video sources, for a device that sends every
    /// channel's events down one subscription. Null means "ask again"; empty means "no filter".</param>
    public OnvifEventService(string camera, OnvifClient onvif,
        Func<CancellationToken, Task<IReadOnlyCollection<string>?>>? ownSources = null)
    {
        _camera = camera;
        _onvif = onvif;
        _ownSources = ownSources;
    }

    /// <summary>Where a detection goes. Set by the wiring in Program, exactly as the
    /// Baichuan services' sink is.</summary>
    public Action<MotionPush>? MotionSink { get; set; }

    /// <summary>Whether this camera has EVER delivered a poll on an event subscription.
    /// Latched, so a reconnect does not make its Home Assistant sensors come and go.</summary>
    public bool EverSubscribed { get; private set; }

    /// <summary>Suspended cameras hold no connection to anything, this included.</summary>
    public bool Suspended { get; private set; }

    public void SetSuspended(bool suspended)
    {
        Suspended = suspended;
        // The poll in flight is cut short too, so a detection reported just after the
        // suspend cannot open a recording. Its token source may already be disposed.
        if (!suspended) return;
        try { _poll?.Cancel(); }
        catch (ObjectDisposedException) { /* the poll has already returned */ }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var repush = RepushLoopAsync(ct);
        var backoff = TimeSpan.FromSeconds(5);
        // Whether the last attempt ended badly, so a recovery is worth one Info line
        // and a routine re-subscribe (the camera dropped it, as they do) is not.
        bool troubled = false;
        bool refusalLogged = false;
        while (!ct.IsCancellationRequested)
        {
            if (Suspended)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            PullPointSubscription? subscription = null;
            bool proved = false;
            bool tidy = false; // leave the camera's subscription behind, or unsubscribe it
            try
            {
                var created = await _onvif.CreatePullPointAsync(Termination, ct).ConfigureAwait(false);
                if (created.NoEventService)
                {
                    // Not a failure: this camera's ONVIF simply has no event service
                    // (or ONVIF is not reachable at all, which its own log line has
                    // already explained). Nothing to poll, so ask again only rarely.
                    await Task.Delay(NoServiceRecheck, ct).ConfigureAwait(false);
                    continue;
                }
                subscription = created.Subscription;
                if (subscription == null)
                {
                    // The camera HAS an event service and refused this once. That is
                    // worth retrying on the ordinary backoff, not the long wait for a
                    // camera with no service — and worth saying why, once per outage.
                    if (!refusalLogged)
                    {
                        refusalLogged = true;
                        Log.Info($"{_camera}: the camera refused an ONVIF event subscription " +
                                 $"({created.Reason ?? "no reason given"}) — retrying");
                    }
                    troubled = true;
                }
                else
                {
                    (proved, tidy) = await PollAsync(subscription, troubled || !EverSubscribed, ct).ConfigureAwait(false);
                    if (proved) refusalLogged = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Debug($"{_camera}: ONVIF event subscription failed: {Log.Flatten(ex)}");
            }
            finally
            {
                // Anything the camera was reporting when the subscription died has
                // to be closed out, or the recorder keeps a clip open on a detection
                // nobody can end.
                ClearAll();
                // Unsubscribed only when leaving a subscription that still works: some
                // cameras (Tapo) answer Unsubscribe by dropping EVERY subscription they hold.
                if (subscription != null && (tidy || ct.IsCancellationRequested))
                    await _onvif.UnsubscribeAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested) break;
            // The backoff only resets once a subscription has PROVED itself by
            // delivering a poll. Resetting on the create alone let a camera that
            // accepts subscriptions and then refuses every poll be hammered every
            // five seconds forever.
            if (proved)
            {
                backoff = TimeSpan.FromSeconds(5);
                troubled = false;
            }
            else
            {
                troubled = true;
            }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (!proved) backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
        ClearAll();
        try { await repush.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    /// <summary>The poll loop for one subscription. Proved: it delivered at least one
    /// poll. Alive: it still worked when we left it (suspended), so unsubscribing is worth doing.</summary>
    private async Task<(bool Proved, bool Alive)> PollAsync(PullPointSubscription subscription, bool announce,
        CancellationToken ct)
    {
        var subscribedAt = DateTime.UtcNow;
        var renewDue = subscribedAt + RenewAt;
        bool delivered = false;
        // The poll is cancellable on its own, so a suspend cuts it short without
        // ending the service.
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _poll = poll;
        try
        {
            while (!ct.IsCancellationRequested && !Suspended)
            {
                var started = DateTime.UtcNow;
                var messages = await _onvif.PullMessagesAsync(subscription, Hold, poll.Token).ConfigureAwait(false);
                if (messages == null) return (delivered, false); // gone: re-subscribe
                if (!delivered)
                {
                    delivered = true;
                    bool first = !EverSubscribed;
                    EverSubscribed = true;
                    if (first || announce)
                        Log.Info($"{_camera}: ONVIF events subscribed — detections from this camera " +
                                 "record, notify and reach Home Assistant like a Reolink camera's");
                    else
                        Log.Debug($"{_camera}: ONVIF event subscription renewed after the camera dropped it");
                }
                await FilterSourcesAsync(ct).ConfigureAwait(false);
                foreach (var m in messages) Handle(m);
                ExpireOneShots();
                // A camera is ASKED to hold the poll open until it has something to say,
                // but not all of them do — some answer "nothing" at once, and polling
                // one of those as fast as it can reply is a busy loop against the
                // camera. When a poll comes back early and empty, pause before the next.
                if (messages.Count == 0 && DateTime.UtcNow - started < MinPollInterval)
                    await Task.Delay(MinPollInterval, poll.Token).ConfigureAwait(false);
                if (DateTime.UtcNow >= renewDue)
                {
                    // A refused Renew is not the end: the polling itself keeps a conformant
                    // pull point alive, and a lapsed subscription shows up on the next poll.
                    if (!await _onvif.RenewSubscriptionAsync(subscription, Termination, poll.Token).ConfigureAwait(false)
                        && !_renewRefusedLogged)
                    {
                        _renewRefusedLogged = true;
                        Log.Debug($"{_camera}: the camera refused to renew its ONVIF event subscription — " +
                                  "relying on the polls to keep it alive");
                    }
                    renewDue = DateTime.UtcNow + RenewAt;
                }
            }
            return (delivered, Suspended);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Suspended)
        {
            // The suspend cut the poll short; the subscription itself is fine.
            return (delivered, true);
        }
        finally
        {
            _poll = null;
        }
    }

    private bool _renewRefusedLogged;

    /// <summary>Learns which video sources are this camera's, once the control surface
    /// has bound the streams. Asked again while unknown; any answer is final.</summary>
    private async Task FilterSourcesAsync(CancellationToken ct)
    {
        if (_ownSources == null || _sourcesKnown) return;
        if (DateTime.UtcNow - _sourcesAskedAt < TimeSpan.FromSeconds(30)) return;
        _sourcesAskedAt = DateTime.UtcNow;
        try
        {
            var sources = await _ownSources(ct).ConfigureAwait(false);
            if (sources == null) return;
            _sources = sources.Count > 0 ? sources : null;
            _sourcesKnown = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"{_camera}: could not learn the camera's own video sources: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Re-emits the current detections every <see cref="Repush"/> while any
    /// is active, the way a Baichuan camera re-pushes ongoing motion.</summary>
    private async Task RepushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Repush, ct).ConfigureAwait(false);
            // One-shots lapse on their own clock, not the camera's next reply: a
            // poll can be held open for 20 seconds after the hold has passed.
            ExpireOneShots();
            bool any;
            lock (_gate) any = _active.Count > 0;
            if (any && DateTime.UtcNow - _lastEmit >= Repush) Emit();
        }
    }

    /// <summary>One notification, turned into a start or an end of a detection.</summary>
    internal void Handle(OnvifNotification n)
    {
        // Another channel's business: a device with several video sources sends
        // every one's events down the one subscription.
        if (_sources is { Count: > 0 } mine && n.SourceToken is { } source && !mine.Contains(source))
            return;
        if (n.Deleted)
        {
            // The property was withdrawn (a rule reconfigured or removed): whatever it reported is over.
            End(n.Key);
            return;
        }
        if (!n.IsDetection)
        {
            // Worth exactly one line: an unrecognised topic is how a vendor's own
            // detection rule stays invisible, and the topic is what someone would
            // need to report to have it added.
            if (!_unknownLogged && n.Items.Count > 0)
            {
                _unknownLogged = true;
                Log.Debug($"{_camera}: ONVIF topics that are not detections are ignored (first: {n.Topic})");
            }
            return;
        }
        if (n.Active == false) End(n.Key);
        else Start(n.Key, n.Labels, stateful: n.Active == true);
    }

    private void Start(string key, IReadOnlyList<string> labels, bool stateful)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_active.ContainsKey(key);
            // Once a speaker has been reported with state it stays stateful until it ends:
            // a one-shot for the same thing must not turn it into one that lapses by itself.
            var wasStateful = _active.TryGetValue(key, out var prior) && prior.Stateful;
            _active[key] = (labels, stateful || wasStateful, DateTime.UtcNow);
        }
        // Emitted even when nothing changed: a re-report is harmless to the recorder
        // (labels accumulate, nothing restarts) and is what re-opens an event that
        // had gone quiet and was sitting in its post-roll.
        Emit();
        if (changed)
            Log.Debug($"{_camera}: ONVIF detection started ({string.Join("+", labels)}{(stateful ? "" : ", one-shot")})");
    }

    private void End(string key)
    {
        IReadOnlyList<string> labels;
        lock (_gate)
        {
            if (!_active.Remove(key, out var was)) return;
            labels = was.Labels;
        }
        Log.Debug($"{_camera}: ONVIF detection ended ({string.Join("+", labels)})");
        Emit();
    }

    /// <summary>Lapses the one-shot detections whose own last report is older than
    /// <see cref="OneShotHold"/>. Stateful ones are left alone however quiet the
    /// camera is — ONVIF reports a state only when it CHANGES, so silence during a
    /// stateful detection means "still happening", not "over".</summary>
    internal void ExpireOneShots()
    {
        List<string> lapsed;
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow - OneShotHold;
            lapsed = _active.Where(kv => !kv.Value.Stateful && kv.Value.LastReport < cutoff)
                .SelectMany(kv => kv.Value.Labels).Distinct().ToList();
            foreach (var k in _active.Where(kv => !kv.Value.Stateful && kv.Value.LastReport < cutoff)
                         .Select(kv => kv.Key).ToList())
                _active.Remove(k);
        }
        if (lapsed.Count == 0) return;
        Log.Debug($"{_camera}: ONVIF one-shot detection lapsed ({string.Join("+", lapsed)})");
        Emit();
    }

    private void ClearAll()
    {
        lock (_gate)
        {
            if (_active.Count == 0) return;
            _active.Clear();
        }
        Emit();
    }

    /// <summary>The current set of detections, in the shape a Baichuan push has:
    /// "MD" with every active label, or "none" for the all-clear.
    ///
    /// Every label goes out, "motion" included. On a Reolink one push carries one
    /// classification, but ONVIF reports motion and a person through two separate
    /// rules; dropping "motion" whenever something else was also active left the
    /// recorder a set in which an event could be filtered out entirely (a camera
    /// set to record motion but not people would record nothing).</summary>
    private void Emit()
    {
        // Held across snapshot AND delivery: the poll thread and the re-push loop both
        // emit, and a stale "MD" landing after a "none" would re-arm an event nobody ends.
        lock (_emitGate)
        {
            string[] labels;
            lock (_gate)
                labels = _active.Values.SelectMany(v => v.Labels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _lastEmit = DateTime.UtcNow;
            MotionSink?.Invoke(labels.Length == 0
                ? new MotionPush("none", Array.Empty<string>())
                : new MotionPush("MD", labels));
        }
    }

    private readonly object _emitGate = new();

    /// <summary>The labels currently active, for tests.</summary>
    internal IReadOnlyCollection<string> ActiveLabels
    {
        get
        {
            lock (_gate)
                return _active.Values.SelectMany(v => v.Labels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Test seam: ages every active detection by <paramref name="by"/>, so
    /// the one-shot lapse can be exercised without waiting it out.</summary>
    internal void AgeForTest(TimeSpan by)
    {
        lock (_gate)
            foreach (var k in _active.Keys.ToList())
                _active[k] = (_active[k].Labels, _active[k].Stateful, _active[k].LastReport - by);
    }

    /// <summary>Test seam: restricts the detections to these video sources, as the
    /// control surface would once it has bound the streams.</summary>
    internal void UseSourcesForTest(IReadOnlyCollection<string> sources)
    {
        _sources = sources;
        _sourcesKnown = true;
    }
}
