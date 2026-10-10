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

    /// <summary>Raw &lt;WifiSignal&gt; XML (RSSI dBm in &lt;signal&gt;), or null on
    /// wired cameras / firmwares without the query (msg 115, empty request body).</summary>
    public static async Task<XElement?> GetWifiSignalAsync(this IBcCamera cam, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdWifiSignal,
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement("WifiSignal");
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

    /// <summary>The camera's own service-port table (msg 37): raw ServerPort,
    /// HttpPort, HttpsPort, RtspPort, RtmpPort and OnvifPort elements, each with
    /// a port number and an "enable" flag (absent on services the firmware can't
    /// toggle). Always asked of the camera live — never a cached value.</summary>
    public static async Task<List<XElement>?> GetServicePortsAsync(this IBcCamera cam,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdGetServicePorts, extension: ChannelExt(cam),
            replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        var ports = reply?.Xml?.Raw
            .Where(e => e.Name.LocalName.EndsWith("Port", StringComparison.Ordinal)).ToList();
        return ports is { Count: > 0 } ? ports : null;
    }

    /// <summary>Writes back one (modified) service element obtained from
    /// <see cref="GetServicePortsAsync"/> (msg 36) — the same read-modify-write
    /// the app's Port Settings screen performs. Some firmwares never acknowledge
    /// a successful set, so a missing reply is tolerated; callers re-read the
    /// table to verify.</summary>
    public static Task SetServicePortAsync(this IBcCamera cam, XElement service, CancellationToken ct = default) =>
        cam.SendCommandAsync(BcConstants.MsgIdSetServicePorts, BcXmlBody.FromRaw(service), ChannelExt(cam),
            replyTimeout: TimeSpan.FromMilliseconds(800), tolerateNoReply: true, ct: ct);

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

    /// <summary>Raw &lt;FloodlightTask&gt; XML (brightness, auto-mode, schedule), or null if unsupported.
    /// <paramref name="msgId"/> is 289 (the firmware's GET_FLOODLIGHT_TASK) or 438 (reference neolink's).</summary>
    public static async Task<XElement?> GetFloodlightTasksAsync(this IBcCamera cam, TimeSpan? timeout = null,
        CancellationToken ct = default, uint msgId = BcConstants.MsgIdFloodlightTasksGet)
    {
        var reply = await cam.SendCommandAsync(msgId, extension: ChannelExt(cam),
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

    // ------------------------------------------------------------------ device settings
    // Ids, roots and field names below come from the firmware's own command and
    // serializer tables (firmware-analysis/data/bc_ids.csv), not from captures.

    /// <summary>Raw &lt;<paramref name="root"/>&gt; from a channel-scoped read, or null when absent.</summary>
    public static async Task<XElement?> GetRawAsync(this IBcCamera cam, uint msgId, string root,
        XElement? body = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(msgId, body == null ? null : BcXmlBody.FromRaw(body),
            ChannelExt(cam), replyTimeout: timeout, ct: ct).ConfigureAwait(false);
        return reply?.Xml?.RawElement(root);
    }

    /// <summary>Writes an element with a set id; a silent camera counts as accepted.</summary>
    public static Task SetRawAsync(this IBcCamera cam, uint msgId, XElement element, CancellationToken ct = default,
        TimeSpan? replyTimeout = null) =>
        cam.SendCommandAsync(msgId, BcXmlBody.FromRaw(element), ChannelExt(cam),
            replyTimeout: replyTimeout ?? TimeSpan.FromMilliseconds(1500), tolerateNoReply: true, ct: ct);

    /// <summary>&lt;SystemGeneral&gt; (msg 104): device name, time zone and the camera's wall clock.</summary>
    public static async Task<XElement?> GetSystemGeneralAsync(this IBcCamera cam, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var reply = await cam.SendCommandAsync(BcConstants.MsgIdGetGeneral, replyTimeout: timeout, ct: ct)
            .ConfigureAwait(false);
        return reply?.Xml?.RawElement("SystemGeneral");
    }

    /// <summary>Writes back a (modified) &lt;SystemGeneral&gt; (msg 105).</summary>
    public static Task SetSystemGeneralAsync(this IBcCamera cam, XElement general, CancellationToken ct = default) =>
        cam.SendCommandAsync(BcConstants.MsgIdSetGeneral, BcXmlBody.FromRaw(general),
            replyTimeout: TimeSpan.FromSeconds(2), tolerateNoReply: true, ct: ct);

    /// <summary>A firmware time element: year, month, day, hour, minute, second children.</summary>
    internal static XElement TimeElement(string name, DateTime t) => new(name,
        new XElement("year", t.Year), new XElement("month", t.Month), new XElement("day", t.Day),
        new XElement("hour", t.Hour), new XElement("minute", t.Minute), new XElement("second", t.Second));

    /// <summary>Reads a time element (or the same fields directly on <paramref name="t"/>); null when invalid.</summary>
    internal static DateTime? ParseTime(XElement? t)
    {
        if (t == null) return null;
        int? F(string n) => int.TryParse(t.Element(n)?.Value.Trim(), out var v) ? v : null;
        try
        {
            return F("year") is { } y && F("month") is { } mo && F("day") is { } d
                ? new DateTime(y, mo, d, F("hour") ?? 0, F("minute") ?? 0, F("second") ?? 0)
                : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ------------------------------------------------------------------ SD card over Baichuan

    /// <summary>&lt;DayRecords&gt; calendar request (msg 142) for one channel over a time window.</summary>
    internal static XElement BuildDayRecords(byte channelId, DateTime start, DateTime end) =>
        new("DayRecords", new XAttribute("version", BcXmlBody.XmlVersion),
            TimeElement("startTime", start), TimeElement("endTime", end),
            new XElement("DayRecordList", new XElement("DayRecord", new XElement("channelId", channelId))));

    /// <summary>Days of the month with recordings from a &lt;DayRecords&gt; reply asked from the
    /// 1st: the firmware lists only recorded days, dayType "index" counting from 0.</summary>
    internal static IReadOnlyList<int> ParseDayRecords(XElement dayRecords, int year, int month)
    {
        int last = DateTime.DaysInMonth(year, month);
        return dayRecords.Descendants("dayType")
            .Where(d => d.Element("type")?.Value.Trim().ToLowerInvariant() is not (null or "" or "none"))
            .Select(d => int.TryParse(d.Element("index")?.Value.Trim(), out var i) ? i + 1 : 0)
            .Where(d => d >= 1 && d <= last)
            .Distinct().OrderBy(d => d).ToList();
    }

    /// <summary>Every record type the firmwares' file filters know (doorbells add visitor and
    /// package); each is matched as a substring, so a token a model lacks is ignored.</summary>
    internal const string AllRecordTypes = "manual,sched,md,pir,io,people,face,vehicle,other,dog_cat,visitor,package";

    /// <summary>&lt;FileInfoList&gt; file-search open (msg 14).</summary>
    internal static XElement BuildFileSearch(byte channelId, string streamType, DateTime start, DateTime end) =>
        new("FileInfoList", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("FileInfo",
                new XElement("channelId", channelId),
                new XElement("streamType", streamType),
                new XElement("recordType", AllRecordTypes),
                TimeElement("startTime", start), TimeElement("endTime", end)));

    /// <summary>&lt;FileInfoList&gt; carrying a search handle (msg 15 next page, msg 16 close).</summary>
    internal static XElement BuildFileHandle(byte channelId, int handle) =>
        new("FileInfoList", new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("FileInfo", new XElement("channelId", channelId), new XElement("handle", handle)));

    /// <summary>Download request (msg 8): the file's own search entry, echoed back.</summary>
    internal static XElement BuildDownload(XElement fileInfo) =>
        new("FileInfoList", new XAttribute("version", BcXmlBody.XmlVersion), new XElement(fileInfo));

    /// <summary>A file entry's size: sizeH/sizeL (high/low 32 bits), else fileSize.</summary>
    internal static long FileInfoSize(XElement f)
    {
        long.TryParse(f.Element("sizeL")?.Value.Trim(), out var lo);
        long.TryParse(f.Element("sizeH")?.Value.Trim(), out var hi);
        if (lo > 0 || hi > 0) return (hi << 32) | (lo & 0xffffffffL);
        return long.TryParse(f.Element("fileSize")?.Value.Trim(), out var size) ? size : 0;
    }

    /// <summary>The named entries of a search reply, as (raw entry, name, start, end, size).</summary>
    internal static IEnumerable<(XElement Raw, string Name, DateTime Start, DateTime End, long Size)> ParseFileInfos(XElement? list)
    {
        if (list == null) yield break;
        foreach (var f in list.Descendants("FileInfo"))
        {
            var name = f.Element("name")?.Value.Trim() ?? "";
            if (name.Length == 0) continue;
            yield return (f, name, ParseTime(f.Element("startTime")) ?? default,
                ParseTime(f.Element("endTime")) ?? default, FileInfoSize(f));
        }
    }

    /// <summary>The search handle a msg-14 reply hands out, or null.</summary>
    internal static int? SearchHandle(XElement? list) =>
        list?.Descendants("handle").Select(h => int.TryParse(h.Value.Trim(), out var v) ? v : (int?)null)
            .FirstOrDefault(v => v != null);

    /// <summary>Closes a file search; best-effort.</summary>
    public static Task CloseFileSearchAsync(this IBcCamera cam, int handle, CancellationToken ct = default) =>
        cam.SendCommandAsync(BcConstants.MsgIdSearchClose, BcXmlBody.FromRaw(BuildFileHandle(cam.ChannelId, handle)),
            ChannelExt(cam), replyTimeout: TimeSpan.FromSeconds(2), tolerateNoReply: true, ct: ct);
}
