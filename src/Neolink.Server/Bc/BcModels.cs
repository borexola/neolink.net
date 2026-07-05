namespace Neolink.Bc;

/// <summary>Constants and models for the Baichuan ("BC") protocol used by Reolink cameras on port 9000.</summary>
public static class BcConstants
{
    public const uint MagicHeader = 0x0abcdef0;

    public const uint MsgIdLogin = 1;
    public const uint MsgIdLogout = 2;
    public const uint MsgIdVideo = 3;
    public const uint MsgIdReboot = 23;
    public const uint MsgIdMotionRequest = 31;
    public const uint MsgIdMotion = 33;
    public const uint MsgIdVersion = 80;
    public const uint MsgIdPing = 93;
    public const uint MsgIdGetGeneral = 104;
    public const uint MsgIdSetGeneral = 105;
    public const uint MsgIdUdpKeepAlive = 234;

    // Message classes. The class dictates the header size:
    //  0x6514: legacy, 20 byte header (initial login)
    //  0x6614: modern, 20 byte header (reply to the legacy login)
    //  0x6414: modern, 24 byte header (has payload offset)
    //  0x0000: modern, 24 byte header (has payload offset)
    public const ushort ClassLegacy = 0x6514;
    public const ushort ClassModernNoOffset = 0x6614;
    public const ushort ClassModern = 0x6414;
    public const ushort ClassModernZero = 0x0000;

    // Legacy "login upgrade" request: the response_code advertises the highest
    // encryption scheme the client supports. The camera replies with the scheme it
    // will actually use plus a login nonce. Advertise AES: modern (post-2021)
    // cameras are AES-only and drop the connection if the client can't do AES.
    public const ushort LegacyUpgradeNone = 0xdc00;
    public const ushort LegacyUpgradeBcEncrypt = 0xdc01;
    public const ushort LegacyUpgradeAes = 0xdc12;

    /// <summary>An empty password in the legacy login format: 32 NUL bytes.</summary>
    public static readonly string EmptyLegacyPassword = new('\0', 32);

    public static bool HasPayloadOffset(ushort cls) => cls == ClassModern || cls == ClassModernZero;
    public static bool IsModernClass(ushort cls) => cls != ClassLegacy;
}

/// <summary>The negotiated payload encryption scheme.</summary>
public enum EncryptionKind
{
    /// <summary>Older cameras use no encryption.</summary>
    Unencrypted,
    /// <summary>Cameras/firmwares before ~2021 use a simple XOR scheme.</summary>
    BcEncrypt,
    /// <summary>Newer cameras use AES-128-CFB with a key derived from password + nonce.</summary>
    Aes,
    /// <summary>Same as AES, but the media (binary) stream is also encrypted, not just control XML.</summary>
    FullAes,
}

public sealed class EncryptionState
{
    private readonly object _gate = new();
    private EncryptionKind _kind = EncryptionKind.Unencrypted;
    private byte[]? _aesKey;

    public (EncryptionKind kind, byte[]? aesKey) Snapshot()
    {
        lock (_gate) return (_kind, _aesKey);
    }

    public void Set(EncryptionKind kind, byte[]? aesKey = null)
    {
        lock (_gate)
        {
            _kind = kind;
            if (aesKey != null || kind is not (EncryptionKind.Aes or EncryptionKind.FullAes)) _aesKey = aesKey;
        }
    }

    public void SetAesKey(byte[] key)
    {
        lock (_gate) { _aesKey = key; }
    }
}

/// <summary>The application-level metadata of a BC message (mirrors Rust's BcMeta).</summary>
public sealed record BcMeta
{
    public uint MsgId { get; init; }
    public byte ChannelId { get; init; }
    public byte StreamType { get; init; }
    public ushort ResponseCode { get; init; }
    public ushort MsgNum { get; init; }
    public ushort Class { get; init; }
}

/// <summary>A complete BC message.</summary>
public sealed class BcMessage
{
    public required BcMeta Meta { get; init; }

    // Modern body:
    public Xml.ExtensionXml? Extension { get; set; }
    public Xml.BcXmlBody? Xml { get; set; }
    public byte[]? Binary { get; set; }

    // Legacy body (only login is supported):
    public string? LegacyUsername { get; set; }
    public string? LegacyPassword { get; set; }

    public bool IsEmptyModern => Extension == null && Xml == null && Binary == null;

    public static BcMessage FromXml(BcMeta meta, Xml.BcXmlBody xml) => new() { Meta = meta, Xml = xml };
    public static BcMessage HeaderOnly(BcMeta meta) => new() { Meta = meta };
}

/// <summary>Per-connection deserialization state (mirrors Rust's BcContext).</summary>
public sealed class BcContext
{
    /// <summary>Message numbers whose payloads have switched to binary mode.</summary>
    public HashSet<ushort> InBinMode { get; } = new();

    public EncryptionState Encryption { get; }

    public BcContext(EncryptionState encryption) => Encryption = encryption;
}
