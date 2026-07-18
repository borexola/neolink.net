// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
namespace Neolink.Bc;

/// <summary>Constants and models for the Baichuan ("BC") protocol used by Reolink cameras on port 9000.</summary>
public static class BcConstants
{
    public const uint MagicHeader = 0x0abcdef0;

    public const uint MsgIdLogin = 1;
    public const uint MsgIdLogout = 2;
    public const uint MsgIdVideo = 3;
    public const uint MsgIdTalkAbility = 10;
    public const uint MsgIdTalkReset = 11;
    public const uint MsgIdPtzControl = 18;
    public const uint MsgIdPtzPreset = 19;
    public const uint MsgIdReboot = 23;
    public const uint MsgIdMotionRequest = 31;
    public const uint MsgIdMotion = 33;
    public const uint MsgIdGetAbilitySupport = 58;
    public const uint MsgIdVersion = 80;
    public const uint MsgIdPing = 93;
    public const uint MsgIdGetGeneral = 104;
    public const uint MsgIdSetGeneral = 105;
    public const uint MsgIdSnap = 109;
    public const uint MsgIdStreamInfoList = 146;
    public const uint MsgIdAbilityInfo = 151;
    public const uint MsgIdGetSupport = 199;
    public const uint MsgIdTalkConfig = 201;
    public const uint MsgIdTalk = 202;
    public const uint MsgIdGetLedStatus = 208;
    public const uint MsgIdSetLedStatus = 209;
    public const uint MsgIdGetPirAlarm = 212;
    public const uint MsgIdSetPirAlarm = 213;
    public const uint MsgIdUdpKeepAlive = 234;
    public const uint MsgIdBatteryInfoList = 252;
    public const uint MsgIdBatteryInfo = 253;
    public const uint MsgIdPlayAudio = 263;            // "audioPlayInfo" — manual siren trigger
    public const uint MsgIdFloodlightManual = 288;
    public const uint MsgIdFloodlightTasksWrite = 290;
    public const uint MsgIdFloodlightStatusList = 291;
    public const uint MsgIdGetZoomFocus = 294;
    public const uint MsgIdSetZoomFocus = 295;
    public const uint MsgIdFloodlightTasksRead = 438;
    public const uint MsgIdNetInfo = 464;
    public const uint MsgIdSleepState = 574;           // privacy-mode read (reply carries <sleep>)
    public const uint MsgIdSetSleepState = 575;        // privacy-mode write (<sleepState> body)
    public const uint MsgIdSirenStatusList = 547;
    public const uint MsgIdSmartAiEventList = 600; // "yoloWorldEventList" pushes on newer firmware
    public const uint MsgIdSleepStatus = 623;      // privacy-mode pushes AND write (<sleepState>)

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
    private System.Security.Cryptography.Aes? _aes; // keyed once per session; see EcbEncrypt

    public (EncryptionKind kind, byte[]? aesKey) Snapshot()
    {
        lock (_gate) return (_kind, _aesKey);
    }

    public void Set(EncryptionKind kind, byte[]? aesKey = null)
    {
        lock (_gate)
        {
            _kind = kind;
            if (aesKey != null || kind is not (EncryptionKind.Aes or EncryptionKind.FullAes))
            {
                _aesKey = aesKey;
                _aes?.Dispose();
                _aes = null;
            }
        }
    }

    public void SetAesKey(byte[] key)
    {
        lock (_gate)
        {
            _aesKey = key;
            _aes?.Dispose();
            _aes = null;
        }
    }

    /// <summary>
    /// One-shot AES-128-ECB over block-aligned input with the session key. The keyed
    /// AES instance (whose key schedule is the expensive part) lives for the whole
    /// session instead of being rebuilt per message — under FullAes this runs for
    /// every media message, so that rebuild used to dominate the protocol layer's
    /// CPU. Serialized by the gate: the read loop is the only heavy caller, and an
    /// occasional outgoing command holds it for microseconds.
    /// </summary>
    public void EcbEncrypt(ReadOnlySpan<byte> input, Span<byte> output)
    {
        lock (_gate)
        {
            if (_aesKey == null)
                throw new InvalidOperationException("AES key not negotiated");
            if (_aes == null)
            {
                _aes = System.Security.Cryptography.Aes.Create();
                _aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                _aes.Padding = System.Security.Cryptography.PaddingMode.None;
                _aes.Key = _aesKey;
            }
            _aes.EncryptEcb(input, output, System.Security.Cryptography.PaddingMode.None);
        }
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
