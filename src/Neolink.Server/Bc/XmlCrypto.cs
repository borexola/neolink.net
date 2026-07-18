// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
using System.Security.Cryptography;

namespace Neolink.Bc;

/// <summary>
/// Payload encryption/decryption for BC messages.
///
/// BCEncrypt: byte-wise XOR with a rotating 8 byte key and the channel offset.
/// AES: AES-128-CFB (128-bit feedback segments) with a fixed IV and a key derived
/// from the camera password + login nonce. Implemented manually on top of AES-ECB
/// because .NET's CFB mode requires block-aligned input, while BC payloads are
/// arbitrary length.
/// </summary>
public static class XmlCrypto
{
    private static readonly byte[] XmlKey = { 0x1F, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78, 0xFF };
    private static readonly byte[] Iv = "0123456789abcdef"u8.ToArray();

    public static byte[] Decrypt(uint offset, ReadOnlySpan<byte> buf, EncryptionState enc)
    {
        var (kind, key) = enc.Snapshot();
        return kind switch
        {
            EncryptionKind.Unencrypted => buf.ToArray(),
            EncryptionKind.BcEncrypt => BcXor(offset, buf),
            EncryptionKind.Aes or EncryptionKind.FullAes when key != null => AesCfbDecrypt(buf, enc),
            // AES negotiated but key not yet derived (login phase): BCEncrypt is used.
            EncryptionKind.Aes or EncryptionKind.FullAes => BcXor(offset, buf),
            _ => throw new InvalidOperationException("unknown encryption"),
        };
    }

    public static byte[] Encrypt(uint offset, ReadOnlySpan<byte> buf, EncryptionState enc)
    {
        var (kind, key) = enc.Snapshot();
        return kind switch
        {
            EncryptionKind.Unencrypted => buf.ToArray(),
            EncryptionKind.BcEncrypt => BcXor(offset, buf), // XOR is symmetric
            EncryptionKind.Aes or EncryptionKind.FullAes when key != null => AesCfbEncrypt(buf, enc),
            EncryptionKind.Aes or EncryptionKind.FullAes => BcXor(offset, buf),
            _ => throw new InvalidOperationException("unknown encryption"),
        };
    }

    /// <summary>
    /// Fast CFB decrypt. Under FullAes this runs on EVERY video byte, so it is the
    /// protocol layer's hot path. CFB decryption has no serial dependency: the
    /// keystream for block i is AES-ECB(previous CIPHERTEXT block), and all the
    /// ciphertext is already in hand — so the whole keystream is produced with one
    /// ECB pass over (IV ++ ciphertext) using the connection's cached, keyed AES,
    /// then XORed out word-wide. The per-16-byte TransformBlock loop this replaces
    /// cost 15-20% of a core per camera on FullAes firmwares.
    /// </summary>
    internal static byte[] AesCfbDecrypt(ReadOnlySpan<byte> input, EncryptionState enc)
    {
        if (input.Length == 0) return Array.Empty<byte>();
        int blocks = (input.Length + 15) / 16;

        // Keystream input: IV, then every ciphertext block that feeds a successor.
        var ksIn = new byte[blocks * 16];
        Iv.CopyTo(ksIn, 0);
        input[..((blocks - 1) * 16)].CopyTo(ksIn.AsSpan(16));

        var keystream = new byte[blocks * 16];
        enc.EcbEncrypt(ksIn, keystream);

        var output = new byte[input.Length];
        XorInto(input, keystream, output);
        return output;
    }

    /// <summary>
    /// CFB encrypt via the connection's cached AES. Encryption IS serial (each
    /// block's keystream needs the previous ciphertext block), but the client only
    /// ever encrypts small outgoing XML commands, so block-at-a-time is fine —
    /// the win here is just not rebuilding the AES key schedule per message.
    /// </summary>
    internal static byte[] AesCfbEncrypt(ReadOnlySpan<byte> input, EncryptionState enc)
    {
        if (input.Length == 0) return Array.Empty<byte>();
        var output = new byte[input.Length];
        Span<byte> feedback = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        Iv.CopyTo(feedback);
        for (int pos = 0; pos < input.Length; pos += 16)
        {
            enc.EcbEncrypt(feedback, keystream);
            int n = Math.Min(16, input.Length - pos);
            for (int i = 0; i < n; i++)
                output[pos + i] = (byte)(input[pos + i] ^ keystream[i]);
            if (n == 16)
                output.AsSpan(pos, 16).CopyTo(feedback);
        }
        return output;
    }

    /// <summary>dst = a ^ b, eight bytes at a time (spans may exceed dst; extra key bytes are ignored).</summary>
    private static void XorInto(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int len = dst.Length;
        var a8 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(a[..len]);
        var b8 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(b[..len]);
        var d8 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(dst);
        for (int i = 0; i < d8.Length; i++)
            d8[i] = a8[i] ^ b8[i];
        for (int i = d8.Length * 8; i < len; i++)
            dst[i] = (byte)(a[i] ^ b[i]);
    }

    internal static byte[] BcXor(uint offset, ReadOnlySpan<byte> buf)
    {
        var result = new byte[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            var key = XmlKey[(int)((offset + (uint)i) % 8)];
            result[i] = (byte)(buf[i] ^ key ^ (byte)offset);
        }
        return result;
    }

    /// <summary>AES-128 CFB with full-block (128-bit) feedback; works on arbitrary
    /// lengths. REFERENCE implementation: no longer on the wire path (see
    /// <see cref="AesCfbDecrypt"/>/<see cref="AesCfbEncrypt"/>), kept so the
    /// selftest can prove the fast paths byte-identical to it.</summary>
    internal static byte[] AesCfb(ReadOnlySpan<byte> input, byte[] key, bool encrypting)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var ecb = aes.CreateEncryptor();

        var output = new byte[input.Length];
        var feedback = (byte[])Iv.Clone();
        var keystream = new byte[16];

        for (int pos = 0; pos < input.Length; pos += 16)
        {
            ecb.TransformBlock(feedback, 0, 16, keystream, 0);
            int n = Math.Min(16, input.Length - pos);
            for (int i = 0; i < n; i++)
                output[pos + i] = (byte)(input[pos + i] ^ keystream[i]);

            // Next feedback register = this block's ciphertext
            if (n == 16)
            {
                if (encrypting)
                    Array.Copy(output, pos, feedback, 0, 16);
                else
                    input.Slice(pos, 16).CopyTo(feedback);
            }
        }
        return output;
    }
}
