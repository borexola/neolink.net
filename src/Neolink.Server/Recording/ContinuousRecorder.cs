// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Config;
using Neolink.Streaming;

namespace Neolink.Recording;

/// <summary>
/// 24/7 recording to rolling fMP4 segment files, like a classic NVR:
/// {root}/{camera}/continuous/{yyyy-MM-dd}/{HH-mm-ss}.mp4.
///
/// Toggleable at runtime (web UI → RecordingSettings): the hub subscription stays
/// open either way, packets are simply discarded while the switch is off, so a
/// toggle takes effect on the next frame. Segments roll at the first keyframe once
/// they reach the time (SegmentMinutes) OR size (MaxSegmentSizeMb) limit, whichever
/// comes first — the size cap keeps high-bitrate streams from producing enormous
/// files. Every file is independently playable. Day folders are pruned by the same
/// retention task that expires events.
/// </summary>
public sealed class ContinuousRecorder
{
    /// <summary>After a write failure (disk full, volume gone) don't retry instantly.</summary>
    private static readonly TimeSpan WriteErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly string _camera;
    private readonly IStreamHub _hub;
    private readonly IReadOnlyDictionary<string, IStreamHub>? _hubsByKind;
    private readonly EventStore _store;
    private readonly RecordingSettings _settings;
    private readonly double _segmentSeconds;
    private readonly long _maxSegmentBytes;
    private readonly Func<bool>? _hasRoom;
    private readonly Action<string>? _onWriteError;
    private volatile bool _writing;
    private bool _fullLogged;

    /// <summary>True while 24/7 footage is actually being written to disk (feeds the UI's REC badge).</summary>
    public bool IsWriting => _writing;

    private sealed record ActiveInfo(string Date, string File, ClipWriter Writer);
    private volatile ActiveInfo? _active;

    /// <summary>The segment being written right now — day ("yyyy-MM-dd"), file name
    /// and seconds of media muxed so far — straight from the recorder's memory.
    /// The day listing overlays this onto its filesystem view: a file that is held
    /// open reports a STALE directory mtime on NTFS (updated lazily on close) and
    /// on FUSE/network mounts (attribute caches), so an mtime-derived duration can
    /// trail minutes behind and make a recording camera's lane look stopped —
    /// worst on high-bitrate cameras, whose large segments stay open longest.</summary>
    public (string Date, string File, double Seconds)? ActiveSegment =>
        _active is { } a ? (a.Date, a.File, a.Writer.DurationSeconds) : null;

    /// <param name="hubsByKind">The camera's streams by kind, enabling the per-camera
    /// "record from main/sub" runtime choice; null pins recording to <paramref name="hub"/>.</param>
    /// <param name="hasRoom">Free-space guard: false = the recordings volume is full,
    /// so no new segment is opened (retried on a backoff). Null = never blocks.</param>
    public ContinuousRecorder(string camera, IStreamHub hub, EventStore store,
        RecordingSettings settings, RecordingConfig cfg,
        IReadOnlyDictionary<string, IStreamHub>? hubsByKind = null,
        Func<bool>? hasRoom = null, Action<string>? onWriteError = null)
    {
        _camera = camera;
        _hub = hub;
        _hubsByKind = hubsByKind;
        _store = store;
        _settings = settings;
        _segmentSeconds = cfg.SegmentMinutes * 60.0;
        _maxSegmentBytes = (long)cfg.MaxSegmentSizeMb * 1024 * 1024;
        _hasRoom = hasRoom;
        _onWriteError = onWriteError;
    }

    /// <summary>The stream taped right now: the user's per-camera choice when set (and served).</summary>
    private IStreamHub RecordHub()
    {
        var kind = _settings.Get(_camera).RecordStream;
        return kind != null && _hubsByKind != null && _hubsByKind.TryGetValue(kind, out var hub)
            ? hub
            : _hub;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Outer loop: one pass per stream selection; a runtime change of the
            // record stream ends the current segment and resubscribes.
            while (!ct.IsCancellationRequested)
                await PumpAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>An open segment is closed after this much source silence: an
    /// offline or SUSPENDED camera must leave a real gap in the timeline. Gluing
    /// resumed footage into the old file would time-compress the gap (ClipWriter
    /// clamps timestamp jumps) and place everything after it hours off. Mutable
    /// for the selftest only.</summary>
    internal static TimeSpan SilenceRoll = TimeSpan.FromSeconds(15);

    private async Task PumpAsync(CancellationToken ct)
    {
        var hub = RecordHub();
        var (id, reader) = hub.Subscribe();
        ClipWriter? writer = null;
        bool wasOn = false;
        long lastIndex = -1;
        var retryAfter = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Channel drained: wait for more. With no file open a plain wait
                // does; with a segment open the wait carries the silence deadline.
                if (!reader.TryRead(out var packet))
                {
                    if (writer == null)
                    {
                        if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                            return; // hub completed
                        continue;
                    }
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    silence.CancelAfter(SilenceRoll);
                    try
                    {
                        if (!await reader.WaitToReadAsync(silence.Token).ConfigureAwait(false))
                            return;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Source went quiet (camera offline or suspended): finalize
                        // the segment so it is bounded and playable; footage after
                        // the gap starts a NEW segment stamped with the true time.
                        writer.Dispose();
                        writer = null;
                        _active = null;
                        _writing = false;
                        Log.Info($"{_camera}: stream quiet for {SilenceRoll.TotalSeconds:0}s — segment closed; " +
                                 "footage after the gap starts a new segment");
                    }
                    continue;
                }
                if (!ReferenceEquals(RecordHub(), hub))
                {
                    // Stream selection changed: close this segment, resubscribe.
                    Log.Info($"{_camera}: continuous recording source {hub.Name} → {RecordHub().Name}");
                    return;
                }
                // Hub indices are global across video and audio — see EventRecorder.
                bool gap = lastIndex >= 0 && packet.Index != lastIndex + 1;
                lastIndex = packet.Index;

                // AAC audio rides into the open segment; everything that decides the
                // segment's lifecycle (on/off, roll, create) stays video-driven.
                if (packet is HubAudioAac aac)
                {
                    writer?.AddAudio(aac);
                    continue;
                }
                if (packet is not HubVideo v) continue;

                bool on = _settings.Get(_camera).Continuous;
                if (!on)
                {
                    if (writer != null)
                    {
                        writer.Dispose();
                        writer = null;
                        _active = null;
                        Log.Info($"{_camera}: continuous recording stopped");
                    }
                    _writing = false;
                    wasOn = false;
                    continue;
                }
                if (!wasOn)
                {
                    Log.Info($"{_camera}: continuous recording started " +
                             $"(segments roll at {_segmentSeconds / 60:0} min or {_maxSegmentBytes / (1024 * 1024)} MB)");
                    wasOn = true;
                }

                // Roll to a new file at the first keyframe once the segment reaches
                // either limit — time OR size, whichever comes first (a size cap keeps
                // high-bitrate main streams from producing enormous files).
                if (writer != null && v.Keyframe
                    && (writer.DurationSeconds >= _segmentSeconds || writer.ApproxBytes >= _maxSegmentBytes))
                {
                    writer.Dispose();
                    writer = null;
                    _active = null;
                }

                if (writer == null)
                {
                    // Every segment starts on a keyframe so it plays standalone.
                    if (!v.Keyframe || DateTime.UtcNow < retryAfter) continue;
                    // Free-space guard: a full volume halts new segments cleanly
                    // (instead of failing mid-write) until the hourly cleanup or
                    // the user frees space. One log per transition, not per try.
                    if (_hasRoom?.Invoke() == false)
                    {
                        if (!_fullLogged)
                        {
                            Log.Error($"{_camera}: recordings storage is FULL — continuous recording halted until space is freed");
                            _fullLogged = true;
                        }
                        _writing = false;
                        retryAfter = DateTime.UtcNow + WriteErrorBackoff;
                        continue;
                    }
                    if (_fullLogged)
                    {
                        Log.Info($"{_camera}: storage has room again — continuous recording resumes");
                        _fullLogged = false;
                    }
                    var startedAt = DateTime.Now;
                    try
                    {
                        // Never blocks on disk: the file is opened and written by the
                        // writer's own background thread.
                        writer = ClipWriter.TryCreate(_store.NewSegmentPath(_camera, startedAt), hub);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"{_camera}: cannot start recording segment: {ex.Message}");
                        _onWriteError?.Invoke(_camera);
                        retryAfter = DateTime.UtcNow + WriteErrorBackoff;
                        continue;
                    }
                    if (writer == null) continue; // codec parameters not known yet
                    _active = new ActiveInfo($"{startedAt:yyyy-MM-dd}", $"{startedAt:HH-mm-ss}.mp4", writer);
                    gap = false; // fresh file: this keyframe is a clean start
                }

                writer.Add(v, gap);
                if (writer.Faulted)
                {
                    // A write error while there IS free space (the guard above would
                    // have skipped a full volume): a failing/disconnected drive.
                    writer.Dispose();
                    writer = null;
                    _active = null;
                    _onWriteError?.Invoke(_camera);
                    retryAfter = DateTime.UtcNow + WriteErrorBackoff;
                }
                _writing = writer != null;
            }
        }
        finally
        {
            _writing = false;
            _active = null;
            if (writer != null)
            {
                writer.Dispose();
                // Give the writer thread a moment to finalize the file (segment
                // roll on a stream switch, or shutdown).
                try { await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch { }
            }
            hub.Unsubscribe(id);
        }
    }
}
