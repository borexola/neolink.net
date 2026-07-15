// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using Neolink.Media;

namespace Neolink.Recording;

/// <summary>
/// Combines several continuous-recording segments into ONE classic MP4 — pure
/// concatenation, no re-encoding: each input's sample tables are read from its
/// classic index, the timelines are butt-joined (a real gap between segments
/// becomes a hard cut, like any NVR export), and the media bytes are
/// stream-copied verbatim into a single fast-start file (ftyp · moov · mdat,
/// exact length known up front, so downloads get a Content-Length).
///
/// Inputs go through <see cref="VirtualMp4.Open"/>, so old-format (fragmented)
/// recordings combine just like finalized ones. The catch with a single
/// container: every segment must carry the SAME stream configuration. A mid-day
/// record-stream switch (main → sub) changes resolution, and a single MP4 track
/// cannot change resolution mid-stream — <see cref="TryPlan"/> detects that and
/// reports why, so callers can fall back to the zip-of-segments export.
/// </summary>
public static class Mp4Export
{
    /// <summary>Everything needed to stream the combined file: the prebuilt
    /// header (ftyp + merged moov + mdat head) and, per input, the byte runs to
    /// copy. TotalBytes is the exact output size.</summary>
    public sealed class Plan
    {
        internal byte[] Header = Array.Empty<byte>();
        internal readonly List<(string Path, List<(long Offset, uint Size)> Runs)> Copies = new();
        public long TotalBytes { get; internal set; }
        public ulong DurationMs { get; internal set; }
    }

    private sealed record SegmentScan(string Path, byte[] Head, ClipWriter.InitLayout Layout,
        List<ClipWriter.SampleRec> Samples, int AudioRate);

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Reads every input's classic index and lays out the combined file, trimmed
    /// to [<paramref name="fromSec"/>, <paramref name="toSec"/>] seconds-of-day
    /// (each input carries its own start-of-day offset). The start snaps to the
    /// nearest video keyframe AT OR BEFORE the requested time — decoding can only
    /// begin on one, so the file starts at most one GOP (a few seconds) early
    /// instead of a whole segment; the end cuts exactly. Null (with a
    /// human-readable <paramref name="reason"/>) when the inputs cannot share one
    /// container — callers fall back to the zip export.
    /// </summary>
    public static Plan? TryPlan(IReadOnlyList<(string Path, double StartSeconds)> inputs,
        double fromSec, double toSec, out string? reason)
    {
        reason = null;
        if (inputs.Count == 0) { reason = "no segments in range"; return null; }

        var scans = new List<SegmentScan>(inputs.Count);
        var starts = new List<double>(inputs.Count);
        foreach (var (path, startSec) in inputs)
        {
            try
            {
                scans.Add(Scan(path));
                starts.Add(startSec);
            }
            catch (Exception ex)
            {
                reason = $"{Path.GetFileName(path)} cannot be combined ({ex.Message})";
                return null;
            }
        }

        // One container = one stream configuration: the codec entry (stsd) of
        // every track must match byte-for-byte across segments, and the track
        // layout itself (audio present or not, same sample rate) must agree.
        var first = scans[0];
        foreach (var s in scans.Skip(1))
        {
            if (s.Layout.Traks.Count != first.Layout.Traks.Count || s.AudioRate != first.AudioRate)
            {
                reason = "segments differ (audio track came or went mid-range)";
                return null;
            }
            for (int t = 0; t < first.Layout.Traks.Count; t++)
            {
                if (!StsdBytes(first.Head, first.Layout.Traks[t]).SequenceEqual(StsdBytes(s.Head, s.Layout.Traks[t])))
                {
                    reason = t == 0
                        ? "video format changes mid-range (resolution or codec switched — e.g. the record stream was changed)"
                        : "audio format changes mid-range";
                    return null;
                }
            }
        }

        // Trim each segment to the range, then butt-join the timelines. Both
        // tracks of a segment start on the same instant (the writer gates audio
        // on the first video keyframe), so each segment shifts by the VIDEO time
        // accumulated so far — audio follows the same wall clock instead of its
        // own length, keeping A/V in sync across every joint.
        var merged = new List<ClipWriter.SampleRec>(scans.Sum(s => s.Samples.Count));
        var plan = new Plan();
        ulong videoShift = 0;
        for (int i = 0; i < scans.Count; i++)
        {
            var s = scans[i];
            var kept = TrimSegment(s, fromSec - starts[i], toSec - starts[i]);
            if (kept.Count == 0) continue; // media truly outside the range
            ulong audioShift = s.AudioRate > 0 ? videoShift * (ulong)s.AudioRate / FMp4.Timescale : 0;
            ulong segVideoTicks = 0;
            var runs = new List<(long Offset, uint Size)>(kept.Count);
            foreach (var rec in kept)
            {
                if (rec.Track == 1)
                    segVideoTicks = Math.Max(segVideoTicks, rec.DecodeTime + rec.Duration);
                merged.Add(rec with { DecodeTime = rec.DecodeTime + (rec.Track == 1 ? videoShift : audioShift) });
                runs.Add((rec.Offset, rec.Size));
            }
            plan.Copies.Add((s.Path, runs));
            videoShift += segVideoTicks;
        }
        if (!merged.Any(m => m.Track == 1))
        {
            reason = "no footage in this range";
            return null;
        }

        // The moov must precede the data (fast start), but chunk offsets depend
        // on the moov's size — which depends on the offsets' width (stco/co64).
        // Iterate to the fixed point; widths only ever grow, so it settles fast.
        var ftyp = FtypBytes(first.Head);
        long payload = merged.Sum(m => (long)m.Size);
        long headerLen = 0;
        byte[] moov = Array.Empty<byte>();
        bool stable = false;
        for (int i = 0; i < 5 && !stable; i++)
        {
            long dataStart = headerLen == 0 ? ftyp.Length : headerLen;
            long running = dataStart;
            for (int k = 0; k < merged.Count; k++)
            {
                merged[k] = merged[k] with { Offset = running };
                running += merged[k].Size;
            }
            moov = ClipWriter.BuildClassicMoov(first.Head, first.Layout, merged, first.AudioRate);
            long mdatHead = payload + 8 > uint.MaxValue ? 16 : 8;
            long next = ftyp.Length + moov.Length + mdatHead;
            stable = next == headerLen;
            if (!stable) headerLen = next;
        }
        if (!stable) // cannot happen (offset widths only grow), but never emit a broken file
            throw new InvalidOperationException("output layout did not converge");

        var header = new byte[headerLen];
        ftyp.CopyTo(header, 0);
        moov.CopyTo(header, ftyp.Length);
        WriteMdatHead(header.AsSpan(ftyp.Length + moov.Length), payload);
        plan.Header = header;
        plan.TotalBytes = headerLen + payload;
        plan.DurationMs = videoShift * 1000 / FMp4.Timescale;
        return plan;
    }

    /// <summary>
    /// The samples of one segment that belong in [fromOff, toOff] seconds
    /// relative to the segment's own start, REBASED so the kept footage begins
    /// at zero again: video from the last keyframe at or before fromOff (a
    /// decoder can only start on one, so the cut lands at most one GOP early),
    /// everything strictly before toOff; audio follows the same wall instants.
    /// File order is preserved so the copy phase stays forward-only.
    /// </summary>
    private static List<ClipWriter.SampleRec> TrimSegment(SegmentScan s, double fromOff, double toOff)
    {
        if (toOff <= 0) return new List<ClipWriter.SampleRec>();
        ulong toTicks = (ulong)(toOff * FMp4.Timescale);

        // The cut-in: the last keyframe at or before fromOff — or the very start
        // when the range begins at/behind the segment's own beginning.
        ulong cutIn = 0;
        if (fromOff > 0)
        {
            ulong fromTicks = (ulong)(fromOff * FMp4.Timescale);
            bool anyAfter = false;
            foreach (var rec in s.Samples)
            {
                if (rec.Track != 1) continue;
                if (rec.Keyframe && rec.DecodeTime <= fromTicks) cutIn = Math.Max(cutIn, rec.DecodeTime);
                if (rec.DecodeTime + rec.Duration > fromTicks) anyAfter = true;
            }
            if (!anyAfter) // the media ends before the range (an mtime overstated it)
                return new List<ClipWriter.SampleRec>();
        }
        ulong audioCutIn = s.AudioRate > 0 ? cutIn * (ulong)s.AudioRate / FMp4.Timescale : 0;

        var kept = new List<ClipWriter.SampleRec>(s.Samples.Count);
        foreach (var rec in s.Samples)
        {
            if (rec.Track == 1)
            {
                if (rec.DecodeTime < cutIn || rec.DecodeTime >= toTicks) continue;
                kept.Add(rec with { DecodeTime = rec.DecodeTime - cutIn });
            }
            else
            {
                ulong dt90 = rec.DecodeTime * FMp4.Timescale / (ulong)s.AudioRate;
                if (dt90 < cutIn || dt90 >= toTicks) continue;
                kept.Add(rec with { DecodeTime = rec.DecodeTime - audioCutIn });
            }
        }
        return kept;
    }

    /// <summary>Streams the combined file: the prebuilt header, then every
    /// input's sample bytes in order. Inputs are read forward-only, so the copy
    /// runs at disk streaming speed even on cold spinning storage.</summary>
    public static async Task WriteAsync(Plan plan, Stream output, CancellationToken ct)
    {
        await output.WriteAsync(plan.Header, ct).ConfigureAwait(false);
        var buf = new byte[256 * 1024];
        foreach (var (path, runs) in plan.Copies)
        {
            await using var src = VirtualMp4.Open(path);
            foreach (var (offset, size) in runs)
            {
                src.Position = offset;
                long left = size;
                while (left > 0)
                {
                    int n = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct)
                        .ConfigureAwait(false);
                    if (n <= 0) throw new IOException($"{Path.GetFileName(path)} ended early — changed during export?");
                    await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    left -= n;
                }
            }
        }
    }

    // ------------------------------------------------------------------ input scanning

    /// <summary>Reads one segment's classic index into the same per-sample records
    /// the live writer tracks — via <see cref="VirtualMp4"/>, so old fragmented
    /// files parse exactly like finalized ones.</summary>
    private static SegmentScan Scan(string path)
    {
        using var s = VirtualMp4.Open(path);
        byte[]? ftyp = null, moov = null;
        long len = s.Length, pos = 0;
        var head8 = new byte[16];
        while (pos + 8 <= len)
        {
            s.Position = pos;
            ReadExactly(s, head8.AsSpan(0, 8));
            long size = BinaryPrimitives.ReadUInt32BigEndian(head8);
            var type = System.Text.Encoding.ASCII.GetString(head8, 4, 4);
            if (size == 1)
            {
                ReadExactly(s, head8.AsSpan(8, 8)); // 64-bit "largesize" form
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(head8.AsSpan(8)));
            }
            if (size < 8 || pos + size > len) throw new InvalidOperationException("truncated box structure");
            if (type == "ftyp") ftyp = ReadBox(s, pos, size);
            else if (type == "moov") moov = ReadBox(s, pos, size);
            pos += size;
        }
        if (ftyp == null || moov == null)
            throw new InvalidOperationException("no classic index (still being written?)");

        var head = new byte[ftyp.Length + moov.Length];
        ftyp.CopyTo(head, 0);
        moov.CopyTo(head, ftyp.Length);
        var layout = ClipWriter.AnalyzeInit(head);
        int audioRate = layout.Traks.Count > 1
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(layout.Traks[1].MdhdOff + 20))
            : 0;

        var samples = new List<ClipWriter.SampleRec>();
        for (int t = 0; t < layout.Traks.Count; t++)
            ParseTrackTables(head, layout.Traks[t], t == 0 ? (byte)1 : (byte)FMp4.AudioTrackId, samples);
        if (!samples.Any(x => x.Track == 1))
            throw new InvalidOperationException("no video samples");
        // File order — the interleave the writer chose — so the output keeps it
        // and the copy phase reads each input strictly forward.
        samples.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new SegmentScan(path, head, layout, samples, audioRate);
    }

    /// <summary>One track's stts/stss/stsc/stsz/stco(co64) → per-sample records.
    /// Only this muxer's shape is accepted (one sample per chunk, no ctts);
    /// anything else throws and the caller reports the file as not combinable.</summary>
    private static void ParseTrackTables(byte[] head, ClipWriter.TrakLayout t, byte track,
        List<ClipWriter.SampleRec> into)
    {
        List<uint>? durations = null, sizes = null;
        List<long>? offsets = null;
        HashSet<int>? keys = null;

        foreach (var (type, start, boxLen) in ClipWriter.Boxes(head, t.StsdEnd, t.Start + t.Len))
        {
            int p = start + 12; // past size/type/version/flags
            switch (type)
            {
                case "ctts":
                    throw new InvalidOperationException("composition offsets present (foreign file)");
                case "stts":
                {
                    uint runs = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p));
                    durations = new List<uint>();
                    for (uint r = 0; r < runs; r++)
                    {
                        uint count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 4 + (int)r * 8));
                        uint delta = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 8 + (int)r * 8));
                        for (uint c = 0; c < count; c++) durations.Add(delta);
                    }
                    break;
                }
                case "stss":
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p));
                    keys = new HashSet<int>();
                    for (uint i = 0; i < n; i++)
                        keys.Add((int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 4 + (int)i * 4)));
                    break;
                }
                case "stsc":
                {
                    // The writer emits exactly one sample per chunk; a different
                    // mapping means a foreign file we must not misread.
                    uint entries = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p));
                    if (entries > 1 || (entries == 1
                            && (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 4)) != 1
                                || BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 8)) != 1)))
                        throw new InvalidOperationException("unexpected chunk mapping (foreign file)");
                    break;
                }
                case "stsz":
                {
                    uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p));
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 4));
                    sizes = new List<uint>((int)n);
                    for (uint i = 0; i < n; i++)
                        sizes.Add(fixedSize != 0 ? fixedSize : BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 8 + (int)i * 4)));
                    break;
                }
                case "stco":
                case "co64":
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p));
                    offsets = new List<long>((int)n);
                    for (uint i = 0; i < n; i++)
                        offsets.Add(type == "stco"
                            ? BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(p + 4 + (int)i * 4))
                            : checked((long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(p + 4 + (int)i * 8))));
                    break;
                }
            }
        }

        if (durations == null || sizes == null || offsets == null
            || durations.Count != sizes.Count || sizes.Count != offsets.Count)
            throw new InvalidOperationException("incomplete sample tables");

        ulong decode = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            // Audio has no stss: every AAC frame is a sync sample.
            bool key = track != 1 || (keys?.Contains(i + 1) ?? false);
            into.Add(new ClipWriter.SampleRec(track, key, sizes[i], durations[i], decode, offsets[i]));
            decode += durations[i];
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static byte[] FtypBytes(byte[] head)
    {
        uint len = BinaryPrimitives.ReadUInt32BigEndian(head);
        return head.AsSpan(0, (int)len).ToArray();
    }

    /// <summary>The stsd box bytes of one track — codec, dimensions and parameter
    /// sets. Byte-equality across segments is the "same stream config" gate.</summary>
    private static ReadOnlySpan<byte> StsdBytes(byte[] head, ClipWriter.TrakLayout t)
    {
        foreach (var (type, start, len) in ClipWriter.Boxes(head, t.StblOff + 8, t.StsdEnd))
            if (type == "stsd")
                return head.AsSpan(start, len);
        throw new InvalidOperationException("no stsd");
    }

    private static void WriteMdatHead(Span<byte> dst, long payload)
    {
        if (payload + 8 <= uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)(payload + 8));
            "mdat"u8.CopyTo(dst[4..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst, 1);
            "mdat"u8.CopyTo(dst[4..]);
            BinaryPrimitives.WriteUInt64BigEndian(dst[8..], (ulong)(payload + 16));
        }
    }

    private static byte[] ReadBox(Stream s, long pos, long size)
    {
        if (size > 256 * 1024 * 1024) throw new InvalidOperationException("header box implausibly large");
        var buf = new byte[size];
        s.Position = pos;
        ReadExactly(s, buf);
        return buf;
    }

    private static void ReadExactly(Stream s, Span<byte> buf)
    {
        while (buf.Length > 0)
        {
            int n = s.Read(buf);
            if (n <= 0) throw new EndOfStreamException();
            buf = buf[n..];
        }
    }
}
