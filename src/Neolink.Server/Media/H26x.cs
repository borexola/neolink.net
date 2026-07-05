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
}
