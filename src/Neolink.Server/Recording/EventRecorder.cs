using System.Threading.Channels;
using Neolink.Config;
using Neolink.Media;
using Neolink.Protocol;
using Neolink.Streaming;

namespace Neolink.Recording;

/// <summary>
/// Turns a camera's alarm pushes into stored, labeled events with video clips.
///
/// Two long-lived tasks cooperate:
///  - the packet pump subscribes to the stream hub and either maintains a keyframe-
///    aligned pre-roll buffer (idle) or feeds the active clip writer (recording);
///  - the event loop consumes motion pushes and drives the event lifecycle.
///
/// Grouping: one event spans a burst of activity. It opens on the first detection,
/// keeps extending while the camera reports activity (labels accumulate — a person
/// walking to their car becomes one "person + vehicle" event, not five), and closes
/// after PostSeconds of quiet or at the MaxClipSeconds cap. A thumbnail is captured
/// via the camera's own JPEG snapshot command, so no server-side decoding is needed.
/// </summary>
public sealed class EventRecorder
{
    private readonly string _camera;
    private readonly IStreamHub _hub;
    private readonly IStreamHub? _previewHub;
    private readonly ICameraControl _control;
    private readonly EventStore _store;
    private readonly RecordingConfig _cfg;
    private readonly RecordingSettings _settings;

    private readonly Channel<MotionPush> _pushes = Channel.CreateUnbounded<MotionPush>(
        new UnboundedChannelOptions { SingleReader = true });

    // Pre-roll buffers and clip writers are handed between the pumps and the event
    // loop under one gate; all touch them briefly (never blocking on disk).
    // Each buffered packet carries its drop flag so a clip started later still
    // knows where the stream was discontinuous. The optional preview capture
    // records the sub stream into preview.mp4 alongside the full clip — that's
    // what the review strip's ambient players use, keeping client decode cheap.
    private readonly object _mediaGate = new();
    private readonly List<(HubVideo Video, bool Gap)> _preroll = new();
    private readonly List<(HubVideo Video, bool Gap)> _previewPreroll = new();
    private ClipWriter? _writer;
    private ClipWriter? _previewWriter;

    public EventRecorder(string camera, IStreamHub hub, ICameraControl control,
        EventStore store, RecordingConfig cfg, RecordingSettings settings,
        IStreamHub? previewHub = null)
    {
        _camera = camera;
        _hub = hub;
        _previewHub = previewHub;
        _control = control;
        _store = store;
        _cfg = cfg;
        _settings = settings;
    }

    public string Camera => _camera;

    /// <summary>Called from the camera connection for every alarm push (any thread).</summary>
    public void OnMotion(MotionPush push) => _pushes.Writer.TryWrite(push);

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"{_camera}: event recording enabled ({_hub.Name}, pre={_cfg.PreSeconds}s, post={_cfg.PostSeconds}s" +
                 $"{(_previewHub != null ? $", previews from {_previewHub.Name}" : "")})");
        var pump = Task.Run(() => PumpPacketsAsync(_hub, preview: false, ct), CancellationToken.None);
        var previewPump = _previewHub == null
            ? Task.CompletedTask
            : Task.Run(() => PumpPacketsAsync(_previewHub, preview: true, ct), CancellationToken.None);
        try
        {
            await EventLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ClipWriter? closing, closingPreview;
            lock (_mediaGate)
            {
                closing = _writer;
                closingPreview = _previewWriter;
                _writer = null;
                _previewWriter = null;
            }
            closing?.Dispose();
            closingPreview?.Dispose();
            try { await pump.ConfigureAwait(false); } catch { }
            try { await previewPump.ConfigureAwait(false); } catch { }
            // Give the writer threads a moment to finalize files on shutdown.
            foreach (var w in new[] { closing, closingPreview })
            {
                if (w == null) continue;
                try { await w.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ packet pump

    private async Task PumpPacketsAsync(IStreamHub hub, bool preview, CancellationToken ct)
    {
        var (id, reader) = hub.Subscribe();
        try
        {
            // The hub index is global across video AND audio packets, so it must be
            // tracked for every packet — a video-only view sees non-consecutive
            // indices whenever audio is interleaved, and treating those as drops
            // would discard all P-frames (0-second clips).
            long lastIndex = -1;
            await foreach (var packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                bool gap = lastIndex >= 0 && packet.Index != lastIndex + 1;
                lastIndex = packet.Index;

                if (packet is not HubVideo v) continue;
                lock (_mediaGate)
                {
                    var writer = preview ? _previewWriter : _writer;
                    if (writer != null)
                    {
                        // Add never blocks on disk (background writer thread); a dead
                        // disk surfaces as Faulted and the event continues clip-less.
                        writer.Add(v, gap);
                        if (writer.Faulted)
                        {
                            Log.Warn($"{_camera}: {(preview ? "preview" : "clip")} writer failed; " +
                                     "event continues without further video");
                            writer.Dispose();
                            if (preview) _previewWriter = null;
                            else _writer = null;
                        }
                    }
                    else
                    {
                        var buffer = preview ? _previewPreroll : _preroll;
                        buffer.Add((v, gap));
                        TrimPreroll(buffer);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            hub.Unsubscribe(id);
        }
    }

    /// <summary>
    /// Keeps a pre-roll spanning at least PreSeconds while starting on a keyframe
    /// (a clip must begin decodable): drop everything before the latest keyframe
    /// that still preserves the wanted span.
    /// </summary>
    private void TrimPreroll(List<(HubVideo Video, bool Gap)> buffer)
    {
        uint want = (uint)_cfg.PreSeconds * FMp4.Timescale;
        uint last = buffer[^1].Video.RtpTs;
        int cut = -1;
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            if (!buffer[i].Video.Keyframe) continue;
            if (unchecked(last - buffer[i].Video.RtpTs) >= want)
            {
                cut = i;
                break;
            }
        }
        if (cut > 0)
            buffer.RemoveRange(0, cut);
        // Safety valve for streams with pathological keyframe intervals.
        if (buffer.Count > 4096)
            buffer.RemoveRange(0, buffer.Count - 4096);
    }

    // ------------------------------------------------------------------ event lifecycle

    private async Task EventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            MotionPush push;
            try
            {
                push = await _pushes.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!push.Active) continue; // stray all-clear with no open event

            // Runtime switches (web UI): events off, or every label of this push
            // filtered out by the camera's event-type selection → discard silently.
            var settings = _settings.Get(_camera);
            if (!settings.Events) continue;
            var labels = LabelsOf(push).Where(settings.AllowsLabel).ToList();
            if (labels.Count == 0) continue;

            try
            {
                await RunEventAsync(labels, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"{_camera}: event handling failed: {Log.Flatten(ex)}");
            }
        }
    }

    private async Task RunEventAsync(List<string> initialLabels, CancellationToken ct)
    {
        var rec = _store.Create(_camera,
            DateTime.UtcNow - TimeSpan.FromSeconds(_cfg.PreSeconds), initialLabels);
        Log.Info($"{_camera}: ⚡ event started ({string.Join("+", rec.Labels)})");

        StartClip(rec);
        var thumbTask = CaptureThumbAsync(rec, ct);

        var hardStop = DateTime.UtcNow.AddSeconds(_cfg.MaxClipSeconds);
        var quietUntil = DateTime.UtcNow.AddSeconds(_cfg.PostSeconds);
        bool active = true; // the camera currently reports detection

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (now >= hardStop) break;
            var deadline = active ? hardStop : (quietUntil < hardStop ? quietUntil : hardStop);
            if (!active && now >= deadline) break;

            MotionPush push;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(deadline - now);
            try
            {
                push = await _pushes.Reader.ReadAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // a deadline fired; loop re-evaluates
            }

            if (push.Active)
            {
                // Filtered-out detection types don't extend the event either —
                // as far as recording is concerned, they never happened.
                var allowed = LabelsOf(push).Where(_settings.Get(_camera).AllowsLabel).ToList();
                if (allowed.Count == 0) continue;
                active = true;
                var fresh = allowed.Where(l => !rec.Labels.Contains(l)).ToList();
                if (fresh.Count > 0)
                {
                    rec.Labels.AddRange(fresh);
                    rec.EndUtc = DateTime.UtcNow;
                    _store.Save(rec);
                    Log.Info($"{_camera}: event escalated (+{string.Join("+", fresh)})");
                }
            }
            else
            {
                active = false;
                quietUntil = DateTime.UtcNow.AddSeconds(_cfg.PostSeconds);
            }
        }

        lock (_mediaGate)
        {
            _writer?.Dispose();
            _writer = null;
            _previewWriter?.Dispose();
            _previewWriter = null;
        }
        rec.EndUtc = DateTime.UtcNow;
        rec.Ongoing = false;
        _store.Save(rec);
        try { await thumbTask.ConfigureAwait(false); } catch { }
        Log.Info($"{_camera}: event ended ({string.Join("+", rec.Labels)}, " +
                 $"{(rec.EndUtc - rec.StartUtc).TotalSeconds:0}s{(rec.HasClip ? ", clip saved" : "")})");
    }

    private void StartClip(EventRecord rec)
    {
        lock (_mediaGate)
        {
            try
            {
                _writer = ClipWriter.TryCreate(Path.Combine(_store.EventDir(rec), "clip.mp4"), _hub);
                if (_writer == null)
                {
                    Log.Warn($"{_camera}: stream not ready; event stored without a clip");
                }
                else
                {
                    foreach (var (v, gap) in _preroll)
                        _writer.Add(v, gap);
                    _preroll.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"{_camera}: cannot start clip: {ex.Message}");
                _writer?.Dispose();
                _writer = null;
            }

            // The low-res twin from the sub stream, for the review strip's previews.
            if (_previewHub != null)
            {
                try
                {
                    _previewWriter = ClipWriter.TryCreate(Path.Combine(_store.EventDir(rec), "preview.mp4"), _previewHub);
                    if (_previewWriter != null)
                    {
                        foreach (var (v, gap) in _previewPreroll)
                            _previewWriter.Add(v, gap);
                        _previewPreroll.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"{_camera}: cannot start preview clip: {ex.Message}");
                    _previewWriter?.Dispose();
                    _previewWriter = null;
                }
            }
        }
        rec.HasClip = _writer != null;
        rec.HasPreview = _previewWriter != null;
        if (rec.HasClip || rec.HasPreview)
            _store.Save(rec);
    }

    /// <summary>Best-effort thumbnail via the camera's own JPEG snapshot command.</summary>
    private async Task CaptureThumbAsync(EventRecord rec, CancellationToken ct)
    {
        try
        {
            var jpeg = await _control.SnapshotAsync(ct).ConfigureAwait(false);
            if (jpeg is not { Length: > 100 } || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
                return; // not a JPEG (or camera doesn't support snapshots)
            await File.WriteAllBytesAsync(Path.Combine(_store.EventDir(rec), "thumb.jpg"), jpeg, ct)
                .ConfigureAwait(false);
            rec.HasThumb = true;
            _store.Save(rec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"{_camera}: event snapshot failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Camera AI classifications → normalized event labels.</summary>
    internal static List<string> LabelsOf(MotionPush push)
    {
        var labels = push.AiTypes.Select(t => t switch
        {
            "people" or "person" or "face" => "person",
            "vehicle" or "car" => "vehicle",
            "dog_cat" or "animal" or "pet" => "animal",
            "package" => "package",
            _ => t,
        }).Distinct().ToList();
        return labels.Count > 0 ? labels : new List<string> { "motion" };
    }
}
