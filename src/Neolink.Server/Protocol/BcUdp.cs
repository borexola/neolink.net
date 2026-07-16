// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan-over-UDP framing (magic numbers and packet layout) derives from the
// reverse-engineering work in the QuantumEntangledAndy/neolink project
// (crates/core/src/bcudp).
using System.Buffers.Binary;

namespace Neolink;

/// <summary>
/// The three Baichuan-over-UDP packet kinds beyond discovery: the transport data
/// packets (BC bytes inside) and their acknowledgments. All integers little-endian.
///
///   UdpData : magic(4) connId(4) 0(4) packetId(4) len(4) payload[len]
///   UdpAck  : magic(4) connId(4) 0(4) groupId(4) packetId(4) latency(4) len(4) payload[len]
///
/// connId is the DESTINATION's connection id: packets to the camera carry its did,
/// packets to us carry our cid.
/// </summary>
internal static class BcUdp
{
    public const int DataHeader = 20;
    public const int AckHeader = 28;

    /// <summary>The signalling value for "no packets received yet" (group id / -1).</summary>
    public const uint NoneReceived = 0xFFFFFFFF;

    public enum Kind { Unknown, Data, Ack, Discovery }

    public static byte[] BuildData(int connId, uint packetId, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[DataHeader + payload.Length];
        var s = pkt.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, UdpDiscovery.MagicData);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], connId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)payload.Length);
        payload.CopyTo(s[20..]);
        return pkt;
    }

    public static byte[] BuildAck(int connId, uint groupId, uint packetId, uint latency, ReadOnlySpan<byte> truthTable)
    {
        var pkt = new byte[AckHeader + truthTable.Length];
        var s = pkt.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, UdpDiscovery.MagicAck);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], connId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], groupId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], latency);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)truthTable.Length);
        truthTable.CopyTo(s[28..]);
        return pkt;
    }

    /// <summary>Peek the packet kind from its magic.</summary>
    public static Kind PeekKind(ReadOnlySpan<byte> dgram)
    {
        if (dgram.Length < 4) return Kind.Unknown;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(dgram);
        return magic == UdpDiscovery.MagicData ? Kind.Data
            : magic == UdpDiscovery.MagicAck ? Kind.Ack
            : magic == UdpDiscovery.MagicDiscovery ? Kind.Discovery
            : Kind.Unknown;
    }

    public static bool TryParseData(ReadOnlySpan<byte> dgram, out int connId, out uint packetId, out byte[] payload)
    {
        connId = 0; packetId = 0; payload = Array.Empty<byte>();
        if (dgram.Length < DataHeader || PeekKind(dgram) != Kind.Data) return false;
        connId = BinaryPrimitives.ReadInt32LittleEndian(dgram[4..]);
        packetId = BinaryPrimitives.ReadUInt32LittleEndian(dgram[12..]);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(dgram[16..]);
        if (len > dgram.Length - DataHeader) return false;
        payload = dgram.Slice(DataHeader, (int)len).ToArray();
        return true;
    }

    public static bool TryParseAck(ReadOnlySpan<byte> dgram, out int connId, out uint groupId, out uint packetId)
    {
        connId = 0; groupId = 0; packetId = 0;
        if (dgram.Length < AckHeader || PeekKind(dgram) != Kind.Ack) return false;
        connId = BinaryPrimitives.ReadInt32LittleEndian(dgram[4..]);
        groupId = BinaryPrimitives.ReadUInt32LittleEndian(dgram[12..]);
        packetId = BinaryPrimitives.ReadUInt32LittleEndian(dgram[16..]);
        return true;
    }
}
