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

    private readonly CameraConfig _config;
    private readonly StreamKind _kind;
    private readonly IMediaSink _hub;
    private readonly TimeSpan _startupDelay;

    /// <summary>AI tokens the pipeline knows how to normalize (see EventRecorder.LabelsOf).</summary>
    private static readonly HashSet<string> KnownAiTypes = new(StringComparer.Ordinal)
    {
        "people", "person", "face", "vehicle", "car", "dog_cat", "animal", "pet",
        "package", "visitor", "doorbell",
    };
    private readonly HashSet<string> _reportedAiTypes = new(StringComparer.Ordinal);
    private volatile IBcCamera? _live;

    public CameraService(CameraConfig config, StreamKind kind, IMediaSink hub, TimeSpan startupDelay)
    {
        _config = config;
        _kind = kind;
        _hub = hub;
        _startupDelay = startupDelay;
    }

    public string Name => _config.Name;
    public IBcCamera? LiveCamera => _live;

    /// <summary>
    /// When set (on the primary stream service), alarm pushes are requested on each
    /// connection and forwarded here. Assigned once during startup wiring.
    /// </summary>
    public Action<MotionPush>? MotionSink { get; set; }

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
            bool gotFrames = false;
            try
            {
                await StreamOnceAsync(ct, () => gotFrames = true).ConfigureAwait(false);
                return; // cancelled cleanly
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
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
                if (gotFrames) backoff = MinBackoff; // we did stream; reset the backoff
                Log.Error($"{Tag}: {Log.Flatten(ex)}; retrying in {backoff.TotalSeconds:0}s");
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

    private async Task StreamOnceAsync(CancellationToken ct, Action onFrame)
    {
        Log.Info($"{Tag}: connecting to {_config.Host}:{_config.Port}");
        await using IBcCamera camera = await BcCamera.ConnectAsync(_config.Host, _config.Port, _config.ChannelId, ct).ConfigureAwait(false);

        Log.Info($"{Tag}: logging in as '{_config.Username}'");
        await camera.LoginAsync(_config.Username, _config.Password, ct).ConfigureAwait(false);
        var res = camera.DeviceInfo;
        Log.Info($"{Tag}: logged in{(res != null && res.Width > 0 ? $", camera reports {res.Width}x{res.Height}" : "")}");

        var binary = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var videoTask = Task.Run(() => camera.StartVideoAsync(_kind, binary.Writer, linked.Token), CancellationToken.None);

        var reader = new MediaFrameReader(binary.Reader);
        long frames = 0;
        _live = camera;
        Task? motionTask = MotionSink is { } sink
            ? Task.Run(() => WatchMotionGuardedAsync(camera, sink, linked.Token), CancellationToken.None)
            : null;
        try
        {
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
                    await videoTask.ConfigureAwait(false);
                    throw new IOException("video stream ended");
                }

                if (++frames == 1)
                    Log.Info($"{Tag}: receiving media");
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
            }
        }
        finally
        {
            _live = null;
            linked.Cancel();
            try { await videoTask.ConfigureAwait(false); } catch { }
            if (motionTask != null)
            {
                try { await motionTask.ConfigureAwait(false); } catch { }
            }
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
}
