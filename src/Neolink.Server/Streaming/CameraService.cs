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

    /// <summary>True while this source is deliberately offline so a battery camera
    /// can sleep (sleep-friendly + parked). While EVERY source of a camera says so,
    /// background pollers (camera-HTTP reads, ONVIF discovery, Wi-Fi warms) must
    /// not touch the network for it: nothing can answer, and the traffic itself
    /// keeps the camera's radio out of power-save — seen live as ping-flat runs
    /// that faked wake edges. Default: never quiet.</summary>
    bool NetworkQuiet => false;
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
    /// <summary>Battery cameras get an aggressive idle linger: every awake second
    /// costs charge. Active motion counts as demand (see
    /// <see cref="MotionDemandHold"/>), so event clips still run to their natural
    /// end before this timer starts; a viewer coming back inside these 10 s reuses
    /// the warm session anyway, so a longer linger buys nothing.</summary>
    private static readonly TimeSpan BatteryIdleGrace = TimeSpan.FromSeconds(10);
    /// <summary>Battery cameras also get a much shorter DEMAND window: a mere
    /// stream-open attempt holds a mains camera for 20 s, but on a battery model
    /// the ask either turns into an attached viewer within seconds (ViewerCount
    /// takes over) or it was a stray page load that must not keep the camera up.</summary>
    private static readonly TimeSpan BatteryDemandWindow = TimeSpan.FromSeconds(5);
    /// <summary>How long after the last ACTIVE motion push the session still counts
    /// as in-demand on a sleep-friendly camera — covers the recorder's post-roll
    /// (post_seconds, default 8) so a wake-capture clip isn't cut short, without
    /// letting the recorder hold a battery camera awake around the clock.</summary>
    private static readonly TimeSpan MotionDemandHold = TimeSpan.FromSeconds(10);
    /// <summary>Wake-capture ping cadence while the camera still reads awake or
    /// settling. ICMP is answered by the camera's Wi-Fi module without waking
    /// anything (its power-save sawtooth continues under sustained 1 s pings,
    /// measured), so even this is generous.</summary>
    private static readonly TimeSpan WakeScanInterval = TimeSpan.FromSeconds(3);
    /// <summary>Ping cadence once ARMED (camera asleep) — the SAME as unarmed, on
    /// purpose. It was 1 s (faster wake confirmation), but live runs showed flat
    /// RTT runs appearing 6–20 s after every switch to the tighter cadence — the
    /// denser traffic itself plausibly pulling the radio out of power-save and
    /// faking the wake. A steady cadence removes that confound; the three-sample
    /// confirmation lands ~9 s after the PIR, well inside the ~27 s flat window a
    /// real event measured, and the recording reaches back to our connect anyway.</summary>
    private static readonly TimeSpan ArmedScanInterval = TimeSpan.FromSeconds(3);
    /// <summary>Quiet beat after parking before the scan starts — long enough for
    /// the session teardown to drain. The scan itself is non-waking, so this no
    /// longer needs to cover the camera's whole descent into sleep.</summary>
    private static readonly TimeSpan WakeSettleWindow = TimeSpan.FromSeconds(15);
    /// <summary>Ping timeout for the wake scan. The measured power-save sawtooth
    /// tops out under 1 s; anything past 2 s is a genuine miss.</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    /// <summary>Timeout for the LEGACY transport probe (UDP discovery / TCP
    /// connect), used only when ICMP is blocked outright on the network. Generous:
    /// measured against a live Argus Solar, awake discovery answers ranged 211 ms
    /// to 1.9 s — and 4.9 s while the SoC boots from a PIR wake.</summary>
    private static readonly TimeSpan WakeProbeTimeout = TimeSpan.FromSeconds(6);
    /// <summary>Cadence of the legacy transport probe on ICMP-blocked networks —
    /// sparse, because unlike ping it CAN disturb a sleeping camera.</summary>
    private static readonly TimeSpan LegacyProbeInterval = TimeSpan.FromSeconds(60);

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
    // Consecutive wake-scan connects that caught nothing (no detection during
    // the session): each one raises the next park's arming threshold and settle
    // (see WakeRttDetector.ArmThreshold), so misreading an idle-awake camera as
    // asleep can't become a connect loop that never lets it sleep. A real catch
    // resets. Capped so a real-but-quiet camera is never locked out for good.
    private int _fruitlessWakes;
    // Capped LOW: at cap the scan needs 32 clean samples (~96 s) + a 45 s settle,
    // so re-arming is never minutes away — a REAL event during a skeptical park
    // (missed live, 2026-07-22, cap was 4 = up to 6.4 min unarmed) costs footage
    // that skepticism is not entitled to spend.
    private const int MaxWakeSkepticism = 2; // 8→32 samples, 15→45 s settle
    private double _lastProbeMs;
    private bool _wakeClipStarted; // one wake clip per session, on the first keyframe
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

    /// <summary>Leave the network alone for this camera (see ILiveCameraSource).</summary>
    public bool NetworkQuiet => _parked && SleepFriendly;

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
    // everything else streams around the clock (the pre-battery behavior). A live
    // keep-alive window suspends dozing entirely (see KeepAliveActive).
    private bool AllowSleep => _demandHub != null && !(_config.AlwaysOn ?? !_batteryPowered) && !KeepAliveActive;

    private readonly DateTime _serviceStartUtc = DateTime.UtcNow;

    /// <summary>User chose to hold this battery camera awake: while the keep-alive
    /// window is open (keep_alive_hours after startup, capped at 24h so it can never
    /// drain forever), the camera never dozes and every event is caught live. This is
    /// the deliberate, warned battery cost — the reliable alternative to the
    /// best-effort non-waking wake-scan.</summary>
    private bool KeepAliveActive =>
        _config.KeepAliveHours > 0
        && DateTime.UtcNow - _serviceStartUtc < TimeSpan.FromHours(_config.KeepAliveHours);

    /// <summary>True when this camera is one Neolink lets doze — a battery model
    /// without always_on. The web UI marks these tiles and manages their viewing
    /// budget, since every second of video costs the camera charge. Only becomes
    /// true once the camera has actually reported a battery. Note this reflects the
    /// STANDING policy (battery + no always_on); a transient keep-alive window makes
    /// AllowSleep false without changing what the UI should badge.</summary>
    public bool SleepFriendly => _demandHub != null && !(_config.AlwaysOn ?? !_batteryPowered);

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
    // Sleep-friendly battery cameras use the short ask-window: the ask either turns
    // into an attached viewer within seconds or must not hold the camera awake.
    private bool DemandNow => _demandHub == null
        || _demandHub.ViewerCount > 0
        || DateTime.UtcNow - _demandHub.LastViewerAskUtc < (SleepFriendly ? BatteryDemandWindow : DemandWindow);

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
                        // The scan is ICMP RTT-PATTERN based (field data, Argus Solar):
                        // a sleeping camera's Wi-Fi module answers ping through a
                        // power-save sawtooth (150–950 ms climbing ramps, the radio
                        // napping between beacon windows), while an awake main
                        // processor answers dead flat at 2–15 ms. Single samples
                        // overlap — mid-sleep dips like 37/52/60 ms happen — but a
                        // RUN of fast answers only ever appears when the camera is
                        // truly up. Radios that power off entirely just stop
                        // answering, and misses arm the detector the same way.
                        int skepticism = Math.Min(_fruitlessWakes, MaxWakeSkepticism);
                        var rtt = new WakeRttDetector
                        {
                            ArmThreshold = WakeRttDetector.NonFastToArm << skepticism,
                        };
                        var nextProbe = DateTime.UtcNow + WakeSettleWindow * (1 + skepticism);
                        var diag = new WakeDiag { ParkedAt = DateTime.UtcNow, NonWakingScan = true };
                        bool sawAnyReply = false;
                        var lastLegacyProbe = DateTime.MinValue;
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
                                         $"{(DateTime.UtcNow - diag.ParkedAt).TotalSeconds:0}s parked " +
                                         "(the camera did not wake on its own)");
                                break;
                            }
                            if (DateTime.UtcNow < nextProbe) continue;
                            // One steady cadence, armed or not: tightening on arm
                            // correlated with fake flat runs (see ArmedScanInterval).
                            nextProbe = DateTime.UtcNow + (rtt.Armed ? ArmedScanInterval : WakeScanInterval);
                            double? ms = await PingRttAsync(ct).ConfigureAwait(false);
                            if (ms != null) sawAnyReply = true;
                            diag.OnProbe(ms != null, ms ?? PingTimeout.TotalMilliseconds);
                            bool armedBefore = rtt.Armed;
                            bool woke = rtt.OnSample(ms);
                            Log.Debug($"{Tag}: wake scan {(ms is { } v ? $"{v:0}ms" : "no reply")} " +
                                      $"(armed={rtt.Armed})");
                            if (rtt.Armed && !armedBefore)
                            {
                                diag.ArmedAt = DateTime.UtcNow;
                                Log.Info($"{Tag}: camera is asleep (ping settled into the power-save pattern) — " +
                                         "armed to connect on its next self-wake");
                            }
                            if (woke)
                            {
                                diag.EdgeAt = DateTime.UtcNow;
                                _wakeDiag = diag;
                                Log.Info($"{Tag}: camera woke itself — connecting to catch the event " +
                                         $"(ping fell from the sleep sawtooth to a flat {ms:0}ms run, " +
                                         $"{diag.SinceArmedSeconds:0}s after it read asleep)");
                                break;
                            }
                            // ICMP blocked on this network (no ping has EVER answered
                            // this park): the pattern scan is blind, so fall back to
                            // the legacy transport probe — sparse, because unlike ping
                            // it CAN disturb a sleeping camera. Its answer after an
                            // all-miss park is the old-style wake edge.
                            if (rtt.Armed && !sawAnyReply
                                && DateTime.UtcNow - lastLegacyProbe > LegacyProbeInterval)
                            {
                                lastLegacyProbe = DateTime.UtcNow;
                                if (await LegacyProbeAwakeAsync(ct).ConfigureAwait(false))
                                {
                                    diag.EdgeAt = DateTime.UtcNow;
                                    diag.NonWakingScan = false; // this edge came from a probe that can wake
                                    _wakeDiag = diag;
                                    Log.Info($"{Tag}: camera answered the transport probe after an all-silent park " +
                                             $"({(ResolveProbeTarget() == null ? "no address known yet to ping — the first connect will teach it" : "ICMP appears blocked here")}) — " +
                                             "connecting to catch the event");
                                    break;
                                }
                            }
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

    // Where the camera was last seen (from a prior connect) — the ICMP scan target
    // for a UID-only camera that has no configured host address.
    private System.Net.IPAddress? _lastCameraIp;
    private bool _probeNoTargetLogged;

    /// <summary>
    /// Non-waking liveness sample for wake-capture: one ICMP ping, returning the
    /// round-trip in milliseconds, or null when it went unanswered. A ping never
    /// reaches the camera's main processor — the Wi-Fi module's stack answers it —
    /// so unlike a connect-probe it cannot wake or even disturb a sleeping camera
    /// (its power-save sawtooth continues under sustained 1 s pings, measured).
    /// The RTT is the signal, not the mere answer: see <see cref="WakeRttDetector"/>.
    /// </summary>
    private async Task<double?> PingRttAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var target = ResolveProbeTarget();
        if (target == null)
        {
            // UID-only camera we have never connected to yet: no address to scan
            // without a session-establishing (and therefore waking) broadcast. Skip —
            // a viewer opening the stream, or keep-alive, establishes the address.
            if (!_probeNoTargetLogged)
            {
                _probeNoTargetLogged = true;
                Log.Debug($"{Tag}: wake-scan idle — no address known yet (open the stream once, or set keep_alive_hours)");
            }
            _lastProbeMs = 0;
            return null;
        }
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(target, PingTimeout).ConfigureAwait(false);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? Math.Max(1, reply.RoundtripTime)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real shutdown — let the caller unwind
        }
        catch
        {
            return null; // unreachable / ICMP blocked — a miss, the detector's problem
        }
        finally
        {
            _lastProbeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// The LEGACY transport probe — UDP discovery hello or a bare TCP connect — used
    /// only on networks where ICMP is blocked outright (the RTT scan then never sees
    /// a single reply). It can disturb a sleeping camera, which is exactly why the
    /// ping scan replaced it as the primary; here it runs sparse and last-resort.
    /// The TCP arm doubles as the crisp check for models that accept TCP: a sleeping
    /// camera can't complete a connect that an awake one accepts instantly.
    /// </summary>
    private async Task<bool> LegacyProbeAwakeAsync(CancellationToken ct)
    {
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
    }

    /// <summary>The address the ICMP scan pings: the configured host if any, else the
    /// IP discovery found on the last connection (UID-only cameras).</summary>
    private System.Net.IPAddress? ResolveProbeTarget()
    {
        if (!string.IsNullOrWhiteSpace(_config.Host)
            && System.Net.IPAddress.TryParse(_config.Host, out var literal))
            return literal;
        return _lastCameraIp;
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

        // Remember where discovery found it, so the non-waking liveness scan (ICMP)
        // has an address for a UID-only camera that has no configured host.
        if (camera.RemoteIp is { } ip) _lastCameraIp = ip;

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
                        _heldSince = default; // hold released — the next one counts from its own start
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
        Log.Debug($"{Tag}: self-wake recording window open ({WakeClipWindow.TotalSeconds:0}s) — " +
                  "the recorder keeps the footage only if a detection this camera's event types allow arrives");
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
    /// <summary>How long the tentative self-wake recording stays open waiting for a
    /// detection push to confirm it. Thirty seconds because the pushes are LATE:
    /// measured 25 s after the PIR on a genuine catch — a shorter window ends the
    /// tentative clip before the push lands, and the confirmed event then starts a
    /// second, beheaded clip missing the wake footage this feature exists to keep.</summary>
    private static readonly TimeSpan WakeClipWindow = TimeSpan.FromSeconds(30);

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
            _fruitlessWakes++; // caught nothing — the next park is stricter too
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

        // Adaptive skepticism (live loop, 2026-07-21): connects on a wake edge
        // that yield NO detection — especially with the camera reporting it was
        // already up — mean the scan is misreading an idle-awake camera's radio
        // power-save as sleep, and every such connect re-wakes it: left alone,
        // a self-sustaining loop that never lets the camera doze. Each fruitless
        // cycle makes the next park's arming stricter and its settle longer;
        // one real catch resets to full sensitivity.
        if (d.SawDetection)
        {
            if (_fruitlessWakes > 0)
                Log.Info($"{Tag}: real catch — wake-scan skepticism reset");
            _fruitlessWakes = 0;
        }
        else
        {
            _fruitlessWakes++;
            int shift = Math.Min(_fruitlessWakes, MaxWakeSkepticism);
            Log.Info($"{Tag}: that self-wake connect caught nothing ({_fruitlessWakes} in a row" +
                     $"{(d.SleepStatusOnArrival == false ? "; the camera was already up" : "")}) — " +
                     $"being more skeptical: the next park needs " +
                     $"{WakeRttDetector.NonFastToArm << shift} uninterrupted sleep-pattern samples " +
                     $"and settles {(WakeSettleWindow * (1 + shift)).TotalSeconds:0}s before scanning, " +
                     "so the camera actually gets to fall asleep");
        }
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
///   3. it was never asleep — probes timed out on a camera that answers
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
    /// <summary>True while the edge came from the ICMP RTT scan, which cannot wake
    /// or disturb the camera — the "our probe woke it" verdict is then impossible
    /// by construction. Cleared if the legacy transport probe produced the edge.</summary>
    public bool NonWakingScan;

    public double SinceArmedSeconds =>
        ArmedAt == default ? -1 : (EdgeAt - ArmedAt).TotalSeconds;

    public void OnProbe(bool answered, double ms)
    {
        ProbesTotal++;
        if (ArmedAt != default) ProbesSinceArmed++;
        if (answered)
        {
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
        // reason to land inside that one interval. IMPOSSIBLE for the ICMP RTT
        // scan (pings don't reach the main processor) — only the legacy transport
        // probe, on ICMP-blocked networks, can still earn this verdict.
        if (!NonWakingScan && ProbesSinceArmed <= 1 && SleepStatusOnArrival != false)
            return "LIKELY OUR PROBE woke the camera (it answered the first probe after reading asleep, " +
                   "and no detection followed) — wake-capture is costing battery instead of saving it";
        // Answers were crowding the timeout, so the "asleep" reading is suspect.
        // RTT-scan parks judge by pattern, not timeout, so this only means
        // something for the legacy transport probe.
        if (!NonWakingScan && SlowestAnsweredMs > probeTimeoutMs * 0.6)
            return $"SUSPECT FALSE ASLEEP — answered probes ran to {SlowestAnsweredMs:0}ms against a " +
                   $"{probeTimeoutMs:0}ms timeout, so the unanswered ones may be slow answers, not sleep";
        return "INCONCLUSIVE — the wake did not follow our probe immediately, but no detection arrived either";
    }
}

/// <summary>
/// The wake-capture sleep→wake edge, read from PING ROUND-TRIP PATTERNS.
/// Field data (Argus Solar, 1 s pings): a sleeping camera's Wi-Fi module answers
/// through a power-save sawtooth — RTTs climbing 150→950 ms as the radio naps
/// between beacon windows — while an awake main processor answers dead flat at
/// 2–15 ms. Single samples overlap (mid-sleep dips of 37/52/60 ms occur, and an
/// awake camera can hiccup high), so no single ping means anything; the PATTERN
/// does: it arms after a sustained run of non-fast samples (sleep), and fires
/// only on <see cref="FastToFire"/> CONSECUTIVE fast answers, a run that in the
/// captures only ever appears when the camera is truly up. Misses count as
/// non-fast, so radios that power off entirely arm the same way — and their
/// first fast answers after silence fire the same edge.
/// </summary>
internal sealed class WakeRttDetector
{
    /// <summary>An answer at or above LAN-flat speed. Awake answers measure
    /// 2–15 ms; 50 leaves jitter margin without admitting the sawtooth.</summary>
    public const double FastMs = 50;
    /// <summary>Non-fast samples in a row before the camera counts as asleep —
    /// ~24 s of sustained power-save pattern at the 3 s scan cadence. A single
    /// fast dip resets it, which only delays arming; sleep lasts minutes.</summary>
    public const int NonFastToArm = 8;
    /// <summary>Consecutive fast answers that mean "the main processor is up".
    /// Three: isolated dips never chain, real wakes hold flat for many samples.</summary>
    public const int FastToFire = 3;

    private int _nonFast;
    private int _fastRun;

    /// <summary>Arming threshold for THIS park. Defaults to NonFastToArm; the
    /// probe loop raises it after fruitless self-wake connects (adaptive
    /// skepticism, live loop 2026-07-21): an IDLE-AWAKE camera's radio power-save
    /// also pings as a sawtooth, so right after a session the scan can read
    /// "asleep" on a camera that never dozed, connect on its next housekeeping
    /// flat, and re-wake it — a loop that never lets it sleep. Requiring a much
    /// longer uninterrupted pattern breaks the loop: a truly sleeping camera
    /// passes anyway (sleep lasts hours), an idle-awake one keeps interrupting
    /// the count with flats and never arms, so it finally gets to doze off.</summary>
    public int ArmThreshold { get; init; } = NonFastToArm;

    /// <summary>True once the sleep pattern has been established — a fast run
    /// after this is a wake edge.</summary>
    public bool Armed { get; private set; }

    /// <summary>Feed one scan sample (RTT in ms, or null for a miss). Returns
    /// true exactly when the wake edge is detected.</summary>
    public bool OnSample(double? rttMs)
    {
        bool fast = rttMs is { } ms && ms < FastMs;
        if (!fast)
        {
            _fastRun = 0;
            if (++_nonFast >= ArmThreshold) Armed = true;
            return false;
        }
        _nonFast = 0;
        return ++_fastRun >= FastToFire && Armed;
    }
}
