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
            EncryptionKind.Aes or EncryptionKind.FullAes when key != null => AesCfb(buf, key, encrypting: false),
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
            EncryptionKind.Aes or EncryptionKind.FullAes when key != null => AesCfb(buf, key, encrypting: true),
            EncryptionKind.Aes or EncryptionKind.FullAes => BcXor(offset, buf),
            _ => throw new InvalidOperationException("unknown encryption"),
        };
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

    /// <summary>AES-128 CFB with full-block (128-bit) feedback; works on arbitrary lengths.</summary>
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
