// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using System.Threading.Channels;
using Neolink.Media;
using Neolink.Recording;
using Neolink.Streaming;

namespace Neolink.Notifications;

/// <summary>
/// Sends a camera's detection events (email and/or webhook, per-camera opt-in
/// each), snapshots attached, from the recorder's hooks. The notification
/// leaves <see cref="NotificationSettings.EventEmailDelaySeconds"/> seconds
/// into the event, sampling the clip mid-write (a live fragmented MP4);
/// 0 waits for the event to end. Sampling starts at the detection, not the
/// clip's pre-roll, and reads go through <see cref="FootageVault"/> so
/// encrypted footage samples the same as plain; without ffmpeg or a clip the
/// thumbnail goes instead. Composition runs detached and the send rides the
/// Notifier's bounded queue — nothing here may back up into the recorder.
/// Flood control is per camera (<see cref="NotificationSettings.EventCooldownMinutes"/>);
/// skipped events are still recorded.
/// </summary>
public sealed class EventEmailer
{
    private readonly NotificationStore _store;
    private readonly Notifier _notifier;
    private readonly RecordingSettings _settings;
    private readonly EventStore _events;
    private readonly Dictionary<string, DateTime> _lastSent = new(StringComparer.OrdinalIgnoreCase);
    // Events the start path owns; the close hook must not mail them again.
    private readonly HashSet<string> _claimed = new();
    // Events whose start hook saw a nonzero delay: the send decision was made
    // there, and the close hook must not re-read the (possibly changed) setting.
    private readonly HashSet<string> _deferred = new();
    // Detection time per event: the record's StartUtc reaches back into
    // pre-roll, and snapshots must not come from there.
    private readonly Dictionary<string, DateTime> _trigger = new();
    // In-memory capture (EventSnapshotMode "memory"): record hubs by camera and
    // one live tap per event. The tap is an ordinary extra hub subscriber — its
    // channel drops ITS OWN oldest packets when it lags, so it can never slow
    // or starve the recording pumps.
    private readonly Dictionary<string, IStreamHub> _hubs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemoryTap> _taps = new();
    private readonly object _gate = new();

    public EventEmailer(NotificationStore store, Notifier notifier,
        RecordingSettings settings, EventStore events)
    {
        _store = store;
        _notifier = notifier;
        _settings = settings;
        _events = events;
    }

    /// <summary>The camera's record-stream hub, for in-memory snapshot capture.</summary>
    public void RegisterHub(string camera, IStreamHub hub)
    {
        lock (_gate) _hubs[camera] = hub;
    }

    /// <summary>The recorder's event-started hook (fires at the detection; a
    /// self-wake only on promotion). Schedules the email for the configured
    /// delay; with a delay of 0 the close hook sends instead. Must not block —
    /// the recorder calls this on its own pump.</summary>
    public void OnEventStarted(EventRecord rec)
    {
        try
        {
            var trigger = DateTime.UtcNow;
            lock (_gate) _trigger[rec.Id] = trigger;

            var s = _store.Snapshot();
            StartTap(rec, s); // no-op unless memory capture is on and a channel is armed
            int delay = Math.Clamp(s.EventEmailDelaySeconds, 0, 300);
            if (delay <= 0) return;
            lock (_gate) _deferred.Add(rec.Id);
            if (!Claim(rec, s, out var stamp, out var prev, out var channels)) { StopTap(rec.Id); return; }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                await GuardedComposeAsync(rec, s, trigger, stamp, prev, channels).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email skipped after an unexpected error: {Log.Flatten(ex)}");
        }
    }

    /// <summary>The recorder's event-closed hook: sends when the start hook
    /// deferred to it (delay 0 at the start), otherwise only clears the claim.
    /// Must not block or throw into the recorder's pump.</summary>
    public void OnEventClosed(EventRecord rec)
    {
        try
        {
            DateTime? trigger = null;
            lock (_gate)
            {
                if (_trigger.Remove(rec.Id, out var t)) trigger = t;
                bool startOwned = _deferred.Remove(rec.Id);
                _claimed.Remove(rec.Id);
                if (startOwned) return;
            }
            var s = _store.Snapshot();
            if (!Claim(rec, s, out var stamp, out var prev, out var channels)) { StopTap(rec.Id); return; }

            _ = Task.Run(() => GuardedComposeAsync(rec, s, trigger, stamp, prev, channels));
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email skipped after an unexpected error: {Log.Flatten(ex)}");
        }
    }

    /// <summary>Config on, camera opted in, cooldown clear. The cooldown window
    /// is claimed up front so a burst cannot double-send; a failed hand-off to
    /// the queue gives it back (see <see cref="GuardedComposeAsync"/>).</summary>
    private bool Claim(EventRecord rec, NotificationSettings s,
        out DateTime stamp, out DateTime? prev, out AlertChannels channels)
    {
        stamp = default;
        prev = null;
        var cam = _settings.Get(rec.Camera);
        channels = AlertChannels.None;
        if (Notifier.EmailReady(s) && cam.EmailEvents) channels |= AlertChannels.Email;
        if (Notifier.WebhookReady(s) && cam.WebhookEvents) channels |= AlertChannels.Webhook;
        if (channels == AlertChannels.None) return false;

        lock (_gate)
        {
            if (_lastSent.TryGetValue(rec.Camera, out var last))
            {
                if (s.EventCooldownMinutes > 0
                    && DateTime.UtcNow - last < TimeSpan.FromMinutes(s.EventCooldownMinutes))
                {
                    Log.Debug($"{rec.Camera}: event email skipped (cooldown, " +
                              $"{s.EventCooldownMinutes} min per camera) — the event is still recorded");
                    return false;
                }
                prev = last;
            }
            stamp = DateTime.UtcNow;
            _lastSent[rec.Camera] = stamp;
            _claimed.Add(rec.Id);
            return true;
        }
    }

    private async Task GuardedComposeAsync(EventRecord rec, NotificationSettings s,
        DateTime? trigger, DateTime stamped, DateTime? prev, AlertChannels channels)
    {
        bool queued = false;
        try
        {
            queued = await ComposeAndSendAsync(rec, s, trigger, channels).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email could not be prepared: {Log.Flatten(ex)} " +
                     "— the event itself is recorded and unaffected");
        }
        finally { StopTap(rec.Id); }
        if (!queued)
            lock (_gate)
            {
                if (_lastSent.TryGetValue(rec.Camera, out var cur) && cur == stamped)
                {
                    if (prev is { } p) _lastSent[rec.Camera] = p;
                    else _lastSent.Remove(rec.Camera);
                }
            }
    }

    /// <summary>Why fewer snapshots than asked for are attached.</summary>
    private enum Shortfall { None, NoFfmpeg, NoClip, Sampling, Memory }

    private async Task<bool> ComposeAndSendAsync(EventRecord rec, NotificationSettings s,
        DateTime? trigger, AlertChannels channels)
    {
        int want = Math.Clamp(s.EventSnapshots, 1, 50);
        // Sampled once so the duration line, attachments and closing note agree.
        bool ongoing = rec.Ongoing;
        var end = ongoing ? DateTime.UtcNow : rec.EndUtc;
        double skip = trigger is { } t && t > rec.StartUtc ? (t - rec.StartUtc).TotalSeconds : 0;
        double span = Math.Max(1, (end - rec.StartUtc).TotalSeconds - skip);
        var (attachments, why) = await SnapshotsAsync(rec, want, skip, span, s.EventSnapshotMode)
            .ConfigureAwait(false);

        var labels = rec.Labels.Count > 0 ? string.Join(" + ", rec.Labels) : "detection";
        var local = rec.StartUtc.ToLocalTime();
        var seconds = Math.Max(0, (end - rec.StartUtc).TotalSeconds);
        var subject = $"{rec.Camera}: {labels} at {local:HH:mm:ss}";
        string attachNote = attachments.Count switch
        {
            0 => " No snapshot could be captured for this event.",
            var n when n < want && why == Shortfall.NoFfmpeg =>
                $" {n} snapshot attached — sampling {want} frames from the clip needs ffmpeg, " +
                "which is not installed on this server, so this is the event's thumbnail.",
            var n when n < want && why == Shortfall.NoClip =>
                $" {n} snapshot attached — this event has no saved clip, so this is its thumbnail.",
            var n when n < want && why == Shortfall.Memory =>
                $" {n} of the {want} snapshots asked for are attached (sampled in memory; the live tap held no more).",
            var n when n < want =>
                $" {n} of the {want} snapshots asked for are attached (the clip held no more).",
            var n => $" {n} snapshot(s) attached.",
        };
        var body = ongoing
            ? $"{Cap(labels)} on {rec.Camera}, {local:yyyy-MM-dd HH:mm:ss} (local server time), " +
              $"still ongoing when this email was sent ({seconds:0} seconds in)." + attachNote +
              " The full clip will be in the web UI's events page once the event ends."
            : $"{Cap(labels)} on {rec.Camera}, {local:yyyy-MM-dd HH:mm:ss} (local server time), " +
              $"about {seconds:0} seconds long." + attachNote +
              " The full clip is in the web UI's events page.";

        return _notifier.Send(new Alert($"event:{rec.Id}", Recovery: false, subject,
            Headline: $"{Cap(labels)} — {rec.Camera}", body, Context: null,
            Attachments: attachments.Count > 0 ? attachments : null,
            Channels: channels,
            Event: new EventInfo(rec.Camera, rec.Labels.ToArray(), rec.StartUtc, seconds, ongoing, rec.Id),
            Brief: $"{Cap(labels)} on {rec.Camera} at {local:HH:mm}"));
    }

    private static string Cap(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Evenly spaced JPEG frames from the event's footage (the clip
    /// minus <paramref name="skipSeconds"/> of pre-roll); the thumbnail when
    /// the clip or ffmpeg is unavailable; empty only when both are.</summary>
    private async Task<(List<EmailAttachment> Attachments, Shortfall Why)> SnapshotsAsync(
        EventRecord rec, int want, double skipSeconds, double spanSeconds, string mode)
    {
        var dir = _events.EventDir(rec);
        var result = new List<EmailAttachment>();
        var safe = EventStore.SafeName(rec.Camera);
        var why = Shortfall.None;
        bool memory = string.Equals(mode, "memory", StringComparison.OrdinalIgnoreCase);

        var clip = Path.Combine(dir, "clip.mp4");
        if (want > 1 && Media.Ffmpeg.ExePath == null)
        {
            why = Shortfall.NoFfmpeg;
            Log.Info($"{rec.Camera}: event email attaches the thumbnail only — sampling {want} " +
                     "frames from the clip needs ffmpeg, and none was found on PATH " +
                     "(set NEOLINK_FFMPEG, or install ffmpeg; the Docker image ships one)");
        }
        else if (want > 1 && !memory && !(rec.HasClip && File.Exists(clip)))
        {
            why = Shortfall.NoClip;
            Log.Info($"{rec.Camera}: event email attaches the thumbnail only — this event has no clip " +
                     "(recording was off, the disk was full, or the stream never carried a keyframe)");
        }

        if (memory && Media.Ffmpeg.ExePath is { } ff)
        {
            // Never opens the clip file — the frames come from the live tap.
            var (tap, aus) = TakeTap(rec.Id);
            if (tap == null || aus.Count == 0)
            {
                why = Shortfall.Memory;
                Log.Info($"{rec.Camera}: in-memory snapshot capture held no frames (stream quiet " +
                         "or capture unavailable) — attaching the thumbnail instead");
            }
            else
            {
                try
                {
                    var frames = PickEvenly(
                        await DecodeMemoryAsync(ff, tap, SelectForDecode(aus, MaxDecodedFrames))
                            .ConfigureAwait(false), want);
                    for (int i = 0; i < frames.Count; i++)
                        result.Add(new EmailAttachment($"{safe}-{i + 1}.jpg", "image/jpeg", frames[i]));
                    if (frames.Count < want)
                    {
                        why = Shortfall.Memory;
                        Log.Info($"{rec.Camera}: event email carries {frames.Count} of the {want} " +
                                 "snapshots asked for — the in-memory tap held no more decodable frames");
                    }
                }
                catch (Exception ex)
                {
                    why = Shortfall.Memory;
                    Log.Warn($"{rec.Camera}: in-memory snapshot decode failed " +
                             $"({Log.Flatten(ex)}) — attaching the thumbnail instead");
                }
            }
        }
        else if (!memory && rec.HasClip && File.Exists(clip) && Media.Ffmpeg.ExePath is { } ffmpeg)
        {
            try
            {
                var frames = await SampleClipAsync(ffmpeg, clip, skipSeconds, spanSeconds, want)
                    .ConfigureAwait(false);
                for (int i = 0; i < frames.Count; i++)
                    result.Add(new EmailAttachment($"{safe}-{i + 1}.jpg", "image/jpeg", frames[i]));
                if (frames.Count < want)
                {
                    why = Shortfall.Sampling;
                    Log.Info($"{rec.Camera}: event email carries {frames.Count} of the {want} " +
                             "snapshots asked for — the clip held no more decodable frames");
                }
            }
            catch (Exception ex)
            {
                why = Shortfall.Sampling;
                Log.Warn($"{rec.Camera}: could not sample the clip for the event email " +
                         $"({Log.Flatten(ex)}) — attaching the thumbnail instead");
            }
        }
        if (result.Count > 0) return (result, why);

        var thumb = Path.Combine(dir, "thumb.jpg");
        if (rec.HasThumb && File.Exists(thumb))
        {
            try
            {
                await using var src = FootageVault.OpenRead(thumb);
                using var ms = new MemoryStream();
                await src.CopyToAsync(ms).ConfigureAwait(false);
                if (ms.Length > 2) result.Add(new EmailAttachment($"{safe}-1.jpg", "image/jpeg", ms.ToArray()));
            }
            catch (Exception ex)
            {
                Log.Debug($"{rec.Camera}: event thumbnail unreadable for email: {Log.Flatten(ex)}");
            }
        }
        return (result, why);
    }

    /// <summary>Frames decoded per pass (~30 MB of 720p JPEG at the extreme).
    /// Must never bind before the clip ends, or snapshots bunch at its start.</summary>
    internal const int MaxDecodedFrames = 300;

    // Generous: must clear feeding a max-length clip through the pipe on a slow NAS.
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Decode rate for footage believed to span
    /// <paramref name="eventSeconds"/>, over-producing 2.5x for
    /// <see cref="PickEvenly"/>. The floor only guards ffmpeg against a
    /// degenerate rate; at 0.02 fps the frame cap still spans hours.</summary>
    internal static double InitialRate(int want, double eventSeconds) =>
        Math.Clamp(want * 2.5 / eventSeconds, 0.02, 10);

    /// <summary>Rate for the next pass after one under-filled: what came out
    /// bounds the decodable length (got/fps seconds). Grows at least 2.5x per
    /// pass, so escalation to the 10 fps ceiling stays short.</summary>
    internal static double RetryRate(int want, double fps, int got) =>
        Math.Min(10, want * 2.5 * fps / Math.Max(1, got));

    private static async Task<List<byte[]>> SampleClipAsync(string ffmpeg, string clipPath,
        double skipSeconds, double spanSeconds, int want)
    {
        double fps = InitialRate(want, spanSeconds);
        var frames = await DecodeAsync(ffmpeg, clipPath, skipSeconds, fps).ConfigureAwait(false);
        // A clip can be shorter than its event; retries re-aim at the length
        // the previous pass proved.
        while (frames.Count > 0 && frames.Count < want && fps < 10)
        {
            double next = RetryRate(want, fps, frames.Count);
            if (next <= fps) break;
            fps = next;
            List<byte[]> again;
            try { again = await DecodeAsync(ffmpeg, clipPath, skipSeconds, fps).ConfigureAwait(false); }
            catch { break; }
            if (again.Count <= frames.Count) break;   // the clip truly holds no more
            frames = again;
        }
        return PickEvenly(frames, want);
    }

    /// <summary>The -vf chain for one decode pass. Pre-roll is dropped with a
    /// trim ahead of the rate filter (a pipe cannot seek, so input-side -ss is
    /// not an option), timestamps rebased so sampling starts at the event.</summary>
    internal static string VideoFilter(double skipSeconds, double fps)
    {
        var sample = FormattableString.Invariant($"fps={fps:0.######},scale=-2:720");
        return skipSeconds > 0.05
            ? FormattableString.Invariant($"trim=start={skipSeconds:0.###},setpts=PTS-STARTPTS,{sample}")
            : sample;
    }

    private static Task<List<byte[]>> DecodeAsync(string ffmpeg, string clipPath,
        double skipSeconds, double fps) =>
        RunDecodeAsync(ffmpeg, new[] { "-i", "pipe:0" }, VideoFilter(skipSeconds, fps),
            // Feed through the vault (decrypts transparently; plaintext passes as-is).
            async (stdin, ct) =>
            {
                await using var src = FootageVault.OpenRead(clipPath);
                await src.CopyToAsync(stdin, ct).ConfigureAwait(false);
            });

    /// <summary>Decodes tapped access units: an elementary stream fed from RAM —
    /// the clip file is never opened.</summary>
    private static Task<List<byte[]>> DecodeMemoryAsync(string ffmpeg, MemoryTap tap,
        List<(byte[] Au, bool Key)> aus) =>
        RunDecodeAsync(ffmpeg, new[] { "-f", tap.Codec == VideoCodec.H265 ? "hevc" : "h264", "-i", "pipe:0" },
            "scale=-2:720",
            async (stdin, ct) =>
            {
                // Parameter sets up front: tapped keyframes usually carry them
                // in-band, but the stream must decode even when they do not.
                foreach (var nal in new[] { tap.Vps, tap.Sps, tap.Pps })
                {
                    if (nal == null) continue;
                    await stdin.WriteAsync(new byte[] { 0, 0, 0, 1 }, ct).ConfigureAwait(false);
                    await stdin.WriteAsync(nal, ct).ConfigureAwait(false);
                }
                foreach (var (au, _) in aus)
                    await stdin.WriteAsync(au, ct).ConfigureAwait(false);
            });

    private static async Task<List<byte[]>> RunDecodeAsync(string ffmpeg, string[] inputArgs,
        string videoFilter, Func<Stream, CancellationToken, Task> feedSource)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            // Bounded appetite: an unthrottled full-speed decode of a 5 MP clip
            // can starve the camera pumps on a small box — and a starved session
            // is a dropped session, which cuts the very recording being sampled.
            "-threads", "2",
        }.Concat(inputArgs).Concat(new[]
        {
            "-vf", videoFilter,
            "-frames:v", MaxDecodedFrames.ToString(),
            "-q:v", "4", "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
        })) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* the process may have exited already; priority is best-effort */ }
        // The timeout must cover the stdout copy: it only ends when ffmpeg closes
        // stdout, and a feed stalled on dead storage keeps ffmpeg alive forever.
        using var cts = new CancellationTokenSource(DecodeTimeout);
        // The feed runs while stdout drains concurrently — one-sided pumping
        // deadlocks pipes.
        var feed = Task.Run(async () =>
        {
            try
            {
                await feedSource(p.StandardInput.BaseStream, cts.Token).ConfigureAwait(false);
            }
            catch { /* ffmpeg may close stdin once it has its frames — normal */ }
            finally
            {
                try { p.StandardInput.Close(); } catch { }
            }
        });
        var drainErr = p.StandardError.ReadToEndAsync();
        using var stdout = new MemoryStream();
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (stdout.Length == 0)
                throw new TimeoutException(
                    $"ffmpeg produced nothing within {DecodeTimeout.TotalSeconds:0} s");
        }
        if (!p.WaitForExit(10_000)) { try { p.Kill(entireProcessTree: true); } catch { } }
        // A feed wedged on unresponsive storage must not chain-hang the compose
        // task; the grace only covers the normal post-kill unwind.
        await Task.WhenAny(feed, Task.Delay(5_000)).ConfigureAwait(false);
        if (p.HasExited && p.ExitCode != 0 && stdout.Length == 0)
        {
            var err = (await drainErr.ConfigureAwait(false)).Split('\n')
                .Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new IOException(err ?? $"ffmpeg exit code {p.ExitCode}");
        }
        return Ai.AiPreroll.SplitJpegs(stdout.ToArray());
    }

    /// <summary>Exactly <paramref name="want"/> items spread across the list
    /// (first and last always included when there is room), or the whole list
    /// when it is shorter. The decode over-produces on purpose; this is what
    /// makes "3 snapshots" mean three, spaced across the event.</summary>
    internal static List<byte[]> PickEvenly(List<byte[]> frames, int want)
    {
        if (want <= 0 || frames.Count == 0) return new List<byte[]>();
        if (frames.Count <= want) return frames;
        if (want == 1) return new List<byte[]> { frames[frames.Count / 2] };
        var picked = new List<byte[]>(want);
        for (int i = 0; i < want; i++)
            picked.Add(frames[(int)Math.Round(i * (frames.Count - 1.0) / (want - 1))]);
        return picked;
    }

    // ------------------------------------------------------------------ in-memory capture

    /// <summary>Starts the live tap for an event when memory capture is on and a
    /// channel is armed. Failure costs snapshots only, never the event.</summary>
    private void StartTap(EventRecord rec, NotificationSettings s)
    {
        if (!string.Equals(s.EventSnapshotMode, "memory", StringComparison.OrdinalIgnoreCase))
            return;
        var cam = _settings.Get(rec.Camera);
        bool armed = (Notifier.EmailReady(s) && cam.EmailEvents)
                     || (Notifier.WebhookReady(s) && cam.WebhookEvents);
        if (!armed) return;
        IStreamHub? hub;
        lock (_gate) _hubs.TryGetValue(rec.Camera, out hub);
        if (hub?.Codec == null)
        {
            Log.Info($"{rec.Camera}: in-memory snapshot capture unavailable (no live stream) — " +
                     "the event notification will carry the thumbnail");
            return;
        }
        try
        {
            var tap = new MemoryTap(hub);
            MemoryTap? old;
            lock (_gate)
            {
                _taps.Remove(rec.Id, out old);
                _taps[rec.Id] = tap;
            }
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: in-memory snapshot capture failed to start: {Log.Flatten(ex)}");
        }
    }

    private void StopTap(string eventId)
    {
        MemoryTap? tap;
        lock (_gate) _taps.Remove(eventId, out tap);
        tap?.Dispose();
    }

    /// <summary>Consumes the event's tap: its frames so far, and the tap is done.</summary>
    private (MemoryTap? Tap, List<(byte[] Au, bool Key)> Aus) TakeTap(string eventId)
    {
        MemoryTap? tap;
        lock (_gate) _taps.Remove(eventId, out tap);
        if (tap == null) return (null, new List<(byte[], bool)>());
        var aus = tap.Take();
        tap.Dispose();
        return (tap, aus);
    }

    /// <summary>Trims a tapped AU list to a decodable, bounded feed: leading
    /// P-frames (before the first keyframe) go; under <paramref name="cap"/>
    /// everything decodes; over it, keyframes only (standalone-decodable),
    /// evenly thinned.</summary>
    internal static List<(byte[] Au, bool Key)> SelectForDecode(
        List<(byte[] Au, bool Key)> aus, int cap)
    {
        int first = aus.FindIndex(a => a.Key);
        if (first < 0) return new List<(byte[], bool)>();
        if (first > 0) aus = aus.GetRange(first, aus.Count - first);
        if (aus.Count <= cap) return aus;
        var keys = aus.Where(a => a.Key).ToList();
        if (keys.Count <= cap) return keys;
        var picked = new List<(byte[], bool)>(cap);
        for (int i = 0; i < cap; i++)
            picked.Add(keys[(int)Math.Round(i * (keys.Count - 1.0) / (cap - 1))]);
        return picked;
    }

    /// <summary>One event's live frame capture: an ordinary extra hub subscriber
    /// holding video AU references in RAM. Its bounded channel drops its OWN
    /// oldest packets when it lags, so it cannot slow the recording pumps; over
    /// the byte budget the oldest GOPs go (recent frames matter most to a
    /// notification), and collection hard-stops after ten minutes so an event
    /// that never composes cannot pin memory.</summary>
    private sealed class MemoryTap : IDisposable
    {
        private const long BudgetBytes = 48 * 1024 * 1024;
        private static readonly TimeSpan MaxCollect = TimeSpan.FromMinutes(10);

        private readonly IStreamHub _hub;
        private readonly Guid _id;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private readonly List<(byte[] Au, bool Key)> _aus = new();
        private long _bytes;
        private bool _done;

        public readonly VideoCodec? Codec;
        public readonly byte[]? Sps;
        public readonly byte[]? Pps;
        public readonly byte[]? Vps;

        public MemoryTap(IStreamHub hub)
        {
            _hub = hub;
            Codec = hub.Codec;
            Sps = hub.Sps;
            Pps = hub.Pps;
            Vps = hub.Vps;
            var (id, reader) = hub.Subscribe();
            _id = id;
            _ = Task.Run(() => PumpAsync(reader));
        }

        private async Task PumpAsync(ChannelReader<HubPacket> reader)
        {
            var stopAt = DateTime.UtcNow + MaxCollect;
            try
            {
                await foreach (var p in reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    if (DateTime.UtcNow > stopAt) break;
                    if (p is not HubVideo v) continue;
                    lock (_lock)
                    {
                        if (_done) break;
                        _aus.Add((v.AnnexB, v.Keyframe));
                        _bytes += v.AnnexB.Length;
                        while (_bytes > BudgetBytes && DropOldestGop()) { }
                        // Still over budget = one GOP the budget cannot hold (a
                        // stream gone keyframe-less): stop collecting, keep what decodes.
                        if (_bytes > BudgetBytes) break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { /* a dead tap only costs snapshots, never the event */ }
            finally { _hub.Unsubscribe(_id); }
        }

        /// <summary>Drops the oldest GOP (whole, so the rest stays decodable);
        /// false when only one is left. Called under the lock.</summary>
        private bool DropOldestGop()
        {
            int second = -1;
            for (int i = 1; i < _aus.Count; i++)
                if (_aus[i].Key) { second = i; break; }
            if (second <= 0) return false;
            for (int i = 0; i < second; i++) _bytes -= _aus[i].Au.Length;
            _aus.RemoveRange(0, second);
            return true;
        }

        public List<(byte[] Au, bool Key)> Take()
        {
            lock (_lock)
            {
                _done = true;
                var taken = new List<(byte[], bool)>(_aus);
                _aus.Clear();
                _bytes = 0;
                return taken;
            }
        }

        public void Dispose()
        {
            lock (_lock) { _done = true; _aus.Clear(); _bytes = 0; }
            _cts.Cancel();
        }
    }
}
