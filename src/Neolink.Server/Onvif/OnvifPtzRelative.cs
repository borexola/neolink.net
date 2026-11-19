// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using Neolink.Config;
using Neolink.Streaming;

namespace Neolink.Onvif;

/// <summary>Moves by a fraction of the picture, which Reolink's head cannot take: each runs as a timed pan, then
/// a timed tilt. Also the zoom lens's exact positions, and preset moves that last until the head is there.</summary>
public sealed partial class OnvifPtzCamera
{
    /// <summary>The speed Frigate's arrow buttons drive at (its velocity 0.5).</summary>
    internal static readonly float ArrowSpeed = OnvifPtzServer.Direction(0.5, 0)!.Value.Speed;
    /// <summary>A move under this fraction of the half-picture is not worth a motor pulse.</summary>
    private const double RelativeDeadZone = 0.01;
    private static readonly TimeSpan ShortestPulse = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstStopRetry = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PositionReadLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PresetPoll = TimeSpan.FromMilliseconds(300);
    /// <summary>The longest a preset move counts as under way.</summary>
    private static readonly TimeSpan PresetTravelLimit = TimeSpan.FromSeconds(20);

    /// <summary>The lens angles and head rates; null = relative moves are not offered.</summary>
    internal PtzRelativeConfig? Relative { get; init; }

    /// <summary>The size of whichever stream runs, when the main stream's is unknown: its shape is the picture's.</summary>
    internal Func<(uint Width, uint Height)>? AnyStreamSize { get; init; }

    /// <summary>How long the head still counts as moving after its last stop, while the picture settles.</summary>
    internal TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How long a reading of where the head rests is trusted: past it the camera may have moved the
    /// head itself (a guard return waits at least 10 s).</summary>
    internal TimeSpan RestingFor { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether moves by a fraction of the picture are offered: configured, on a head that pans and tilts.</summary>
    internal bool HasRelative => Relative != null && HasPanTilt;

    /// <summary>The speed relative moves run at, which the configured rates are timed at.</summary>
    private float MoveSpeed => (float)(Relative?.Speed ?? ArrowSpeed);

    // ------------------------------------------------------------------ geometry

    private static double Rad(double degrees) => degrees * Math.PI / 180;
    private static double Deg(double radians) => radians * 180 / Math.PI;

    /// <summary>How much narrower the view is than at the widest zoom (1 = widest), taking focal length as
    /// linear in the lens position.</summary>
    internal static double Magnification(double fovWide, double fovTele, double zoomFraction)
    {
        double atTele = Math.Tan(Rad(fovWide) / 2) / Math.Tan(Rad(fovTele) / 2);
        return 1 + (atTele - 1) * Math.Clamp(zoomFraction, 0, 1);
    }

    /// <summary>The pan and tilt in degrees that centre the point at (x, y) of the picture (1 = centre to edge): the
    /// tilt is the point's elevation once the pan has brought it to the middle, exact for a level head.</summary>
    internal static (double Pan, double Tilt) AnglesFor(double x, double y, double fovH, double fovV, double magnification)
    {
        double u = Math.Clamp(x, -1, 1) * Math.Tan(Rad(fovH) / 2) / magnification;
        double v = Math.Clamp(y, -1, 1) * Math.Tan(Rad(fovV) / 2) / magnification;
        return (Deg(Math.Atan(u)), Deg(Math.Atan(v / Math.Sqrt(1 + u * u))));
    }

    /// <summary>The vertical angle of view a horizontal one implies for a picture this shape (height over width).</summary>
    internal static double VerticalFov(double fovH, double shape) => Deg(2 * Math.Atan(Math.Tan(Rad(fovH) / 2) * shape));

    private double _encodedShape;

    /// <summary>The picture's height over its width: from a stream that has run, else the camera's encode table, else 16:9.</summary>
    internal double PictureShape()
    {
        if (_size?.Invoke() is { Width: > 0, Height: > 0 } main) return (double)main.Height / main.Width;
        if (AnyStreamSize?.Invoke() is { Width: > 0, Height: > 0 } any) return (double)any.Height / any.Width;
        double encoded = Volatile.Read(ref _encodedShape);
        return encoded > 0 ? encoded : 9.0 / 16;
    }

    /// <summary>Reads the main stream's size from the camera's encode table, for the picture's shape before any stream runs.</summary>
    private async Task ReadPictureShapeAsync(CancellationToken ct)
    {
        try
        {
            var info = await Control.GetStreamInfoAsync(ct).ConfigureAwait(false);
            var main = info?.StreamInfos.SelectMany(i => i.EncodeTables)
                .FirstOrDefault(t => t.Type == "mainStream" && t.Width > 0 && t.Height > 0);
            if (main != null) Volatile.Write(ref _encodedShape, (double)main.Height / main.Width);
        }
        catch (Exception) { /* a stream that runs, or 16:9, stands in */ }
    }

    /// <summary>One timed run of the motor, turning the head by Degrees (right and up positive).</summary>
    internal readonly record struct Leg(string Command, TimeSpan For, double Degrees);

    /// <summary>The timed pan and tilt for a move across the picture (x right, y up; 1 = centre to edge).</summary>
    internal List<Leg> LegsFor(double x, double y, double magnification)
    {
        var cfg = Relative!;
        if (!double.IsFinite(x)) x = 0;
        if (!double.IsFinite(y)) y = 0;
        var (pan, tilt) = AnglesFor(x, y, cfg.FovH, cfg.FovV ?? VerticalFov(cfg.FovH, PictureShape()), magnification);
        var legs = new List<Leg>(2);
        Add(x, pan, cfg.PanRate, x > 0 ? "right" : "left", _panLag);
        Add(y, tilt, cfg.Tilt, y > 0 ? "up" : "down", _tiltLag);
        return legs;

        void Add(double fraction, double degrees, double rate, string command, LagEstimator learned)
        {
            if (Math.Abs(fraction) < RelativeDeadZone) return;
            double lag = cfg.LagMs is { } ms ? ms / 1000 : learned.Seconds ?? 0;
            legs.Add(new Leg(command, TimeSpan.FromSeconds(Math.Clamp(Math.Abs(degrees) / rate + lag,
                ShortestPulse.TotalSeconds, OnvifPtzServer.MaxMoveTimeout.TotalSeconds)), degrees));
        }
    }

    // ------------------------------------------------------------------ relative moves

    /// <summary>Starts a move across the picture and returns once the head is under way; it counts as moving
    /// until the last leg has stopped and the picture has settled.</summary>
    internal async Task RelativeMoveAsync(double x, double y, CancellationToken ct)
    {
        if (Relative is not { } cfg) throw new NotSupportedException("relative moves are not configured for this camera");
        double magnification = cfg.TeleFovH is { } tele
            ? Magnification(cfg.FovH, tele, await LensFractionAsync(ct).ConfigureAwait(false)) : 1;
        var legs = LegsFor(x, y, magnification);
        await _moveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (legs.Count == 0)
            {
                // Nothing to turn, but a new move still ends the one under way.
                if (_moving) await StopHeadAsync(ct).ConfigureAwait(false);
                return;
            }
            var from = FreshResting();
            long started;
            try { started = await SendAsync(legs[0].Command, MoveSpeed, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not CameraOfflineException)
            {
                // The start may have reached the camera before the failure: stop the head to be sure.
                LoseTrack();
                _ = WatchdogAsync(TakeOver(), FirstStopRetry, attempt: 1, timedOut: false);
                throw;
            }
            _ = RunLegsAsync(TakeOver(), legs, started, from);
        }
        finally { _moveGate.Release(); }
    }

    /// <summary>A new move takes the head over, so earlier legs and watchdogs stand down; the caller holds the move gate.</summary>
    private long TakeOver()
    {
        var generation = Interlocked.Increment(ref _generation);
        _moving = true;
        return generation;
    }

    /// <summary>Stops the head and retires whatever was driving it; the caller holds the move gate.</summary>
    private async Task StopHeadAsync(CancellationToken ct)
    {
        await SendAsync("stop", ArrowSpeed, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _generation);
        LoseTrack();
        _moving = false;
    }

    private async Task RunLegsAsync(long generation, List<Leg> legs, long started, Resting? from)
    {
        var ran = new double[legs.Count]; // seconds from each leg's start leaving for the camera to its stop leaving
        try
        {
            for (int i = 0; ; i++)
            {
                var left = legs[i].For - Stopwatch.GetElapsedTime(started);
                if (left > TimeSpan.Zero) await Task.Delay(left).ConfigureAwait(false);
                await _moveGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (Interlocked.Read(ref _generation) != generation) return; // a later command took the head over
                    ran[i] = Stopwatch.GetElapsedTime(started, await SendLimitedAsync("stop").ConfigureAwait(false)).TotalSeconds;
                    Track(legs[i]);
                    if (i + 1 == legs.Count) break;
                    // A start that fails may still have reached the camera: the catch below stops the head.
                    started = await SendLimitedAsync(legs[i + 1].Command).ConfigureAwait(false);
                }
                finally { _moveGate.Release(); }
            }
            await Task.Delay(Settle).ConfigureAwait(false);
            await LearnAsync(generation, legs, ran, from).ConfigureAwait(false);
            await EndMoveAsync(generation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"{Name}: ONVIF PTZ relative move broke off: {Log.Flatten(ex)}");
            LoseTrack();
            _ = WatchdogAsync(generation, FirstStopRetry, attempt: 1, timedOut: false); // the head may still be running
        }
    }

    private async Task<long> SendLimitedAsync(string command)
    {
        using var limit = new CancellationTokenSource(SendLimit);
        return await SendAsync(command, command == "stop" ? ArrowSpeed : MoveSpeed, limit.Token).ConfigureAwait(false);
    }

    /// <summary>Marks the head idle if this move still owns it; false when a later command took over.</summary>
    private async Task<bool> EndMoveAsync(long generation)
    {
        await _moveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _generation) != generation) return false;
            _moving = false;
            return true;
        }
        finally { _moveGate.Release(); }
    }

    // ------------------------------------------------------------------ position feedback

    /// <summary>Where the head was read to rest, when, and under which command.</summary>
    private sealed record Resting(long Generation, int? Pan, int? Tilt, long At);

    private readonly LagEstimator _panLag = new(), _tiltLag = new();
    private volatile Resting? _resting;
    private volatile bool _positionLogged;

    /// <summary>The learned pan and tilt lags in seconds, null until learned.</summary>
    internal (double? Pan, double? Tilt) LearnedLag => (_panLag.Seconds, _tiltLag.Seconds);

    /// <summary>Whether a fresh reading of where the head rests is there to measure the next move from.</summary>
    internal bool KnowsRestingPlace => FreshResting() != null;

    private Resting? FreshResting() =>
        _resting is { } r && r.Generation == Interlocked.Read(ref _generation)
        && Stopwatch.GetElapsedTime(r.At) < RestingFor ? r : null;

    /// <summary>Where the head is; null when the camera does not say or does not answer in time.</summary>
    private async Task<(int? Pan, int? Tilt)?> ReadPositionAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(PositionReadLimit);
            var at = await Control.GetPtzPositionAsync(limit.Token).ConfigureAwait(false);
            if (at is { } p && !_positionLogged)
            {
                _positionLogged = true;
                Log.Info($"{Name}: the head reports its position (pan {p.Pan?.ToString() ?? "-"}, tilt {p.Tilt?.ToString() ?? "-"})" +
                         (Relative is { LagMs: null } ? "; relative moves learn their start-up lag from it" : ""));
            }
            return at;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Reads where the move left the head and, knowing where it began, how far each leg took it in the
    /// time it ran. A lag set in the config is the user's own, so nothing is learned then.</summary>
    private async Task LearnAsync(long generation, List<Leg> legs, double[] ran, Resting? from)
    {
        if (Relative is not { LagMs: null }) return;
        if (await ReadPositionAsync().ConfigureAwait(false) is not { } at) return;
        if (Interlocked.Read(ref _generation) != generation) return; // the head has moved on meanwhile
        _resting = new Resting(generation, at.Pan, at.Tilt, Stopwatch.GetTimestamp());
        if (from == null) return;
        for (int i = 0; i < legs.Count; i++)
        {
            bool pan = legs[i].Command is "left" or "right";
            if ((pan ? at.Pan - from.Pan : at.Tilt - from.Tilt) is not { } moved) continue;
            var lag = pan ? _panLag : _tiltLag;
            if (lag.Add(ran[i], Math.Abs(moved)))
                Log.Debug($"{Name}: {(pan ? "pan" : "tilt")} lag learned from the head's position: " +
                          (lag.Seconds is { } s ? $"{s * 1000:0} ms at {lag.UnitsPerSecond:0.#} units/s" : "none, the readings disagree"));
        }
    }

    // ------------------------------------------------------------------ the way back to a preset

    /// <summary>Degrees turned since the head last arrived at a preset.</summary>
    private sealed record Offset(double Pan, double Tilt);

    private readonly object _trackLock = new();
    /// <summary>Degrees this endpoint's own moves have turned the head since it last reached a preset; null once
    /// anything else has moved it.</summary>
    private Offset? _fromPreset;

    private Offset? TrackedOffset { get { lock (_trackLock) return _fromPreset; } }

    private void Track(Leg leg)
    {
        lock (_trackLock)
            if (_fromPreset is { } f)
                _fromPreset = leg.Command is "left" or "right" ? f with { Pan = f.Pan + leg.Degrees } : f with { Tilt = f.Tilt + leg.Degrees };
    }

    private void LoseTrack()
    {
        lock (_trackLock) _fromPreset = null;
    }

    /// <summary>How long the way back to a preset takes at the configured rates: from this endpoint's own moves
    /// since the head was last there, else a whole picture each way.</summary>
    private TimeSpan PresetTravel(Offset? away)
    {
        var cfg = Relative!;
        var (pan, tilt) = away is { } a ? (Math.Abs(a.Pan), Math.Abs(a.Tilt))
            : (cfg.FovH, cfg.FovV ?? VerticalFov(cfg.FovH, PictureShape()));
        double seconds = pan / cfg.PanRate + tilt / cfg.Tilt + 2 * Math.Max(0, (cfg.LagMs ?? 0) / 1000);
        return TimeSpan.FromSeconds(Math.Min(seconds, PresetTravelLimit.TotalSeconds));
    }

    /// <summary>Counts a preset move as under way until the head is there: until two readings of where it is agree,
    /// on a head that says, else for as long as the way back takes.</summary>
    private async Task SettleAtPresetAsync(long generation, TimeSpan travel)
    {
        var begun = Stopwatch.GetTimestamp();
        (int? Pan, int? Tilt)? last = null;
        bool moved = false;
        while (Stopwatch.GetElapsedTime(begun) < PresetTravelLimit)
        {
            await Task.Delay(PresetPoll).ConfigureAwait(false);
            if (Interlocked.Read(ref _generation) != generation) return;
            if (await ReadPositionAsync().ConfigureAwait(false) is not { } at)
            {
                // The head does not say where it is: wait out the way back.
                var left = travel - Stopwatch.GetElapsedTime(begun);
                if (left > TimeSpan.Zero) await Task.Delay(left).ConfigureAwait(false);
                last = null;
                break;
            }
            // Still twice running, once it has moved or the way back is surely over.
            if (Equals(at, last) && (moved || Stopwatch.GetElapsedTime(begun) >= TimeSpan.FromSeconds(Math.Min(travel.TotalSeconds, 1))))
                break;
            moved |= last != null && !Equals(at, last);
            last = at;
        }
        await Task.Delay(Settle).ConfigureAwait(false);
        await _moveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _generation) != generation) return;
            _moving = false;
            lock (_trackLock) _fromPreset = new Offset(0, 0);
            if (last is { } p) _resting = new Resting(generation, p.Pan, p.Tilt, Stopwatch.GetTimestamp());
        }
        finally { _moveGate.Release(); }
    }

    // ------------------------------------------------------------------ zoom positions

    private sealed record Lens(long Min, long Max, long Cur, long At);

    private static readonly TimeSpan LensStaleAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LensReadLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LensTravelLimit = TimeSpan.FromSeconds(15);
    private readonly object _lensLock = new();
    private Lens? _lens;
    private long _lensTriedAt;
    private int _lensReading;

    private Lens? KnownLens { get { lock (_lensLock) return _lens; } }

    private void NoteLens((long Min, long Max, long Cur) z)
    {
        lock (_lensLock) _lens = new Lens(z.Min, z.Max, z.Cur, Stopwatch.GetTimestamp());
    }

    /// <summary>Marks the reading stale, so the next status reads the lens again.</summary>
    private void MarkLensStale()
    {
        lock (_lensLock)
            if (_lens is { } l)
                _lens = l with { At = 0 };
    }

    /// <summary>Reads the lens, holding the camera's command gate no longer than LensReadLimit.</summary>
    private async Task<(long Min, long Max, long Cur)?> ReadLensAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LensReadLimit);
        var z = CameraControl.ZoomPosition(await Control.GetZoomFocusAsync(limit.Token).ConfigureAwait(false));
        if (z is { } read) NoteLens(read);
        return z;
    }

    private static double Fraction(Lens? lens) =>
        lens is { } l && l.Max > l.Min ? Math.Clamp((double)(l.Cur - l.Min) / (l.Max - l.Min), 0, 1) : 0;

    /// <summary>The zoom as a fraction of its range (0 = widest) for a move: as last known, read only if never known.</summary>
    private async Task<double> LensFractionAsync(CancellationToken ct)
    {
        if (!HasZoom) return 0;
        if (KnownLens != null) RefreshLensIfStale();
        else
        {
            try { await ReadLensAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* taken as the widest */ }
        }
        return Fraction(KnownLens);
    }

    /// <summary>The zoom as last known (0 = widest), for the status. A stale reading is refreshed behind the caller,
    /// but not while the head or the lens moves.</summary>
    internal double ZoomFraction
    {
        get
        {
            if (!HasZoom) return 0;
            if (!_moving && !_zooming) RefreshLensIfStale();
            return Fraction(KnownLens);
        }
    }

    /// <summary>Starts a lens read behind the caller when the reading is stale and none was tried lately, so a lens
    /// that does not answer is not asked on every status.</summary>
    private void RefreshLensIfStale()
    {
        if (KnownLens is { } lens && Stopwatch.GetElapsedTime(lens.At) <= LensStaleAfter) return;
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lensTriedAt)) <= LensStaleAfter) return;
        if (Interlocked.Exchange(ref _lensReading, 1) != 0) return;
        Interlocked.Exchange(ref _lensTriedAt, Stopwatch.GetTimestamp());
        _ = RefreshLensAsync();
    }

    private async Task RefreshLensAsync()
    {
        await Task.Yield(); // off the caller's path
        try { await ReadLensAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* offline, asleep or refused: asked again once the reading is stale again */ }
        finally { Volatile.Write(ref _lensReading, 0); }
    }

    /// <summary>Drives the lens to a position (0 = widest, 1 = full zoom) behind the caller: it counts as zooming
    /// until the lens is there, and an ONVIF Stop holds it where it has got to.</summary>
    internal void ZoomTo(double position) =>
        StartLensJob(LensTravelLimit, job => DriveLensAsync(job, Math.Clamp(position, 0, 1)));

    private async Task DriveLensAsync(LensJob job, double position)
    {
        var ct = job.Cts.Token;
        try
        {
            var lens = KnownLens is { } known ? (known.Min, known.Max, known.Cur) : await ReadLensAsync(ct).ConfigureAwait(false);
            if (lens is not { } range) throw new NotSupportedException("the camera reports no zoom lens");
            long target = range.Min + (long)Math.Round((range.Max - range.Min) * position);
            ct.ThrowIfCancellationRequested();
            await Control.SetZoomFocusAsync("zoomPos", (uint)target, ct).ConfigureAwait(false);
            long last = long.MinValue;
            for (int still = 0; still < 3;)
            {
                await Task.Delay(ZoomTick, ct).ConfigureAwait(false);
                if (await ReadLensAsync(ct).ConfigureAwait(false) is not { } now || now.Cur == target) break;
                still = now.Cur == last ? still + 1 : 0;
                last = now.Cur;
            }
        }
        catch (OperationCanceledException) when (job.Hold)
        {
            await HoldLensAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Keeps the lens where it is: a Reolink lens only takes positions, so it is sent its own.</summary>
    private async Task HoldLensAsync()
    {
        using var limit = new CancellationTokenSource(SendLimit);
        if (await ReadLensAsync(limit.Token).ConfigureAwait(false) is not { } now) return;
        lock (_zoomLock)
            if (_zoom != null) return; // a newer zoom has the lens now
        await Control.SetZoomFocusAsync("zoomPos", (uint)now.Cur, limit.Token).ConfigureAwait(false);
    }
}

/// <summary>Learns how much of a timed move turns no head (negative: how long it coasts) from position readings.
/// Travel is fitted as a line in time by Theil-Sen, which shrugs off a reading across an end stop or a wrap.</summary>
internal sealed class LagEstimator
{
    private const int Window = 24, MinSamples = 8;
    /// <summary>A lag outside these bounds says the readings are off rather than the head: coasting is brief.</summary>
    private const double MinLag = -0.1, MaxLag = 0.5, MinSpread = 0.25;
    private readonly Queue<(double Seconds, double Travel)> _samples = new();
    private (double Lag, double Rate)? _fit;

    /// <summary>The learned lag in seconds; null until enough moves of different lengths have been seen.</summary>
    internal double? Seconds { get { lock (_samples) return _fit?.Lag; } }

    /// <summary>The head's rate in its own position units per second; null until learned.</summary>
    internal double? UnitsPerSecond { get { lock (_samples) return _fit?.Rate; } }

    /// <summary>Takes one move's duration and travel; true when the learned lag came, went or moved by 10 ms or more.
    /// A window whose fit makes no sense drops the lag, so the configured one applies again.</summary>
    internal bool Add(double seconds, double travel)
    {
        if (!(seconds > 0) || !(travel > 0)) return false; // an end stop, or an axis the camera does not report
        lock (_samples)
        {
            _samples.Enqueue((seconds, travel));
            if (_samples.Count > Window) _samples.Dequeue();
            var window = _samples.ToArray();
            if (!Judgeable(window)) return false; // too few or too alike: the last fit stands
            var before = _fit;
            _fit = Fit(window);
            return (before == null) != (_fit == null)
                   || before is { } b && _fit is { } f && Math.Abs(b.Lag - f.Lag) >= 0.01;
        }
    }

    private static bool Judgeable(IReadOnlyList<(double Seconds, double Travel)> samples) =>
        samples.Count >= MinSamples && samples.Max(s => s.Seconds) - samples.Min(s => s.Seconds) >= MinSpread;

    /// <summary>The lag and rate the samples agree on; null when they are too few, too alike, or make no sense.</summary>
    internal static (double Lag, double Rate)? Fit(IReadOnlyList<(double Seconds, double Travel)> samples)
    {
        if (!Judgeable(samples)) return null;
        var slopes = new List<double>();
        for (int i = 0; i < samples.Count; i++)
            for (int j = i + 1; j < samples.Count; j++)
                if (Math.Abs(samples[j].Seconds - samples[i].Seconds) >= 0.05)
                    slopes.Add((samples[j].Travel - samples[i].Travel) / (samples[j].Seconds - samples[i].Seconds));
        if (slopes.Count == 0) return null;
        double rate = Median(slopes);
        if (!(rate > 0)) return null;
        double lag = Median(samples.Select(s => s.Seconds - s.Travel / rate).ToList());
        return lag is >= MinLag and <= MaxLag ? (lag, rate) : null;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
