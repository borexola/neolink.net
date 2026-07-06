using System.Buffers.Binary;
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
/// The live-style init segment declares duration 0 (fine for MSE, where fragments
/// stream in). A FILE with duration 0 freezes browsers on the first frame — the
/// element believes the clip is zero-length. So the real duration is patched into
/// mvhd/tkhd/mdhd when the clip closes, and an mfra random-access index (keyframe
/// time → moof offset) is appended so external players (VLC, ffmpeg) can open and
/// seek the file like any ordinary MP4.
/// </summary>
public sealed class ClipWriter : IDisposable
{
    private const uint NominalFrame = 3000; // ~1/30s @ 90 kHz

    private readonly FileStream _file;
    private readonly VideoCodec _codec;
    private readonly int _mvhdOffset;
    private readonly int _tkhdOffset;
    private readonly int _mdhdOffset;

    private uint _sequence = 1;
    private ulong _decodeTime;
    private bool _haveTs;
    private uint _prevTs;
    private bool _waitKeyframe = true; // clips must start decodable
    private List<(byte[] Sample, bool Keyframe)>? _pending;
    private readonly List<(ulong Time, long Offset)> _syncPoints = new();

    private ClipWriter(FileStream file, VideoCodec codec, byte[] init)
    {
        _file = file;
        _codec = codec;
        var boxes = FindHeaderBoxes(init);
        _mvhdOffset = boxes.GetValueOrDefault("mvhd", -1);
        _tkhdOffset = boxes.GetValueOrDefault("tkhd", -1);
        _mdhdOffset = boxes.GetValueOrDefault("mdhd", -1);
    }

    /// <summary>Seconds of video written so far (upper bound; pending frames included at nominal rate).</summary>
    public double DurationSeconds =>
        (_decodeTime + (ulong)(_pending?.Count ?? 0) * NominalFrame) / (double)FMp4.Timescale;

    /// <summary>Creates the file and writes the init segment; null when codec params are not known yet.</summary>
    public static ClipWriter? TryCreate(string path, IStreamHub hub)
    {
        var codec = hub.Codec;
        var sps = hub.Sps;
        var pps = hub.Pps;
        if (codec == null || sps == null || pps == null)
            return null;

        var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        try
        {
            var init = FMp4.BuildInit(codec.Value, sps, pps, hub.Vps, hub.Width, hub.Height);
            file.Write(init);
            return new ClipWriter(file, codec.Value, init);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Feeds the next video packet, in hub order. <paramref name="gap"/> signals that
    /// packets were dropped since the previous one — the caller must derive it from
    /// the hub index of EVERY packet (audio included): hub indices are global, so a
    /// video-only view sees non-consecutive indices whenever audio is interleaved,
    /// and treating those as drops would silently discard all P-frames.
    /// After a real drop, writing resumes at the next keyframe.
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
            if (key)
                _syncPoints.Add((_decodeTime, _file.Position)); // moof starts here
            _file.Write(FMp4.BuildFragment(_sequence++, _decodeTime, per, sample, key));
            _decodeTime += per;
        }
        _pending = null;
    }

    /// <summary>Writes the held-back frames, patches the duration and closes the file.</summary>
    public void Dispose()
    {
        try
        {
            FlushPending((uint)(NominalFrame * (_pending?.Count ?? 1)));
            WriteMfra();
            PatchDurations();
            _file.Flush();
        }
        catch (IOException) { }
        finally
        {
            _file.Dispose();
        }
    }

    /// <summary>Backfills the total duration into the init-segment headers.</summary>
    private void PatchDurations()
    {
        if (_decodeTime == 0) return;
        // mvhd/tkhd run on the movie timescale (1000 = ms), mdhd on the media
        // timescale (90 kHz). All are version-0 boxes, i.e. 32-bit durations.
        uint ms = (uint)Math.Min(uint.MaxValue, _decodeTime * 1000 / FMp4.Timescale);
        uint ticks = (uint)Math.Min(uint.MaxValue, _decodeTime);
        if (_mvhdOffset >= 0) WriteU32At(_mvhdOffset + 24, ms);
        if (_tkhdOffset >= 0) WriteU32At(_tkhdOffset + 28, ms);
        if (_mdhdOffset >= 0) WriteU32At(_mdhdOffset + 24, ticks);
        _file.Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// Appends the mfra box: one tfra entry per keyframe (media time → moof file
    /// offset) plus the mfro trailer players use to find the index from the file end.
    /// </summary>
    private void WriteMfra()
    {
        if (_syncPoints.Count == 0) return;
        int tfraSize = 24 + _syncPoints.Count * 19;
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
        BinaryPrimitives.WriteUInt32BigEndian(t[20..], (uint)_syncPoints.Count);
        int p = 24;
        foreach (var (time, offset) in _syncPoints)
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

        _file.Write(buf);
    }

    private void WriteU32At(long offset, uint value)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        _file.Seek(offset, SeekOrigin.Begin);
        _file.Write(buf);
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
