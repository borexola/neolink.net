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

    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan SpoolTimeout = TimeSpan.FromMinutes(5);

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "neolink-sd-spool");
    private sealed record Entry(Lazy<Task<string>> File)
    {
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static bool _cleaned;
    /// <summary>Expired spools go even when nobody opens the SD view again.</summary>
    private static readonly Timer Sweeper = new(_ => { try { Cleanup(); } catch { } }, null, 60_000, 60_000);

    private static string Key(string camera, string file) => $"{camera}\n{file}";

    /// <summary>Why a recording last failed to serve, kept 2 min so the player can show it.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, string Reason)> Failures = new(StringComparer.Ordinal);

    public static void NoteFailure(string camera, string file, string reason) =>
        Failures[Key(camera, file)] = (DateTime.UtcNow, reason);

    public static string? RecentFailure(string camera, string file) =>
        Failures.TryGetValue(Key(camera, file), out var f) && DateTime.UtcNow - f.At < TimeSpan.FromMinutes(2)
            ? f.Reason : null;

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

    /// <summary>A re-encode of a spooled recording whose video a browser can't decode: ffmpeg
    /// conceals the damaged frames the way VLC does. Cached like a spool.</summary>
    public static async Task<string> RepairAsync(string camera, string file, string spooled, CancellationToken ct)
    {
        Cleanup();
        var entry = new Entry(new Lazy<Task<string>>(() => Task.Run(() => ReencodeAsync(spooled))));
        var winner = Entries.GetOrAdd(Key(camera, file + "\nrepaired"), entry);
        try
        {
            var path = await winner.File.Value.WaitAsync(ct).ConfigureAwait(false);
            winner.LastUsed = DateTime.UtcNow;
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Entries.TryRemove(Key(camera, file + "\nrepaired"), out _);
            throw;
        }
    }

    private static async Task<string> ReencodeAsync(string spooled)
    {
        var ffmpeg = Media.Ffmpeg.ExePath ?? throw new IOException("repairing a damaged recording needs ffmpeg");
        var repaired = Path.Combine(Dir, $"{Guid.NewGuid():N}.mp4");
        using var cts = new CancellationTokenSource(SpoolTimeout);
        var (_, err) = await Media.Ffmpeg.RunAsync(ffmpeg,
            new[] { "-v", "error", "-y", "-i", spooled,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "48k", "-movflags", "+faststart", "-f", "mp4", repaired },
            Array.Empty<byte[]>(), SpoolTimeout, cts.Token).ConfigureAwait(false);
        if (!File.Exists(repaired) || new FileInfo(repaired).Length == 0)
            throw new IOException($"repairing the recording failed: {err.Trim()}");
        Log.Info($"SD recording {Path.GetFileName(spooled)}: damaged video re-encoded with concealment " +
                 $"({new FileInfo(repaired).Length / 1024} KB)");
        return repaired;
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
            return await NormalizeAsync(tmp, download, cts.Token).ConfigureAwait(false);
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

    /// <summary>Where an MP4 (an ftyp box) begins inside <paramref name="head"/>, past any leading header; null if none.</summary>
    internal static int? Mp4Offset(ReadOnlySpan<byte> head)
    {
        for (int i = 8; i + 8 <= head.Length; i++)
            if (head.Slice(i + 4, 4).SequenceEqual("ftyp"u8)
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[i..]) is >= 16 and <= 1024)
                return i;
        return null;
    }

    private static async Task CutHeadAsync(string path, int skip, CancellationToken ct)
    {
        var cut = path + ".cut";
        await using (var src = File.OpenRead(path))
        await using (var dst = File.Create(cut))
        {
            src.Position = skip;
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(cut, path, overwrite: true);
    }

    /// <summary>"mp4", "flv" or null from a file's first bytes.</summary>
    internal static string? SniffContainer(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[..3].SequenceEqual("FLV"u8) ? "flv"
        : head.Length >= 8 && head[4..8].SequenceEqual("ftyp"u8) ? "mp4"
        : null;

    /// <summary>MP4 stays as spooled; FLV (some firmwares' Playback) is remuxed, and BcMedia
    /// frames (a Baichuan download) are muxed, into a seekable MP4; anything else is refused.</summary>
    private static async Task<string> NormalizeAsync(string path, ReolinkHttpApi.SdDownload download, CancellationToken ct)
    {
        var head = new byte[512];
        int n;
        await using (var f = File.OpenRead(path))
            n = await f.ReadAsync(head, ct).ConfigureAwait(false);
        var kind = SniffContainer(head.AsSpan(0, n));
        if (kind == "mp4" || (kind == null && !download.NeedsSpool)) return path;
        // Some firmwares (Lumus) send the MP4 file itself behind one BcMedia header.
        if (kind == null && Mp4Offset(head.AsSpan(0, n)) is { } skip)
        {
            await CutHeadAsync(path, skip, ct).ConfigureAwait(false);
            return path;
        }
        if (kind == null && Media.BcMediaMux.LooksLikeBcMedia(head.AsSpan(0, n)))
        {
            var frames = Path.ChangeExtension(path, ".bcm");
            File.Move(path, frames);
            // The raw download stays beside the MP4 until cleanup: it is what a bug report needs.
            try
            {
                await Media.BcMediaMux.ToMp4Async(frames, path, ct).ConfigureAwait(false);
                return path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"{ex.Message} (the raw download is kept at {frames})", ex);
            }
        }
        if (kind != "flv")
            throw new IOException("the camera sent recording data Neolink can't play yet (it starts " +
                                  $"{Convert.ToHexString(head, 0, n)}); please report this line");
        var ffmpeg = Media.Ffmpeg.ExePath
                     ?? throw new IOException("this camera serves recordings as FLV, which needs ffmpeg to play");
        var flv = Path.ChangeExtension(path, ".flv");
        File.Move(path, flv);
        try
        {
            var (_, err) = await Media.Ffmpeg.RunAsync(ffmpeg,
                new[] { "-v", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", "-f", "mp4", path },
                Array.Empty<byte[]>(), TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException($"remuxing the camera's FLV recording failed: {err.Trim()}");
            return path;
        }
        finally
        {
            try { File.Delete(flv); } catch { }
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
                {
                    File.Delete(e.File.Value.Result);
                    File.Delete(Path.ChangeExtension(e.File.Value.Result, ".bcm"));
                }
                Entries.TryRemove(key, out _);
            }
            catch
            {
                // The file is busy (still being served) — keep the entry, retry later.
            }
        }
        // Strays (a raw download kept after a failed conversion, a crashed copy) go after an hour.
        try
        {
            if (!Directory.Exists(Dir)) return;
            var held = new HashSet<string>(Entries.Values
                .Where(e => e.File.Value.IsCompletedSuccessfully).Select(e => e.File.Value.Result), StringComparer.Ordinal);
            foreach (var f in Directory.EnumerateFiles(Dir))
                if (!held.Contains(f) && !held.Contains(Path.ChangeExtension(f, ".mp4"))
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
