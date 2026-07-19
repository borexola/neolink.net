using System.Collections.Concurrent;
using Neolink.Protocol;

namespace Neolink.Web;

/// <summary>
/// Spools SD-card recordings from the camera into temp files so the web player
/// can actually play them. Two camera realities force this:
///   1. The camera serves a Download strictly SEQUENTIALLY — no byte ranges.
///   2. Camera MP4s carry their moov index at the END of the file, so a browser
///      streaming from the front never finds the index — the player waits
///      forever (field report: SD recordings listed but never played).
/// A spooled file is served with full range processing: playback starts as soon
/// as the spool completes, scrubbing works, and the browser's multiple range
/// probes hit the SAME temp file instead of re-downloading from the camera.
///
/// The spool is bounded: only files up to <see cref="MaxBytes"/> (event clips
/// are a few MB; anything bigger streams directly like before), kept for
/// <see cref="KeepFor"/> since last use, cleaned eagerly on access and wholesale
/// at startup. A spool runs on its own clock — browsers abort their first probe
/// request routinely, and that must not kill the transfer the follow-up range
/// request is waiting for.
/// </summary>
internal static class SdSpool
{
    /// <summary>Files above this stream directly (no spool, no seeking) — spooling
    /// a multi-hundred-MB continuous segment would stall playback for minutes.</summary>
    public const long MaxBytes = 300L * 1024 * 1024;

    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SpoolTimeout = TimeSpan.FromMinutes(5);

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "neolink-sd-spool");
    private sealed record Entry(Lazy<Task<string>> File)
    {
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static bool _cleaned;

    private static string Key(string camera, string file) => $"{camera}\n{file}";

    /// <summary>The already-spooled temp file for this recording, or null. Touches
    /// the entry so an actively-watched clip isn't evicted mid-scrub.</summary>
    public static async Task<string?> TryGetAsync(string camera, string file, CancellationToken ct)
    {
        Cleanup();
        if (!Entries.TryGetValue(Key(camera, file), out var e)) return null;
        try
        {
            var path = await e.File.Value.WaitAsync(ct).ConfigureAwait(false);
            e.LastUsed = DateTime.UtcNow;
            return File.Exists(path) ? path : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // The spool that created this entry failed — clear it so the caller
            // fetches fresh.
            Entries.TryRemove(Key(camera, file), out _);
            return null;
        }
    }

    /// <summary>Spools <paramref name="download"/> to a temp file (taking ownership
    /// of it) and returns the path. Concurrent callers for the same recording share
    /// one spool; the loser's download is disposed unused. The transfer runs on its
    /// own timeout, not the caller's request — an aborted browser probe must not
    /// kill the spool the next range request needs — but the CALLER's wait is
    /// bounded by <paramref name="ct"/>.</summary>
    public static async Task<string> SpoolAsync(string camera, string file,
        ReolinkHttpApi.SdDownload download, CancellationToken ct)
    {
        Cleanup();
        var entry = new Entry(new Lazy<Task<string>>(() => Task.Run(() => CopyAsync(download))));
        var winner = Entries.GetOrAdd(Key(camera, file), entry);
        if (!ReferenceEquals(winner, entry))
            download.Dispose(); // someone else is already spooling this recording
        try
        {
            var path = await winner.File.Value.WaitAsync(ct).ConfigureAwait(false);
            winner.LastUsed = DateTime.UtcNow;
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Entries.TryRemove(Key(camera, file), out _); // failed spools don't stick
            throw;
        }
    }

    private static async Task<string> CopyAsync(ReolinkHttpApi.SdDownload download)
    {
        if (!_cleaned)
        {
            _cleaned = true;
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Dir, $"{Guid.NewGuid():N}.mp4");
        using var cts = new CancellationTokenSource(SpoolTimeout);
        try
        {
            await using (var f = File.Create(tmp))
                await download.Stream.CopyToAsync(f, cts.Token).ConfigureAwait(false);
            return tmp;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        finally
        {
            download.Dispose();
        }
    }

    private static void Cleanup()
    {
        var cutoff = DateTime.UtcNow - KeepFor;
        foreach (var (key, e) in Entries)
        {
            if (e.LastUsed >= cutoff || !e.File.Value.IsCompleted) continue;
            try
            {
                if (e.File.Value.IsCompletedSuccessfully)
                    File.Delete(e.File.Value.Result);
                Entries.TryRemove(key, out _);
            }
            catch
            {
                // The file is busy (still being served) — keep the entry, retry later.
            }
        }
    }
}
