// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using Neolink.Recording;

namespace Neolink.Notifications;

/// <summary>
/// Emails a camera's detection events, snapshots attached, from the recorder's
/// hooks. The email leaves <see cref="NotificationSettings.EventEmailDelaySeconds"/>
/// seconds into the event, sampling the clip mid-write (a live fragmented MP4);
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
    // Detection time per event: the record's StartUtc reaches back into
    // pre-roll, and snapshots must not come from there.
    private readonly Dictionary<string, DateTime> _trigger = new();
    private readonly object _gate = new();

    public EventEmailer(NotificationStore store, Notifier notifier,
        RecordingSettings settings, EventStore events)
    {
        _store = store;
        _notifier = notifier;
        _settings = settings;
        _events = events;
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
            int delay = Math.Clamp(s.EventEmailDelaySeconds, 0, 300);
            if (delay <= 0) return;
            if (!Claim(rec, s, out var stamp, out var prev)) return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                await GuardedComposeAsync(rec, s, trigger, stamp, prev).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email skipped after an unexpected error: {Log.Flatten(ex)}");
        }
    }

    /// <summary>The recorder's event-closed hook: sends when the delay is 0,
    /// otherwise only clears the claim. Must not block or throw into the
    /// recorder's pump.</summary>
    public void OnEventClosed(EventRecord rec)
    {
        try
        {
            DateTime? trigger = null;
            lock (_gate)
            {
                if (_trigger.Remove(rec.Id, out var t)) trigger = t;
                if (_claimed.Remove(rec.Id)) return;
            }
            var s = _store.Snapshot();
            if (Math.Clamp(s.EventEmailDelaySeconds, 0, 300) > 0) return;
            if (!Claim(rec, s, out var stamp, out var prev)) return;

            _ = Task.Run(() => GuardedComposeAsync(rec, s, trigger, stamp, prev));
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
        DateTime? trigger, DateTime stamped, DateTime? prev)
    {
        bool queued = false;
        try
        {
            queued = await ComposeAndSendAsync(rec, s, trigger).ConfigureAwait(false);
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

    /// <summary>Why fewer snapshots than asked for are attached.</summary>
    private enum Shortfall { None, NoFfmpeg, NoClip, Sampling }

    private async Task<bool> ComposeAndSendAsync(EventRecord rec, NotificationSettings s,
        DateTime? trigger)
    {
        int want = Math.Clamp(s.EventSnapshots, 1, 50);
        // Sampled once so the duration line, attachments and closing note agree.
        bool ongoing = rec.Ongoing;
        var end = ongoing ? DateTime.UtcNow : rec.EndUtc;
        double skip = trigger is { } t && t > rec.StartUtc ? (t - rec.StartUtc).TotalSeconds : 0;
        double span = Math.Max(1, (end - rec.StartUtc).TotalSeconds - skip);
        var (attachments, why) = await SnapshotsAsync(rec, want, skip, span).ConfigureAwait(false);

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

    /// <summary>Evenly spaced JPEG frames from the event's footage (the clip
    /// minus <paramref name="skipSeconds"/> of pre-roll); the thumbnail when
    /// the clip or ffmpeg is unavailable; empty only when both are.</summary>
    private async Task<(List<EmailAttachment> Attachments, Shortfall Why)> SnapshotsAsync(
        EventRecord rec, int want, double skipSeconds, double spanSeconds)
    {
        var dir = _events.EventDir(rec);
        var result = new List<EmailAttachment>();
        var safe = EventStore.SafeName(rec.Camera);
        var why = Shortfall.None;

        var clip = Path.Combine(dir, "clip.mp4");
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

    private static async Task<List<byte[]>> DecodeAsync(string ffmpeg, string clipPath,
        double skipSeconds, double fps)
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
            "-vf", VideoFilter(skipSeconds, fps),
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
