// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Media;

/// <summary>
/// Incremental Ogg reader that extracts Opus packets from an ogg/opus byte
/// stream (ffmpeg's stdout in the audio transcoder). The OpusHead/OpusTags
/// header packets are swallowed — they describe the stream, they are not audio.
/// Page CRCs are not verified: the source is a local pipe, not a lossy
/// transport, and a desynced page is caught by the capture-pattern scan.
/// One instance per ffmpeg process — a restart starts a fresh stream (new
/// OpusHead) and must start a fresh reader.
/// </summary>
public sealed class OggOpusReader
{
    // Safety valve only: ffmpeg is asked for one ~20 ms page at a time, so the
    // buffer normally holds well under a page. If it ever grows to this, the
    // stream is not Ogg and holding more of it helps nobody.
    private const int MaxBuffer = 1 << 20;

    private byte[] _buf = new byte[8192];
    private int _len;
    private readonly MemoryStream _packet = new(); // packets may continue across pages

    /// <summary>Consumes one chunk and returns every Opus packet it completed.</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        if (_len + data.Length > _buf.Length)
        {
            if (_len + data.Length > MaxBuffer)
            {
                _len = 0; // not an Ogg stream — drop and resync on the next capture
                _packet.SetLength(0);
            }
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        var packets = new List<byte[]>();
        int pos = 0;
        while (true)
        {
            int page = FindCapture(pos);
            if (page < 0) break;
            pos = page;
            if (_len - pos < 27) break;                       // fixed header
            int segCount = _buf[pos + 26];
            int headerLen = 27 + segCount;
            if (_len - pos < headerLen) break;                // segment table
            int payloadLen = 0;
            for (int i = 0; i < segCount; i++) payloadLen += _buf[pos + 27 + i];
            if (_len - pos < headerLen + payloadLen) break;   // full page

            int off = pos + headerLen;
            for (int i = 0; i < segCount; i++)
            {
                int lace = _buf[pos + 27 + i];
                _packet.Write(_buf, off, lace);
                off += lace;
                if (lace < 255) // a lacing value under 255 terminates the packet
                {
                    var pkt = _packet.ToArray();
                    _packet.SetLength(0);
                    if (!IsHeaderPacket(pkt)) packets.Add(pkt);
                }
            }
            pos += headerLen + payloadLen;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
            _len -= pos;
        }
        return packets;
    }

    private int FindCapture(int from)
    {
        for (int i = from; i + 4 <= _len; i++)
            if (_buf[i] == (byte)'O' && _buf[i + 1] == (byte)'g'
                && _buf[i + 2] == (byte)'g' && _buf[i + 3] == (byte)'S')
                return i;
        return -1;
    }

    internal static bool IsHeaderPacket(ReadOnlySpan<byte> pkt) =>
        pkt.Length >= 8 && (pkt.StartsWith("OpusHead"u8) || pkt.StartsWith("OpusTags"u8));
}
