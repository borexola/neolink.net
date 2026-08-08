// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Neolink.Bc.Xml;
using Neolink.Config;
using Neolink.Media;
using Neolink.Protocol;
using Neolink.Streaming;

namespace Neolink.Demo;

/// <summary>
/// --demo: the whole product with nothing real behind it. Four synthetic cameras
/// loop ffmpeg-generated footage through the SAME pipeline a Reolink uses — the
/// hubs, the recorders, the web streamer — so every page shows the true product,
/// not a mock. Detections are pulsed on a timer; the recorder cuts real clips
/// from the synthetic stream and thumbnails them like any camera's.
///
/// Nothing is saved: config, state, recordings and the generated footage all
/// live under one temp directory that is wiped at the NEXT demo start (files
/// may still be open at shutdown; the wipe-on-start makes cleanup reliable
/// without racing the recorders).
///
/// ffmpeg is required — it is what draws the fake world. The scenes are plain
/// lavfi test sources on purpose: unmistakably synthetic, no real footage
/// anywhere, and nothing to license.
/// </summary>
public sealed class DemoRig
{
    /// <summary>One camera's generated footage, parsed and ready to pump.</summary>
    public sealed class Source
    {
        public required IReadOnlyList<(byte[] Data, bool Keyframe)> AccessUnits { get; init; }
        public required int Fps { get; init; }
        public required uint Width { get; init; }
        public required uint Height { get; init; }
        public required byte[] Thumb { get; init; }
        public required IReadOnlyList<string> Labels { get; init; }
    }

    public required NeolinkConfig Config { get; init; }
    public required string ConfigPath { get; init; }
    public required IReadOnlyDictionary<string, Source> Sources { get; init; }
    public required string Root { get; init; }

    // Name, scene filtergraph, detection labels the pulses draw from. The scenes
    // are chosen for visible MOTION (mandelbrot zooms, life crawls) — a still
    // demo reads as a broken one.
    private static readonly (string Name, string Scene, string[] Labels)[] Cameras =
    {
        ("Driveway", "testsrc2=size=1280x720:rate=15", new[] { "vehicle", "person", "motion" }),
        ("FrontDoor", "gradients=size=1280x720:speed=0.05,fps=15", new[] { "person", "motion" }),
        ("Backyard", "mandelbrot=size=1280x720:end_scale=0.2,fps=15", new[] { "animal", "person", "motion" }),
        ("Garage", "life=size=1280x720:mold=10:rate=15:ratio=0.1:death_color=#301040:life_color=#80a060", new[] { "motion", "person" }),
    };

    private const int Fps = 15;

    /// <summary>Generates the demo world (footage, seeded history, config) under a
    /// temp root. Throws with a person-readable message on any failure — the
    /// caller turns that into a startup error.</summary>
    public static DemoRig Prepare()
    {
        var ffmpeg = Ffmpeg.ExePath
            ?? throw new InvalidOperationException("ffmpeg was not found on PATH (or NEOLINK_FFMPEG)");

        var root = Path.Combine(Path.GetTempPath(), "neolink-demo");
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* a previous instance may still hold a file; stale files are harmless */ }
        var stateDir = Path.Combine(root, "state");
        var recDir = Path.Combine(root, "recordings");
        var mediaDir = Path.Combine(root, "media");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(recDir);
        Directory.CreateDirectory(mediaDir);

        // The web UI's admin config editor needs a file to exist; this one is a
        // scratch pad in temp, read by nothing.
        var configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath,
            "{\n  // Demo mode: this file is a scratch pad. The real demo config is built\n" +
            "  // in code and nothing here is read back. Edits are discarded with the demo.\n}\n");

        var sources = new Dictionary<string, Source>(StringComparer.Ordinal);
        foreach (var (name, scene, labels) in Cameras)
        {
            var src = Generate(ffmpeg, mediaDir, name, scene, labels);
            sources[name] = src;
            SeedEvents(recDir, name, labels, src.Thumb, Path.Combine(mediaDir, $"{name}.clip.mp4"));
        }

        var config = new NeolinkConfig { WebUi = true };
        config.Ui.StateDir = stateDir;
        config.Recording = new RecordingConfig { Path = recDir, RetentionDays = 7 };
        foreach (var (name, _, _) in Cameras)
            config.Cameras.Add(new CameraConfig { Name = name, Username = "", Demo = true });

        return new DemoRig { Config = config, ConfigPath = configPath, Sources = sources, Root = root };
    }

    // ---- footage generation -----------------------------------------------

    private static Source Generate(string ffmpeg, string mediaDir, string name, string scene, string[] labels)
    {
        // The name burnt into the frame sells the illusion; drawtext needs a font
        // (fontconfig on Linux, often absent on Windows builds), and each scene
        // filter varies by ffmpeg build — so fall back scene-then-text-then-plain
        // rather than demand any particular build.
        var overlay = $",drawtext=text='{name}  DEMO':fontcolor=white:fontsize=36:" +
                      "box=1:boxcolor=black@0.5:boxborderw=8:x=24:y=24";
        string[] graphs =
        {
            scene + overlay,
            scene,
            $"testsrc2=size=1280x720:rate={Fps}",
        };

        var h264Path = Path.Combine(mediaDir, $"{name}.h264");
        var thumbPath = Path.Combine(mediaDir, $"{name}.jpg");
        var clipPath = Path.Combine(mediaDir, $"{name}.clip.mp4");
        string? lastError = null;
        foreach (var graph in graphs)
        {
            // Baseline + zerolatency: no B-frames, one slice per frame — access
            // units split cleanly and timestamps stay monotone. repeat-headers
            // puts SPS/PPS on every keyframe (the hub's GOP cache and late
            // joiners depend on in-band parameter sets); aud=1 delimits AUs.
            lastError = Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", graph,
                "-t", "24", "-an", "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
                "-profile:v", "baseline", "-pix_fmt", "yuv420p", "-b:v", "600k",
                "-x264-params", "keyint=30:min-keyint=30:scenecut=0:repeat-headers=1:aud=1",
                "-f", "h264", "-y", h264Path]);
            if (lastError != null) continue;
            lastError = Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", graph,
                "-frames:v", "1", "-q:v", "4", "-y", thumbPath]);
            if (lastError != null) continue;
            lastError = Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", graph,
                "-t", "6", "-an", "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "baseline",
                "-pix_fmt", "yuv420p", "-b:v", "600k", "-movflags", "+faststart", "-y", clipPath]);
            if (lastError == null) break;
        }
        if (lastError != null)
            throw new InvalidOperationException($"ffmpeg could not generate '{name}': {lastError}");

        var aus = ParseAccessUnits(File.ReadAllBytes(h264Path));
        if (aus.Count == 0 || !aus.Any(a => a.Keyframe))
            throw new InvalidOperationException($"'{name}': the generated stream parsed to no keyframed access units");
        return new Source
        {
            AccessUnits = aus, Fps = Fps, Width = 1280, Height = 720,
            Thumb = File.ReadAllBytes(thumbPath), Labels = labels,
        };
    }

    private static string? Run(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            // Drained on a task, not inline: a synchronous ReadToEnd blocks until
            // the process dies, which would put the timeout below out of reach of
            // exactly the hang it exists for.
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000)) { try { p.Kill(entireProcessTree: true); } catch { } return "timed out"; }
            var err = errTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0)
            {
                var line = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                return line ?? $"exit code {p.ExitCode}";
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Splits an Annex-B file into access units the way the hub wants
    /// them: one complete frame per VideoFrame, 4-byte start codes, keyframes
    /// flagged, and SPS/PPS guaranteed in-band on every keyframe (stashed from
    /// the stream and prepended where the encoder left them out).</summary>
    internal static List<(byte[] Data, bool Keyframe)> ParseAccessUnits(byte[] annexB)
    {
        var nals = H26x.SplitNals(annexB);
        // zerolatency x264 slices every frame across its threads (11 slice NALs
        // per frame was measured), so "a second slice = a new frame" is only true
        // in a stream WITHOUT delimiters. With AUDs present they are the sole
        // authority — splitting on slices there shipped frames in eleven pieces.
        bool hasAud = nals.Any(n => H26x.H264NalType(n.Span) == 9);
        byte[]? sps = null, pps = null;
        var result = new List<(byte[], bool)>();
        var pending = new List<ReadOnlyMemory<byte>>();

        void Emit()
        {
            if (!pending.Any(n => H26x.H264NalType(n.Span) is >= 1 and <= 5)) { pending.Clear(); return; }
            bool key = pending.Any(n => H26x.H264NalType(n.Span) == H26x.H264Idr);
            bool hasParams = pending.Any(n => H26x.H264NalType(n.Span) == H26x.H264Sps);
            var au = new MemoryStream();
            void Write(ReadOnlySpan<byte> nal)
            {
                au.Write(stackalloc byte[] { 0, 0, 0, 1 });
                au.Write(nal);
            }
            if (key && !hasParams && sps != null && pps != null) { Write(sps); Write(pps); }
            foreach (var n in pending) Write(n.Span);
            result.Add((au.ToArray(), key));
            pending.Clear();
        }

        foreach (var nal in nals)
        {
            int type = H26x.H264NalType(nal.Span);
            switch (type)
            {
                case H26x.H264Sps: sps = nal.ToArray(); break;
                case H26x.H264Pps: pps = nal.ToArray(); break;
                case 9: Emit(); continue;    // access-unit delimiter: frame boundary
                case >= 1 and <= 5 when !hasAud && pending.Any(n => H26x.H264NalType(n.Span) is >= 1 and <= 5):
                    // No AUDs anywhere (encoder fallback): a second slice means a
                    // new frame — single-slice is all that path ever produces.
                    Emit();
                    break;
            }
            pending.Add(nal);
        }
        Emit();
        return result;
    }

    // ---- seeded history ----------------------------------------------------

    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // A few events get an "AI description" so the events page shows the feature.
    // Written to be read: they say out loud that the world is synthetic.
    private static readonly string[] DemoDescriptions =
    {
        "A synthetic visitor crosses the frame with great determination and no legs to speak of.",
        "Two test patterns meet near the gate; one of them is almost certainly a vehicle.",
        "Motion in the lower third — the demo cat again, orbiting the mandelbrot.",
        "A gradient drifts by at walking pace. In this neighbourhood, that counts as a person.",
    };

    /// <summary>Writes a believable last-two-days event history straight into the
    /// EventStore's on-disk layout, before the store loads. Same shape the
    /// recorder writes: folder per event, event.json + thumb.jpg + clip.mp4.</summary>
    private static void SeedEvents(string recDir, string camera, string[] labels, byte[] thumb, string clipPath)
    {
        var rng = new Random(camera.GetHashCode() ^ 0x5eed);
        var clip = File.ReadAllBytes(clipPath);
        int described = 0;
        for (int day = 2; day >= 0; day--)
        {
            int count = day == 0 ? 2 + rng.Next(2) : 3 + rng.Next(3);
            for (int i = 0; i < count; i++)
            {
                var local = DateTime.Now.Date.AddDays(-day)
                    .AddHours(6 + rng.Next(16)).AddMinutes(rng.Next(60)).AddSeconds(rng.Next(60));
                if (local >= DateTime.Now.AddMinutes(-5)) continue;   // today: keep the future empty
                var startUtc = local.ToUniversalTime();
                var suffix = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..2]).ToLowerInvariant();
                var dir = Path.Combine(recDir, camera, local.ToString("yyyy-MM-dd"),
                    "detections", $"{local:HHmmss}-{suffix}");
                Directory.CreateDirectory(dir);
                var evLabels = new List<string> { labels[rng.Next(labels.Length)] };
                // One described event per camera (the oldest day's first), so the
                // AI feature shows without implying every event gets a writeup.
                bool describe = described == 0 && day == 2;
                var rec = new Dictionary<string, object?>
                {
                    ["id"] = $"{camera}~{local:yyyy-MM-dd}~{local:HHmmss}-{suffix}",
                    ["camera"] = camera,
                    ["startUtc"] = startUtc,
                    ["endUtc"] = startUtc.AddSeconds(6 + rng.Next(20)),
                    ["labels"] = evLabels,
                    ["reviewed"] = day == 2,
                    ["ongoing"] = false,
                    ["hasClip"] = true,
                    ["hasThumb"] = true,
                    ["hasPreview"] = false,
                };
                if (describe)
                {
                    described++;
                    rec["aiDescription"] = DemoDescriptions[rng.Next(DemoDescriptions.Length)];
                    rec["aiLevel"] = rng.Next(3) == 0 ? "yellow" : "green";
                    rec["aiModel"] = "demo";
                    rec["aiDescribedUtc"] = startUtc.AddSeconds(30);
                }
                File.WriteAllText(Path.Combine(dir, "event.json"), JsonSerializer.Serialize(rec, EventJson));
                File.WriteAllBytes(Path.Combine(dir, "thumb.jpg"), thumb);
                File.WriteAllBytes(Path.Combine(dir, "clip.mp4"), clip);
            }
        }
    }

    // ---- live pulses -------------------------------------------------------

    /// <summary>Fires a detection every minute or three, holds it a few seconds,
    /// releases it — the recorder does everything else exactly as it would for a
    /// camera that saw a person. External=true is the established idiom for
    /// pushes no camera sent (on-demand records, wake clips).</summary>
    public static async Task RunPulsesAsync(string camera, Source source,
        Action<MotionPush> sink, CancellationToken ct)
    {
        try
        {
            // First event soon after startup, so an impatient visitor sees one land.
            await Task.Delay(TimeSpan.FromSeconds(20 + Random.Shared.Next(25)), ct);
            while (!ct.IsCancellationRequested)
            {
                var label = source.Labels[Random.Shared.Next(source.Labels.Count)];
                sink(new MotionPush("MD", new[] { label }, External: true));
                await Task.Delay(TimeSpan.FromSeconds(6 + Random.Shared.Next(7)), ct);
                sink(new MotionPush("none", Array.Empty<string>(), External: true));
                await Task.Delay(TimeSpan.FromSeconds(45 + Random.Shared.Next(120)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>
/// The pump: pushes one camera's looped access units into its StreamHub at frame
/// rate, forever. Mirrors RtspCameraService's shape (Online/Suspended/RunAsync)
/// so the wiring in Program.cs treats it like any other per-stream service.
/// Timestamps are a single monotonic microsecond counter that never resets at
/// the loop seam — the hub's delta math is wrap-safe, resets are not.
/// </summary>
public sealed class DemoCameraService
{
    private readonly string _name;
    private readonly DemoRig.Source _source;
    private readonly IMediaSink _sink;
    private volatile bool _suspended;

    public DemoCameraService(string name, DemoRig.Source source, IMediaSink sink)
    {
        _name = name;
        _source = source;
        _sink = sink;
    }

    public bool Online => !_suspended;
    public bool Suspended => _suspended;
    public void SetSuspended(bool suspended) => _suspended = suspended;

    public async Task RunAsync(CancellationToken ct)
    {
        _sink.PublishInfo(new MediaInfo(_source.Width, _source.Height, (byte)_source.Fps));
        var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _source.Fps);
        uint intervalUs = (uint)(1_000_000 / _source.Fps);
        var clock = Stopwatch.StartNew();
        long frame = 0;
        uint ts = 0;
        int index = 0;
        bool wasSuspended = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_suspended)
                {
                    if (!wasSuspended)
                    {
                        wasSuspended = true;
                        _sink.SourceStopped();   // viewers must not replay a stale GOP
                    }
                    await Task.Delay(250, ct);
                    // Resume clean: on a keyframe, with the pacing clock rebased so
                    // the pump doesn't sprint to catch up on the suspended gap.
                    while (index < _source.AccessUnits.Count && !_source.AccessUnits[index].Keyframe)
                        index = (index + 1) % _source.AccessUnits.Count;
                    clock.Restart();
                    frame = 0;
                    continue;
                }
                wasSuspended = false;

                var (data, key) = _source.AccessUnits[index];
                _sink.PublishVideo(new VideoFrame(VideoCodec.H264, key, ts, null, data));
                index = (index + 1) % _source.AccessUnits.Count;
                unchecked { ts += intervalUs; }
                frame++;

                // Absolute schedule, not per-frame sleeps: Delay slop would drift
                // the stream slow and starve the recorders' silence timers.
                var wait = TimeSpan.FromTicks(frame * interval.Ticks) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A dead pump is a silently frozen tile; every other camera task in
            // the process is guarded, so this one says why it died too.
            Log.Error($"{_name}: demo pump crashed: {Log.Flatten(ex)}");
        }
        finally
        {
            _sink.SourceStopped();
        }
    }
}

/// <summary>
/// A demo camera's control surface: reports no device features (so the settings
/// tab hides, exactly like a generic RTSP camera), but DOES answer snapshots —
/// with a frame from its own synthetic footage — so events get thumbnails.
/// </summary>
public sealed class DemoCameraControl : ICameraControl
{
    private readonly DemoCameraService _service;
    private readonly byte[] _thumb;

    public DemoCameraControl(string cameraName, DemoCameraService service, byte[] thumb)
    {
        CameraName = cameraName;
        _service = service;
        _thumb = thumb;
    }

    public string CameraName { get; }

    public bool Online => _service.Online;

    public Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new CameraCapabilities(
            Version: null,
            Support: null,
            Features: new CameraFeatures(Ptz: false, Led: false, Pir: false, Battery: false, Talk: false)));

    public Task<StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct) =>
        Task.FromResult<StreamInfoListXml?>(null);

    public bool CanSetStreamSettings => false;

    public Task<IReadOnlyList<StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StreamEncSetting>?>(null);

    public Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
        uint? framerate, uint? bitrate, CancellationToken ct) => throw Nope();

    public Task<XElement?> GetBatteryInfoAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task<byte[]?> SnapshotAsync(CancellationToken ct) => Task.FromResult<byte[]?>(_thumb);

    public Task<XElement?> GetLedStateAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetLedStateAsync(string? state, string? lightState,
        string? doorbellLightState, int? irBrightness, CancellationToken ct) => throw Nope();

    public Task<XElement?> GetPirStateAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetPirEnabledAsync(bool enabled, CancellationToken ct) => throw Nope();

    public Task PtzAsync(string command, float speed, CancellationToken ct) => throw Nope();

    public Task RebootAsync(CancellationToken ct) => throw Nope();

    public Task<XElement?> GetZoomFocusAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct) => throw Nope();

    public Task SirenAsync(bool? on, CancellationToken ct) => throw Nope();

    public Task<bool?> GetPrivacyModeAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task SetPrivacyModeAsync(bool on, CancellationToken ct) => throw Nope();

    public Task<XElement?> GetFloodlightTasksAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetFloodlightTasksAsync(XElement task, CancellationToken ct) => throw Nope();

    public Task<WhiteLedState?> GetWhiteLedAsync(CancellationToken ct) => Task.FromResult<WhiteLedState?>(null);

    public Task SetWhiteLedAsync(int? bright, bool? on, int? mode, CancellationToken ct) => throw Nope();

    public Task<HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct) => Task.FromResult<HttpFeatures?>(null);

    public Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct) => Task.FromResult<ImageSettings?>(null);

    public Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue, int? sharpen,
        string? dayNight, string? antiFlicker, bool? flip, bool? mirror, CancellationToken ct) => throw Nope();

    public Task<int?> GetVolumeAsync(CancellationToken ct) => Task.FromResult<int?>(null);

    public Task SetVolumeAsync(int volume, CancellationToken ct) => throw Nope();

    public Task<WifiReading?> GetWifiSignalAsync(CancellationToken ct) => Task.FromResult<WifiReading?>(null);

    public Task<IReadOnlyList<PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PtzPresetInfo>?>(null);

    public Task PtzToPresetAsync(int id, CancellationToken ct) => throw Nope();

    public Task SavePtzPresetAsync(int id, string name, CancellationToken ct) => throw Nope();

    public Task<IReadOnlyList<QuickReplyFile>?> GetQuickRepliesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QuickReplyFile>?>(null);

    public Task PlayQuickReplyAsync(int id, CancellationToken ct) => throw Nope();

    public Task<AutoReplyState?> GetAutoReplyAsync(CancellationToken ct) =>
        Task.FromResult<AutoReplyState?>(null);

    public Task SetAutoReplyAsync(int? fileId, int? timeoutSeconds, CancellationToken ct) => throw Nope();

    public Task<bool?> GetAutoTrackAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task SetAutoTrackAsync(bool on, CancellationToken ct) => throw Nope();

    public Task<IReadOnlyList<SdCardInfo>?> GetSdCardsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SdCardInfo>?>(null);

    public Task TalkAsync(int sampleRate, System.Threading.Channels.ChannelReader<byte[]> pcm, CancellationToken ct) =>
        throw Nope();

    private static NotSupportedException Nope() =>
        new("demo cameras are synthetic — there is no device to control");
}
