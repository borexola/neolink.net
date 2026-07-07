using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>
/// Keeps one generic RTSP stream flowing into its hub: connect, pump, and on any
/// failure back off and reconnect — the same contract CameraService gives
/// Baichuan cameras. Online reflects whether the pull is currently established.
/// </summary>
public sealed class RtspCameraService
{
    private readonly string _tag;
    private readonly string _url;
    private readonly StreamHub _hub;
    private readonly TimeSpan _startDelay;
    private volatile bool _online;

    public bool Online => _online;

    public RtspCameraService(string tag, string url, StreamHub hub, TimeSpan startDelay)
    {
        _tag = tag;
        _url = url;
        _hub = hub;
        _startDelay = startDelay;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_startDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(_startDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log.Info($"{_tag}: connecting (RTSP pull)");
                var puller = new RtspPuller(_tag, _url, _hub);
                _online = true; // refined below: any failure before PLAY drops it again
                await puller.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"{_tag}: RTSP pull ended: {Log.Flatten(ex)}; retrying in {backoff.TotalSeconds:0}s");
            }
            finally
            {
                _online = false;
            }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }
}
