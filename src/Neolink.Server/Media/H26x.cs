namespace Neolink.Media;

/// <summary>Utilities for H.264/H.265 Annex-B elementary streams.</summary>
public static class H26x
{
    /// <summary>Splits an Annex-B buffer into NAL units (without start codes).</summary>
    public static List<ReadOnlyMemory<byte>> SplitNals(ReadOnlyMemory<byte> annexB)
    {
        var result = new List<ReadOnlyMemory<byte>>();
        var span = annexB.Span;
        int i = 0;
        int nalStart = -1;

        while (i + 2 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0 && (span[i + 2] == 1 || (i + 3 < span.Length && span[i + 2] == 0 && span[i + 3] == 1)))
            {
                int scLen = span[i + 2] == 1 ? 3 : 4;
                if (nalStart >= 0)
                {
                    int end = i;
                    // Trim trailing zero bytes belonging to the next start code prefix
                    result.Add(annexB[nalStart..end]);
                }
                nalStart = i + scLen;
                i += scLen;
            }
            else
            {
                i++;
            }
        }
        if (nalStart >= 0 && nalStart < annexB.Length)
            result.Add(annexB[nalStart..]);

        // Remove trailing zero padding from each NAL
        for (int k = 0; k < result.Count; k++)
        {
            var nal = result[k];
            var s = nal.Span;
            int len = nal.Length;
            while (len > 0 && s[len - 1] == 0) len--;
            // Keep at least 1 byte; some encoders legitimately end with 0x00 in cabac? (rare) — trailing
            // zeros between NALs are far more common, so trim is the right default.
            if (len > 0 && len != nal.Length) result[k] = nal[..len];
        }
        return result;
    }

    public static int H264NalType(ReadOnlySpan<byte> nal) => nal.Length > 0 ? nal[0] & 0x1F : -1;
    public static int H265NalType(ReadOnlySpan<byte> nal) => nal.Length > 0 ? (nal[0] >> 1) & 0x3F : -1;

    public const int H264Sps = 7;
    public const int H264Pps = 8;
    public const int H264Idr = 5;

    public const int H265Vps = 32;
    public const int H265Sps = 33;
    public const int H265Pps = 34;

    // ------------------------------------------------------------------ SPS dimensions

    /// <summary>
    /// Parses the coded picture size out of an SPS NAL. Needed for sources that
    /// carry no side-channel resolution (generic RTSP pulls) — MSE refuses a
    /// 0×0 decoder config. Returns false on anything it can't follow.
    /// </summary>
    public static bool TryGetDimensions(VideoCodec codec, ReadOnlySpan<byte> sps, out uint width, out uint height)
    {
        width = height = 0;
        try
        {
            return codec == VideoCodec.H264
                ? ParseH264Sps(sps, out width, out height)
                : ParseH265Sps(sps, out width, out height);
        }
        catch
        {
            return false; // malformed/truncated SPS: report no dimensions
        }
    }

    private static bool ParseH264Sps(ReadOnlySpan<byte> sps, out uint width, out uint height)
    {
        width = height = 0;
        var r = new BitReader(sps, skipBytes: 1); // NAL header
        uint profile = r.Bits(8);
        r.Bits(8);  // constraint flags + reserved
        r.Bits(8);  // level_idc
        r.Ue();     // seq_parameter_set_id

        uint chromaFormat = 1; // 4:2:0 unless the high profiles say otherwise
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = r.Ue();
            if (chromaFormat == 3) r.Bits(1); // separate_colour_plane_flag
            r.Ue(); // bit_depth_luma_minus8
            r.Ue(); // bit_depth_chroma_minus8
            r.Bits(1); // qpprime_y_zero_transform_bypass_flag
            if (r.Bits(1) == 1) // seq_scaling_matrix_present_flag
            {
                int lists = chromaFormat == 3 ? 12 : 8;
                for (int i = 0; i < lists; i++)
                {
                    if (r.Bits(1) == 0) continue; // seq_scaling_list_present_flag[i]
                    int size = i < 6 ? 16 : 64;
                    int lastScale = 8, nextScale = 8;
                    for (int j = 0; j < size; j++)
                    {
                        if (nextScale != 0)
                            nextScale = (lastScale + (int)r.Se() + 256) % 256;
                        lastScale = nextScale == 0 ? lastScale : nextScale;
                    }
                }
            }
        }

        r.Ue(); // log2_max_frame_num_minus4
        uint pocType = r.Ue();
        if (pocType == 0)
        {
            r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (pocType == 1)
        {
            r.Bits(1); // delta_pic_order_always_zero_flag
            r.Se(); r.Se();
            uint cycles = r.Ue();
            for (uint i = 0; i < cycles; i++) r.Se();
        }
        r.Ue();    // max_num_ref_frames
        r.Bits(1); // gaps_in_frame_num_value_allowed_flag

        uint widthMbs = r.Ue() + 1;
        uint heightMapUnits = r.Ue() + 1;
        uint frameMbsOnly = r.Bits(1);
        if (frameMbsOnly == 0) r.Bits(1); // mb_adaptive_frame_field_flag
        r.Bits(1); // direct_8x8_inference_flag

        width = widthMbs * 16;
        height = (2 - frameMbsOnly) * heightMapUnits * 16;

        if (r.Bits(1) == 1) // frame_cropping_flag
        {
            uint cropL = r.Ue(), cropR = r.Ue(), cropT = r.Ue(), cropB = r.Ue();
            uint unitX = chromaFormat is 1 or 2 ? 2u : 1u;
            uint unitY = (chromaFormat == 1 ? 2u : 1u) * (2 - frameMbsOnly);
            width -= (cropL + cropR) * unitX;
            height -= (cropT + cropB) * unitY;
        }
        return width > 0 && height > 0;
    }

    private static bool ParseH265Sps(ReadOnlySpan<byte> sps, out uint width, out uint height)
    {
        width = height = 0;
        var r = new BitReader(sps, skipBytes: 2); // 2-byte NAL header
        r.Bits(4); // sps_video_parameter_set_id
        uint maxSubLayers = r.Bits(3);
        r.Bits(1); // sps_temporal_id_nesting_flag

        // profile_tier_level: 12 bytes of general PTL...
        r.Bits(8); r.Bits(32); r.Bits(32); r.Bits(24); r.Bits(8);
        // ...plus per-sub-layer presence flags and their PTL blocks.
        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (int i = 0; i < maxSubLayers; i++)
        {
            profilePresent[i] = r.Bits(1) == 1;
            levelPresent[i] = r.Bits(1) == 1;
        }
        if (maxSubLayers > 0)
            for (uint i = maxSubLayers; i < 8; i++) r.Bits(2); // reserved alignment
        for (int i = 0; i < maxSubLayers; i++)
        {
            if (profilePresent[i]) { r.Bits(32); r.Bits(32); r.Bits(24); }
            if (levelPresent[i]) r.Bits(8);
        }

        r.Ue(); // sps_seq_parameter_set_id
        uint chromaFormat = r.Ue();
        if (chromaFormat == 3) r.Bits(1); // separate_colour_plane_flag
        width = r.Ue();   // pic_width_in_luma_samples
        height = r.Ue();  // pic_height_in_luma_samples
        if (r.Bits(1) == 1) // conformance_window_flag
        {
            uint winL = r.Ue(), winR = r.Ue(), winT = r.Ue(), winB = r.Ue();
            uint subW = chromaFormat is 1 or 2 ? 2u : 1u;
            uint subH = chromaFormat == 1 ? 2u : 1u;
            width -= (winL + winR) * subW;
            height -= (winT + winB) * subH;
        }
        return width > 0 && height > 0;
    }

    /// <summary>MSB-first bit reader over RBSP: strips 00 00 03 emulation prevention on the fly.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _byte;
        private int _bit;
        private int _zeros; // consecutive 0x00 bytes seen (for emulation prevention)

        public BitReader(ReadOnlySpan<byte> data, int skipBytes)
        {
            _data = data;
            _byte = skipBytes;
        }

        private byte Current
        {
            get
            {
                var b = _data[_byte];
                return b;
            }
        }

        public uint Bits(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                if (_byte >= _data.Length) throw new IndexOutOfRangeException();
                value = (value << 1) | (uint)((Current >> (7 - _bit)) & 1);
                if (++_bit == 8)
                {
                    _zeros = Current == 0 ? _zeros + 1 : 0;
                    _bit = 0;
                    _byte++;
                    // 00 00 03: the 0x03 is an escape byte, not stream data.
                    if (_zeros >= 2 && _byte < _data.Length && _data[_byte] == 3)
                    {
                        _byte++;
                        _zeros = 0;
                    }
                }
            }
            return value;
        }

        /// <summary>Unsigned exp-Golomb.</summary>
        public uint Ue()
        {
            int zeros = 0;
            while (Bits(1) == 0)
            {
                if (++zeros > 31) throw new InvalidDataException("bad exp-Golomb");
            }
            return (1u << zeros) - 1 + (zeros > 0 ? Bits(zeros) : 0);
        }

        /// <summary>Signed exp-Golomb.</summary>
        public long Se()
        {
            uint v = Ue();
            return (v & 1) == 1 ? (v + 1) / 2 : -(long)(v / 2);
        }
    }
}
