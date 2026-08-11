// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using Neolink.Recording;

namespace Neolink.Notifications;

/// <summary>
/// Emails a camera's detection events, snapshots attached. Rides the recorder's
/// hooks: the recorder decided what was worth keeping (the per-camera event-type
/// filter), so anything it announces is by definition worth telling the
/// recipient about — no second filter to keep in sync.
///
/// Timing is the point of an alert: the email goes out
/// <see cref="NotificationSettings.EventEmailDelaySeconds"/> seconds into the
/// event (an alert that waits for a four-minute visit to end arrives four
/// minutes late), sampling the clip as it stands — the clip is a live
/// fragmented MP4, decodable mid-write. 0 waits for the event to end and
/// samples the whole clip. A tentative self-wake never mails: the recorder
/// announces only promoted events.
///
/// Snapshots are sampled evenly across the clip with ffmpeg (read through
/// <see cref="FootageVault"/>, so encrypted footage samples the same as
/// plain); without ffmpeg — or a clip — the event's thumbnail goes instead, so
/// the email is never empty-handed. Composition runs on its own task and the
/// send rides the Notifier's bounded queue: a slow disk or mail server can
/// never back up into the recorder.
///
/// Flood control is per camera (<see cref="NotificationSettings.EventCooldownMinutes"/>):
/// a busy driveway sends one email per window, and the skipped events are still
/// recorded and reviewable — the email is the tap on the shoulder, not the record.
/// </summary>
public sealed class EventEmailer
{
    private readonly NotificationStore _store;
    private readonly Notifier _notifier;
    private readonly RecordingSettings _settings;
    private readonly EventStore _events;
    private readonly Dictionary<string, DateTime> _lastSent = new(StringComparer.OrdinalIgnoreCase);
    // Events the start-triggered path has claimed, so the close hook never
    // mails the same event twice (an event shorter than the delay closes
    // before its scheduled email has gone out).
    private readonly HashSet<string> _claimed = new();
    private readonly object _gate = new();

    public EventEmailer(NotificationStore store, Notifier notifier,
        RecordingSettings settings, EventStore events)
    {
        _store = store;
        _notifier = notifier;
        _settings = settings;
        _events = events;
    }

    /// <summary>The recorder's event-started hook (real events only — a
    /// tentative self-wake is announced, and so mailed, only on promotion).
    /// Schedules the email for <see cref="NotificationSettings.EventEmailDelaySeconds"/>
    /// seconds in; with a delay of 0 the close hook owns the event instead.
    /// Cheap checks inline (the recorder calls this on its own pump);
    /// everything heavier is detached.</summary>
    public void OnEventStarted(EventRecord rec)
    {
        try
        {
            var s = _store.Snapshot();
            int delay = Math.Clamp(s.EventEmailDelaySeconds, 0, 300);
            if (delay <= 0) return;
            if (!Claim(rec, s, out var stamp, out var prev)) return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                await GuardedComposeAsync(rec, s, stamp, prev).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email skipped after an unexpected error: {Log.Flatten(ex)}");
        }
    }

    /// <summary>The recorder's event-closed hook — the sender when the delay is
    /// 0 (whole-clip snapshots), otherwise only the claim set's janitor.</summary>
    public void OnEventClosed(EventRecord rec)
    {
        // Nothing in here may escape into the recorder's pump — a mail problem
        // is a mail problem, never a recording one. The inner work is detached
        // and guarded too; this outer net covers even the settings reads.
        try
        {
            lock (_gate)
            {
                if (_claimed.Remove(rec.Id)) return;   // the start path mailed (or is about to)
            }
            var s = _store.Snapshot();
            if (Math.Clamp(s.EventEmailDelaySeconds, 0, 300) > 0)
                return;   // the start path was configured; it declined this event
            if (!Claim(rec, s, out var stamp, out var prev)) return;

            _ = Task.Run(() => GuardedComposeAsync(rec, s, stamp, prev));
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email skipped after an unexpected error: {Log.Flatten(ex)}");
        }
    }

    /// <summary>The shared gate: config on, camera opted in, cooldown clear.
    /// The cooldown window is claimed up front (a burst of events must not
    /// double-send) but only KEPT by a successful hand-off to the mail queue —
    /// <see cref="GuardedComposeAsync"/> gives it back on failure, because the
    /// next event in the window is then the recipient's only shot.</summary>
    private bool Claim(EventRecord rec, NotificationSettings s,
        out DateTime stamp, out DateTime? prev)
    {
        stamp = default;
        prev = null;
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Recipient) || string.IsNullOrWhiteSpace(s.SmtpHost))
            return false;
        if (!_settings.Get(rec.Camera).EmailEvents) return false;

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
        DateTime stamped, DateTime? prev)
    {
        bool queued = false;
        try
        {
            queued = await ComposeAndSendAsync(rec, s).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email could not be prepared: {Log.Flatten(ex)} " +
                     "— the event itself is recorded and unaffected");
        }
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

    /// <summary>Why fewer snapshots than asked for are attached — the email
    /// states the reason, because a silent shortfall reads as a bug.</summary>
    private enum Shortfall { None, NoFfmpeg, NoClip, Sampling }

    private async Task<bool> ComposeAndSendAsync(EventRecord rec, NotificationSettings s)
    {
        int want = Math.Clamp(s.EventSnapshots, 1, 50);
        var (attachments, why) = await SnapshotsAsync(rec, want).ConfigureAwait(false);

        var labels = rec.Labels.Count > 0 ? string.Join(" + ", rec.Labels) : "detection";
        var local = rec.StartUtc.ToLocalTime();
        // Sampled once: the recorder flips Ongoing on its own pump, and the
        // duration line, the attachments and the closing note must agree on
        // which event they describe.
        bool ongoing = rec.Ongoing;
        var seconds = Math.Max(0, ((ongoing ? DateTime.UtcNow : rec.EndUtc) - rec.StartUtc).TotalSeconds);
        var subject = $"{rec.Camera}: {labels} at {local:HH:mm:ss}";
        // When fewer snapshots arrive than were asked for, the mail says why —
        // otherwise "1 snapshot" against a setting of 3 looks like a bug.
        string attachNote = attachments.Count switch
        {
            0 => " No snapshot could be captured for this event.",
            var n when n < want && why == Shortfall.NoFfmpeg =>
                $" {n} snapshot attached — sampling {want} frames from the clip needs ffmpeg, " +
                "which is not installed on this server, so this is the event's thumbnail.",
            var n when n < want && why == Shortfall.NoClip =>
                $" {n} snapshot attached — this event has no saved clip, so this is its thumbnail.",
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
            Attachments: attachments.Count > 0 ? attachments : null));
    }

    private static string Cap(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Evenly spaced JPEG frames from the event's clip; the thumbnail
    /// when the clip or ffmpeg is unavailable; empty only when both are.</summary>
    private async Task<(List<EmailAttachment> Attachments, Shortfall Why)> SnapshotsAsync(
        EventRecord rec, int want)
    {
        var dir = _events.EventDir(rec);
        var result = new List<EmailAttachment>();
        var safe = EventStore.SafeName(rec.Camera);
        var why = Shortfall.None;

        var clip = Path.Combine(dir, "clip.mp4");
        // Why only one snapshot arrived is otherwise invisible to the person
        // reading the mail — every fallback below says so, at Info.
        if (want > 1 && Media.Ffmpeg.ExePath == null)
        {
            why = Shortfall.NoFfmpeg;
            Log.Info($"{rec.Camera}: event email attaches the thumbnail only — sampling {want} " +
                     "frames from the clip needs ffmpeg, and none was found on PATH " +
                     "(set NEOLINK_FFMPEG, or install ffmpeg; the Docker image ships one)");
        }
        else if (want > 1 && !(rec.HasClip && File.Exists(clip)))
        {
            why = Shortfall.NoClip;
            Log.Info($"{rec.Camera}: event email attaches the thumbnail only — this event has no clip " +
                     "(recording was off, the disk was full, or the stream never carried a keyframe)");
        }

        if (rec.HasClip && File.Exists(clip) && Media.Ffmpeg.ExePath is { } ffmpeg)
        {
            try
            {
                // An ongoing event's clip only reaches "now" — EndUtc is stale
                // mid-event (touched on label changes, final only at close).
                double eventSeconds = Math.Max(1,
                    ((rec.Ongoing ? DateTime.UtcNow : rec.EndUtc) - rec.StartUtc).TotalSeconds);
                var frames = await SampleClipAsync(ffmpeg, clip, eventSeconds, want).ConfigureAwait(false);
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

    /// <summary>Frames decoded per pass, whatever the clip (~30 MB of 720p JPEG
    /// at the extreme). Wide on purpose: a cap the sampling rate can outrun
    /// stops the decode early, and every snapshot then comes from the clip's
    /// opening seconds — the cap must never bind before the clip ends.</summary>
    internal const int MaxDecodedFrames = 300;

    /// <summary>The decode rate for a clip believed to run the event's length,
    /// over-producing 2.5x so <see cref="PickEvenly"/> always has the request
    /// covered even with pre- and post-roll padding the estimate. The floor
    /// only guards ffmpeg against a degenerate rate: at 0.02 fps the frame cap
    /// still spans over four hours of clip, so it cannot cause early cutoff.</summary>
    internal static double InitialRate(int want, double eventSeconds) =>
        Math.Clamp(want * 2.5 / eventSeconds, 0.02, 10);

    /// <summary>The next rate after a pass returned fewer frames than wanted:
    /// what came out bounds the decodable length (at most got/fps seconds), so
    /// this aims the same 2.5x over-production at THAT. Grows at least 2.5x per
    /// pass, so escalation to the 10 fps ceiling takes only a handful of passes.</summary>
    internal static double RetryRate(int want, double fps, int got) =>
        Math.Min(10, want * 2.5 * fps / Math.Max(1, got));

    private static async Task<List<byte[]>> SampleClipAsync(string ffmpeg, string clipPath,
        double eventSeconds, int want)
    {
        double fps = InitialRate(want, eventSeconds);
        var frames = await DecodeAsync(ffmpeg, clipPath, fps).ConfigureAwait(false);
        // The clip can be far shorter than the event that owns it (recording
        // started late, the disk hiccupped): the first pass then under-fills —
        // "asked for 3, got 1" — and each retry re-aims at the length the
        // previous pass proved. Cheap by construction: a pass only under-fills
        // when the clip is short, and that is exactly what a re-decode costs.
        while (frames.Count > 0 && frames.Count < want && fps < 10)
        {
            double next = RetryRate(want, fps, frames.Count);
            if (next <= fps) break;
            fps = next;
            List<byte[]> again;
            try { again = await DecodeAsync(ffmpeg, clipPath, fps).ConfigureAwait(false); }
            catch { break; }                          // keep what the first pass proved
            if (again.Count <= frames.Count) break;   // the clip truly holds no more
            frames = again;
        }
        return PickEvenly(frames, want);
    }

    private static async Task<List<byte[]>> DecodeAsync(string ffmpeg, string clipPath, double fps)
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
            "-i", "pipe:0",
            "-vf", FormattableString.Invariant($"fps={fps:0.######},scale=-2:720"),
            "-frames:v", MaxDecodedFrames.ToString(),
            "-q:v", "4", "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
        }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        // Feed through the vault (decrypts transparently; plaintext passes as-is)
        // while draining stdout concurrently — one-sided pumping deadlocks pipes.
        var feed = Task.Run(async () =>
        {
            try
            {
                await using var src = FootageVault.OpenRead(clipPath);
                await src.CopyToAsync(p.StandardInput.BaseStream).ConfigureAwait(false);
            }
            catch { /* ffmpeg may close stdin once it has its frames — normal */ }
            finally
            {
                try { p.StandardInput.Close(); } catch { }
            }
        });
        var drainErr = p.StandardError.ReadToEndAsync();
        using var stdout = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(stdout).ConfigureAwait(false);
        if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } }
        try { await feed.ConfigureAwait(false); } catch { }
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
}
