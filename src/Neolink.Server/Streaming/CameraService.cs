// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Threading.Channels;
using Neolink.Config;
using Neolink.Media;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>A source of the live, logged-in camera session of one stream service.</summary>
public interface ILiveCameraSource
{
    string Name { get; }

    /// <summary>The current logged-in session, or null while (re)connecting.</summary>
    IBcCamera? LiveCamera { get; }

    /// <summary>Told when some OTHER path proved this is a battery camera (the
    /// capability sweep, which probes with a longer budget than the one short
    /// login-time query). Without it a slow camera that misses that query is
    /// treated as mains for the whole session — mains idle grace, no battery
    /// reading. Default: ignore, for sources that have no such state.</summary>
    void BatteryDetected() { }
}

/// <summary>
/// Owns the connection to one camera stream (main/sub/extern): connects, logs in,
/// starts the video stream, demuxes media frames into the hub, and reconnects
/// with exponential backoff on failure. While streaming, the session is published
/// via <see cref="LiveCamera"/> so control commands can ride the same connection.
/// </summary>
public sealed class CameraService : ILiveCameraSource
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthRetryDelay = TimeSpan.FromSeconds(30);
    /// <summary>How recent a viewer's DESCRIBE/init attempt still counts as demand.</summary>
    private static readonly TimeSpan DemandWindow = TimeSpan.FromSeconds(20);
    /// <summary>How long a sleep-friendly stream keeps running after the last viewer
    /// leaves. Short on purpose: every second past the last viewer is a second the
    /// camera is held awake for nobody.</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(30);
    /// <summary>Battery cameras get a much shorter idle linger: every awake second
    /// costs charge, and the Reolink app itself holds sessions for ~15s. Active
    /// motion counts as demand (see <see cref="MotionDemandHold"/>), so event
    /// clips still run to their natural end before this timer starts.</summary>
    private static readonly TimeSpan BatteryIdleGrace = TimeSpan.FromSeconds(20);
    /// <summary>How long after the last ACTIVE motion push the session still counts
    /// as in-demand on a sleep-friendly camera — covers the recorder's post-roll
    /// (post_seconds, default 8) so a wake-capture clip isn't cut short, without
    /// letting the recorder hold a battery camera awake around the clock.</summary>
    private static readonly TimeSpan MotionDemandHold = TimeSpan.FromSeconds(10);
    /// <summary>Wake-capture: how often to check whether a sleeping camera has woken.</summary>
    private static readonly TimeSpan WakeScanInterval = TimeSpan.FromSeconds(5);
    /// <summary>Probe-free window after parking: the camera needs quiet to doze off,
    /// and our own discovery probes would reset its idle timer.</summary>
    private static readonly TimeSpan WakeSettleWindow = TimeSpan.FromSeconds(90);
    /// <summary>Probe cadence while the camera is still awake after our release
    /// (sibling stream, Reolink app, slow dozer) — sparse on purpose.</summary>
    private static readonly TimeSpan AwaitSleepInterval = TimeSpan.FromSeconds(30);
    /// <summary>Probe-interval ceiling while a parked camera keeps answering — the
    /// backoff schedule doubles from <see cref="AwaitSleepInterval"/> up to here.
    /// Ten minutes, because the Argus Solar needed roughly that much TRUE quiet to
    /// sink from its answering light sleep into probe-silent deep sleep — and a
    /// probe burst resets that clock.</summary>
    private static readonly TimeSpan MaxAwaitSleepInterval = TimeSpan.FromMinutes(10);
    /// <summary>Consecutive answered probes at which the backoff explains itself once.</summary>
    private const int WakeChipBackoffNote = 6;
    /// <summary>Wake-capture liveness probe timeout. Generous on purpose: measured
    /// against a live Argus Solar, an AWAKE camera's discovery answers ranged from
    /// 211 ms to 1883 ms — a 2 s timeout misclassified 4 of 9 probes as unanswered,
    /// two in a row read as "asleep", and the next slow answer became a false
    /// "camera woke itself" (the reconnect churn of issue #44). Its wake chip's
    /// light-sleep answers run slower still (up to 4.5 s measured). A deeply
    /// sleeping camera never answers at all, so the patience costs nothing.</summary>
    private static readonly TimeSpan WakeProbeTimeout = TimeSpan.FromSeconds(6);

    private readonly CameraConfig _config;
    private readonly StreamKind _kind;
    private readonly IMediaSink _hub;
    private readonly IStreamHub? _demandHub; // same hub, viewer-demand view (null in tests)
    private readonly TimeSpan _startupDelay;

    /// <summary>AI tokens the pipeline knows how to normalize (see EventRecorder.LabelsOf).</summary>
    private static readonly HashSet<string> KnownAiTypes = new(StringComparer.Ordinal)
    {
        "people", "person", "face", "vehicle", "car", "dog_cat", "animal", "pet",
        "package", "visitor", "doorbell",
        // Crying-sound detection (indoor cams, e.g. E1 series): "cry" confirmed
        // from an E1 Pro; the other spellings are guesses at firmware variants.
        "cry", "baby_cry", "babycry",
        // Perimeter protection (line/zone crossing) token spellings seen or expected
        "crossline", "cross_line", "tripwire", "intrude", "intrusion", "region",
        "perimeter", "linger", "loiter", "loitering",
    };
    private readonly HashSet<string> _reportedAiTypes = new(StringComparer.Ordinal);
    private volatile IBcCamera? _live;
    private volatile bool _batteryPowered;
    private volatile BatteryPush? _battery;
    private volatile int _wifiDbm = int.MinValue; // msg 464 NetInfo pushes; MinValue = none seen
    private volatile string? _netType;            // msg 464 <net_type>: "wifi", "ethernet", …
    private volatile int _sirenOn = -1;    // from msg 547 pushes: -1 unknown, 0 off, 1 on
    private volatile int _privacyOn = -1;  // from msg 623 pushes: -1 unknown, 0 off, 1 on
    private volatile bool _privacyLoop;    // dark + reconnecting: log it once, then quiet the churn
    private volatile bool _parked;
    private volatile bool _suspended;      // user pressed "suspend": hold no connection at all
    private volatile CancellationTokenSource? _activeStream; // the live session's CTS, to interrupt on suspend
    private bool _sleepHintLogged;
    private bool _scanLogged; // "wake-capture watching" said once
    // Wake-capture forensics (issue #44). A "self-wake" that is really OUR probe
    // waking the camera looks identical in the log to a real motion wake — both
    // read "camera woke itself". These carry the evidence that tells them apart
    // from the probe loop into the session that follows.
    private WakeDiag? _wakeDiag;
    private double _lastProbeMs;
    private bool _wakeClipStarted; // one wake clip per session, on the first keyframe
    private bool _wakeChipLogged;  // the wake-chip explanation is said once, at Warn
    // "Held awake by …" reporting: when it was last said, and when the current
    // hold began, so a camera that never sleeps says so instead of going quiet.
    private DateTime _lastHoldLog;
    private DateTime _heldSince;
    private static readonly TimeSpan HoldLogEvery = TimeSpan.FromHours(1);
    // Ticks (UTC) of the last ACTIVE motion push seen this session — active
    // detection counts as demand on sleep-friendly cameras so event clips finish
    // before the idle timer starts. Written from the motion watcher task.
    private long _lastMotionActiveTicks;
    // Discovery-probe state per camera NAME (shared across its Main/Sub services,
    // so they don't both sweep). We probe often for a short window — likely to
    // catch a briefly-woken battery camera — then stop for good.
    private sealed class ProbeState { public DateTime First; public DateTime Last; public bool StopLogged; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProbeState> _probeState = new();
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromMinutes(15);

    public CameraService(CameraConfig config, StreamKind kind, IMediaSink hub, TimeSpan startupDelay)
    {
        _config = config;
        _kind = kind;
        _hub = hub;
        _demandHub = hub as IStreamHub;
        _startupDelay = startupDelay;
    }

    public string Name => _config.Name;
    public StreamKind Kind => _kind;
    public IBcCamera? LiveCamera => _live;

    /// <summary>
    /// Exactly ONE service per camera runs the wake-capture probe loop (set during
    /// startup wiring to the recording stream's service, falling back to the
    /// primary). Sibling streams park passively — a battery camera must not be
    /// discovery-probed by two loops at once, and on a self-wake only the stream
    /// that records the event needs to connect at all.
    /// </summary>
    public bool WakeProbeOwner { get; set; } = true;

    /// <summary>True once the camera has answered a battery query (battery model).</summary>
    public bool BatteryPowered => _batteryPowered;

    /// <inheritdoc />
    public void BatteryDetected()
    {
        if (_batteryPowered) return;
        _batteryPowered = true;
        Log.Info($"{Tag}: battery-powered camera confirmed by the capability probe — " +
                 "switching to the short battery idle grace");
    }

    /// <summary>Latest battery reading (login query + msg 252 pushes), or null.</summary>
    public BatteryPush? Battery => _battery;

    /// <summary>Latest Wi-Fi RSSI in dBm (msg 464 NetInfo pushes), or null. The
    /// Baichuan-side source for the sidebar Wi-Fi chip — the only one on cameras
    /// without the Reolink HTTP API (Lumus, battery doorbells).</summary>
    public int? WifiSignal => _wifiDbm == int.MinValue ? null : _wifiDbm;

    /// <summary>The link type the camera last announced ("wifi", "ethernet", …), or
    /// null. A wired camera says so even though it reports no signal.</summary>
    public string? NetType => _netType;

    /// <summary>Last siren state the camera pushed (msg 547); null before the first push.</summary>
    public bool? SirenOn => _sirenOn < 0 ? null : _sirenOn == 1;

    /// <summary>Last privacy-mode state the camera pushed (msg 623); null before the first push.</summary>
    public bool? PrivacyOn => _privacyOn < 0 ? null : _privacyOn == 1;

    /// <summary>True while intentionally disconnected so a battery camera can sleep.</summary>
    public bool Parked => _parked;

    /// <summary>True while the user has SUSPENDED this stream: Neolink holds no
    /// connection, so it can't be viewed or recorded here (the camera is untouched).</summary>
    public bool Suspended => _suspended;

    /// <summary>Suspends or resumes the stream at runtime. Suspending drops any live
    /// session at once; resuming lets the connect loop reconnect on the next tick.</summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended)
        {
            try { _activeStream?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    // Sleep policy: explicit always_on wins; unset = battery cameras doze,
    // everything else streams around the clock (the pre-battery behavior).
    private bool AllowSleep => _demandHub != null && !(_config.AlwaysOn ?? !_batteryPowered);

    /// <summary>True when this camera is one Neolink lets doze — a battery model
    /// without always_on. The web UI marks these tiles and manages their viewing
    /// budget, since every second of video costs the camera charge. Only becomes
    /// true once the camera has actually reported a battery.</summary>
    public bool SleepFriendly => AllowSleep;

    /// <summary>
    /// Set at wiring for cameras that MAY idle without a video subscription. While
    /// no recorder wants frames (<see cref="RecorderWantsFrames"/>, live from the
    /// per-camera recording switches) and nobody watches, the session holds a
    /// control-only connection (sensors, detections and controls stay live) and
    /// subscribes video only on demand. See <see cref="MediaOnDemandPolicy"/>.
    /// </summary>
    public bool MediaOnDemand { get; set; }

    /// <summary>
    /// Whether any recorder wants this camera's frames RIGHT NOW — evaluated live
    /// so the runtime switches count: detection events on, 24/7 on, or an
    /// on-demand clip capture running. Null when no recorder is wired at all.
    /// Flipping a switch on wakes the video within a moment; flipping the last
    /// one off lets the camera go idle after the usual grace.
    /// </summary>
    public Func<bool>? RecorderWantsFrames { get; set; }

    // Wired cameras with default power settings only: an explicit always_on
    // keeps the old always-streaming behavior, and sleep-friendly battery
    // cameras have stronger medicine (the full park above).
    private bool MediaOnDemandActive =>
        _demandHub != null && MediaOnDemand && MediaOnDemandPolicy(_config.AlwaysOn, _batteryPowered);

    // Someone wants frames: a viewer (or a recent stream-open attempt), or a
    // recorder whose switch is on.
    private bool MediaWanted => DemandNow || (RecorderWantsFrames?.Invoke() ?? false);

    /// <summary>On-demand video applies iff always_on is unset (an explicit choice
    /// either way wins) and the camera isn't battery powered (those park the whole
    /// connection instead). What "idle" means is then decided live: no viewers AND
    /// no recorder switch on.</summary>
    internal static bool MediaOnDemandPolicy(bool? alwaysOn, bool batteryPowered) =>
        alwaysOn == null && !batteryPowered;

    // Demand = someone is watching, or recently tried to start watching (viewers
    // only subscribe once video is ready, so the DESCRIBE attempt is the wake call).
    private bool DemandNow => _demandHub == null
        || _demandHub.ViewerCount > 0
        || DateTime.UtcNow - _demandHub.LastViewerAskUtc < DemandWindow;

    /// <summary>Which half of <see cref="DemandNow"/> is asserting, in words — the
    /// log line that ends a park has to name what pulled the camera back up, or a
    /// battery drain caused by our own side is indistinguishable from a real wake.</summary>
    private string DemandReason()
    {
        if (_demandHub == null) return "no demand tracking (test harness)";
        if (_demandHub.ViewerCount > 0)
            return $"{_demandHub.ViewerCount} viewer(s) watching";
        var since = DateTime.UtcNow - _demandHub.LastViewerAskUtc;
        return since < DemandWindow
            ? $"a stream was opened {since.TotalSeconds:0}s ago (RTSP DESCRIBE or a web/HA tile)"
            : "recording switched on";
    }

    // An active detection within the hold window keeps a sleep-friendly session
    // alive so the event clip completes (bounded — this can never hold the camera
    // awake longer than the detection itself plus the hold).
    private bool MotionActiveRecently =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMotionActiveTicks) < MotionDemandHold.Ticks;

    /// <summary>
    /// When set (on the primary stream service), alarm pushes are requested on each
    /// connection and forwarded here. Assigned once during startup wiring.
    /// </summary>
    public Action<MotionPush>? MotionSink { get; set; }

    /// <summary>
    /// When set (on the primary stream service), unsolicited status pushes (Wi-Fi
    /// signal, sleep, siren, floodlight) are forwarded here. Assigned once during
    /// startup wiring.
    /// </summary>
    public Action<StatusPush>? StatusSink { get; set; }

    private string Tag => $"{_config.Name} ({_kind})";

    public async Task RunAsync(CancellationToken ct)
    {
        if (_startupDelay > TimeSpan.Zero)
        {
            Log.Info($"{Tag}: delaying startup by {_startupDelay.TotalSeconds:0.#}s");
            try
            {
                await Task.Delay(_startupDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            // Suspended by the user (Neolink-side): hold NO connection, so the camera
            // can't be viewed or recorded here — regardless of viewer demand or power
            // source. The camera itself is untouched (its own SD/cloud recording and
            // any other client pulling it directly keep working). Resuming reconnects.
            if (_suspended)
            {
                Log.Info($"{Tag}: suspended — Neolink.NET will not view or record this camera until it is resumed");
                try
                {
                    while (_suspended && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (ct.IsCancellationRequested) return;
                Log.Info($"{Tag}: resumed — reconnecting");
                backoff = MinBackoff;
                continue;
            }

            // Sleep-friendly cameras: stay disconnected while nobody watches, so
            // the camera can power down. A viewer's DESCRIBE/init attempt wakes us.
            if (AllowSleep && !DemandNow)
            {
                _parked = true;
                try
                {
                    if (_config.WakeCapture && WakeProbeOwner)
                    {
                        // Wake-capture (opt-in): watch for the camera to wake ITSELF
                        // (motion) and connect the moment it does, so its events are
                        // caught without holding it awake. The trigger is the
                        // sleep→wake EDGE, never mere reachability: right after we
                        // release the camera it is still awake (and while a sibling
                        // stream keeps streaming it stays awake), so "it answered a
                        // probe" proves nothing — and probing an awake camera resets
                        // its doze timer, the very thing that must not happen. So:
                        // a probe-free settle window first (let it doze off), then
                        // sparse probes until it is seen ASLEEP, and only a reachable
                        // answer AFTER that means "the camera woke itself".
                        //
                        // "Seen asleep" is DEBOUNCED (issue #44): a single unanswered
                        // probe proves nothing — Wi-Fi power save eats unicast for
                        // seconds at a time, and treating one lost packet as "asleep"
                        // manufactured a false asleep→awake edge on the very next
                        // answered probe, i.e. Neolink itself waking the camera and
                        // burning its battery. Only consecutive misses arm the edge.
                        if (!_scanLogged)
                        {
                            _scanLogged = true;
                            Log.Info($"{Tag}: sleep-friendly (wake-capture) — letting the camera doze; " +
                                     "will connect when it wakes itself (motion) or a viewer opens the stream");
                        }
                        else
                        {
                            // Every later park too: without this the log jumps from a
                            // disconnect straight to the next connect with nothing in
                            // between, and there is no way to tell a self-wake from
                            // something on OUR side asking for the stream.
                            Log.Debug($"{Tag}: parked again — watching for a self-wake");
                        }
                        var parkedAt = DateTime.UtcNow;
                        var edge = new WakeEdgeDetector();
                        var nextProbe = DateTime.UtcNow + WakeSettleWindow;
                        var diag = new WakeDiag { ParkedAt = DateTime.UtcNow };
                        // Wake-chip models (Argus Solar, the issue-#44 doorbell):
                        // a low-power chip answers discovery IN THE CAMERA'S SLEEP —
                        // measured live: full session offers 3+ minutes into true
                        // radio silence. Probing can then never see sleep OR wake;
                        // every probe just pokes the chip. When every post-settle
                        // probe keeps answering, stop for this park and wait
                        // passively. (Per park, not forever: a sibling viewer
                        // session also answers everything while it streams, and
                        // the next park re-evaluates from scratch.)
                        int answeredStraight = 0;
                        while (!DemandNow)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                            if (DemandNow)
                            {
                                // Left the park because OUR side wants the stream, not
                                // because the camera woke. Say so and say who: on a
                                // battery camera this is the difference between "an
                                // event was caught" and "something here just spent your
                                // charge", and it was previously a silent break.
                                Log.Info($"{Tag}: {DemandReason()} — reconnecting after " +
                                         $"{(DateTime.UtcNow - parkedAt).TotalSeconds:0}s parked " +
                                         "(the camera did not wake on its own)");
                                break;
                            }
                            if (DateTime.UtcNow < nextProbe) continue;
                            bool answered = await ProbeAwakeAsync(ct).ConfigureAwait(false);
                            bool armedBefore = edge.Armed;
                            diag.OnProbe(answered, _lastProbeMs);
                            if (edge.OnProbe(answered))
                            {
                                diag.EdgeAt = DateTime.UtcNow;
                                _wakeDiag = diag;
                                Log.Info($"{Tag}: camera woke itself — connecting to catch the event " +
                                         $"({diag.ProbesSinceArmed} probe(s) and {diag.SinceArmedSeconds:0}s after it " +
                                         $"read asleep; this probe answered in {_lastProbeMs:0}ms)");
                                break;
                            }
                            Log.Debug($"{Tag}: wake probe {(answered ? "answered" : "unanswered")} " +
                                      $"in {_lastProbeMs:0}ms (armed={edge.Armed})");
                            if (edge.Armed && !armedBefore)
                            {
                                diag.ArmedAt = DateTime.UtcNow;
                                Log.Info($"{Tag}: camera is asleep ({WakeEdgeDetector.MissesToArm} probes " +
                                         "unanswered) — armed to connect on its next self-wake");
                            }
                            // Persistent answers are NOT a verdict — they are pressure
                            // to back off. Measured on an Argus Solar: its wake chip
                            // answers discovery slowly (1.7–4.5 s) through a LIGHT
                            // sleep stage that our own 30 s probing helps sustain, but
                            // after ~10 minutes of true quiet it reaches DEEP sleep
                            // and goes probe-silent (ICMP stays up — the Wi-Fi module
                            // answers that autonomously) — and silence is exactly what
                            // the edge detector needs. So: every answered probe
                            // stretches the next interval (30 s → 60 → 120 → 240,
                            // capped at 300 s), giving the camera the quiet it needs
                            // to sink; the first miss snaps back to the fast scan.
                            answeredStraight = answered && !edge.Armed ? answeredStraight + 1 : 0;
                            if (answeredStraight == WakeChipBackoffNote && !_wakeChipLogged)
                            {
                                _wakeChipLogged = true;
                                Log.Info($"{Tag}: still answering probes ({answeredStraight} straight) — either " +
                                         "something holds it awake, or this model's low-power chip answers discovery " +
                                         "through its light sleep (Argus Solar, some doorbells). Backing the probes " +
                                         "off so it can reach deep sleep, where it goes silent and its next " +
                                         "self-wake becomes detectable.");
                            }
                            var awaitSleep = answeredStraight <= 1
                                ? AwaitSleepInterval
                                : TimeSpan.FromTicks(Math.Min(MaxAwaitSleepInterval.Ticks,
                                    AwaitSleepInterval.Ticks << Math.Min(answeredStraight - 1, 4)));
                            // Unanswered: recheck soon (confirm the miss, then scan for
                            // the wake). Answered while unarmed: back off as above so
                            // we never hold the camera in light sleep ourselves.
                            nextProbe = DateTime.UtcNow + (answered ? awaitSleep : WakeScanInterval);
                        }
                    }
                    else if (_config.WakeCapture)
                    {
                        // Sibling stream of a wake-capture camera: the probe-owner
                        // stream (the one that records) watches for self-wakes and
                        // connects alone — a second session would double the probing
                        // and the awake time. This one connects only for a viewer.
                        Log.Info($"{Tag}: parked — the recording stream watches for self-wakes; " +
                                 "this stream connects when a viewer opens it");
                        while (!DemandNow)
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        Log.Info($"{Tag}: viewer waiting — reconnecting");
                    }
                    else
                    {
                        Log.Info($"{Tag}: parked — letting the battery camera sleep (open the stream to reconnect)");
                        while (!DemandNow)
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        Log.Info($"{Tag}: viewer waiting — reconnecting");
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    _parked = false;
                }
                backoff = MinBackoff;
            }

            bool gotFrames = false;
            try
            {
                bool wentIdle = await StreamOnceAsync(ct, () => gotFrames = true).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return; // cancelled cleanly
                if (wentIdle)
                {
                    backoff = MinBackoff;
                    continue; // loop parks above until someone watches again
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (_suspended)
            {
                // A live session interrupted by SetSuspended — the top-of-loop park
                // holds it until resumed. Not an error; no backoff, no log noise.
                continue;
            }
            catch (AuthFailedException ex)
            {
                // Wrong credentials are permanent, but cameras also reject logins
                // transiently (rebooting, user table full), so retry at a slow pace
                // rather than giving up for good.
                Log.Error($"{Tag}: authentication failed: {ex.Message}; " +
                          $"retrying in {AuthRetryDelay.TotalSeconds:0}s (check the camera credentials)");
                try
                {
                    await Task.Delay(AuthRetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = MinBackoff;
                continue;
            }
            catch (Exception ex)
            {
                // Interrupted by a user suspend (the session's error races the token):
                // not a failure — let the top-of-loop park hold it, no log, no backoff.
                if (_suspended) continue;
                if (gotFrames)
                {
                    backoff = MinBackoff; // we did stream; reset the backoff
                    _sleepHintLogged = false;
                }
                // A sleeping battery camera refuses connections until IT wakes
                // (PIR motion, the Reolink app) — say so once per outage.
                string hint = "";
                if (_batteryPowered && !gotFrames && !_sleepHintLogged)
                {
                    _sleepHintLogged = true;
                    hint = " (a sleeping battery camera is unreachable until it wakes itself — PIR motion or the Reolink app)";
                }
                // Privacy-mode churn (dark camera closing the connection) is expected
                // and already announced once as a warning — keep the per-retry line at
                // Debug so it doesn't flood the log.
                if (_privacyLoop)
                    Log.Debug($"{Tag}: privacy reconnect: {Log.Flatten(ex)}; retrying in {backoff.TotalSeconds:0}s");
                else
                    Log.Error($"{Tag}: {Log.Flatten(ex)}; retrying in {backoff.TotalSeconds:0}s{hint}");

                // Opt-in diagnostics for UDP-only battery cameras (Argus family):
                // TCP will never answer on those, so while the camera stays
                // unreachable, probe the Baichuan-over-UDP discovery handshake
                // and log the exchange (UID masked, no credentials).
                if (_config.UdpProbe && !gotFrames && !ct.IsCancellationRequested)
                    await MaybeUdpProbeAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                // A wake that never got as far as a live session (connect refused,
                // login failed) still owes its verdict here: StreamOnceAsync's own
                // report only runs once logged in, and a diagnostic left pending
                // would be printed against the NEXT session with nonsense timings.
                ReportWakeDiag();
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    /// <summary>
    /// The opt-in camera-discovery diagnostic (<see cref="CameraProbe"/>). While an
    /// unreachable camera stays down, it sweeps once every <see cref="ProbeEvery"/>
    /// for a <see cref="ProbeWindow"/> window (from the first sweep) and then stops
    /// for good — frequent enough to catch a battery camera during a brief wake,
    /// without probing forever. Shared across the camera's stream services; never
    /// lets a sweep failure disturb the retry loop.
    /// </summary>
    private async Task MaybeUdpProbeAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var st = _probeState.GetOrAdd(_config.Name, _ => new ProbeState());
        lock (st)
        {
            if (st.First == default) st.First = now;
            else if (now - st.First > ProbeWindow)
            {
                if (!st.StopLogged)
                {
                    st.StopLogged = true;
                    Log.Info($"{Tag}: [discover] discovery probing stopped — the {ProbeWindow.TotalMinutes:0}-minute " +
                             "window has elapsed; restart Neolink to probe again");
                }
                return;
            }
            if (st.Last != default && now - st.Last < ProbeEvery) return;
            st.Last = now;
        }
        try
        {
            await CameraProbe.SweepAsync(Tag, _config, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"{Tag}: [discover] sweep crashed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>
    /// Cheap liveness probe for wake-capture: is the camera answering right now?
    /// A short-timeout UDP discovery reply (udp cameras) or TCP connect (everything
    /// else). Never throws; at Debug level the UDP path logs the raw reply — some
    /// battery models answer discovery from their low-power wake chip even while
    /// asleep, and the reply contents are the evidence needed to tell those apart.
    /// </summary>
    private async Task<bool> ProbeAwakeAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_config.Udp)
                return await UdpDiscovery.IsReachableAsync(_config.Host, _config.Uid!, WakeProbeTimeout, ct,
                    logTag: Tag).ConfigureAwait(false);

            using var tcp = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(WakeProbeTimeout);
            await tcp.ConnectAsync(_config.Host, _config.Port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real shutdown — let the caller unwind
        }
        catch
        {
            return false; // asleep / unreachable — the expected common case
        }
        finally
        {
            // How long the answer took matters as much as whether it came: an
            // answered probe that lands near the timeout means the timeout is the
            // marginal thing, and an "unanswered" probe may be a false negative
            // rather than a sleeping camera. See WakeDiag.
            _lastProbeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// One connected session. Returns true when the stream was stopped on purpose
    /// to let an idle battery camera sleep; false when cancelled.
    /// </summary>
    private async Task<bool> StreamOnceAsync(CancellationToken ct, Action onFrame)
    {
        // While the camera is dark (privacy mode) some models (E1 Pro) keep closing
        // and reopening the connection. Announce that once as a warning, then route
        // the per-reconnect chatter to Debug so it doesn't flood the log. `Note`
        // logs at Info normally, Debug while the privacy loop is active.
        void Note(string m) { if (_privacyLoop) Log.Debug(m); else Log.Info(m); }
        bool StallTolerable()
        {
            if (_privacyOn != 1) return false;
            if (!_privacyLoop)
            {
                _privacyLoop = true;
                Log.Warn($"{Tag}: in privacy mode — no video until it is turned off. The camera may keep " +
                         "closing and reopening the connection while dark; that is expected, control still " +
                         "works (e.g. turning privacy off), and reconnect logging is quieted until video resumes.");
            }
            return true;
        }

        // UDP transport (experimental, opt-in) for battery-only cameras that never
        // listen on TCP; everything after connect is identical to the TCP path.
        Note(_config.Udp
            ? $"{Tag}: connecting over UDP to " +
              $"{(string.IsNullOrWhiteSpace(_config.Host) ? "the UID via broadcast discovery" : _config.Host)} (uid set)"
            : $"{Tag}: connecting to {_config.Host}:{_config.Port}");
        await using IBcCamera camera = _config.Udp
            ? await BcCamera.ConnectUdpAsync(_config.Host, _config.Uid!, _config.ChannelId, ct, tag: Tag).ConfigureAwait(false)
            : await BcCamera.ConnectAsync(_config.Host, _config.Port, _config.ChannelId, ct, tag: Tag).ConfigureAwait(false);

        Note($"{Tag}: logging in as '{_config.Username}'");
        await camera.LoginAsync(_config.Username, _config.Password, ct).ConfigureAwait(false);
        var res = camera.DeviceInfo;
        Note($"{Tag}: logged in{(res != null && res.Width > 0 ? $", camera reports {res.Width}x{res.Height}" : "")}");

        await ProbeBatteryAsync(camera, ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeStream = linked; // let SetSuspended interrupt this session at once
        if (_suspended) linked.Cancel(); // a suspend that raced the assignment above

        // Controls and sinks are live from HERE — they ride the control channel and
        // need no video subscription. That's what makes the on-demand hold below
        // free: sensors, detections and the settings panel all keep working.
        _live = camera;
        // A wake-capture cycle handed us its evidence: this session is the rest of
        // the experiment (see WakeDiag). Timed from the point we are logged in.
        if (_wakeDiag is { Reported: false } wdConnect) wdConnect.ConnectedAt = DateTime.UtcNow;
        // One "held awake by …" line per session (then hourly if it never lets go),
        // so flickering demand can't turn the diagnostic into a flood.
        _lastHoldLog = default;
        _heldSince = default;
        _wakeClipStarted = false;
        Task? videoTask = null;
        Task? motionTask = MotionSink is { } sink
            ? Task.Run(() => WatchMotionGuardedAsync(camera, sink, linked.Token), CancellationToken.None)
            : null;
        // The status watch always runs: battery pushes keep the sidebar reading
        // fresh even without MQTT; other pushes go to the external sink if any.
        var externalStatusSink = StatusSink;
        Action<StatusPush> statusSink = push =>
        {
            switch (push)
            {
                case BatteryPush b:
                    _battery = b;
                    // A battery push PROVES this is a battery camera — latch it.
                    // The login-time probe (ProbeBatteryAsync) has a 3s budget and
                    // stays silent when it misses, which on a slow/loaded camera
                    // (Argus Solar over UDP) left _batteryPowered false for the whole
                    // session: the camera then got the mains idle grace instead of
                    // the short battery one and was held awake far longer than
                    // intended, with no battery reading anywhere. The pushes arrive
                    // regularly, so latching here heals that within seconds.
                    if (!_batteryPowered)
                    {
                        _batteryPowered = true;
                        Log.Info($"{Tag}: battery-powered camera detected from its own status push " +
                                 $"(battery at {b.Percent}%{(b.Charging ? ", charging" : "")}) — " +
                                 "switching to the short battery idle grace");
                    }
                    break;
                case WifiSignalPush w:
                    if (w.SignalDbm is { } dbm) _wifiDbm = dbm;
                    if (!string.IsNullOrEmpty(w.NetType)) _netType = w.NetType;
                    break;
                case SirenStatusPush s: _sirenOn = s.On ? 1 : 0; break;
                case SleepStatusPush sl:
                    _privacyOn = sl.Sleeping ? 1 : 0;
                    // The camera's own account of whether it was sleeping when we
                    // arrived — the strongest single discriminator in WakeDiag.
                    if (_wakeDiag is { Reported: false, SleepStatusOnArrival: null } wdSleep)
                    {
                        wdSleep.SleepStatusOnArrival = sl.Sleeping;
                        wdSleep.SleepStatusMs = (DateTime.UtcNow - wdSleep.ConnectedAt).TotalMilliseconds;
                    }
                    break;
            }
            externalStatusSink?.Invoke(push);
        };
        Task statusTask = Task.Run(() => WatchStatusGuardedAsync(camera, statusSink, linked.Token), CancellationToken.None);
        // UDP battery firmware wants to see a LIVING CLIENT, not just transport
        // acks: the official client asks something at the BC layer every 5 s, and
        // an idle session gets recycled after ~1-2 min even with every msg-234
        // keepalive answered (field logs: D2C_DISC at 45-105 s). Ping like the
        // reference client does; the request itself is the activity signal, so a
        // firmware that never answers pings is tolerated. On-demand sessions ping
        // too: a control-only hold would otherwise send nothing for hours, and the
        // ping doubles as its keep-alive/activity signal.
        Task? pingTask = _config.Udp || MediaOnDemandActive
            ? Task.Run(() => PingLoopAsync(camera, linked.Token), CancellationToken.None)
            : null;
        DateTime? idleSince = null;
        try
        {
            // On-demand hold: nothing records this camera and nobody is watching —
            // stay connected WITHOUT subscribing video until a viewer shows up. The
            // camera sends only occasional status pushes, so both ends idle at ~zero
            // cost. A viewer's DESCRIBE/init is the wake signal (same one the battery
            // park uses) — but this session is already logged in, so video starts
            // sub-second instead of after a full reconnect.
            if (MediaOnDemandActive && !MediaWanted)
            {
                Log.Info($"{Tag}: idle — nothing recording and nobody watching; holding a control-only " +
                         "connection (sensors and controls stay live, video starts when someone watches)");
                while (!MediaWanted)
                    await Task.Delay(250, linked.Token).ConfigureAwait(false);
                Log.Info($"{Tag}: {(DemandNow ? "viewer asked" : "recording switched on")} — starting the video stream");
            }

            var binary = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            // Privacy mode (E1 Pro etc.) makes the camera go dark: no video is expected,
            // and losing the connection over it would make the camera — and the privacy
            // switch itself — Unavailable in Home Assistant. Hold the connection instead.
            videoTask = Task.Run(() => camera.StartVideoAsync(_kind, binary.Writer,
                StallTolerable, linked.Token), CancellationToken.None);
            var reader = new MediaFrameReader(binary.Reader);
            long frames = 0;

            while (!ct.IsCancellationRequested)
            {
                MediaFrame frame;
                try
                {
                    frame = await reader.ReadFrameAsync(linked.Token).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    // Binary channel completed -> the video task holds the underlying error
                    await videoTask!.ConfigureAwait(false);
                    throw new IOException("video stream ended");
                }

                if (_privacyLoop)
                {
                    _privacyLoop = false;
                    Log.Info($"{Tag}: privacy mode off — video resumed");
                }
                if (++frames == 1)
                {
                    Log.Info($"{Tag}: receiving media");
                    // Media flowing = definitely not private. Heals a stale flag
                    // when the wake happened while we were reconnecting (the
                    // "off" push can be missed across a connection swap).
                    if (_privacyOn == 1) _privacyOn = 0;
                    if (_wakeDiag is { Reported: false, FirstFrameMs: null } wdFrame)
                        wdFrame.FirstFrameMs = (DateTime.UtcNow - wdFrame.ConnectedAt).TotalMilliseconds;
                }
                onFrame();

                switch (frame)
                {
                    case MediaInfo info:
                        _hub.PublishInfo(info);
                        break;
                    case VideoFrame video:
                        _hub.PublishVideo(video);
                        // Wake clip: starts on the first KEYFRAME, after it is in the
                        // hub — starting on the session's first frame raced the hub's
                        // readiness (the MediaInfo/init hadn't been published yet) and
                        // the recorder logged "stream not ready; event stored without
                        // a clip": a wake event with no footage, which is the exact
                        // failure this exists to prevent.
                        if (video.Keyframe && !_wakeClipStarted && _wakeDiag is { Reported: false })
                        {
                            _wakeClipStarted = true;
                            StartWakeClip(linked.Token);
                        }
                        break;
                    case AacFrame aac:
                        _hub.PublishAac(aac);
                        break;
                    case AdpcmFrame adpcm:
                        _hub.PublishAdpcm(adpcm);
                        break;
                }

                // Sleep-friendly: once the last viewer has been gone a while,
                // disconnect on purpose so the camera can power down. Recorders
                // don't count — holding a battery camera awake to record 24/7
                // needs an explicit always_on. ACTIVE motion does count (bounded
                // by MotionDemandHold): during a wake-capture catch the clip must
                // reach its post-roll before the idle timer starts. Battery
                // cameras get the short grace — the old 60s linger multiplied
                // every event's awake time several-fold (issue #44).
                if (AllowSleep)
                {
                    if (DemandNow || MotionActiveRecently)
                    {
                        idleSince = null;
                        // WHY a sleep-friendly camera is still awake is the one thing
                        // the log never said: it announces the disconnect, but a
                        // camera that never disconnects produced no line at all, so a
                        // battery draining for hours looked identical to a healthy
                        // idle one. Name the holder, once per hold and then hourly.
                        if (DateTime.UtcNow - _lastHoldLog > HoldLogEvery)
                        {
                            bool first = _lastHoldLog == default;
                            _lastHoldLog = DateTime.UtcNow;
                            string held = MotionActiveRecently
                                ? $"active motion {(DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastMotionActiveTicks), DateTimeKind.Utc)).TotalSeconds:0}s ago"
                                : DemandReason();
                            Log.Info($"{Tag}: held awake by {held} — this battery camera cannot sleep " +
                                     $"while that holds{(first ? "" : $" (still held after {(DateTime.UtcNow - _heldSince).TotalMinutes:0} min)")}");
                        }
                        if (_heldSince == default) _heldSince = DateTime.UtcNow;
                    }
                    else
                    {
                        var grace = _batteryPowered ? BatteryIdleGrace : IdleGrace;
                        idleSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - idleSince > grace)
                        {
                            Log.Info($"{Tag}: no viewers for {grace.TotalSeconds:0}s — " +
                                     "disconnecting so the battery camera can sleep");
                            return true;
                        }
                    }
                }
                // On-demand video (nothing recording right now): once the last
                // consumer has been gone a while, drop the whole session and let
                // the reconnect settle into the control-only hold above. A
                // reconnect — not just cancelling the subscription — because the
                // camera keeps pushing video until told otherwise, and a fresh
                // login is the one way every firmware provably stops.
                else if (MediaOnDemandActive)
                {
                    if (MediaWanted)
                    {
                        idleSince = null;
                    }
                    else
                    {
                        idleSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - idleSince > IdleGrace)
                        {
                            Log.Info($"{Tag}: nothing consumed the video for {IdleGrace.TotalSeconds:0}s — " +
                                     "dropping the video stream (staying connected for sensors and controls)");
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        finally
        {
            _live = null;
            _activeStream = null;
            _hub.SourceStopped(); // stale GOP must not prime viewers while we're down
            ReportWakeDiag();
            linked.Cancel();
            if (videoTask != null)
            {
                try { await videoTask.ConfigureAwait(false); } catch { }
            }
            if (motionTask != null)
            {
                try { await motionTask.ConfigureAwait(false); } catch { }
            }
            try { await statusTask.ConfigureAwait(false); } catch { }
            if (pingTask != null)
            {
                try { await pingTask.ConfigureAwait(false); } catch { }
            }
        }
    }

    /// <summary>
    /// Starts an event clip for a self-wake without waiting for a detection push
    /// (which never arrives when the subject left frame before we connected). The
    /// synthetic push is External: wake-capture is the user's explicit opt-in, so
    /// it bypasses the event-type filter the way the HA record switch does — only
    /// the master events switch can veto it. If no REAL detection follows within
    /// <see cref="WakeClipWindow"/>, a matching all-clear ends the clip through the
    /// recorder's normal post-roll; a real one takes over the event's lifecycle.
    /// </summary>
    private void StartWakeClip(CancellationToken ct)
    {
        if (MotionSink is not { } sink || !_config.WakeCapture) return;
        var started = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastMotionActiveTicks, started); // hold the session while the clip runs
        sink(new MotionPush("wake", WakeLabel, External: true));
        Log.Info($"{Tag}: recording the self-wake (no detection push needed; " +
                 $"a detection within {WakeClipWindow.TotalSeconds:0}s extends and relabels the clip)");
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(WakeClipWindow, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            // A real detection arrived meanwhile: its own all-clear governs.
            if (Interlocked.Read(ref _lastMotionActiveTicks) > started) return;
            sink(new MotionPush("none", Array.Empty<string>(), External: true));
        }, CancellationToken.None);
    }

    private static readonly string[] WakeLabel = { "wake" };
    /// <summary>How much footage a bare self-wake keeps (plus the recorder's
    /// pre/post-roll) when no detection push follows to say more.</summary>
    private static readonly TimeSpan WakeClipWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Closes out one wake-capture cycle: prints the evidence gathered by the probe
    /// loop and the session it triggered, plus a plain verdict on whether the camera
    /// really woke itself or we woke it. Info level and one line per wake — this is
    /// the data an affected user is asked to paste into issue #44, so it must not
    /// need a debug build to appear. No-op for sessions that no wake started.
    /// </summary>
    private void ReportWakeDiag()
    {
        if (_wakeDiag is not { Reported: false } d) return;
        d.Reported = true;
        _wakeDiag = null;
        if (d.ConnectedAt == default)
        {
            // The wake fired but the session never got as far as logging in.
            Log.Info($"{Tag}: [wake-diag] the wake did not lead to a session (connect or login failed). " +
                     $"Evidence: woke {d.SinceArmedSeconds:0}s / {d.ProbesSinceArmed} probe(s) after reading " +
                     $"asleep ({d.ProbesTotal} probes this park, slowest answer {d.SlowestAnsweredMs:0}ms of a " +
                     $"{WakeProbeTimeout.TotalMilliseconds:0}ms timeout) — a probe answered but the camera then " +
                     "refused the connection, which points at the probe having briefly roused it.");
            return;
        }
        string sleepSaid = d.SleepStatusOnArrival switch
        {
            true => $"the camera said ASLEEP {d.SleepStatusMs:0}ms after connect (it was sleeping when we knocked)",
            false => $"the camera said AWAKE {d.SleepStatusMs:0}ms after connect (it was already up)",
            _ => "the camera never sent a sleepStatus push",
        };
        Log.Info($"{Tag}: [wake-diag] {d.Verdict(WakeProbeTimeout.TotalMilliseconds)}. " +
                 $"Evidence: woke {d.SinceArmedSeconds:0}s / {d.ProbesSinceArmed} probe(s) after reading asleep " +
                 $"({d.ProbesTotal} probes this park, slowest answer {d.SlowestAnsweredMs:0}ms of a " +
                 $"{WakeProbeTimeout.TotalMilliseconds:0}ms timeout); " +
                 $"{d.UnansweredSummary(WakeProbeTimeout.TotalMilliseconds)}; {sleepSaid}; " +
                 $"first frame {(d.FirstFrameMs is { } ff ? $"{ff:0}ms after connect" : "never arrived")}; " +
                 $"detection during the session: {(d.SawDetection ? "YES" : "none")}.");
    }

    /// <summary>Session-activity ping for UDP cameras: msg 93 every 5 s, like the
    /// official client. An unanswered ping is logged once and pinging continues —
    /// the request is what proves the client alive; a dead connection is noticed
    /// by the video stream, which tears the session down.</summary>
    private async Task PingLoopAsync(IBcCamera camera, CancellationToken ct)
    {
        bool quiet = false;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                await camera.PingAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    quiet = true;
                    Log.Debug($"{Tag}: ping (msg 93) unanswered ({ex.Message}) — continuing to ping for session activity");
                }
            }
        }
    }

    /// <summary>
    /// One battery query at session start: only battery models answer, so this
    /// doubles as power-source detection for the sleep default and seeds the
    /// sidebar reading (msg 252 pushes keep it fresh afterwards).
    /// </summary>
    private async Task ProbeBatteryAsync(IBcCamera camera, CancellationToken ct)
    {
        try
        {
            var info = await camera.GetBatteryInfoAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (info != null && BcCamera.ParseBatteryInfo(info) is { } b)
            {
                bool first = !_batteryPowered;
                _batteryPowered = true;
                _battery = b;
                var reading = $"battery at {b.Percent}%{(b.Charging ? ", charging" : "")}";
                if (first)
                    Log.Info($"{Tag}: battery-powered camera detected ({reading}); " +
                             (AllowSleep
                                 ? "it will sleep while nobody watches (set \"always_on\": true to keep it awake)"
                                 : "always_on keeps it awake around the clock"));
                else
                    Log.Debug($"{Tag}: {reading}");
            }
        }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException)
        {
            // Usually genuine: a mains camera rejects the query. But a battery
            // camera that is merely slow lands here too, and the consequences are
            // invisible (mains idle grace, no battery reading), so say which
            // happened. A real battery camera heals on its first msg-252 push.
            Log.Debug($"{Tag}: battery query unanswered within 3s ({ex.GetType().Name}) — " +
                      "treating as mains unless the camera pushes a battery reading");
        }
    }

    /// <summary>
    /// Alarm-push listener riding the same connection as the video stream. Failures
    /// stay local: a camera without motion push must not disturb streaming.
    /// </summary>
    private async Task WatchMotionGuardedAsync(IBcCamera camera, Action<MotionPush> sink, CancellationToken ct)
    {
        // A video doorbell's button press arrives as a "visitor" AI push — worth
        // its own log line even when event recording and MQTT are switched off.
        void LoggedSink(MotionPush push)
        {
            if (push.Active)
            {
                Interlocked.Exchange(ref _lastMotionActiveTicks, DateTime.UtcNow.Ticks);
                // A detection during a wake-capture session is the proof that the
                // wake was real — the whole point of the feature (see WakeDiag).
                if (_wakeDiag is { Reported: false } wd) wd.SawDetection = true;
            }
            if (push.Active && (push.AiTypes.Contains("visitor") || push.AiTypes.Contains("doorbell")))
                Log.Info($"{Tag}: doorbell pressed");
            // AI tokens we don't recognize still become events (raw label), but
            // say so once — firmware vocabularies vary, and the token is exactly
            // what's needed to extend the mapping (doorbells especially).
            foreach (var t in push.AiTypes)
                if (!KnownAiTypes.Contains(t) && _reportedAiTypes.Add(t))
                    Log.Info($"{Tag}: camera pushed unrecognized AI type '{t}' (kept as an event label) — " +
                             "if this fired when the doorbell was pressed, please report the label");
            sink(push);
        }

        try
        {
            await camera.WatchMotionAsync(LoggedSink, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is CameraCommandException or TimeoutException)
        {
            Log.Warn($"{Tag}: camera declined motion pushes ({ex.Message}); no events this session");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Connection died; the video loop notices and reconnects everything.
            Log.Debug($"{Tag}: motion watch ended: {ex.Message}");
        }
    }

    /// <summary>
    /// Status-push listener riding the same connection as the video stream. These
    /// are purely informational, so any failure stays local to this task.
    /// </summary>
    private async Task WatchStatusGuardedAsync(IBcCamera camera, Action<StatusPush> sink, CancellationToken ct)
    {
        try
        {
            await camera.WatchStatusAsync(sink, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Connection died; the video loop notices and reconnects everything.
            Log.Debug($"{Tag}: status watch ended: {ex.Message}");
        }
    }
}

/// <summary>
/// The wake-capture sleep→wake edge, debounced. Feed it one probe result at a
/// time: it arms only after <see cref="MissesToArm"/> CONSECUTIVE unanswered
/// probes (a single miss is routinely just Wi-Fi power save eating a packet,
/// and arming on it made the next answered probe read as a false "camera woke
/// itself" — Neolink then connected and woke the camera it was trying to let
/// sleep). Once armed, the first answered probe reports the wake edge.
/// </summary>
/// <summary>
/// Evidence for ONE wake-capture cycle, carried from the probe loop into the
/// session it triggered, so the log can say WHY the camera was awake instead of
/// asserting "it woke itself" (issue #44).
///
/// Three explanations produce the same "camera woke itself" line today:
///   1. a real self-wake — the camera's PIR fired and it came up on its own;
///   2. OUR probe woke it — the wake probe is a C2D_C connect request, not a
///      passive ping, so on firmware whose low-power chip forwards it to the
///      main SoC we are the ones turning the camera on, every probe interval,
///      forever (and flattening the battery doing it);
///   3. it was never asleep — probes timed out (2 s) on a camera that answers
///      slowly, manufacturing a false asleep→awake edge.
///
/// The discriminators, all recorded here: how quickly the "wake" followed our
/// arming probe (case 2 lands within one probe interval, every time), how close
/// answered probes run to the timeout (case 3), what the camera's own
/// sleepStatus says on arrival (asleep→awake means we caught it waking), and
/// how long the first frame took (a cold radio start is slower than a camera
/// that was already up).
/// </summary>
internal sealed class WakeDiag
{
    public DateTime ParkedAt;
    public DateTime ArmedAt;
    public DateTime EdgeAt;
    public DateTime ConnectedAt;
    public int ProbesSinceArmed;
    public int ProbesTotal;
    public double EdgeProbeMs;
    public double SlowestAnsweredMs;
    // Unanswered probes carry as much signal as answered ones, and it is the
    // discriminator the answered-only view is missing: a probe that burned the WHOLE
    // timeout was probably a slow answer we gave up waiting for (light sleep, the
    // low-power chip fielding it), while one that failed FAST is genuine silence —
    // nobody home, i.e. really asleep. Same number, opposite conclusions.
    public int UnansweredCount;
    public double FastestUnansweredMs = double.MaxValue;
    public double SlowestUnansweredMs;
    public double? FirstFrameMs;
    public bool? SleepStatusOnArrival;   // camera's own msg 623: true = it said asleep
    public double? SleepStatusMs;
    public bool SawDetection;
    public bool Reported;

    public double SinceArmedSeconds =>
        ArmedAt == default ? -1 : (EdgeAt - ArmedAt).TotalSeconds;

    public void OnProbe(bool answered, double ms)
    {
        ProbesTotal++;
        if (ArmedAt != default) ProbesSinceArmed++;
        if (answered)
        {
            EdgeProbeMs = ms;
            if (ms > SlowestAnsweredMs) SlowestAnsweredMs = ms;
        }
        else
        {
            UnansweredCount++;
            if (ms < FastestUnansweredMs) FastestUnansweredMs = ms;
            if (ms > SlowestUnansweredMs) SlowestUnansweredMs = ms;
        }
    }

    /// <summary>How the unanswered probes failed, in words — the evidence for whether
    /// "asleep" was real. Within 15% of the timeout means we abandoned a slow answer;
    /// failing well short of it means the camera genuinely said nothing.</summary>
    public string UnansweredSummary(double probeTimeoutMs)
    {
        if (UnansweredCount == 0) return "no unanswered probes";
        string verdict = FastestUnansweredMs >= probeTimeoutMs * 0.85
            ? "all burned the full timeout, so these look like slow answers, not silence"
            : SlowestUnansweredMs < probeTimeoutMs * 0.85
                ? "all failed well short of the timeout, so this looks like genuine silence"
                : "mixed: some burned the timeout, some failed fast";
        return $"{UnansweredCount} unanswered ({FastestUnansweredMs:0}-{SlowestUnansweredMs:0}ms " +
               $"of a {probeTimeoutMs:0}ms timeout) — {verdict}";
    }

    /// <summary>The one-line verdict, written when the woken session ends.</summary>
    public string Verdict(double probeTimeoutMs)
    {
        // A detection during the session outranks every heuristic below: it is the
        // event wake-capture exists to catch, and a REAL wake's edge probe is
        // NATURALLY slow (the SoC answers while still booting — 4.9 s measured on
        // a genuine catch), so the latency check must never get to veto this.
        if (SawDetection)
            return "REAL self-wake — a detection followed, which is exactly what wake-capture is for";
        // Our own probe woke it: the answer came on the FIRST probe after arming,
        // i.e. the camera was asleep until we knocked. A real motion wake has no
        // reason to land inside that one interval.
        if (ProbesSinceArmed <= 1 && SleepStatusOnArrival != false)
            return "LIKELY OUR PROBE woke the camera (it answered the first probe after reading asleep, " +
                   "and no detection followed) — wake-capture is costing battery instead of saving it";
        // Answers were crowding the timeout, so the "asleep" reading is suspect.
        if (SlowestAnsweredMs > probeTimeoutMs * 0.6)
            return $"SUSPECT FALSE ASLEEP — answered probes ran to {SlowestAnsweredMs:0}ms against a " +
                   $"{probeTimeoutMs:0}ms timeout, so the unanswered ones may be slow answers, not sleep";
        return "INCONCLUSIVE — the wake did not follow our probe immediately, but no detection arrived either";
    }
}

internal sealed class WakeEdgeDetector
{
    /// <summary>Consecutive unanswered probes required before the camera counts
    /// as asleep. Two, 5s apart (WakeScanInterval), keeps wake detection prompt
    /// while filtering single lost packets.</summary>
    public const int MissesToArm = 2;

    private int _misses;

    /// <summary>True once the camera has been seen asleep (debounced) — the next
    /// answered probe is a wake edge.</summary>
    public bool Armed { get; private set; }

    /// <summary>Feed one probe result. Returns true exactly when a wake edge is
    /// detected: the camera answered after having been seen asleep.</summary>
    public bool OnProbe(bool answered)
    {
        if (!answered)
        {
            _misses++;
            if (_misses >= MissesToArm) Armed = true;
            return false;
        }
        if (Armed) return true;
        _misses = 0; // an answer between misses: they weren't consecutive
        return false;
    }
}
