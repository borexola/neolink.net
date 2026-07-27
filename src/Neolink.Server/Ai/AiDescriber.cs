// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Neolink.Recording;
using Neolink.Streaming;

namespace Neolink.Ai;

/// <summary>
/// One event's frame set: while the event records, the camera's own JPEG
/// snapshot command is sampled — the Baichuan snap serves the low-res
/// sub-stream image, so no server-side video decoding is ever needed. The
/// event's OPENING seconds get one paced shot per second, and those frames are
/// kept at full density however long the event runs — the subject that
/// triggered the event is in them. The rest of the budget SPREADS across the
/// remainder: sampling continues at one frame per second, and whenever the
/// budget fills, every other stored tail frame is dropped and the interval
/// doubles. The final set is the dense opening plus frames spanning the entire
/// tail (a person leaving 30s in tells a different story than the first 10s),
/// stays within budget, and each frame remembers when it was taken so the model
/// can be told the real offsets. Frames live in memory only and die with the
/// capture unless the event completes and the job is submitted.
/// </summary>
public sealed class AiCapture
{
    private readonly ICameraControl _control;
    private readonly int _budget;
    private readonly TimeSpan _startInterval;
    private readonly CancellationTokenSource _stop;
    private volatile bool _discarded;
    private int _disposeArmed;

    internal string Camera { get; }
    internal List<(DateTime Utc, byte[] Jpeg)> Frames { get; } = new();
    internal Task Completion { get; }
    /// <summary>The recorder's pre-roll at event start (compressed packet refs) —
    /// decoded into leading frames at describe time, if ffmpeg is around.</summary>
    internal AiPrerollVideo? Preroll { get; }
    /// <summary>Stream-tap mode (ffmpeg + a flowing stream): instead of asking
    /// the camera for snapshots, the capture listens to this hub and keeps the
    /// event's own KEYFRAMES (compressed refs; an IDR decodes standalone) on the
    /// same budget/thinning contract. Costs the camera nothing — the frames are
    /// already flowing for the recording — and never touches the HTTP session
    /// pool the snapshot fallback chain drains (max-session lockout, live
    /// 2026-07-26). Null = classic snapshot polling.</summary>
    internal IStreamHub? StreamHub { get; }
    internal List<(DateTime Utc, byte[] AnnexB)> StreamPackets { get; } = new();

    internal AiCapture(string camera, ICameraControl control, int budget,
        TimeSpan startInterval, CancellationToken ct, AiPrerollVideo? preroll = null,
        IStreamHub? streamHub = null)
    {
        Camera = camera;
        _control = control;
        Preroll = preroll;
        StreamHub = streamHub;
        _budget = Math.Max(2, budget); // decimation needs headroom to halve
        _startInterval = startInterval < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1) : startInterval;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Completion = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>Event over: stop sampling, keep what was captured. The loop may
    /// already be done (frame budget spent, or the camera gave up) — the CTS is
    /// only disposed via <see cref="DisposeWhenDone"/>, so Cancel stays legal.</summary>
    internal void Stop() => _stop.Cancel();

    /// <summary>Tentative event discarded: stop and never send anything.</summary>
    public void Cancel()
    {
        _discarded = true;
        _stop.Cancel();
        DisposeWhenDone(); // nobody will submit this capture — clean up here
    }

    internal bool Discarded => _discarded;

    /// <summary>Disposes the linked CTS once the capture loop has fully exited
    /// (its cancellation registration would otherwise outlive the event). Called
    /// exactly once, by whichever path ends this capture's life: the worker after
    /// processing, Cancel(), or a failed submit.</summary>
    internal void DisposeWhenDone()
    {
        if (Interlocked.Exchange(ref _disposeArmed, 1) != 0) return;
        Completion.ContinueWith(static (_, s) => ((CancellationTokenSource)s!).Dispose(),
            _stop, TaskScheduler.Default);
    }

    /// <summary>The event's first seconds get one PACED shot per second, and
    /// frames landed in this window are exempt from decimation — the subject
    /// that triggered the event is often FAST (a car passing takes 2-3 seconds)
    /// and the pre-roll footage it appears in cannot be sampled after the fact,
    /// so the opening seconds are the best look at it and stay at full density
    /// however long the event then runs. One shot per second, hit or miss: a
    /// failed second is one missing frame, never a reason to stop or slow the
    /// slots that remain.</summary>
    private const int OpeningSeconds = 5;

    private async Task RunAsync()
    {
        if (StreamHub != null)
        {
            await RunStreamTapAsync().ConfigureAwait(false);
            return;
        }
        int failures = 0; // consecutive misses; any good frame resets
        int locked = 0;   // opening-window frames, exempt from decimation
        var launched = DateTime.UtcNow;
        var interval = _startInterval; // doubles at every decimation
        // The spread phase keeps at least two budget slots — decimation needs
        // headroom to halve (the Math.Max(2, budget) floor, same reason).
        int maxLocked = Math.Clamp(_budget - 2, 0, OpeningSeconds);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var t0 = DateTime.UtcNow;
                bool opening = locked < maxLocked
                    && t0 - launched < TimeSpan.FromSeconds(OpeningSeconds);
                bool got = false;
                try
                {
                    // Per-shot deadline so one hung command can't silently eat the
                    // whole window; SnapshotSmall is the size-limited variant (the
                    // HTTP API scales server-side, Baichuan answers sub-stream).
                    // Opening slots stay tight — miss one second, try the next.
                    // Afterwards the deadline WIDENS with consecutive misses: the
                    // fallback chain behind SnapshotSmall allows ~20s per HTTP
                    // tier before the Baichuan snap, and a camera busy starting
                    // the event's streams needs one patient attempt, not another
                    // hasty one.
                    using var shot = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    shot.CancelAfter(TimeSpan.FromSeconds(
                        opening || failures == 0 ? 5 : failures == 1 ? 20 : 45));
                    Interlocked.Increment(ref _attempts);
                    var jpeg = await _control.SnapshotSmallAsync(shot.Token).ConfigureAwait(false);
                    // Stamped at ARRIVAL, not at request start: the patient retry
                    // deadline stretches to 45s, and a frame the camera finally
                    // answered a minute in must not be labeled with the offset of
                    // the second the command was issued — the model is told these
                    // offsets as truth, so they have to be when the frame is FROM.
                    var taken = DateTime.UtcNow;
                    if (jpeg is { Length: > 100 } && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                    {
                        lock (Frames)
                        {
                            if (opening)
                            {
                                locked++;
                            }
                            else if (Frames.Count >= _budget)
                            {
                                // Budget full: thin the tail, never the opening.
                                ThinTail(Frames, locked);
                                interval += interval;
                            }
                            Frames.Add((taken, jpeg));
                        }
                        failures = 0;
                        got = true;
                    }
                    else
                    {
                        failures++;
                        _lastMiss = jpeg == null
                            ? "the camera answered the snapshot command with nothing"
                            : $"the camera answered {jpeg.Length} bytes that are not a JPEG";
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    _lastMiss = Log.Flatten(ex);
                    Log.Debug($"{Camera}: AI frame capture miss: {_lastMiss}");
                }
                // Pacing. The opening window ticks at one shot per second, hit
                // or miss. After it, successes pace to the current interval and
                // misses BACK OFF instead of giving up: events run minutes, and
                // a camera too busy to answer while its streams spin up usually
                // answers fine moments later — walking away after three early
                // strikes cost every frame of a 4-minute event (live 2026-07-25).
                // A camera with no snapshot support at all costs one failed
                // command per ~30s, and only while an event records.
                var spent = DateTime.UtcNow - t0;
                var slot = opening ? TimeSpan.FromSeconds(1)
                         : got ? interval
                         : RetryPause(failures);
                if (slot > spent)
                {
                    try { await Task.Delay(slot - spent, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception ex)
        {
            // Belt and braces: the loop body already contains its failures, and
            // nothing here may ever surface into the event lifecycle.
            Log.Debug($"{Camera}: AI frame capture aborted: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Stream-tap mode: listen to the hub and keep keyframes on the
    /// same opening/budget/thin-and-double contract as the snapshot loop —
    /// keyframes arrive at the camera's GOP cadence (typically 2-4s), so the
    /// pacing interval acts as a floor, not a metronome. Packets are shared
    /// byte-array refs; they die with the capture.</summary>
    private async Task RunStreamTapAsync()
    {
        var hub = StreamHub!;
        var launched = DateTime.UtcNow;
        var interval = _startInterval;
        int locked = 0;
        int maxLocked = Math.Clamp(_budget - 2, 0, OpeningSeconds);
        var lastKept = DateTime.MinValue;
        var (id, reader) = hub.Subscribe(viewer: false);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                HubPacket packet;
                try
                {
                    packet = await reader.ReadAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; } // stream ended; keep what we have
                if (packet is not HubVideo { Keyframe: true } kv) continue;
                var now = DateTime.UtcNow;
                bool opening = locked < maxLocked
                    && now - launched < TimeSpan.FromSeconds(OpeningSeconds);
                // The 200ms grace keeps a GOP that lands just short of the
                // interval from being skipped for a whole further GOP.
                if (!opening && now - lastKept < interval - TimeSpan.FromMilliseconds(200))
                    continue;
                lock (StreamPackets)
                {
                    if (opening)
                    {
                        locked++;
                    }
                    else if (StreamPackets.Count >= _budget)
                    {
                        ThinTail(StreamPackets, locked);
                        interval += interval;
                    }
                    StreamPackets.Add((now, kv.AnnexB));
                }
                lastKept = now;
                Interlocked.Increment(ref _attempts);
            }
        }
        catch (Exception ex)
        {
            // Same contract as the snapshot loop: nothing here may ever surface
            // into the event lifecycle.
            Log.Debug($"{Camera}: AI stream tap aborted: {Log.Flatten(ex)}");
        }
        finally
        {
            hub.Unsubscribe(id);
        }
    }

    /// <summary>Budget full: drop every other frame BEYOND the protected opening
    /// prefix (<paramref name="locked"/> frames). The survivors still span the
    /// whole tail so far; the opening keeps its full one-per-second density.</summary>
    internal static void ThinTail(List<(DateTime Utc, byte[] Jpeg)> frames, int locked)
    {
        for (int k = frames.Count - 1; k > locked; k -= 2)
            frames.RemoveAt(k);
    }

    /// <summary>Pause before the next attempt after N consecutive misses:
    /// 4, 8, 16, then 30s flat — patient enough to ride out a busy event start,
    /// cheap enough to run for the life of a long event.</summary>
    internal static TimeSpan RetryPause(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures + 1, 5)));

    private int _attempts;
    private volatile string? _lastMiss;

    /// <summary>Snapshot attempts made (successful or not) — for the skip log.</summary>
    internal int Attempts => _attempts;
    /// <summary>What the last failed attempt said — the skip log's diagnosis.</summary>
    internal string? LastMiss => _lastMiss;

}

/// <summary>
/// Sends each completed detection event's frame burst to an OpenAI-style
/// chat-completions endpoint (LM Studio and friends) and stores the model's
/// description on the event. Deliberately fire-and-forget from the recorder's
/// point of view: jobs queue on a small bounded channel and one background
/// worker drains it, so a slow or dead LLM can never back-pressure recording,
/// streaming or anything else — when the queue is full, new jobs are dropped
/// (and say so in the log).
/// </summary>
public sealed class AiDescriber
{
    private sealed record Job(AiCapture Capture, EventRecord Record);

    // Small on purpose: each queued job holds its frames in memory, and a queue
    // deeper than this means the model can't keep up anyway.
    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(
        new BoundedChannelOptions(8) { SingleReader = true });

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly AiStore _store;
    private readonly EventStore _events;
    private readonly Func<string, bool> _cameraOptIn;
    private readonly Func<string, string?>? _cameraContext;
    // Events queued or in flight, so the UI can say "describing…" instead of
    // showing nothing while the model works. Ids only — jobs hold the frames.
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    public AiDescriber(AiStore store, EventStore events, Func<string, bool> cameraOptIn,
        Func<string, string?>? cameraContext = null)
    {
        _store = store;
        _events = events;
        _cameraOptIn = cameraOptIn;
        _cameraContext = cameraContext;
    }

    /// <summary>Both gates in one place: the global switch AND this camera's opt-in.
    /// Checked per event (not at wiring), so settings changes apply immediately.</summary>
    public bool WantsCapture(string camera) => _store.Enabled && _cameraOptIn(camera);

    /// <summary>Fires after a description (and threat level) lands on an event and
    /// is persisted — the MQTT bridge mirrors it into the camera's sensors.</summary>
    public event Action<EventRecord>? Described;

    /// <summary>Starts the frame capture for a starting event; null when the
    /// feature is off for this camera. <paramref name="preroll"/> is the recorder's
    /// pre-roll copy (the seconds BEFORE the trigger), decoded at describe time;
    /// a non-null <paramref name="streamHub"/> switches the capture to the
    /// stream tap (the event's own keyframes) instead of snapshot polling.</summary>
    public AiCapture? TryBeginCapture(string camera, ICameraControl control, CancellationToken ct,
        AiPrerollVideo? preroll = null, IStreamHub? streamHub = null)
    {
        if (!WantsCapture(camera)) return null;
        var cfg = _store.Snapshot();
        // The two knobs compose in one machine: sample about every N seconds
        // (source permitting), and when the cap fills, thin-and-double so the
        // kept set spans the whole event. The old budget/fixed-rate mode
        // switch was only ever two presets of exactly this.
        return new AiCapture(camera, control, Math.Max(1, cfg.MaxFrames),
            TimeSpan.FromSeconds(Math.Clamp(cfg.SampleEverySeconds, 1, 600)), ct, preroll, streamHub);
    }

    /// <summary>Event closed and saved: stop sampling and queue the description
    /// job. Never blocks — a full queue drops the job with a log line.</summary>
    public void Submit(AiCapture capture, EventRecord rec)
    {
        capture.Stop();
        if (capture.Discarded) return;
        _pending[rec.Id] = 1;
        if (!_jobs.Writer.TryWrite(new Job(capture, rec)))
        {
            Log.Warn($"{capture.Camera}: AI describe queue is full — event {rec.Id} skipped " +
                     "(the model is not keeping up with the event rate)");
            _pending.TryRemove(rec.Id, out _);
            capture.DisposeWhenDone();
        }
    }

    /// <summary>True while the event's description is queued or being generated —
    /// the web UI shows "describing…" instead of silently missing text.</summary>
    public bool IsPending(string eventId) => _pending.ContainsKey(eventId);

    /// <summary>The worker: one job at a time, every failure contained.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(job, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"{job.Capture.Camera}: AI describe failed: {Log.Flatten(ex)}");
                }
                finally
                {
                    _pending.TryRemove(job.Record.Id, out _);
                    job.Capture.DisposeWhenDone();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(Job job, CancellationToken ct)
    {
        // The capture normally finished with the event; the bound is a seatbelt.
        try { await job.Capture.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        catch (TimeoutException) { }

        var cfg = _store.Snapshot();
        if (!cfg.Enabled || job.Capture.Discarded) return; // switched off since capture
        List<(DateTime Utc, byte[] Jpeg)> frames;
        lock (job.Capture.Frames) frames = job.Capture.Frames.ToList();
        if (cfg.ActiveUrl() is not { } url)
        {
            Log.Warn($"{job.Capture.Camera}: AI describe skipped — the " +
                     $"{(cfg.UsesOllama ? "Ollama" : cfg.UsesAnthropic ? "Anthropic-style" : "OpenAI-style")} " +
                     $"endpoint '{(cfg.UsesOllama ? cfg.OllamaEndpoint : cfg.UsesAnthropic ? cfg.AnthropicEndpoint : cfg.Endpoint)}' " +
                     "is not a usable http(s) URL");
            return;
        }

        var rec = job.Record;
        // Stream-tap mode: the frames ARE the event's own keyframes, decoded
        // here in one ffmpeg pass — no snapshot command was ever sent. A failed
        // decode falls through to whatever the snapshot list holds (usually
        // nothing in this mode) and then to the pre-roll below.
        bool streamMode = false;
        List<(DateTime Utc, byte[] AnnexB)> taps;
        lock (job.Capture.StreamPackets) taps = job.Capture.StreamPackets.ToList();
        if (taps.Count > 0 && job.Capture.StreamHub is { Codec: { } tapCodec } tapHub)
        {
            var decoded = await AiPreroll.DecodeFramesAsync(tapCodec, tapHub.Vps, tapHub.Sps,
                tapHub.Pps, taps, ct).ConfigureAwait(false);
            if (decoded.Count > 0)
            {
                frames = decoded;
                streamMode = true;
            }
            else
            {
                Log.Warn($"{rec.Camera}: stream-tap decode produced no frames from " +
                         $"{taps.Count} keyframe(s) — the event falls back to snapshots/pre-roll");
            }
        }
        // The pre-roll seconds, decoded here on the worker (never on the event
        // path): the trigger moment itself, which the live burst structurally
        // missed. They PREPEND — oldest first still holds, offsets go negative.
        // A short event whose burst came up empty can still be described by
        // these alone.
        if (job.Capture.Preroll is { } preroll)
        {
            var pre = await AiPreroll.ExtractAsync(preroll, rec.StartUtc, ct).ConfigureAwait(false);
            if (pre.Count > 0)
            {
                frames.InsertRange(0, pre);
                Log.Info($"{rec.Camera}: {pre.Count} pre-roll frame(s) join the AI set " +
                         "(the moments before the trigger)");
            }
        }
        if (frames.Count == 0)
        {
            Log.Warn($"{job.Capture.Camera}: AI describe skipped — no frames captured (" +
                     (job.Capture.StreamHub != null
                         ? "the stream tap caught no decodable keyframes"
                         : job.Capture.Attempts == 0
                             ? "the event ended before a snapshot could be asked for"
                             : $"all {job.Capture.Attempts} snapshot attempt(s) failed; " +
                               $"last: {job.Capture.LastMiss ?? "no detail"}") + ")");
            return;
        }
        // Some cameras answer the "small" snapshot command with the FULL
        // resolution picture (~5 MB a frame) — a long event's payload then
        // outgrows what chat endpoints accept (a 234 MB body broke the pipe,
        // live 2026-07-26). With ffmpeg around, oversized frames shrink to
        // model size here; without it, the byte-capped parts below keep every
        // request deliverable on its own.
        int oversized = frames.Count(f => f.Jpeg.Length > AiPreroll.OversizeBytes);
        if (oversized > 0)
        {
            long before = frames.Sum(f => (long)f.Jpeg.Length);
            if (AiPreroll.FfmpegPath != null)
            {
                frames = await AiPreroll.ShrinkAsync(frames, ct).ConfigureAwait(false);
                long after = frames.Sum(f => (long)f.Jpeg.Length);
                if (after < before)
                    Log.Info($"{rec.Camera}: downscaled {oversized} full-resolution frame(s) " +
                             $"for the model ({before / 1024 / 1024} MB → {after / 1024 / 1024} MB — " +
                             "this camera's snapshot command ignores the size request)");
            }
            else if (before > MaxBytesPerRequest)
            {
                Log.Info($"{rec.Camera}: {frames.Count} frame(s) total {before / 1024 / 1024} MB — " +
                         "this camera's snapshot command serves full-resolution images, so the " +
                         "event goes to the model in byte-capped parts; installing ffmpeg (or " +
                         "setting NEOLINK_FFMPEG) would downscale the frames to a fraction of " +
                         "the size and time");
            }
        }

        var modelName = cfg.UsesOllama ? cfg.OllamaModel : cfg.UsesAnthropic ? cfg.AnthropicModel : cfg.Model;
        // Coverage up front: "12 frames over 14s of the 15s event" answers the
        // first question a puzzling description raises — what did the model see?
        long bytes = frames.Sum(f => (long)f.Jpeg.Length);
        int coveredSecs = (int)(frames[^1].Utc - frames[0].Utc).TotalSeconds;
        int eventSecs = Math.Max(1, (int)(rec.EndUtc - rec.StartUtc).TotalSeconds);
        Log.Info($"{rec.Camera}: 🧠 describing event ({frames.Count} frame(s) over " +
                 $"{coveredSecs}s of the {eventSecs}s event, {bytes / 1024} KB" +
                 $"{(streamMode ? ", stream-tap" : "")} " +
                 $"→ {url.GetLeftPart(UriPartial.Authority)}" +
                 $"{(string.IsNullOrWhiteSpace(modelName) ? "" : $", model {modelName}")})");
        if (cfg.KeepFrames)
            await KeepFramesAsync(rec, frames, ct).ConfigureAwait(false); // before the call: reviewable even if the model fails

        // Very long events (fixed-rate mode especially) — and full-resolution
        // frames a missing ffmpeg couldn't shrink — can outgrow a single
        // request: parts are capped by FRAME COUNT and by PAYLOAD BYTES, sent
        // in order, each part told what came before it, and the answers append
        // to the same event. The final threat level is the most severe any part
        // reported.
        var chunks = ChunkFrames(frames, MaxFramesPerRequest, MaxBytesPerRequest);
        if (chunks.Count > 1)
            Log.Info($"{rec.Camera}: long event — describing in {chunks.Count} parts " +
                     $"of up to {MaxFramesPerRequest} frames / {MaxBytesPerRequest / 1024 / 1024} MB each");

        var sw = Stopwatch.StartNew();
        string? model = null;
        long usageSum = 0;
        string? level = null;
        var parts = new List<string>();
        // Fetched per event (not per part, not at wiring): settings edits apply
        // to the next event, and every part of one event sees the same notes.
        var sceneNotes = _cameraContext?.Invoke(rec.Camera);
        for (int c = 0; c < chunks.Count; c++)
        {
            var chunk = chunks[c];
            // The carry-forward is ALL earlier parts, not just the last one — a
            // subject from part 1 must not resurface in part 3 as a stranger.
            var userText = BuildUserText(rec, chunk, c + 1, chunks.Count,
                prevSummary: parts.Count > 0 ? string.Join(" ", parts) : null,
                sceneNotes: sceneNotes);
            // Every image travels with its own inline label ("Frame 3 of 12 —
            // +4s…") so the timestamp sits ADJACENT to its picture on backends
            // that allow interleaving — a single offsets list up front provably
            // smears: the model loses which image is which and starts narrating.
            var jpegs = chunk.Select((f, i) => (f.Jpeg, (string?)FrameLabel(i, chunk.Count,
                (int)(f.Utc - rec.StartUtc).TotalSeconds))).ToList();
            (string? raw, string? model, long? usage) reply;
            try
            {
                try
                {
                    reply = await CompleteAsync(cfg, _store.ActiveApiKey(cfg), userText, jpegs,
                        classify: true, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    // Connection-level failure only (refused, reset, DNS) — the
                    // server is probably restarting or swapping models; one delayed
                    // retry rescues the description. Slow answers (timeouts) are NOT
                    // retried: doubling the wait on a struggling model would only
                    // dig the queue deeper.
                    Log.Info($"{rec.Camera}: AI endpoint unreachable ({Log.Flatten(ex)}) — retrying once in 10s");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    reply = await CompleteAsync(cfg, _store.ActiveApiKey(cfg), userText, jpegs,
                        classify: true, ct).ConfigureAwait(false);
                }
            }
            catch (Exception) when (parts.Count > 0 && !ct.IsCancellationRequested)
            {
                // A later part failing must not throw away the parts that already
                // answered — save what the model DID say and note the cut.
                Log.Warn($"{rec.Camera}: part {c + 1} of {chunks.Count} failed — saving the " +
                         $"description of the first {c} part(s)");
                break;
            }
            var (raw, replyModel, chunkUsage) = reply;
            model ??= replyModel;
            usageSum += chunkUsage ?? 0;
            var (chunkLevel, chunkText) = SplitLevel(raw);
            if (!string.IsNullOrWhiteSpace(chunkText)) parts.Add(chunkText!);
            level = MoreSevere(level, chunkLevel);
        }
        sw.Stop();
        string? text = parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join("\n\n", parts),
        };
        long? usage = usageSum > 0 ? usageSum : null;

        if (level == null && string.IsNullOrWhiteSpace(text))
        {
            Log.Warn($"{rec.Camera}: AI describe returned an empty answer after {sw.Elapsed.TotalSeconds:0.0}s");
            return;
        }

        // The event may have been deleted (retention, user) while the model
        // thought — Save() on an unknown id is a no-op, but don't log success.
        if (_events.Find(rec.Id) == null)
        {
            Log.Info($"{rec.Camera}: AI description arrived after the event was deleted — discarded");
            return;
        }
        rec.AiDescription = text;
        rec.AiLevel = level;
        rec.AiModel = model;
        rec.AiDescribedUtc = DateTime.UtcNow;
        _events.Save(rec);
        Log.Info($"{rec.Camera}: 🧠 event described in {sw.Elapsed.TotalSeconds:0.0}s" +
                 $"{(level == null ? "" : $" [{level.ToUpperInvariant()}]")}" +
                 $"{(usage == null ? "" : $" ({usage} tokens)")}: \"{text ?? "(no description)"}\"");
        Described?.Invoke(rec);
    }

    /// <summary>Stores the exact JPEGs going to the model in the event's folder
    /// (ai-frames/, one file per frame with its time offset in the name) so a
    /// puzzling description can be checked against what the model saw. PLAIN
    /// files on purpose — they exist to be opened, so the footage vault is
    /// deliberately not used (the clip beside them stays encrypted when the
    /// vault is on). They are deleted together with the event.</summary>
    private async Task KeepFramesAsync(EventRecord rec,
        IReadOnlyList<(DateTime Utc, byte[] Jpeg)> frames, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(_events.EventDir(rec), "ai-frames");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < frames.Count; i++)
            {
                // Pre-roll frames land before the event start; "m3s" = minus 3s
                // (a literal '-' here would read as just another name separator).
                int off = (int)(frames[i].Utc - rec.StartUtc).TotalSeconds;
                await File.WriteAllBytesAsync(
                    Path.Combine(dir, $"{i + 1:000}-{(off < 0 ? $"m{-off}" : $"{off}")}s.jpg"),
                    frames[i].Jpeg, ct).ConfigureAwait(false);
            }
            Log.Info($"{rec.Camera}: kept the {frames.Count} frame(s) sent to the model in {dir}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Review copies are best-effort; the description must still happen.
            Log.Debug($"{rec.Camera}: could not keep the AI frames: {Log.Flatten(ex)}");
        }
    }

    /// <summary>
    /// Peels the threat level off the answer per <see cref="AiSettings.LevelProtocol"/>:
    /// the first word (allowing markdown litter and same-line continuation) when it
    /// is GREEN/YELLOW/RED; anything else leaves the level null and the text whole.
    /// </summary>
    internal static (string? Level, string? Text) SplitLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        // Accepts the bare word and the "Threat level: X" spelling some models
        // insist on — but ONLY as a prefix, so "a man in a RED jacket" in a
        // level-less answer can never be misread as a verdict.
        var m = Regex.Match(text,
            @"^[\s*#>_`\-]*(?:threat\s*level\s*[:=\-]\s*)?[*_`]*(GREEN|YELLOW|RED)\b[.:,;!\s*_`\-—]*",
            RegexOptions.IgnoreCase);
        if (!m.Success) return (null, text.Trim());
        var rest = text[m.Length..].Trim();
        return (m.Groups[1].Value.ToLowerInvariant(), rest.Length == 0 ? null : rest);
    }

    /// <summary>The inline label sent immediately before each image, binding the
    /// picture to its place and time — "Frame 3 of 12 — +4s into the event:".
    /// Negative offsets are pre-roll (decoded from before the trigger) and say so
    /// in words — a bare "-3s" invites the model to misread it as a typo.</summary>
    internal static string FrameLabel(int index, int count, int offsetSeconds) =>
        offsetSeconds < 0
            ? $"Frame {index + 1} of {count} — {-offsetSeconds}s BEFORE the trigger (pre-roll):"
            : $"Frame {index + 1} of {count} — +{offsetSeconds}s into the event:";

    /// <summary>No more than this many frames ride one model request. A very long
    /// event (fixed-rate sampling on a half-hour clip) is described in ordered
    /// parts instead of one context-blowing payload — each part is told what the
    /// previous part saw, and the answers append to the same event.</summary>
    internal const int MaxFramesPerRequest = 100;

    /// <summary>No part may carry more than this much raw JPEG either (base64
    /// adds a third on top). Chat endpoints slam the connection shut on huge
    /// bodies — "Broken pipe" mid-upload, live 2026-07-26 at 210+ MB, while
    /// 28 MB sailed through; 24 MB stays under the proven ceiling.</summary>
    internal const long MaxBytesPerRequest = 24_000_000;

    /// <summary>The frame list in ordered slices, each at most
    /// <paramref name="maxFrames"/> frames AND <paramref name="maxBytes"/> of
    /// JPEG. A single frame over the byte budget still travels — alone.</summary>
    internal static List<List<(DateTime Utc, byte[] Jpeg)>> ChunkFrames(
        IReadOnlyList<(DateTime Utc, byte[] Jpeg)> frames, int maxFrames, long maxBytes)
    {
        var result = new List<List<(DateTime Utc, byte[] Jpeg)>>();
        var part = new List<(DateTime Utc, byte[] Jpeg)>();
        long bytes = 0;
        foreach (var f in frames)
        {
            if (part.Count > 0 && (part.Count >= maxFrames || bytes + f.Jpeg.Length > maxBytes))
            {
                result.Add(part);
                part = new List<(DateTime Utc, byte[] Jpeg)>();
                bytes = 0;
            }
            part.Add(f);
            bytes += f.Jpeg.Length;
        }
        if (part.Count > 0) result.Add(part);
        return result;
    }

    /// <summary>The more severe of two threat levels (red > yellow > green): a
    /// long event's final level is the worst any of its parts reported.</summary>
    internal static string? MoreSevere(string? a, string? b)
    {
        static int Rank(string? l) => l switch
        {
            "red" => 3, "yellow" => 2, "green" => 1, _ => 0,
        };
        return Rank(a) >= Rank(b) ? a ?? b : b;
    }

    internal static string BuildUserText(EventRecord rec, IReadOnlyList<(DateTime Utc, byte[] Jpeg)> frames,
        int part, int parts, string? prevSummary, string? sceneNotes = null)
    {
        var local = rec.StartUtc.ToLocalTime();
        // Real per-frame offsets: sampling spreads across the whole event (the
        // interval grows as it runs), and telling the model where each frame
        // sits lets it narrate "at first … then 30 seconds later …" truthfully.
        // Negative = pre-roll, decoded from the seconds before the trigger.
        var offsets = string.Join(", ",
            frames.Select(f => (int)(f.Utc - rec.StartUtc).TotalSeconds)
                .Select(s => s < 0 ? $"-{-s}s" : $"+{s}s"));
        // The trigger labels as an ASSIGNMENT, not metadata — paired with the
        // default prompt's "find it first" guide, both halves point at each
        // other. The camera's detector is the one witness that saw the event
        // at full resolution.
        var text = $"Camera \"{rec.Camera}\" recorded this event; its own detector " +
                   $"triggered on: {string.Join(", ", rec.Labels)} — these are the " +
                   $"subjects to look for. " +
                   $"Event started {local:yyyy-MM-dd HH:mm:ss} (local) and lasted " +
                   $"{Math.Max(1, (int)(rec.EndUtc - rec.StartUtc).TotalSeconds)}s. ";
        if (!string.IsNullOrWhiteSpace(sceneNotes))
        {
            // The owner's own words about what this camera watches — the model's
            // only source of "what is normal HERE". Framed as context, so notes
            // can calibrate the threat call but never overwrite what frames show.
            text += $"The owner's notes about this camera's scene (context for judging what " +
                    $"is routine here, not a description of these frames): {sceneNotes.Trim()} ";
        }
        if (frames.Count > 0 && frames[0].Utc < rec.StartUtc)
        {
            // Only when pre-roll frames actually made it in — an unexplained
            // negative offset would otherwise read like an error to the model.
            text += "Frames at negative offsets were decoded from the recorder's pre-roll — " +
                    "the moments just BEFORE the trigger, usually showing what caused it. ";
        }
        if (parts > 1)
        {
            // A long event arrives in ordered slices: the model must know it is
            // seeing a WINDOW of the event (and what happened before it), or the
            // first and last frames of every part would read as beginnings and
            // endings that never happened.
            text += $"This request covers part {part} of {parts} of that event. ";
            if (prevSummary != null)
                text += $"What the earlier frames showed (already described): {prevSummary} ";
            text += $"The {frames.Count} frame(s) below are the next window, oldest first, " +
                    $"taken at {offsets} after the event's start. Continue the description " +
                    "from where the earlier part left off; describe only this window.";
        }
        else
        {
            text += $"The {frames.Count} frame(s) below span the event, oldest first, " +
                    $"taken at {offsets} after its start.";
        }
        return text;
    }

    /// <summary>One chat request against the active backend (OpenAI-style or
    /// Ollama native). Returns the cleaned answer (level line still attached — the
    /// caller splits it), the model the server says it used, and a token count.</summary>
    private static async Task<(string? Text, string? Model, long? Tokens)> CompleteAsync(
        AiSettings cfg, string apiKey, string userText, IReadOnlyList<(byte[] Jpeg, string? Label)> frames,
        bool classify, CancellationToken ct)
    {
        // NoThink rides the prompt ("/no_think", the Qwen-family convention, which
        // Ollama templates honor too); <think> blocks are stripped either way.
        // Claude models don't use the marker — it would just be prompt noise.
        // Grounding rules ride event requests only (classify) — the Test button's
        // probe stays a bare connectivity check.
        var system = cfg.EffectivePrompt
                     + (classify ? "\n\n" + AiSettings.GroundingProtocol
                                 + "\n\n" + AiSettings.LevelProtocol : "")
                     + (cfg.NoThink && !cfg.UsesAnthropic ? " /no_think" : "");
        object payload;
        if (cfg.UsesAnthropic)
        {
            if (string.IsNullOrWhiteSpace(cfg.AnthropicModel))
                throw new InvalidOperationException(
                    "the Anthropic backend needs a vision-capable model name — set one in Settings → AI");
            // Messages API: system is a top-level field, images are base64 source
            // blocks, and max_tokens is REQUIRED. Each image is PRECEDED by its
            // own label block, so its timestamp cannot detach from the picture
            // (a single offsets list up front provably smears — see FrameLabel).
            var blocks = new List<object> { new { type = "text", text = userText } };
            foreach (var (jpeg, label) in frames)
            {
                if (label != null) blocks.Add(new { type = "text", text = label });
                blocks.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = Convert.ToBase64String(jpeg),
                    },
                });
            }
            payload = new Dictionary<string, object>
            {
                ["model"] = cfg.AnthropicModel.Trim(),
                ["max_tokens"] = 1024,
                ["system"] = system,
                ["messages"] = new object[] { new { role = "user", content = blocks } },
                ["temperature"] = 0.2,
            };
        }
        else if (cfg.UsesOllama)
        {
            if (string.IsNullOrWhiteSpace(cfg.OllamaModel))
                throw new InvalidOperationException(
                    "Ollama needs a vision-capable model name (it has no \"currently loaded\" " +
                    "default) — set one in Settings → AI");
            // Native /api/chat: images are a flat base64 array on the user message
            // — no interleaving possible, so the per-frame labels join the text as
            // a numbered list instead (in the same order as the images array).
            var labels = frames.Where(f => f.Label != null).Select(f => f.Label!).ToList();
            payload = new Dictionary<string, object>
            {
                ["model"] = cfg.OllamaModel.Trim(),
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new
                    {
                        role = "user",
                        content = labels.Count == 0
                            ? userText
                            : userText + "\nThe images are attached in this order:\n"
                              + string.Join("\n", labels),
                        images = frames.Select(f => Convert.ToBase64String(f.Jpeg)).ToArray(),
                    },
                },
                ["stream"] = false,
                ["options"] = new { temperature = 0.2 },
            };
        }
        else
        {
            // Interleaved like the Anthropic path: label block, then its image.
            var content = new List<object> { new { type = "text", text = userText } };
            foreach (var (jpeg, label) in frames)
            {
                if (label != null) content.Add(new { type = "text", text = label });
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                        detail = "low",
                    },
                });
            }
            // Only fields every OpenAI-compatible server understands — vendor
            // extensions get 400s from strict ones.
            var oai = new Dictionary<string, object>
            {
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content },
                },
                ["temperature"] = 0.2,
                ["stream"] = false,
            };
            if (!string.IsNullOrWhiteSpace(cfg.Model))
                oai["model"] = cfg.Model.Trim();
            payload = oai;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.ActiveUrl());
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (cfg.UsesAnthropic)
        {
            // The Messages API authenticates with x-api-key, not a Bearer token.
            if (apiKey.Length > 0)
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (apiKey.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 5, 600)));
        string body;
        try
        {
            using var res = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"LLM answered {(int)res.StatusCode}: {Excerpt(body)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM did not answer within {cfg.TimeoutSeconds}s");
        }

        Log.Debug($"AI raw response: {Excerpt(body, 2000)}");
        using var doc = JsonDocument.Parse(body);
        return cfg.UsesOllama ? ParseOllama(doc, body)
             : cfg.UsesAnthropic ? ParseAnthropic(doc, body)
             : ParseOpenAi(doc, body);
    }

    private static (string? Text, string? Model, long? Tokens) ParseAnthropic(JsonDocument doc, string body)
    {
        // Messages shape: { model, content: [{type:"text",text}...], usage: {input_tokens, output_tokens} }.
        if (!doc.RootElement.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "the server answered 200, but not in the Messages-API shape (no content array). " +
                $"Is the endpoint really Anthropic-style? It said: {Excerpt(body)}");
        var text = string.Concat(content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
            .Select(b => b.TryGetProperty("text", out var x) ? x.GetString() : null));
        string? model = doc.RootElement.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        long tokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv)) tokens += iv;
            if (u.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov)) tokens += ov;
        }
        return (CleanAnswer(text), model, tokens > 0 ? tokens : null);
    }

    private static (string? Text, string? Model, long? Tokens) ParseOpenAi(JsonDocument doc, string body)
    {
        // A 200 without choices is not a completion at all — LM Studio answers a
        // wrong path with exactly that ("Unexpected endpoint ... Returning 200
        // anyway"). Name the real problem instead of "empty completion".
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException(
                "the server answered 200, but not with a chat completion (no choices). " +
                "The endpoint is probably not the OpenAI-style API base — it usually ends " +
                $"in /v1. Server said: {Excerpt(body)}");
        string? text = null;
        if (choices[0].TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var c))
            text = c.ValueKind == JsonValueKind.String
                ? c.GetString()
                // Some servers answer content as an array of {type,text} parts.
                : c.ValueKind == JsonValueKind.Array
                    ? string.Concat(c.EnumerateArray().Select(p =>
                        p.TryGetProperty("text", out var t) ? t.GetString() : null))
                    : null;
        string? model = doc.RootElement.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        long? tokens = doc.RootElement.TryGetProperty("usage", out var u)
                       && u.TryGetProperty("total_tokens", out var tt)
                       && tt.TryGetInt64(out var n) ? n : null;
        return (CleanAnswer(text), model, tokens);
    }

    private static (string? Text, string? Model, long? Tokens) ParseOllama(JsonDocument doc, string body)
    {
        // Native shape: { model, message: { content }, prompt_eval_count, eval_count }.
        if (!doc.RootElement.TryGetProperty("message", out var msg))
            throw new InvalidOperationException(
                "the server answered 200, but not in Ollama's /api/chat shape (no message). " +
                $"Is the endpoint really an Ollama server? It said: {Excerpt(body)}");
        string? text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;
        string? model = doc.RootElement.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        long tokens = 0;
        if (doc.RootElement.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt64(out var p))
            tokens += p;
        if (doc.RootElement.TryGetProperty("eval_count", out var ev) && ev.TryGetInt64(out var e))
            tokens += e;
        return (CleanAnswer(text), model, tokens > 0 ? tokens : null);
    }

    /// <summary>Drops reasoning-model &lt;think&gt; blocks and trims; null when nothing is left.</summary>
    internal static string? CleanAnswer(string? text)
    {
        if (text == null) return null;
        text = Regex.Replace(text, @"(?s)<think(?:ing)?>.*?(</think(?:ing)?>|\z)", "").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Excerpt(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>A tiny embedded JPEG (64×48, an amber circle on dark blue, ~0.9 KB)
    /// attached to the connectivity test so it exercises the same vision path real
    /// events use — a text-only model fails at the Test button, not on the first
    /// event. Trivial token cost even against a hosted API.</summary>
    internal static byte[] TestJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDABIMDhAOCxIQDxAUExIVGy0dGxkZGzcoKiEtQjpFREA6Pz5IUWhYSE1iTj4/WntcYmtvdHZ0RleAiX9xiGhydHD/" +
        "2wBDARMUFBsYGzUdHTVwSz9LcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHD/wAARCAAwAEADASIAAhEBAxEB/8QA" +
        "HwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkK" +
        "FhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG" +
        "x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
        "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOE" +
        "hYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDl6KKK1ICi" +
        "nwxSTyrFEpd2OABXRQeHIRGPPmkL99mAB+YrOpVjT+IuMHLY5qiunk8OWxQiOaVW7FsEflgVz13bS2k7QzLhh0PYj1FKnWhU0iEoSjuQ0UUVqQFFFFAHS+Fo" +
        "UFtNPj5y+zPoAAf6/oK3KxvC8oaxkiLEsj5wewI4/UGtmvIxF/aO520/hQVl+I4Uk0tpGHzREFT9SAR+v6CtSs3xC6ppEiscFyqr7nOf5A1NG/tFbuOfws5C" +
        "iiivZOEKKKKAJrS5ltJ1mhbDDqOxHoa6eDXrKSMNI7RN3UqT+orkqKxqUY1Ny41HHY6+TXdPRCyys5H8Kocn88Vzeo30t/P5knCjhEHRR/nvVSiinQhTd0OV" +
        "SUtGFFFFbGZ//9k=");

    /// <summary>Connectivity test for the settings UI: one tiny completion WITH a
    /// test image, against (possibly unsaved) settings — so a text-only model fails
    /// here instead of on the first real event. When the vision request fails for
    /// any reason other than a timeout, a text-only probe tells apart "server
    /// unreachable" from "model rejects images". Null = OK; otherwise the error.</summary>
    public static async Task<(string? Error, string? Detail)> TestAsync(
        AiSettings cfg, string apiKey, CancellationToken ct)
    {
        if (cfg.ActiveUrl() == null)
            return (cfg.UsesOllama
                ? "The Ollama endpoint is not a usable http(s) URL. Expected something like http://127.0.0.1:11434"
                : cfg.UsesAnthropic
                    ? "The Anthropic-style endpoint is not a usable http(s) URL — blank means https://api.anthropic.com"
                    : "The endpoint is not a usable http(s) URL. Expected something like http://127.0.0.1:1234/v1", null);
        const string probe = "Connectivity test. Reply with the single word: READY";
        try
        {
            var sw = Stopwatch.StartNew();
            var (text, model, _) = await CompleteAsync(cfg, apiKey,
                probe, new[] { (TestJpeg(), (string?)null) },
                classify: false, ct).ConfigureAwait(false);
            return text == null
                ? ("The server answered, but with an empty completion.", null)
                : (null, $"{model ?? "model"} answered the vision test in {sw.Elapsed.TotalSeconds:0.0}s: \"{Excerpt(text, 120)}\"");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The request carried an image; find out whether THAT was the problem —
            // except after a timeout, where a second full wait would help nobody.
            if (ex is not TimeoutException)
            {
                try
                {
                    _ = await CompleteAsync(cfg, apiKey, probe, Array.Empty<(byte[], string?)>(),
                        classify: false, ct).ConfigureAwait(false);
                    return ("The server is reachable and answers a text-only request, but " +
                            "rejected it once an image was attached — the model is probably " +
                            "not vision-capable. Event descriptions need a model that accepts " +
                            $"images. The server said: {Log.Flatten(ex)}", null);
                }
                catch { /* text fails too: the original error names the real problem */ }
            }
            return (Log.Flatten(ex), null);
        }
    }
}
