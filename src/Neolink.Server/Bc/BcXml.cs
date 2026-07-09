// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Neolink.Bc.Xml;

/// <summary>
/// The XML payloads used by the BC protocol. Hand-rolled (de)serialization with
/// XElement gives us exact control over element names/order and is lenient about
/// unknown elements the camera may send.
/// </summary>
public sealed class BcXmlBody
{
    public EncryptionXml? Encryption { get; set; }
    public LoginUserXml? LoginUser { get; set; }
    public LoginNetXml? LoginNet { get; set; }
    public DeviceInfoXml? DeviceInfo { get; set; }
    public VersionInfoXml? VersionInfo { get; set; }
    public PreviewXml? Preview { get; set; }
    public PtzControlXml? PtzControl { get; set; }
    public StreamInfoListXml? StreamInfoList { get; set; }

    /// <summary>
    /// Every child element of the body as raw XML, in wire order. This keeps
    /// messages we have no typed model for (Support, LedState, RfAlarmCfg,
    /// BatteryInfo, ...) fully accessible, and enables read-modify-write of
    /// camera settings without modelling — and risking reordering — every field.
    /// </summary>
    public List<XElement> Raw { get; } = new();

    /// <summary>First raw body element with the given name, if present.</summary>
    public XElement? RawElement(string name) =>
        Raw.FirstOrDefault(e => e.Name.LocalName == name);

    public const string XmlVersion = "1.1";

    public static BcXmlBody? TryParse(byte[] plaintext)
    {
        var root = XmlUtil.TryParseRoot(plaintext);
        if (root == null || root.Name.LocalName != "body") return null;

        var body = new BcXmlBody();
        foreach (var el in root.Elements())
        {
            body.Raw.Add(el);
            switch (el.Name.LocalName)
            {
                case "Encryption":
                    body.Encryption = new EncryptionXml
                    {
                        Version = (string?)el.Attribute("version") ?? "",
                        Type = (string?)el.Element("type") ?? "",
                        Nonce = (string?)el.Element("nonce") ?? "",
                    };
                    break;
                case "LoginUser":
                    body.LoginUser = new LoginUserXml
                    {
                        UserName = (string?)el.Element("userName") ?? "",
                        Password = (string?)el.Element("password") ?? "",
                        UserVer = (uint?)el.Element("userVer") ?? 0,
                    };
                    break;
                case "LoginNet":
                    body.LoginNet = new LoginNetXml
                    {
                        Type = (string?)el.Element("type") ?? "LAN",
                        UdpPort = (ushort)((uint?)el.Element("udpPort") ?? 0),
                    };
                    break;
                case "DeviceInfo":
                    body.DeviceInfo = DeviceInfoXml.Parse(el);
                    break;
                case "VersionInfo":
                    body.VersionInfo = new VersionInfoXml
                    {
                        Name = (string?)el.Element("name") ?? "",
                        Model = (string?)el.Element("model") ?? (string?)el.Element("type") ?? "",
                        SerialNumber = (string?)el.Element("serialNumber") ?? "",
                        BuildDay = (string?)el.Element("buildDay") ?? "",
                        HardwareVersion = (string?)el.Element("hardwareVersion") ?? "",
                        CfgVersion = (string?)el.Element("cfgVersion") ?? "",
                        FirmwareVersion = (string?)el.Element("firmwareVersion") ?? "",
                        Detail = (string?)el.Element("detail") ?? "",
                    };
                    break;
                case "StreamInfoList":
                    body.StreamInfoList = StreamInfoListXml.Parse(el);
                    break;
                case "Preview":
                    body.Preview = new PreviewXml
                    {
                        ChannelId = (byte)((uint?)el.Element("channelId") ?? 0),
                        Handle = (uint?)el.Element("handle") ?? 0,
                        StreamType = (string?)el.Element("streamType") ?? "",
                    };
                    break;
            }
        }
        return body;
    }

    public byte[] Serialize()
    {
        var root = new XElement("body");
        if (LoginUser != null)
        {
            root.Add(new XElement("LoginUser",
                new XAttribute("version", XmlVersion),
                new XElement("userName", LoginUser.UserName),
                new XElement("password", LoginUser.Password),
                new XElement("userVer", LoginUser.UserVer)));
        }
        if (LoginNet != null)
        {
            root.Add(new XElement("LoginNet",
                new XAttribute("version", XmlVersion),
                new XElement("type", LoginNet.Type),
                new XElement("udpPort", LoginNet.UdpPort)));
        }
        if (Preview != null)
        {
            root.Add(new XElement("Preview",
                new XAttribute("version", XmlVersion),
                new XElement("channelId", Preview.ChannelId),
                new XElement("handle", Preview.Handle),
                new XElement("streamType", Preview.StreamType)));
        }
        if (PtzControl != null)
        {
            root.Add(new XElement("PtzControl",
                new XAttribute("version", XmlVersion),
                new XElement("channelId", PtzControl.ChannelId),
                new XElement("speed", PtzControl.Speed),
                new XElement("command", PtzControl.Command)));
        }
        // Raw elements ride along verbatim — but never duplicate an element the
        // typed properties above already emitted (a parsed body has both views).
        foreach (var el in Raw)
        {
            bool typed = el.Name.LocalName switch
            {
                "LoginUser" => LoginUser != null,
                "LoginNet" => LoginNet != null,
                "Preview" => Preview != null,
                "PtzControl" => PtzControl != null,
                _ => false,
            };
            if (!typed)
                root.Add(el);
        }
        return XmlUtil.SerializeDoc(root);
    }

    /// <summary>A body carrying one raw element verbatim (read-modify-write of camera settings).</summary>
    public static BcXmlBody FromRaw(XElement element)
    {
        var body = new BcXmlBody();
        body.Raw.Add(element);
        return body;
    }
}

public sealed class EncryptionXml
{
    public string Version = "";
    public string Type = "";
    public string Nonce = "";
}

public sealed class LoginUserXml
{
    public string UserName = "";
    public string Password = "";
    public uint UserVer = 1;
}

public sealed class LoginNetXml
{
    public string Type = "LAN";
    public ushort UdpPort;
}

public sealed class DeviceInfoXml
{
    public string ResolutionName = "";
    public uint Width;
    public uint Height;

    public static DeviceInfoXml Parse(XElement el)
    {
        var info = new DeviceInfoXml();
        var res = el.Element("resolution");
        if (res != null)
        {
            info.ResolutionName = (string?)res.Element("resolutionName") ?? "";
            info.Width = (uint?)res.Element("width") ?? 0;
            info.Height = (uint?)res.Element("height") ?? 0;
        }
        return info;
    }
}

public sealed class VersionInfoXml
{
    public string Name = "";
    public string Model = "";
    public string SerialNumber = "";
    public string BuildDay = "";
    public string HardwareVersion = "";
    public string CfgVersion = "";
    public string FirmwareVersion = "";
    public string Detail = "";
}

public sealed class PreviewXml
{
    public byte ChannelId;
    public uint Handle;
    public string StreamType = "mainStream";
}

/// <summary>PTZ movement command. Commands: up, down, left, right, stop.</summary>
public sealed class PtzControlXml
{
    public byte ChannelId;
    public float Speed = 32;
    public string Command = "stop";
}

/// <summary>The encode profiles a camera supports, per stream (msg 146 reply).</summary>
public sealed class StreamInfoListXml
{
    public List<StreamInfoXml> StreamInfos { get; } = new();

    public static StreamInfoListXml Parse(XElement el)
    {
        // Lenient numeric read: cameras put surprising strings in numeric-looking
        // fields (e.g. "none"), and an XElement uint cast throws on those.
        static uint U(XElement? e) => e != null && uint.TryParse(e.Value.Trim(), out var v) ? v : 0;

        var list = new StreamInfoListXml();
        foreach (var si in el.Elements("StreamInfo"))
        {
            var info = new StreamInfoXml { ChannelBits = U(si.Element("channelBits")) };
            foreach (var et in si.Elements("encodeTable"))
            {
                info.EncodeTables.Add(new EncodeTableXml
                {
                    Type = (string?)et.Element("type") ?? "",
                    Width = U(et.Element("resolution")?.Element("width")),
                    Height = U(et.Element("resolution")?.Element("height")),
                    DefaultFramerate = U(et.Element("defaultFramerate")),
                    DefaultBitrate = U(et.Element("defaultBitrate")),
                    FramerateTable = (string?)et.Element("framerateTable") ?? "",
                    BitrateTable = (string?)et.Element("bitrateTable") ?? "",
                });
            }
            list.StreamInfos.Add(info);
        }
        return list;
    }
}

public sealed class StreamInfoXml
{
    public uint ChannelBits;
    public List<EncodeTableXml> EncodeTables { get; } = new();
}

/// <summary>One encode profile: name (e.g. "mainStream"), resolution and the framerate/bitrate options.</summary>
public sealed class EncodeTableXml
{
    public string Type = "";
    public uint Width;
    public uint Height;
    public uint DefaultFramerate;
    public uint DefaultBitrate;
    /// <summary>Space-separated list of selectable framerates.</summary>
    public string FramerateTable = "";
    /// <summary>Space-separated list of selectable bitrates (kbps).</summary>
    public string BitrateTable = "";
}

/// <summary>
/// The audio profile a camera accepts for two-way talk (msg 10 TalkAbility reply).
/// Cameras list one or more audioConfig entries; the first (highest priority) is
/// used. Missing fields fall back to the values every talk-capable Reolink model
/// observed so far uses: full-duplex 16 kHz mono ADPCM.
/// </summary>
public sealed class TalkAbilityXml
{
    public string Duplex = "FDX";
    public string AudioStreamMode = "followVideoStream";
    public string AudioType = "adpcm";
    public uint SampleRate = 16000;
    public uint SamplePrecision = 16;
    public uint LengthPerEncoder = 512;
    public string SoundTrack = "mono";

    public static TalkAbilityXml Parse(XElement el)
    {
        var ability = new TalkAbilityXml();
        static string? S(XElement? e) => string.IsNullOrWhiteSpace(e?.Value) ? null : e!.Value.Trim();
        static uint? U(XElement? e) => e != null && uint.TryParse(e.Value.Trim(), out var v) && v > 0 ? v : null;

        ability.Duplex = S(el.Element("duplexList")?.Element("duplex")) ?? ability.Duplex;
        ability.AudioStreamMode = S(el.Element("audioStreamModeList")?.Element("audioStreamMode"))
            ?? ability.AudioStreamMode;
        var cfg = el.Element("audioConfigList")?.Element("audioConfig");
        if (cfg != null)
        {
            ability.AudioType = S(cfg.Element("audioType")) ?? ability.AudioType;
            ability.SampleRate = U(cfg.Element("sampleRate")) ?? ability.SampleRate;
            ability.SamplePrecision = U(cfg.Element("samplePrecision")) ?? ability.SamplePrecision;
            ability.LengthPerEncoder = U(cfg.Element("lengthPerEncoder")) ?? ability.LengthPerEncoder;
            ability.SoundTrack = S(cfg.Element("soundTrack")) ?? ability.SoundTrack;
        }
        return ability;
    }
}

/// <summary>The `Extension` XML which precedes payloads (at the payload offset).</summary>
public sealed class ExtensionXml
{
    public uint? BinaryData;
    public byte? ChannelId;
    /// <summary>PIR sensor id; PIR get/set address the sensor via rfId instead of channelId.</summary>
    public byte? RfId;
    /// <summary>Plaintext length of an encrypted (FullAes) binary payload; the ciphertext is padded.</summary>
    public uint? EncryptLen;

    public static ExtensionXml? TryParse(byte[] plaintext)
    {
        var root = XmlUtil.TryParseRoot(plaintext);
        if (root == null || root.Name.LocalName != "Extension") return null;
        return new ExtensionXml
        {
            BinaryData = (uint?)root.Element("binaryData"),
            ChannelId = (byte?)(uint?)root.Element("channelId"),
            RfId = (byte?)(uint?)root.Element("rfId"),
            EncryptLen = (uint?)root.Element("encryptLen"),
        };
    }

    public byte[] Serialize()
    {
        var root = new XElement("Extension", new XAttribute("version", BcXmlBody.XmlVersion));
        if (BinaryData.HasValue) root.Add(new XElement("binaryData", BinaryData.Value));
        if (ChannelId.HasValue) root.Add(new XElement("channelId", ChannelId.Value));
        if (RfId.HasValue) root.Add(new XElement("rfId", RfId.Value));
        return XmlUtil.SerializeDoc(root);
    }
}

internal static class XmlUtil
{
    /// <summary>Parses an XML document from raw bytes, tolerating trailing NULs/garbage.</summary>
    public static XElement? TryParseRoot(byte[] plaintext)
    {
        try
        {
            var text = Encoding.UTF8.GetString(plaintext);
            // Trim anything after the final '>' (some cameras pad with NULs)
            int end = text.LastIndexOf('>');
            if (end < 0) return null;
            text = text[..(end + 1)];
            int start = text.IndexOf('<');
            if (start < 0) return null;
            text = text[start..];
            return XDocument.Parse(text).Root;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] SerializeDoc(XElement root)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            new XDocument(root).Save(writer);
        }
        return ms.ToArray();
    }
}
