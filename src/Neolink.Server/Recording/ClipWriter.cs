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
/// Writes hub media packets into a fragmented-MP4 file (playable by browsers and
/// ffmpeg alike). Same pacing trick as the live web player: a camera buffer's true
/// duration is only known when the NEXT buffer arrives (RTP delta), so one buffer
/// is held back and its measured delta spread evenly across its frames.
/// AAC audio is recorded as track 2 when the camera has it; ADPCM cameras record
/// video-only (raw PCM in MP4 is unplayable in browsers).
///
/// Disk I/O is fully decoupled from the caller: fragments are muxed on the
/// caller's thread but written by a dedicated background thread (below-normal
/// priority, never a thread-pool thread) behind a bounded memory budget. A slow
/// disk — HDD flush stalls, spun-down drives, network shares — can therefore
/// never block the media pipeline or starve the thread pool that live streaming
/// runs on. When the disk falls behind the budget, recorded frames are dropped
/// (with a warning) and writing resumes at the next keyframe.
///
/// While recording, the file is a live-style fragmented MP4 (crash-safe: every
/// flushed fragment is durable). That shape is terrible to SEEK over HTTP,
/// though: one moof per frame means a browser must range-request its way
/// through thousands of tiny headers to build an index — instant on localhost,
/// tens of seconds across a network. So when the clip closes it is finalized
/// IN PLACE into a classic indexed MP4: a moov with full sample tables (built
/// from offsets tracked during writing — no data is copied or moved) is
/// appended, every moof is retyped to a skippable "free" box, and lastly the
/// fragmented header up front is retired the same way. Players then seek by
/// byte offset with two or three range requests, like any ordinary MP4. A
/// crash at any point leaves a playable fragmented file; if the classic
/// finalize fails, the old fallback (duration patch + mfra keyframe index)
/// keeps the file usable. Finalization happens on the writer thread after
/// <see cref="Dispose"/>; await <see cref="Completion"/> to observe it.
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

    // Audio (AAC track 2) mux state — same producer thread.
    private readonly int _audioRate; // 0 = no audio track in this file
    private long _audioDecodeTime;   // sample-rate ticks; Volatile.Read on the writer thread
    private uint _prevAudioTs;
    private bool _haveAudioTs;

    // Shared with the writer thread.
    private long _queuedBytes;
    private long _approxBytes; // cumulative bytes destined for the file (size-cap roll)
    private volatile bool _faulted;

    private readonly record struct QueueItem(byte[] Data, bool Keyframe, ulong DecodeTime, byte Track, uint Duration);

    /// <summary>One written sample, tracked for the classic (indexed) finalize:
    /// where its payload bytes live in the file and how it sits on the timeline.</summary>
    internal readonly record struct SampleRec(byte Track, bool Keyframe, uint Size, uint Duration,
        ulong DecodeTime, long Offset);

    /// <summary>What a walk over an old-format (fragmented) file yields — everything
    /// needed to build the classic index, on disk (refinalize) or in memory
    /// (<see cref="VirtualMp4"/> serving). FragEnd is where the fragments stop:
    /// the legacy mfra index, a truncated tail, or end of file.</summary>
    internal readonly record struct FragmentedScan(byte[] Init, InitLayout Layout,
        List<SampleRec> Samples, int AudioRate, long FragEnd);

    private ClipWriter(string path, VideoCodec codec, byte[] init, int audioRate)
    {
        _path = path;
        _codec = codec;
        _audioRate = audioRate;
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
        var audio = hub.Audio is { IsAac: true, AudioSpecificConfig: not null } a ? a : null;
        var init = FMp4.BuildInit(codec.Value, sps, pps, hub.Vps, hub.Width, hub.Height,
            audio?.AudioSpecificConfig, audio?.SampleRate ?? 0, audio?.Channels ?? 0);
        return new ClipWriter(path, codec.Value, init, audio?.SampleRate ?? 0);
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

    /// <summary>
    /// Feeds one AAC access unit (hub order, same producer thread as
    /// <see cref="Add"/>). Ignored while the clip still waits for its first video
    /// keyframe, so both tracks start on the same instant. The decode-time advance
    /// comes from the RTP delta: dropped packets shift the timeline instead of
    /// desyncing it.
    /// </summary>
    public void AddAudio(HubAudioAac aac)
    {
        if (_audioRate == 0 || _waitKeyframe) return;
        if (_haveAudioTs)
        {
            uint d = unchecked(aac.RtpTs - _prevAudioTs);
            _audioDecodeTime += (d > 0 && d < 30u * (uint)_audioRate) ? d : FMp4.AacSamplesPerAu;
        }
        _prevAudioTs = aac.RtpTs;
        _haveAudioTs = true;
        Enqueue(FMp4.BuildFragment(_sequence++, (ulong)_audioDecodeTime, FMp4.AacSamplesPerAu, aac.Au,
                keyframe: true, trackId: FMp4.AudioTrackId),
            keyframe: false, (ulong)_audioDecodeTime, (byte)FMp4.AudioTrackId, FMp4.AacSamplesPerAu);
    }

    private void FlushPending(uint totalDuration)
    {
        if (_pending == null || _pending.Count == 0) { _pending = null; return; }
        uint per = Math.Clamp(totalDuration / (uint)_pending.Count, 900u, 45_000u); // 10..500ms per frame
        foreach (var (sample, key) in _pending)
        {
            Enqueue(FMp4.BuildFragment(_sequence++, (ulong)_decodeTime, per, sample, key),
                key, (ulong)_decodeTime, track: 1, per);
            _decodeTime += per;
        }
        _pending = null;
    }

    /// <summary>
    /// Hands one muxed fragment to the writer thread — or drops it when the disk
    /// is too far behind. Drops start when the budget is exceeded and end at the
    /// first keyframe after the backlog has halved, so the file stays decodable.
    /// </summary>
    private void Enqueue(byte[] fragment, bool keyframe, ulong decodeTime, byte track, uint duration)
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
            _queue.Add(new QueueItem(fragment, keyframe, decodeTime, track, duration));
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

    private void WriteLoop(byte[] init, Dictionary<string, List<int>> boxes)
    {
        FileStream? file = null;
        bool failed = false;
        var syncPoints = new List<(ulong Time, long Offset)>();
        // Per-sample bookkeeping for the classic finalize. Each fragment is one
        // moof + mdat holding exactly one sample, so the payload's file offset
        // and size fall out of the fragment head. ~32 bytes per frame in memory.
        var samples = new List<SampleRec>();
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
                long pos = file!.Position;
                if (item.Keyframe)
                    syncPoints.Add((item.DecodeTime, pos));
                uint moofSize = BinaryPrimitives.ReadUInt32BigEndian(item.Data);
                samples.Add(new SampleRec(item.Track, item.Keyframe,
                    (uint)(item.Data.Length - moofSize - 8), item.Duration, item.DecodeTime,
                    pos + moofSize + 8));
                file.Write(item.Data);
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
                FinalizeClassic(file!, init, samples);
                file!.Flush();
                StorageMetrics.FileCompleted();
            }
            catch (Exception ex)
            {
                // The file is still a valid fragmented MP4 — fall back to the
                // legacy finalize (duration patch + mfra keyframe index).
                Log.Warn($"{_path}: classic finalize failed ({ex.Message}); keeping fragmented layout");
                try
                {
                    WriteMfra(file!, syncPoints);
                    PatchDurations(file!, boxes);
                    file!.Flush();
                    StorageMetrics.FileCompleted();
                }
                catch (Exception ex2)
                {
                    Log.Warn($"{_path}: clip finalize failed: {ex2.Message}");
                }
            }
        }
        try
        {
            file?.Dispose();
        }
        catch (Exception ex)
        {
            // Dispose flushes the write buffer — a volume that filled or vanished
            // can throw HERE, and an unhandled exception on this raw thread would
            // take down the whole process.
            _faulted = true;
            Log.Warn($"{_path}: closing clip file failed: {ex.Message}");
        }
        _done.TrySetResult();
    }

    /// <summary>Backfills the total duration into the init-segment headers.</summary>
    private void PatchDurations(FileStream file, Dictionary<string, List<int>> boxes)
    {
        ulong decodeTime = (ulong)Volatile.Read(ref _decodeTime);
        if (decodeTime == 0) return;
        // mvhd/tkhd run on the movie timescale (1000 = ms); each mdhd runs on its
        // own media timescale (track 1: 90 kHz, track 2: the audio sample rate).
        // All are version-0 boxes, i.e. 32-bit durations. Tracks appear in the
        // init in ID order, so the box lists line up: [0]=video, [1]=audio.
        uint ms = (uint)Math.Min(uint.MaxValue, decodeTime * 1000 / FMp4.Timescale);
        uint ticks = (uint)Math.Min(uint.MaxValue, decodeTime);
        if (boxes.TryGetValue("mvhd", out var mvhd)) WriteU32At(file, mvhd[0] + 24, ms);
        if (boxes.TryGetValue("tkhd", out var tkhds))
            foreach (var tkhd in tkhds) WriteU32At(file, tkhd + 28, ms);
        if (boxes.TryGetValue("mdhd", out var mdhds))
        {
            WriteU32At(file, mdhds[0] + 24, ticks);
            if (mdhds.Count > 1)
                WriteU32At(file, mdhds[1] + 24,
                    (uint)Math.Min(uint.MaxValue, (ulong)Volatile.Read(ref _audioDecodeTime) + FMp4.AacSamplesPerAu));
        }
        file.Seek(0, SeekOrigin.End);
    }

    // ------------------------------------------------------------------ classic finalize

    /// <summary>
    /// Rewrites the closed file, in place and without moving a single media byte,
    /// into a classic indexed MP4: appends a moov whose sample tables were
    /// accumulated during writing, then — as the single commit step — rewrites
    /// the live header's box head so that ONE "free" box spans everything from
    /// there to the appended moov (the retired header plus every per-frame
    /// moof/mdat, whose payloads the new tables point into). Readers thus see
    /// just ftyp → free → moov and seek by byte offset in two or three range
    /// requests, instead of crawling thousands of per-frame fragment headers —
    /// the cause of minute-long timeline seeks over remote links. A crash
    /// anywhere in here leaves a valid fragmented file behind.
    /// </summary>
    private void FinalizeClassic(FileStream file, byte[] init, List<SampleRec> samples)
    {
        if (!samples.Any(s => s.Track == 1))
            throw new InvalidOperationException("no video samples");
        var layout = AnalyzeInit(init);
        file.Seek(0, SeekOrigin.End);
        long moovPos = file.Position;
        file.Write(BuildClassicMoov(init, layout, samples, _audioRate));
        CommitFreeSpan(file, layout.MoovOff, moovPos);
    }

    /// <summary>The commit step: one box head turns the whole fragment region
    /// into skippable free space (64-bit "largesize" form when it exceeds 4 GB).</summary>
    private static void CommitFreeSpan(FileStream file, long start, long end)
    {
        file.Seek(start, SeekOrigin.Begin);
        file.Write(FreeHeader(end - start)); // largesize form overwrites retired header content
    }

    /// <summary>A "free" box head covering <paramref name="len"/> bytes: the usual
    /// 8-byte form, or the 16-byte 64-bit "largesize" form past 4 GB.</summary>
    internal static byte[] FreeHeader(long len)
    {
        if (len <= uint.MaxValue)
        {
            var head = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)len);
            "free"u8.CopyTo(head.AsSpan(4));
            return head;
        }
        var wide = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(wide, 1);
        "free"u8.CopyTo(wide.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(wide.AsSpan(8), (ulong)len);
        return wide;
    }

    /// <summary>
    /// Upgrades an already-closed fragmented recording (from an older version)
    /// to the classic indexed layout, in place. Returns false when the file is
    /// already classic. Only understands this muxer's own single-sample
    /// fragments; anything unexpected throws before the file is touched.
    /// (Optional: <see cref="VirtualMp4"/> serves old files seek-fast without
    /// touching them — this exists for those who want the files themselves
    /// portable and index-complete.)
    /// </summary>
    public static bool RefinalizeClassic(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        if (!IsFragmented(file)) return false; // already finalized (free) or foreign
        var scan = ScanFragmented(file);
        file.Seek(0, SeekOrigin.End);
        long moovPos = file.Position;
        file.Write(BuildClassicMoov(scan.Init, scan.Layout, scan.Samples, scan.AudioRate));
        CommitFreeSpan(file, scan.Layout.MoovOff, moovPos);
        return true;
    }

    /// <summary>True when the file still has the live moov up front (old format /
    /// still being recorded), as opposed to the finalized free-span layout.
    /// A moov right after ftyp is not proof by itself: an ordinary fast-start
    /// classic file (a combined export, say) has the same silhouette — only the
    /// live fragmented header carries an mvex box, so that is the discriminator
    /// (checked by walking the moov's child HEADERS, a few seeks, no payload
    /// reads).</summary>
    internal static bool IsFragmented(FileStream file)
    {
        Span<byte> head = stackalloc byte[8];
        file.Seek(0, SeekOrigin.Begin);
        file.ReadExactly(head);
        uint ftypLen = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (!head[4..].SequenceEqual("ftyp"u8)) throw new InvalidOperationException("not an MP4");
        file.Seek(ftypLen, SeekOrigin.Begin);
        file.ReadExactly(head);
        if (!head[4..].SequenceEqual("moov"u8)) return false;

        long moovEnd = ftypLen + BinaryPrimitives.ReadUInt32BigEndian(head);
        long pos = ftypLen + 8;
        while (pos + 8 <= moovEnd)
        {
            file.Seek(pos, SeekOrigin.Begin);
            file.ReadExactly(head);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (size < 8 || pos + size > moovEnd) break;
            if (head[4..].SequenceEqual("mvex"u8)) return true;
            pos += size;
        }
        return false;
    }

    /// <summary>
    /// Walks an old-format file's fragments, reconstructing the same per-sample
    /// records the live writer tracks. tfhd/tfdt/trun offsets are fixed because
    /// this muxer always writes the same one-sample fragment shape; anything
    /// else throws. A truncated tail (crash mid-write, or a file still being
    /// recorded) simply ends the walk — the scan covers the complete samples.
    /// The pass is strictly SEQUENTIAL: hopping between the ~18k fragment heads
    /// of a big segment would be instant on cached flash but minutes of random
    /// reads on a cold spinning disk, which is where terabyte archives live —
    /// reading straight through goes at full disk streaming speed instead.
    /// </summary>
    internal static FragmentedScan ScanFragmented(FileStream file)
    {
        long end = file.Length;
        long pos = 0; // bytes consumed so far — the stream never seeks backwards
        file.Seek(0, SeekOrigin.Begin);
        var skip = new byte[64 * 1024];

        void Discard(long count)
        {
            while (count > 0)
            {
                int n = file.Read(skip, 0, (int)Math.Min(skip.Length, count));
                if (n <= 0) throw new EndOfStreamException();
                pos += n;
                count -= n;
            }
        }

        var boxHead = new byte[8];
        file.ReadExactly(boxHead);
        pos += 8;
        uint ftypLen = BinaryPrimitives.ReadUInt32BigEndian(boxHead);
        if (!boxHead.AsSpan(4).SequenceEqual("ftyp"u8)) throw new InvalidOperationException("not an MP4");

        // The init segment (ftyp + live moov) is reused verbatim by the builder.
        var ftypRest = new byte[ftypLen - 8];
        file.ReadExactly(ftypRest);
        pos += ftypRest.Length;
        file.ReadExactly(boxHead);
        pos += 8;
        uint moovLen = BinaryPrimitives.ReadUInt32BigEndian(boxHead);
        var init = new byte[ftypLen + moovLen];
        BinaryPrimitives.WriteUInt32BigEndian(init, ftypLen);
        "ftyp"u8.CopyTo(init.AsSpan(4));
        ftypRest.CopyTo(init.AsSpan(8));
        boxHead.CopyTo(init.AsSpan((int)ftypLen));
        file.ReadExactly(init.AsSpan((int)ftypLen + 8));
        pos += moovLen - 8;

        var layout = AnalyzeInit(init);
        int audioRate = layout.Traks.Count > 1
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(layout.Traks[1].MdhdOff + 20))
            : 0;

        var samples = new List<SampleRec>();
        while (pos + 8 <= end)
        {
            long boxStart = pos;
            file.ReadExactly(boxHead);
            pos += 8;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(boxHead);
            var type = Encoding.ASCII.GetString(boxHead, 4, 4);
            if (size < 8 || boxStart + size > end) { pos = boxStart; break; } // truncated tail
            if (type == "mfra") { pos = boxStart; break; } // legacy index; not media
            if (type == "moof")
            {
                var moof = new byte[size];
                boxHead.CopyTo(moof, 0);
                file.ReadExactly(moof.AsSpan(8));
                pos += size - 8;
                byte track = 0; bool key = false; uint dur = 0, sampleSize = 0, dataOff = 0; ulong dt = 0;
                foreach (var (t1, s1, l1) in Boxes(moof, 8, moof.Length))
                {
                    if (t1 != "traf") continue;
                    foreach (var (t2, s2, l2) in Boxes(moof, s1 + 8, s1 + l1))
                    {
                        switch (t2)
                        {
                            case "tfhd": track = moof[s2 + 15]; break;
                            case "tfdt": dt = BinaryPrimitives.ReadUInt64BigEndian(moof.AsSpan(s2 + 12)); break;
                            case "trun":
                                if (BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(s2 + 12)) != 1)
                                    throw new InvalidOperationException("not a single-sample fragment");
                                dataOff = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(s2 + 16));
                                key = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(s2 + 20)) == 0x02000000u;
                                dur = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(s2 + 24));
                                sampleSize = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(s2 + 28));
                                break;
                        }
                    }
                }
                if (track == 0 || sampleSize == 0)
                    throw new InvalidOperationException("unexpected fragment layout");
                samples.Add(new SampleRec(track, key, sampleSize, dur, dt, boxStart + dataOff));
            }
            else
            {
                Discard(size - 8); // mdat payload (or any stray box): stream past it
            }
        }
        if (!samples.Any(s => s.Track == 1))
            throw new InvalidOperationException("no video samples");
        return new FragmentedScan(init, layout, samples, audioRate, pos);
    }

    internal static byte[] BuildClassicMoov(byte[] init, InitLayout layout, List<SampleRec> samples, int audioRate)
    {
        var video = samples.Where(s => s.Track == 1).ToList();
        var audio = samples.Where(s => s.Track == FMp4.AudioTrackId).ToList();

        static ulong TotalTicks(List<SampleRec> s) => s.Count == 0 ? 0 : s[^1].DecodeTime + s[^1].Duration;
        ulong videoTicks = TotalTicks(video);
        ulong audioTicks = TotalTicks(audio);
        uint videoMs = (uint)Math.Min(uint.MaxValue, videoTicks * 1000 / FMp4.Timescale);
        uint audioMs = audioRate > 0 ? (uint)Math.Min(uint.MaxValue, audioTicks * 1000 / (ulong)audioRate) : 0;
        uint movieMs = Math.Max(videoMs, audioMs);

        // mvhd: the init's own copy, with the real duration dropped in.
        var mvhd = init.AsSpan(layout.MvhdOff, layout.MvhdLen).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(24), movieMs);

        // Each trak: everything through the stsd entry is reused verbatim from
        // the init (tkhd/mdhd/hdlr/codec config), then the four empty live
        // tables are replaced by the real ones. The tables are the tail of
        // stbl/minf/mdia/trak alike, so each ancestor just grows by the
        // difference.
        var traks = new List<byte[]>();
        for (int i = 0; i < layout.Traks.Count; i++)
        {
            bool isVideo = i == 0;
            var t = layout.Traks[i];
            var tables = BuildSampleTables(isVideo ? video : audio, isVideo);
            int headLen = t.StsdEnd - t.Start;
            var trak = new byte[headLen + tables.Length];
            init.AsSpan(t.Start, headLen).CopyTo(trak);
            tables.CopyTo(trak.AsSpan(headLen));
            int grow = tables.Length - (t.Len - headLen);
            foreach (int off in new[] { 0, t.MdiaOff - t.Start, t.MinfOff - t.Start, t.StblOff - t.Start })
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(trak.AsSpan(off));
                BinaryPrimitives.WriteUInt32BigEndian(trak.AsSpan(off), (uint)(size + grow));
            }
            BinaryPrimitives.WriteUInt32BigEndian(trak.AsSpan(t.TkhdOff - t.Start + 28), isVideo ? videoMs : audioMs);
            BinaryPrimitives.WriteUInt32BigEndian(trak.AsSpan(t.MdhdOff - t.Start + 24),
                (uint)Math.Min(uint.MaxValue, isVideo ? videoTicks : audioTicks));
            traks.Add(trak);
        }

        var moov = new byte[8 + mvhd.Length + traks.Sum(t => t.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(moov, (uint)moov.Length);
        "moov"u8.CopyTo(moov.AsSpan(4));
        int pos = 8;
        mvhd.CopyTo(moov.AsSpan(pos)); pos += mvhd.Length;
        foreach (var t in traks) { t.CopyTo(moov.AsSpan(pos)); pos += t.Length; }
        return moov;
    }

    /// <summary>The real stts/stss/stsc/stsz/stco tables for one track, replacing
    /// the empty placeholders the live init segment carries.</summary>
    private static byte[] BuildSampleTables(List<SampleRec> s, bool video)
    {
        var w = new Mp4Writer();
        using (w.FullBox("stts", 0, 0))
        {
            // Run-length durations. A sample lasts until the NEXT one's decode
            // time, so drop-gaps keep the wall clock; the last sample uses its
            // nominal duration.
            var runs = new List<(uint Delta, uint Count)>();
            for (int i = 0; i < s.Count; i++)
            {
                uint delta = i + 1 < s.Count
                    ? (uint)Math.Max(1, (long)(s[i + 1].DecodeTime - s[i].DecodeTime))
                    : Math.Max(1u, s[i].Duration);
                if (runs.Count > 0 && runs[^1].Delta == delta)
                    runs[^1] = (delta, runs[^1].Count + 1);
                else
                    runs.Add((delta, 1));
            }
            w.U32((uint)runs.Count);
            foreach (var (delta, count) in runs) { w.U32(count); w.U32(delta); }
        }
        if (video)
        {
            using (w.FullBox("stss", 0, 0))
            {
                var keys = new List<uint>();
                for (int i = 0; i < s.Count; i++)
                    if (s[i].Keyframe) keys.Add((uint)i + 1);
                w.U32((uint)keys.Count);
                foreach (var k in keys) w.U32(k);
            }
        }
        using (w.FullBox("stsc", 0, 0))
        {
            // Every chunk holds exactly one sample (each lives in its own mdat).
            w.U32(s.Count == 0 ? 0u : 1u);
            if (s.Count > 0) { w.U32(1); w.U32(1); w.U32(1); }
        }
        using (w.FullBox("stsz", 0, 0))
        {
            w.U32(0);
            w.U32((uint)s.Count);
            foreach (var x in s) w.U32(x.Size);
        }
        bool wide = s.Count > 0 && s[^1].Offset > uint.MaxValue;
        using (w.FullBox(wide ? "co64" : "stco", 0, 0))
        {
            w.U32((uint)s.Count);
            foreach (var x in s)
            {
                if (wide) w.U64((ulong)x.Offset);
                else w.U32((uint)x.Offset);
            }
        }
        return w.ToArray();
    }

    internal sealed record TrakLayout(int Start, int Len, int TkhdOff, int MdhdOff, int MdiaOff,
        int MinfOff, int StblOff, int StsdEnd);
    internal sealed record InitLayout(int MoovOff, int MvhdOff, int MvhdLen, List<TrakLayout> Traks);

    /// <summary>Locates the init-segment boxes the classic moov reuses or replaces.
    /// Works on any ftyp+moov blob: a live init segment and a finalized file's
    /// classic header share the structure this reads (Mp4Export relies on that).</summary>
    internal static InitLayout AnalyzeInit(byte[] init)
    {
        int moovOff = -1, mvhdOff = -1, mvhdLen = 0;
        var traks = new List<TrakLayout>();

        foreach (var (type, start, len) in Boxes(init, 0, init.Length))
        {
            if (type != "moov") continue;
            moovOff = start;
            foreach (var (t2, s2, l2) in Boxes(init, start + 8, start + len))
            {
                if (t2 == "mvhd") { mvhdOff = s2; mvhdLen = l2; }
                if (t2 != "trak") continue;
                int tkhd = -1, mdhd = -1, mdia = -1, minf = -1, stbl = -1, stsdEnd = -1;
                foreach (var (t3, s3, l3) in Boxes(init, s2 + 8, s2 + l2))
                {
                    if (t3 == "tkhd") tkhd = s3;
                    if (t3 != "mdia") continue;
                    mdia = s3;
                    foreach (var (t4, s4, l4) in Boxes(init, s3 + 8, s3 + l3))
                    {
                        if (t4 == "mdhd") mdhd = s4;
                        if (t4 != "minf") continue;
                        minf = s4;
                        foreach (var (t5, s5, l5) in Boxes(init, s4 + 8, s4 + l4))
                        {
                            if (t5 != "stbl") continue;
                            stbl = s5;
                            foreach (var (t6, s6, l6) in Boxes(init, s5 + 8, s5 + l5))
                                if (t6 == "stsd") stsdEnd = s6 + l6;
                        }
                    }
                }
                if (tkhd < 0 || mdhd < 0 || mdia < 0 || minf < 0 || stbl < 0 || stsdEnd < 0)
                    throw new InvalidOperationException("unexpected init-segment layout");
                traks.Add(new TrakLayout(s2, l2, tkhd, mdhd, mdia, minf, stbl, stsdEnd));
            }
        }
        if (moovOff < 0 || mvhdOff < 0 || traks.Count == 0)
            throw new InvalidOperationException("unexpected init-segment layout");
        return new InitLayout(moovOff, mvhdOff, mvhdLen, traks);
    }

    internal static IEnumerable<(string Type, int Start, int Len)> Boxes(byte[] b, int pos, int end)
    {
        while (pos + 8 <= end)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
            if (size < 8 || pos + size > (uint)end) yield break;
            yield return (Encoding.ASCII.GetString(b, pos + 4, 4), pos, (int)size);
            pos += (int)size;
        }
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

    /// <summary>Absolute offsets of the duration-carrying boxes inside the init
    /// segment, in file order (one tkhd/mdhd per track).</summary>
    private static Dictionary<string, List<int>> FindHeaderBoxes(byte[] init)
    {
        var found = new Dictionary<string, List<int>>();
        Walk(0, init.Length);
        return found;

        void Walk(int pos, int end)
        {
            while (pos + 8 <= end)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(pos));
                if (size < 8 || pos + size > (uint)end) return;
                var type = Encoding.ASCII.GetString(init, pos + 4, 4);
                if (type is "mvhd" or "tkhd" or "mdhd")
                    (found.TryGetValue(type, out var list) ? list : found[type] = new()).Add(pos);
                if (type is "moov" or "trak" or "mdia") Walk(pos + 8, pos + (int)size);
                pos += (int)size;
            }
        }
    }
}
