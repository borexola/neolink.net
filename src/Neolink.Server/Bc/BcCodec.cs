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
using System.Text;

namespace Neolink.Bc;

public sealed class BcProtocolException : Exception
{
    public BcProtocolException(string message) : base(message) { }
}

/// <summary>
/// Wire codec for BC messages.
///
/// Header layout (little endian):
///   u32 magic (0x0abcdef0)
///   u32 msg_id
///   u32 body_len
///   u8  channel_id
///   u8  stream_type
///   u16 msg_num
///   u16 response_code
///   u16 class
///   [u32 payload_offset]   -- only for classes 0x6414 / 0x0000
/// </summary>
public static class BcCodec
{
    public static async Task<BcMessage> ReadMessageAsync(Stream stream, BcContext ctx, CancellationToken ct)
    {
        var head = new byte[20];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0));
        if (magic != BcConstants.MagicHeader)
            throw new BcProtocolException($"Invalid magic header 0x{magic:x8} (stream desynchronized)");

        uint msgId = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        byte channelId = head[12];
        byte streamType = head[13];
        ushort msgNum = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
        ushort responseCode = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(16));
        ushort cls = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(18));

        uint? payloadOffset = null;
        if (BcConstants.HasPayloadOffset(cls))
        {
            var ext = new byte[4];
            await stream.ReadExactlyAsync(ext, ct).ConfigureAwait(false);
            payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(ext);
        }

        if (bodyLen > 64 * 1024 * 1024)
            throw new BcProtocolException($"Implausible body length {bodyLen}");

        var body = new byte[bodyLen];
        if (bodyLen > 0)
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

        var meta = new BcMeta
        {
            MsgId = msgId,
            ChannelId = channelId,
            StreamType = streamType,
            MsgNum = msgNum,
            ResponseCode = responseCode,
            Class = cls,
        };

        var msg = new BcMessage { Meta = meta };

        if (!BcConstants.IsModernClass(cls))
        {
            // Legacy message: only login replies are understood (username/password are 32-byte fields)
            if (msgId == BcConstants.MsgIdLogin && body.Length >= 64)
            {
                msg.LegacyUsername = Encoding.ASCII.GetString(body, 0, 32);
                msg.LegacyPassword = Encoding.ASCII.GetString(body, 32, 32);
            }
            return msg;
        }

        // The login reply carries the negotiated encryption scheme in its response code.
        // It also carries the nonce, which hasn't been exchanged yet, so the reply itself
        // is only ever plaintext (0x00) or BCEncrypt. If AES was negotiated (any other low
        // byte, e.g. 0x12) the switch to AES happens after the handshake, once the session
        // key can be derived from the nonce — see BcCamera.LoginAsync.
        if (msgId == BcConstants.MsgIdLogin && (responseCode >> 8) == 0xdd)
        {
            ctx.Encryption.Set((responseCode & 0xff) == 0x00
                ? EncryptionKind.Unencrypted
                : EncryptionKind.BcEncrypt);
        }

        uint extLen = payloadOffset ?? 0;
        if (extLen > bodyLen)
            throw new BcProtocolException($"payload offset {extLen} exceeds body length {bodyLen}");

        // Extension XML (always encrypted with the negotiated protocol)
        if (extLen > 0)
        {
            var extRaw = new byte[extLen];
            Array.Copy(body, 0, extRaw, 0, (int)extLen);
            var extPlain = XmlCrypto.Decrypt(channelId, extRaw, ctx.Encryption);
            var extension = Xml.ExtensionXml.TryParse(extPlain);
            if (extension != null)
            {
                if (extension.BinaryData == 1)
                    ctx.InBinMode.Add(msgNum);
                msg.Extension = extension;
            }
            else
            {
                Log.Debug($"Failed to parse Extension XML on msg {msgId}/{msgNum}");
            }
        }

        int payloadLen = (int)(bodyLen - extLen);
        if (payloadLen > 0)
        {
            // No extension (the common case for every streaming media message):
            // the body IS the payload — reuse it instead of allocating and copying
            // the entire frame again. body is never referenced after this point.
            byte[] payloadRaw;
            if (extLen == 0)
            {
                payloadRaw = body;
            }
            else
            {
                payloadRaw = new byte[payloadLen];
                Array.Copy(body, (int)extLen, payloadRaw, 0, payloadLen);
            }
            if (ctx.InBinMode.Contains(msgNum))
            {
                var (kind, _) = ctx.Encryption.Snapshot();
                var encLen = msg.Extension?.EncryptLen;
                if (kind == EncryptionKind.FullAes && encLen.HasValue)
                {
                    // FullAes: the media stream is encrypted too. The ciphertext is padded,
                    // so truncate to the plaintext length from this message's Extension.
                    var plain = XmlCrypto.Decrypt(channelId, payloadRaw, ctx.Encryption);
                    msg.Binary = encLen.Value <= (uint)plain.Length ? plain[..(int)encLen.Value] : plain;
                }
                else
                {
                    // Under None/BCEncrypt/plain AES (or no encryptLen), binary payloads are NOT encrypted
                    msg.Binary = payloadRaw;
                }
            }
            else
            {
                var plain = XmlCrypto.Decrypt(channelId, payloadRaw, ctx.Encryption);
                var xml = Xml.BcXmlBody.TryParse(plain);
                if (xml != null)
                {
                    msg.Xml = xml;
                }
                else
                {
                    // Be forgiving: deliver as binary rather than killing the connection.
                    Log.Debug($"Unparseable XML payload on msg {msgId}/{msgNum}; treating as binary ({payloadLen} bytes)");
                    msg.Binary = payloadRaw;
                }
            }
        }

        return msg;
    }

    public static byte[] Serialize(BcMessage msg, EncryptionState enc)
    {
        byte[] body;
        uint? payloadOffset = null;

        if (!BcConstants.IsModernClass(msg.Meta.Class))
        {
            if (msg.LegacyUsername == null && msg.LegacyPassword == null)
            {
                // Header-only legacy message ("login upgrade"): no body.
                body = Array.Empty<byte>();
            }
            else
            {
                // Legacy login: 32-byte username + 32-byte password + 1772 zero bytes = 1836 bytes
                var user = msg.LegacyUsername ?? throw new BcProtocolException("legacy message requires username");
                var pass = msg.LegacyPassword ?? throw new BcProtocolException("legacy message requires password");
                if (user.Length != 32 || pass.Length != 32)
                    throw new BcProtocolException("legacy login fields must be exactly 32 chars");
                body = new byte[1836];
                Encoding.ASCII.GetBytes(user, body.AsSpan(0, 32));
                Encoding.ASCII.GetBytes(pass, body.AsSpan(32, 32));
            }
        }
        else
        {
            using var ms = new MemoryStream();
            uint extLen = 0;
            if (msg.Extension != null)
            {
                var extBytes = XmlCrypto.Encrypt(msg.Meta.ChannelId, msg.Extension.Serialize(), enc);
                ms.Write(extBytes);
                extLen = (uint)extBytes.Length;
            }
            if (BcConstants.HasPayloadOffset(msg.Meta.Class))
                payloadOffset = msg.Extension != null ? extLen : 0;

            if (msg.Xml != null)
            {
                var xmlBytes = XmlCrypto.Encrypt(msg.Meta.ChannelId, msg.Xml.Serialize(), enc);
                ms.Write(xmlBytes);
            }
            else if (msg.Binary != null)
            {
                ms.Write(msg.Binary);
            }
            body = ms.ToArray();
        }

        int headerLen = 20 + (payloadOffset.HasValue ? 4 : 0);
        var packet = new byte[headerLen + body.Length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], BcConstants.MagicHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], msg.Meta.MsgId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)body.Length);
        span[12] = msg.Meta.ChannelId;
        span[13] = msg.Meta.StreamType;
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], msg.Meta.MsgNum);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], msg.Meta.ResponseCode);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], msg.Meta.Class);
        if (payloadOffset.HasValue)
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], payloadOffset.Value);
        body.CopyTo(span[headerLen..]);
        return packet;
    }
}
