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
        /// <summary>Set once the raw download holds a complete MP4 index (moov at the front): the
        /// file can play while the rest arrives. Cancelled when it never does.</summary>
        public TaskCompletionSource<(string Raw, int Offset)> Playable { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Opening = new(StringComparer.Ordinal);

    /// <summary>One request at a time opens a given recording; the others then find its spool.</summary>
    public static async Task<IDisposable> OpeningAsync(string camera, string file, CancellationToken ct)
    {
        var gate = Opening.GetOrAdd(Key(camera, file), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>True once the recording has fully arrived and is playable at any speed.</summary>
    public static bool IsComplete(string camera, string file) =>
        Entries.TryGetValue(Key(camera, file), out var e) && e.File.Value.IsCompletedSuccessfully;

    /// <summary>True while a spool for the recording is still arriving (no spool at all = nothing to wait for).</summary>
    public static bool IsSpooling(string camera, string file) =>
        Entries.TryGetValue(Key(camera, file), out var e) && !e.File.Value.IsCompleted;

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
        var winner = Start(camera, file, download);
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

    /// <summary>Like <see cref="SpoolAsync"/>, but hands back a stream that plays while the camera
    /// is still sending, as soon as the MP4's index has arrived (slow battery cameras).</summary>
    public static async Task<(string? Ready, Stream? Growing)> ServeAsync(string camera, string file,
        ReolinkHttpApi.SdDownload download, CancellationToken ct)
    {
        var winner = Start(camera, file, download);
        try
        {
            var done = winner.File.Value;
            await Task.WhenAny(done, winner.Playable.Task).WaitAsync(ct).ConfigureAwait(false);
            winner.LastUsed = DateTime.UtcNow;
            if (done.IsCompleted) return (await done.ConfigureAwait(false), null);
            if (winner.Playable.Task.IsCompletedSuccessfully)
            {
                var (raw, offset) = winner.Playable.Task.Result;
                return (null, new GrowingStream(raw, offset, done));
            }
            return (await done.WaitAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Entries.TryRemove(Key(camera, file), out _);
            throw;
        }
    }

    private static Entry Start(string camera, string file, ReolinkHttpApi.SdDownload download)
    {
        Cleanup();
        Entry? entry = null;
        entry = new Entry(new Lazy<Task<string>>(() => Task.Run(() => CopyAsync(download, entry!))));
        var winner = Entries.GetOrAdd(Key(camera, file), entry);
        if (!ReferenceEquals(winner, entry))
            download.Dispose(); // someone else is already spooling this recording
        return winner;
    }

    /// <summary>Reads a download that is still being written, from the MP4's start, until the
    /// spool completes; then reports end of stream.</summary>
    private sealed class GrowingStream : Stream
    {
        private readonly FileStream _file;
        private readonly Task _done;
        public GrowingStream(string path, int offset, Task done)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, true);
            _file.Position = offset;
            _done = done;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                int n = await _file.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n > 0) return n;
                if (_done.IsCompleted)
                {
                    n = await _file.ReadAsync(buffer, ct).ConfigureAwait(false); // the last bytes, if any
                    return n;
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Whether playback can start without stalling: the media buffered so far must play
    /// longer than the rest takes to arrive at the measured rate (plus 2 s), and at least 5 s of it.
    /// Without a duration or a size, a quarter of the file, or 1 MB.</summary>
    internal static bool EnoughToPlay(long written, int indexEnd, long? expectedBytes, double? durationSeconds, double elapsedSeconds)
    {
        double media = written - indexEnd;
        double total = (expectedBytes ?? 0) - indexEnd;
        if (expectedBytes is { } exp && written >= exp) return true;
        if (durationSeconds is > 0 && total > 0)
        {
            double bitrate = total / durationSeconds.Value;                    // bytes per second of video
            double buffered = media / bitrate;                                  // seconds in hand
            double rate = written / Math.Max(0.2, elapsedSeconds);              // bytes per second arriving
            double remaining = (total - media) / Math.Max(1, rate);             // seconds until complete
            return buffered >= Math.Max(5, remaining + 2);
        }
        return media >= Math.Max(1024 * 1024, total * 0.25);
    }

    /// <summary>The clip's duration from the moov's mvhd, in seconds; null when not readable.</summary>
    internal static double? Mp4Duration(ReadOnlySpan<byte> head, int at)
    {
        var be = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian;
        if (at + 8 > head.Length) return null;
        int moovAt = at + (int)be(head[at..]);
        if (moovAt + 8 > head.Length || !head.Slice(moovAt + 4, 4).SequenceEqual("moov"u8)) return null;
        long moovEnd = Math.Min(head.Length, moovAt + be(head[moovAt..]));
        for (int p = moovAt + 8; p + 8 <= moovEnd;)
        {
            long size = be(head[p..]);
            if (size < 8) return null;
            if (head.Slice(p + 4, 4).SequenceEqual("mvhd"u8))
            {
                int version = head[p + 8];
                if (version == 0 && p + 28 <= head.Length)
                    return be(head[(p + 24)..]) / (double)Math.Max(1, be(head[(p + 20)..]));
                if (version == 1 && p + 40 <= head.Length)
                    return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(head[(p + 32)..])
                           / (double)Math.Max(1, be(head[(p + 28)..]));
                return null;
            }
            p += (int)size;
        }
        return null;
    }

    /// <summary>Where a complete MP4 index ends inside <paramref name="head"/>: the ftyp box at
    /// <paramref name="at"/>, then the moov box, both whole. Null while it is still arriving.</summary>
    internal static int? Mp4IndexEnd(ReadOnlySpan<byte> head, int at)
    {
        if (at + 8 > head.Length) return null;
        int ftyp = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[at..]);
        int moovAt = at + ftyp;
        if (moovAt + 8 > head.Length) return null;
        if (!head.Slice(moovAt + 4, 4).SequenceEqual("moov"u8)) return null;
        long moov = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[moovAt..]);
        if (moov < 8 || moov > 64 * 1024 * 1024) return null;
        return moovAt + moov <= head.Length ? (int)(moovAt + moov) : null;
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

    private static async Task<string> CopyAsync(ReolinkHttpApi.SdDownload download, Entry entry)
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
            // Copied by hand so the index can be spotted on the way: once the ftyp and moov
            // boxes are whole (plus a little media), the player may start on the raw file.
            var head = new byte[2 * 1024 * 1024];
            int headLen = 0;
            long written = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var buf = new byte[64 * 1024];
            await using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
            {
                int n;
                while ((n = await download.Stream.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                {
                    await f.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                    written += n;
                    if (headLen < head.Length)
                    {
                        int take = Math.Min(n, head.Length - headLen);
                        buf.AsSpan(0, take).CopyTo(head.AsSpan(headLen));
                        headLen += take;
                    }
                    if (!entry.Playable.Task.IsCompleted && download.NeedsSpool
                        && Mp4Offset(head.AsSpan(0, headLen)) is { } at
                        && Mp4IndexEnd(head.AsSpan(0, headLen), at) is { } indexEnd
                        && EnoughToPlay(written, indexEnd, download.ExpectedBytes, Mp4Duration(head.AsSpan(0, headLen), at),
                            clock.Elapsed.TotalSeconds))
                    {
                        await f.FlushAsync(cts.Token).ConfigureAwait(false);
                        entry.Playable.TrySetResult((tmp, at));
                    }
                }
            }
            entry.Playable.TrySetCanceled();
            clock.Restart();
            var ready = await NormalizeAsync(tmp, download, cts.Token).ConfigureAwait(false);
            if (clock.Elapsed > TimeSpan.FromMilliseconds(500))
                Log.Info($"SD spool: preparing {Path.GetFileName(ready)} for the player took {clock.Elapsed.TotalSeconds:0.0} s");
            return ready;
        }
        catch
        {
            entry.Playable.TrySetCanceled();
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
        for (int i = head.IndexOf("ftyp"u8); i >= 4; i = head[(i + 4)..].IndexOf("ftyp"u8) is var k and >= 0 ? i + 4 + k : -1)
        {
            int at = i - 4; // the box size precedes the type
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[at..]) is >= 16 and <= 1024
                && i + 8 <= head.Length && head.Slice(i + 4, 4).ToArray().All(b => b is >= 0x20 and < 0x7f))
                return at;
        }
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
        // Firmwares put the MP4 behind a BcMedia header (Lumus, doorbell) or behind that and
        // ~180 KB of thumbnails (Argus), so the first 2 MB are searched for its start.
        var head = new byte[2 * 1024 * 1024];
        int n;
        await using (var f = File.OpenRead(path))
            n = await f.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var kind = SniffContainer(head.AsSpan(0, n));
        if (kind == "mp4" || (kind == null && !download.NeedsSpool)) return path;
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
        {
            var kept = Path.ChangeExtension(path, ".bcm");
            File.Move(path, kept, overwrite: true);
            throw new IOException("the camera sent recording data Neolink can't play yet (it starts " +
                                  $"{Convert.ToHexString(head, 0, Math.Min(n, 48))}; the raw download is kept at {kept})");
        }
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
        // Idle per-file gates of recordings no longer spooled go too.
        foreach (var (key, gate) in Opening)
            if (!Entries.ContainsKey(key) && gate.CurrentCount == 1 && Opening.TryRemove(key, out var removed))
                removed.Dispose();
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
