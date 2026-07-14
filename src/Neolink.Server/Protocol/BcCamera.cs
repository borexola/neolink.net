// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// The Baichuan protocol format and the encryption/decryption schemes implemented
// in this file derive from the reverse-engineering work of the original Neolink
// project by George Hilliard (github.com/thirtythreeforty/neolink) and its
// actively maintained fork by @QuantumEntangledAndy
// (github.com/QuantumEntangledAndy/neolink).
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Neolink.Bc;
using Neolink.Bc.Xml;

namespace Neolink.Protocol;

public enum StreamKind
{
    Main,
    Sub,
    Extern,
}

public sealed class AuthFailedException : Exception
{
    public AuthFailedException(string msg) : base(msg) { }
}

/// <summary>High-level camera operations: login, video streaming, ping, control commands.</summary>
public sealed class BcCamera : IBcCamera
{
    public static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(15);

    private readonly BcConnection _conn;
    private readonly byte _channelId;
    private int _messageNum = -1;
    private bool _oddMotionPushLogged; // one diagnostic per connection is plenty

    public byte ChannelId => _channelId;
    public DeviceInfoXml? DeviceInfo { get; private set; }

    // Log tag: with several cameras every one is usually channel 0, so a bare
    // "ch0" is ambiguous in captures — callers pass the camera name instead.
    private readonly string _logTag;

    private BcCamera(BcConnection conn, byte channelId, string? tag)
    {
        _conn = conn;
        _channelId = channelId;
        _logTag = tag ?? $"ch{channelId}";
    }

    public static async Task<BcCamera> ConnectAsync(string host, int port, byte channelId, CancellationToken ct,
        string? tag = null)
    {
        var conn = await BcConnection.ConnectAsync(host, port, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return new BcCamera(conn, channelId, tag);
    }

    private ushort NewMessageNum() => (ushort)Interlocked.Increment(ref _messageNum);

    /// <summary>
    /// Login flow: send a header-only legacy "login upgrade" advertising AES support,
    /// receive the negotiated encryption scheme + nonce, then send a modern XML login
    /// with nonce-salted MD5 credentials. On AES cameras, derive the session key.
    /// </summary>
    public async Task LoginAsync(string username, string? password, CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdLogin);

        // Header-only "login upgrade": no legacy username/password body. The camera
        // replies with the encryption scheme it will use and a login nonce.
        var legacy = new BcMessage
        {
            Meta = new BcMeta
            {
                MsgId = BcConstants.MsgIdLogin,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = BcConstants.LegacyUpgradeAes,
                Class = BcConstants.ClassLegacy,
            },
        };
        await _conn.SendAsync(legacy, ct).ConfigureAwait(false);

        var reply = await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
        var nonce = reply.Xml?.Encryption?.Nonce
            ?? throw new BcProtocolException("Expected an Encryption message in the login reply");

        // The reply's response-code low byte is the negotiated scheme:
        // 0x00 = none, 0x01 = BCEncrypt, anything else (e.g. 0x12) = AES.
        int negotiated = reply.Meta.ResponseCode & 0xff;

        var modernPassword = password ?? "";
        var modern = BcMessage.FromXml(
            new BcMeta
            {
                MsgId = BcConstants.MsgIdLogin,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            new BcXmlBody
            {
                LoginUser = new LoginUserXml
                {
                    UserName = Md5Utils.Md5String31(username + nonce, zeroLast: false),
                    Password = Md5Utils.Md5String31(modernPassword + nonce, zeroLast: false),
                    UserVer = 1,
                },
                LoginNet = new LoginNetXml { Type = "LAN", UdpPort = 0 },
            });
        await _conn.SendAsync(modern, ct).ConfigureAwait(false);

        var modernReply = await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
        if (modernReply.Xml?.DeviceInfo != null)
        {
            DeviceInfo = modernReply.Xml.DeviceInfo;
        }
        else if (modernReply.IsEmptyModern)
        {
            throw new AuthFailedException("Camera rejected the credentials");
        }
        // else: some cameras reply with other XML; treat as success if response code is 200
        else if (modernReply.Meta.ResponseCode != 200)
        {
            throw new BcProtocolException($"Unexpected login reply (response code {modernReply.Meta.ResponseCode})");
        }

        // Now that the handshake is complete and the nonce is known, switch the
        // connection to AES for all subsequent messages if the camera negotiated it.
        // 0x02 = AES on control XML only; anything higher (e.g. 0x12) = FullAes,
        // where the media stream is encrypted too.
        if (negotiated != 0x00 && negotiated != 0x01)
        {
            var kind = negotiated == 0x02 ? EncryptionKind.Aes : EncryptionKind.FullAes;
            _conn.Encryption.Set(kind, Md5Utils.MakeAesKey(nonce, password ?? ""));
        }
    }

    /// <summary>
    /// Requests the video stream and pumps the raw binary sub-stream chunks into
    /// <paramref name="binaryOut"/> until cancelled or the connection drops.
    /// </summary>
    public async Task StartVideoAsync(StreamKind stream, ChannelWriter<byte[]> binaryOut,
        Func<bool>? stallTolerable, CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdVideo);

        // Stream codes/handles as used by the official clients
        byte streamCode = stream == StreamKind.Sub ? (byte)1 : (byte)0;
        uint handle = stream switch
        {
            StreamKind.Main => 0u,
            StreamKind.Sub => 256u,
            StreamKind.Extern => 1024u,
            _ => 0u,
        };
        string streamName = stream switch
        {
            StreamKind.Main => "mainStream",
            StreamKind.Sub => "subStream",
            StreamKind.Extern => "externStream",
            _ => "mainStream",
        };

        var startVideo = BcMessage.FromXml(
            new BcMeta
            {
                MsgId = BcConstants.MsgIdVideo,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = streamCode,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            new BcXmlBody
            {
                Preview = new PreviewXml
                {
                    ChannelId = _channelId,
                    Handle = handle,
                    StreamType = streamName,
                },
            });
        await _conn.SendAsync(startVideo, ct).ConfigureAwait(false);

        try
        {
            bool holding = false;
            while (!ct.IsCancellationRequested)
            {
                BcMessage msg;
                try
                {
                    msg = await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // No video for 15s. If the camera is dark on purpose (privacy
                    // mode), that is expected — keep the connection (and thus control
                    // commands, e.g. turning privacy off) alive instead of tearing it
                    // down and reconnecting in a loop. A genuine disconnect surfaces
                    // as a socket error, not a timeout, so it still reconnects.
                    if (stallTolerable?.Invoke() == true)
                    {
                        if (!holding)
                        {
                            holding = true;
                            Log.Debug($"{_logTag}: no video — camera is in privacy mode; holding the connection");
                        }
                        continue;
                    }
                    throw new IOException("Video stream stalled (no data for 15s)");
                }
                if (holding)
                {
                    holding = false;
                    Log.Debug($"{_logTag}: privacy mode off — video resumed");
                }
                if (msg.Binary is { Length: > 0 } bin)
                    await binaryOut.WriteAsync(bin, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            binaryOut.TryComplete();
        }
    }

    /// <summary>
    /// One-shot control command: subscribe to the message ID, send, await the reply.
    /// Runs safely alongside an active video stream (the connection routes replies
    /// by message ID), but concurrent commands with the SAME ID are not allowed —
    /// callers serialize (see CameraControl).
    /// </summary>
    public async Task<BcMessage?> SendCommandAsync(uint msgId, BcXmlBody? xml = null, ExtensionXml? extension = null,
        TimeSpan? replyTimeout = null, bool tolerateNoReply = false, CancellationToken ct = default)
    {
        using var sub = _conn.Subscribe(msgId);
        var msg = new BcMessage
        {
            Meta = new BcMeta
            {
                MsgId = msgId,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            Extension = extension,
            Xml = xml,
        };
        await _conn.SendAsync(msg, ct).ConfigureAwait(false);

        try
        {
            var reply = await sub.ReceiveAsync(replyTimeout ?? RxTimeout, ct).ConfigureAwait(false);
            if (reply.Meta.ResponseCode != 200)
                throw new CameraCommandException(msgId, reply.Meta.ResponseCode);
            return reply;
        }
        catch (TimeoutException) when (tolerateNoReply)
        {
            // Some cameras never acknowledge accepted set commands.
            return null;
        }
    }

    public async Task WatchMotionAsync(Action<MotionPush> onEvent, CancellationToken ct)
    {
        // Subscribe BEFORE opting in so no push can slip past between the two.
        using var sub = _conn.Subscribe(BcConstants.MsgIdMotion);

        // Opt in to alarm pushes. Some cameras ack the request, some stay silent
        // and just start pushing — tolerate both.
        await SendCommandAsync(BcConstants.MsgIdMotionRequest,
            extension: new ExtensionXml { ChannelId = _channelId },
            replyTimeout: TimeSpan.FromSeconds(5), tolerateNoReply: true, ct: ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            BcMessage msg;
            try
            {
                // Pushes are sporadic by nature: no read timeout. A dead connection
                // is noticed by the video stream, which tears everything down.
                msg = await sub.Messages.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return; // connection closed
            }

            var list = msg.Xml?.RawElement("AlarmEventList");
            if (list == null)
            {
                // Some firmware variants push different XML on the motion channel
                // (doorbell/visitor pushes among the suspects) — surface the first
                // one seen so the mapping can be extended from a real sample.
                if (msg.Xml != null && (!_oddMotionPushLogged || Log.AlarmXml))
                {
                    _oddMotionPushLogged = true;
                    var raw = Encoding.UTF8.GetString(msg.Xml.Serialize()).Replace('\r', ' ').Replace('\n', ' ');
                    Log.Info($"BC {_logTag}: motion-channel push without AlarmEventList — " +
                             $"raw: {(raw.Length > 400 ? raw[..400] + "…" : raw)} (please report if this " +
                             "coincides with a doorbell press)");
                }
                continue;
            }
            // NEOLINK_DEBUG_ALARMS=1 elevates the full push XML to Info — the
            // capture mode for mapping new firmware dialects without the
            // media-packet flood of NEOLINK_LOG=debug.
            if (Log.AlarmXml)
                Log.Info($"BC {_logTag}: alarm push {list.ToString(SaveOptions.DisableFormatting)}");
            else
                Log.Debug($"BC {_logTag}: alarm push {list.ToString(SaveOptions.DisableFormatting)}");
            foreach (var ev in list.Elements("AlarmEvent"))
            {
                if (uint.TryParse(ev.Element("channelId")?.Value.Trim(), out var ch) && ch != _channelId)
                    continue;
                onEvent(ParseAlarmEvent(ev));
            }
        }
    }

    /// <summary>
    /// One AlarmEvent element → MotionPush. AItype carries the AI classifications
    /// on most firmware (repeated elements, comma lists, "none" placeholders,
    /// varying capitalization) — but some put detection tokens INSIDE the status
    /// list instead: a video doorbell's button press arrives as
    /// status="MD,visitor" with AItype="none" (captured from a real Reolink
    /// doorbell). Detection tokens found in the status are folded into the AI list.
    /// </summary>
    internal static MotionPush ParseAlarmEvent(XElement ev)
    {
        var status = ev.Element("status")?.Value.Trim() ?? "";
        var aiTypes = ev.Elements()
            .Where(e => e.Name.LocalName.Equals("AItype", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t is not ("" or "none"))
            .Distinct()
            .ToList();
        foreach (var t in status.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = t.Trim().ToLowerInvariant();
            // Detection tokens that firmware puts INSIDE the status list: doorbell
            // presses (captured: "MD,visitor") and, expected in the same style,
            // the perimeter-protection smart events.
            if (token is "visitor" or "doorbell"
                    or "crossline" or "cross_line" or "tripwire"
                    or "intrude" or "intrusion" or "region" or "perimeter"
                    or "linger" or "loiter" or "loitering"
                && !aiTypes.Contains(token))
                aiTypes.Add(token);
        }

        // Perimeter protection on newer firmware nests its verdicts in a
        // smartAiTypeList (captured from a real camera, 2026-07-09):
        //   <smartAiTypeList><smartAiType><type>intrusion</type><index>1</index>
        //     <subList><index>0</index><type>people</type></subList></smartAiType>
        //     <pts>…</pts><frameIndex>…</frameIndex></smartAiTypeList>
        // type = the tripped rule (crossline/intrusion/loitering), index = which
        // of the user's zones/lines, subList = the object class that tripped it.
        // Collecting every <type> under the list captures rule AND object tokens;
        // these pushes can arrive with status "none", so without this fold a
        // loitering alert would read as an all-clear.
        foreach (var smartList in ev.Elements()
                     .Where(e => e.Name.LocalName.Equals("smartAiTypeList", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var typeEl in smartList.Descendants()
                         .Where(d => d.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase)))
            {
                var token = typeEl.Value.Trim().ToLowerInvariant();
                if (token is not ("" or "none") && !aiTypes.Contains(token))
                    aiTypes.Add(token);
            }
        }
        return new MotionPush(status, aiTypes);
    }

    public async Task WatchStatusAsync(Action<StatusPush> onEvent, CancellationToken ct)
    {
        // Cameras broadcast these without any opt-in request. The connection allows
        // one subscriber per message id, so each id gets its own long-lived listener;
        // none of these ids are used by the polled control commands.
        await Task.WhenAll(
            WatchStatusIdAsync(BcConstants.MsgIdNetInfo, "NetInfo", ParseNetInfo, onEvent, ct),
            WatchStatusIdAsync(BcConstants.MsgIdSleepStatus, "sleepStatus", ParseSleepStatus, onEvent, ct),
            WatchStatusIdAsync(BcConstants.MsgIdSirenStatusList, "SirenStatusList",
                el => ParseStatusList(el, _channelId) is { } on ? new SirenStatusPush(on) : null, onEvent, ct),
            WatchStatusIdAsync(BcConstants.MsgIdFloodlightStatusList, "FloodlightStatusList",
                el => ParseStatusList(el, _channelId) is { } on ? new FloodlightStatusPush(on) : null, onEvent, ct),
            WatchStatusIdAsync(BcConstants.MsgIdBatteryInfoList, "BatteryList",
                el => ParseBatteryList(el, _channelId), onEvent, ct),
            WatchSmartAiEventsAsync(ct)
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// Capture aid for perimeter protection: newer firmware pushes smart events
    /// (yoloWorldEventList, msg 600) whose XML shape has not been mapped yet.
    /// The first push per connection WITH ACTUAL CONTENT is logged in full at
    /// Info so a user tripping their line-crossing/intrusion zone can report
    /// the exact wire format. Empty lists (&lt;yoloWorldEventList /&gt;) ride
    /// along constantly on some firmware and say nothing — those stay at Debug.
    /// </summary>
    private async Task WatchSmartAiEventsAsync(CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdSmartAiEventList);
        bool logged = false;
        while (!ct.IsCancellationRequested)
        {
            BcMessage msg;
            try
            {
                msg = await sub.Messages.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return; // connection closed
            }
            if (msg.Xml == null) continue;
            var raw = Encoding.UTF8.GetString(msg.Xml.Serialize()).Replace('\r', ' ').Replace('\n', ' ');
            bool hasContent = msg.Xml.Raw.Any(e => e.HasElements);
            if (hasContent && (!logged || Log.AlarmXml))
            {
                logged = true;
                Log.Info($"BC {_logTag}: smart-event push (msg 600) — raw: " +
                         $"{(raw.Length > 500 ? raw[..500] + "…" : raw)} (if this coincides with a " +
                         "perimeter/line-crossing alert, please report it so it can be mapped to events)");
            }
            else
            {
                Log.Debug($"BC {_logTag}: smart-event push {(raw.Length > 500 ? raw[..500] + "…" : raw)}");
            }
        }
    }

    private async Task WatchStatusIdAsync(uint msgId, string elementName,
        Func<XElement, StatusPush?> parse, Action<StatusPush> onEvent, CancellationToken ct)
    {
        using var sub = _conn.Subscribe(msgId);
        while (!ct.IsCancellationRequested)
        {
            BcMessage msg;
            try
            {
                // Pushes are sporadic by nature: no read timeout (same as motion).
                msg = await sub.Messages.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return; // connection closed
            }

            // Firmware capitalization varies; match the wrapper element leniently.
            var el = msg.Xml?.Raw.FirstOrDefault(
                e => e.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase));
            if (el == null) continue;
            if (parse(el) is { } push)
            {
                Log.Debug($"BC {_logTag}: status push {el.ToString(SaveOptions.DisableFormatting)}");
                onEvent(push);
            }
        }
    }

    /// <summary>First descendant with the given name, any capitalization.</summary>
    private static XElement? Descendant(XElement el, string name) =>
        el.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// msg 464 NetInfo push. Captured sample: &lt;net_type&gt;wifi&lt;/net_type&gt;
    /// &lt;signal&gt;-45&lt;/signal&gt; — signal is the Wi-Fi RSSI in dBm. Pushes
    /// without a numeric signal (wired cameras) are ignored.
    /// </summary>
    internal static WifiSignalPush? ParseNetInfo(XElement el)
    {
        if (Descendant(el, "signal") is not { } signal
            || !int.TryParse(signal.Value.Trim(), out var dbm))
            return null;
        var netType = Descendant(el, "net_type")?.Value.Trim();
        return new WifiSignalPush(string.IsNullOrEmpty(netType) ? null : netType, dbm);
    }

    /// <summary>
    /// msg 623 sleepStatus push. The value rides in a &lt;status&gt; child (or the
    /// element body); firmware uses numeric and token forms, so accept both.
    /// </summary>
    internal static SleepStatusPush? ParseSleepStatus(XElement el)
    {
        var v = (Descendant(el, "status")?.Value ?? el.Value).Trim().ToLowerInvariant();
        if (v.Length == 0) return null;
        return new SleepStatusPush(v is "1" or "true" or "sleep" or "sleeping");
    }

    /// <summary>
    /// Shared shape of the SirenStatusList (msg 547) / FloodlightStatusList (msg 291)
    /// pushes: per-channel entries carrying a status. Returns the on/off state for
    /// <paramref name="channelId"/>, or null when the push has no entry for it.
    /// Entries without a channel element apply to any channel (single-camera pushes).
    /// </summary>
    internal static bool? ParseStatusList(XElement list, byte channelId)
    {
        foreach (var entry in list.Elements())
        {
            var chEl = entry.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase)
                || e.Name.LocalName.Equals("channelId", StringComparison.OrdinalIgnoreCase));
            if (chEl != null && uint.TryParse(chEl.Value.Trim(), out var ch) && ch != channelId)
                continue;
            var status = entry.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals("status", StringComparison.OrdinalIgnoreCase))?.Value.Trim().ToLowerInvariant();
            if (status is null or "") continue;
            return status is not ("0" or "close" or "off" or "false" or "none");
        }
        return null;
    }

    /// <summary>
    /// msg 252 BatteryList push: per-channel &lt;BatteryInfo&gt; entries. Returns the
    /// reading for <paramref name="channelId"/> (entries without a channel apply to
    /// any), or null when the push has nothing usable.
    /// </summary>
    internal static BatteryPush? ParseBatteryList(XElement list, byte channelId)
    {
        foreach (var entry in list.Elements())
        {
            var chEl = entry.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals("channelId", StringComparison.OrdinalIgnoreCase)
                || e.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase));
            if (chEl != null && uint.TryParse(chEl.Value.Trim(), out var ch) && ch != channelId)
                continue;
            if (ParseBatteryInfo(entry) is { } b)
                return b;
        }
        return null;
    }

    /// <summary>
    /// One &lt;BatteryInfo&gt; element (msg 253 reply or a BatteryList entry):
    /// batteryPercent plus chargeStatus ("charging" / "chargeComplete" / "none").
    /// Charging is true whenever a power source is attached.
    /// </summary>
    internal static BatteryPush? ParseBatteryInfo(XElement el)
    {
        if (Descendant(el, "batteryPercent") is not { } pctEl
            || !int.TryParse(pctEl.Value.Trim(), out var pct))
            return null;
        var charge = Descendant(el, "chargeStatus")?.Value.Trim().ToLowerInvariant() ?? "";
        return new BatteryPush(Math.Clamp(pct, 0, 100), charge.Contains("charg"));
    }

    public async Task<byte[]?> SnapAsync(CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdSnap);

        var body = BcXmlBody.FromRaw(new XElement("Snap",
            new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", _channelId),
            new XElement("logicChannel", _channelId),
            new XElement("time", 0),
            new XElement("fullFrame", 0),
            new XElement("streamType", "subStream")));
        var req = new BcMessage
        {
            Meta = new BcMeta
            {
                MsgId = BcConstants.MsgIdSnap,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            Extension = new ExtensionXml { ChannelId = _channelId },
            Xml = body,
        };
        await _conn.SendAsync(req, ct).ConfigureAwait(false);

        // The JPEG may span several messages: the first reply carries a <Snap>
        // with pictureSize, the image bytes arrive as binary payloads until
        // that many bytes have been received.
        int expected = -1;
        using var jpeg = new MemoryStream();
        var perMessageTimeout = TimeSpan.FromSeconds(8);
        while (true)
        {
            var reply = await sub.ReceiveAsync(perMessageTimeout, ct).ConfigureAwait(false);
            if (reply.Meta.ResponseCode != 200)
                throw new CameraCommandException(BcConstants.MsgIdSnap, reply.Meta.ResponseCode);
            if (reply.Xml?.RawElement("Snap") is { } snap
                && int.TryParse(snap.Element("pictureSize")?.Value.Trim(), out var size) && size > 0)
                expected = size;
            if (reply.Binary is { Length: > 0 } bin)
                jpeg.Write(bin, 0, bin.Length);
            if (expected >= 0 && jpeg.Length >= expected)
                return jpeg.ToArray();
            if (expected < 0 && jpeg.Length > 0)
                return jpeg.ToArray(); // no size advertised: single-message JPEG
        }
    }

    public async Task TalkAsync(TalkAbilityXml ability, ChannelReader<byte[]> frames, CancellationToken ct)
    {
        // TalkConfig claims the camera's speaker for our stream. Response 422
        // means another talker (phone app, NVR) holds it — reset and retry once.
        try
        {
            await SendTalkConfigAsync(ability, ct).ConfigureAwait(false);
        }
        catch (CameraCommandException ex) when (ex.ResponseCode == 422)
        {
            await SendTalkResetAsync(ct).ConfigureAwait(false);
            await SendTalkConfigAsync(ability, ct).ConfigureAwait(false);
        }

        // The whole audio stream rides one message number, like the camera's own
        // media stream does; binaryData=1 marks the payloads as raw media.
        var talkNum = NewMessageNum();
        try
        {
            while (await frames.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (frames.TryRead(out var frame))
                {
                    var msg = new BcMessage
                    {
                        Meta = new BcMeta
                        {
                            MsgId = BcConstants.MsgIdTalk,
                            ChannelId = _channelId,
                            MsgNum = talkNum,
                            StreamType = 0,
                            ResponseCode = 0,
                            Class = BcConstants.ClassModern,
                        },
                        Extension = new ExtensionXml { ChannelId = _channelId, BinaryData = 1 },
                        Binary = frame,
                    };
                    await _conn.SendAsync(msg, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Release the speaker even when the caller's token is already
            // cancelled — otherwise the camera stays "busy" for the next talker.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await SendTalkResetAsync(cts.Token).ConfigureAwait(false); }
            catch { /* best effort; the camera also times the session out */ }
        }
    }

    private async Task SendTalkConfigAsync(TalkAbilityXml ability, CancellationToken ct)
    {
        var cfg = new XElement("TalkConfig",
            new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", _channelId),
            new XElement("duplex", ability.Duplex),
            new XElement("audioStreamMode", ability.AudioStreamMode),
            new XElement("audioConfig",
                new XElement("audioType", ability.AudioType),
                new XElement("sampleRate", ability.SampleRate),
                new XElement("samplePrecision", ability.SamplePrecision),
                new XElement("lengthPerEncoder", ability.LengthPerEncoder),
                new XElement("soundTrack", ability.SoundTrack)));
        await SendCommandAsync(BcConstants.MsgIdTalkConfig, BcXmlBody.FromRaw(cfg),
            new ExtensionXml { ChannelId = _channelId }, ct: ct).ConfigureAwait(false);
    }

    private Task SendTalkResetAsync(CancellationToken ct) =>
        SendCommandAsync(BcConstants.MsgIdTalkReset,
            extension: new ExtensionXml { ChannelId = _channelId },
            replyTimeout: TimeSpan.FromSeconds(2), tolerateNoReply: true, ct: ct);

    public async Task PingAsync(CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdPing);
        var ping = BcMessage.HeaderOnly(new BcMeta
        {
            MsgId = BcConstants.MsgIdPing,
            ChannelId = _channelId,
            MsgNum = NewMessageNum(),
            StreamType = 0,
            ResponseCode = 0,
            Class = BcConstants.ClassModern,
        });
        await _conn.SendAsync(ping, ct).ConfigureAwait(false);
        await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}

public static class Md5Utils
{
    /// <summary>
    /// Reolink's odd MD5 mangling: uppercase hex MD5 truncated to 31 chars,
    /// with either a trailing NUL (legacy fields, 32 bytes total) or nothing (XML fields).
    /// </summary>
    public static string Md5String31(string input, bool zeroLast)
    {
        var hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))); // uppercase, 32 chars
        var truncated = hex[..31];
        return zeroLast ? truncated + "\0" : truncated;
    }

    /// <summary>AES key = first 16 bytes of the uppercase hex MD5 of "{nonce}-{password}".</summary>
    public static byte[] MakeAesKey(string nonce, string password)
    {
        var hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{nonce}-{password}")));
        return Encoding.ASCII.GetBytes(hex[..16]);
    }
}
