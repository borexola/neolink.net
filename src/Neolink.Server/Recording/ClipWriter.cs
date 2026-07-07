// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Neolink.Media;
using Neolink.Streaming;

namespace Neolink.Recording;

/// <summary>
/// Writes hub video packets into a fragmented-MP4 file (playable by browsers and
/// ffmpeg alike). Same pacing trick as the live web player: a camera buffer's true
/// duration is only known when the NEXT buffer arrives (RTP delta), so one buffer
/// is held back and its measured delta spread evenly across its frames.
/// Video-only by design — detection clips don't need the audio track.
///
/// Disk I/O is fully decoupled from the caller: fragments are muxed on the
/// caller's thread but written by a dedicated background thread (below-normal
/// priority, never a thread-pool thread) behind a bounded memory budget. A slow
/// disk — HDD flush stalls, spun-down drives, network shares — can therefore
/// never block the media pipeline or starve the thread pool that live streaming
/// runs on. When the disk falls behind the budget, recorded frames are dropped
/// (with a warning) and writing resumes at the next keyframe.
///
/// The live-style init segment declares duration 0 (fine for MSE, where fragments
/// stream in). A FILE with duration 0 freezes browsers on the first frame — the
/// element believes the clip is zero-length. So the real duration is patched into
/// mvhd/tkhd/mdhd when the clip closes, and an mfra random-access index (keyframe
/// time → moof offset) is appended so external players (VLC, ffmpeg) can open and
/// seek the file like any ordinary MP4. Finalization happens on the writer thread
/// after <see cref="Dispose"/>; await <see cref="Completion"/> to observe it.
/// </summary>
public sealed class ClipWriter : IDisposable
{
    private const uint NominalFrame = 3000; // ~1/30s @ 90 kHz
    /// <summary>Max muxed video buffered in memory awaiting disk before frames are dropped.</summary>
    private const long QueueBudget = 16 * 1024 * 1024;

    private readonly string _path;
    private readonly VideoCodec _codec;
    private readonly BlockingCollection<QueueItem> _queue = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Mux state — owned by the caller's thread (one producer per writer).
    private uint _sequence = 1;
    private long _decodeTime; // 90 kHz ticks; long so the writer thread can Volatile.Read it
    private bool _haveTs;
    private uint _prevTs;
    private bool _waitKeyframe = true; // clips must start decodable
    private List<(byte[] Sample, bool Keyframe)>? _pending;
    private bool _dropping;
    private int _dropped;
    private bool _disposed;

    // Shared with the writer thread.
    private long _queuedBytes;
    private long _approxBytes; // cumulative bytes destined for the file (size-cap roll)
    private volatile bool _faulted;

    private readonly record struct QueueItem(byte[] Data, bool Keyframe, ulong DecodeTime);

    private ClipWriter(string path, VideoCodec codec, byte[] init)
    {
        _path = path;
        _codec = codec;
        _approxBytes = init.Length; // the init segment is already on its way to disk
        var boxes = FindHeaderBoxes(init);
        new Thread(() => WriteLoop(init, boxes))
        {
            IsBackground = true,
            Name = "clip-writer",
            Priority = ThreadPriority.BelowNormal, // disk work must never compete with streaming
        }.Start();
    }

    /// <summary>The file cannot be written (disk full/gone); callers should drop the writer.</summary>
    public bool Faulted => _faulted;

    /// <summary>Completes once the file is finalized (or given up on) after <see cref="Dispose"/>.</summary>
    public Task Completion => _done.Task;

    /// <summary>Seconds of video muxed so far (upper bound; pending frames included at nominal rate).</summary>
    public double DurationSeconds =>
        ((ulong)_decodeTime + (ulong)(_pending?.Count ?? 0) * NominalFrame) / (double)FMp4.Timescale;

    /// <summary>Approximate size of the file so far in bytes (used to cap segment size).</summary>
    public long ApproxBytes => Volatile.Read(ref _approxBytes);

    /// <summary>Prepares a clip file; null when codec params are not known yet. Never blocks on disk.</summary>
    public static ClipWriter? TryCreate(string path, IStreamHub hub)
    {
        var codec = hub.Codec;
        var sps = hub.Sps;
        var pps = hub.Pps;
        if (codec == null || sps == null || pps == null)
            return null;
        var init = FMp4.BuildInit(codec.Value, sps, pps, hub.Vps, hub.Width, hub.Height);
        return new ClipWriter(path, codec.Value, init);
    }

    /// <summary>
    /// Feeds the next video packet, in hub order. <paramref name="gap"/> signals that
    /// packets were dropped since the previous one — the caller must derive it from
    /// the hub index of EVERY packet (audio included): hub indices are global, so a
    /// video-only view sees non-consecutive indices whenever audio is interleaved,
    /// and treating those as drops would silently discard all P-frames.
    /// After a real drop, writing resumes at the next keyframe. Never blocks on disk.
    /// </summary>
    public void Add(HubVideo v, bool gap = false)
    {
        if (gap)
        {
            FlushPending((uint)(NominalFrame * (_pending?.Count ?? 1)));
            _haveTs = false;
            _waitKeyframe = true;
        }
        if (_waitKeyframe)
        {
            if (!v.Keyframe) return;
            _waitKeyframe = false;
        }

        if (_haveTs && _pending != null)
        {
            uint delta = unchecked(v.RtpTs - _prevTs);
            if (delta == 0 || delta > 30 * FMp4.Timescale)
                delta = (uint)(NominalFrame * _pending.Count); // clock garbage: assume ~30fps
            FlushPending(delta);
        }
        _prevTs = v.RtpTs;
        _haveTs = true;

        var aus = FMp4.SplitAccessUnits(_codec, v.AnnexB);
        if (aus.Count > 0)
            _pending = aus;
    }

    private void FlushPending(uint totalDuration)
    {
        if (_pending == null || _pending.Count == 0) { _pending = null; return; }
        uint per = Math.Clamp(totalDuration / (uint)_pending.Count, 900u, 45_000u); // 10..500ms per frame
        foreach (var (sample, key) in _pending)
        {
            Enqueue(FMp4.BuildFragment(_sequence++, (ulong)_decodeTime, per, sample, key), key, (ulong)_decodeTime);
            _decodeTime += per;
        }
        _pending = null;
    }

    /// <summary>
    /// Hands one muxed fragment to the writer thread — or drops it when the disk
    /// is too far behind. Drops start when the budget is exceeded and end at the
    /// first keyframe after the backlog has halved, so the file stays decodable.
    /// </summary>
    private void Enqueue(byte[] fragment, bool keyframe, ulong decodeTime)
    {
        if (_faulted) return;
        long queued = Interlocked.Read(ref _queuedBytes);
        if (_dropping)
        {
            if (!keyframe || queued > QueueBudget / 2)
            {
                _dropped++;
                return;
            }
            Log.Warn($"{_path}: storage caught up (dropped {_dropped} recorded frame(s))");
            _dropping = false;
            _dropped = 0;
        }
        else if (queued > QueueBudget)
        {
            _dropping = true;
            _dropped = 1;
            Log.Warn($"{_path}: storage cannot keep up (>{QueueBudget / (1024 * 1024)} MB pending); " +
                     "dropping recorded frames until it recovers — live streaming is unaffected");
            return;
        }

        Interlocked.Add(ref _queuedBytes, fragment.Length);
        Interlocked.Add(ref _approxBytes, fragment.Length);
        try
        {
            _queue.Add(new QueueItem(fragment, keyframe, decodeTime));
        }
        catch (InvalidOperationException)
        {
            // Adding completed concurrently (disposed): give the budget back.
            Interlocked.Add(ref _queuedBytes, -fragment.Length);
        }
    }

    /// <summary>
    /// Flushes the held-back frames and hands the file to the writer thread for
    /// finalization (mfra index + duration patch). Returns immediately; the file
    /// is complete when <see cref="Completion"/> finishes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        FlushPending((uint)(NominalFrame * (_pending?.Count ?? 1)));
        _disposed = true;
        _queue.CompleteAdding();
    }

    // ------------------------------------------------------------------ writer thread

    private void WriteLoop(byte[] init, Dictionary<string, int> boxes)
    {
        FileStream? file = null;
        bool failed = false;
        var syncPoints = new List<(ulong Time, long Offset)>();
        try
        {
            // Large buffer: fewer, bigger writes are what HDDs want.
            file = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
            file.Write(init);
            StorageMetrics.AddBytes(init.Length);
        }
        catch (Exception ex)
        {
            failed = true;
            _faulted = true;
            Log.Warn($"{_path}: cannot create clip file: {ex.Message}");
        }

        // Consume until the producer completes; after a failure keep draining so
        // the producer's memory budget is released instead of pinned.
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            Interlocked.Add(ref _queuedBytes, -item.Data.Length);
            if (failed) continue;
            try
            {
                if (item.Keyframe)
                    syncPoints.Add((item.DecodeTime, file!.Position));
                file!.Write(item.Data);
                StorageMetrics.AddBytes(item.Data.Length);
            }
            catch (Exception ex)
            {
                failed = true;
                _faulted = true;
                Log.Warn($"{_path}: clip write failed: {ex.Message}");
            }
        }

        if (!failed)
        {
            try
            {
                WriteMfra(file!, syncPoints);
                PatchDurations(file!, boxes);
                file!.Flush();
                StorageMetrics.FileCompleted();
            }
            catch (Exception ex)
            {
                Log.Warn($"{_path}: clip finalize failed: {ex.Message}");
            }
        }
        file?.Dispose();
        _done.TrySetResult();
    }

    /// <summary>Backfills the total duration into the init-segment headers.</summary>
    private void PatchDurations(FileStream file, Dictionary<string, int> boxes)
    {
        ulong decodeTime = (ulong)Volatile.Read(ref _decodeTime);
        if (decodeTime == 0) return;
        // mvhd/tkhd run on the movie timescale (1000 = ms), mdhd on the media
        // timescale (90 kHz). All are version-0 boxes, i.e. 32-bit durations.
        uint ms = (uint)Math.Min(uint.MaxValue, decodeTime * 1000 / FMp4.Timescale);
        uint ticks = (uint)Math.Min(uint.MaxValue, decodeTime);
        if (boxes.TryGetValue("mvhd", out var mvhd)) WriteU32At(file, mvhd + 24, ms);
        if (boxes.TryGetValue("tkhd", out var tkhd)) WriteU32At(file, tkhd + 28, ms);
        if (boxes.TryGetValue("mdhd", out var mdhd)) WriteU32At(file, mdhd + 24, ticks);
        file.Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// Appends the mfra box: one tfra entry per keyframe (media time → moof file
    /// offset) plus the mfro trailer players use to find the index from the file end.
    /// </summary>
    private static void WriteMfra(FileStream file, List<(ulong Time, long Offset)> syncPoints)
    {
        if (syncPoints.Count == 0) return;
        int tfraSize = 24 + syncPoints.Count * 19;
        int mfraSize = 8 + tfraSize + 16;
        var buf = new byte[mfraSize];
        var s = buf.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)mfraSize);
        "mfra"u8.CopyTo(s[4..]);

        var t = s[8..];
        BinaryPrimitives.WriteUInt32BigEndian(t, (uint)tfraSize);
        "tfra"u8.CopyTo(t[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(t[8..], 0x01000000); // version 1 (64-bit fields)
        BinaryPrimitives.WriteUInt32BigEndian(t[12..], 1);         // track_ID
        BinaryPrimitives.WriteUInt32BigEndian(t[16..], 0);         // 1-byte traf/trun/sample numbers
        BinaryPrimitives.WriteUInt32BigEndian(t[20..], (uint)syncPoints.Count);
        int p = 24;
        foreach (var (time, offset) in syncPoints)
        {
            BinaryPrimitives.WriteUInt64BigEndian(t[p..], time);
            BinaryPrimitives.WriteUInt64BigEndian(t[(p + 8)..], (ulong)offset);
            t[p + 16] = 1; // traf 1
            t[p + 17] = 1; // trun 1
            t[p + 18] = 1; // sample 1
            p += 19;
        }

        var m = s[(8 + tfraSize)..];
        BinaryPrimitives.WriteUInt32BigEndian(m, 16);
        "mfro"u8.CopyTo(m[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(m[8..], 0);           // version/flags
        BinaryPrimitives.WriteUInt32BigEndian(m[12..], (uint)mfraSize);

        file.Write(buf);
    }

    private static void WriteU32At(FileStream file, long offset, uint value)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        file.Seek(offset, SeekOrigin.Begin);
        file.Write(buf);
    }

    /// <summary>Absolute offsets of the duration-carrying boxes inside the init segment.</summary>
    private static Dictionary<string, int> FindHeaderBoxes(byte[] init)
    {
        var found = new Dictionary<string, int>();
        Walk(0, init.Length);
        return found;

        void Walk(int pos, int end)
        {
            while (pos + 8 <= end)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(pos));
                if (size < 8 || pos + size > (uint)end) return;
                var type = Encoding.ASCII.GetString(init, pos + 4, 4);
                if (type is "mvhd" or "tkhd" or "mdhd") found[type] = pos;
                if (type is "moov" or "trak" or "mdia") Walk(pos + 8, pos + (int)size);
                pos += (int)size;
            }
        }
    }
}
