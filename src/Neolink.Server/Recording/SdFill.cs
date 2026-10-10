// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Streaming;

namespace Neolink.Recording;

/// <summary>Fetches a battery camera's own recording of each event from its SD card: the
/// camera started at its PIR trigger, so its copy holds the seconds Neolink missed.</summary>
public sealed class SdFill
{
    /// <summary>Camera clips above this are left on the card (a transfer holds the camera awake).</summary>
    public const long MaxBytes = 60L * 1024 * 1024;
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Patience = TimeSpan.FromHours(6); // a backlog of old events must not hold a woken camera

    private sealed class Cam
    {
        public required ICameraControl Control { get; init; }
        public required Func<bool> Battery { get; init; }
        /// <summary>Keeps the camera's session up for a transfer (a parked session is reconnected).</summary>
        public Action<TimeSpan>? Hold { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>The last card check (mounted and recording), good for an hour.</summary>
        public (DateTime At, bool Ready, string Why) Card;
        /// <summary>The last camera-clock reading (server minus camera), good for an hour.</summary>
        public (DateTime At, TimeSpan? Offset) Clock;
    }

    /// <summary>Card times are the camera's clock; this is what to add to put them on the server's.</summary>
    private async Task<TimeSpan> ClockShiftAsync(Cam cam, string camera, CancellationToken ct)
    {
        if (DateTime.UtcNow - cam.Clock.At >= CardCheckFor)
        {
            var offset = await cam.Control.GetClockOffsetAsync(ct).ConfigureAwait(false);
            cam.Clock = (DateTime.UtcNow, offset);
            if (offset is { } o && Math.Abs(o.TotalSeconds) >= 2)
                Log.Info($"{camera}: camera clock is {Math.Abs(o.TotalSeconds):0} s {(o > TimeSpan.Zero ? "behind" : "ahead of")} the server; its card times are corrected");
            else if (offset == null)
                Log.Info($"{camera}: camera clock could not be read; its card times are taken as they are");
        }
        return cam.Clock.Offset ?? TimeSpan.Zero;
    }

    private static readonly TimeSpan CardCheckFor = TimeSpan.FromHours(1);

    /// <summary>A camera without a mounted card, or not recording to it, is never searched.</summary>
    private async Task<bool> CardReadyAsync(Cam cam, string camera, CancellationToken ct)
    {
        if (DateTime.UtcNow - cam.Card.At < CardCheckFor) return cam.Card.Ready;
        var state = await cam.Control.GetSdStateAsync(ct).ConfigureAwait(false);
        var why = state switch
        {
            null => "the camera did not report its SD card",
            { Mounted: false } => "no SD card is mounted",
            { Recording: false } => "the camera's SD recording is off",
            _ => "",
        };
        cam.Card = (DateTime.UtcNow, why.Length == 0, why);
        if (why.Length > 0) Log.Info($"{camera}: camera copies of events are not fetched: {why} (checked again in an hour)");
        return cam.Card.Ready;
    }

    private readonly EventStore _store;
    private readonly RecordingSettings _settings;
    private readonly CancellationToken _shutdown;
    private readonly Dictionary<string, Cam> _cams = new(StringComparer.OrdinalIgnoreCase);

    public SdFill(EventStore store, RecordingSettings settings, CancellationToken shutdown)
    {
        _store = store;
        _settings = settings;
        _shutdown = shutdown;
    }

    public void Register(string camera, ICameraControl control, Func<bool> battery, Action<TimeSpan>? hold)
    {
        lock (_cams) _cams[camera] = new Cam { Control = control, Battery = battery, Hold = hold };
    }

    /// <summary>The per-camera switch; unset means on for battery cameras only.</summary>
    public bool Enabled(string camera)
    {
        Cam? cam;
        lock (_cams) _cams.TryGetValue(camera, out cam);
        return _settings.Get(camera).SdFill ?? (cam != null && cam.Battery());
    }

    /// <summary>Detached: the event pump never waits for the card.</summary>
    public void OnEventClosed(EventRecord rec)
    {
        Cam? cam;
        lock (_cams) _cams.TryGetValue(rec.Camera, out cam);
        if (cam == null || !Enabled(rec.Camera)) return;
        _ = Task.Run(() => RunAsync(cam, rec));
    }

    private async Task RunAsync(Cam cam, EventRecord rec)
    {
        int misses = 0;
        var deadline = DateTime.UtcNow + Patience;
        try
        {
            while (!_shutdown.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (_store.Find(rec.Id) == null) return; // deleted meanwhile
                // Only while the camera is awake anyway; it is never woken for this.
                if (!cam.Control.Online)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _shutdown).ConfigureAwait(false);
                    continue;
                }
                string? verdict;
                await cam.Gate.WaitAsync(_shutdown).ConfigureAwait(false);
                try
                {
                    if (!await CardReadyAsync(cam, rec.Camera, _shutdown).ConfigureAwait(false)) return;
                    verdict = await TryOnceAsync(cam, rec, _shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
                {
                    verdict = "retry: a viewer's transfer took precedence"; // pre-empted by a pick in the SD view
                }
                catch (Exception ex) when (ex is IOException or CameraOfflineException or TimeoutException)
                {
                    verdict = $"retry: {Log.Flatten(ex)}"; // a dropped link or a parked camera: worth another go
                }
                finally { cam.Gate.Release(); }
                if (verdict == null) return; // fetched
                if (verdict.StartsWith("retry: ", StringComparison.Ordinal))
                {
                    if (++misses >= 6)
                    {
                        Log.Info($"{rec.Camera}: no camera copy for the event at {rec.StartUtc.ToLocalTime():HH:mm:ss}: {verdict[7..]}");
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20), _shutdown).ConfigureAwait(false);
                    continue;
                }
                Log.Info($"{rec.Camera}: no camera copy for the event at {rec.StartUtc.ToLocalTime():HH:mm:ss}: {verdict}");
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info($"{rec.Camera}: fetching the camera's copy of the event at {rec.StartUtc.ToLocalTime():HH:mm:ss} failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Null when fetched; "retry: why" to try again; any other text gives up.</summary>
    private async Task<string?> TryOnceAsync(Cam cam, EventRecord rec, CancellationToken ct)
    {
        // Card times are camera-local; the server runs in the same zone.
        var startLocal = rec.StartUtc.ToLocalTime();
        var endLocal = rec.EndUtc.ToLocalTime();
        cam.Hold?.Invoke(TimeSpan.FromMinutes(2)); // the search and transfer must outlive the park timer
        var shift = await ClockShiftAsync(cam, rec.Camera, ct).ConfigureAwait(false);
        var list = await cam.Control.GetSdRecordingsAsync(DateOnly.FromDateTime(startLocal - shift), ct).ConfigureAwait(false);
        if (list == null) return "retry: the camera did not answer the SD search";
        var pick = Pick(list, startLocal, endLocal, shift);
        if (pick == null) return "retry: the card lists no recording inside the event";
        var pickStart = pick.Start + shift;
        var pickEnd = pick.End + shift;
        if (pickStart > startLocal + After)
            return $"the card's first clip starts {(pickStart - startLocal).TotalSeconds:0} s after Neolink's recording, so there is nothing to fill";
        if (pickEnd > DateTime.Now.AddSeconds(-5)) return "retry: the camera is still writing it";
        if (pick.SizeBytes > MaxBytes) return $"its copy is {pick.SizeBytes / 1024 / 1024} MB, above the {MaxBytes / 1024 / 1024} MB limit";

        cam.Hold?.Invoke(TimeSpan.FromMinutes(2));
        var download = await cam.Control.OpenSdRecordingAsync(pick.Name, ct, yield: true).ConfigureAwait(false);
        var spooled = await Web.SdSpool.SpoolAsync(rec.Camera, pick.Name, download, ct).ConfigureAwait(false);
        if (_store.Find(rec.Id) == null) return "the event was deleted meanwhile";
        var dir = _store.EventDir(rec);
        var tmp = Path.Combine(dir, "camera.mp4.tmp");
        await using (var src = File.OpenRead(spooled))
        await using (var dst = FootageVault.Create(tmp))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        File.Move(tmp, Path.Combine(dir, "camera.mp4"), overwrite: true);

        rec.HasCameraClip = true;
        rec.CameraClipStartUtc = pickStart.ToUniversalTime();
        rec.CameraClipSeconds = (pickEnd - pickStart).TotalSeconds;
        _store.Save(rec);
        var lead = (startLocal - pickStart).TotalSeconds;
        Log.Info($"{rec.Camera}: the camera's own copy of the event at {startLocal:HH:mm:ss} fetched from its SD card " +
                 $"({pick.SizeBytes / 1024} KB, starts {lead:0} s {(lead >= 0 ? "before" : "after")} Neolink's" +
                 $"{(shift == TimeSpan.Zero ? "" : $", camera clock corrected by {shift.TotalSeconds:+0;-0} s")})");
        return null;
    }

    /// <summary>The earliest card recording inside the event (from 45 s before its start to its end):
    /// a long Neolink event spans several of the camera's short PIR clips. Card times plus
    /// <paramref name="shift"/> are server time.</summary>
    internal static SdRecording? Pick(IReadOnlyList<SdRecording> list, DateTime startLocal, DateTime endLocal,
        TimeSpan shift = default) =>
        list.Where(r => r.Start + shift >= startLocal - Before && r.Start + shift <= endLocal)
            .OrderBy(r => r.Start)
            .FirstOrDefault();
}
