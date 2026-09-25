// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Streaming;

namespace Neolink.Onvif;

/// <summary>One camera behind an ONVIF PTZ endpoint: its profile (named after it), its moves, zoom and presets,
/// the watchdog that stops a move whose Stop never came, and its capabilities once known.</summary>
public sealed class OnvifPtzCamera
{
    /// <summary>A watchdog stop that fails is tried this many times in all, StopRetryDelay apart.</summary>
    private const int StopAttempts = 3;

    /// <summary>Marks the PTZ commands this endpoint sends, so the camera's notice of them is not taken as another UI's.</summary>
    private static readonly AsyncLocal<OnvifPtzCamera?> Sending = new();

    private readonly Func<(uint Width, uint Height)>? _size;
    private readonly SemaphoreSlim _moveGate = new(1, 1);
    /// <summary>Bumped by every PTZ command, from here or any other UI; a watchdog stops only its own move.</summary>
    private long _generation;
    private volatile bool _moving;
    private volatile CameraCapabilities? _caps;
    private readonly object _capsLock = new();
    private Task? _capsRead;
    private bool _noPtzLogged;

    /// <param name="permitted">The users permitted on this camera; null = no login needed, as for RTSP.</param>
    /// <param name="size">The main stream's size, for the profile; 0x0 while unknown.</param>
    public OnvifPtzCamera(string name, ICameraControl control, IReadOnlySet<string>? permitted,
        Func<(uint Width, uint Height)>? size = null)
    {
        Name = name;
        Control = control;
        Permitted = permitted;
        _size = size;
        control.PtzCommandSent += OnPtzCommandSent;
    }

    public string Name { get; }
    internal ICameraControl Control { get; }
    internal IReadOnlySet<string>? Permitted { get; }
    internal TimeSpan StopRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The ONVIF profile token, which is also the profile's name: Frigate's onvif.profile matches either.</summary>
    internal string ProfileToken => Name;

    /// <summary>Whether the head is moving as far as this endpoint knows.</summary>
    internal bool Moving => _moving;

    internal (uint Width, uint Height) Size() =>
        _size?.Invoke() is { Width: > 0, Height: > 0 } s ? s : (1920, 1080);

    // ------------------------------------------------------------------ capabilities

    /// <summary>The camera's capabilities once it has answered, else null with a read started behind the
    /// caller: a cold probe outlasts any wait a request could afford, and is never cut short.</summary>
    internal CameraCapabilities? KnownCapabilities()
    {
        if (_caps is { } known) return known;
        StartCapabilitiesRead();
        return null;
    }

    internal void StartCapabilitiesRead()
    {
        lock (_capsLock)
            if (_caps == null && _capsRead is not { IsCompleted: false })
                _capsRead = ReadCapabilitiesAsync();
    }

    /// <summary>The read in flight, for tests.</summary>
    internal Task CapabilitiesRead => _capsRead ?? Task.CompletedTask;

    private async Task ReadCapabilitiesAsync()
    {
        await Task.Yield(); // off the caller's path
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var caps = await Control.GetCapabilitiesAsync(limit.Token).ConfigureAwait(false);
            if (!caps.Provisional) _caps = caps;
        }
        catch (Exception) { /* offline or asleep: the next request asks again */ }
    }

    /// <summary>Pan/tilt is offered unless the camera has said it has none (until it answers, it is assumed).</summary>
    internal bool HasPanTilt => KnownCapabilities() is not { Features.Ptz: false };

    /// <summary>Zoom is offered only once the camera has said it has a zoom lens.</summary>
    internal bool HasZoom => KnownCapabilities() is { Features.Zoom: true };

    /// <summary>Whether the profile carries any PTZ at all; a camera with neither is said so, once.</summary>
    internal bool OffersPtz()
    {
        if (HasPanTilt || HasZoom) return true;
        if (!_noPtzLogged)
        {
            _noPtzLogged = true;
            Log.Warn($"{Name}: PTZ for Frigate is on, but the camera reports no pan/tilt or zoom, so its ONVIF profile offers none");
        }
        return false;
    }

    // ------------------------------------------------------------------ movement

    /// <summary>Sends a command; a move arms a watchdog that stops the head should no Stop come
    /// (a closed browser tab). A command that fails leaves the previous watchdog armed.</summary>
    internal async Task MoveAsync(string command, float speed, TimeSpan timeout, CancellationToken ct)
    {
        await _moveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendAsync(command, speed, ct).ConfigureAwait(false);
            var generation = Interlocked.Increment(ref _generation);
            _moving = command != "stop";
            if (_moving) _ = WatchdogAsync(generation, timeout, attempt: 1);
        }
        finally { _moveGate.Release(); }
    }

    private async Task SendAsync(string command, float speed, CancellationToken ct)
    {
        Sending.Value = this; // flows into the camera's notice of this command, and ends with this call
        await Control.PtzAsync(command, speed, ct).ConfigureAwait(false);
    }

    /// <summary>Another UI (the web panel, Home Assistant) drove the head: its move is its own to stop.</summary>
    private void OnPtzCommandSent(string command)
    {
        if (ReferenceEquals(Sending.Value, this)) return;
        Interlocked.Increment(ref _generation);
        _moving = command is "up" or "down" or "left" or "right"; // a preset move ends by itself
    }

    // ------------------------------------------------------------------ zoom

    /// <summary>How often a held zoom steps the lens; each step is an exact position, all Reolink takes.</summary>
    private static readonly TimeSpan ZoomTick = TimeSpan.FromMilliseconds(250);
    private CancellationTokenSource? _zoom;
    private volatile bool _zooming;

    /// <summary>Whether a zoom is under way.</summary>
    internal bool Zooming => _zooming;

    /// <summary>Zooms in (velocity above 0) or out until <see cref="StopZoom"/>, the end of the range, or the
    /// timeout: the lens is stepped toward that end, a full range taking about 4 s at Frigate's 0.5.</summary>
    internal async Task ZoomAsync(double velocity, TimeSpan timeout, CancellationToken ct)
    {
        var zoom = CameraControl.ZoomPosition(await Control.GetZoomFocusAsync(ct).ConfigureAwait(false))
                   ?? throw new NotSupportedException("the camera reports no zoom lens");
        var step = Math.Max(1, (long)Math.Round((zoom.Max - zoom.Min) * Math.Min(1, Math.Abs(velocity)) / 8));
        // Under the move gate, so a zoom and a preset move take turns rather than drive the lens together.
        await _moveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var mine = new CancellationTokenSource(timeout);
            StopZoom();
            _zoom = mine;
            _zooming = true;
            _ = ZoomLoopAsync(mine, zoom, Math.Sign(velocity) * step);
        }
        finally { _moveGate.Release(); }
    }

    private async Task ZoomLoopAsync(CancellationTokenSource mine, (long Min, long Max, long Cur) zoom, long step)
    {
        var ct = mine.Token;
        try
        {
            for (long pos = zoom.Cur; !ct.IsCancellationRequested;)
            {
                long next = Math.Clamp(pos + step, zoom.Min, zoom.Max);
                if (next == pos) break; // the end of the range
                pos = next;
                await Control.SetZoomFocusAsync("zoomPos", (uint)pos, ct).ConfigureAwait(false);
                await Task.Delay(ZoomTick, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopped, or the move timed out */ }
        catch (Exception ex) { Log.Debug($"{Name}: ONVIF PTZ zoom stopped: {Log.Flatten(ex)}"); }
        finally
        {
            if (Interlocked.CompareExchange(ref _zoom, null, mine) == mine) _zooming = false;
            mine.Dispose();
        }
    }

    /// <summary>Ends a zoom under way; the lens stops at the last position it was sent.</summary>
    internal void StopZoom()
    {
        if (Interlocked.Exchange(ref _zoom, null) is not { } zoom) return;
        _zooming = false;
        try { zoom.Cancel(); }
        catch (ObjectDisposedException) { /* it had just finished */ }
    }

    // ------------------------------------------------------------------ presets

    private volatile IReadOnlyList<PtzPresetInfo>? _presets;

    /// <summary>The preset slots as the camera last listed them; null until it has.</summary>
    internal IReadOnlyList<PtzPresetInfo>? KnownPresets => _presets;

    /// <summary>How many preset slots the camera reported when last asked (0 until then).</summary>
    internal int PresetSlots => _presets?.Count ?? 0;

    /// <summary>The camera's preset slots, used or free; null when its HTTP API, which keeps them, does not answer.</summary>
    internal async Task<IReadOnlyList<PtzPresetInfo>?> PresetsAsync(CancellationToken ct)
    {
        var slots = await Control.GetPtzPresetsAsync(ct).ConfigureAwait(false);
        if (slots != null) _presets = slots;
        return slots;
    }

    /// <summary>Drives to a saved preset; any continuous move or zoom under way gives way to it.</summary>
    internal async Task GotoPresetAsync(int id, CancellationToken ct)
    {
        await _moveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopZoom();
            Sending.Value = this; // this preset move is not another UI's
            await Control.PtzToPresetAsync(id, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _generation); // retires the watchdog of an earlier move
            _moving = false;
        }
        finally { _moveGate.Release(); }
    }

    internal Task SavePresetAsync(int id, string name, CancellationToken ct) => Control.SavePtzPresetAsync(id, name, ct);

    private async Task WatchdogAsync(long generation, TimeSpan after, int attempt)
    {
        await Task.Delay(after).ConfigureAwait(false);
        await _moveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _generation) != generation) return; // a later command took over
            if (attempt == 1)
                Log.Debug($"{Name}: ONVIF PTZ move had no Stop within {after.TotalSeconds:0}s — stopping the head");
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync("stop", 32, limit.Token).ConfigureAwait(false);
            _moving = false;
        }
        catch (Exception ex)
        {
            if (attempt < StopAttempts)
            {
                Log.Debug($"{Name}: ONVIF PTZ stop after a timed-out move failed ({ex.Message}); trying again");
                _ = WatchdogAsync(generation, StopRetryDelay, attempt + 1);
            }
            else
            {
                _moving = false; // nothing more this endpoint can do; the status must not claim a move for ever
                Log.Warn($"{Name}: ONVIF PTZ could not stop the head after its move timed out ({Log.Flatten(ex)}) — " +
                         "stop it from the camera panel");
            }
        }
        finally { _moveGate.Release(); }
    }

    internal async Task StopIfMovingAsync()
    {
        StopZoom();
        if (!_moving) return;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await MoveAsync("stop", 32, OnvifPtzServer.DefaultMoveTimeout, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Debug($"{Name}: ONVIF PTZ stop on shutdown failed: {Log.Flatten(ex)}"); }
    }
}
