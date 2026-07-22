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

    /// <summary>The camera's resolved IP address once connected (null before), so a
    /// UID-only battery camera can be liveness-scanned by ping without waking it.</summary>
    System.Net.IPAddress? RemoteIp { get; }

    Task LoginAsync(string username, string? password, CancellationToken ct);

    /// <summary>
    /// Requests the video stream and pumps the raw binary sub-stream chunks into
    /// <paramref name="binaryOut"/> until cancelled or the connection drops.
    /// When <paramref name="stallTolerable"/> returns true, a 15s no-video gap is
    /// treated as expected (the camera is dark on purpose, e.g. privacy mode) and
    /// the connection is held open instead of failing — so control stays available.
    /// </summary>
    Task StartVideoAsync(StreamKind stream, ChannelWriter<byte[]> binaryOut,
        Func<bool>? stallTolerable, CancellationToken ct);

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
    /// Two-way talk: configures the camera's speaker for the given audio profile
    /// (msg 201, retried once after a reset if another talker holds the channel),
    /// then forwards each BcMedia-framed ADPCM frame from <paramref name="frames"/>
    /// as a binary talk message (msg 202) until the channel completes or
    /// <paramref name="ct"/> fires. Always releases the talk channel (msg 11) on
    /// the way out. Frames are produced with <see cref="Media.AdpcmEncoder"/> +
    /// <see cref="Media.BcMediaAdpcm"/> per the profile from
    /// <see cref="BcCameraCommands.GetTalkAbilityAsync"/>.
    /// </summary>
    Task TalkAsync(TalkAbilityXml ability, ChannelReader<byte[]> frames, CancellationToken ct);

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
/// External marks a push the CAMERA never sent: an outside system (the Home
/// Assistant "Record" switch) commanding recording. External pushes bypass the
/// per-camera event-type filter and capture schedule — they are explicit user
/// intent, not a detection to be second-guessed — but still respect the master
/// events on/off switch.
/// </summary>
public sealed record MotionPush(string Status, IReadOnlyList<string> AiTypes, bool External = false)
{
    /// <summary>True when this push signals active detection (as opposed to all-clear).</summary>
    public bool Active => !string.Equals(Status, "none", StringComparison.OrdinalIgnoreCase) || AiTypes.Count > 0;
}

/// <summary>
/// One unsolicited status push from the camera (see <see cref="IBcCamera.WatchStatusAsync"/>).
/// </summary>
public abstract record StatusPush;

/// <summary>msg 464 NetInfo: connection type and Wi-Fi signal strength (RSSI, dBm).</summary>
/// <summary>msg 464 NetInfo. <paramref name="SignalDbm"/> is null on a WIRED camera —
/// it still announces its link type, which is how a camera on a cable is told apart
/// from one whose Wi-Fi reading simply hasn't arrived yet.</summary>
public sealed record WifiSignalPush(string? NetType, int? SignalDbm) : StatusPush;

/// <summary>msg 623 sleepStatus: battery cameras report entering/leaving power-save sleep.</summary>
public sealed record SleepStatusPush(bool Sleeping) : StatusPush;

/// <summary>msg 547 SirenStatusList: the siren switched on or off.</summary>
public sealed record SirenStatusPush(bool On) : StatusPush;

/// <summary>msg 291 FloodlightStatusList: the floodlight switched on or off.</summary>
public sealed record FloodlightStatusPush(bool On) : StatusPush;

/// <summary>
/// Battery level, from the msg 252 BatteryList push or a msg 253 BatteryInfo
/// query. Charging is true whenever a power source (adapter/solar) is attached.
/// </summary>
public sealed record BatteryPush(int Percent, bool Charging) : StatusPush;

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
