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
    /// <summary>How long a sleep-friendly stream keeps running after the last viewer leaves.</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);
    /// <summary>Wake-capture: how often to check whether a sleeping camera has woken.</summary>
    private static readonly TimeSpan WakeScanInterval = TimeSpan.FromSeconds(5);
    /// <summary>Probe-free window after parking: the camera needs quiet to doze off,
    /// and our own discovery probes would reset its idle timer.</summary>
    private static readonly TimeSpan WakeSettleWindow = TimeSpan.FromSeconds(90);
    /// <summary>Probe cadence while the camera is still awake after our release
    /// (sibling stream, Reolink app, slow dozer) — sparse on purpose.</summary>
    private static readonly TimeSpan AwaitSleepInterval = TimeSpan.FromSeconds(30);
    /// <summary>Wake-capture liveness probe timeout — short so a sleeping camera fails fast.</summary>
    private static readonly TimeSpan WakeProbeTimeout = TimeSpan.FromSeconds(2);

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
    private volatile int _sirenOn = -1;    // from msg 547 pushes: -1 unknown, 0 off, 1 on
    private volatile int _privacyOn = -1;  // from msg 623 pushes: -1 unknown, 0 off, 1 on
    private volatile bool _privacyLoop;    // dark + reconnecting: log it once, then quiet the churn
    private volatile bool _parked;
    private volatile bool _suspended;      // user pressed "suspend": hold no connection at all
    private volatile CancellationTokenSource? _activeStream; // the live session's CTS, to interrupt on suspend
    private bool _sleepHintLogged;
    private bool _scanLogged; // "wake-capture watching" said once
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
    public IBcCamera? LiveCamera => _live;

    /// <summary>True once the camera has answered a battery query (battery model).</summary>
    public bool BatteryPowered => _batteryPowered;

    /// <summary>Latest battery reading (login query + msg 252 pushes), or null.</summary>
    public BatteryPush? Battery => _battery;

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

    /// <summary>
    /// Set at wiring when NO recorder consumes this camera's frames. With nothing
    /// recording, frames have no consumer unless a viewer is attached — so the
    /// session holds a control-only connection (sensors, detections and controls
    /// stay live) and subscribes video only while someone watches. See
    /// <see cref="MediaOnDemandPolicy"/> for who opts in.
    /// </summary>
    public bool MediaOnDemand { get; set; }

    // Wired cameras with default power settings only: an explicit always_on
    // keeps the old always-streaming behavior, and sleep-friendly battery
    // cameras have stronger medicine (the full park above).
    private bool MediaOnDemandActive =>
        _demandHub != null && MediaOnDemand && MediaOnDemandPolicy(false, _config.AlwaysOn, _batteryPowered);

    /// <summary>On-demand video applies iff nothing records the camera, always_on
    /// is unset (an explicit choice either way wins), and it isn't battery powered
    /// (those park the whole connection instead).</summary>
    internal static bool MediaOnDemandPolicy(bool recorderWired, bool? alwaysOn, bool batteryPowered) =>
        !recorderWired && alwaysOn == null && !batteryPowered;

    // Demand = someone is watching, or recently tried to start watching (viewers
    // only subscribe once video is ready, so the DESCRIBE attempt is the wake call).
    private bool DemandNow => _demandHub == null
        || _demandHub.ViewerCount > 0
        || DateTime.UtcNow - _demandHub.LastViewerAskUtc < DemandWindow;

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
                    if (_config.WakeCapture)
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
                        // sparse probes until it is seen ASLEEP once, and only a
                        // reachable answer AFTER that means "the camera woke itself".
                        if (!_scanLogged)
                        {
                            _scanLogged = true;
                            Log.Info($"{Tag}: sleep-friendly (wake-capture) — letting the camera doze; " +
                                     "will connect when it wakes itself (motion) or a viewer opens the stream");
                        }
                        bool sawAsleep = false;
                        var nextProbe = DateTime.UtcNow + WakeSettleWindow;
                        while (!DemandNow)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                            if (DemandNow) break;
                            if (DateTime.UtcNow < nextProbe) continue;
                            if (!await ProbeAwakeAsync(ct).ConfigureAwait(false))
                            {
                                if (!sawAsleep)
                                {
                                    sawAsleep = true;
                                    Log.Info($"{Tag}: camera is asleep — armed to connect on its next self-wake");
                                }
                                nextProbe = DateTime.UtcNow + WakeScanInterval;
                            }
                            else if (sawAsleep)
                            {
                                Log.Info($"{Tag}: camera woke itself — connecting to catch the event");
                                break;
                            }
                            else
                            {
                                // Still awake since our release (a sibling stream, the
                                // Reolink app, or a slow dozer): wait for real sleep,
                                // probing sparsely so we never hold it awake ourselves.
                                nextProbe = DateTime.UtcNow + AwaitSleepInterval;
                            }
                        }
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
    /// else). Never throws and never logs — it runs every few seconds while the
    /// camera sleeps, and a sleeping camera simply doesn't answer.
    /// </summary>
    private async Task<bool> ProbeAwakeAsync(CancellationToken ct)
    {
        try
        {
            if (_config.Udp)
                return await UdpDiscovery.IsReachableAsync(_config.Host, _config.Uid!, WakeProbeTimeout, ct)
                    .ConfigureAwait(false);

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
            ? $"{Tag}: connecting over UDP to {_config.Host} (uid set)"
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
                case BatteryPush b: _battery = b; break;
                case SirenStatusPush s: _sirenOn = s.On ? 1 : 0; break;
                case SleepStatusPush sl: _privacyOn = sl.Sleeping ? 1 : 0; break;
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
            if (MediaOnDemandActive && !DemandNow)
            {
                Log.Info($"{Tag}: idle — nothing recording and nobody watching; holding a control-only " +
                         "connection (sensors and controls stay live, video starts when someone watches)");
                while (!DemandNow)
                    await Task.Delay(250, linked.Token).ConfigureAwait(false);
                Log.Info($"{Tag}: viewer asked — starting the video stream");
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
                }
                onFrame();

                switch (frame)
                {
                    case MediaInfo info:
                        _hub.PublishInfo(info);
                        break;
                    case VideoFrame video:
                        _hub.PublishVideo(video);
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
                // needs an explicit always_on.
                if (AllowSleep)
                {
                    if (DemandNow)
                    {
                        idleSince = null;
                    }
                    else
                    {
                        idleSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - idleSince > IdleGrace)
                        {
                            Log.Info($"{Tag}: no viewers for {IdleGrace.TotalSeconds:0}s — " +
                                     "disconnecting so the battery camera can sleep");
                            return true;
                        }
                    }
                }
                // On-demand video (nothing records this camera): once the last
                // viewer has been gone a while, drop the whole session and let the
                // reconnect settle into the control-only hold above. A reconnect —
                // not just cancelling the subscription — because the camera keeps
                // pushing video until told otherwise, and a fresh login is the one
                // way every firmware provably stops.
                else if (MediaOnDemandActive)
                {
                    if (DemandNow)
                    {
                        idleSince = null;
                    }
                    else
                    {
                        idleSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - idleSince > IdleGrace)
                        {
                            Log.Info($"{Tag}: no viewers for {IdleGrace.TotalSeconds:0}s — " +
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
            // Not battery-powered (or it declined to say) — treat as mains.
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
