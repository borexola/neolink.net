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
    /// <summary>How long a subscription is asked to live; it is renewed at 80% of what the
    /// camera grants. As Home Assistant does: renewing often upsets some cameras (Tapo).</summary>
    private static readonly TimeSpan Termination = TimeSpan.FromMinutes(10);

    /// <summary>Polls in a row that may fail on the transport (timeout, refused connection)
    /// before the subscription is given up for a new one.</summary>
    private const int MaxPollFailures = 5;

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
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<string>?>>? _otherChannels;
    private readonly object _gate = new();

    /// <summary>One active detection per speaker (<see cref="OnvifNotification.Key"/>), so
    /// one rule ending cannot end another's; one-shots lapse after <see cref="OneShotHold"/>.</summary>
    private readonly Dictionary<string, (IReadOnlyList<string> Labels, bool Stateful, DateTime LastReport, string? Source)>
        _active = new(StringComparer.Ordinal);

    /// <summary>How long a subscription may give nothing but hang-ups before it is asked to renew,
    /// and replaced if it will not: a camera that rebooted hangs up on one it no longer has.</summary>
    private static readonly TimeSpan HangUpProbe = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _unknownTopics = new(StringComparer.Ordinal);
    private DateTime _lastEmit = DateTime.MinValue;
    private IReadOnlyCollection<string>? _others;
    private bool _othersKnown;
    private DateTime _othersAskedAt = DateTime.MinValue;
    private CancellationTokenSource? _poll;

    /// <param name="otherChannels">The video source tokens of an NVR's OTHER channels, whose events share
    /// the subscription. Null means "ask again"; empty means "no filter".</param>
    public OnvifEventService(string camera, OnvifClient onvif,
        Func<CancellationToken, Task<IReadOnlyCollection<string>?>>? otherChannels = null)
    {
        _camera = camera;
        _onvif = onvif;
        _otherChannels = otherChannels;
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
                    (proved, tidy) = await PollAsync(subscription, created.Granted, troubled || !EverSubscribed, ct)
                        .ConfigureAwait(false);
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
    private async Task<(bool Proved, bool Alive)> PollAsync(PullPointSubscription subscription, TimeSpan granted,
        bool announce, CancellationToken ct)
    {
        var renewDue = DateTime.UtcNow + RenewAfter(granted);
        var answeredAt = DateTime.UtcNow; // the camera's last real reply on this subscription
        int failures = 0;
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
                var pulled = await _onvif.PullMessagesAsync(subscription, Hold, poll.Token).ConfigureAwait(false);
                if (pulled.Messages is not { } messages)
                {
                    // Gone by the camera's word: re-subscribe. A timeout or a refused connection is retried
                    // on this subscription instead — making new ones often is what upsets some cameras.
                    if (pulled.SubscriptionLost || ++failures > MaxPollFailures) return (delivered, false);
                    // What it reported stands: the subscription is intact, so its ends still come (ONVIF
                    // never repeats a state), and one that lapsed meanwhile faults the next poll.
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << failures)), poll.Token).ConfigureAwait(false);
                    continue;
                }
                failures = 0;
                if (!pulled.HungUp) answeredAt = DateTime.UtcNow;
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
                bool onlyHangUps = DateTime.UtcNow - answeredAt > HangUpProbe;
                if (onlyHangUps) renewDue = DateTime.UtcNow;
                if (DateTime.UtcNow >= renewDue)
                {
                    // A refused Renew is not the end: the polling itself keeps a conformant pull point
                    // alive, and a lapsed subscription faults the next poll. It is not asked again.
                    var (renewed, refused) = await _onvif.RenewSubscriptionAsync(subscription, Termination, poll.Token)
                        .ConfigureAwait(false);
                    if (renewed != null) answeredAt = DateTime.UtcNow;
                    else if (onlyHangUps) return (delivered, false); // nothing on it answers: a new one
                    if (refused && !_renewRefusedLogged)
                    {
                        _renewRefusedLogged = true;
                        Log.Debug($"{_camera}: the camera refused to renew its ONVIF event subscription — " +
                                  "relying on the polls to keep it alive");
                    }
                    renewDue = renewed is { } g ? DateTime.UtcNow + RenewAfter(g)
                        : refused ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMinutes(1);
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

    /// <summary>When to renew a subscription the camera granted for <paramref name="granted"/>.</summary>
    internal static TimeSpan RenewAfter(TimeSpan granted) =>
        TimeSpan.FromSeconds(Math.Max(5, granted.TotalSeconds * 0.8));

    /// <summary>Learns which video sources are the device's other channels, once the control
    /// surface has bound the streams. Asked again while unknown; any answer is final.</summary>
    private async Task FilterSourcesAsync(CancellationToken ct)
    {
        if (_otherChannels == null || _othersKnown) return;
        if (DateTime.UtcNow - _othersAskedAt < TimeSpan.FromSeconds(30)) return;
        _othersAskedAt = DateTime.UtcNow;
        try
        {
            var others = await _otherChannels(ct).ConfigureAwait(false);
            if (others == null) return;
            _others = others.Count > 0 ? others : null;
            _othersKnown = true;
            if (_others != null) EndOtherChannels(_others);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"{_camera}: could not learn the device's other channels: {Log.Flatten(ex)}");
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
            if (any && DateTime.UtcNow - _lastEmit >= Repush) Emit(repush: true);
        }
    }

    /// <summary>Ends what the device's other channels were reported doing before they were known to be other.</summary>
    private void EndOtherChannels(IReadOnlyCollection<string> others)
    {
        bool any;
        lock (_gate)
        {
            var gone = _active.Where(kv => kv.Value.Source is { } s && others.Contains(s)).Select(kv => kv.Key).ToList();
            foreach (var k in gone) _active.Remove(k);
            any = gone.Count > 0;
        }
        if (any) Emit();
    }

    /// <summary>One notification, turned into a start or an end of a detection.</summary>
    internal void Handle(OnvifNotification n)
    {
        // Only a start naming another channel is dropped: an end still closes what it started, and an
        // unknown value (Foscam: VideoSource=HUMAN_DETECTION_ALARM) is this camera's.
        bool other = _others is { Count: > 0 } others && n.SourceToken is { } source && others.Contains(source);
        if (n.Deleted)
        {
            // The property was withdrawn (a rule reconfigured or removed): whatever it reported is over.
            EndSpeaker(n.Speaker);
            return;
        }
        if (!n.IsDetection)
        {
            // One line per topic: an unrecognised topic is how a vendor's own detection
            // rule stays invisible, and it is what someone would report to have it added.
            bool first;
            lock (_gate) first = _unknownTopics.Count < 64 && _unknownTopics.Add(n.Topic);
            if (first && n.Items.Count > 0)
                Log.Debug($"{_camera}: ONVIF topic is not a detection, ignored: {n.Topic}");
            return;
        }
        // Initialized is the rule's current value, replayed on subscribing: a one-shot's is not
        // a new sighting, and a doorbell already ringing does not ring again.
        bool replay = string.Equals(n.Operation, "Initialized", StringComparison.OrdinalIgnoreCase);
        if (n.Active == null && replay) return;
        if (n.Active == false) End(n.Key);
        else if (!other) Start(n.Key, n.Labels, stateful: n.Active == true, n.SourceToken, ring: !replay);
    }

    private void Start(string key, IReadOnlyList<string> labels, bool stateful, string? source = null, bool ring = true)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_active.ContainsKey(key);
            // Once a speaker has been reported with state it stays stateful until it ends:
            // a one-shot for the same thing must not turn it into one that lapses by itself.
            var wasStateful = _active.TryGetValue(key, out var prior) && prior.Stateful;
            _active[key] = (labels, stateful || wasStateful, DateTime.UtcNow, source);
        }
        // Emitted even when nothing changed: a re-report is harmless to the recorder
        // (labels accumulate, nothing restarts) and is what re-opens an event that
        // had gone quiet and was sitting in its post-roll.
        Emit(ring: ring && changed && labels.Contains("visitor"));
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

    /// <summary>Ends everything one rule on one channel reported, whatever its subject.</summary>
    private void EndSpeaker(string speaker)
    {
        bool any;
        lock (_gate)
        {
            var gone = _active.Keys.Where(k => k == speaker || k.StartsWith(speaker + "|", StringComparison.Ordinal)).ToList();
            foreach (var k in gone) _active.Remove(k);
            any = gone.Count > 0;
        }
        if (!any) return;
        Log.Debug($"{_camera}: ONVIF detection withdrawn by the camera ({speaker})");
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
    private void Emit(bool repush = false, bool ring = false)
    {
        // Held across snapshot AND delivery: the poll thread and the re-push loop both
        // emit, and a stale "MD" landing after a "none" would re-arm an event nobody ends.
        lock (_emitGate)
        {
            string[] labels;
            lock (_gate)
                labels = _active.Values.SelectMany(v => v.Labels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // A doorbell press goes out once, with its own start: Home Assistant takes every push carrying it as a ring.
            bool visitor = labels.Contains("visitor", StringComparer.OrdinalIgnoreCase);
            if (!ring) labels = labels.Where(l => !l.Equals("visitor", StringComparison.OrdinalIgnoreCase)).ToArray();
            // Nor is the all-clear sent while it is still active: the recording stays open until it ends.
            if (labels.Length == 0 && (repush || visitor)) return;
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
                _active[k] = _active[k] with { LastReport = _active[k].LastReport - by };
    }

    /// <summary>Test seam: one re-push, as the loop would send it.</summary>
    internal void RepushForTest() => Emit(repush: true);

    /// <summary>Test seam: ignores events naming these video sources, as the control
    /// surface would once it has bound the streams.</summary>
    internal void UseOtherChannelsForTest(IReadOnlyCollection<string> others)
    {
        _others = others;
        _othersKnown = true;
        if (others.Count > 0) EndOtherChannels(others);
    }
}
