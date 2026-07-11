// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
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
    private readonly IReadOnlyDictionary<string, IStreamHub>? _hubsByKind;
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
    private readonly List<(HubPacket Packet, bool Gap)> _preroll = new();
    private readonly List<(HubPacket Packet, bool Gap)> _previewPreroll = new();
    private ClipWriter? _writer;
    private ClipWriter? _previewWriter;
    /// <summary>The hub the record pump is subscribed to RIGHT NOW — clips must
    /// take their codec parameters from here, never from a just-changed selection.</summary>
    private volatile IStreamHub? _activeRecordHub;

    /// <param name="hubsByKind">The camera's streams by kind, enabling the per-camera
    /// "record from main/sub" runtime choice; null pins recording to <paramref name="hub"/>.</param>
    public EventRecorder(string camera, IStreamHub hub, ICameraControl control,
        EventStore store, RecordingConfig cfg, RecordingSettings settings,
        IStreamHub? previewHub = null, IReadOnlyDictionary<string, IStreamHub>? hubsByKind = null)
    {
        _camera = camera;
        _hub = hub;
        _previewHub = previewHub;
        _hubsByKind = hubsByKind;
        _control = control;
        _store = store;
        _cfg = cfg;
        _settings = settings;
    }

    /// <summary>The hub clips are cut from right now: the user's per-camera stream
    /// choice when set (and served), otherwise the configured default.</summary>
    private IStreamHub RecordHub()
    {
        var kind = _settings.Get(_camera).RecordStream;
        return kind != null && _hubsByKind != null && _hubsByKind.TryGetValue(kind, out var hub)
            ? hub
            : _hub;
    }

    public string Camera => _camera;

    /// <summary>Called from the camera connection for every alarm push (any thread).</summary>
    public void OnMotion(MotionPush push) => _pushes.Writer.TryWrite(push);

    // ------------------------------------------------------------------ recording status

    private volatile bool _eventActive;

    /// <summary>True while an event (camera detection or on-demand) is being captured.</summary>
    public bool EventInProgress => _eventActive;

    /// <summary>Fires when event capture starts (true) / ends (false) — the MQTT
    /// bridge mirrors it into the camera's "Recording" status sensor.</summary>
    public event Action<bool>? RecordingChanged;

    // ------------------------------------------------------------------ on-demand recording

    /// <summary>A running user-commanded recording (web UI record button / HA Record switch).</summary>
    public sealed record OnDemandSession(DateTime StartedUtc, DateTime EndsUtc)
    {
        public double RemainingSeconds => Math.Max(0, (EndsUtc - DateTime.UtcNow).TotalSeconds);
    }

    private readonly object _onDemandGate = new();
    private CancellationTokenSource? _onDemandStop;
    private volatile OnDemandSession? _onDemand;

    /// <summary>Fires on start (the session) and on end (null), whatever the trigger
    /// path — the MQTT bridge mirrors this onto the HA switch state.</summary>
    public event Action<OnDemandSession?>? OnDemandChanged;

    /// <summary>The on-demand recording in progress, if any.</summary>
    public OnDemandSession? OnDemand => _onDemand;

    /// <summary>On-demand capture needs the camera's master events switch on —
    /// the event loop discards every push, external or not, while it is off.</summary>
    public bool OnDemandAvailable => _settings.Get(_camera).Events;

    /// <summary>The cap every on-demand session runs to (recording.max_clip_seconds).</summary>
    public int OnDemandMaxSeconds => _cfg.MaxClipSeconds;

    /// <summary>
    /// Starts a user-commanded recording: synthetic "external" pushes open a normal
    /// event right away and keep it alive, so the clip machinery, labels, retention
    /// and HA event flow all behave exactly as for a camera detection. The session
    /// stops itself so ONE clip lands at ~MaxClipSeconds total (see the loop).
    /// Returns false when a session is already running or events are switched off.
    /// </summary>
    public bool StartOnDemand()
    {
        if (!OnDemandAvailable) return false;
        CancellationTokenSource cts;
        OnDemandSession session;
        lock (_onDemandGate)
        {
            if (_onDemand != null) return false;
            cts = new CancellationTokenSource();
            _onDemandStop = cts;
            var now = DateTime.UtcNow;
            session = new OnDemandSession(now, now.AddSeconds(_cfg.MaxClipSeconds));
            _onDemand = session;
        }
        Log.Info($"{_camera}: ⏺ on-demand recording started (up to {_cfg.MaxClipSeconds}s)");
        OnDemandChanged?.Invoke(session);
        _ = Task.Run(() => OnDemandLoopAsync(session, cts));
        return true;
    }

    /// <summary>Ends the running session early; the event still gets its normal
    /// post-roll. Returns false when nothing was running.</summary>
    public bool StopOnDemand()
    {
        CancellationTokenSource? cts;
        lock (_onDemandGate)
        {
            cts = _onDemandStop;
            _onDemandStop = null;
            if (cts != null) _onDemand = null; // state reads as "off" immediately
        }
        if (cts == null) return false;
        cts.Cancel();
        return true;
    }

    private async Task OnDemandLoopAsync(OnDemandSession session, CancellationTokenSource cts)
    {
        // Stop pulsing early enough that the post-roll closes the event just under
        // the MaxClipSeconds hard stop. Pulsing right up to the cap would let the
        // recorder cut the event at the cap while pulses keep coming — which would
        // open a second event and produce a surprise extra clip.
        var pulseUntil = session.StartedUtc.AddSeconds(
            Math.Max(2, _cfg.MaxClipSeconds - _cfg.PostSeconds - 2));
        bool stopped = false;
        try
        {
            while (DateTime.UtcNow < pulseUntil)
            {
                OnMotion(new MotionPush("MD", new[] { "external" }, External: true));
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        // One explicit all-clear arms the post-roll now instead of at quiet-timeout.
        OnMotion(new MotionPush("none", Array.Empty<string>(), External: true));
        if (!stopped)
        {
            // Ride out the post-roll: footage is still being written, so the UI
            // indicator (driven by this session) must stay on until the cap.
            var tail = session.EndsUtc - DateTime.UtcNow;
            if (tail > TimeSpan.Zero)
            {
                try { await Task.Delay(tail, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { stopped = true; }
            }
        }
        lock (_onDemandGate)
        {
            _onDemand = null;
            if (ReferenceEquals(_onDemandStop, cts)) _onDemandStop = null;
        }
        cts.Dispose();
        Log.Info($"{_camera}: on-demand recording {(stopped ? "stopped" : "reached its cap")} " +
                 $"({(DateTime.UtcNow - session.StartedUtc).TotalSeconds:0}s)");
        OnDemandChanged?.Invoke(null);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"{_camera}: event recording enabled ({_hub.Name}, pre={_cfg.PreSeconds}s, post={_cfg.PostSeconds}s" +
                 $"{(_previewHub != null ? $", previews from {_previewHub.Name}" : "")})");
        var pump = Task.Run(() => PumpRecordAsync(ct), CancellationToken.None);
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

    /// <summary>
    /// The record pump follows the user's stream choice: it resubscribes when the
    /// selection changes, but only while no clip is being written — one clip must
    /// stay a single codec/resolution end to end.
    /// </summary>
    private async Task PumpRecordAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var hub = RecordHub();
                var (id, reader) = hub.Subscribe();
                _activeRecordHub = hub;
                try
                {
                    long lastIndex = -1;
                    await foreach (var packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        if (!ReferenceEquals(RecordHub(), hub) && TrySwitchWhileIdle(hub))
                            break; // resubscribe to the newly selected stream

                        bool gap = lastIndex >= 0 && packet.Index != lastIndex + 1;
                        lastIndex = packet.Index;
                        if (packet is not HubVideo and not HubAudioAac) continue;
                        lock (_mediaGate)
                        {
                            if (_writer != null)
                            {
                                if (packet is HubVideo v) _writer.Add(v, gap);
                                else _writer.AddAudio((HubAudioAac)packet);
                                if (_writer.Faulted)
                                {
                                    Log.Warn($"{_camera}: clip writer failed; event continues without further video");
                                    _writer.Dispose();
                                    _writer = null;
                                }
                            }
                            else
                            {
                                _preroll.Add((packet, gap));
                                if (packet is HubVideo) TrimPreroll(_preroll);
                            }
                        }
                    }
                }
                finally
                {
                    hub.Unsubscribe(id);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Pre-roll of the old stream can't seed a clip of the new one — drop it on switch.</summary>
    private bool TrySwitchWhileIdle(IStreamHub from)
    {
        lock (_mediaGate)
        {
            if (_writer != null) return false; // mid-event: finish the clip first
            _preroll.Clear();
        }
        Log.Info($"{_camera}: event recording source {from.Name} → {RecordHub().Name}");
        return true;
    }

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

                if (packet is not HubVideo and not HubAudioAac) continue;
                lock (_mediaGate)
                {
                    var writer = preview ? _previewWriter : _writer;
                    if (writer != null)
                    {
                        // Add never blocks on disk (background writer thread); a dead
                        // disk surfaces as Faulted and the event continues clip-less.
                        if (packet is HubVideo v) writer.Add(v, gap);
                        else writer.AddAudio((HubAudioAac)packet);
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
                        buffer.Add((packet, gap));
                        if (packet is HubVideo) TrimPreroll(buffer);
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
    /// that still preserves the wanted span. Only called right after a VIDEO
    /// packet was appended; interleaved audio is trimmed along with its video.
    /// </summary>
    private void TrimPreroll(List<(HubPacket Packet, bool Gap)> buffer)
    {
        uint want = (uint)_cfg.PreSeconds * FMp4.Timescale;
        uint last = ((HubVideo)buffer[^1].Packet).RtpTs;
        int cut = -1;
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            if (buffer[i].Packet is not HubVideo { Keyframe: true } kv) continue;
            if (unchecked(last - kv.RtpTs) >= want)
            {
                cut = i;
                break;
            }
        }
        if (cut > 0)
            buffer.RemoveRange(0, cut);
        // Safety valve for streams with pathological keyframe intervals.
        if (buffer.Count > 8192)
            buffer.RemoveRange(0, buffer.Count - 8192);
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

            // Runtime switches (web UI): events off, outside the camera's capture
            // schedule, or every label of this push filtered out by the camera's
            // event-type selection → discard silently. External pushes (the HA
            // "Record" switch) are explicit user intent: only the master events
            // switch can veto them — no schedule, no type filter.
            var settings = _settings.Get(_camera);
            if (!settings.Events) continue;
            List<string> labels;
            if (push.External)
            {
                labels = LabelsOf(push);
            }
            else
            {
                if (!settings.ScheduleAllows(DateTime.Now)) continue; // schedules are wall-clock local
                labels = LabelsOf(push).Where(settings.AllowsLabel).ToList();
                if (labels.Count == 0) continue;
            }

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
        _eventActive = true;
        RecordingChanged?.Invoke(true);
        try
        {
            await RunEventCoreAsync(rec, ct).ConfigureAwait(false);
        }
        finally
        {
            _eventActive = false;
            RecordingChanged?.Invoke(false);
        }
    }

    private async Task RunEventCoreAsync(EventRecord rec, CancellationToken ct)
    {
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
                // External holds always extend: the switch is still on.
                var allowed = push.External
                    ? LabelsOf(push)
                    : LabelsOf(push).Where(_settings.Get(_camera).AllowsLabel).ToList();
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
            else if (active)
            {
                // Arm the post-roll ONLY on the active→quiet transition. Cameras
                // repeat all-clear pushes while idle, and re-arming on every one
                // kept events open until the MaxClipSeconds hard stop (the
                // suspicious wall of exactly-max-length clips in busy setups).
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
                _writer = ClipWriter.TryCreate(Path.Combine(_store.EventDir(rec), "clip.mp4"),
                    _activeRecordHub ?? _hub);
                if (_writer == null)
                {
                    Log.Warn($"{_camera}: stream not ready; event stored without a clip");
                }
                else
                {
                    // Audio before the first keyframe is ignored by the writer,
                    // so both tracks start on the same instant.
                    foreach (var (p, gap) in _preroll)
                    {
                        if (p is HubVideo v) _writer.Add(v, gap);
                        else if (p is HubAudioAac a) _writer.AddAudio(a);
                    }
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
                        foreach (var (p, gap) in _previewPreroll)
                        {
                            if (p is HubVideo v) _previewWriter.Add(v, gap);
                            else if (p is HubAudioAac a) _previewWriter.AddAudio(a);
                        }
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
            "visitor" or "doorbell" => "doorbell", // video doorbells: the button was pressed
            // Perimeter protection (smart events configured in the Reolink app).
            // Token spellings vary by firmware; extend as captures come in.
            "crossline" or "cross_line" or "tripwire" => "line-crossing",
            "intrude" or "intrusion" or "region" or "perimeter" => "intrusion",
            "linger" or "loiter" or "loitering" => "loitering",
            _ => t,
        }).Distinct().ToList();
        return labels.Count > 0 ? labels : new List<string> { "motion" };
    }
}
