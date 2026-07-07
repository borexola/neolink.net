// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using Neolink.Media;

namespace Neolink.Rtsp;

/// <summary>RTP packet construction and H.264/H.265/AAC/L16 payload packetization.</summary>
public sealed class RtpPacketizer
{
    public const int MaxPayload = 1400;

    public byte PayloadType { get; }
    public uint Ssrc { get; }
    public ushort Seq { get; private set; }

    public RtpPacketizer(byte payloadType)
    {
        PayloadType = payloadType;
        Ssrc = (uint)Random.Shared.Next();
        Seq = (ushort)Random.Shared.Next(0, ushort.MaxValue);
    }

    private byte[] BuildPacket(ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, uint ts, bool marker)
    {
        var pkt = new byte[12 + payload1.Length + payload2.Length];
        pkt[0] = 0x80; // V=2
        pkt[1] = (byte)((marker ? 0x80 : 0x00) | PayloadType);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), Seq);
        BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), ts);
        BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(8), Ssrc);
        payload1.CopyTo(pkt.AsSpan(12));
        payload2.CopyTo(pkt.AsSpan(12 + payload1.Length));
        Seq++;
        return pkt;
    }

    /// <summary>Packetizes one H.264/H.265 access unit (Annex-B) into RTP packets.</summary>
    public List<byte[]> PacketizeVideo(VideoCodec codec, ReadOnlyMemory<byte> annexB, uint ts)
    {
        var packets = new List<byte[]>();
        var nals = H26x.SplitNals(annexB);
        for (int n = 0; n < nals.Count; n++)
        {
            var nal = nals[n];
            if (nal.Length == 0) continue;
            bool lastNal = n == nals.Count - 1;

            if (nal.Length <= MaxPayload)
            {
                packets.Add(BuildPacket(nal.Span, ReadOnlySpan<byte>.Empty, ts, marker: lastNal));
                continue;
            }

            if (codec == VideoCodec.H264)
                FragmentH264(packets, nal, ts, lastNal);
            else
                FragmentH265(packets, nal, ts, lastNal);
        }
        return packets;
    }

    private void FragmentH264(List<byte[]> packets, ReadOnlyMemory<byte> nal, uint ts, bool lastNal)
    {
        var span = nal.Span;
        byte nalHeader = span[0];
        byte type = (byte)(nalHeader & 0x1F);

        Span<byte> head = stackalloc byte[2];
        head[0] = (byte)((nalHeader & 0xE0) | 28); // FU-A indicator

        int offset = 1;
        bool first = true;
        while (offset < nal.Length)
        {
            int chunk = Math.Min(MaxPayload - 2, nal.Length - offset);
            bool last = offset + chunk >= nal.Length;
            head[1] = (byte)(type | (first ? 0x80 : 0) | (last ? 0x40 : 0));
            packets.Add(BuildPacket(head, span.Slice(offset, chunk), ts, marker: last && lastNal));
            offset += chunk;
            first = false;
        }
    }

    private void FragmentH265(List<byte[]> packets, ReadOnlyMemory<byte> nal, uint ts, bool lastNal)
    {
        var span = nal.Span;
        byte type = (byte)((span[0] >> 1) & 0x3F);

        Span<byte> head = stackalloc byte[3];
        head[0] = (byte)((span[0] & 0x81) | (49 << 1)); // PayloadHdr: FU type=49, keep layer/tid bits
        head[1] = span[1];

        int offset = 2;
        bool first = true;
        while (offset < nal.Length)
        {
            int chunk = Math.Min(MaxPayload - 3, nal.Length - offset);
            bool last = offset + chunk >= nal.Length;
            head[2] = (byte)(type | (first ? 0x80 : 0) | (last ? 0x40 : 0));
            packets.Add(BuildPacket(head, span.Slice(offset, chunk), ts, marker: last && lastNal));
            offset += chunk;
            first = false;
        }
    }

    /// <summary>RFC 3640 (mpeg4-generic, AAC-hbr): 16-bit AU-headers-length + one AU header.</summary>
    public byte[] PacketizeAac(ReadOnlySpan<byte> au, uint ts)
    {
        Span<byte> head = stackalloc byte[4];
        head[0] = 0x00;
        head[1] = 0x10; // AU-headers-length = 16 bits
        int sizeBits = (au.Length << 3) & 0xFFF8; // 13-bit size, 3-bit index (0)
        head[2] = (byte)(sizeBits >> 8);
        head[3] = (byte)(sizeBits & 0xFF);
        return BuildPacket(head, au, ts, marker: true);
    }

    /// <summary>L16 (RFC 3551): network byte order 16-bit PCM. Input is little-endian.</summary>
    public List<byte[]> PacketizePcm(ReadOnlySpan<byte> pcmLe, uint baseTs)
    {
        const int samplesPerPacket = 320; // 40 ms @ 8 kHz
        var packets = new List<byte[]>();
        int totalSamples = pcmLe.Length / 2;
        for (int s = 0; s < totalSamples; s += samplesPerPacket)
        {
            int count = Math.Min(samplesPerPacket, totalSamples - s);
            var payload = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                payload[i * 2] = pcmLe[(s + i) * 2 + 1];     // swap to big-endian
                payload[i * 2 + 1] = pcmLe[(s + i) * 2];
            }
            packets.Add(BuildPacket(payload, ReadOnlySpan<byte>.Empty, unchecked(baseTs + (uint)s), marker: false));
        }
        return packets;
    }
}
