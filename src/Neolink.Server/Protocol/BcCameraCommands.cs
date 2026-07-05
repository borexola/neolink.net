using System.Xml.Linq;
using Neolink.Bc;
using Neolink.Bc.Xml;

namespace Neolink.Protocol;

/// <summary>
/// Typed control commands over <see cref="IBcCamera.SendCommandAsync"/>. Message IDs,
/// request shapes and reply handling follow the reference Rust neolink implementation.
/// Settings ("set") commands use read-modify-write of the camera's own XML so unknown
/// fields and element order are preserved.
/// </summary>
public static class BcCameraCommands
{
    public static readonly string[] PtzCommands = { "up", "down", "left", "right", "stop" };

    private static ExtensionXml ChannelExt(IBcCamera cam) => new() { ChannelId = cam.ChannelId };
    private static ExtensionXml RfExt(IBcCamera cam) => new() { RfId = cam.ChannelId };

    /// <summary>Feature flags the camera advertises (raw &lt;Support&gt; XML), or null if unsupported.</summary>
    public static async Task<XElement?> GetSupportAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdGetSupport, replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("Support");
    }

    public static async Task<VersionInfoXml?> GetVersionAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdVersion, replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.VersionInfo;
    }

    /// <summary>The encode profiles (resolution/framerate/bitrate tables) of each stream.</summary>
    public static async Task<StreamInfoListXml?> GetStreamInfoAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdStreamInfoList, replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.StreamInfoList;
    }

    /// <summary>Raw &lt;BatteryInfo&gt; XML (percent, charge status, ...), or null if the camera has no battery.</summary>
    public static async Task<XElement?> GetBatteryInfoAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdBatteryInfo, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("BatteryInfo");
    }

    /// <summary>Raw &lt;LedState&gt; XML: status LED ("state") and floodlight ("lightState").</summary>
    public static async Task<XElement?> GetLedStateAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdGetLedStatus, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("LedState");
    }

    /// <summary>Writes back a (modified) &lt;LedState&gt; element obtained from <see cref="GetLedStateAsync"/>.</summary>
    public static async Task SetLedStateAsync(this IBcCamera cam, XElement ledState, CancellationToken ct = default)
    {
        // ledVersion is reported by the camera but must not be echoed back.
        ledState.Element("ledVersion")?.Remove();
        await cam.SendCommandAsync(BcConstants.MsgIdSetLedStatus, BcXmlBody.FromRaw(ledState), ChannelExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(800), tolerateNoReply: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Raw &lt;RfAlarmCfg&gt; XML (PIR motion sensor settings), or null if unsupported.</summary>
    public static async Task<XElement?> GetPirStateAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdGetPirAlarm, extension: RfExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("rfAlarmCfg") ?? reply?.Xml?.RawElement("RfAlarmCfg");
    }

    /// <summary>Writes back a (modified) PIR config element obtained from <see cref="GetPirStateAsync"/>.</summary>
    public static async Task SetPirStateAsync(this IBcCamera cam, XElement rfAlarmCfg, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdSetPirAlarm, BcXmlBody.FromRaw(rfAlarmCfg), RfExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(800), tolerateNoReply: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Starts a PTZ movement (or stops it with command "stop").</summary>
    public static async Task PtzAsync(this IBcCamera cam, string command, float speed = 32, CancellationToken ct = default)
    {
        if (!PtzCommands.Contains(command))
            throw new ArgumentException($"Unknown PTZ command '{command}' (expected one of: {string.Join(", ", PtzCommands)})");
        var body = new BcXmlBody
        {
            PtzControl = new PtzControlXml { ChannelId = cam.ChannelId, Speed = speed, Command = command },
        };
        await cam.SendCommandAsync(BcConstants.MsgIdPtzControl, body, ChannelExt(cam), ct: ct).ConfigureAwait(false);
    }

    public static async Task RebootAsync(this IBcCamera cam, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdReboot, ct: ct).ConfigureAwait(false);
    }
}
