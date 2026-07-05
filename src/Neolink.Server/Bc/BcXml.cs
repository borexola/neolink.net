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

    public const string XmlVersion = "1.1";

    public static BcXmlBody? TryParse(byte[] plaintext)
    {
        var root = XmlUtil.TryParseRoot(plaintext);
        if (root == null || root.Name.LocalName != "body") return null;

        var body = new BcXmlBody();
        foreach (var el in root.Elements())
        {
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
                        SerialNumber = (string?)el.Element("serialNumber") ?? "",
                        BuildDay = (string?)el.Element("buildDay") ?? "",
                        HardwareVersion = (string?)el.Element("hardwareVersion") ?? "",
                        CfgVersion = (string?)el.Element("cfgVersion") ?? "",
                        FirmwareVersion = (string?)el.Element("firmwareVersion") ?? "",
                    };
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
        return XmlUtil.SerializeDoc(root);
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
    public string SerialNumber = "";
    public string BuildDay = "";
    public string HardwareVersion = "";
    public string CfgVersion = "";
    public string FirmwareVersion = "";
}

public sealed class PreviewXml
{
    public byte ChannelId;
    public uint Handle;
    public string StreamType = "mainStream";
}

/// <summary>The `Extension` XML which precedes payloads (at the payload offset).</summary>
public sealed class ExtensionXml
{
    public uint? BinaryData;
    public byte? ChannelId;
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
            EncryptLen = (uint?)root.Element("encryptLen"),
        };
    }

    public byte[] Serialize()
    {
        var root = new XElement("Extension", new XAttribute("version", BcXmlBody.XmlVersion));
        if (BinaryData.HasValue) root.Add(new XElement("binaryData", BinaryData.Value));
        if (ChannelId.HasValue) root.Add(new XElement("channelId", ChannelId.Value));
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
