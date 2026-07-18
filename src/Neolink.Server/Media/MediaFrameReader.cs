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
using System.Threading.Channels;

namespace Neolink.Media;

/// <summary>
/// Parses the BcMedia sub-stream (the binary payload inside MSG_ID_VIDEO messages)
/// into discrete media frames.
///
/// Packet types (little-endian magic):
///   0x31303031 "1001"  InfoV1     (header_size=32)
///   0x32303031 "1002"  InfoV2     (header_size=32)
///   0x63643030-39      IFrame     ("00dc".."90dc"; low digit = channel)
///   0x63643130-39      PFrame     ("01dc".."91dc")
///   0x62773530         AAC        ("05wb")
///   0x62773130         ADPCM      ("01wb")
/// Payloads are padded to 8-byte boundaries.
/// </summary>
public sealed class MediaFrameReader
{
    private const uint MagicInfoV1 = 0x31303031;
    private const uint MagicInfoV2 = 0x32303031;
    private const uint MagicIframeFirst = 0x63643030;
    private const uint MagicIframeLast = 0x63643039;
    private const uint MagicPframeFirst = 0x63643130;
    private const uint MagicPframeLast = 0x63643139;
    private const uint MagicAac = 0x62773530;
    private const uint MagicAdpcm = 0x62773130;
    private const int PadSize = 8;

    private readonly ChunkStream _stream;

    public MediaFrameReader(ChannelReader<byte[]> chunks)
    {
        _stream = new ChunkStream(chunks);
    }

    /// <summary>Reads the next media frame. Throws EndOfStreamException when the source completes.</summary>
    public async ValueTask<MediaFrame> ReadFrameAsync(CancellationToken ct)
    {
        bool wasSynced = true;
        while (true)
        {
            var peek = await _stream.PeekAsync(4, ct).ConfigureAwait(false);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(peek.Span);

            bool known = magic is MagicInfoV1 or MagicInfoV2 or MagicAac or MagicAdpcm
                or (>= MagicIframeFirst and <= MagicIframeLast)
                or (>= MagicPframeFirst and <= MagicPframeLast);

            if (!known)
            {
                // Desynchronized: slide one byte forward and hunt for the next magic.
                // (More robust than the original, which tears down the whole connection.)
                if (wasSynced)
                {
                    Log.Warn($"Media stream desynchronized (magic 0x{magic:x8}); resynchronizing");
                    wasSynced = false;
                }
                _stream.Consume(1);
                continue;
            }
            _stream.Consume(4);

            switch (magic)
            {
                case MagicInfoV1:
                case MagicInfoV2:
                    return await ReadInfoAsync(ct).ConfigureAwait(false);
                case >= MagicIframeFirst and <= MagicIframeLast:
                    return await ReadVideoAsync(keyframe: true, ct).ConfigureAwait(false);
                case >= MagicPframeFirst and <= MagicPframeLast:
                    return await ReadVideoAsync(keyframe: false, ct).ConfigureAwait(false);
                case MagicAac:
                    return await ReadAacAsync(ct).ConfigureAwait(false);
                default:
                    return await ReadAdpcmAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<MediaInfo> ReadInfoAsync(CancellationToken ct)
    {
        var buf = await ReadBytesAsync(28, ct).ConfigureAwait(false);
        // [0..4] header_size (=32), [4..8] width, [8..12] height, [12] unknown, [13] fps, then dates
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8));
        byte fps = buf[13];
        return new MediaInfo(width, height, fps);
    }

    private async ValueTask<VideoFrame> ReadVideoAsync(bool keyframe, CancellationToken ct)
    {
        var head = await ReadBytesAsync(16, ct).ConfigureAwait(false);
        // Byte compare, not GetString: this runs per frame, and the string was
        // only ever needed for the error message.
        var codec = head[0] == (byte)'H' && head[1] == (byte)'2' && head[2] == (byte)'6' && head[3] == (byte)'4'
            ? VideoCodec.H264
            : head[0] == (byte)'H' && head[1] == (byte)'2' && head[2] == (byte)'6' && head[3] == (byte)'5'
                ? VideoCodec.H265
                : throw new InvalidDataException(
                    $"Unrecognised video type '{Encoding.ASCII.GetString(head, 0, 4)}'");
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        uint additionalHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        uint microseconds = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        // 4 more bytes of unknown
        await ReadBytesAsync(4, ct).ConfigureAwait(false);

        uint? unixTime = null;
        if (additionalHeaderSize >= 4)
        {
            var t = await ReadBytesAsync(4, ct).ConfigureAwait(false);
            if (keyframe)
                unixTime = BinaryPrimitives.ReadUInt32LittleEndian(t);
            if (additionalHeaderSize > 4)
                await SkipAsync((int)(additionalHeaderSize - 4), ct).ConfigureAwait(false);
        }

        if (payloadSize > 32 * 1024 * 1024)
            throw new InvalidDataException($"Implausible video payload size {payloadSize}");

        var data = await ReadBytesAsync((int)payloadSize, ct).ConfigureAwait(false);
        await SkipAsync(Padding(payloadSize), ct).ConfigureAwait(false);
        return new VideoFrame(codec, keyframe, microseconds, unixTime, data);
    }

    private async ValueTask<AacFrame> ReadAacAsync(CancellationToken ct)
    {
        var head = await ReadBytesAsync(4, ct).ConfigureAwait(false);
        ushort payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0));
        var data = await ReadBytesAsync(payloadSize, ct).ConfigureAwait(false);
        await SkipAsync(Padding(payloadSize), ct).ConfigureAwait(false);
        return new AacFrame(data);
    }

    private async ValueTask<AdpcmFrame> ReadAdpcmAsync(CancellationToken ct)
    {
        var head = await ReadBytesAsync(4, ct).ConfigureAwait(false);
        ushort payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0));
        // Sub-header: u16 magic 0x0100, u16 half block size
        var sub = await ReadBytesAsync(4, ct).ConfigureAwait(false);
        ushort subMagic = BinaryPrimitives.ReadUInt16LittleEndian(sub.AsSpan(0));
        if (subMagic != 0x0100)
            throw new InvalidDataException($"ADPCM data magic 0x{subMagic:x4} is invalid");
        if (payloadSize < 4)
            throw new InvalidDataException("ADPCM payload too small");
        var data = await ReadBytesAsync(payloadSize - 4, ct).ConfigureAwait(false);
        await SkipAsync(Padding(payloadSize), ct).ConfigureAwait(false);
        return new AdpcmFrame(data);
    }

    private static int Padding(uint payloadSize)
    {
        uint rem = payloadSize % PadSize;
        return rem == 0 ? 0 : (int)(PadSize - rem);
    }

    private async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var mem = await _stream.PeekAsync(count, ct).ConfigureAwait(false);
        var result = mem.ToArray();
        _stream.Consume(count);
        return result;
    }

    private async ValueTask SkipAsync(int count, CancellationToken ct)
    {
        if (count <= 0) return;
        await _stream.PeekAsync(count, ct).ConfigureAwait(false);
        _stream.Consume(count);
    }

    /// <summary>
    /// Buffered reader over a channel of byte[] chunks with peek/consume semantics
    /// (needed for magic-based resynchronization).
    /// </summary>
    private sealed class ChunkStream
    {
        private readonly ChannelReader<byte[]> _chunks;
        private byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public ChunkStream(ChannelReader<byte[]> chunks) => _chunks = chunks;

        private int Available => _end - _start;

        public async ValueTask<ReadOnlyMemory<byte>> PeekAsync(int count, CancellationToken ct)
        {
            while (Available < count)
            {
                byte[] chunk;
                try
                {
                    chunk = await _chunks.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (ChannelClosedException ex)
                {
                    throw new EndOfStreamException("media stream ended", ex.InnerException);
                }
                Append(chunk);
            }
            return _buffer.AsMemory(_start, count);
        }

        public void Consume(int count)
        {
            _start += count;
            if (_start == _end) { _start = 0; _end = 0; }
        }

        private void Append(byte[] chunk)
        {
            int needed = Available + chunk.Length;
            if (_end + chunk.Length > _buffer.Length)
            {
                if (needed > _buffer.Length)
                {
                    int newSize = Math.Max(_buffer.Length * 2, needed);
                    var nb = new byte[newSize];
                    Array.Copy(_buffer, _start, nb, 0, Available);
                    _end = Available;
                    _start = 0;
                    _buffer = nb;
                }
                else
                {
                    Array.Copy(_buffer, _start, _buffer, 0, Available);
                    _end = Available;
                    _start = 0;
                }
            }
            Array.Copy(chunk, 0, _buffer, _end, chunk.Length);
            _end += chunk.Length;
        }
    }
}
