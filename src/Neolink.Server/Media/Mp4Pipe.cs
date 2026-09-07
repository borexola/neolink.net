// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Text;

namespace Neolink.Media;

/// <summary>
/// Feeds an MP4 to a reader that cannot seek (ffmpeg on stdin). A finalized clip
/// keeps its index (moov) at the END of the file, behind the media, and hides the
/// media inside a retired "free" box; a plain byte copy hands the reader every
/// media byte before it knows what any of them is, and ffmpeg gives up ("partial
/// file", no frames). The plan moves the index ahead of the media, shifts every
/// chunk offset by the bytes the index now displaces, and retypes the free boxes
/// to mdat so the reader stops walking atoms and starts reading samples — which
/// it then does in file order, so every seek is a forward one. A file that
/// already leads with its index (still recording, pre-upgrade fragmented, foreign
/// fast-start) copies as-is, cut-off tail included — a clip still being written
/// ends mid-box, and the reader copes with that as it always has.
/// </summary>
public sealed class Mp4Pipe
{
    private const long MaxMoovBytes = 128L * 1024 * 1024;

    private readonly record struct Box(long Offset, long Size, int HeaderLength, string Type, bool Truncated = false);

    private readonly Stream _src;
    private readonly List<Box> _boxes;
    private readonly int _moovIndex;
    private readonly byte[]? _moov;
    private readonly long _insertAt;

    /// <summary>Sync samples of the first video track, when the index says.</summary>
    public int? VideoKeyframes { get; private set; }
    public double? DurationSeconds { get; private set; }
    /// <summary>The index had to move ahead of the media.</summary>
    public bool Rewritten => _moov != null;

    private Mp4Pipe(Stream src, List<Box> boxes, int moovIndex)
    {
        _src = src;
        _boxes = boxes;
        _moovIndex = moovIndex;
        if (moovIndex < 0 || boxes[moovIndex].Truncated) return;
        var moov = boxes[moovIndex];
        bool mediaBefore = boxes.Take(moovIndex).Any(b => b.Type is "mdat" or "moof" or "free" or "skip");
        if (moov.Size > MaxMoovBytes) throw new InvalidDataException($"moov of {moov.Size} bytes is larger than the {MaxMoovBytes / (1024 * 1024)} MB limit");
        var bytes = new byte[moov.Size];
        src.Seek(moov.Offset, SeekOrigin.Begin);
        src.ReadExactly(bytes);
        _insertAt = boxes[0].Type == "ftyp" ? boxes[0].Size : 0;
        // Offsets never point inside the index itself: media before it slides
        // down by the index's length, media after it stays where it was (the
        // index leaves as many bytes ahead of it as it adds).
        long Shift(long offset) => offset >= _insertAt && offset < moov.Offset ? offset + moov.Size : offset;
        bool video = false;
        Walk(bytes, moov.HeaderLength, bytes.Length, mediaBefore ? Shift : null, ref video);
        if (mediaBefore) _moov = bytes;
    }

    public static Mp4Pipe Open(Stream src)
    {
        if (!src.CanSeek) throw new ArgumentException("the source must be seekable", nameof(src));
        long length = src.Length;
        var boxes = new List<Box>();
        Span<byte> head = stackalloc byte[16];
        long pos = 0;
        int moovIndex = -1;
        while (pos + 8 <= length)
        {
            src.Seek(pos, SeekOrigin.Begin);
            src.ReadExactly(head[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = Encoding.ASCII.GetString(head[4..8]);
            int headerLength = 8;
            if (size == 1)
            {
                if (pos + 16 > length) break;
                src.ReadExactly(head[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..16]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = length - pos;
            }
            if (size < headerLength)
                throw new InvalidDataException($"box '{type}' at {pos} claims {size} bytes, less than its own header");
            bool truncated = pos + size > length;
            if (truncated) size = length - pos;
            if (type == "moov" && moovIndex < 0) moovIndex = boxes.Count;
            boxes.Add(new Box(pos, size, headerLength, type, truncated));
            pos += size;
        }
        if (boxes.Count == 0) throw new InvalidDataException("not an MP4: no boxes");
        return new Mp4Pipe(src, boxes, moovIndex);
    }

    /// <summary>Copies the file in a decodable order for a non-seeking reader.</summary>
    public async Task CopyToAsync(Stream dst, CancellationToken ct)
    {
        if (_moov == null)
        {
            _src.Seek(0, SeekOrigin.Begin);
            await _src.CopyToAsync(dst, ct).ConfigureAwait(false);
            return;
        }
        var buffer = new byte[1 << 16];
        bool inserted = _insertAt == 0;
        if (inserted) await dst.WriteAsync(_moov, ct).ConfigureAwait(false);
        for (int i = 0; i < _boxes.Count; i++)
        {
            if (i == _moovIndex) continue;
            var box = _boxes[i];
            if (box.Type is "free" or "skip")
            {
                _src.Seek(box.Offset, SeekOrigin.Begin);
                await _src.ReadExactlyAsync(buffer.AsMemory(0, box.HeaderLength), ct).ConfigureAwait(false);
                "mdat"u8.CopyTo(buffer.AsSpan(4, 4));
                await dst.WriteAsync(buffer.AsMemory(0, box.HeaderLength), ct).ConfigureAwait(false);
                await CopyRangeAsync(box.Offset + box.HeaderLength, box.Size - box.HeaderLength, dst, buffer, ct).ConfigureAwait(false);
            }
            else
            {
                await CopyRangeAsync(box.Offset, box.Size, dst, buffer, ct).ConfigureAwait(false);
            }
            if (!inserted && box.Offset + box.Size == _insertAt)
            {
                await dst.WriteAsync(_moov, ct).ConfigureAwait(false);
                inserted = true;
            }
        }
    }

    private async Task CopyRangeAsync(long offset, long count, Stream dst, byte[] buffer, CancellationToken ct)
    {
        _src.Seek(offset, SeekOrigin.Begin);
        while (count > 0)
        {
            int n = await _src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("the file ended inside a box");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            count -= n;
        }
    }

    /// <summary>Reads the index for its duration and the video track's sync-sample
    /// count and, when <paramref name="shift"/> is given, rewrites every chunk
    /// offset in place. A track's handler precedes its sample tables, so the
    /// video flag set by hdlr is in force by the time stss comes round.</summary>
    private void Walk(byte[] moov, int pos, int end, Func<long, long>? shift, ref bool isVideo)
    {
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(pos));
            var type = Encoding.ASCII.GetString(moov, pos + 4, 4);
            int header = 8;
            if (size == 1)
            {
                if (pos + 16 > end) throw new InvalidDataException("truncated box header in moov");
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(pos + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < header || pos + size > end)
                throw new InvalidDataException($"box '{type}' overruns its parent in moov");
            int body = pos + header, bodyEnd = (int)(pos + size);
            switch (type)
            {
                case "trak":
                {
                    bool video = false;
                    Walk(moov, body, bodyEnd, shift, ref video);
                    break;
                }
                case "mdia" or "minf" or "stbl":
                    Walk(moov, body, bodyEnd, shift, ref isVideo);
                    break;
                case "mvhd":
                    ReadMvhd(moov, body, bodyEnd);
                    break;
                case "hdlr" when bodyEnd - body >= 12:
                    if (Encoding.ASCII.GetString(moov, body + 8, 4) == "vide") isVideo = true;
                    break;
                case "stss" when isVideo && VideoKeyframes == null && bodyEnd - body >= 8:
                    VideoKeyframes = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 4)));
                    break;
                case "stco" when shift != null && bodyEnd - body >= 8:
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 4));
                    if (body + 8 + 4L * n > bodyEnd) throw new InvalidDataException("stco table overruns its box");
                    for (int i = 0; i < n; i++)
                    {
                        var entry = moov.AsSpan(body + 8 + 4 * i, 4);
                        long moved = shift(BinaryPrimitives.ReadUInt32BigEndian(entry));
                        if (moved > uint.MaxValue) throw new InvalidDataException("a chunk offset no longer fits 32 bits once the index moves");
                        BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)moved);
                    }
                    break;
                }
                case "co64" when shift != null && bodyEnd - body >= 8:
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 4));
                    if (body + 8 + 8L * n > bodyEnd) throw new InvalidDataException("co64 table overruns its box");
                    for (int i = 0; i < n; i++)
                    {
                        var entry = moov.AsSpan(body + 8 + 8 * i, 8);
                        BinaryPrimitives.WriteUInt64BigEndian(entry, (ulong)shift((long)BinaryPrimitives.ReadUInt64BigEndian(entry)));
                    }
                    break;
                }
            }
            pos = bodyEnd;
        }
    }

    private void ReadMvhd(byte[] moov, int body, int end)
    {
        if (end - body < 4) return;
        int version = moov[body];
        if (version == 0 && end - body >= 20)
        {
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 12));
            uint duration = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 16));
            if (timescale > 0 && duration != uint.MaxValue) DurationSeconds = duration / (double)timescale;
        }
        else if (version == 1 && end - body >= 32)
        {
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 20));
            ulong duration = BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(body + 24));
            if (timescale > 0 && duration != ulong.MaxValue) DurationSeconds = duration / (double)timescale;
        }
    }
}
