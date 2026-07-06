using System.Buffers.Binary;
using System.Text;

namespace Neolink.Mqtt;

/// <summary>
/// MQTT 3.1.1 (protocol level 4) wire encoding — the small subset a publish/
/// subscribe client needs. Kept dependency-free in keeping with the rest of the
/// project (no external MQTT library). Only what Home Assistant needs is here:
/// CONNECT, PUBLISH (QoS 0), SUBSCRIBE, PINGREQ, DISCONNECT, and parsing of the
/// packets the broker sends back.
/// </summary>
internal static class MqttPacket
{
    // Control packet types (high nibble of the fixed-header byte).
    public const byte Connect = 0x10;
    public const byte ConnAck = 0x20;
    public const byte Publish = 0x30;
    public const byte PubAck = 0x40;
    public const byte Subscribe = 0x82;   // reserved low bits must be 0b0010
    public const byte SubAck = 0x90;
    public const byte PingReq = 0xC0;
    public const byte PingResp = 0xD0;
    public const byte Disconnect = 0xE0;

    // ------------------------------------------------------------------ encode

    public static byte[] BuildConnect(string clientId, string? username, string? password,
        ushort keepAlive, string? willTopic, string? willPayload, bool willRetain)
    {
        var vh = new List<byte>();
        WriteString(vh, "MQTT");
        vh.Add(4); // protocol level 4 = MQTT 3.1.1

        byte flags = 0x02; // clean session
        if (username != null) flags |= 0x80;
        if (password != null) flags |= 0x40;
        if (willTopic != null)
        {
            flags |= 0x04;                    // will flag
            if (willRetain) flags |= 0x20;    // will retain (QoS 0)
        }
        vh.Add(flags);
        vh.Add((byte)(keepAlive >> 8));
        vh.Add((byte)(keepAlive & 0xFF));

        var payload = new List<byte>();
        WriteString(payload, clientId);
        if (willTopic != null)
        {
            WriteString(payload, willTopic);
            WriteBytes(payload, Encoding.UTF8.GetBytes(willPayload ?? ""));
        }
        if (username != null) WriteString(payload, username);
        if (password != null) WriteString(payload, password);

        return Assemble(Connect, vh, payload);
    }

    public static byte[] BuildPublish(string topic, ReadOnlySpan<byte> payload, bool retain)
    {
        var vh = new List<byte>();
        WriteString(vh, topic); // QoS 0: no packet identifier
        byte header = (byte)(Publish | (retain ? 0x01 : 0x00));
        return Assemble(header, vh, payload);
    }

    public static byte[] BuildSubscribe(ushort packetId, IReadOnlyList<string> topics)
    {
        var vh = new List<byte> { (byte)(packetId >> 8), (byte)(packetId & 0xFF) };
        var payload = new List<byte>();
        foreach (var t in topics)
        {
            WriteString(payload, t);
            payload.Add(0x00); // requested QoS 0
        }
        return Assemble(Subscribe, vh, payload);
    }

    public static byte[] BuildPingReq() => new byte[] { PingReq, 0x00 };
    public static byte[] BuildDisconnect() => new byte[] { Disconnect, 0x00 };

    // ------------------------------------------------------------------ decode

    /// <summary>Parsed PUBLISH from the broker (QoS 0 handled; higher QoS ignored by the client).</summary>
    public readonly record struct IncomingPublish(string Topic, byte[] Payload);

    /// <summary>Splits a received PUBLISH body (after the fixed header) into topic + payload.</summary>
    public static IncomingPublish ParsePublish(byte header, ReadOnlySpan<byte> body)
    {
        int qos = (header >> 1) & 0x03;
        int topicLen = (body[0] << 8) | body[1];
        var topic = Encoding.UTF8.GetString(body.Slice(2, topicLen));
        int pos = 2 + topicLen;
        if (qos > 0) pos += 2; // skip the packet identifier (we don't ack QoS>0)
        return new IncomingPublish(topic, body[pos..].ToArray());
    }

    // ------------------------------------------------------------------ framing helpers

    /// <summary>Encodes the "remaining length" variable-byte integer (1–4 bytes).</summary>
    public static void WriteRemainingLength(List<byte> to, int value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0) b |= 0x80;
            to.Add(b);
        } while (value > 0);
    }

    private static byte[] Assemble(byte header, List<byte> variableHeader, ReadOnlySpan<byte> payload)
    {
        var frame = new List<byte>(variableHeader.Count + payload.Length + 5) { header };
        WriteRemainingLength(frame, variableHeader.Count + payload.Length);
        frame.AddRange(variableHeader);
        var arr = new byte[frame.Count + payload.Length];
        frame.CopyTo(arr);
        payload.CopyTo(arr.AsSpan(frame.Count));
        return arr;
    }

    private static byte[] Assemble(byte header, List<byte> variableHeader, List<byte> payload)
    {
        var frame = new List<byte>(variableHeader.Count + payload.Count + 5) { header };
        WriteRemainingLength(frame, variableHeader.Count + payload.Count);
        frame.AddRange(variableHeader);
        frame.AddRange(payload);
        return frame.ToArray();
    }

    private static void WriteString(List<byte> to, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteBytes(to, bytes);
    }

    private static void WriteBytes(List<byte> to, byte[] bytes)
    {
        if (bytes.Length > 0xFFFF) throw new ArgumentException("MQTT field exceeds 65535 bytes");
        to.Add((byte)(bytes.Length >> 8));
        to.Add((byte)(bytes.Length & 0xFF));
        to.AddRange(bytes);
    }

    internal static ushort ReadUInt16(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt16BigEndian(s);
}
