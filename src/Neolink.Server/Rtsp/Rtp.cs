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

    /// <summary>
    /// Packetizes one H.264/H.265 access unit (Annex-B) into <paramref name="batch"/>:
    /// every packet '$'-framed back to back for a single interleaved TCP write,
    /// with each packet's bare-RTP slice recorded so UDP sends datagrams from the
    /// same buffer. Replaces an allocation per RTP packet (~350-700/s per viewer).
    /// </summary>
    public void PacketizeVideoInto(RtpBatch batch, VideoCodec codec, ReadOnlyMemory<byte> annexB, uint ts,
        byte channel)
    {
        batch.Reset(channel);
        var nals = H26x.SplitNals(annexB);
        for (int n = 0; n < nals.Count; n++)
        {
            var nal = nals[n];
            if (nal.Length == 0) continue;
            bool lastNal = n == nals.Count - 1;

            if (nal.Length <= MaxPayload)
            {
                WritePacket(batch, nal.Span, ReadOnlySpan<byte>.Empty, ts, marker: lastNal);
                continue;
            }

            if (codec == VideoCodec.H264)
                FragmentH264(batch, nal, ts, lastNal);
            else
                FragmentH265(batch, nal, ts, lastNal);
        }
    }

    /// <summary>Test seam over <see cref="PacketizeVideoInto"/>: the same packets
    /// as individual arrays.</summary>
    internal List<byte[]> PacketizeVideo(VideoCodec codec, ReadOnlyMemory<byte> annexB, uint ts)
    {
        var batch = new RtpBatch();
        PacketizeVideoInto(batch, codec, annexB, ts, channel: 0);
        var packets = new List<byte[]>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
            packets.Add(batch.PacketAt(i).ToArray());
        return packets;
    }

    private void FragmentH264(RtpBatch batch, ReadOnlyMemory<byte> nal, uint ts, bool lastNal)
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
            WritePacket(batch, head, span.Slice(offset, chunk), ts, marker: last && lastNal);
            offset += chunk;
            first = false;
        }
    }

    private void FragmentH265(RtpBatch batch, ReadOnlyMemory<byte> nal, uint ts, bool lastNal)
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
            WritePacket(batch, head, span.Slice(offset, chunk), ts, marker: last && lastNal);
            offset += chunk;
            first = false;
        }
    }

    /// <summary>One '$'-framed RTP packet appended to the batch — the same bytes
    /// <see cref="BuildPacket"/> plus the interleave header used to produce.</summary>
    private void WritePacket(RtpBatch b, ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, uint ts,
        bool marker)
    {
        int rtpLen = 12 + payload1.Length + payload2.Length;
        var s = b.Reserve(4 + rtpLen);
        s[0] = 0x24; // '$'
        s[1] = b.Channel;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)rtpLen);
        var r = s[4..];
        r[0] = 0x80; // V=2
        r[1] = (byte)((marker ? 0x80 : 0x00) | PayloadType);
        BinaryPrimitives.WriteUInt16BigEndian(r[2..], Seq);
        BinaryPrimitives.WriteUInt32BigEndian(r[4..], ts);
        BinaryPrimitives.WriteUInt32BigEndian(r[8..], Ssrc);
        payload1.CopyTo(r[12..]);
        payload2.CopyTo(r[(12 + payload1.Length)..]);
        Seq++;
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

    /// <summary>Opus (RFC 7587): one Opus packet per RTP packet, 48 kHz clock.
    /// Camera audio packets (20 ms @ 32 kb/s ≈ 80 bytes) never need fragmenting.</summary>
    public byte[] PacketizeOpus(ReadOnlySpan<byte> packet, uint ts) =>
        BuildPacket(packet, ReadOnlySpan<byte>.Empty, ts, marker: false);

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

/// <summary>
/// Reusable per-session output of <see cref="RtpPacketizer.PacketizeVideoInto"/>:
/// one access unit's packets laid out back to back, each prefixed with its
/// 4-byte '$' interleave header. TCP writes <see cref="Framed"/> whole; UDP
/// sends each <see cref="PacketAt"/> slice as its own datagram (the framing
/// bytes between slices are simply skipped). The buffer must not be reused
/// until the send completes — one pump per session guarantees that.
/// </summary>
public sealed class RtpBatch
{
    private byte[] _buf = new byte[16 * 1024];
    private int _len;
    private readonly List<(int Offset, int Length)> _rtp = new();

    internal byte Channel { get; private set; }

    public int Count => _rtp.Count;
    public ReadOnlyMemory<byte> Framed => _buf.AsMemory(0, _len);
    public ReadOnlyMemory<byte> PacketAt(int i)
    {
        var (offset, length) = _rtp[i];
        return _buf.AsMemory(offset, length);
    }

    internal void Reset(byte channel)
    {
        _len = 0;
        _rtp.Clear();
        Channel = channel;
    }

    /// <summary>Grows as needed and records the packet slice; the returned span is
    /// the framed packet's bytes (header + RTP).</summary>
    internal Span<byte> Reserve(int size)
    {
        if (_len + size > _buf.Length)
        {
            int grown = _buf.Length;
            while (grown < _len + size) grown *= 2;
            Array.Resize(ref _buf, grown);
        }
        var span = _buf.AsSpan(_len, size);
        _rtp.Add((_len + 4, size - 4));
        _len += size;
        return span;
    }
}
