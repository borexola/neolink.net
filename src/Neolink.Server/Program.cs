// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink;
using Neolink.Config;
using Neolink.Mqtt;
using Neolink.Protocol;
using Neolink.Recording;
using Neolink.Rtsp;
using Neolink.Streaming;
using Neolink.Web;

const string Version = "0.6.0";

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
        case "rtsp" or "selftest":
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

NeolinkConfig config;
try
{
    config = NeolinkConfig.Load(configPath);
}
catch (Exception ex)
{
    return Fail($"Failed to load config '{configPath}': {ex.Message}");
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
        shutdown.Cancel();
    }
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

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
if (config.Recording is { } recCfg)
{
    try
    {
        eventStore = new EventStore(recCfg.Path);
        eventStore.Load();
        // Runtime switches live in the state dir; older locations (config dir,
        // recordings root) are checked once for migration.
        recordingSettings = new RecordingSettings(stateDir, configDir, eventStore.Root);
        Log.Info($"Recording to {eventStore.Root} " +
                 $"(default retention — events: {(recCfg.RetentionDays > 0 ? $"{recCfg.RetentionDays} days" : "forever")}, " +
                 $"continuous: {(recCfg.EffectiveContinuousRetentionDays > 0 ? $"{recCfg.EffectiveContinuousRetentionDays} days" : "forever")}; " +
                 "per-camera overrides apply)");
        // Per-camera retention: the cleanup pass sees storage-directory names, so map
        // them back to camera names to look up each camera's override (null = default).
        var store = eventStore;
        var settings = recordingSettings;
        var camerasByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in config.Cameras)
            camerasByDir.TryAdd(EventStore.SafeName(c.Name), c.Name);
        (int, int) RetentionFor(string dirName)
        {
            var name = camerasByDir.TryGetValue(dirName, out var n) ? n : dirName;
            var s = settings.Get(name);
            return (s.EventRetentionDays ?? recCfg.RetentionDays,
                    s.ContinuousRetentionDays ?? recCfg.EffectiveContinuousRetentionDays);
        }
        tasks.Add(Task.Run(() => store.RunRetentionAsync(RetentionFor, shutdown.Token)));
    }
    catch (Exception ex)
    {
        Log.Error($"Recording disabled: cannot use storage path '{recCfg.Path}': {ex.Message}");
        eventStore = null;
    }
}

// Motion pushes fan out to the recorder and/or the MQTT bridge; wired after both
// exist (the bridge needs the camera list, built below).
var motionTargets = new List<(CameraService Primary, string Name, Action<MotionPush>? RecorderSink)>();

foreach (var cam in config.Cameras)
{
    var permitted = config.PermittedUsersFor(cam);
    var webStreams = new List<WebStreamInfo>();
    ICameraControl control;
    CameraService? primaryService = null;

    if (cam.IsGenericRtsp)
    {
        // Generic (non-Reolink) camera: pull its RTSP URL(s) into hubs. It streams
        // and records, but has no Baichuan control surface and no motion pushes.
        var pullServices = new List<RtspCameraService>();
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
        void AddStream(StreamKind kind, string suffix, bool alsoRoot)
        {
            var hub = new StreamHub($"{cam.Name} {suffix}");
            server.AddMount(new RtspMount { Path = $"/{cam.Name}/{suffix}", Hub = hub, PermittedUsers = permitted });
            if (alsoRoot)
                server.AddMount(new RtspMount { Path = $"/{cam.Name}", Hub = hub, PermittedUsers = permitted });
            webStreams.Add(new WebStreamInfo(suffix, $"/{cam.Name}/{suffix}", hub));
            var service = new CameraService(cam, kind, hub, TimeSpan.FromSeconds(2 * streamIndex++));
            primaryService ??= service;
            tasks.Add(Task.Run(() => RunCameraGuardedAsync(service, $"{cam.Name} ({kind})", shutdown.Token)));
        }

        // The bare /name mount points at the "best" configured stream
        if (main) AddStream(StreamKind.Main, "mainStream", alsoRoot: true);
        if (sub) AddStream(StreamKind.Sub, "subStream", alsoRoot: !main);
        if (ext) AddStream(StreamKind.Extern, "externStream", alsoRoot: !main && !sub);

        // Control commands (capabilities, PTZ, LED, ...) ride the primary stream's
        // connection — cameras have session limits, so no extra login is spent.
        // Stream encode settings are written via the camera's HTTP API when configured.
        var httpApi = cam.HttpAddress == null ? null
            : new ReolinkHttpApi(cam.HttpAddress, cam.Username, cam.Password, cam.ChannelId);
        var primary = primaryService
            ?? throw new InvalidOperationException($"camera '{cam.Name}' has no streams");
        control = new CameraControl(primary, httpApi);
    }
    if (webStreams.Count == 0)
        throw new InvalidOperationException($"camera '{cam.Name}' has no streams");
    Action<MotionPush>? recorderSink = null;
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
                    config.Recording, recordingSettings, previewStream?.Hub, hubsByKind);
                recorderSink = recorder.OnMotion;
                tasks.Add(Task.Run(() => recorder.RunAsync(shutdown.Token)));
            }

            // Continuous (24/7) recording is temporarily disabled — see
            // RecordingConfig.ContinuousEnabled.
            if (RecordingConfig.ContinuousEnabled)
            {
                var continuous = new ContinuousRecorder(cam.Name, recordStream.Hub, eventStore,
                    recordingSettings, config.Recording, hubsByKind);
                continuousRecorder = continuous;
                tasks.Add(Task.Run(() => continuous.RunAsync(shutdown.Token)));
            }
        }
    }

    // Registered after the recorders so the web API can report live REC state.
    webCameras.Add(new WebCameraInfo(cam.Name, webStreams, control, permitted,
        ContinuousActive: continuousRecorder == null ? null : () => continuousRecorder.IsWriting,
        SupportsEvents: !cam.IsGenericRtsp));
    if (primaryService != null)
        motionTargets.Add((primaryService, cam.Name, recorderSink));
}

// MQTT / Home Assistant bridge (single connection for all cameras), then wire
// motion: the camera's alarm push goes to the recorder and/or the bridge. The
// alarm-push listener only runs when a MotionSink is set, so this also enables
// motion for MQTT-only setups (recording off).
if (config.Mqtt is { } mqttCfg)
{
    mqtt = new HomeAssistantMqtt(mqttCfg, webCameras, Version);
    tasks.Add(Task.Run(() => mqtt.RunAsync(shutdown.Token)));
}
foreach (var (primary, name, recorderSink) in motionTargets)
{
    var bridge = mqtt;
    if (recorderSink == null && bridge == null) continue;
    primary.MotionSink = push =>
    {
        recorderSink?.Invoke(push);
        bridge?.OnMotion(name, push);
    };
    // Unsolicited status pushes (Wi-Fi signal, siren, floodlight) only feed the
    // bridge; the listener only runs when the sink is set.
    if (bridge != null)
        primary.StatusSink = push => bridge.OnStatus(name, push);
}

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

    // Resource sampler for the UI's monitor page (CPU/RAM/disk/viewers over time).
    // ViewerCount deliberately excludes the server's own recorder subscriptions —
    // "viewers" means humans/externals watching, not us taping.
    var monitor = new SystemMonitor(
        diskProbePath: eventStore?.Root ?? stateDir,
        recordingsRoot: eventStore?.Root,
        viewerCount: () => webCameras.Sum(c => c.Streams.Sum(s => s.Hub.ViewerCount)),
        recordingCameras: () => webCameras.Count(c => c.ContinuousActive?.Invoke() == true),
        cameraStates: () => webCameras.Select(c => (c.Name, c.Control.Online)));
    tasks.Add(Task.Run(() => monitor.RunAsync(shutdown.Token)));

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
        UserStore = userStore,
        ResetAdminPassword = config.EffectiveResetAdminPassword,
        TrickleSpeed = config.Ui.TrickleSpeed,
        Version = Version,
        ConfigPath = Path.GetFullPath(configPath),
        Updates = updates,
        Monitor = monitor,
        Logs = logBuffer,
        // Graceful shutdown; docker's restart policy (or systemd) starts us again.
        RestartRequested = () =>
        {
            Log.Warn("Shutting down for a UI-requested restart");
            shutdown.Cancel();
        },
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

static void PrintHelp()
{
    Console.WriteLine(
        """
        Neolink.NET - RTSP bridge for Reolink cameras that speak the Baichuan protocol (port 9000)

        USAGE:
            neolink.net [rtsp] --config <config.json>     Serve all configured cameras over RTSP
            neolink.net selftest [--config <samples-dir>] Run built-in protocol self-tests
            neolink.net --version
            neolink.net --help

        OPTIONS:
            -c, --config <PATH>   Path to the configuration file (JSON; legacy TOML also accepted)
                                  (defaults to ./config.json if present)
            -v, --verbose         Debug logging (or set NEOLINK_LOG=debug|trace)

        Streams are served at rtsp://<bind>:<port>/<camera-name>[/mainStream|/subStream]
        """);
}
