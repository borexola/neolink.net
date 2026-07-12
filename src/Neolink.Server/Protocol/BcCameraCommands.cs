// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
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

    /// <summary>
    /// The audio profile the camera accepts for two-way talk (msg 10), or null when
    /// the camera has no speaker / doesn't support talk.
    /// </summary>
    public static async Task<TalkAbilityXml?> GetTalkAbilityAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdTalkAbility, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        var el = reply?.Xml?.RawElement("TalkAbility");
        return el == null ? null : TalkAbilityXml.Parse(el);
    }

    // ------------------------------------------------------------------ zoom & focus

    public static readonly string[] ZoomFocusCommands = { "zoomPos", "focusPos" };

    /// <summary>Raw &lt;PtzZoomFocus&gt; XML (zoom/focus positions and their ranges), or null if unsupported.</summary>
    public static async Task<XElement?> GetZoomFocusAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdGetZoomFocus, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("PtzZoomFocus");
    }

    /// <summary>&lt;StartZoomFocus&gt; body — wire format per the reference Rust neolink.</summary>
    internal static XElement BuildStartZoomFocus(byte channelId, string command, uint movePos) =>
        new("StartZoomFocus", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", channelId),
            new XElement("command", command),
            new XElement("movePos", movePos));

    /// <summary>Drives the optical zoom ("zoomPos") or focus ("focusPos") to an absolute position.</summary>
    public static async Task SetZoomFocusAsync(this IBcCamera cam, string command, uint movePos, CancellationToken ct = default)
    {
        if (!ZoomFocusCommands.Contains(command))
            throw new ArgumentException($"Unknown zoom/focus command '{command}' (expected one of: {string.Join(", ", ZoomFocusCommands)})");
        await cam.SendCommandAsync(BcConstants.MsgIdSetZoomFocus,
            BcXmlBody.FromRaw(BuildStartZoomFocus(cam.ChannelId, command, movePos)), ChannelExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(800), tolerateNoReply: true, ct: ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ siren

    /// <summary>&lt;audioPlayInfo&gt; body — the field values the reference Rust neolink sends for one siren burst.</summary>
    internal static XElement BuildAudioAlarmPlay(byte channelId) =>
        new("audioPlayInfo",
            new XElement("channelId", channelId),
            new XElement("playMode", 0),
            new XElement("playDuration", 0),
            new XElement("playTimes", 1),
            new XElement("onOff", 0));

    /// <summary>&lt;audioPlayInfo&gt; manual mode — playMode 2 with onOff as the latch,
    /// exactly as Home Assistant's reolink library (reolink_aio) sends it: the siren
    /// sounds until switched off.</summary>
    internal static XElement BuildAudioAlarmManual(byte channelId, bool on) =>
        new("audioPlayInfo", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", channelId),
            new XElement("playMode", 2),
            new XElement("playDuration", 10),
            new XElement("playTimes", 1),
            new XElement("onOff", on ? 1 : 0));

    /// <summary>Sounds the camera's siren once (msg 263). The camera must answer 200.</summary>
    public static async Task SirenBurstAsync(this IBcCamera cam, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdPlayAudio,
            BcXmlBody.FromRaw(BuildAudioAlarmPlay(cam.ChannelId)), ChannelExt(cam), ct: ct).ConfigureAwait(false);
    }

    /// <summary>Latches the siren on (sounds until stopped) or off (msg 263, manual mode).</summary>
    public static async Task SirenManualAsync(this IBcCamera cam, bool on, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdPlayAudio,
            BcXmlBody.FromRaw(BuildAudioAlarmManual(cam.ChannelId, on)), ChannelExt(cam), ct: ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ privacy mode

    /// <summary>&lt;sleepState&gt; write body — privacy mode on/off, exactly as
    /// Home Assistant's reolink library sends it (operate 2 = set).</summary>
    internal static XElement BuildSleepState(bool on) =>
        new("sleepState", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("operate", 2),
            new XElement("sleep", on ? 1 : 0));

    /// <summary>Whether privacy mode (camera "sleep": no video, lens dark) is on —
    /// null when the camera doesn't answer the query (msg 574).</summary>
    public static async Task<bool?> GetPrivacyModeAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdSleepState, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return ParseSleepValue(reply?.Xml);
    }

    /// <summary>The &lt;sleep&gt; boolean wherever the reply nests it (firmwares vary).</summary>
    internal static bool? ParseSleepValue(BcXmlBody? xml)
    {
        if (xml == null) return null;
        foreach (var root in xml.Raw)
        {
            var el = root.Name.LocalName == "sleep" ? root : root.Descendants("sleep").FirstOrDefault();
            if (el == null) continue;
            var v = el.Value.Trim().ToLowerInvariant();
            if (v.Length > 0) return v is "1" or "true" or "sleep" or "sleeping";
        }
        return null;
    }

    /// <summary>Turns privacy mode on (camera goes dark) or off (msg 575 — NOT 623:
    /// that id carries the state pushes, which our status watcher owns).</summary>
    public static async Task SetPrivacyModeAsync(this IBcCamera cam, bool on, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdSetSleepState,
            BcXmlBody.FromRaw(BuildSleepState(on)), ChannelExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(800), tolerateNoReply: true, ct: ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ floodlight

    /// <summary>Raw &lt;FloodlightTask&gt; XML (brightness, auto-mode, schedule), or null if unsupported.</summary>
    public static async Task<XElement?> GetFloodlightTasksAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdFloodlightTasksRead, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("FloodlightTask");
    }

    /// <summary>Writes back a (modified) &lt;FloodlightTask&gt; obtained from <see cref="GetFloodlightTasksAsync"/>.</summary>
    public static async Task SetFloodlightTasksAsync(this IBcCamera cam, XElement task, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdFloodlightTasksWrite, BcXmlBody.FromRaw(task), ChannelExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(800), tolerateNoReply: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>&lt;FloodlightManual&gt; body — wire format per the reference Rust neolink (version "1").</summary>
    internal static XElement BuildFloodlightManual(byte channelId, bool on, int durationSeconds) =>
        new("FloodlightManual", new XAttribute("version", "1"),
            new XElement("channelId", channelId),
            new XElement("status", on ? 1 : 0),
            new XElement("duration", durationSeconds));

    /// <summary>Manually turns the floodlight on (for a duration) or off (msg 288).</summary>
    public static async Task FloodlightManualAsync(this IBcCamera cam, bool on, int durationSeconds, CancellationToken ct = default)
    {
        await cam.SendCommandAsync(BcConstants.MsgIdFloodlightManual,
            BcXmlBody.FromRaw(BuildFloodlightManual(cam.ChannelId, on, durationSeconds)), ChannelExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(500), tolerateNoReply: true, ct: ct).ConfigureAwait(false);
    }
}
