// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
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
    private volatile bool _suspended;
    private volatile CancellationTokenSource? _activePull;

    public bool Online => _online;

    /// <summary>True while the user has SUSPENDED this pull: Neolink holds no
    /// connection, so it can't be viewed or recorded here (the camera is untouched).</summary>
    public bool Suspended => _suspended;

    /// <summary>Suspends or resumes the pull at runtime. Suspending drops any live
    /// pull at once; resuming lets the loop reconnect on the next tick.</summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended)
        {
            try { _activePull?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

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
            // Suspended by the user: hold no pull, so the camera can't be viewed or
            // recorded here. The camera is untouched (its own recording / other
            // clients pulling it directly are unaffected). Resuming reconnects.
            if (_suspended)
            {
                Log.Info($"{_tag}: suspended — Neolink will not view or record this camera until it is resumed");
                try
                {
                    while (_suspended && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;
                Log.Info($"{_tag}: resumed — reconnecting");
                backoff = TimeSpan.FromSeconds(1);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activePull = linked; // let SetSuspended interrupt this pull at once
            if (_suspended) linked.Cancel(); // a suspend that raced the assignment above
            try
            {
                Log.Info($"{_tag}: connecting (RTSP pull)");
                var puller = new RtspPuller(_tag, _url, _hub);
                _online = true; // refined below: any failure before PLAY drops it again
                await puller.RunAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (_suspended)
            {
                // Interrupted by a user suspend — park at the top, no backoff, no noise.
                continue;
            }
            catch (Exception ex)
            {
                if (_suspended) continue; // suspend raced the error; the park holds it
                Log.Warn($"{_tag}: RTSP pull ended: {Log.Flatten(ex)}; retrying in {backoff.TotalSeconds:0}s");
            }
            finally
            {
                _online = false;
                _activePull = null;
            }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }
}
