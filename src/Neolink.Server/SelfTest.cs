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
                AssertEq(cfg.Cameras.Count, 1);
                AssertEq(cfg.Cameras[0].Host, "192.168.1.187");
                AssertEq(cfg.Cameras[0].Port, 9000); // default port applied
                AssertEq(cfg.Users.Count, 1);
                var permitted = cfg.PermittedUsersFor(cfg.Cameras[0]);
                Assert(permitted != null && permitted.Contains("me"), "permitted users");
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
                });
                var reloaded = Config.NeolinkConfig.Load(path);
                AssertEq(reloaded.WebPort, 9001);
                Assert(Math.Abs(reloaded.Ui.TrickleSpeed - 8) < 0.001, "ui.trickle_speed written");
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

        Test("clip writer produces a playable fmp4 structure", () =>
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
                Assert(bytes.Length >= 16, "has boxes");
                // moov must follow, and at least one moof/mdat pair must exist
                var text = Encoding.ASCII.GetString(bytes);
                Assert(text.Contains("moov"), "init segment present");
                Assert(text.Contains("moof") && text.Contains("mdat"), "fragments present");

                // The closed file must carry a real duration: browsers trust mvhd,
                // and duration 0 freezes the <video> on the first frame.
                static uint DurationAfterTag(byte[] data, string tag, int offsetFromTag)
                {
                    int i = Encoding.ASCII.GetString(data).IndexOf(tag, StringComparison.Ordinal);
                    Assert(i > 0, $"{tag} box present");
                    return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                        data.AsSpan(i + offsetFromTag));
                }
                Assert(DurationAfterTag(bytes, "mvhd", 20) > 0, "mvhd duration patched (ms)");
                Assert(DurationAfterTag(bytes, "tkhd", 24) > 0, "tkhd duration patched (ms)");
                Assert(DurationAfterTag(bytes, "mdhd", 20) > 0, "mdhd duration patched (90kHz)");

                // The seek index players use: mfra at the end, mfro trailer pointing at it.
                uint mfraSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4));
                Assert(mfraSize > 24 && mfraSize < bytes.Length, "mfro trailer carries mfra size");
                AssertEq(Encoding.ASCII.GetString(bytes, bytes.Length - (int)mfraSize + 4, 4), "mfra");
                Assert(text.Contains("tfra"), "tfra keyframe index present");
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
                AssertEq(IndicesOf(text, "tkhd").Count, 2);
                AssertEq(IndicesOf(text, "trex").Count, 2);

                // Both media headers carry a real duration after finalization —
                // video in 90 kHz ticks, audio in sample-rate ticks (2 AUs = 2048).
                var mdhds = IndicesOf(text, "mdhd");
                AssertEq(mdhds.Count, 2);
                uint DurAt(int mdhd) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(mdhd + 20));
                Assert(DurAt(mdhds[0]) > 0, "video mdhd duration patched");
                AssertEq(DurAt(mdhds[1]), 2048u);
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
        });

        Test("web api: auth gate, token transports, endpoint contracts", () =>
        {
            // Boots the real HTTP API on an ephemeral loopback port (UI disabled)
            // and exercises the contracts the web client binds to. This is the
            // seam merges keep touching: the auth middleware and the JSON shapes.
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            using var cts = new CancellationTokenSource();
            Task? server = null;
            try
            {
                int port = FreeTcpPort();
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
                    Events = new Recording.EventStore(Path.Combine(dir, "recordings")),
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

                // Recorded-events listing responds (empty store).
                AssertEq(GetJson(http, $"/api/events{tokenQ}").GetArrayLength(), 0);
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
