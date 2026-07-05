using System.Threading.Channels;
using Neolink.Config;
using Neolink.Media;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>
/// Owns the connection to one camera stream (main/sub/extern): connects, logs in,
/// starts the video stream, demuxes media frames into the hub, and reconnects
/// with exponential backoff on failure.
/// </summary>
public sealed class CameraService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthRetryDelay = TimeSpan.FromSeconds(30);

    private readonly CameraConfig _config;
    private readonly StreamKind _kind;
    private readonly StreamHub _hub;
    private readonly TimeSpan _startupDelay;

    public CameraService(CameraConfig config, StreamKind kind, StreamHub hub, TimeSpan startupDelay)
    {
        _config = config;
        _kind = kind;
        _hub = hub;
        _startupDelay = startupDelay;
    }

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
        await using var camera = await BcCamera.ConnectAsync(_config.Host, _config.Port, _config.ChannelId, ct).ConfigureAwait(false);

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
            linked.Cancel();
            try { await videoTask.ConfigureAwait(false); } catch { }
        }
    }
}
