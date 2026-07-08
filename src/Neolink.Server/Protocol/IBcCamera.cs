// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
using System.Threading.Channels;
using Neolink.Bc;
using Neolink.Bc.Xml;

namespace Neolink.Protocol;

/// <summary>
/// A Baichuan camera session: login, video streaming, keep-alive, and generic
/// control commands. Typed command helpers build on <see cref="SendCommandAsync"/>
/// in <see cref="BcCameraCommands"/>.
/// </summary>
public interface IBcCamera : IAsyncDisposable
{
    byte ChannelId { get; }

    /// <summary>Resolution info the camera reported at login, if any.</summary>
    DeviceInfoXml? DeviceInfo { get; }

    Task LoginAsync(string username, string? password, CancellationToken ct);

    /// <summary>
    /// Requests the video stream and pumps the raw binary sub-stream chunks into
    /// <paramref name="binaryOut"/> until cancelled or the connection drops.
    /// </summary>
    Task StartVideoAsync(StreamKind stream, ChannelWriter<byte[]> binaryOut, CancellationToken ct);

    Task PingAsync(CancellationToken ct);

    /// <summary>
    /// Opts in to motion/AI alarm pushes (msg 31) and invokes <paramref name="onEvent"/>
    /// for every push (msg 33) until cancelled or the connection drops. The camera sends
    /// these on its own; there is no polling.
    /// </summary>
    Task WatchMotionAsync(Action<MotionPush> onEvent, CancellationToken ct);

    /// <summary>
    /// Listens for the unsolicited status pushes the camera broadcasts on its own —
    /// Wi-Fi signal (msg 464), sleep state (msg 623), siren (msg 547) and floodlight
    /// (msg 291) — and invokes <paramref name="onEvent"/> for each one it can parse,
    /// until cancelled or the connection drops. Unlike motion, no opt-in is needed.
    /// </summary>
    Task WatchStatusAsync(Action<StatusPush> onEvent, CancellationToken ct);

    /// <summary>Requests a JPEG snapshot from the camera (msg 109), or null if unsupported.</summary>
    Task<byte[]?> SnapAsync(CancellationToken ct);

    /// <summary>
    /// Sends one control message and awaits the camera's reply on the same message ID.
    /// Throws <see cref="CameraCommandException"/> when the camera answers with a
    /// non-200 response code. With <paramref name="tolerateNoReply"/> a missing reply
    /// yields null instead of a timeout — some cameras simply don't acknowledge
    /// accepted "set" commands.
    /// Callers must not issue concurrent commands with the same message ID.
    /// </summary>
    Task<BcMessage?> SendCommandAsync(uint msgId, BcXmlBody? xml = null, ExtensionXml? extension = null,
        TimeSpan? replyTimeout = null, bool tolerateNoReply = false, CancellationToken ct = default);
}

/// <summary>
/// One alarm push from the camera. Status is the raw detection state ("MD" while
/// motion is seen, "none" when it stops); AiTypes carries the camera's own AI
/// classification when it has one ("people", "vehicle", "dog_cat").
/// </summary>
public sealed record MotionPush(string Status, IReadOnlyList<string> AiTypes)
{
    /// <summary>True when this push signals active detection (as opposed to all-clear).</summary>
    public bool Active => !string.Equals(Status, "none", StringComparison.OrdinalIgnoreCase) || AiTypes.Count > 0;
}

/// <summary>
/// One unsolicited status push from the camera (see <see cref="IBcCamera.WatchStatusAsync"/>).
/// </summary>
public abstract record StatusPush;

/// <summary>msg 464 NetInfo: connection type and Wi-Fi signal strength (RSSI, dBm).</summary>
public sealed record WifiSignalPush(string? NetType, int SignalDbm) : StatusPush;

/// <summary>msg 623 sleepStatus: battery cameras report entering/leaving power-save sleep.</summary>
public sealed record SleepStatusPush(bool Sleeping) : StatusPush;

/// <summary>msg 547 SirenStatusList: the siren switched on or off.</summary>
public sealed record SirenStatusPush(bool On) : StatusPush;

/// <summary>msg 291 FloodlightStatusList: the floodlight switched on or off.</summary>
public sealed record FloodlightStatusPush(bool On) : StatusPush;

/// <summary>The camera answered a control command with a non-200 response code.</summary>
public sealed class CameraCommandException : Exception
{
    public uint MsgId { get; }
    public ushort ResponseCode { get; }

    public CameraCommandException(uint msgId, ushort responseCode)
        : base($"Camera rejected command {msgId} (response code {responseCode})")
    {
        MsgId = msgId;
        ResponseCode = responseCode;
    }
}
