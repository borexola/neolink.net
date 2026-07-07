// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
namespace Neolink.Media;

/// <summary>
/// DVI-4 / IMA ADPCM decoder (port of the Rust implementation).
/// Each block starts with a 4-byte DVI block header: i16 predictor, u8 step index, u8 reserved.
/// </summary>
public static class Adpcm
{
    private static readonly int[] Steps =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50,
        55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279,
        307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282,
        1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818,
        18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    private static readonly int[] Changes = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
    private const int MaxStepIndex = 88;
    private const int MaxSample = 32768;

    /// <summary>
    /// Decodes one ADPCM block (with its 4-byte predictor-state header) to 16-bit LE PCM.
    /// </summary>
    public static byte[] BlockToPcm(ReadOnlySpan<byte> block)
    {
        if (block.Length < 4)
            throw new InvalidDataException("ADPCM block too short for its header");

        int lastOutput = BitConverter.ToInt16(block[..2]);
        int stepIndex = block[2]; // u8 step index (u16le with reserved byte 0)

        var data = block[4..];
        var pcm = new byte[data.Length * 4]; // 2 samples per byte, 2 bytes per sample
        int o = 0;

        foreach (var b in data)
        {
            for (int half = 0; half < 2; half++)
            {
                int nibble = half == 0 ? (b & 0xF0) >> 4 : b & 0x0F;
                stepIndex = Math.Clamp(stepIndex, 0, MaxStepIndex);
                int step = Steps[stepIndex];

                int diff = step >> 3;
                if ((nibble & 0b0100) != 0) diff += step;
                if ((nibble & 0b0010) != 0) diff += step >> 1;
                if ((nibble & 0b0001) != 0) diff += step >> 2;

                int raw = (nibble & 0b1000) != 0 ? lastOutput - diff : lastOutput + diff;
                int sample = Math.Clamp(raw, -MaxSample, MaxSample - 1);

                short scaled = (short)(sample * short.MaxValue / (MaxSample - 1));
                pcm[o++] = (byte)(scaled & 0xff);
                pcm[o++] = (byte)((scaled >> 8) & 0xff);

                stepIndex += Changes[nibble];
                lastOutput = sample;
            }
        }
        return pcm;
    }
}
