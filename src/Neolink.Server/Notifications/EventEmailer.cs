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
                Log.Warn($"{rec.Camera}: event email failed: {Log.Flatten(ex)}");
            }
        });
    }

    private async Task ComposeAndSendAsync(EventRecord rec, NotificationSettings s)
    {
        int want = Math.Clamp(s.EventSnapshots, 1, 50);
        var attachments = await SnapshotsAsync(rec, want).ConfigureAwait(false);

        var labels = rec.Labels.Count > 0 ? string.Join(" + ", rec.Labels) : "detection";
        var local = rec.StartUtc.ToLocalTime();
        var seconds = Math.Max(0, (rec.EndUtc - rec.StartUtc).TotalSeconds);
        var subject = $"{rec.Camera}: {labels} at {local:HH:mm:ss}";
        var body =
            $"{Cap(labels)} on {rec.Camera}, {local:yyyy-MM-dd HH:mm:ss} (local server time), " +
            $"about {seconds:0} seconds long." +
            (attachments.Count > 0
                ? $" {attachments.Count} snapshot(s) attached."
                : " No snapshot could be captured for this event.") +
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
        if (rec.HasClip && File.Exists(clip) && Media.Ffmpeg.ExePath is { } ffmpeg)
        {
            try
            {
                var frames = await SampleClipAsync(ffmpeg, clip, rec, want).ConfigureAwait(false);
                for (int i = 0; i < frames.Count; i++)
                    result.Add(new EmailAttachment($"{safe}-{i + 1}.jpg", "image/jpeg", frames[i]));
            }
            catch (Exception ex)
            {
                Log.Debug($"{rec.Camera}: clip snapshot sampling failed ({Log.Flatten(ex)}); " +
                          "falling back to the thumbnail");
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
        // fps = frames wanted over the clip's length spreads them evenly; the
        // duration estimate includes the recorder's pre-roll (default 5 s), and
        // -frames:v caps the output so an estimate that runs short just spaces
        // the frames tighter instead of overshooting the attachment count.
        double duration = Math.Max(2, (rec.EndUtc - rec.StartUtc).TotalSeconds + 5);
        double fps = want / duration;
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
            "-frames:v", want.ToString(),
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
        return Ai.AiPreroll.SplitJpegs(stdout.ToArray()).Take(want).ToList();
    }
}
