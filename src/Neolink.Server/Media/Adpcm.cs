// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
using System.Buffers.Binary;

namespace Neolink.Media;

/// <summary>
/// DVI-4 / IMA ADPCM decoder (port of the Rust implementation).
/// Each block starts with a 4-byte DVI block header: i16 predictor, u8 step index, u8 reserved.
/// </summary>
public static class Adpcm
{
    internal static readonly int[] Steps =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50,
        55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279,
        307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282,
        1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818,
        18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    internal static readonly int[] Changes = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
    internal const int MaxStepIndex = 88;
    internal const int MaxSample = 32768;

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

/// <summary>
/// Stateful DVI-4 / IMA ADPCM encoder for the outbound two-way-talk stream — the
/// inverse of <see cref="Adpcm.BlockToPcm"/>. After quantizing each sample it
/// reconstructs it exactly the way the decoder will, so both sides' predictor
/// state stays in lockstep across an arbitrarily long stream of blocks.
/// </summary>
public sealed class AdpcmEncoder
{
    private int _predictor;
    private int _stepIndex;

    /// <summary>
    /// Encodes 16-bit PCM samples into one DVI block: the 4-byte state header
    /// (i16le predictor, u8 step index, u8 reserved) followed by one 4-bit code
    /// per sample, packed two to a byte, high nibble first. The sample count
    /// must be even so the codes fill whole bytes.
    /// </summary>
    public byte[] EncodeBlock(ReadOnlySpan<short> samples)
    {
        if (samples.Length % 2 != 0)
            throw new ArgumentException("ADPCM blocks need an even sample count", nameof(samples));

        var block = new byte[4 + samples.Length / 2];
        BinaryPrimitives.WriteInt16LittleEndian(block, (short)_predictor);
        block[2] = (byte)_stepIndex;

        for (int i = 0; i < samples.Length; i++)
        {
            int nibble = EncodeSample(samples[i]);
            if ((i & 1) == 0) block[4 + i / 2] = (byte)(nibble << 4);
            else block[4 + i / 2] |= (byte)nibble;
        }
        return block;
    }

    private int EncodeSample(int sample)
    {
        int step = Adpcm.Steps[_stepIndex];
        int delta = sample - _predictor;
        int nibble = 0;
        if (delta < 0)
        {
            nibble = 0b1000;
            delta = -delta;
        }
        if (delta >= step) { nibble |= 0b0100; delta -= step; }
        if (delta >= step >> 1) { nibble |= 0b0010; delta -= step >> 1; }
        if (delta >= step >> 2) { nibble |= 0b0001; delta -= step >> 2; }

        // Reconstruct with the decoder's own arithmetic (including its clamping)
        // so the predicted value here equals the decoded value there.
        int diff = step >> 3;
        if ((nibble & 0b0100) != 0) diff += step;
        if ((nibble & 0b0010) != 0) diff += step >> 1;
        if ((nibble & 0b0001) != 0) diff += step >> 2;
        int raw = (nibble & 0b1000) != 0 ? _predictor - diff : _predictor + diff;
        _predictor = Math.Clamp(raw, -Adpcm.MaxSample, Adpcm.MaxSample - 1);
        _stepIndex = Math.Clamp(_stepIndex + Adpcm.Changes[nibble], 0, Adpcm.MaxStepIndex);
        return nibble;
    }
}

/// <summary>
/// Serializes ADPCM blocks into BcMedia frames for the outbound talk stream — the
/// mirror image of MediaFrameReader's ADPCM parsing. Layout (little-endian, verified
/// against a real camera capture): u32 magic 0x62773130 "01wb", u16 payload size,
/// u16 payload size repeated, u16 sub-magic 0x0100, u16 half block size, block,
/// zero padding to an 8-byte boundary. The payload size counts the 4-byte
/// sub-header plus the block; the half-block field is the block length / 2.
/// </summary>
public static class BcMediaAdpcm
{
    public const uint Magic = 0x62773130; // "01wb"

    public static byte[] Serialize(ReadOnlySpan<byte> block)
    {
        int payloadSize = 4 + block.Length;
        int pad = (8 - payloadSize % 8) % 8;
        var frame = new byte[8 + payloadSize + pad];
        var s = frame.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)payloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)payloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 0x0100);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], (ushort)(block.Length / 2));
        block.CopyTo(s[12..]);
        return frame;
    }
}

/// <summary>
/// The complete outbound talk-audio pipeline: 16-bit LE mono PCM chunks at an
/// arbitrary input rate in, BcMedia-framed ADPCM at the camera's talk rate out.
/// Linear resampling → fixed-size block accumulation → <see cref="AdpcmEncoder"/>
/// → <see cref="BcMediaAdpcm"/>. Stateful across chunks; one instance per session.
/// </summary>
public sealed class TalkFrameEncoder
{
    private readonly AdpcmEncoder _encoder = new();
    private readonly short[] _block;
    private int _fill;

    // Streaming linear resampler: _frac is the position of the next output sample
    // between the previous input sample (_prev) and the current one, in input-
    // sample units; _step is how far it advances per output sample.
    private readonly double _step;
    private double _frac;
    private short _prev;
    private bool _primed;

    private readonly List<byte[]> _out = new();

    public TalkFrameEncoder(int inputRate, int outputRate, int samplesPerBlock)
    {
        if (inputRate <= 0 || outputRate <= 0)
            throw new ArgumentException("sample rates must be positive");
        // Blocks pack two samples per nibble byte, so keep the count even.
        _block = new short[Math.Max(2, samplesPerBlock & ~1)];
        _step = (double)inputRate / outputRate;
    }

    // Chunk boundaries are arbitrary (WebSocket fragmentation), so a chunk may
    // end mid-sample; the dangling byte is carried into the next chunk.
    private byte _carry;
    private bool _hasCarry;

    /// <summary>
    /// Feeds one PCM chunk and returns the frames it completed (possibly none).
    /// The returned list is reused by the next call — consume it before feeding again.
    /// </summary>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> pcm16le)
    {
        _out.Clear();
        int i = 0;
        if (_hasCarry && pcm16le.Length >= 1)
        {
            PushInput((short)(_carry | (pcm16le[0] << 8)));
            _hasCarry = false;
            i = 1;
        }
        for (; i + 1 < pcm16le.Length; i += 2)
            PushInput(BinaryPrimitives.ReadInt16LittleEndian(pcm16le[i..]));
        if (i < pcm16le.Length)
        {
            _carry = pcm16le[i];
            _hasCarry = true;
        }
        return _out;
    }

    private void PushInput(short cur)
    {
        if (!_primed)
        {
            _primed = true;
            _prev = cur;
            return;
        }
        while (_frac < 1.0)
        {
            Emit((short)(_prev + (cur - _prev) * _frac));
            _frac += _step;
        }
        _frac -= 1.0;
        _prev = cur;
    }

    private void Emit(short sample)
    {
        _block[_fill++] = sample;
        if (_fill == _block.Length)
        {
            _out.Add(BcMediaAdpcm.Serialize(_encoder.EncodeBlock(_block)));
            _fill = 0;
        }
    }
}
