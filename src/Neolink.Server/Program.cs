// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Reflection;
using Neolink;
using Neolink.Config;
using Neolink.Mqtt;
using Neolink.Protocol;
using Neolink.Recording;
using Neolink.Rtsp;
using Neolink.Streaming;
using Neolink.Web;

// Single source of truth: <Version> in Neolink.Server.csproj. Release builds
// override it with the git tag (docker.yml passes -p:Version), so the reported
// version increments with every release without touching code. The build may
// append "+<commit>" to the informational version — not for human eyes.
string Version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "0.0.0";

var argList = args.ToList();
string? command = null;
string? configPath = null;

for (int i = 0; i < argList.Count; i++)
{
    var a = argList[i];
    switch (a)
    {
        case "--help" or "-h":
            PrintHelp();
            return 0;
        case "--version" or "-V":
            Console.WriteLine($"neolink.net {Version}");
            return 0;
        case "--verbose" or "-v":
            Log.Level = Neolink.LogLevel.Debug;
            break;
        case "--config" or "-c":
            if (i + 1 >= argList.Count) return Fail("--config requires a path");
            configPath = argList[++i];
            break;
        case var s when s.StartsWith("--config="):
            configPath = s["--config=".Length..];
            break;
        case "rtsp" or "selftest" or "probe":
            command = a;
            break;
        default:
            return Fail($"Unknown argument: {a}");
    }
}

command ??= "rtsp";

if (command == "selftest")
    return SelfTest.Run(configPath) ? 0 : 1;

if (configPath == null)
{
    // Convenience: look for config.json in the working directory, then next to the executable
    var candidates = new[]
    {
        "config.json", "config.toml",
        Path.Combine(AppContext.BaseDirectory, "config.json"),
        Path.Combine(AppContext.BaseDirectory, "config.toml"),
    };
    configPath = candidates.FirstOrDefault(File.Exists);
    if (configPath == null)
        return Fail("Missing --config <path-to-config.json> (no config.json found in the current directory or next to the executable)");
    Log.Info($"Using config file: {Path.GetFullPath(configPath)}");
}

// First run with an explicit --config path that doesn't exist yet (the Docker /
// Unraid / add-on case): write a commented starter config so the container boots
// straight to the web UI instead of crash-looping on a missing file. The user
// edits it to add cameras and restarts. Only reached with an explicit --config
// path — the working-directory search above returns only files that exist.
if (!File.Exists(configPath))
{
    try
    {
        var full = Path.GetFullPath(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, StarterConfig());
        Log.Info($"No config file found — wrote a starter config to {full}. " +
                 "Edit it to add your cameras (see the comments inside), then restart.");
    }
    catch (Exception ex)
    {
        return Fail($"No config at '{configPath}', and a starter could not be created there: {ex.Message}");
    }
}

NeolinkConfig config;
try
{
    config = NeolinkConfig.Load(configPath);
}
catch (Exception ex)
{
    return Fail($"Failed to load config '{configPath}': {ex.Message}");
}

// On-demand camera-discovery diagnostic: run the sweep once, now, for every
// Baichuan camera, then exit. This is the way to probe a battery camera —
// wake it (motion or the Reolink app), run this immediately, and the report
// lands while it is awake, instead of waiting for the background sweep's next
// 15-minute window to happen to coincide with a wake. Add --verbose for the
// full ability-table dump.
if (command == "probe")
{
    var targets = config.Cameras.Where(c => !c.IsGenericRtsp).ToList();
    if (targets.Count == 0)
        return Fail("No Baichuan cameras in the config to probe (generic RTSP cameras are skipped)");
    using var probeCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; probeCts.Cancel(); };
    foreach (var cam in targets)
    {
        try { await CameraProbe.SweepAsync($"{cam.Name} (probe)", cam, probeCts.Token); }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Log.Warn($"{cam.Name} (probe): [discover] sweep failed: {Log.Flatten(ex)}"); }
    }
    return 0;
}

// Capture log lines from here on for the web UI's admin log stream — created
// before anything interesting happens so startup lines are in the backlog.
var logBuffer = new Neolink.Web.LogBuffer();
Log.Tap = logBuffer.Publish;

Log.Info($"Neolink.NET {Version} starting");
var tzOffset = DateTimeOffset.Now.Offset;
Log.Info($"Local time: {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
         $"({TimeZoneInfo.Local.Id}, UTC{(tzOffset < TimeSpan.Zero ? "-" : "+")}{tzOffset:hh\\:mm}) — " +
         "set the TZ env var to change it");

// Server-side UI state (accounts, runtime settings) lives in state_dir — the
// config directory unless relocated (e.g. because the config mount is read-only).
var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
var stateDir = config.Ui.StateDir ?? configDir;
try
{
    Directory.CreateDirectory(stateDir);
}
catch (Exception ex)
{
    return Fail($"ui.state_dir '{stateDir}' is unusable: {ex.Message}");
}
if (stateDir != configDir)
    Log.Info($"UI state directory: {stateDir}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!shutdown.IsCancellationRequested)
    {
        Log.Info("Shutting down...");
        try { shutdown.Cancel(); } catch (ObjectDisposedException) { }
    }
};
// ProcessExit also fires on exits WE initiated (Ctrl+C, the UI's restart
// button) — by then Main has finished and the `using` has disposed the CTS,
// so Cancel would throw ObjectDisposedException into the exit handler.
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { shutdown.Cancel(); } catch (ObjectDisposedException) { }
};

var users = config.Users.ToDictionary(u => u.Name, u => u.Pass);
if (users.Count > 0)
    Log.Warn("RTSP is unencrypted: usernames and passwords are exchanged in plaintext.");

var server = new RtspServer(users);
var tasks = new List<Task>();
var webCameras = new List<WebCameraInfo>();

// MQTT / Home Assistant bridge is created after the cameras are built (below) so
// it can reference their controls; declared here so motion wiring can reach it.
HomeAssistantMqtt? mqtt = null;

// Recording (detection events + continuous), when a storage path is configured.
EventStore? eventStore = null;
RecordingSettings? recordingSettings = null;
StorageLocations? storage = null;
if (config.Recording is { } recCfg)
{
    try
    {
        storage = new StorageLocations(recCfg);
        eventStore = new EventStore(storage.MainRoot,
            storage.HasClipsTier ? storage.ClipsRoot : null, storage.ArchiveRoot);
        eventStore.Load();
        // Runtime switches live in the state dir; older locations (config dir,
        // recordings root) are checked once for migration.
        recordingSettings = new RecordingSettings(stateDir, configDir, eventStore.Root);
        Log.Info($"Recording to {eventStore.Root} " +
                 $"(default retention — events: {(recCfg.RetentionDays > 0 ? $"{recCfg.RetentionDays} days" : "forever")}, " +
                 $"continuous: {(recCfg.EffectiveContinuousRetentionDays > 0 ? $"{recCfg.EffectiveContinuousRetentionDays} days" : "forever")}; " +
                 "per-camera overrides apply)");
        if (storage.HasClipsTier)
            Log.Info($"Recording: event clips go to the fast tier at {storage.ClipsRoot}");
        if (storage.ArchiveRoot is { } archRoot)
            Log.Info($"Recording: archive tier at {archRoot} — cameras opt in from the web UI");
        // Catch the common Docker footgun where separate tier paths silently land
        // on the same (root) filesystem because their bind mounts never attached.
        foreach (var warn in storage.SharedVolumeWarnings())
            Log.Warn($"Storage: {warn}");
        // Per-camera lifecycle: the cleanup pass sees storage-directory names, so map
        // them back to camera names to look up each camera's settings. Archiving is
        // a per-camera opt-in and needs the archive tier to exist.
        var store = eventStore;
        var settings = recordingSettings;
        bool hasArchive = storage.HasArchiveTier;
        var camerasByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in config.Cameras)
            camerasByDir.TryAdd(EventStore.SafeName(c.Name), c.Name);
        EventStore.CameraStoragePolicy PolicyFor(string dirName)
        {
            var name = camerasByDir.TryGetValue(dirName, out var n) ? n : dirName;
            var s = settings.Get(name);
            return new EventStore.CameraStoragePolicy(
                s.EventRetentionDays ?? recCfg.RetentionDays,
                s.ContinuousRetentionDays ?? recCfg.EffectiveContinuousRetentionDays,
                ArchiveEvents: hasArchive && s.ArchiveEvents,
                ArchiveContinuous: hasArchive && s.ArchiveContinuous,
                ArchiveDeleteDays: s.ArchiveRetentionDays ?? 0);
        }
        tasks.Add(Task.Run(() => store.RunRetentionAsync(PolicyFor, shutdown.Token)));
    }
    catch (Exception ex)
    {
        Log.Error($"Recording disabled: cannot use storage path '{recCfg.Path}': {ex.Message}");
        eventStore = null;
        storage = null;
    }
}

// Free-space trend per storage location (persisted in the state dir): feeds the
// monitor's "~N days until full at the current rate" forecast.
StorageForecast? storageForecast = null;
if (storage != null)
{
    storageForecast = new StorageForecast(storage, stateDir);
    tasks.Add(Task.Run(() => storageForecast.RunAsync(shutdown.Token)));
}

// Email notifications for critical alerts (opt-in, configured in the web UI).
// Fully isolated: the notifier and its alert monitor run on their own tasks and
// swallow every failure, so a mis-set or unreachable mail server can never affect
// recording, streaming or MQTT. The SMTP password is encrypted at rest.
var secretProtector = new Neolink.Notifications.SecretProtector(stateDir);
var notificationStore = new Neolink.Notifications.NotificationStore(stateDir, secretProtector);
var notifier = new Neolink.Notifications.Notifier(notificationStore, Environment.MachineName);
var recordingHealth = new Neolink.Recording.RecordingHealth();
tasks.Add(Task.Run(() => notifier.RunAsync(shutdown.Token)));

// Footage encryption (beta, opt-in via recording.encrypt): new clips, segments
// and thumbnails are written as chunked AES-256-GCM. The vault gets the key
// whenever recording exists AT ALL — even with the switch off — so footage
// recorded while it WAS on stays playable after toggling back. Reads sniff the
// format, so plaintext and encrypted footage live side by side forever.
if (config.Recording != null)
{
    FootageVault.Configure(secretProtector.DeriveSubKey("neolink-footage-master-v1"),
        encryptNew: config.Recording.Encrypt);
    if (config.Recording.Encrypt)
    {
        Log.Info("Recording: footage encryption is ON (beta) — new footage is written " +
                 "AES-256-GCM; earlier plaintext footage keeps playing. Back up the key " +
                 $"({Neolink.Notifications.SecretProtector.KeyEnvVar} or the state dir's " +
                 $"{Neolink.Notifications.SecretProtector.KeyFileName}) — without it the footage is gone. " +
                 $"Key in use: {secretProtector.KeySource}, fingerprint {secretProtector.Fingerprint}.");
        // Encrypting footage with a key that lives ON the footage disk protects
        // nothing against that disk being stolen — say so where it will be seen.
        if (secretProtector is { KeySource: "file", KeyFile: { } keyFile }
            && storage != null && storage.SharesVolumeWith(keyFile, out var keyTier))
            Log.Warn($"Footage encryption: the key file ({keyFile}) sits on the SAME disk as the " +
                     $"{keyTier} storage — a stolen disk would carry its own key. Move ui.state_dir " +
                     $"to a different disk, or provide the key via " +
                     $"{Neolink.Notifications.SecretProtector.KeyEnvVar} so it never touches disk.");
        if (secretProtector.KeySource == "ephemeral")
            Log.Warn("Footage encryption: the key is EPHEMERAL (unwritable state dir) — footage " +
                     "encrypted this run becomes UNREADABLE after a restart. Fix the state dir now.");
    }
}

// Motion pushes fan out to the recorder and/or the MQTT bridge; wired after both
// exist (the bridge needs the camera list, built below).
var motionTargets = new List<(IReadOnlyList<CameraService> Services, string Name, Action<MotionPush>? RecorderSink)>();

// Per-camera runtime state that survives restarts (today: the SUSPEND flag —
// Neolink holds no connection to the camera, so it can't be viewed or recorded).
// Shared by the web API / MQTT bridge (which toggle it) and applied at startup.
var cameraState = new Neolink.Web.CameraStateStore(stateDir);

foreach (var cam in config.Cameras)
{
    var permitted = config.PermittedUsersFor(cam);
    var webStreams = new List<WebStreamInfo>();
    ICameraControl control;
    CameraService? primaryService = null;
    var camServices = new List<CameraService>();
    var pullServices = new List<RtspCameraService>();

    if (cam.IsGenericRtsp)
    {
        // Generic (non-Reolink) camera: pull its RTSP URL(s) into hubs. It streams
        // and records, but has no Baichuan control surface and no motion pushes.
        int rtspIndex = 0;
        void AddRtsp(string url, string suffix, bool alsoRoot)
        {
            var hub = new StreamHub($"{cam.Name} {suffix}");
            server.AddMount(new RtspMount { Path = $"/{cam.Name}/{suffix}", Hub = hub, PermittedUsers = permitted });
            if (alsoRoot)
                server.AddMount(new RtspMount { Path = $"/{cam.Name}", Hub = hub, PermittedUsers = permitted });
            webStreams.Add(new WebStreamInfo(suffix, $"/{cam.Name}/{suffix}", hub));
            var service = new RtspCameraService($"{cam.Name} {suffix}", url, hub,
                TimeSpan.FromSeconds(2 * rtspIndex++));
            service.SetSuspended(cameraState.Suspended(cam.Name)); // restore persisted suspend
            pullServices.Add(service);
            tasks.Add(Task.Run(() => RunRtspGuardedAsync(service, $"{cam.Name} ({suffix})", shutdown.Token)));
        }
        if (cam.RtspMain != null) AddRtsp(cam.RtspMain, "mainStream", alsoRoot: true);
        if (cam.RtspSub != null) AddRtsp(cam.RtspSub, "subStream", alsoRoot: cam.RtspMain == null);
        control = new GenericCameraControl(cam.Name, pullServices);
    }
    else
    {
        bool main = cam.Stream is "both" or "all" or "mainStream";
        bool sub = cam.Stream is "both" or "all" or "subStream";
        bool ext = cam.Stream is "all" or "externStream";

        // Stagger the streams of one camera by 2s each so their logins don't collide.
        int streamIndex = 0;
        var camMounts = new List<RtspMount>();
        void AddStream(StreamKind kind, string suffix, bool alsoRoot)
        {
            var hub = new StreamHub($"{cam.Name} {suffix}");
            var mount = new RtspMount { Path = $"/{cam.Name}/{suffix}", Hub = hub, PermittedUsers = permitted };
            server.AddMount(mount);
            camMounts.Add(mount);
            if (alsoRoot)
            {
                var rootMount = new RtspMount { Path = $"/{cam.Name}", Hub = hub, PermittedUsers = permitted };
                server.AddMount(rootMount);
                camMounts.Add(rootMount);
            }
            webStreams.Add(new WebStreamInfo(suffix, $"/{cam.Name}/{suffix}", hub));
            var service = new CameraService(cam, kind, hub, TimeSpan.FromSeconds(2 * streamIndex++));
            service.SetSuspended(cameraState.Suspended(cam.Name)); // restore persisted suspend
            primaryService ??= service;
            camServices.Add(service);
            tasks.Add(Task.Run(() => RunCameraGuardedAsync(service, $"{cam.Name} ({kind})", shutdown.Token)));
        }

        // The bare /name mount points at the "best" configured stream
        if (main) AddStream(StreamKind.Main, "mainStream", alsoRoot: true);
        if (sub) AddStream(StreamKind.Sub, "subStream", alsoRoot: !main);
        if (ext) AddStream(StreamKind.Extern, "externStream", alsoRoot: !main && !sub);

        // Control commands (capabilities, PTZ, LED, ...) ride the primary stream's
        // connection — cameras have session limits, so no extra login is spent.
        // Some controls (stream encode writes, white-LED brightness) go over the
        // camera's HTTP API. Use an explicit http_address when set, otherwise derive
        // it from the Baichuan host (same box, port 80) — UID-only cameras have no
        // host and get none. Unreachable HTTP just fails those calls gracefully.
        var httpAddr = cam.HttpAddress
            ?? (string.IsNullOrWhiteSpace(cam.Host) ? null : cam.Host);
        var httpApi = httpAddr == null ? null
            : new ReolinkHttpApi(httpAddr, cam.Username, cam.Password, cam.ChannelId);
        var primary = primaryService
            ?? throw new InvalidOperationException($"camera '{cam.Name}' has no streams");
        // All stream services: the camera is online if ANY session is live (a
        // viewer watching only the sub stream must not read as "offline"), and
        // commands fall back to whichever session exists.
        control = new CameraControl(primary, httpApi, camServices);

        // Two-way talk is opt-in (ui.talk). When on, this camera's RTSP mounts can
        // serve an ONVIF audio backchannel (go2rtc / HA WebRTC) — DESCRIBE still
        // gates the SDP track on the camera actually having a speaker.
        if (config.Ui.Talk)
            foreach (var m in camMounts) m.Talk = control;
    }
    if (webStreams.Count == 0)
        throw new InvalidOperationException($"camera '{cam.Name}' has no streams");
    Action<MotionPush>? recorderSink = null;
    EventRecorder? eventRecorder = null;
    ContinuousRecorder? continuousRecorder = null;

    // Recording: alarm pushes ride the primary connection; event clips and
    // continuous segments are cut from the configured stream's hub (auto = main
    // when served, else the first stream). The per-camera on/off switches live in
    // RecordingSettings and are flipped from the web UI at runtime; the config's
    // "record" flag only seeds the initial events default.
    if (eventStore != null && recordingSettings != null)
    {
        var recordStream = config.Recording!.Stream == "auto"
            ? webStreams.FirstOrDefault(s => s.Kind == "mainStream") ?? webStreams[0]
            : webStreams.FirstOrDefault(s => s.Kind == config.Recording.Stream);
        if (recordStream == null)
        {
            Log.Warn($"{cam.Name}: recording.stream '{config.Recording.Stream}' is not served " +
                     $"by this camera (stream = \"{cam.Stream}\"); recording disabled for it");
        }
        else
        {
            // Generic RTSP cameras have no detection pushes, so event recording can
            // never trigger — only continuous (24/7) applies to them.
            recordingSettings.Seed(cam.Name, eventsDefault: cam.Record && !cam.IsGenericRtsp);
            // The user can retarget recording to another served stream at runtime
            // (per camera, from the web UI); the config stream stays the default.
            var hubsByKind = webStreams.ToDictionary(s => s.Kind, s => s.Hub, StringComparer.Ordinal);
            if (!cam.IsGenericRtsp)
            {
                // Strip previews are cut from the sub stream when it is served and
                // differs from the recording stream (no point recording twice).
                var previewStream = webStreams.FirstOrDefault(s => s.Kind == "subStream");
                if (previewStream == recordStream) previewStream = null;
                var recorder = new EventRecorder(cam.Name, recordStream.Hub, control, eventStore,
                    config.Recording, recordingSettings, previewStream?.Hub, hubsByKind,
                    hasRoom: storage == null ? null : () => storage.HasRoom(StorageRole.Clips),
                    onWriteError: recordingHealth.MarkWriteError);
                recorderSink = recorder.OnMotion;
                eventRecorder = recorder;
                tasks.Add(Task.Run(() => recorder.RunAsync(shutdown.Token)));
            }

            // Continuous (24/7) recording is temporarily disabled — see
            // RecordingConfig.ContinuousEnabled.
            if (RecordingConfig.ContinuousEnabled)
            {
                var continuous = new ContinuousRecorder(cam.Name, recordStream.Hub, eventStore,
                    recordingSettings, config.Recording, hubsByKind,
                    hasRoom: storage == null ? null : () => storage.HasRoom(StorageRole.Main),
                    onWriteError: recordingHealth.MarkWriteError);
                continuousRecorder = continuous;
                tasks.Add(Task.Run(() => continuous.RunAsync(shutdown.Token)));
            }
        }
    }

    // Registered after the recorders so the web API can report live REC state.
    // Battery/siren/privacy readings scan every stream service (primary first —
    // camServices[0]): each session probes and receives pushes on its own, so a
    // viewer watching only the sub stream still gets fresh readings.
    var battery = primaryService;
    var readers = camServices;
    var sleepers = camServices;
    // Suspend applies to every stream of the camera (Baichuan or generic RTSP);
    // reading it back is "all streams held" (they toggle together). Persist here so
    // the API/bridge just flip one switch. Both service lists exist; only one is
    // populated per camera, so concatenating covers both kinds.
    var suspendables = camServices.Cast<object>().Concat(pullServices).ToList();
    void SetCamSuspended(bool v)
    {
        foreach (var s in camServices) s.SetSuspended(v);
        foreach (var s in pullServices) s.SetSuspended(v);
        cameraState.SetSuspended(cam.Name, v);
    }
    bool IsCamSuspended() =>
        (camServices.Count > 0 && camServices.All(s => s.Suspended))
        || (pullServices.Count > 0 && pullServices.All(s => s.Suspended));
    webCameras.Add(new WebCameraInfo(cam.Name, webStreams, control, permitted,
        ContinuousActive: continuousRecorder == null ? null : () => continuousRecorder.IsWriting,
        SupportsEvents: !cam.IsGenericRtsp,
        Battery: battery == null ? null : () => readers.Select(s => s.Battery).FirstOrDefault(b => b != null),
        // Asleep = every stream of the camera is parked on purpose (battery doze),
        // as opposed to offline-because-unreachable.
        Asleep: sleepers.Count == 0 ? null : () => sleepers.All(s => s.Parked),
        SirenOn: battery == null ? null : () => readers.Select(s => s.SirenOn).FirstOrDefault(v => v != null),
        PrivacyOn: battery == null ? null : () => readers.Select(s => s.PrivacyOn).FirstOrDefault(v => v != null),
        Suspended: suspendables.Count == 0 ? null : IsCamSuspended,
        SetSuspended: suspendables.Count == 0 ? null : SetCamSuspended,
        // The open segment from the recorder's memory — the day listing trusts
        // this over the file's mtime, which is stale while the handle is open.
        ActiveSegment: continuousRecorder == null ? null : () => continuousRecorder.ActiveSegment,
        // 24/7 recording on/off — the same persisted setting the web UI toggles;
        // the recorder reads it live, so flipping it starts/stops taping at once.
        // Offered (Baichuan and generic RTSP alike) whenever a continuous recorder runs.
        ContinuousEnabled: continuousRecorder == null || recordingSettings == null ? null
            : () => recordingSettings.Get(cam.Name).Continuous,
        SetContinuousEnabled: continuousRecorder == null || recordingSettings == null ? null
            : v => recordingSettings.Update(cam.Name, events: null, continuous: v, eventTypes: null, setEventTypes: false))
        // The recorder rides along so the web API and the MQTT bridge share one
        // on-demand recording session per camera (UI button ≡ HA Record switch).
        { EventRecorder = eventRecorder });
    if (primaryService != null)
        motionTargets.Add((camServices, cam.Name, recorderSink));
}

// Resource sampler: feeds the UI's monitor page AND the server's own Home
// Assistant device — created whenever either consumer runs. ViewerCount
// deliberately excludes the server's own recorder subscriptions — "viewers"
// means humans/externals watching, not us taping.
SystemMonitor? monitor = null;
if (config.WebPort > 0 || config.Mqtt is { StatsIntervalSeconds: > 0 })
{
    monitor = new SystemMonitor(
        diskProbePath: eventStore?.Root ?? stateDir,
        recordingsRoot: eventStore?.Root,
        viewerCount: () => webCameras.Sum(c => c.Streams.Sum(s => s.Hub.ViewerCount)),
        recordingCameras: () => webCameras.Count(c => c.ContinuousActive?.Invoke() == true),
        cameraStates: () => webCameras.Select(c => (c.Name, c.Control.Online)));
    var mon = monitor;
    tasks.Add(Task.Run(() => mon.RunAsync(shutdown.Token)));
}

// Alert monitor: polls health and feeds the notifier (storage full, sustained
// overload, cameras offline past their threshold, recording write failures).
var alertMonitor = new Neolink.Notifications.AlertMonitor(
    notifier, Environment.MachineName, storage, monitor,
    cameras: () => webCameras.Select(c => new Neolink.Notifications.CameraHealth(
        c.Name, c.Control.Online,
        // "Intentionally offline" — a dozing battery camera OR one the user
        // suspended — must not raise a camera-offline alert.
        (c.Asleep?.Invoke() ?? false) || (c.Suspended?.Invoke() ?? false))),
    recording: recordingHealth);
tasks.Add(Task.Run(() => alertMonitor.RunAsync(shutdown.Token)));

// MQTT / Home Assistant bridge (single connection for all cameras), then wire
// motion: the camera's alarm push goes to the recorder and/or the bridge. The
// alarm-push listener only runs when a MotionSink is set, so this also enables
// motion for MQTT-only setups (recording off).
if (config.Mqtt is { } mqttCfg)
{
    mqtt = new HomeAssistantMqtt(mqttCfg, webCameras, Version) { Monitor = monitor, Storage = storage };
}
foreach (var (services, name, recorderSink) in motionTargets)
{
    var bridge = mqtt;
    if (recorderSink == null && bridge == null) continue;
    // Every stream service listens (each holds its own camera session), but only
    // the LEADER — the first service with a live connection — forwards, so a
    // camera with both main and sub connected doesn't double-fire events (a
    // doorbell press must ring automations once). With only the sub stream
    // connected, its session leads and detections still flow.
    var all = services;
    CameraService? Leader() => all.FirstOrDefault(s => s.LiveCamera != null);
    foreach (var svc in services)
    {
        var self = svc;
        svc.MotionSink = push =>
        {
            if (!ReferenceEquals(self, Leader())) return;
            recorderSink?.Invoke(push);
            bridge?.OnMotion(name, push);
        };
        // Unsolicited status pushes (Wi-Fi signal, siren, floodlight) only feed the
        // bridge; the listener only runs when the sink is set.
        if (bridge != null)
            svc.StatusSink = push =>
            {
                if (ReferenceEquals(self, Leader()))
                    bridge.OnStatus(name, push);
            };
    }
}
// The reverse direction — HA's "Record" switch starting an on-demand recording —
// needs no wiring here: the bridge reaches the recorder via WebCameraInfo.
if (mqtt is { } bridgeToRun)
    tasks.Add(Task.Run(() => bridgeToRun.RunAsync(shutdown.Token)));

// Web API (camera list + fMP4 live streams for browsers); guarded like the cameras.
if (config.WebPort > 0)
{
    // Web-UI accounts (users.json in the state dir). Auth is off until the
    // first account (the admin) is created from the UI itself.
    var userStore = new UserStore(stateDir, legacyDir: configDir);
    if (!userStore.Enabled)
        Log.Info("Web UI authentication is off — create the admin account from the UI to enable it");
    if (config.EffectiveResetAdminPassword)
        Log.Warn("reset_admin_password is TRUE: anyone reaching the login page can set a new admin " +
                 "password. Set it back to false as soon as the reset is done!");

    // Daily best-effort check for newer releases (feeds the UI's update banner).
    var updates = new UpdateChecker(Version);
    tasks.Add(Task.Run(() => updates.RunAsync(shutdown.Token)));

    var webOptions = new WebApiOptions
    {
        BindAddr = config.WebBind ?? config.BindAddr,
        Port = config.WebPort,
        WebUi = config.WebUi,
        Cameras = webCameras,
        Users = users,
        RtspPort = config.BindPort,
        Events = eventStore,
        RecordingSettings = recordingSettings,
        Recording = config.Recording,
        ArchiveAvailable = storage?.HasArchiveTier ?? false,
        Storage = storage,
        Forecast = storageForecast,
        Secrets = secretProtector,
        UserStore = userStore,
        ResetAdminPassword = config.EffectiveResetAdminPassword,
        TrickleSpeed = config.Ui.TrickleSpeed,
        TalkEnabled = config.Ui.Talk,
        ShowBackgroundTasks = config.Ui.ShowBackgroundTasks,
        Version = Version,
        ConfigPath = Path.GetFullPath(configPath),
        Updates = updates,
        Monitor = monitor,
        Notifier = notifier,
        Logs = logBuffer,
        // Graceful shutdown; docker's restart policy (or systemd) starts us again.
        RestartRequested = () =>
        {
            Log.Warn("Shutting down for a UI-requested restart");
            shutdown.Cancel();
        },
        // Let a web-UI setting change reflect in Home Assistant right away, rather
        // than on the bridge's ~20s refresh, so automations don't act on a stale switch.
        OnCameraChanged = mqtt == null ? null : mqtt.RepublishCameraAsync,
    };

    tasks.Add(Task.Run(async () =>
    {
        try
        {
            await WebApi.RunAsync(webOptions, shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Web API failed (RTSP continues): {Log.Flatten(ex)}");
        }
    }));
}

// The RTSP server is the one component nothing works without: run the process
// for as long as it lives. Camera tasks are individually guarded and can never
// fault; each reconnects on its own schedule.
int exitCode = 0;
try
{
    await server.RunAsync(config.BindAddr, config.BindPort, shutdown.Token);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Log.Error($"RTSP server failed: {Log.Flatten(ex)}");
    exitCode = 1;
}

// Server is done (Ctrl+C or fatal error): wind down the camera tasks gracefully.
shutdown.Cancel();
try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException) { }

Log.Info("Goodbye");
return exitCode;

// Safety net around one camera stream: a crash in CameraService must never take
// the process (and the other cameras) down. Log it and start the service again.
static async Task RunCameraGuardedAsync(CameraService service, string tag, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await service.RunAsync(ct);
            return; // clean exit (shutdown requested)
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"{tag}: camera task crashed unexpectedly: {Log.Flatten(ex)}; restarting in 15s");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

// Same safety net for generic RTSP pulls (their own loop already retries; this
// catches anything unexpected escaping it).
static async Task RunRtspGuardedAsync(RtspCameraService service, string tag, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await service.RunAsync(ct);
            return; // clean exit (shutdown requested)
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"{tag}: RTSP pull task crashed unexpectedly: {Log.Flatten(ex)}; restarting in 15s");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}

// Written on first run when --config points at a file that doesn't exist yet, so
// a fresh container boots to the web UI instead of crash-looping. Cameras start
// empty (the app runs, the wall is empty); the user fills in a block and restarts.
// The loader accepts // comments and trailing commas, so this stays valid as-is.
static string StarterConfig() =>
    """
    {
      // Neolink.NET wrote this starter config because none existed here.
      // Add your cameras below, then restart. Full reference + all options:
      // https://github.com/borexola/neolink.net (see config.example.json)

      "bind": "0.0.0.0",
      "bind_port": 8654,   // RTSP
      "web_port": 8655,    // web UI + API
      "webui": true,

      // One entry per camera. Uncomment a block and fill in your camera's IP and
      // the same username/password you use in the Reolink app. Until you add at
      // least one, the web UI runs but shows no cameras.
      "cameras": [
        // {
        //   "name": "driveway",
        //   "username": "admin",
        //   "password": "CHANGE-ME",
        //   "address": "192.168.1.187:9000"
        // }
      ]

      // To record, add a recording block (and map a volume at the path):
      // ,"recording": { "path": "/recordings" }
    }
    """;

static void PrintHelp()
{
    Console.WriteLine(
        """
        Neolink.NET - RTSP bridge for Reolink cameras that speak the Baichuan protocol (port 9000)

        USAGE:
            neolink.net [rtsp] --config <config.json>     Serve all configured cameras over RTSP
            neolink.net selftest [--config <samples-dir>] Run built-in protocol self-tests
            neolink.net probe --config <config.json>      Run the camera-discovery diagnostic once and exit
            neolink.net --version
            neolink.net --help

        OPTIONS:
            -c, --config <PATH>   Path to the configuration file (JSON; legacy TOML also accepted)
                                  (defaults to ./config.json if present)
            -v, --verbose         Debug logging (or set NEOLINK_LOG=debug|trace)

        Streams are served at rtsp://<bind>:<port>/<camera-name>[/mainStream|/subStream]
        """);
}
