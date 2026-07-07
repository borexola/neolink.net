// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Media;

/// <summary>A single raw AAC frame extracted from an ADTS stream.</summary>
public sealed record AacAccessUnit(byte[] Data, int SampleRate, int Channels, byte[] AudioSpecificConfig);

/// <summary>Minimal ADTS (Audio Data Transport Stream) parser for AAC.</summary>
public static class Adts
{
    private static readonly int[] SampleRates =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    };

    /// <summary>
    /// Splits a buffer of one or more ADTS frames into raw AAC access units.
    /// Returns an empty list if the data doesn't look like ADTS.
    /// </summary>
    public static List<AacAccessUnit> Split(ReadOnlySpan<byte> data)
    {
        var result = new List<AacAccessUnit>();
        int i = 0;
        while (i + 7 <= data.Length)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xF0) != 0xF0)
            {
                i++; // hunt for syncword
                continue;
            }
            bool crcAbsent = (data[i + 1] & 0x01) != 0;
            int profile = (data[i + 2] >> 6) & 0x03;           // 0=Main,1=LC,2=SSR
            int freqIdx = (data[i + 2] >> 2) & 0x0F;
            int chanCfg = ((data[i + 2] & 0x01) << 2) | ((data[i + 3] >> 6) & 0x03);
            int frameLen = ((data[i + 3] & 0x03) << 11) | (data[i + 4] << 3) | ((data[i + 5] >> 5) & 0x07);

            if (frameLen < 7 || i + frameLen > data.Length)
                break;

            int headerLen = crcAbsent ? 7 : 9;
            if (frameLen <= headerLen) { i += frameLen; continue; }

            var payload = data.Slice(i + headerLen, frameLen - headerLen).ToArray();
            int sampleRate = SampleRates[freqIdx];
            int audioObjectType = profile + 1;
            var asc = new byte[2];
            asc[0] = (byte)((audioObjectType << 3) | (freqIdx >> 1));
            asc[1] = (byte)(((freqIdx & 1) << 7) | (chanCfg << 3));
            result.Add(new AacAccessUnit(payload, sampleRate, Math.Max(chanCfg, 1), asc));
            i += frameLen;
        }
        return result;
    }
}
