// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Text;

namespace Neolink.Media;

/// <summary>
/// Minimal fragmented-MP4 (fMP4/CMAF-style) muxer for live H.264/H.265 video
/// plus optional AAC audio, suitable for browser Media Source Extensions.
/// Track 1 is video (timescale = the RTP video clock, 90 kHz), track 2 — when
/// the camera has AAC audio — runs on the audio sample rate. One sample per
/// fragment (lowest latency). ADPCM cameras get no MP4 audio track: browsers
/// can't play raw PCM via MSE, so their audio is RTSP-only.
/// </summary>
public static class FMp4
{
    public const uint Timescale = 90_000;
    public const uint AudioTrackId = 2;
    /// <summary>An AAC access unit is always exactly 1024 samples.</summary>
    public const uint AacSamplesPerAu = 1024;

    // ------------------------------------------------------------------ public API

    /// <summary>MSE codec string for an AAC track, e.g. mp4a.40.2 (AAC-LC).</summary>
    public static string AacCodecString(byte[] audioSpecificConfig) =>
        $"mp4a.40.{(audioSpecificConfig.Length > 0 ? audioSpecificConfig[0] >> 3 : 2)}";

    /// <summary>MIME codec string for MSE, e.g. avc1.64001F or hvc1.1.6.L153.B0.</summary>
    public static string CodecString(VideoCodec codec, byte[] sps)
    {
        if (codec == VideoCodec.H264)
            return sps.Length >= 4 ? $"avc1.{sps[1]:X2}{sps[2]:X2}{sps[3]:X2}" : "avc1.42E01F";

        var ptl = H265ProfileTierLevel(sps);
        if (ptl == null) return "hvc1.1.6.L120.B0";
        int profileIdc = ptl[0] & 0x1F;
        bool highTier = (ptl[0] & 0x20) != 0;
        // compatibility flags: 32 bits, bit-reversed, trailing zero bytes trimmed
        uint compat = BinaryPrimitives.ReadUInt32BigEndian(ptl.AsSpan(1));
        uint reversed = 0;
        for (int i = 0; i < 32; i++)
            if ((compat & (1u << (31 - i))) != 0) reversed |= 1u << i;
        int level = ptl[11];
        return $"hvc1.{profileIdc}.{reversed:X}.{(highTier ? 'H' : 'L')}{level}.B0";
    }

    /// <summary>
    /// Builds ftyp + moov (the MSE initialization segment). When
    /// <paramref name="aacConfig"/> is given, a second (AAC audio) track is
    /// declared with the audio sample rate as its timescale.
    /// </summary>
    public static byte[] BuildInit(VideoCodec codec, byte[] sps, byte[] pps, byte[]? vps, uint width, uint height,
        byte[]? aacConfig = null, int aacRate = 0, int aacChannels = 0)
    {
        var w = new Mp4Writer();
        bool audio = aacConfig != null && aacRate > 0;

        using (w.Box("ftyp"))
        {
            w.Tag("iso5");             // major brand
            w.U32(512);                // minor version
            w.Tag("iso5"); w.Tag("iso6"); w.Tag("mp41");
        }

        using (w.Box("moov"))
        {
            using (w.FullBox("mvhd", 0, 0))
            {
                w.U32(0); w.U32(0);    // creation/modification time
                w.U32(1000);           // timescale (movie)
                w.U32(0);              // duration (unknown/live)
                w.U32(0x00010000);     // rate 1.0
                w.U16(0x0100);         // volume 1.0
                w.U16(0); w.U32(0); w.U32(0);
                WriteIdentityMatrix(w);
                for (int i = 0; i < 6; i++) w.U32(0); // pre_defined
                w.U32(audio ? 3u : 2u); // next_track_ID
            }

            using (w.Box("trak"))
            {
                using (w.FullBox("tkhd", 0, 7)) // enabled | in movie | in preview
                {
                    w.U32(0); w.U32(0); // times
                    w.U32(1);           // track ID
                    w.U32(0);           // reserved
                    w.U32(0);           // duration
                    w.U32(0); w.U32(0); // reserved
                    w.U16(0); w.U16(0); // layer, alternate group
                    w.U16(0); w.U16(0); // volume (video=0), reserved
                    WriteIdentityMatrix(w);
                    w.U32(width << 16);  // fixed-point 16.16
                    w.U32(height << 16);
                }

                using (w.Box("mdia"))
                {
                    using (w.FullBox("mdhd", 0, 0))
                    {
                        w.U32(0); w.U32(0);
                        w.U32(Timescale);
                        w.U32(0);          // duration
                        w.U16(0x55C4);     // language "und"
                        w.U16(0);
                    }
                    using (w.FullBox("hdlr", 0, 0))
                    {
                        w.U32(0);
                        w.Tag("vide");
                        w.U32(0); w.U32(0); w.U32(0);
                        w.Bytes(Encoding.ASCII.GetBytes("Neolink.NET Video\0"));
                    }
                    using (w.Box("minf"))
                    {
                        using (w.FullBox("vmhd", 0, 1))
                        {
                            w.U16(0); w.U16(0); w.U16(0); w.U16(0);
                        }
                        using (w.Box("dinf"))
                        using (w.FullBox("dref", 0, 0))
                        {
                            w.U32(1);
                            using (w.FullBox("url ", 0, 1)) { } // self-contained
                        }
                        using (w.Box("stbl"))
                        {
                            using (w.FullBox("stsd", 0, 0))
                            {
                                w.U32(1);
                                WriteVisualSampleEntry(w, codec, sps, pps, vps, width, height);
                            }
                            using (w.FullBox("stts", 0, 0)) w.U32(0);
                            using (w.FullBox("stsc", 0, 0)) w.U32(0);
                            using (w.FullBox("stsz", 0, 0)) { w.U32(0); w.U32(0); }
                            using (w.FullBox("stco", 0, 0)) w.U32(0);
                        }
                    }
                }
            }

            if (audio)
                WriteAudioTrak(w, aacConfig!, aacRate, aacChannels);

            using (w.Box("mvex"))
            {
                using (w.FullBox("trex", 0, 0))
                {
                    w.U32(1);            // track ID
                    w.U32(1);            // default sample description index
                    w.U32(0);            // default sample duration
                    w.U32(0);            // default sample size
                    w.U32(0x01010000);   // default flags: non-sync sample
                }
                if (audio)
                {
                    using (w.FullBox("trex", 0, 0))
                    {
                        w.U32(AudioTrackId);
                        w.U32(1);
                        w.U32(0);
                        w.U32(0);
                        w.U32(0x02000000); // default flags: sync sample (all AAC AUs are)
                    }
                }
            }
        }

        return w.ToArray();
    }

    /// <summary>Builds one moof + mdat fragment containing a single sample (video or audio).</summary>
    public static byte[] BuildFragment(uint sequence, ulong decodeTime, uint duration, byte[] sample, bool keyframe,
        uint trackId = 1)
    {
        var w = new Mp4Writer();
        int trunDataOffsetPos;

        using (w.Box("moof"))
        {
            using (w.FullBox("mfhd", 0, 0))
                w.U32(sequence);

            using (w.Box("traf"))
            {
                using (w.FullBox("tfhd", 0, 0x020000)) // default-base-is-moof
                    w.U32(trackId);
                using (w.FullBox("tfdt", 1, 0))
                    w.U64(decodeTime);
                // trun flags: data-offset | first-sample-flags | sample-duration | sample-size
                using (w.FullBox("trun", 0, 0x000305))
                {
                    w.U32(1);                          // sample count
                    trunDataOffsetPos = w.Position;
                    w.U32(0);                          // data_offset (patched below)
                    w.U32(keyframe ? 0x02000000u : 0x01010000u); // first sample flags
                    w.U32(duration);
                    w.U32((uint)sample.Length);
                }
            }
        }

        // The sample data starts right after moof + mdat header
        int moofSize = w.Position;
        w.PatchU32(trunDataOffsetPos, (uint)(moofSize + 8));

        using (w.Box("mdat"))
            w.Bytes(sample);

        return w.ToArray();
    }

    /// <summary>
    /// Splits an Annex-B buffer into individual access units (one per video frame),
    /// each as length-prefixed sample data with parameter sets/AUDs stripped.
    /// Cameras may deliver whole GOPs in one buffer; MSE requires one frame per sample.
    /// </summary>
    public static List<(byte[] Sample, bool Keyframe)> SplitAccessUnits(VideoCodec codec, byte[] annexB)
    {
        var result = new List<(byte[], bool)>();
        var current = new List<ReadOnlyMemory<byte>>();
        bool currentHasVcl = false, currentKey = false;
        int currentBytes = 0;

        void Close()
        {
            if (currentBytes == 0) return;
            var sample = new byte[currentBytes];
            int pos = 0;
            foreach (var nal in current)
            {
                BinaryPrimitives.WriteUInt32BigEndian(sample.AsSpan(pos), (uint)nal.Length);
                nal.Span.CopyTo(sample.AsSpan(pos + 4));
                pos += 4 + nal.Length;
            }
            result.Add((sample, currentKey));
            current.Clear();
            currentHasVcl = false; currentKey = false; currentBytes = 0;
        }

        foreach (var nal in H26x.SplitNals(annexB))
        {
            var span = nal.Span;
            if (span.Length < 2) continue;

            bool isVcl, isKey, firstSlice, skip;
            if (codec == VideoCodec.H264)
            {
                int t = H26x.H264NalType(span);
                skip = t is H26x.H264Sps or H26x.H264Pps or 9;
                isVcl = t is >= 1 and <= 5;
                isKey = t == H26x.H264Idr;
                // first_mb_in_slice == 0 -> ue(v) leading bit set
                firstSlice = isVcl && span.Length > 1 && (span[1] & 0x80) != 0;
            }
            else
            {
                int t = H26x.H265NalType(span);
                skip = t is H26x.H265Vps or H26x.H265Sps or H26x.H265Pps or 35;
                isVcl = t <= 31;
                isKey = t is >= 16 and <= 23; // IRAP
                // first_slice_segment_in_pic_flag: first bit after the 2-byte header
                firstSlice = isVcl && span.Length > 2 && (span[2] & 0x80) != 0;
            }
            if (skip) continue;

            // A VCL NAL starting a new picture closes the previous access unit
            if (isVcl && firstSlice && currentHasVcl)
                Close();

            current.Add(nal);
            currentBytes += 4 + nal.Length;
            currentHasVcl |= isVcl;
            currentKey |= isKey;
        }
        Close();
        return result;
    }

    /// <summary>
    /// Converts an Annex-B access unit into length-prefixed (AVCC/HVCC) sample data,
    /// dropping parameter sets and AUDs (they live in the init segment).
    /// </summary>
    public static byte[] AnnexBToSample(VideoCodec codec, byte[] annexB)
    {
        var nals = H26x.SplitNals(annexB);
        int total = 0;
        var keep = new List<ReadOnlyMemory<byte>>(nals.Count);
        foreach (var nal in nals)
        {
            var span = nal.Span;
            if (span.Length == 0) continue;
            if (codec == VideoCodec.H264)
            {
                int t = H26x.H264NalType(span);
                if (t is H26x.H264Sps or H26x.H264Pps or 9 /*AUD*/) continue;
            }
            else
            {
                int t = H26x.H265NalType(span);
                if (t is H26x.H265Vps or H26x.H265Sps or H26x.H265Pps or 35 /*AUD*/) continue;
            }
            keep.Add(nal);
            total += 4 + nal.Length;
        }

        var result = new byte[total];
        int pos = 0;
        foreach (var nal in keep)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), (uint)nal.Length);
            nal.Span.CopyTo(result.AsSpan(pos + 4));
            pos += 4 + nal.Length;
        }
        return result;
    }

    // ------------------------------------------------------------------ audio track

    private static void WriteAudioTrak(Mp4Writer w, byte[] asc, int rate, int channels)
    {
        using (w.Box("trak"))
        {
            using (w.FullBox("tkhd", 0, 7)) // enabled | in movie | in preview
            {
                w.U32(0); w.U32(0);   // times
                w.U32(AudioTrackId);
                w.U32(0);             // reserved
                w.U32(0);             // duration
                w.U32(0); w.U32(0);   // reserved
                w.U16(0); w.U16(0);   // layer, alternate group
                w.U16(0x0100);        // volume 1.0 (audio track)
                w.U16(0);             // reserved
                WriteIdentityMatrix(w);
                w.U32(0); w.U32(0);   // width/height (audio = 0)
            }

            using (w.Box("mdia"))
            {
                using (w.FullBox("mdhd", 0, 0))
                {
                    w.U32(0); w.U32(0);
                    w.U32((uint)rate);  // timescale = sample rate
                    w.U32(0);           // duration
                    w.U16(0x55C4);      // language "und"
                    w.U16(0);
                }
                using (w.FullBox("hdlr", 0, 0))
                {
                    w.U32(0);
                    w.Tag("soun");
                    w.U32(0); w.U32(0); w.U32(0);
                    w.Bytes(Encoding.ASCII.GetBytes("Neolink.NET Audio\0"));
                }
                using (w.Box("minf"))
                {
                    using (w.FullBox("smhd", 0, 0))
                    {
                        w.U16(0); // balance
                        w.U16(0);
                    }
                    using (w.Box("dinf"))
                    using (w.FullBox("dref", 0, 0))
                    {
                        w.U32(1);
                        using (w.FullBox("url ", 0, 1)) { } // self-contained
                    }
                    using (w.Box("stbl"))
                    {
                        using (w.FullBox("stsd", 0, 0))
                        {
                            w.U32(1);
                            WriteAudioSampleEntry(w, asc, rate, channels);
                        }
                        using (w.FullBox("stts", 0, 0)) w.U32(0);
                        using (w.FullBox("stsc", 0, 0)) w.U32(0);
                        using (w.FullBox("stsz", 0, 0)) { w.U32(0); w.U32(0); }
                        using (w.FullBox("stco", 0, 0)) w.U32(0);
                    }
                }
            }
        }
    }

    private static void WriteAudioSampleEntry(Mp4Writer w, byte[] asc, int rate, int channels)
    {
        using (w.Box("mp4a"))
        {
            for (int i = 0; i < 6; i++) w.U8(0);   // reserved
            w.U16(1);                              // data reference index
            w.U32(0); w.U32(0);                    // reserved
            w.U16((ushort)Math.Max(channels, 1));
            w.U16(16);                             // sample size (bits)
            w.U16(0); w.U16(0);                    // pre_defined, reserved
            w.U32((uint)rate << 16);               // sample rate, 16.16 fixed point

            // esds: the MPEG-4 descriptor chain that carries the
            // AudioSpecificConfig — decoders refuse mp4a without it.
            using (w.FullBox("esds", 0, 0))
            {
                int dsiLen = asc.Length;
                int dcdLen = 13 + 2 + dsiLen;      // DecoderConfig + nested DSI
                int esLen = 3 + 2 + dcdLen + 3;    // ES header + DCD + SLConfig

                w.U8(0x03);                        // ES_Descriptor
                w.U8((byte)esLen);
                w.U16((ushort)AudioTrackId);       // ES_ID
                w.U8(0);                           // flags

                w.U8(0x04);                        // DecoderConfigDescriptor
                w.U8((byte)dcdLen);
                w.U8(0x40);                        // objectTypeIndication: MPEG-4 Audio
                w.U8(0x15);                        // streamType audio (0x05<<2 | 1)
                w.U8(0); w.U16(0);                 // bufferSizeDB (24-bit)
                w.U32(0);                          // maxBitrate (unknown)
                w.U32(0);                          // avgBitrate (unknown)

                w.U8(0x05);                        // DecoderSpecificInfo
                w.U8((byte)dsiLen);
                w.Bytes(asc);

                w.U8(0x06);                        // SLConfigDescriptor
                w.U8(1);
                w.U8(0x02);                        // MP4 predefined
            }
        }
    }

    // ------------------------------------------------------------------ codec boxes

    private static void WriteVisualSampleEntry(Mp4Writer w, VideoCodec codec, byte[] sps, byte[] pps, byte[]? vps, uint width, uint height)
    {
        using (w.Box(codec == VideoCodec.H264 ? "avc1" : "hvc1"))
        {
            for (int i = 0; i < 6; i++) w.U8(0); // reserved
            w.U16(1);                            // data reference index
            w.U16(0); w.U16(0);                  // pre_defined, reserved
            w.U32(0); w.U32(0); w.U32(0);        // pre_defined
            w.U16((ushort)width);
            w.U16((ushort)height);
            w.U32(0x00480000);                   // horiz dpi 72
            w.U32(0x00480000);                   // vert dpi 72
            w.U32(0);                            // reserved
            w.U16(1);                            // frame count
            for (int i = 0; i < 32; i++) w.U8(0); // compressor name
            w.U16(0x0018);                       // depth
            w.U16(0xFFFF);                       // pre_defined = -1

            if (codec == VideoCodec.H264)
            {
                using (w.Box("avcC"))
                {
                    w.U8(1);            // configuration version
                    w.U8(sps[1]); w.U8(sps[2]); w.U8(sps[3]); // profile / compat / level
                    w.U8(0xFF);         // 4-byte NAL lengths
                    w.U8(0xE1);         // 1 SPS
                    w.U16((ushort)sps.Length); w.Bytes(sps);
                    w.U8(1);            // 1 PPS
                    w.U16((ushort)pps.Length); w.Bytes(pps);
                }
            }
            else
            {
                using (w.Box("hvcC"))
                {
                    var ptl = H265ProfileTierLevel(sps) ?? new byte[12];
                    w.U8(1);                          // configuration version
                    w.Bytes(ptl);                     // general profile/tier/level (12 bytes)
                    w.U16(0xF000);                    // min spatial segmentation
                    w.U8(0xFC);                       // parallelism type
                    w.U8(0xFC | 1);                   // chroma format 4:2:0
                    w.U8(0xF8);                       // bit depth luma - 8
                    w.U8(0xF8);                       // bit depth chroma - 8
                    w.U16(0);                         // avg frame rate
                    w.U8((1 << 3) | (1 << 2) | 3);    // numTemporalLayers=1, temporalIdNested, 4-byte lengths
                    var arrays = new List<(byte type, byte[] nal)>();
                    if (vps != null) arrays.Add((32, vps));
                    arrays.Add((33, sps));
                    arrays.Add((34, pps));
                    w.U8((byte)arrays.Count);
                    foreach (var (type, nal) in arrays)
                    {
                        w.U8((byte)(0x80 | type));    // array_completeness=1
                        w.U16(1);                     // NAL count
                        w.U16((ushort)nal.Length);
                        w.Bytes(nal);
                    }
                }
            }
        }
    }

    /// <summary>Extracts the 12-byte general_profile_tier_level from an H.265 SPS.</summary>
    internal static byte[]? H265ProfileTierLevel(byte[] sps)
    {
        // Strip emulation prevention bytes from the RBSP (skip the 2-byte NAL header)
        var rbsp = new List<byte>(sps.Length);
        int zeros = 0;
        for (int i = 2; i < sps.Length; i++)
        {
            byte b = sps[i];
            if (zeros == 2 && b == 3) { zeros = 0; continue; }
            zeros = b == 0 ? zeros + 1 : 0;
            rbsp.Add(b);
        }
        // rbsp[0]: sps_video_parameter_set_id(4) + max_sub_layers(3) + nesting(1),
        // then profile_tier_level: 12 bytes
        if (rbsp.Count < 13) return null;
        return rbsp.GetRange(1, 12).ToArray();
    }

    private static void WriteIdentityMatrix(Mp4Writer w)
    {
        w.U32(0x00010000); w.U32(0); w.U32(0);
        w.U32(0); w.U32(0x00010000); w.U32(0);
        w.U32(0); w.U32(0); w.U32(0x40000000);
    }
}

/// <summary>Big-endian MP4 box writer with automatic size back-patching.</summary>
public sealed class Mp4Writer
{
    private byte[] _buf = new byte[4096];
    private int _len;

    public int Position => _len;
    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int size = _buf.Length;
        while (size < _len + extra) size *= 2;
        Array.Resize(ref _buf, size);
    }

    public void U8(byte v) { Ensure(1); _buf[_len++] = v; }
    public void U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(_len), v); _len += 2; }
    public void U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(_len), v); _len += 4; }
    public void U64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64BigEndian(_buf.AsSpan(_len), v); _len += 8; }
    public void Bytes(ReadOnlySpan<byte> data) { Ensure(data.Length); data.CopyTo(_buf.AsSpan(_len)); _len += data.Length; }
    public void Tag(string fourcc) { Bytes(Encoding.ASCII.GetBytes(fourcc)); }

    public void PatchU32(int position, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(position), value);

    public BoxScope Box(string type)
    {
        int start = _len;
        U32(0); // size placeholder
        Tag(type);
        return new BoxScope(this, start);
    }

    public BoxScope FullBox(string type, byte version, uint flags)
    {
        var scope = Box(type);
        U32(((uint)version << 24) | (flags & 0xFFFFFF));
        return scope;
    }

    public readonly struct BoxScope : IDisposable
    {
        private readonly Mp4Writer _w;
        private readonly int _start;
        public BoxScope(Mp4Writer w, int start) { _w = w; _start = start; }
        public void Dispose() => _w.PatchU32(_start, (uint)(_w._len - _start));
    }
}
