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
/// came from.
/// </summary>
public sealed class OnvifEventService
{
    /// <summary>How long a subscription is asked to live, and how long before that
    /// it is renewed. Short enough that a camera reclaims it soon after Neolink
    /// stops, long enough that renewal is not most of the traffic.</summary>
    private static readonly TimeSpan Termination = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RenewAt = TimeSpan.FromSeconds(40);

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
    /// has to lapse on its own — each label on its own clock, refreshed whenever the
    /// camera reports it again. Stateful detections are never timed out here: they
    /// last until the camera says they ended, exactly as it promised to.</summary>
    private static readonly TimeSpan OneShotHold = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait before asking a camera whose ONVIF answered with no
    /// event service at all. A lasting answer, re-checked occasionally only because
    /// someone may turn the service on in the camera's own settings.</summary>
    private static readonly TimeSpan NoServiceRecheck = TimeSpan.FromMinutes(5);

    private readonly string _camera;
    private readonly OnvifClient _onvif;
    private readonly object _gate = new();

    /// <summary>The detections the camera currently reports, by label. Stateful =
    /// it said "true" and owes us a "false"; otherwise it is a one-shot, which lapses
    /// <see cref="OneShotHold"/> after its own last report.</summary>
    private readonly Dictionary<string, (bool Stateful, DateTime LastReport)> _active =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _unknownLogged;

    public OnvifEventService(string camera, OnvifClient onvif)
    {
        _camera = camera;
        _onvif = onvif;
    }

    /// <summary>Where a detection goes. Set by the wiring in Program, exactly as the
    /// Baichuan services' sink is.</summary>
    public Action<MotionPush>? MotionSink { get; set; }

    /// <summary>True while a subscription is up.</summary>
    public bool Subscribed { get; private set; }

    /// <summary>Whether this camera has EVER answered with an event subscription —
    /// the honest test for "detections are possible here". Latched rather than live
    /// because a reconnect drops Subscribed for a moment, and a camera's motion
    /// sensors must not come and go in Home Assistant each time that happens.</summary>
    public bool EverSubscribed { get; private set; }

    /// <summary>Suspended cameras hold no connection to anything, this included.</summary>
    public bool Suspended { get; private set; }

    public void SetSuspended(bool suspended) => Suspended = suspended;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);
        // Whether the last attempt ended badly, so a recovery is worth one Info line
        // and a routine re-subscribe (the camera dropped it, as they do) is not.
        bool troubled = true;
        while (!ct.IsCancellationRequested)
        {
            if (Suspended)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }
            string? subscription = null;
            bool proved = false;
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
                subscription = created.Address;
                if (subscription == null)
                {
                    // The camera HAS an event service and refused this once. That is
                    // worth retrying on the ordinary backoff, not the long wait for a
                    // camera with no service — and worth saying why, once.
                    if (!troubled)
                        Log.Info($"{_camera}: the camera refused an ONVIF event subscription " +
                                 $"({created.Reason ?? "no reason given"}) — retrying");
                    troubled = true;
                }
                else
                {
                    Subscribed = true;
                    bool first = !EverSubscribed;
                    EverSubscribed = true;
                    if (first || troubled)
                        Log.Info($"{_camera}: ONVIF events subscribed — detections from this camera " +
                                 "record, notify and reach Home Assistant like a Reolink camera's");
                    else
                        Log.Debug($"{_camera}: ONVIF event subscription renewed after the camera dropped it");
                    proved = await PollAsync(subscription, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Debug($"{_camera}: ONVIF event subscription failed: {Log.Flatten(ex)}");
            }
            finally
            {
                Subscribed = false;
                // Anything the camera was reporting when the subscription died has
                // to be closed out, or the recorder keeps a clip open on a detection
                // nobody can end.
                ClearAll();
                // A camera holds an abandoned subscription until it times out, and
                // small ones have room for only a few. Tidied on shutdown too, with a
                // token of its own (the run's is already cancelled) and a short cap.
                if (subscription != null)
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
            await Task.Delay(backoff, ct).ConfigureAwait(false);
            if (!proved) backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
        ClearAll();
    }

    /// <summary>The poll loop for one subscription. Returns when the subscription is
    /// no longer usable, and the caller makes a new one — true if it delivered at
    /// least one poll before that, i.e. it was a working subscription.</summary>
    private async Task<bool> PollAsync(string subscription, CancellationToken ct)
    {
        var renewDue = DateTime.UtcNow + RenewAt;
        bool delivered = false;
        while (!ct.IsCancellationRequested && !Suspended)
        {
            var started = DateTime.UtcNow;
            var messages = await _onvif.PullMessagesAsync(subscription, Hold, ct).ConfigureAwait(false);
            if (messages == null) return delivered; // gone: re-subscribe
            delivered = true;
            foreach (var m in messages) Handle(m);
            ExpireOneShots();
            // A camera is ASKED to hold the poll open until it has something to say,
            // but not all of them do — some answer "nothing" at once, and polling
            // one of those as fast as it can reply is a busy loop against the
            // camera. When a poll comes back early and empty, pause before the next.
            if (messages.Count == 0 && DateTime.UtcNow - started < MinPollInterval)
                await Task.Delay(MinPollInterval, ct).ConfigureAwait(false);
            if (DateTime.UtcNow >= renewDue)
            {
                if (!await _onvif.RenewSubscriptionAsync(subscription, Termination, ct).ConfigureAwait(false))
                    return delivered;
                renewDue = DateTime.UtcNow + RenewAt;
            }
        }
        return delivered;
    }

    /// <summary>One notification, turned into a start or an end of a detection.</summary>
    internal void Handle(OnvifNotification n)
    {
        if (n.Label is not { } label)
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
        if (n.Active == false) End(label);
        else Start(label, stateful: n.Active == true);
    }

    private void Start(string label, bool stateful)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_active.ContainsKey(label);
            // Once a label has been reported with state, it stays stateful until it
            // ends: a camera that also fires one-shots for the same thing must not
            // turn a detection it promised to end into one that lapses by itself.
            var wasStateful = _active.TryGetValue(label, out var prior) && prior.Stateful;
            _active[label] = (stateful || wasStateful, DateTime.UtcNow);
        }
        // Emitted even when nothing changed: a re-report is harmless to the recorder
        // (labels accumulate, nothing restarts) and is what re-opens an event that
        // had gone quiet and was sitting in its post-roll.
        Emit();
        if (changed) Log.Debug($"{_camera}: ONVIF detection started ({label}{(stateful ? "" : ", one-shot")})");
    }

    private void End(string label)
    {
        lock (_gate)
        {
            if (!_active.Remove(label)) return;
        }
        Log.Debug($"{_camera}: ONVIF detection ended ({label})");
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
                .Select(kv => kv.Key).ToList();
            foreach (var l in lapsed) _active.Remove(l);
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
        string[] labels;
        lock (_gate) labels = _active.Keys.ToArray();
        MotionSink?.Invoke(labels.Length == 0
            ? new MotionPush("none", Array.Empty<string>())
            : new MotionPush("MD", labels));
    }

    /// <summary>The labels currently active, for tests.</summary>
    internal IReadOnlyCollection<string> ActiveLabels
    {
        get { lock (_gate) return _active.Keys.ToArray(); }
    }

    /// <summary>Test seam: ages every active label by <paramref name="by"/>, so the
    /// one-shot lapse can be exercised without waiting it out.</summary>
    internal void AgeForTest(TimeSpan by)
    {
        lock (_gate)
            foreach (var k in _active.Keys.ToList())
                _active[k] = (_active[k].Stateful, _active[k].LastReport - by);
    }
}
