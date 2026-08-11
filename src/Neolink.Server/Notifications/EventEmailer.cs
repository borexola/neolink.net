// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using Neolink.Recording;

namespace Neolink.Notifications;

/// <summary>
/// Emails a camera's finished detection events, snapshots attached. Sits on the
/// recorder's event-closed callback: the recorder decided what was worth keeping
/// (the per-camera event-type filter), so anything that closed with footage is
/// by definition worth telling the recipient about — no second filter to keep
/// in sync.
///
/// Snapshots are sampled evenly across the finished clip with ffmpeg (read
/// through <see cref="FootageVault"/>, so encrypted footage samples the same as
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
    private readonly object _gate = new();

    public EventEmailer(NotificationStore store, Notifier notifier,
        RecordingSettings settings, EventStore events)
    {
        _store = store;
        _notifier = notifier;
        _settings = settings;
        _events = events;
    }

    /// <summary>The recorder's event-closed hook. Cheap checks inline (the
    /// recorder calls this on its own pump); everything heavier is detached.</summary>
    public void OnEventClosed(EventRecord rec)
    {
        // Nothing in here may escape into the recorder's pump — a mail problem
        // is a mail problem, never a recording one. The inner work is detached
        // and guarded too; this outer net covers even the settings reads.
        try
        {
            var s = _store.Snapshot();
            if (!s.Enabled || string.IsNullOrWhiteSpace(s.Recipient) || string.IsNullOrWhiteSpace(s.SmtpHost))
                return;
            if (!_settings.Get(rec.Camera).EmailEvents) return;

            lock (_gate)
            {
                if (_lastSent.TryGetValue(rec.Camera, out var last)
                    && s.EventCooldownMinutes > 0
                    && DateTime.UtcNow - last < TimeSpan.FromMinutes(s.EventCooldownMinutes))
                {
                    Log.Debug($"{rec.Camera}: event email skipped (cooldown, " +
                              $"{s.EventCooldownMinutes} min per camera) — the event is still recorded");
                    return;
                }
                _lastSent[rec.Camera] = DateTime.UtcNow;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ComposeAndSendAsync(rec, s).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"{rec.Camera}: event email could not be prepared: {Log.Flatten(ex)} " +
                             "— the event itself is recorded and unaffected");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event email skipped after an unexpected error: {Log.Flatten(ex)}");
        }
    }

    private async Task ComposeAndSendAsync(EventRecord rec, NotificationSettings s)
    {
        int want = Math.Clamp(s.EventSnapshots, 1, 50);
        var attachments = await SnapshotsAsync(rec, want).ConfigureAwait(false);

        var labels = rec.Labels.Count > 0 ? string.Join(" + ", rec.Labels) : "detection";
        var local = rec.StartUtc.ToLocalTime();
        var seconds = Math.Max(0, (rec.EndUtc - rec.StartUtc).TotalSeconds);
        var subject = $"{rec.Camera}: {labels} at {local:HH:mm:ss}";
        // When fewer snapshots arrive than were asked for, the mail says why —
        // otherwise "1 snapshot" against a setting of 3 looks like a bug.
        string attachNote = attachments.Count switch
        {
            0 => " No snapshot could be captured for this event.",
            var n when n < want && Media.Ffmpeg.ExePath == null =>
                $" {n} snapshot attached — sampling {want} frames from the clip needs ffmpeg, " +
                "which is not installed on this server, so this is the event's thumbnail.",
            var n when n < want =>
                $" {n} of the {want} snapshots asked for are attached (the clip held no more).",
            var n => $" {n} snapshot(s) attached.",
        };
        var body =
            $"{Cap(labels)} on {rec.Camera}, {local:yyyy-MM-dd HH:mm:ss} (local server time), " +
            $"about {seconds:0} seconds long." + attachNote +
            " The full clip is in the web UI's events page.";

        _notifier.Send(new Alert($"event:{rec.Id}", Recovery: false, subject,
            Headline: $"{Cap(labels)} — {rec.Camera}", body, Context: null,
            Attachments: attachments.Count > 0 ? attachments : null));
    }

    private static string Cap(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Evenly spaced JPEG frames from the event's clip; the thumbnail
    /// when the clip or ffmpeg is unavailable; empty only when both are.</summary>
    private async Task<List<EmailAttachment>> SnapshotsAsync(EventRecord rec, int want)
    {
        var dir = _events.EventDir(rec);
        var result = new List<EmailAttachment>();
        var safe = EventStore.SafeName(rec.Camera);

        var clip = Path.Combine(dir, "clip.mp4");
        // Why only one snapshot arrived is otherwise invisible to the person
        // reading the mail — every fallback below says so, at Info.
        if (want > 1 && Media.Ffmpeg.ExePath == null)
            Log.Info($"{rec.Camera}: event email attaches the thumbnail only — sampling {want} " +
                     "frames from the clip needs ffmpeg, and none was found on PATH " +
                     "(set NEOLINK_FFMPEG, or install ffmpeg; the Docker image ships one)");
        else if (want > 1 && !(rec.HasClip && File.Exists(clip)))
            Log.Info($"{rec.Camera}: event email attaches the thumbnail only — this event has no clip " +
                     "(recording was off, the disk was full, or the stream never carried a keyframe)");

        if (rec.HasClip && File.Exists(clip) && Media.Ffmpeg.ExePath is { } ffmpeg)
        {
            try
            {
                var frames = await SampleClipAsync(ffmpeg, clip, rec, want).ConfigureAwait(false);
                for (int i = 0; i < frames.Count; i++)
                    result.Add(new EmailAttachment($"{safe}-{i + 1}.jpg", "image/jpeg", frames[i]));
                if (frames.Count < want)
                    Log.Info($"{rec.Camera}: event email carries {frames.Count} of the {want} " +
                             "snapshots asked for — the clip held no more decodable frames");
            }
            catch (Exception ex)
            {
                Log.Warn($"{rec.Camera}: could not sample the clip for the event email " +
                         $"({Log.Flatten(ex)}) — attaching the thumbnail instead");
            }
        }
        if (result.Count > 0) return result;

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
        return result;
    }

    private static async Task<List<byte[]>> SampleClipAsync(string ffmpeg, string clipPath,
        EventRecord rec, int want)
    {
        // Decode at a rate that comfortably OVER-produces, then pick exactly the
        // frames wanted, evenly spaced, from what actually came out. Computing a
        // rate of want/duration instead — the obvious approach — quietly returns
        // fewer than asked whenever the duration estimate runs long (the clip
        // carries pre-roll the event's own length doesn't know about) or the
        // event is short: a 68 s event asked for 3 and got 1.
        double eventSeconds = Math.Max(1, (rec.EndUtc - rec.StartUtc).TotalSeconds);
        double fps = Math.Clamp(want / eventSeconds * 2.5, 0.5, 10);
        // Bounded work and memory whatever the clip: at most 4x the request, and
        // never more than 300 frames (~30 MB of 720p JPEG at the extreme).
        int maxFrames = Math.Min(300, Math.Max(want * 4, want + 8));
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
            "-frames:v", maxFrames.ToString(),
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
        return PickEvenly(Ai.AiPreroll.SplitJpegs(stdout.ToArray()), want);
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
