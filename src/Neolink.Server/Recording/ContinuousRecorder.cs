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
    private readonly EventStore _store;
    private readonly RecordingSettings _settings;
    private readonly double _segmentSeconds;
    private readonly long _maxSegmentBytes;
    private volatile bool _writing;

    /// <summary>True while 24/7 footage is actually being written to disk (feeds the UI's REC badge).</summary>
    public bool IsWriting => _writing;

    public ContinuousRecorder(string camera, IStreamHub hub, EventStore store,
        RecordingSettings settings, RecordingConfig cfg)
    {
        _camera = camera;
        _hub = hub;
        _store = store;
        _settings = settings;
        _segmentSeconds = cfg.SegmentMinutes * 60.0;
        _maxSegmentBytes = (long)cfg.MaxSegmentSizeMb * 1024 * 1024;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var (id, reader) = _hub.Subscribe();
        ClipWriter? writer = null;
        bool wasOn = false;
        long lastIndex = -1;
        var retryAfter = DateTime.MinValue;

        try
        {
            await foreach (var packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Hub indices are global across video and audio — see EventRecorder.
                bool gap = lastIndex >= 0 && packet.Index != lastIndex + 1;
                lastIndex = packet.Index;
                if (packet is not HubVideo v) continue;

                bool on = _settings.Get(_camera).Continuous;
                if (!on)
                {
                    if (writer != null)
                    {
                        writer.Dispose();
                        writer = null;
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
                }

                if (writer == null)
                {
                    // Every segment starts on a keyframe so it plays standalone.
                    if (!v.Keyframe || DateTime.UtcNow < retryAfter) continue;
                    try
                    {
                        // Never blocks on disk: the file is opened and written by the
                        // writer's own background thread.
                        writer = ClipWriter.TryCreate(_store.NewSegmentPath(_camera, DateTime.Now), _hub);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"{_camera}: cannot start recording segment: {ex.Message}");
                        retryAfter = DateTime.UtcNow + WriteErrorBackoff;
                        continue;
                    }
                    if (writer == null) continue; // codec parameters not known yet
                    gap = false; // fresh file: this keyframe is a clean start
                }

                writer.Add(v, gap);
                if (writer.Faulted)
                {
                    // Disk full/gone — the writer already logged why.
                    writer.Dispose();
                    writer = null;
                    retryAfter = DateTime.UtcNow + WriteErrorBackoff;
                }
                _writing = writer != null;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _writing = false;
            if (writer != null)
            {
                writer.Dispose();
                // Give the writer thread a moment to finalize the file on shutdown.
                try { await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch { }
            }
            _hub.Unsubscribe(id);
        }
    }
}
