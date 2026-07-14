// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Media;

/// <summary>
/// ITU-T G.711 companding: µ-law (PCMU) and A-law (PCMA) to 16-bit linear PCM.
/// These are the payloads carried on an RTSP audio backchannel (RTP payload
/// types 0 and 8), so only the decode direction is needed here.
/// </summary>
public static class G711
{
    /// <summary>Decodes one µ-law byte (RTP payload type 0) to a 16-bit sample.</summary>
    public static short MuLawToLinear(byte u)
    {
        u = (byte)~u;
        int t = ((u & 0x0F) << 3) + 0x84; // mantissa + bias
        t <<= (u & 0x70) >> 4;            // exponent
        return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
    }

    /// <summary>Decodes one A-law byte (RTP payload type 8) to a 16-bit sample.</summary>
    public static short ALawToLinear(byte a)
    {
        a ^= 0x55;
        int t = (a & 0x0F) << 4;
        int seg = (a & 0x70) >> 4;
        t = seg switch
        {
            0 => t + 8,
            1 => t + 0x108,
            _ => (t + 0x108) << (seg - 1),
        };
        return (short)((a & 0x80) != 0 ? t : -t);
    }

    /// <summary>Decodes a buffer of G.711 codes to 16-bit LE PCM bytes.</summary>
    public static byte[] ToPcm16(ReadOnlySpan<byte> codes, bool aLaw)
    {
        var pcm = new byte[codes.Length * 2];
        for (int i = 0; i < codes.Length; i++)
        {
            short s = aLaw ? ALawToLinear(codes[i]) : MuLawToLinear(codes[i]);
            pcm[2 * i] = (byte)s;
            pcm[2 * i + 1] = (byte)(s >> 8);
        }
        return pcm;
    }
}
