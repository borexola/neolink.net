// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text;
using System.Threading.Channels;
using Neolink.Bc;
using Neolink.Config;
using Neolink.Media;
using Neolink.Protocol;

namespace Neolink;

/// <summary>
/// Built-in test suite. Runs pure unit tests always; when given the path to the
/// original Rust neolink repository (via --config), also validates the codec
/// against its captured protocol samples.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static bool Run(string? rustRepoPath)
    {
        Console.WriteLine("Running Neolink.NET self-tests...\n");

        Test("md5 truncate vector", () =>
        {
            AssertEq(Md5Utils.Md5String31("admin", zeroLast: false), "21232F297A57A5A743894A0E4A801FC");
            AssertEq(Md5Utils.Md5String31("admin", zeroLast: true), "21232F297A57A5A743894A0E4A801FC\0");
        });

        Test("aes key derivation shape", () =>
        {
            var key = Md5Utils.MakeAesKey("9E6D1FCB9E69846D", "password123");
            AssertEq(key.Length, 16);
            // Key is ASCII of uppercase hex
            foreach (var b in key)
                Assert(b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'A' and <= (byte)'F'), "key must be hex ASCII");
        });

        Test("bcencrypt roundtrip", () =>
        {
            var data = new byte[256];
            Random.Shared.NextBytes(data);
            var enc = XmlCrypto.BcXor(7, data);
            var dec = XmlCrypto.BcXor(7, enc);
            AssertSeq(dec, data);
        });

        Test("aes-cfb roundtrip (unaligned length)", () =>
        {
            var key = Encoding.ASCII.GetBytes("0123456789ABCDEF");
            foreach (var len in new[] { 1, 15, 16, 17, 100, 1000 })
            {
                var data = new byte[len];
                Random.Shared.NextBytes(data);
                var enc = XmlCrypto.AesCfb(data, key, encrypting: true);
                var dec = XmlCrypto.AesCfb(enc, key, encrypting: false);
                AssertSeq(dec, data);
            }
        });

        Test("xml serialize/parse roundtrip", () =>
        {
            var body = new Bc.Xml.BcXmlBody
            {
                LoginUser = new Bc.Xml.LoginUserXml { UserName = "AAA", Password = "BBB", UserVer = 1 },
                LoginNet = new Bc.Xml.LoginNetXml(),
            };
            var bytes = body.Serialize();
            var parsed = Bc.Xml.BcXmlBody.TryParse(bytes);
            Assert(parsed?.LoginUser?.UserName == "AAA", "roundtrip username");
            Assert(parsed?.LoginNet?.Type == "LAN", "roundtrip lan");
        });

        Test("xml raw element passthrough (read-modify-write)", () =>
        {
            // A settings body we have no typed model for must survive a parse →
            // modify → serialize round trip byte-for-byte apart from the change.
            var wire = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                       "<body><LedState version=\"1.1\"><channelId>0</channelId><ledVersion>2</ledVersion>" +
                       "<state>close</state><lightState>open</lightState></LedState></body>";
            var parsed = Bc.Xml.BcXmlBody.TryParse(Encoding.UTF8.GetBytes(wire));
            var led = parsed?.RawElement("LedState");
            Assert(led != null, "LedState raw element present");
            AssertEq((string?)led!.Element("state"), "close");

            led.Element("state")!.Value = "open";
            led.Element("ledVersion")?.Remove();
            var reser = Bc.Xml.BcXmlBody.FromRaw(led).Serialize();
            var reparsed = Bc.Xml.BcXmlBody.TryParse(reser)?.RawElement("LedState");
            AssertEq((string?)reparsed?.Element("state"), "open");
            AssertEq((string?)reparsed?.Element("lightState"), "open");
            Assert(reparsed?.Element("ledVersion") == null, "ledVersion removed");
        });

        Test("xml PtzControl serialize", () =>
        {
            var body = new Bc.Xml.BcXmlBody
            {
                PtzControl = new Bc.Xml.PtzControlXml { ChannelId = 0, Speed = 32, Command = "left" },
            };
            var parsed = Bc.Xml.BcXmlBody.TryParse(body.Serialize());
            var ptz = parsed?.RawElement("PtzControl");
            AssertEq((string?)ptz?.Element("command"), "left");
            AssertEq((uint?)ptz?.Element("speed") ?? 0, 32u);
        });

        Test("xml StreamInfoList parse", () =>
        {
            var wire = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                       "<body><StreamInfoList version=\"1.1\"><StreamInfo><channelBits>1</channelBits>" +
                       "<encodeTable><type>mainStream</type><resolution><width>2560</width><height>1440</height></resolution>" +
                       "<defaultFramerate>30</defaultFramerate><defaultBitrate>3072</defaultBitrate>" +
                       "<framerateTable>30 25 20 15</framerateTable><bitrateTable>1024 2048 3072</bitrateTable>" +
                       "</encodeTable></StreamInfo></StreamInfoList></body>";
            var parsed = Bc.Xml.BcXmlBody.TryParse(Encoding.UTF8.GetBytes(wire));
            var list = parsed?.StreamInfoList;
            Assert(list is { StreamInfos.Count: 1 }, "one StreamInfo");
            var table = list!.StreamInfos[0].EncodeTables.Single();
            AssertEq(table.Type, "mainStream");
            AssertEq(table.Width, 2560u);
            AssertEq(table.Height, 1440u);
            AssertEq(table.DefaultBitrate, 3072u);
            AssertEq(table.FramerateTable, "30 25 20 15");
        });

        Test("support flags tolerate non-numeric values", () =>
        {
            // Real-world Support xml (E1 Pro) uses strings where numbers were
            // expected: ptzMode="pt" means supported, "none" means not.
            var support = System.Xml.Linq.XElement.Parse(
                "<Support version=\"1.1\"><ptzMode>pt</ptzMode><ptzCfg>0</ptzCfg>" +
                "<audioTalk>1</audioTalk><rtsp>none</rtsp></Support>");
            Assert(Streaming.CameraControl.SupportFlag(support, "ptzMode"), "ptzMode 'pt' = supported");
            Assert(!Streaming.CameraControl.SupportFlag(support, "ptzCfg"), "ptzCfg '0' = unsupported");
            Assert(Streaming.CameraControl.SupportFlag(support, "audioTalk"), "audioTalk '1' = supported");
            Assert(!Streaming.CameraControl.SupportFlag(support, "rtsp"), "'none' = unsupported");
            Assert(!Streaming.CameraControl.SupportFlag(support, "missing"), "absent = unsupported");
            Assert(!Streaming.CameraControl.SupportFlag(null, "ptzMode"), "no Support xml = unsupported");
        });

        Test("extension parse", () =>
        {
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<Extension version=\"1.1\">\n<binaryData>1</binaryData>\n</Extension>\n";
            var ext = Bc.Xml.ExtensionXml.TryParse(Encoding.UTF8.GetBytes(xml));
            Assert(ext?.BinaryData == 1, "binaryData == 1");
        });

        Test("mini-toml parses sample config", () =>
        {
            var toml = """
                bind = "0.0.0.0"
                # comment
                [[users]]
                name = "me"
                pass = "mepass"
                [[cameras]]
                name = "driveway"
                username = "admin"
                password = "12345678"
                address = "192.168.1.187:9000"
                permitted_users = [ "me" ]
                """;
            var root = MiniToml.Parse(toml);
            AssertEq(MiniToml.GetString(root, "bind")!, "0.0.0.0");
            var cams = MiniToml.GetTables(root, "cameras");
            AssertEq(cams.Count, 1);
            AssertEq(MiniToml.GetString(cams[0], "address")!, "192.168.1.187:9000");
            AssertEq(MiniToml.GetStringList(cams[0], "permitted_users")![0], "me");
        });

        Test("json config parses (with comments)", () =>
        {
            var json = """
                {
                  // a comment
                  "bind": "127.0.0.1",
                  "bind_port": 8555,
                  "recording": {
                    "path": "/recordings",
                    "clips_path": "/clips",
                    "archive_path": "/archive",
                    "retention_days": 14,
                  },
                  "users": [ { "name": "me", "pass": "mepass" } ],
                  "cameras": [
                    {
                      "name": "driveway",
                      "username": "admin",
                      "password": "12345678",
                      "address": "192.168.1.187",
                      "stream": "both",
                      "permitted_users": [ "me" ],
                    },
                    {
                      "name": "argus",
                      "username": "admin",
                      "password": "12345678",
                      "address": "192.168.1.188",
                      "always_on": true,
                    },
                  ],
                }
                """;
            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmp, json);
            try
            {
                var cfg = NeolinkConfig.Load(tmp);
                AssertEq(cfg.BindAddr, "127.0.0.1");
                AssertEq(cfg.BindPort, 8555);
                AssertEq(cfg.Cameras.Count, 2);
                AssertEq(cfg.Cameras[0].Host, "192.168.1.187");
                AssertEq(cfg.Cameras[0].Port, 9000); // default port applied
                AssertEq(cfg.Users.Count, 1);
                var permitted = cfg.PermittedUsersFor(cfg.Cameras[0]);
                Assert(permitted != null && permitted.Contains("me"), "permitted users");
                // Battery cameras: always_on is tri-state — unset means auto.
                Assert(cfg.Cameras[0].AlwaysOn == null, "always_on defaults to auto");
                Assert(cfg.Cameras[1].AlwaysOn == true, "always_on parsed");
                // Tiered-storage keys must round-trip through the parser — the
                // archive UI only appears when archive_path survives loading.
                AssertEq(cfg.Recording!.Path, "/recordings");
                AssertEq(cfg.Recording.ClipsPath, "/clips");
                AssertEq(cfg.Recording.ArchivePath, "/archive");
                AssertEq(cfg.Recording.RetentionDays, 14);
            }
            finally
            {
                File.Delete(tmp);
            }
        });

        Test("h264 annex-b NAL splitting", () =>
        {
            var stream = new byte[] { 0, 0, 0, 1, 0x67, 1, 2, 3, 0, 0, 1, 0x68, 9, 8, 0, 0, 0, 1, 0x65, 5, 5, 5 };
            var nals = H26x.SplitNals(stream);
            AssertEq(nals.Count, 3);
            AssertEq(H26x.H264NalType(nals[0].Span), H26x.H264Sps);
            AssertEq(H26x.H264NalType(nals[1].Span), H26x.H264Pps);
            AssertEq(H26x.H264NalType(nals[2].Span), H26x.H264Idr);
        });

        Test("rtp h264 fragmentation", () =>
        {
            var packetizer = new Rtsp.RtpPacketizer(96);
            var bigNal = new byte[5000];
            bigNal[0] = 0x65;
            Random.Shared.NextBytes(bigNal.AsSpan(1));
            var au = new byte[4 + bigNal.Length];
            au[3] = 1;
            bigNal.CopyTo(au, 4);
            var packets = packetizer.PacketizeVideo(VideoCodec.H264, au, 1234);
            Assert(packets.Count >= 4, "should fragment into multiple packets");
            // Check FU-A indicators and reassembly
            var reassembled = new List<byte> { (byte)((packets[0][12] & 0xE0) | (packets[0][13] & 0x1F)) };
            foreach (var p in packets)
            {
                AssertEq(p[12] & 0x1F, 28); // FU-A
                reassembled.AddRange(p.Skip(14));
            }
            AssertSeq(reassembled.ToArray(), bigNal);
            Assert((packets[^1][1] & 0x80) != 0, "marker on last packet");
        });

        Test("event label mapping", () =>
        {
            var labels = Recording.EventRecorder.LabelsOf(new MotionPush("MD",
                new[] { "people", "dog_cat", "people" }));
            AssertEq(string.Join(",", labels.OrderBy(x => x)), "animal,person");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(new MotionPush("MD", Array.Empty<string>()))),
                "motion");
            Assert(new MotionPush("MD", Array.Empty<string>()).Active, "MD is active");
            Assert(!new MotionPush("none", Array.Empty<string>()).Active, "none is all-clear");
            Assert(new MotionPush("none", new[] { "people" }).Active, "AI type implies activity");
            // Video doorbells: the button press arrives as a "visitor" AI push.
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(new MotionPush("MD", new[] { "visitor" }))),
                "doorbell");
            Assert(new MotionPush("none", new[] { "visitor" }).Active, "a doorbell press is an active event");

            // Captured from a real Reolink doorbell (FrontDoor, 2026-07-08): the
            // press token rides in the STATUS list, not the AItype field.
            var pressXml = System.Xml.Linq.XElement.Parse(
                "<AlarmEvent version=\"1.1\"><channelId>0</channelId><status>MD,visitor</status>" +
                "<AItype>none</AItype><recording>0</recording><timeStamp>0</timeStamp></AlarmEvent>");
            var press = BcCamera.ParseAlarmEvent(pressXml);
            Assert(press.Active, "captured press is active");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(press)), "doorbell");

            // Perimeter protection (app-side line/zone crossing): tokens map to
            // dedicated labels so they can be filtered independently of the plain
            // person/vehicle detections; spellings tolerated in AItype AND status.
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(
                new MotionPush("MD", new[] { "crossline" }))), "line-crossing");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(
                new MotionPush("MD", new[] { "intrude", "people" })).OrderBy(x => x)),
                "intrusion,person");
            var cross = BcCamera.ParseAlarmEvent(System.Xml.Linq.XElement.Parse(
                "<AlarmEvent version=\"1.1\"><channelId>0</channelId><status>MD,crossline</status>" +
                "<AItype>none</AItype></AlarmEvent>"));
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(cross)), "line-crossing");

            // Captured from a real Reolink Elite WiFi (Driveway, 2026-07-09): newer
            // firmware nests perimeter verdicts in smartAiTypeList — rule type +
            // zone index + the object class that tripped it.
            var intrusion = BcCamera.ParseAlarmEvent(System.Xml.Linq.XElement.Parse(
                "<AlarmEvent version=\"1.1\"><channelId>0</channelId><status>MD</status>" +
                "<AItype>people</AItype><recording>0</recording><timeStamp>0</timeStamp>" +
                "<smartAiTypeList><smartAiType><type>intrusion</type><index>1</index>" +
                "<subList><index>0</index><type>people</type></subList></smartAiType>" +
                "<pts>15210169395</pts><frameIndex>121231</frameIndex></smartAiTypeList></AlarmEvent>"));
            Assert(intrusion.Active, "captured intrusion is active");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(intrusion).OrderBy(x => x)),
                "intrusion,person");

            // Smart pushes can arrive with status/AItype both "none" — the nested
            // verdict alone must keep them active (a loitering alert is not an
            // all-clear).
            var loiter = BcCamera.ParseAlarmEvent(System.Xml.Linq.XElement.Parse(
                "<AlarmEvent version=\"1.1\"><channelId>0</channelId><status>none</status>" +
                "<AItype>none</AItype><recording>0</recording><timeStamp>0</timeStamp>" +
                "<smartAiTypeList><smartAiType><type>loitering</type><index>1</index>" +
                "<subList><index>0</index><type>people</type></subList></smartAiType>" +
                "<pts>15221690925</pts><frameIndex>121323</frameIndex></smartAiTypeList></AlarmEvent>"));
            Assert(loiter.Active, "smart verdict with status=none stays active");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(loiter).OrderBy(x => x)),
                "loitering,person");

            // An empty <smartAiTypeList /> rides along on many pushes — no effect.
            var emptySmart = BcCamera.ParseAlarmEvent(System.Xml.Linq.XElement.Parse(
                "<AlarmEvent version=\"1.1\"><channelId>0</channelId><status>none</status>" +
                "<AItype>none</AItype><smartAiTypeList /></AlarmEvent>"));
            Assert(!emptySmart.Active, "empty smart list is not a detection");

            // Content-free msg-600 pushes (<yoloWorldEventList />) must not reach
            // the Info log — the capture aid only surfaces payloads with substance.
            var emptyYolo = Bc.Xml.BcXmlBody.TryParse(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><body><yoloWorldEventList version=\"1.1\" /></body>"));
            Assert(emptyYolo != null && emptyYolo.Raw.Count > 0 && !emptyYolo.Raw.Any(e => e.HasElements),
                "empty yoloWorldEventList counts as content-free");

            // Perimeter labels are OPT-IN: an untouched filter (null) records the
            // classic detections but not the new labels — nobody's recordings
            // change until they tick the chips.
            var untouched = new Recording.CameraRecordingSettings(Events: true, Continuous: false, EventTypes: null);
            Assert(untouched.AllowsLabel("person") && untouched.AllowsLabel("motion"),
                "default filter records classic detections");
            Assert(!untouched.AllowsLabel("line-crossing") && !untouched.AllowsLabel("intrusion")
                && !untouched.AllowsLabel("loitering"), "perimeter labels are opt-in");
            var optedIn = new Recording.CameraRecordingSettings(Events: true, Continuous: false,
                EventTypes: new List<string> { "line-crossing", "intrusion" });
            Assert(optedIn.AllowsLabel("line-crossing") && !optedIn.AllowsLabel("person"),
                "explicit filter is exact");
        });

        Test("capture schedule (per-camera day/time gate on events)", () =>
        {
            // 2026-07-06 is a Monday; the week runs Mon 6th .. Sun 12th.
            var untouched = new Recording.CameraRecordingSettings(Events: true, Continuous: false, EventTypes: null);
            Assert(untouched.ScheduleAllows(new DateTime(2026, 7, 12, 3, 30, 0)),
                "untouched schedule captures any day, any hour");

            // The schedule is opt-in: while switched off, a configured window is
            // dormant and everything captures — turning it off must never mean
            // "capture nothing" or silently keep filtering.
            var dormant = new Recording.CameraRecordingSettings(true, false, null,
                ScheduleDays: new List<string> { "mon" }, ScheduleStart: "08:00", ScheduleEnd: "09:00",
                ScheduleEnabled: false);
            Assert(dormant.ScheduleAllows(new DateTime(2026, 7, 12, 3, 30, 0)),
                "disabled schedule captures everything despite a stored window");

            var weekdays = new Recording.CameraRecordingSettings(true, false, null,
                ScheduleDays: new List<string> { "mon", "tue", "wed", "thu", "fri" }, ScheduleEnabled: true);
            Assert(weekdays.ScheduleAllows(new DateTime(2026, 7, 10, 12, 0, 0)), "Friday passes a weekday filter");
            Assert(!weekdays.ScheduleAllows(new DateTime(2026, 7, 11, 12, 0, 0)), "Saturday is discarded");

            var office = new Recording.CameraRecordingSettings(true, false, null,
                ScheduleStart: "08:00", ScheduleEnd: "18:00", ScheduleEnabled: true);
            Assert(office.ScheduleAllows(new DateTime(2026, 7, 6, 8, 0, 0)), "window start is inclusive");
            Assert(!office.ScheduleAllows(new DateTime(2026, 7, 6, 18, 0, 0)), "window end is exclusive");
            Assert(!office.ScheduleAllows(new DateTime(2026, 7, 6, 3, 0, 0)), "before the window is discarded");

            // A window past midnight (nights-only) wraps; the day filter applies
            // to the day the event lands on, so 01:00 needs Saturday enabled.
            var nights = new Recording.CameraRecordingSettings(true, false, null,
                ScheduleDays: new List<string> { "fri", "sat" }, ScheduleStart: "22:00", ScheduleEnd: "06:00",
                ScheduleEnabled: true);
            Assert(nights.ScheduleAllows(new DateTime(2026, 7, 10, 23, 30, 0)), "overnight window before midnight");
            Assert(nights.ScheduleAllows(new DateTime(2026, 7, 11, 1, 0, 0)), "overnight window after midnight");
            Assert(!nights.ScheduleAllows(new DateTime(2026, 7, 10, 12, 0, 0)), "midday outside an overnight window");

            // One-sided windows: a lone start runs to midnight, a lone end from it.
            var fromSix = new Recording.CameraRecordingSettings(true, false, null,
                ScheduleStart: "06:00", ScheduleEnabled: true);
            Assert(!fromSix.ScheduleAllows(new DateTime(2026, 7, 6, 5, 0, 0)), "lone start: small hours excluded");
            Assert(fromSix.ScheduleAllows(new DateTime(2026, 7, 6, 23, 59, 0)), "lone start: runs to midnight");
            var untilTen = new Recording.CameraRecordingSettings(true, false, null,
                ScheduleEnd: "22:00", ScheduleEnabled: true);
            Assert(untilTen.ScheduleAllows(new DateTime(2026, 7, 6, 0, 0, 0)), "lone end: starts at midnight");
            Assert(!untilTen.ScheduleAllows(new DateTime(2026, 7, 6, 23, 0, 0)), "lone end: late evening excluded");

            AssertEq(Recording.CameraRecordingSettings.ParseMinutes("07:45") ?? -1, 7 * 60 + 45);
            Assert(Recording.CameraRecordingSettings.ParseMinutes("7:45") == null
                && Recording.CameraRecordingSettings.ParseMinutes("nope") == null
                && Recording.CameraRecordingSettings.ParseMinutes(null) == null,
                "only strict HH:mm parses");
        });

        Test("status push parsing (unsolicited camera broadcasts)", () =>
        {
            // msg 464 NetInfo — the inner fields are from a real Wi-Fi camera
            // capture: signal is the RSSI in dBm.
            var net = Bc.Xml.BcXmlBody.TryParse(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><body><NetInfo version=\"1.1\">" +
                "<net_type>wifi</net_type><signal>-45</signal></NetInfo></body>"));
            var wifi = BcCamera.ParseNetInfo(net?.RawElement("NetInfo")!);
            Assert(wifi != null, "NetInfo parsed");
            AssertEq(wifi!.NetType!, "wifi");
            AssertEq(wifi.SignalDbm, -45);
            // A wired camera pushes NetInfo without a numeric signal — no reading.
            Assert(BcCamera.ParseNetInfo(System.Xml.Linq.XElement.Parse(
                "<NetInfo version=\"1.1\"><net_type>ethernet</net_type></NetInfo>")) == null,
                "no signal = no Wi-Fi reading");

            // msg 623 sleepStatus — battery cameras announce power-save transitions;
            // firmware uses both token and numeric status forms.
            Assert(BcCamera.ParseSleepStatus(System.Xml.Linq.XElement.Parse(
                "<sleepStatus version=\"1.1\"><channelId>0</channelId><status>sleep</status></sleepStatus>"))
                is { Sleeping: true }, "token status: sleeping");
            Assert(BcCamera.ParseSleepStatus(System.Xml.Linq.XElement.Parse(
                "<sleepStatus version=\"1.1\"><status>0</status></sleepStatus>"))
                is { Sleeping: false }, "numeric status: awake");

            // msg 291 FloodlightStatusList / msg 547 SirenStatusList share the
            // per-channel list-of-status shape.
            var flood = System.Xml.Linq.XElement.Parse(
                "<FloodlightStatusList version=\"1.1\"><FloodlightStatus>" +
                "<channel>0</channel><status>1</status></FloodlightStatus></FloodlightStatusList>");
            AssertEq(BcCamera.ParseStatusList(flood, 0), (bool?)true);
            AssertEq(BcCamera.ParseStatusList(flood, 1), (bool?)null); // other channel's push is not ours
            var siren = System.Xml.Linq.XElement.Parse(
                "<SirenStatusList version=\"1.1\"><SirenStatus>" +
                "<channelId>0</channelId><status>0</status></SirenStatus></SirenStatusList>");
            AssertEq(BcCamera.ParseStatusList(siren, 0), (bool?)false);
            // Entries without a channel element apply to any channel.
            AssertEq(BcCamera.ParseStatusList(System.Xml.Linq.XElement.Parse(
                "<SirenStatusList version=\"1.1\"><SirenStatus><status>1</status></SirenStatus></SirenStatusList>"), 0),
                (bool?)true);

            // msg 253 BatteryInfo reply / msg 252 BatteryList push — battery cameras
            // report percent + chargeStatus (none / charging / chargeComplete).
            var bat = BcCamera.ParseBatteryInfo(System.Xml.Linq.XElement.Parse(
                "<BatteryInfo version=\"1.1\"><channelId>0</channelId><adapterStatus>solarPanel</adapterStatus>" +
                "<chargeStatus>charging</chargeStatus><batteryPercent>87</batteryPercent></BatteryInfo>"));
            Assert(bat is { Percent: 87, Charging: true }, "BatteryInfo parsed");
            var batList = BcCamera.ParseBatteryList(System.Xml.Linq.XElement.Parse(
                "<BatteryList version=\"1.1\"><BatteryInfo><channelId>0</channelId>" +
                "<chargeStatus>none</chargeStatus><batteryPercent>42</batteryPercent></BatteryInfo></BatteryList>"), 0);
            Assert(batList is { Percent: 42, Charging: false }, "BatteryList push parsed");
            Assert(BcCamera.ParseBatteryList(System.Xml.Linq.XElement.Parse(
                "<BatteryList version=\"1.1\"><BatteryInfo><channelId>1</channelId>" +
                "<batteryPercent>10</batteryPercent></BatteryInfo></BatteryList>"), 0) == null,
                "other channel's battery is not ours");
        });

        Test("talk audio: encode → frame → parse → decode round-trip", () =>
        {
            // 16 kHz 440 Hz sine through the full outbound talk pipeline, fed in
            // odd-sized chunks to exercise the mid-sample chunk-boundary carry.
            const int rate = 16000, samplesPerBlock = 512, total = 3200;
            var pcm = new byte[total * 2];
            for (int i = 0; i < total; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
                pcm[i * 2] = (byte)s;
                pcm[i * 2 + 1] = (byte)(s >> 8);
            }
            var enc = new TalkFrameEncoder(rate, rate, samplesPerBlock);
            var framed = new List<byte[]>();
            for (int off = 0; off < pcm.Length;)
            {
                int len = Math.Min(1233 + off % 7, pcm.Length - off);
                framed.AddRange(enc.Feed(pcm.AsSpan(off, len)));
                off += len;
            }
            // The resampler primes on the first sample, so 3199 samples emitted
            // → 6 full 512-sample blocks, the tail stays buffered.
            AssertEq(framed.Count, 6);

            // Frame header must match the layout captured from a real camera:
            // magic "01wb", u16 size ×2 (4-byte sub-header + 260-byte block),
            // u16 sub-magic 0x0100, u16 half block size.
            var head = framed[0].AsSpan(0, 12).ToArray();
            AssertSeq(head, new byte[] { 0x30, 0x31, 0x77, 0x62, 0x08, 0x01, 0x08, 0x01, 0x00, 0x01, 0x82, 0x00 });
            foreach (var f in framed)
                AssertEq(f.Length % 8, 0);

            // Our own inbound parser must accept the frames...
            var channel = Channel.CreateUnbounded<byte[]>();
            foreach (var f in framed)
                channel.Writer.TryWrite(f);
            channel.Writer.Complete();
            var reader = new MediaFrameReader(channel.Reader);
            var decoded = new List<byte>();
            for (int i = 0; i < framed.Count; i++)
            {
                var frame = reader.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert(frame is AdpcmFrame, "frame parses as ADPCM");
                // ...and the existing decoder must reproduce the sine.
                decoded.AddRange(Adpcm.BlockToPcm(((AdpcmFrame)frame).Data));
            }
            AssertEq(decoded.Count, 6 * samplesPerBlock * 2);

            double errSq = 0, sigSq = 0;
            for (int i = 0; i < decoded.Count / 2; i++)
            {
                // The encoder emits input[i] as output[i] (one-sample latency at 1:1).
                short want = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                short got = (short)(decoded[i * 2] | (decoded[i * 2 + 1] << 8));
                errSq += (double)(want - got) * (want - got);
                sigSq += (double)want * want;
            }
            double ratio = Math.Sqrt(errSq / sigSq);
            Assert(ratio < 0.05, $"round-trip RMS error {ratio:P1} of signal");

            // Browser microphones run at 44.1/48 kHz: 3:1 linear resampling must
            // land on the same tone at the camera rate.
            var enc48 = new TalkFrameEncoder(48000, rate, 160);
            var pcm48 = new byte[4801 * 2];
            for (int i = 0; i < 4801; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 12000);
                pcm48[i * 2] = (byte)s;
                pcm48[i * 2 + 1] = (byte)(s >> 8);
            }
            var frames48 = new List<byte[]>(enc48.Feed(pcm48));
            AssertEq(frames48.Count, 10); // 4800/3 = 1600 samples out = 10×160
        });

        Test("camera availability tracking", () =>
        {
            var av = new Web.CameraAvailability();
            long t0 = 1_000_000;
            av.Update("cam", true, t0);
            av.Update("cam", true, t0 + 2_000);     // same state: no new run
            av.Update("cam", false, t0 + 60_000);   // outage starts
            av.Update("cam", true, t0 + 90_000);    // back after 30s
            var snap = av.Snapshots(t0 + 120_000).Single();
            Assert(snap.Online, "currently online");
            AssertEq(snap.Outages, 1);
            AssertEq(snap.LongestOutageMs, 30_000L);
            AssertEq(snap.Runs.Count, 3);
            // 120s observed, 30s of it down => 75% uptime.
            Assert(Math.Abs(snap.UptimePct - 75.0) < 0.01, $"uptime pct = {snap.UptimePct}");
            AssertEq(snap.CurrentSinceMs, t0 + 90_000);

            // Runs that ended before the 24h window are trimmed; the surviving
            // first run is clamped to the window edge when reporting.
            long day = (long)Web.CameraAvailability.Window.TotalMilliseconds;
            av.Update("cam", false, t0 + day + 200_000);
            av.Update("cam", true, t0 + day + 260_000);
            var later = av.Snapshots(t0 + day + 300_000).Single();
            Assert(later.Runs.Count == 3, $"old runs trimmed (got {later.Runs.Count})");
            Assert(later.ObservedMs <= day, "observation clamped to the window");
            AssertEq(later.Outages, 1); // the pre-window outage aged out

            // A camera that has only ever been offline scores zero.
            av.Update("dead", false, t0);
            var dead = av.Snapshots(t0 + 50_000).Single(s => s.Camera == "dead");
            Assert(!dead.Online, "dead camera offline");
            Assert(dead.UptimePct == 0, "dead camera scores 0%");
            AssertEq(dead.Outages, 1);
        });

        Test("event store roundtrip + retention", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            try
            {
                var store = new Recording.EventStore(dir);
                var rec = store.Create("cam1", DateTime.UtcNow, new[] { "person" });
                rec.EndUtc = rec.StartUtc.AddSeconds(30);
                rec.Ongoing = false;
                store.Save(rec);

                // A fresh store must find it again by scanning the files.
                var store2 = new Recording.EventStore(dir);
                store2.Load();
                var listed = store2.List();
                AssertEq(listed.Count, 1);
                AssertEq(listed[0].Id, rec.Id);
                Assert(!listed[0].Reviewed, "starts unreviewed");
                Assert(store2.SetReviewed(rec.Id, true), "review known id");
                Assert(store2.List(reviewed: false).Count == 0, "reviewed filter");

                // Layout: everything for a camera-day under one date folder.
                var recDir = store2.EventDir(store2.List()[0]);
                Assert(recDir.Contains(Path.Combine("cam1", rec.Id.Split('~')[1], "detections")),
                    "events live under {camera}/{date}/detections");

                // Folders older than the retention window get deleted — detections and
                // continuous footage each against their own window; an empty date
                // folder disappears once both halves are gone.
                var oldDay = Path.Combine(dir, "cam1", "2000-01-01");
                var oldDir = Path.Combine(oldDay, "detections", "120000-dead");
                Directory.CreateDirectory(oldDir);
                File.WriteAllText(Path.Combine(oldDir, "event.json"), "{}");
                var oldSeg = Path.Combine(oldDay, "continuous");
                Directory.CreateDirectory(oldSeg);
                var newSeg = store2.NewSegmentPath("cam1", DateTime.Now);
                File.WriteAllText(newSeg, "x");
                store2.Cleanup(retentionDays: 7, continuousRetentionDays: 7);
                Assert(!Directory.Exists(oldDay), "expired day folder removed entirely");
                Assert(File.Exists(newSeg), "recent segment survives retention");
                Assert(store2.List().Count == 1, "recent event survives retention");
                Assert(store2.ListContinuousDays("cam1").Count == 1, "recent day listed");

                // The exact boundary: a day EXACTLY retention-days old is kept
                // (deletion needs strictly older) — "keep 7 days" never eats day 7.
                static string DayDir(string root, string cam, DateTime day, string half) =>
                    Path.Combine(root, cam, $"{day:yyyy-MM-dd}", half);
                var boundary = DayDir(dir, "cam1", DateTime.Now.Date.AddDays(-7), "detections");
                var justOver = DayDir(dir, "cam1", DateTime.Now.Date.AddDays(-8), "detections");
                Directory.CreateDirectory(boundary);
                Directory.CreateDirectory(justOver);
                store2.Cleanup(retentionDays: 7, continuousRetentionDays: 7);
                Assert(Directory.Exists(boundary), "day exactly at the window is kept");
                Assert(!Directory.Exists(Path.GetDirectoryName(justOver)!), "one day past the window is deleted");

                // Per TYPE in the same day folder: expired detections go while
                // continuous footage with a longer window stays (and vice versa).
                var mixedDay = Path.Combine(dir, "cam1", $"{DateTime.Now.Date.AddDays(-10):yyyy-MM-dd}");
                Directory.CreateDirectory(Path.Combine(mixedDay, "detections", "010101-mixd"));
                Directory.CreateDirectory(Path.Combine(mixedDay, "continuous"));
                File.WriteAllText(Path.Combine(mixedDay, "continuous", "01-00-00.mp4"), "x");
                store2.Cleanup(retentionDays: 7, continuousRetentionDays: 30);
                Assert(!Directory.Exists(Path.Combine(mixedDay, "detections")), "expired detections removed");
                Assert(File.Exists(Path.Combine(mixedDay, "continuous", "01-00-00.mp4")),
                    "continuous with a longer window survives in the same day folder");

                // 0 = keep forever, and per-camera windows apply independently.
                var keeper = DayDir(dir, "cam1", DateTime.Now.Date.AddDays(-3650), "detections");
                var goner = DayDir(dir, "cam3", DateTime.Now.Date.AddDays(-3650), "detections");
                Directory.CreateDirectory(keeper);
                Directory.CreateDirectory(goner);
                store2.Cleanup(cam => cam == "cam1" ? (0, 0) : (7, 7));
                Assert(Directory.Exists(keeper), "retention 0 keeps a decade-old day forever");
                Assert(!Directory.Exists(Path.GetDirectoryName(goner)!), "another camera's window still applies");
                Assert(File.Exists(Path.Combine(mixedDay, "continuous", "01-00-00.mp4")),
                    "keep-forever pass leaves the mixed day's footage alone");

                // The old layout ({cam}/{date}/{event}, {cam}/continuous/{date})
                // migrates by rename when a store loads.
                var legacyEvent = Path.Combine(dir, "cam2", "2001-02-03", "090000-beef");
                Directory.CreateDirectory(legacyEvent);
                File.WriteAllText(Path.Combine(legacyEvent, "event.json"),
                    "{\"id\":\"cam2~2001-02-03~090000-beef\",\"camera\":\"cam2\"}");
                var legacySeg = Path.Combine(dir, "cam2", "continuous", "2001-02-03");
                Directory.CreateDirectory(legacySeg);
                File.WriteAllText(Path.Combine(legacySeg, "08-00-00.mp4"), "x");
                var store3 = new Recording.EventStore(dir);
                store3.Load();
                Assert(Directory.Exists(Path.Combine(dir, "cam2", "2001-02-03", "detections", "090000-beef")),
                    "legacy event folder migrated");
                Assert(File.Exists(Path.Combine(dir, "cam2", "2001-02-03", "continuous", "08-00-00.mp4")),
                    "legacy segment migrated");
                Assert(!Directory.Exists(Path.Combine(dir, "cam2", "continuous")),
                    "legacy continuous tree removed");
                AssertEq(store3.ListSegments("cam2", "2001-02-03").Count, 1);
                Assert(store3.Find("cam2~2001-02-03~090000-beef") != null, "migrated event indexed");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("storage locations: tier resolution + capacity thresholds", () =>
        {
            var baseDir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            var mainP = Path.Combine(baseDir, "recordings");
            var clipsP = Path.Combine(baseDir, "ssd");
            var archiveP = Path.Combine(baseDir, "archive");
            try
            {
                // Unconfigured tiers collapse to main — a plain install has one location.
                var plain = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = mainP });
                Assert(!plain.HasClipsTier, "no clips tier by default");
                Assert(!plain.HasArchiveTier, "no archive tier by default");
                AssertEq(plain.Locations.Count, 1);
                AssertEq(Path.GetFullPath(plain.ClipsRoot), Path.GetFullPath(mainP)); // clips fall back to main
                Assert(plain.ArchiveRoot == null, "archive root null when unset");

                // All three tiers configured and distinct → three locations.
                // A fake probe drives the capacity thresholds deterministically (GB-scale,
                // so the 1 GiB free-space reserve behaves as it would on a real drive).
                const long GB = 1024L * 1024 * 1024;
                var caps = new Dictionary<string, (long, long)?>(StringComparer.OrdinalIgnoreCase)
                {
                    [Path.GetFullPath(mainP)] = (1000 * GB, 40 * GB),    // 96% used → warn, plenty free
                    [Path.GetFullPath(clipsP)] = (1000 * GB, 500 * GB),  // 50% used → healthy
                    [Path.GetFullPath(archiveP)] = (1000 * GB, GB / 2),  // 0.5 GB free → full
                };
                var full = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = mainP, ClipsPath = clipsP, ArchivePath = archiveP },
                    probe: p => caps.TryGetValue(Path.GetFullPath(p), out var v) ? v : null);

                Assert(full.HasClipsTier && full.HasArchiveTier, "all tiers detected");
                AssertEq(full.Locations.Count, 3);
                AssertEq(Path.GetFullPath(full.ClipsRoot), Path.GetFullPath(clipsP));
                AssertEq(Path.GetFullPath(full.ArchiveRoot!), Path.GetFullPath(archiveP));

                var sample = full.Sample();
                var mainS = sample.First(s => s.Role == Recording.StorageRole.Main);
                var clipsS = sample.First(s => s.Role == Recording.StorageRole.Clips);
                var archiveS = sample.First(s => s.Role == Recording.StorageRole.Archive);
                Assert(mainS.UsedPercent >= 90 && mainS.Warn && !mainS.Full, "main warns at 96% used, plenty free");
                Assert(!clipsS.Warn && !clipsS.Full, "healthy tier is neither warn nor full");
                Assert(archiveS.Full, "archive under the free-space reserve is full");
                Assert(!full.HasRoom(Recording.StorageRole.Archive), "no room to write the near-full archive");
                Assert(full.HasRoom(Recording.StorageRole.Clips), "room to write the healthy clips tier");

                // Threshold logic keys off the byte reserve, not just percent.
                var tiny = new Recording.StorageStatus(Recording.StorageRole.Main, "m", mainP,
                    TotalBytes: 10_000_000_000, FreeBytes: 100_000_000, Online: true); // 100 MB free < 1 GiB reserve
                Assert(tiny.Full, "under the free-space reserve is full");
                var roomy = tiny with { FreeBytes = 5L * 1024 * 1024 * 1024 };
                Assert(!roomy.Full, "well above the reserve is not full");

                // HasRoom fails open when a volume can't be read.
                var blind = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = mainP }, probe: _ => null);
                Assert(blind.HasRoom(Recording.StorageRole.Main), "unreadable volume does not block recording");
            }
            finally
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { }
            }
        });

        Test("archive lifecycle: move at age, serve from archive, delete from archive", () =>
        {
            var baseDir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            var live = Path.Combine(baseDir, "recordings");
            var arch = Path.Combine(baseDir, "archive");
            try
            {
                // Stage a 10-day-old day (event + continuous) and a fresh event.
                string dayOld = $"{DateTime.Now.Date.AddDays(-10):yyyy-MM-dd}";
                var oldEvDir = Path.Combine(live, "cam1", dayOld, "detections", "080000-arc1");
                Directory.CreateDirectory(oldEvDir);
                File.WriteAllText(Path.Combine(oldEvDir, "event.json"),
                    $$"""{"id":"cam1~{{dayOld}}~080000-arc1","camera":"cam1","startUtc":"{{dayOld}}T08:00:00Z","endUtc":"{{dayOld}}T08:00:30Z","labels":["person"],"hasClip":true}""");
                File.WriteAllText(Path.Combine(oldEvDir, "clip.mp4"), "clip-bytes");
                var oldCont = Path.Combine(live, "cam1", dayOld, "continuous");
                Directory.CreateDirectory(oldCont);
                File.WriteAllText(Path.Combine(oldCont, "08-00-00.mp4"), "seg-bytes");
                // A 40-day-old day already IN the archive: past the delete window.
                string dayAncient = $"{DateTime.Now.Date.AddDays(-40):yyyy-MM-dd}";
                var ancient = Path.Combine(arch, "cam1", dayAncient, "continuous");
                Directory.CreateDirectory(ancient);
                File.WriteAllText(Path.Combine(ancient, "01-00-00.mp4"), "x");

                var store = new Recording.EventStore(live, clipsRoot: null, archiveRoot: arch);
                store.Load();
                Assert(store.Find($"cam1~{dayOld}~080000-arc1") != null, "old event indexed from live");

                // Archive on for both types: retention 7 days = footage moves at
                // day 7 (instead of deletion); archive deletes after 30.
                store.Cleanup(_ => new Recording.EventStore.CameraStoragePolicy(
                    EventDays: 7, ContinuousDays: 7,
                    ArchiveEvents: true, ArchiveContinuous: true, ArchiveDeleteDays: 30));

                Assert(!Directory.Exists(Path.Combine(live, "cam1", dayOld)), "aged day left the live tier");
                Assert(File.Exists(Path.Combine(arch, "cam1", dayOld, "detections", "080000-arc1", "clip.mp4")),
                    "event clip moved to the archive");
                Assert(File.Exists(Path.Combine(arch, "cam1", dayOld, "continuous", "08-00-00.mp4")),
                    "continuous segment moved to the archive");
                // The index followed the move: the event still resolves and plays.
                var moved = store.ArtifactPath($"cam1~{dayOld}~080000-arc1", "clip.mp4");
                Assert(moved != null && moved.StartsWith(Path.GetFullPath(arch), StringComparison.OrdinalIgnoreCase),
                    "archived event's clip resolves inside the archive");
                // The timeline sees archived footage transparently.
                Assert(store.ListContinuousDays("cam1").Contains(dayOld), "archived day listed for the timeline");
                AssertEq(store.ListSegments("cam1", dayOld).Count, 1);
                Assert(store.SegmentPath("cam1", dayOld, "08-00-00.mp4") != null, "archived segment path resolves");
                // The ancient archived day fell past the 30-day archive window.
                Assert(!Directory.Exists(Path.Combine(arch, "cam1", dayAncient)), "expired archive day deleted");

                // A second pass is a no-op (idempotent), and 0 = keep forever.
                store.Cleanup(_ => new Recording.EventStore.CameraStoragePolicy(
                    EventDays: 7, ContinuousDays: 7,
                    ArchiveEvents: true, ArchiveContinuous: true, ArchiveDeleteDays: 0));
                Assert(File.Exists(Path.Combine(arch, "cam1", dayOld, "continuous", "08-00-00.mp4")),
                    "archived footage survives with delete window 0 (forever)");

                // The per-type split: events archive while continuous deletes.
                string daySplit = $"{DateTime.Now.Date.AddDays(-9):yyyy-MM-dd}";
                var splitEv = Path.Combine(live, "cam2", daySplit, "detections", "090000-arc2");
                Directory.CreateDirectory(splitEv);
                File.WriteAllText(Path.Combine(splitEv, "event.json"),
                    $$"""{"id":"cam2~{{daySplit}}~090000-arc2","camera":"cam2","startUtc":"{{daySplit}}T09:00:00Z","endUtc":"{{daySplit}}T09:00:30Z","labels":["person"],"hasClip":true}""");
                File.WriteAllText(Path.Combine(splitEv, "clip.mp4"), "clip-bytes");
                var splitCont = Path.Combine(live, "cam2", daySplit, "continuous");
                Directory.CreateDirectory(splitCont);
                File.WriteAllText(Path.Combine(splitCont, "09-00-00.mp4"), "seg-bytes");
                store.Cleanup(_ => new Recording.EventStore.CameraStoragePolicy(
                    EventDays: 7, ContinuousDays: 7,
                    ArchiveEvents: true, ArchiveContinuous: false));
                Assert(File.Exists(Path.Combine(arch, "cam2", daySplit, "detections", "090000-arc2", "clip.mp4")),
                    "events-only archiving moved the event clip");
                Assert(!Directory.Exists(Path.Combine(arch, "cam2", daySplit, "continuous"))
                    && !Directory.Exists(splitCont),
                    "events-only archiving still DELETED expired continuous footage");

                // Old settings.json without the archive fields deserializes to archive-off.
                var legacy = System.Text.Json.JsonSerializer.Deserialize<Recording.CameraRecordingSettings>(
                    """{"events":true,"continuous":false,"eventTypes":null}""",
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert(legacy != null && !legacy.ArchiveEvents && !legacy.ArchiveContinuous
                    && legacy.ArchiveRetentionDays == null, "pre-archive settings default to archive off");
            }
            finally
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { }
            }
        });

        Test("recording settings roundtrip + type filter", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var settings = new Recording.RecordingSettings(dir);
                settings.Seed("cam1", eventsDefault: false);
                Assert(!settings.Get("cam1").Events, "seeded default respected");
                Assert(settings.Get("cam1").AllowsLabel("person"), "null filter allows everything");

                settings.Update("cam1", events: true, continuous: true,
                    new List<string> { "person" }, setEventTypes: true);
                var s = settings.Get("cam1");
                Assert(s.Events && s.Continuous, "switches updated");
                Assert(s.AllowsLabel("person") && !s.AllowsLabel("vehicle"), "type filter applied");

                // Seeding again must NOT clobber the user's choices; neither may a reload.
                settings.Seed("cam1", eventsDefault: false);
                Assert(settings.Get("cam1").Events, "seed does not overwrite user choice");
                var reloaded = new Recording.RecordingSettings(dir);
                Assert(reloaded.Get("cam1").Continuous, "settings persisted across restart");
                Assert(!reloaded.Get("cam1").AllowsLabel("vehicle"), "filter persisted");

                // Migration: settings.json used to live in the recordings root; a fresh
                // config dir must pick it up from there and re-home it.
                var newDir = Path.Combine(dir, "new-config-dir");
                Directory.CreateDirectory(newDir);
                var migrated = new Recording.RecordingSettings(newDir, dir);
                Assert(migrated.Get("cam1").Continuous, "legacy settings migrated");
                Assert(File.Exists(Path.Combine(newDir, "settings.json")), "settings re-homed to config dir");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("user store: hashing, tokens, accounts", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Web.UserStore(dir);
                Assert(!store.Enabled, "auth off until the first account exists");

                var admin = store.Add("admin", "correct horse", admin: true);
                Assert(store.Enabled, "auth on once an account exists");
                Assert(store.Verify("admin", "correct horse") != null, "right password verifies");
                Assert(store.Verify("admin", "wrong horse") == null, "wrong password fails");
                Assert(store.Verify("ADMIN", "correct horse") != null, "usernames are case-insensitive");
                Assert(admin.Hash.StartsWith("pbkdf2-sha256$210000$"), "PBKDF2 format with strong iteration count");

                var token = store.IssueToken(admin);
                Assert(store.ValidateToken(token)?.Name == "admin", "token round-trips");
                Assert(store.ValidateToken(token + "x") == null, "tampered token rejected");
                store.SetPassword("admin", "new password!");
                Assert(store.ValidateToken(token) == null, "password change invalidates old tokens");

                store.Add("viewer", "viewerpass", admin: false);
                Assert(!store.Delete("admin"), "the admin cannot be deleted");
                Assert(store.Delete("viewer"), "normal users can be deleted");

                store.Add("viewer2", "viewerpass", admin: false);
                store.SetSettings("viewer2", "{\"mode\":\"grid\"}");
                var reloaded = new Web.UserStore(dir);
                Assert(reloaded.Enabled, "accounts persist across restart");
                Assert(reloaded.Verify("admin", "new password!") != null, "password persists");
                Assert(reloaded.GetSettings("viewer2").Contains("grid"), "per-user settings persist");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("mqtt packet codec", () =>
        {
            // CONNECT: fixed header, remaining length, protocol name, level, flags.
            var connect = Mqtt.MqttPacket.BuildConnect("neolink", "user", "pw", 30,
                willTopic: "neolink/bridge/state", willPayload: "offline", willRetain: true);
            AssertEq(connect[0], (byte)0x10); // CONNECT
            // variable header protocol name "MQTT"
            AssertEq(Encoding.ASCII.GetString(connect, 4, 4), "MQTT");
            AssertEq(connect[8], (byte)4); // protocol level 3.1.1
            byte flags = connect[9];
            Assert((flags & 0x02) != 0, "clean session");
            Assert((flags & 0x80) != 0, "username flag");
            Assert((flags & 0x40) != 0, "password flag");
            Assert((flags & 0x04) != 0, "will flag");
            Assert((flags & 0x20) != 0, "will retain");

            // PUBLISH round-trips through the incoming parser (topic + payload, QoS 0).
            var pub = Mqtt.MqttPacket.BuildPublish("neolink/cam/motion", Encoding.UTF8.GetBytes("ON"), retain: true);
            AssertEq(pub[0], (byte)0x31); // PUBLISH | retain
            // Strip the fixed header + remaining-length byte to get the body the reader passes on.
            var body = pub.AsSpan(2).ToArray();
            var parsed = Mqtt.MqttPacket.ParsePublish(pub[0], body);
            AssertEq(parsed.Topic, "neolink/cam/motion");
            AssertEq(Encoding.UTF8.GetString(parsed.Payload), "ON");

            // SUBSCRIBE has the reserved 0b0010 low bits and carries the packet id.
            var sub = Mqtt.MqttPacket.BuildSubscribe(7, new[] { "neolink/cam/+/set" });
            AssertEq(sub[0], (byte)0x82);

            // Remaining-length varint encodes multi-byte lengths correctly (321 → 0xC1 0x02).
            var rl = new List<byte>();
            Mqtt.MqttPacket.WriteRemainingLength(rl, 321);
            AssertEq(rl.Count, 2);
            AssertEq(rl[0], (byte)0xC1);
            AssertEq(rl[1], (byte)0x02);
        });

        Test("server health sensors for Home Assistant", () =>
        {
            var sample = new Web.SystemSample(
                UnixMs: 0, CpuPercent: 12.34, WorkingSetBytes: 512L * 1024 * 1024,
                ManagedHeapBytes: 0, AllocMbPerSec: 0, Threads: 0, Handles: 0,
                DiskTotalBytes: 1000L * 1024 * 1024 * 1024, DiskFreeBytes: 250L * 1024 * 1024 * 1024,
                RecordingsBytes: 42L * 1024 * 1024 * 1024, Viewers: 3, RecordingCameras: 2,
                StorageMbPerSec: 7.5, StorageFiles: 0);
            var payloads = Mqtt.HomeAssistantMqtt.ServerStatePayloads(sample, camerasOnline: 5)
                .ToDictionary(p => p.Key, p => p.Value);
            AssertEq(payloads["cpu"], "12.3");
            AssertEq(payloads["memory"], "512");
            AssertEq(payloads["disk_free"], "250");
            AssertEq(payloads["disk_used_pct"], "75");
            AssertEq(payloads["recordings_size"], "42");
            AssertEq(payloads["write_rate"], "7.5");
            AssertEq(payloads["viewers"], "3");
            AssertEq(payloads["cameras_online"], "5");
            AssertEq(payloads["cameras_recording"], "2");

            // Every published key has a matching discovery config — the contract
            // between the state loop and what HA is told to expect.
            Assert(payloads.Keys.All(k => Mqtt.HomeAssistantMqtt.ServerSensors.Any(s => s.Key == k)),
                "every payload key has a discovery sensor");

            // Sources that don't exist simply don't publish: no probed volume,
            // recording disabled — absent beats a misleading zero.
            var headless = sample with { DiskTotalBytes = 0, RecordingsBytes = -1 };
            var keys = Mqtt.HomeAssistantMqtt.ServerStatePayloads(headless, 0).Select(p => p.Key).ToList();
            Assert(!keys.Contains("disk_free") && !keys.Contains("disk_used_pct")
                && !keys.Contains("recordings_size"), "unavailable sources are absent, not zero");

            // Discovery JSON must OMIT unset fields: HA validates every key it
            // sees, rejects explicit nulls ("icon": null) and silently drops the
            // entity — the reason a config built with optional members must go
            // through the null-stripping serializer.
            var json = System.Text.Json.JsonSerializer.Serialize(
                new { name = "X", icon = (string?)null, unit_of_measurement = (string?)null },
                Mqtt.HomeAssistantMqtt.DiscoveryJson);
            AssertEq(json, "{\"name\":\"X\"}");
        });

        Test("config editor: read-modify-write with validation", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "config.json");
            try
            {
                File.WriteAllText(path, """
                    { "bind": "0.0.0.0", "web_port": 8655,
                      "cameras": [ { "name": "cam", "username": "u", "address": "1.2.3.4" } ] }
                    """);

                // A valid edit persists and round-trips through the loader.
                Config.ConfigEditor.Apply(path, root =>
                {
                    Config.ConfigEditor.Set(root, "web_port", 9001);
                    var ui = Config.ConfigEditor.Section(root, "ui");
                    Config.ConfigEditor.Set(ui, "trickle_speed", 8);
                    Config.ConfigEditor.Set(ui, "talk", true);
                });
                var reloaded = Config.NeolinkConfig.Load(path);
                AssertEq(reloaded.WebPort, 9001);
                Assert(Math.Abs(reloaded.Ui.TrickleSpeed - 8) < 0.001, "ui.trickle_speed written");
                Assert(reloaded.Ui.Talk, "ui.talk (beta two-way talk) written");
                Assert(File.Exists(path + ".bak"), "previous config backed up");

                // Camera list (and any unknown fields) survive an unrelated edit.
                AssertEq(reloaded.Cameras.Count, 1);
                AssertEq(reloaded.Cameras[0].Name, "cam");

                // An invalid edit is rejected and the file is left untouched.
                bool threw = false;
                try
                {
                    Config.ConfigEditor.Apply(path, root => Config.ConfigEditor.Set(root, "web_port", 999999));
                }
                catch (FormatException) { threw = true; }
                Assert(threw, "invalid port rejected");
                AssertEq(Config.NeolinkConfig.Load(path).WebPort, 9001); // unchanged
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("clip writer finalizes a seekable classic MP4", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // Feed a synthetic H.264 stream through a hub so the writer can
                // pick up codec parameters, then write a few frames.
                var hub = new Streaming.StreamHub("selftest");
                byte[] Nal(byte type, int len)
                {
                    // Non-zero body: NAL splitting trims trailing zeros and must not
                    // find stray start codes. 0x80 in byte 1 = first_mb_in_slice bit.
                    var nal = new byte[len];
                    Array.Fill(nal, (byte)0xAA);
                    nal[0] = type;
                    nal[1] = 0x80;
                    return nal;
                }
                byte[] Au(params byte[][] nals)
                {
                    var ms = new MemoryStream();
                    foreach (var n in nals)
                    {
                        ms.Write(new byte[] { 0, 0, 0, 1 });
                        ms.Write(n);
                    }
                    return ms.ToArray();
                }
                var sps = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA0 };
                var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
                hub.PublishVideo(new VideoFrame(VideoCodec.H264, Keyframe: true, Microseconds: 0, UnixTime: null,
                    Au(sps, pps, Nal(0x65, 40))));

                var path = Path.Combine(dir, "clip.mp4");
                var writer = Recording.ClipWriter.TryCreate(path, hub);
                Assert(writer != null, "writer created once params are known");
                // Hub indices jump by 2, as they do when audio packets are
                // interleaved. That is NOT a drop: the writer must keep every
                // frame (regression: index-based gap detection dropped all
                // P-frames, producing near-zero-length clips).
                long index = 0;
                uint ts = 1000;
                writer!.Add(new Streaming.HubVideo(index, Au(Nal(0x65, 40)), true, ts));
                for (int i = 0; i < 5; i++)
                {
                    index += 2;
                    writer.Add(new Streaming.HubVideo(index, Au(Nal(0x41, 25)), false, ts += 3000));
                }
                Assert(writer.DurationSeconds > 0.15, "all frames survive audio interleave");
                Assert(writer.ApproxBytes > 0, "byte counter advances (drives the segment size cap)");

                // A real drop (gap flag) resumes at the next keyframe.
                writer.Add(new Streaming.HubVideo(index + 50, Au(Nal(0x41, 25)), false, ts += 3000), gap: true);
                writer.Add(new Streaming.HubVideo(index + 51, Au(Nal(0x65, 40)), true, ts += 3000));

                // Disk work is asynchronous by design: the file is finalized on the
                // writer's own thread after Dispose.
                writer.Dispose();
                Assert(writer.Completion.Wait(TimeSpan.FromSeconds(10)), "writer finalizes in background");
                Assert(!writer.Faulted, "no write faults");

                var bytes = File.ReadAllBytes(path);
                Assert(bytes.Length > 200, "file has content");
                AssertEq(Encoding.ASCII.GetString(bytes, 4, 4), "ftyp");

                // The closed file is finalized into a CLASSIC indexed MP4 of
                // exactly three boxes: ftyp, one free box spanning the retired
                // live header plus every per-frame fragment, and a moov with
                // real sample tables. Players then reach the index in a single
                // skip and seek by byte offset over HTTP.
                static List<(string Type, int Start, int Len)> TopBoxes(byte[] b)
                {
                    var list = new List<(string, int, int)>();
                    int pos = 0;
                    while (pos + 8 <= b.Length)
                    {
                        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
                        if (size < 8 || pos + size > (uint)b.Length) break;
                        list.Add((Encoding.ASCII.GetString(b, pos + 4, 4), pos, (int)size));
                        pos += (int)size;
                    }
                    return list;
                }
                var boxes = TopBoxes(bytes);
                Assert(boxes.Sum(b => b.Len) == bytes.Length, "box sizes cover the whole file");
                AssertEq(boxes.Count, 3);
                AssertEq(boxes[0].Type, "ftyp");
                AssertEq(boxes[1].Type, "free"); // header + fragments, one skippable span
                AssertEq(boxes[2].Type, "moov"); // the classic index, last box

                int moov = boxes[^1].Start;
                var tail = Encoding.ASCII.GetString(bytes, moov, bytes.Length - moov);
                uint U32At(int pos) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
                uint AfterTag(string tag, int offsetFromTag) =>
                    U32At(moov + tail.IndexOf(tag, StringComparison.Ordinal) + offsetFromTag);

                // 7 frames × 3000 ticks: mvhd carries ms, mdhd 90 kHz ticks.
                AssertEq(AfterTag("mvhd", 20), 21000u * 1000 / 90000);
                AssertEq(AfterTag("mdhd", 20), 21000u);
                Assert(AfterTag("tkhd", 24) > 0, "tkhd duration set");

                // Sample tables: 7 samples, keyframes 1 and 7, offsets that land
                // exactly on the length-prefixed NAL data inside the old mdats.
                // (Offsets are tag-relative: box start + 4.)
                AssertEq(AfterTag("stsz", 12), 7u);     // sample count
                AssertEq(AfterTag("stss", 8), 2u);      // two keyframes
                AssertEq(AfterTag("stss", 12), 1u);
                AssertEq(AfterTag("stss", 16), 7u);
                AssertEq(AfterTag("stco", 8), 7u);      // one chunk per sample
                uint firstSample = AfterTag("stco", 12);
                AssertEq(U32At((int)firstSample), 40u); // 4-byte NAL length prefix
                AssertEq(bytes[firstSample + 4], (byte)0x65); // ... of the IDR NAL
                Assert(!tail.Contains("mfra") && !tail.Contains("mvex"),
                    "classic moov carries no fragmented leftovers");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("virtual classic index serves old fragmented recordings untouched", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // Compose a file exactly as the OLD writer left them: live init,
                // one moof+mdat per frame, mfra trailer. This is what upgraded
                // installs have on disk, terabytes of it — it must serve fast
                // without any migration touching it.
                var sps = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA0 };
                var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
                var init = FMp4.BuildInit(VideoCodec.H264, sps, pps, null, 640, 360);
                var path = Path.Combine(dir, "old.mp4");
                using (var fs = File.Create(path))
                {
                    fs.Write(init);
                    ulong dt = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        bool key = i % 5 == 0;
                        var sample = new byte[24];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sample, 20);
                        sample[4] = key ? (byte)0x65 : (byte)0x41;
                        fs.Write(FMp4.BuildFragment((uint)(i + 1), dt, 3000, sample, key));
                        dt += 3000;
                    }
                    var mfra = new byte[16]; // legacy trailer: the scan must stop here
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mfra, 16);
                    Encoding.ASCII.GetBytes("mfra").CopyTo(mfra, 4);
                    fs.Write(mfra);
                }
                var original = File.ReadAllBytes(path);

                byte[] served;
                using (var v = Recording.VirtualMp4.Open(path))
                {
                    served = new byte[v.Length];
                    v.ReadExactly(served);

                    // The virtual view is the classic 3-box shape; the legacy
                    // mfra trailer is not part of it.
                    int pos = 0, moovStart = 0;
                    var types = new List<string>();
                    while (pos + 8 <= served.Length)
                    {
                        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(served.AsSpan(pos));
                        types.Add(Encoding.ASCII.GetString(served, pos + 4, 4));
                        moovStart = pos;
                        pos += (int)size;
                    }
                    AssertEq(pos, served.Length);
                    AssertEq(string.Join(",", types), "ftyp,free,moov");

                    // Sample tables land byte-exact on the original media data.
                    var tail = Encoding.ASCII.GetString(served, served.Length - 2048, 2048);
                    int tailBase = served.Length - 2048;
                    uint After(string tag, int off) => System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt32BigEndian(served.AsSpan(tailBase + tail.IndexOf(tag, StringComparison.Ordinal) + off));
                    AssertEq(After("stsz", 12), 10u);
                    AssertEq(After("stss", 8), 2u);
                    AssertEq(After("stss", 16), 6u); // second keyframe = sample 6
                    uint firstSample = After("stco", 12);
                    AssertEq(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                        original.AsSpan((int)firstSample)), 20u); // NAL length prefix on disk

                    // Ranged reads must match the full view, including across the
                    // patched header (starts at 28, after ftyp) and the
                    // disk→moov boundary.
                    Span<byte> slice = stackalloc byte[64];
                    foreach (long at in new long[] { 0, 24, moovStart - 32, served.LongLength - 100 })
                    {
                        long a = Math.Clamp(at, 0, v.Length - slice.Length);
                        v.Seek(a, SeekOrigin.Begin);
                        v.ReadExactly(slice);
                        Assert(slice.SequenceEqual(served.AsSpan((int)a, slice.Length)),
                            $"ranged read at {a} matches the full view");
                    }
                }

                // Serving synthesized the index in memory only — the archive
                // file is bit-identical afterwards.
                Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
                    "the on-disk file is untouched");

                // The optional on-disk upgrade still works, once.
                Assert(Recording.ClipWriter.RefinalizeClassic(path), "refinalize converts in place");
                Assert(!Recording.ClipWriter.RefinalizeClassic(path), "second run is a no-op");
                using (var raw = Recording.VirtualMp4.Open(path))
                    AssertEq(raw.Length, new FileInfo(path).Length); // classic → served raw
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("clip writer muxes an AAC audio track", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var hub = new Streaming.StreamHub("audiotest");
                byte[] Nal(byte type, int len)
                {
                    var nal = new byte[len];
                    Array.Fill(nal, (byte)0xAA);
                    nal[0] = type;
                    nal[1] = 0x80;
                    return nal;
                }
                byte[] Au(params byte[][] nals)
                {
                    var ms = new MemoryStream();
                    foreach (var n in nals)
                    {
                        ms.Write(new byte[] { 0, 0, 0, 1 });
                        ms.Write(n);
                    }
                    return ms.ToArray();
                }
                var sps = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA0 };
                var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
                hub.PublishVideo(new VideoFrame(VideoCodec.H264, Keyframe: true, Microseconds: 0, UnixTime: null,
                    Au(sps, pps, Nal(0x65, 40))));

                // One ADTS frame: syncword, MPEG-4/no-CRC, AAC-LC, 16 kHz, mono,
                // frame length 39 (7-byte header + 32-byte payload).
                var adts = new byte[39];
                adts[0] = 0xFF; adts[1] = 0xF1; adts[2] = 0x60; adts[3] = 0x40; adts[4] = 0x04; adts[5] = 0xE0;
                hub.PublishAac(new AacFrame(adts));
                Assert(hub.Audio is { IsAac: true, SampleRate: 16000, Channels: 1 }, "hub learned the AAC track");
                AssertEq(FMp4.AacCodecString(hub.Audio!.AudioSpecificConfig!), "mp4a.40.2");

                var path = Path.Combine(dir, "clip.mp4");
                var writer = Recording.ClipWriter.TryCreate(path, hub);
                Assert(writer != null, "writer created");
                // Audio before the first video keyframe is skipped (track alignment).
                writer!.AddAudio(new Streaming.HubAudioAac(0, new byte[32], 500));
                writer.Add(new Streaming.HubVideo(1, Au(Nal(0x65, 40)), true, 1000));
                writer.AddAudio(new Streaming.HubAudioAac(2, new byte[32], 5000));
                writer.AddAudio(new Streaming.HubAudioAac(3, new byte[32], 5000 + 1024));
                writer.Add(new Streaming.HubVideo(4, Au(Nal(0x41, 25)), false, 4000));
                writer.Dispose();
                Assert(writer.Completion.Wait(TimeSpan.FromSeconds(10)), "writer finalizes");
                Assert(!writer.Faulted, "no write faults");

                var bytes = File.ReadAllBytes(path);
                var text = Encoding.ASCII.GetString(bytes);
                Assert(text.Contains("mp4a"), "AAC sample entry present");
                Assert(text.Contains("soun"), "audio handler present");
                Assert(text.Contains("esds"), "decoder config present");

                static List<int> IndicesOf(string haystack, string needle)
                {
                    var list = new List<int>();
                    for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                         i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
                        list.Add(i);
                    return list;
                }
                // Two tkhd in the retired fragmented header + two in the classic
                // moov; trex only ever existed up front (no mvex in the index).
                AssertEq(IndicesOf(text, "tkhd").Count, 4);
                AssertEq(IndicesOf(text, "trex").Count, 2);

                // The classic moov's media headers (the LAST two mdhd) carry real
                // durations — video in 90 kHz ticks, audio in sample-rate ticks
                // (2 AUs = 2048).
                var mdhds = IndicesOf(text, "mdhd");
                AssertEq(mdhds.Count, 4);
                uint DurAt(int mdhd) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(mdhd + 20));
                Assert(DurAt(mdhds[2]) > 0, "video mdhd duration set");
                AssertEq(DurAt(mdhds[3]), 2048u);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("loopback base for blazor circuits (reverse-proxy fix)", () =>
        {
            // Wildcard binds are not dialable — the circuit's own-server calls
            // must go to plain loopback; a concrete bind address is used as-is.
            AssertEq(Web.WebApi.LoopbackBase("0.0.0.0", 8655), "http://127.0.0.1:8655");
            AssertEq(Web.WebApi.LoopbackBase("::", 8655), "http://127.0.0.1:8655");
            AssertEq(Web.WebApi.LoopbackBase("[::]", 8655), "http://127.0.0.1:8655");
            AssertEq(Web.WebApi.LoopbackBase("*", 8655), "http://127.0.0.1:8655");
            AssertEq(Web.WebApi.LoopbackBase("192.168.1.10", 9000), "http://192.168.1.10:9000");
        });

        Test("update checker: release tag comparison", () =>
        {
            var chk = new Web.UpdateChecker("0.6.0");
            Assert(chk.IsNewer("v0.7.0"), "newer tag detected");
            Assert(chk.IsNewer("V1.0"), "capital V and two-part version accepted");
            Assert(!chk.IsNewer("v0.6.0"), "same version is not an update");
            Assert(!chk.IsNewer("0.5.9"), "older version is not an update");
            Assert(!chk.IsNewer("not-a-version"), "junk tag ignored");

            // Suffixed builds (test tars, prereleases) compare by their numeric
            // prefix — a 0.8.5-events-test box must NOT be offered 0.8.4 as an
            // "update" (the regression: the suffix failed to parse, the running
            // version fell back to 0.0, and every release looked newer).
            var test = new Web.UpdateChecker("0.8.5-events-test");
            Assert(!test.IsNewer("v0.8.4"), "release older than a test build's base is not an update");
            Assert(!test.IsNewer("v0.8.5"), "the test build's own base release is not an update");
            Assert(test.IsNewer("v0.8.6"), "a genuinely newer release still is");
        });

        Test("camera control: zoom/siren/floodlight wire formats", () =>
        {
            // These bodies must match the reference Rust neolink structurally —
            // element names, nesting and field values are the contract.
            var zf = Protocol.BcCameraCommands.BuildStartZoomFocus(0, "zoomPos", 27);
            AssertEq(zf.Name.LocalName, "StartZoomFocus");
            AssertEq((string?)zf.Attribute("version") ?? "", Bc.Xml.BcXmlBody.XmlVersion);
            AssertEq((string?)zf.Element("channelId") ?? "", "0");
            AssertEq((string?)zf.Element("command") ?? "", "zoomPos");
            AssertEq((string?)zf.Element("movePos") ?? "", "27");

            var siren = Protocol.BcCameraCommands.BuildAudioAlarmPlay(1);
            AssertEq(siren.Name.LocalName, "audioPlayInfo");
            AssertEq((long?)siren.Element("channelId") ?? -1, 1L);
            AssertEq((long?)siren.Element("playMode") ?? -1, 0L);
            AssertEq((long?)siren.Element("playDuration") ?? -1, 0L);
            AssertEq((long?)siren.Element("playTimes") ?? -1, 1L);
            AssertEq((long?)siren.Element("onOff") ?? -1, 0L);

            var fl = Protocol.BcCameraCommands.BuildFloodlightManual(0, on: true, durationSeconds: 30);
            AssertEq(fl.Name.LocalName, "FloodlightManual");
            AssertEq((string?)fl.Attribute("version") ?? "", "1");
            AssertEq((long?)fl.Element("status") ?? -1, 1L);
            AssertEq((long?)fl.Element("duration") ?? -1, 30L);

            // Manual (latched) siren: playMode 2 with onOff as the switch, per
            // Home Assistant's reolink library.
            var manual = Protocol.BcCameraCommands.BuildAudioAlarmManual(0, on: true);
            AssertEq((long?)manual.Element("playMode") ?? -1, 2L);
            AssertEq((long?)manual.Element("onOff") ?? -1, 1L);
            AssertEq((long?)Protocol.BcCameraCommands.BuildAudioAlarmManual(0, on: false).Element("onOff") ?? -1, 0L);

            // Privacy-mode write body (msg 623): operate 2 = set, sleep 0/1.
            var sleep = Protocol.BcCameraCommands.BuildSleepState(on: true);
            AssertEq(sleep.Name.LocalName, "sleepState");
            AssertEq((long?)sleep.Element("operate") ?? -1, 2L);
            AssertEq((long?)sleep.Element("sleep") ?? -1, 1L);

            // The <sleep> boolean parses from either a bare or a nested reply.
            var nested = Bc.Xml.BcXmlBody.FromRaw(System.Xml.Linq.XElement.Parse(
                "<sleepState version=\"1.1\"><channelId>0</channelId><sleep>1</sleep></sleepState>"));
            AssertEq(Protocol.BcCameraCommands.ParseSleepValue(nested), (bool?)true);
            AssertEq(Protocol.BcCameraCommands.ParseSleepValue(null), (bool?)null);

            // Privacy support is gated on the login DeviceInfo advertising <sleep>
            // — models without the feature still answer the state query.
            var withSleep = Bc.Xml.DeviceInfoXml.Parse(System.Xml.Linq.XElement.Parse(
                "<DeviceInfo><resolution><width>1</width><height>1</height></resolution><sleep>0</sleep></DeviceInfo>"));
            var withoutSleep = Bc.Xml.DeviceInfoXml.Parse(System.Xml.Linq.XElement.Parse(
                "<DeviceInfo><resolution><width>1</width><height>1</height></resolution></DeviceInfo>"));
            Assert(withSleep.HasSleep, "DeviceInfo with <sleep> advertises privacy mode");
            Assert(!withoutSleep.HasSleep, "DeviceInfo without <sleep> does not");

            // Capability gating: only a usable zoom range (maxPos > 0) advertises
            // zoom — fixed-lens cameras answer with 0 (or not at all).
            var range = System.Xml.Linq.XElement.Parse(
                "<PtzZoomFocus version=\"1.1\"><channelId>0</channelId>" +
                "<zoom><maxPos>32</maxPos><minPos>1</minPos><curPos>7</curPos></zoom>" +
                "<focus><maxPos>249</maxPos><minPos>0</minPos><curPos>100</curPos></focus></PtzZoomFocus>");
            AssertEq(Streaming.CameraControl.ZoomMax(range), 32L);
            AssertEq(Streaming.CameraControl.ZoomMax(null), 0L);
        });

        Test("web api: auth gate, token transports, endpoint contracts", () =>
        {
            // Boots the real HTTP API on an ephemeral loopback port (UI disabled)
            // and exercises the contracts the web client binds to. This is the
            // seam merges keep touching: the auth middleware and the JSON shapes.
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            // One event staged on disk before the store loads: the /api/events
            // list and the single-event deep-link lookup have something to find.
            const string evId = "apicam~2026-01-05~101112-sf01";
            var evDir = Path.Combine(dir, "recordings", "apicam", "2026-01-05", "detections", "101112-sf01");
            Directory.CreateDirectory(evDir);
            File.WriteAllText(Path.Combine(evDir, "event.json"),
                $$"""{"id":"{{evId}}","camera":"apicam","startUtc":"2026-01-05T10:11:12Z","endUtc":"2026-01-05T10:11:30Z","labels":["person"]}""");

            using var cts = new CancellationTokenSource();
            Task? server = null;
            try
            {
                int port = FreeTcpPort();
                var store = new Recording.EventStore(Path.Combine(dir, "recordings"));
                store.Load(); // index the staged event, as Program does at startup
                var hub = new Streaming.StreamHub("apicam");
                var cam = new Web.WebCameraInfo("apicam",
                    new List<Web.WebStreamInfo> { new("mainStream", "/apicam/mainStream", hub) },
                    new StubCameraControl("apicam"), PermittedUsers: null);
                server = Web.WebApi.RunAsync(new Web.WebApiOptions
                {
                    BindAddr = "127.0.0.1",
                    Port = port,
                    WebUi = false,
                    Cameras = new[] { cam },
                    Users = new Dictionary<string, string>(),
                    RtspPort = 8654,
                    Events = store,
                    RecordingSettings = new Recording.RecordingSettings(dir),
                    UserStore = new Web.UserStore(dir),
                    Version = "0.0.0-selftest",
                    ConfigPath = Path.Combine(dir, "config.json"),
                    RestartRequested = () => { },
                }, cts.Token);

                using var http = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                    Timeout = TimeSpan.FromSeconds(10),
                };

                // Wait for Kestrel to accept; surface a startup crash immediately.
                System.Text.Json.JsonElement features = default;
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (true)
                {
                    if (server.IsCompleted) server.GetAwaiter().GetResult();
                    try { features = GetJson(http, "/api/features"); break; }
                    catch when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
                }

                // Feature discovery — what ApiFeaturesInfo binds to.
                Assert(features.GetProperty("events").GetBoolean(), "events feature on (store wired)");
                AssertEq(features.GetProperty("version").GetString()!, "0.0.0-selftest");
                Assert(features.TryGetProperty("trickleSpeed", out _), "trickleSpeed exposed");
                AssertEq(features.GetProperty("repoUrl").GetString()!, Web.UpdateChecker.RepoUrl);

                // Two-way talk is a beta opt-in: off by default, and the endpoint
                // refuses (403) rather than upgrading the socket while disabled.
                Assert(!features.GetProperty("talk").GetBoolean(), "talk beta defaults off");
                AssertEq((int)http.GetAsync("/api/talk?camera=apicam").Result.StatusCode, 403);

                // Camera list — what ApiCamera/ApiStream bind to; open while no accounts exist.
                var cams = GetJson(http, "/api/cameras");
                AssertEq(cams.GetArrayLength(), 1);
                AssertEq(cams[0].GetProperty("name").GetString()!, "apicam");
                var stream = cams[0].GetProperty("streams")[0];
                AssertEq(stream.GetProperty("kind").GetString()!, "mainStream");
                AssertEq(stream.GetProperty("path").GetString()!, "/apicam/mainStream");
                AssertEq(stream.GetProperty("rtspPort").GetInt32(), 8654);

                // CORS preflight short-circuits (the web client may be served from anywhere).
                using (var preflight = http.Send(new HttpRequestMessage(HttpMethod.Options, "/api/cameras")))
                {
                    AssertEq((int)preflight.StatusCode, 204);
                    AssertEq(preflight.Headers.GetValues("Access-Control-Allow-Origin").First(), "*");
                }

                // First account = the admin; creating it turns authentication ON.
                var setup = PostJson(http, "/api/auth/setup",
                    """{"username":"admin","password":"correct horse"}""");
                var token = setup.GetProperty("token").GetString()!;
                Assert(setup.GetProperty("admin").GetBoolean(), "first account is the admin");
                using (var again = PostRaw(http, "/api/auth/setup", """{"username":"x","password":"yyyyyyyy"}"""))
                    AssertEq((int)again.StatusCode, 409); // setup is one-shot

                // The gate: every /api route except the auth handshake now needs a session.
                AssertEq((int)http.GetAsync("/api/cameras").Result.StatusCode, 401);
                AssertEq((int)http.GetAsync("/api/features").Result.StatusCode, 401);
                AssertEq((int)http.GetAsync("/api/auth/status").Result.StatusCode, 200);

                // Both token transports authenticate: Bearer header (component fetches)
                // and ?token= (media elements + the stream WebSocket, where headers can't go).
                using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/cameras"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 200);
                }
                var tokenQ = $"?token={Uri.EscapeDataString(token)}";
                AssertEq((int)http.GetAsync($"/api/cameras{tokenQ}").Result.StatusCode, 200);
                AssertEq((int)http.GetAsync($"/api/cameras{tokenQ}x").Result.StatusCode, 401); // tampered

                // Login: wrong password rejected (401), right one issues a token.
                using (var bad = PostRaw(http, "/api/auth/login", """{"username":"admin","password":"wrong horse"}"""))
                    AssertEq((int)bad.StatusCode, 401);
                var login = PostJson(http, "/api/auth/login", """{"username":"admin","password":"correct horse"}""");
                Assert(login.GetProperty("token").GetString()!.Length > 20, "login issues a token");

                // Per-camera recording switches round-trip through the API.
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"/api/cameras/apicam/recording{tokenQ}")
                       { Content = new StringContent("""{"events":true}""", Encoding.UTF8, "application/json") })
                using (var res = http.Send(req))
                    AssertEq((int)res.StatusCode, 200);
                var rec = GetJson(http, $"/api/cameras/apicam/recording{tokenQ}");
                Assert(rec.GetProperty("events").GetBoolean(), "recording switch persisted via API");
                Assert(rec.TryGetProperty("continuous", out _), "continuous switch exposed");

                // Recorded-events listing sees the staged event, and the
                // single-event lookup (notification deep links) round-trips.
                var evList = GetJson(http, $"/api/events{tokenQ}");
                AssertEq(evList.GetArrayLength(), 1);
                AssertEq(evList[0].GetProperty("id").GetString()!, evId);
                var one = GetJson(http, $"/api/events/{Uri.EscapeDataString(evId)}{tokenQ}");
                AssertEq(one.GetProperty("id").GetString()!, evId);
                AssertEq(one.GetProperty("camera").GetString()!, "apicam");
                Assert(!one.GetProperty("hasClip").GetBoolean(), "staged event has no clip");
                AssertEq((int)http.GetAsync($"/api/events/nope{tokenQ}").Result.StatusCode, 404);
            }
            finally
            {
                cts.Cancel();
                try { server?.Wait(TimeSpan.FromSeconds(15)); } catch { }
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        if (rustRepoPath != null)
        {
            var bcSamples = Path.Combine(rustRepoPath, "crates", "core", "src", "bc", "samples");
            var mediaSamples = Path.Combine(rustRepoPath, "crates", "core", "src", "bcmedia", "samples");
            if (!Directory.Exists(bcSamples))
            {
                Console.WriteLine($"! sample dir not found: {bcSamples} (skipping sample tests)");
            }
            else
            {
                RunSampleTests(bcSamples, mediaSamples);
            }
        }
        else
        {
            Console.WriteLine("(pass --config <path-to-rust-neolink-repo> to also run protocol sample tests)");
        }

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0;
    }

    private static void RunSampleTests(string bcSamples, string mediaSamples)
    {
        Test("sample: xml_crypto BCEncrypt", () =>
        {
            var enc = File.ReadAllBytes(Path.Combine(bcSamples, "xml_crypto_sample1.bin"));
            var plain = File.ReadAllBytes(Path.Combine(bcSamples, "xml_crypto_sample1_plaintext.bin"));
            AssertSeq(XmlCrypto.BcXor(0, enc), plain);
        });

        Test("sample: legacy login deserialize + byte-exact reserialize", () =>
        {
            var sample = File.ReadAllBytes(Path.Combine(bcSamples, "model_sample_legacy_login.bin"));
            var msg = ReadMessage(sample);
            AssertEq(msg.Meta.MsgId, 1u);
            AssertEq(msg.Meta.Class, (ushort)0x6514);
            AssertEq(msg.Meta.ResponseCode, (ushort)0xdc01);
            AssertEq(msg.LegacyUsername!, "21232F297A57A5A743894A0E4A801FC\0");
            AssertEq(msg.LegacyPassword!, BcConstants.EmptyLegacyPassword);

            var enc = new EncryptionState();
            enc.Set(EncryptionKind.BcEncrypt);
            var reser = BcCodec.Serialize(msg, enc);
            AssertSeq(reser, sample);
        });

        Test("sample: modern login reply (nonce)", () =>
        {
            var sample = File.ReadAllBytes(Path.Combine(bcSamples, "model_sample_modern_login.bin"));
            var msg = ReadMessage(sample);
            AssertEq(msg.Meta.Class, (ushort)0x6614);
            AssertEq(msg.Meta.ResponseCode, (ushort)0xdd01);
            AssertEq(msg.Xml?.Encryption?.Nonce ?? "", "9E6D1FCB9E69846D");
        });

        Test("sample: modern login failed", () =>
        {
            var msg = ReadMessage(File.ReadAllBytes(Path.Combine(bcSamples, "modern_login_failed.bin")));
            AssertEq(msg.Meta.ResponseCode, (ushort)400);
            Assert(msg.IsEmptyModern, "should be empty modern message");
        });

        Test("sample: modern login success", () =>
        {
            var msg = ReadMessage(File.ReadAllBytes(Path.Combine(bcSamples, "modern_login_success.bin")));
            AssertEq(msg.Meta.ResponseCode, (ushort)200);
            Assert(msg.Xml != null, "expected xml payload");
        });

        Test("sample: video start binary mode", () =>
        {
            var ctx = NewContext();
            var msg1 = ReadMessage(File.ReadAllBytes(Path.Combine(bcSamples, "modern_video_start1.bin")), ctx);
            Assert(msg1.Extension?.BinaryData == 1, "binaryData extension");
            AssertEq(msg1.Binary?.Length ?? -1, 32);
            var msg2 = ReadMessage(File.ReadAllBytes(Path.Combine(bcSamples, "modern_video_start2.bin")), ctx);
            AssertEq(msg2.Binary?.Length ?? -1, 30344);
        });

        Test("sample: b800 oddball headers", () =>
        {
            foreach (var f in new[] { "xml_externstream_b800.bin", "xml_substream_b800.bin", "xml_mainstream_b800.bin" })
            {
                var msg = ReadMessage(File.ReadAllBytes(Path.Combine(bcSamples, f)));
                AssertEq(msg.Meta.MsgId, 3u);
                Assert(msg.Xml?.Preview != null, $"{f}: expected Preview xml");
            }
        });

        Test("sample: media info_v1", () =>
        {
            var frame = ReadMediaFrames(Path.Combine(mediaSamples, "info_v1.raw")).First();
            var info = (MediaInfo)frame;
            AssertEq(info.Width, 2560u);
            AssertEq(info.Height, 1440u);
            AssertEq(info.Fps, (byte)30);
        });

        Test("sample: media iframe", () =>
        {
            var files = Enumerable.Range(0, 5).Select(i => Path.Combine(mediaSamples, $"iframe_{i}.raw")).ToArray();
            var frame = (VideoFrame)ReadMediaFrames(files).First();
            AssertEq(frame.Codec, VideoCodec.H264);
            Assert(frame.Keyframe, "keyframe");
            AssertEq(frame.Microseconds, 3557705112u);
            AssertEq(frame.UnixTime ?? 0u, 1628085232u);
            AssertEq(frame.Data.Length, 192881);
        });

        Test("sample: media pframe", () =>
        {
            var files = new[] { Path.Combine(mediaSamples, "pframe_0.raw"), Path.Combine(mediaSamples, "pframe_1.raw") };
            var frame = (VideoFrame)ReadMediaFrames(files).First();
            AssertEq(frame.Codec, VideoCodec.H264);
            Assert(!frame.Keyframe, "pframe");
            AssertEq(frame.Microseconds, 3557767112u);
            AssertEq(frame.Data.Length, 45108);
        });

        Test("sample: media adpcm + decode", () =>
        {
            var frame = (AdpcmFrame)ReadMediaFrames(Path.Combine(mediaSamples, "adpcm_0.raw")).First();
            AssertEq(frame.Data.Length, 244);
            var pcm = Adpcm.BlockToPcm(frame.Data);
            AssertEq(pcm.Length, (244 - 4) * 2 * 2);
        });

        Test("sample: full swann stream demux", () =>
        {
            var files = Enumerable.Range(0, 10)
                .Select(i => Path.Combine(mediaSamples, $"video_stream_swan_{i:00}.raw")).ToArray();
            var frames = ReadMediaFrames(files).ToList();
            // The capture contains exactly 5 frames: 1 IFrame, 2 PFrames, 2 ADPCM blocks
            AssertEq(frames.Count, 5);
            AssertEq(frames.OfType<VideoFrame>().Count(), 3);
            AssertEq(frames.OfType<AdpcmFrame>().Count(), 2);
        });

        Test("sample: argus2 extended headers", () =>
        {
            var ifiles = Enumerable.Range(0, 5).Select(i => Path.Combine(mediaSamples, $"argus2_iframe_{i}.raw"));
            var pfiles = Enumerable.Range(0, 18).Select(i => Path.Combine(mediaSamples, $"argus2_pframe_{i}.raw"));
            Assert(ReadMediaFrames(ifiles.ToArray()).Count > 0, "argus2 iframe set");
            Assert(ReadMediaFrames(pfiles.ToArray()).Count > 0, "argus2 pframe set");
        });
    }

    // ------------------------------------------------------------- helpers

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static System.Text.Json.JsonElement GetJson(HttpClient http, string path)
    {
        using var res = http.GetAsync(path).GetAwaiter().GetResult();
        res.EnsureSuccessStatusCode();
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private static HttpResponseMessage PostRaw(HttpClient http, string path, string json) =>
        http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"))
            .GetAwaiter().GetResult();

    private static System.Text.Json.JsonElement PostJson(HttpClient http, string path, string json)
    {
        using var res = PostRaw(http, path, json);
        res.EnsureSuccessStatusCode();
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    /// <summary>An always-online camera that supports nothing — the API test only
    /// needs a control surface to exist, not to do anything.</summary>
    private sealed class StubCameraControl(string name) : Streaming.ICameraControl
    {
        public string CameraName => name;
        public bool Online => true;
        public bool CanSetStreamSettings => false;
        public Task<IReadOnlyList<Streaming.StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Streaming.StreamEncSetting>?>(null);
        public Task<Streaming.CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Bc.Xml.StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct) =>
            Task.FromResult<Bc.Xml.StreamInfoListXml?>(null);
        public Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
            uint? framerate, uint? bitrate, CancellationToken ct) => throw new NotSupportedException();
        public Task<System.Xml.Linq.XElement?> GetBatteryInfoAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task<byte[]?> SnapshotAsync(CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<System.Xml.Linq.XElement?> GetLedStateAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task SetLedStateAsync(string? state, string? lightState, CancellationToken ct) => Task.CompletedTask;
        public Task<System.Xml.Linq.XElement?> GetPirStateAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task SetPirEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task PtzAsync(string command, float speed, CancellationToken ct) => Task.CompletedTask;
        public Task RebootAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<System.Xml.Linq.XElement?> GetZoomFocusAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct) => Task.CompletedTask;
        public Task SirenAsync(bool? on, CancellationToken ct) => Task.CompletedTask;
        public Task<bool?> GetPrivacyModeAsync(CancellationToken ct) => Task.FromResult<bool?>(null);
        public Task SetPrivacyModeAsync(bool on, CancellationToken ct) => Task.CompletedTask;
        public Task<System.Xml.Linq.XElement?> GetFloodlightTasksAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task SetFloodlightTasksAsync(System.Xml.Linq.XElement task, CancellationToken ct) => Task.CompletedTask;
        public Task TalkAsync(int sampleRate, ChannelReader<byte[]> pcm, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static BcContext NewContext()
    {
        var enc = new EncryptionState();
        enc.Set(EncryptionKind.BcEncrypt);
        return new BcContext(enc);
    }

    private static BcMessage ReadMessage(byte[] data, BcContext? ctx = null)
    {
        using var ms = new MemoryStream(data);
        return BcCodec.ReadMessageAsync(ms, ctx ?? NewContext(), CancellationToken.None).GetAwaiter().GetResult();
    }

    private static List<MediaFrame> ReadMediaFrames(params string[] files)
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        foreach (var f in files)
            channel.Writer.TryWrite(File.ReadAllBytes(f));
        channel.Writer.Complete();

        var reader = new MediaFrameReader(channel.Reader);
        var frames = new List<MediaFrame>();
        try
        {
            while (true)
                frames.Add(reader.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
        }
        catch (EndOfStreamException)
        {
            return frames;
        }
    }

    private static void Test(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok: {name}");
            _passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {name}: {ex.Message}");
            _failed++;
        }
    }

    private static void Assert(bool cond, string what)
    {
        if (!cond) throw new Exception($"assertion failed: {what}");
    }

    private static void AssertEq<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception($"expected {expected}, got {actual}");
    }

    private static void AssertSeq(IReadOnlyList<byte> actual, IReadOnlyList<byte> expected)
    {
        if (actual.Count != expected.Count)
            throw new Exception($"length mismatch: expected {expected.Count}, got {actual.Count}");
        for (int i = 0; i < actual.Count; i++)
            if (actual[i] != expected[i])
                throw new Exception($"byte mismatch at {i}: expected 0x{expected[i]:x2}, got 0x{actual[i]:x2}");
    }
}
