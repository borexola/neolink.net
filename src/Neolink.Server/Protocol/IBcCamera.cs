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
