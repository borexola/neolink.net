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

        Test("aes-cfb fast paths match the reference implementation", () =>
        {
            // The wire path now uses AesCfbDecrypt (one keystream pass) and
            // AesCfbEncrypt (cached key schedule). Both must be byte-identical to
            // the block-loop reference for every length shape: empty, sub-block,
            // aligned, one over, and video-sized — a mismatch on FullAes firmware
            // would corrupt every frame.
            var key = Encoding.ASCII.GetBytes("0123456789ABCDEF");
            var state = new Bc.EncryptionState();
            state.Set(Bc.EncryptionKind.FullAes, key);
            foreach (var len in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 100, 1000, 65536 + 7 })
            {
                var plain = new byte[len];
                Random.Shared.NextBytes(plain);
                var refCipher = XmlCrypto.AesCfb(plain, key, encrypting: true);
                AssertSeq(XmlCrypto.AesCfbEncrypt(plain, state), refCipher);
                AssertSeq(XmlCrypto.AesCfbDecrypt(refCipher, state), plain);
            }

            // Rekeying mid-session (relogin) must not serve stale keystream from
            // the cached AES instance.
            var key2 = Encoding.ASCII.GetBytes("FEDCBA9876543210");
            var probe = new byte[48];
            Random.Shared.NextBytes(probe);
            state.SetAesKey(key2);
            AssertSeq(XmlCrypto.AesCfbDecrypt(XmlCrypto.AesCfb(probe, key2, encrypting: true), state), probe);
        });

        Test("media-on-demand policy: who holds control-only connections", () =>
        {
            // On-demand video (connected for sensors/controls, video only while
            // consumed) applies to wired cameras with always_on unset. Whether
            // such a camera actually idles is then decided LIVE: no viewers AND
            // no recording switch on (events, 24/7, or a running clip capture) —
            // the switches count, not whether a recorder object is wired.
            Assert(Streaming.CameraService.MediaOnDemandPolicy(null, false),
                "wired camera, default power settings -> may idle on demand");
            Assert(!Streaming.CameraService.MediaOnDemandPolicy(true, false),
                "explicit always_on -> always stream");
            Assert(!Streaming.CameraService.MediaOnDemandPolicy(false, false),
                "explicit always_on:false -> the park mode governs, not on-demand");
            Assert(!Streaming.CameraService.MediaOnDemandPolicy(null, true),
                "battery camera -> full park governs, not on-demand");
        });

        Test("wake-capture edge detector: debounced sleep, prompt wake", () =>
        {
            // The sleep→wake edge only counts after CONSECUTIVE unanswered probes:
            // one lost packet (Wi-Fi power save) must not arm it, or the next
            // answered probe reads as a false "camera woke itself" and Neolink
            // wakes the very camera it is letting sleep (issue #44).
            var d = new Streaming.WakeEdgeDetector();
            Assert(!d.OnProbe(true) && !d.Armed, "answered while awake -> nothing");
            Assert(!d.OnProbe(false) && !d.Armed, "one miss -> not armed yet (could be packet loss)");
            Assert(!d.OnProbe(true) && !d.Armed, "answer after a single miss -> miss forgotten");
            Assert(!d.OnProbe(false), "miss 1 of 2");
            Assert(!d.OnProbe(false) && d.Armed, "second consecutive miss -> armed");
            Assert(!d.OnProbe(false) && d.Armed, "still asleep -> stays armed");
            Assert(d.OnProbe(true), "answer while armed -> the wake edge");

            var flappy = new Streaming.WakeEdgeDetector();
            for (int i = 0; i < 50; i++)
            {
                Assert(!flappy.OnProbe(false), $"alternating miss {i} never arms");
                Assert(!flappy.OnProbe(true), $"alternating answer {i} never fires a wake");
            }
            Assert(!flappy.Armed, "alternating loss pattern -> never armed, never a false wake");
        });

        Test("stream hub GOP cache: new viewer primed keyframe-first, resets per keyframe", () =>
        {
            // A new viewer must receive the buffered GOP (keyframe onward) at once so
            // it can decode immediately instead of waiting for the camera's next
            // keyframe — the main cause of slow first-frame on a multi-tile refresh.
            var hub = new Streaming.StreamHub("t");
            void Pub(bool key, int size, uint us) =>
                hub.PublishVideo(new Media.VideoFrame(Media.VideoCodec.H264, key, us, null, new byte[size]));

            // Before any keyframe there is nothing decodable to prime with.
            var (id0, r0) = hub.Subscribe();
            Assert(!r0.TryRead(out _), "no keyframe yet -> new viewer primed with nothing");
            hub.Unsubscribe(id0);

            // A GOP: keyframe then two P-frames. A viewer joining now gets all three.
            Pub(true, 100, 1000);
            Pub(false, 40, 2000);
            Pub(false, 40, 3000);
            var (id1, r1) = hub.Subscribe();
            var got = new List<Streaming.HubPacket>();
            while (r1.TryRead(out var p)) got.Add(p);
            Assert(got.Count == 3, "primed with the whole current GOP");
            Assert(got[0] is Streaming.HubVideo { Keyframe: true }, "primed GOP starts at the keyframe");

            // A fresh keyframe resets the GOP — the previous frames are gone.
            Pub(true, 100, 4000);
            Pub(false, 40, 5000);
            var (id2, r2) = hub.Subscribe();
            var got2 = new List<Streaming.HubPacket>();
            while (r2.TryRead(out var p)) got2.Add(p);
            Assert(got2.Count == 2, "new keyframe starts a fresh GOP (old frames dropped)");
            Assert(got2[0] is Streaming.HubVideo { Keyframe: true }, "fresh GOP starts at the new keyframe");

            // An already-subscribed viewer keeps getting live packets, not a re-prime.
            var (id3, r3) = hub.Subscribe();
            while (r3.TryRead(out _)) { } // drain the prime
            Pub(false, 40, 6000);
            Assert(r3.TryRead(out var live) && live is Streaming.HubVideo, "live packet reaches an existing subscriber");
            hub.Unsubscribe(id1); hub.Unsubscribe(id2); hub.Unsubscribe(id3);

            // Source gone: the cached GOP is stale — a joiner must get NOTHING
            // (clicking an offline camera played a second of old video and froze).
            hub.SourceStopped();
            var (id4, r4) = hub.Subscribe();
            Assert(!r4.TryRead(out _), "source stopped -> new viewer primed with nothing");
            hub.Unsubscribe(id4);

            // The next session's keyframe reopens the cache and priming resumes.
            Pub(true, 100, 7000);
            Pub(false, 40, 8000);
            var (id5, r5) = hub.Subscribe();
            var got5 = new List<Streaming.HubPacket>();
            while (r5.TryRead(out var p)) got5.Add(p);
            Assert(got5.Count == 2 && got5[0] is Streaming.HubVideo { Keyframe: true },
                "reconnected source primes again from its first keyframe");
            hub.Unsubscribe(id5);
        });

        Test("server overload signal: sustained high CPU, ignores brief spikes", () =>
        {
            // Drives the /api/features "overload" flag the dashboard's browser alert
            // fires on — needs several samples averaging near max, so a momentary
            // spike doesn't trip it.
            static Web.SystemSample S(double cpu) => new(0, cpu, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            Assert(!Web.SystemMonitor.OverloadedFrom(Array.Empty<Web.SystemSample>()), "no samples -> not overloaded");
            Assert(!Web.SystemMonitor.OverloadedFrom(new[] { S(100), S(100) }), "too few samples -> not overloaded");
            Assert(Web.SystemMonitor.OverloadedFrom(new[] { S(95), S(92), S(99), S(90), S(94) }),
                "five samples averaging >=90% -> overloaded");
            Assert(!Web.SystemMonitor.OverloadedFrom(new[] { S(100), S(20), S(20), S(20), S(20) }),
                "one spike among low samples -> not overloaded");
            Assert(!Web.SystemMonitor.OverloadedFrom(new[] { S(89), S(89), S(89), S(89), S(89) }),
                "sustained but below threshold -> not overloaded");
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

        Test("channel support flag reads per-channel item then host fallback", () =>
        {
            // Privacy mode is gated on remoteAbility being >0 for the channel
            // (the reolink_aio discriminator that keeps <sleep>-advertising but
            // non-supporting cameras — e.g. RLC "Elite" — out of the privacy UI).
            var host = System.Xml.Linq.XElement.Parse(
                "<Support version=\"1.1\"><remoteAbility>1</remoteAbility></Support>");
            Assert(Streaming.CameraControl.ChannelSupportFlag(host, 0, "remoteAbility"),
                "host-level remoteAbility=1 supported");

            var perChan = System.Xml.Linq.XElement.Parse(
                "<Support version=\"1.1\">" +
                "<item><chnID>0</chnID><remoteAbility>1</remoteAbility></item>" +
                "<item><chnID>1</chnID><remoteAbility>0</remoteAbility></item></Support>");
            Assert(Streaming.CameraControl.ChannelSupportFlag(perChan, 0, "remoteAbility"),
                "channel 0 remoteAbility=1 supported");
            Assert(!Streaming.CameraControl.ChannelSupportFlag(perChan, 1, "remoteAbility"),
                "channel 1 remoteAbility=0 unsupported");

            // The Elite case: <sleep> is advertised elsewhere but remoteAbility is
            // absent → the second gate fails → privacy stays off.
            var elite = System.Xml.Linq.XElement.Parse(
                "<Support version=\"1.1\"><ptzMode>none</ptzMode></Support>");
            Assert(!Streaming.CameraControl.ChannelSupportFlag(elite, 0, "remoteAbility"),
                "absent remoteAbility = unsupported (Elite stays out of privacy UI)");
            Assert(!Streaming.CameraControl.ChannelSupportFlag(null, 0, "remoteAbility"),
                "no Support xml = unsupported");
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
                      "uid": "95270000ABCDEFGH",
                      "udp_probe": true,
                      "udp": true,
                      "wake_capture": true,
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
                // UDP-only battery cams: uid + the opt-in discovery probe toggle.
                Assert(cfg.Cameras[0].Uid == null && !cfg.Cameras[0].UdpProbe, "udp probe defaults off");
                AssertEq(cfg.Cameras[1].Uid ?? "", "95270000ABCDEFGH");
                Assert(cfg.Cameras[1].UdpProbe, "udp_probe parsed");
                Assert(cfg.Cameras[1].Udp && !cfg.Cameras[0].Udp, "udp transport flag parsed (opt-in, default off)");
                Assert(cfg.Cameras[1].WakeCapture && !cfg.Cameras[0].WakeCapture, "wake_capture flag parsed (opt-in, default off)");
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

        Test("bcudp discovery wire format (battery-camera probe)", () =>
        {
            // Keystream anchor: crypting zeros with tid 0 must expose the first
            // key word's little-endian bytes (0x1f2d3c4b → 4b 3c 2d 1f); tid
            // offsets every word, so tid 1 bumps the low byte.
            var zeros = new byte[8];
            UdpDiscovery.Crypt(0, zeros);
            AssertEq(Convert.ToHexString(zeros[..4]), "4B3C2D1F");
            zeros = new byte[4];
            UdpDiscovery.Crypt(1, zeros);
            AssertEq(Convert.ToHexString(zeros), "4C3C2D1F");

            // Symmetric: crypt twice with the same tid restores the original.
            var data = Encoding.UTF8.GetBytes("<P2P><C2D_C>roundtrip</C2D_C></P2P>");
            var copy = (byte[])data.Clone();
            UdpDiscovery.Crypt(0x1234abcd, copy);
            Assert(!copy.SequenceEqual(data), "crypt changes the bytes");
            UdpDiscovery.Crypt(0x1234abcd, copy);
            Assert(copy.SequenceEqual(data), "crypt is its own inverse");

            // CRC: table implementation must agree with a naive bit-by-bit
            // reference (raw register: init 0, reflected poly, no final xor).
            static uint BitwiseCrc(byte[] d)
            {
                uint c = 0;
                foreach (var b in d)
                {
                    c ^= b;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
                return c;
            }
            AssertEq(UdpDiscovery.Crc(data), BitwiseCrc(data));

            // A built discovery packet parses back: same tid, same XML, CRC ok;
            // and one flipped payload bit must fail the CRC check.
            var xml = UdpDiscovery.BuildC2dC("95270000ABCDEFGH", 53777, 12345, xmlDeclaration: false);
            Assert(xml.Contains("<uid>95270000ABCDEFGH</uid>") && xml.Contains("<mtu>1350</mtu>")
                && xml.Contains("<cid>12345</cid>") && xml.Contains("<p>WIN</p>"),
                "C2D_C carries uid/mtu/cid/platform");
            var pkt = UdpDiscovery.BuildDiscovery(0xDEADBEEF, xml);
            Assert(UdpDiscovery.TryParseDiscovery(pkt, out var tid, out var back, out _)
                && tid == 0xDEADBEEF && back == xml, "discovery packet roundtrips");
            pkt[^1] ^= 0x01;
            Assert(!UdpDiscovery.TryParseDiscovery(pkt, out _, out _, out var why)
                && why != null && why.Contains("CRC"), "corruption is caught by the checksum");

            // Logs must never carry the full UID.
            var masked = UdpDiscovery.MaskUid($"reply for 95270000ABCDEFGH accepted", "95270000ABCDEFGH");
            Assert(!masked.Contains("95270000ABCDEFGH") && masked.Contains("9527**********GH"),
                "uid is masked in probe logs");

            // Directed-broadcast math: host bits set. The /23 case is the point —
            // a naive /24 guess would send to the wrong broadcast and miss the camera.
            static string Bcast(string ip, string mask) =>
                UdpDiscovery.DirectedBroadcast(System.Net.IPAddress.Parse(ip), System.Net.IPAddress.Parse(mask)).ToString();
            AssertEq(Bcast("192.168.1.50", "255.255.255.0"), "192.168.1.255");
            AssertEq(Bcast("10.0.0.5", "255.255.254.0"), "10.0.1.255");   // /23 spans .0 and .1
            AssertEq(Bcast("172.16.40.9", "255.255.0.0"), "172.16.255.255");
            // The live enumeration must not throw and must yield only IPv4 addresses.
            var live = UdpDiscovery.LocalDirectedBroadcasts().ToList();
            Assert(live.All(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork),
                "directed broadcasts are IPv4");
        });

        Test("bcudp transport: handshake + reliable out-of-order reassembly", () =>
        {
            RunBcUdpTransport().GetAwaiter().GetResult();
        });

        Test("bcudp wake-capture liveness probe (reachable vs silent)", () =>
        {
            RunWakeProbe().GetAwaiter().GetResult();
        });

        Test("camera discovery sweep: ONVIF parse + recommendation table", () =>
        {
            // A real-shaped WS-Discovery ProbeMatch: pull the service URL and only
            // the telling scopes (name/hardware), namespace-agnostic.
            var soap =
                "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" " +
                "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\"><s:Body><d:ProbeMatches>" +
                "<d:ProbeMatch><d:Scopes>onvif://www.onvif.org/name/Reolink onvif://www.onvif.org/hardware/RLC-810A " +
                "onvif://www.onvif.org/Profile/Streaming</d:Scopes>" +
                "<d:XAddrs>http://192.168.1.50/onvif/device_service</d:XAddrs></d:ProbeMatch>" +
                "</d:ProbeMatches></s:Body></s:Envelope>";
            var matches = CameraProbe.ParseOnvifMatches(soap).ToList();
            AssertEq(matches.Count, 1);
            AssertEq(matches[0].XAddrs, "http://192.168.1.50/onvif/device_service");
            Assert(matches[0].Scopes.Contains("/name/Reolink") && matches[0].Scopes.Contains("/hardware/RLC-810A")
                && !matches[0].Scopes.Contains("/Profile/"), "keeps identifying scopes, drops noise");
            Assert(!CameraProbe.ParseOnvifMatches("not xml").Any(), "garbage yields no matches");

            // Recommendation decision table, most-specific first.
            var http = new CameraProbe.HttpResult(true, "Argus", "v3.0");
            var none = new CameraProbe.HttpResult(false, null, null);
            Assert(CameraProbe.Recommend(new() { 9000 }, none, 0, UdpDiscovery.UdpOutcome.Silent).Contains("TCP 9000 is OPEN"),
                "open 9000 wins");
            Assert(CameraProbe.Recommend(new(), none, 0, UdpDiscovery.UdpOutcome.Accepted).Contains("Baichuan-over-UDP"),
                "accepted UDP handshake wins over everything but is reported");
            Assert(CameraProbe.Recommend(new(), http, 0, UdpDiscovery.UdpOutcome.Silent).Contains("HTTP API answers"),
                "HTTP reachability reported when no TCP/UDP");
            Assert(CameraProbe.Recommend(new(), none, 1, UdpDiscovery.UdpOutcome.Silent).Contains("ONVIF"),
                "ONVIF-only path suggested");
            Assert(CameraProbe.Recommend(new(), none, 0, UdpDiscovery.UdpOutcome.Silent).Contains("unreachable"),
                "total silence is called out");
        });

        Test("config with zero cameras loads (first-run web UI boots empty)", () =>
        {
            // A fresh install must NOT crash on an empty camera list — the web UI
            // boots so the user can add cameras. Regression guard for the
            // frictionless first-run (auto-created starter config).
            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmp, """{ "web_port": 8655, "cameras": [] }""");
            try
            {
                var cfg = NeolinkConfig.Load(tmp); // must not throw
                AssertEq(cfg.Cameras.Count, 0);
                AssertEq(cfg.WebPort, 8655);
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
            // Deterministic non-zero fill: random bytes can (1-in-thousands) contain
            // a spurious 00 00 01 start code, which the packetizer rightly treats as
            // a NAL boundary — real encoders emulation-prevent those inside a NAL.
            for (int i = 1; i < bigNal.Length; i++) bigNal[i] = (byte)(i % 251 + 1);
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

            // Crying-sound detection ("cry" captured from an E1 Pro, 2026-07-15):
            // maps to its own label and records by default — it is audio-only, so
            // no other detection would catch the moment.
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(
                new MotionPush("MD", new[] { "cry" }))), "crying");
            Assert(Recording.CameraRecordingSettings.DefaultLabels.Contains("crying")
                && Recording.CameraRecordingSettings.KnownLabels.Contains("crying"),
                "crying is a default and offered detection type");

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
            // A wired camera pushes NetInfo without a numeric signal. That is still
            // worth reporting — "on a cable" is why it has no reading — but it must
            // carry a null signal so nothing publishes a bogus dBm for it.
            var wired = BcCamera.ParseNetInfo(System.Xml.Linq.XElement.Parse(
                "<NetInfo version=\"1.1\"><net_type>ethernet</net_type></NetInfo>"));
            Assert(wired is { SignalDbm: null, NetType: "ethernet" }, "wired push: link type, no reading");
            Assert(BcCamera.ParseNetInfo(System.Xml.Linq.XElement.Parse(
                "<NetInfo version=\"1.1\"/>")) == null, "an empty push says nothing at all");

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

        Test("g711 backchannel: decode + RTP depacketize", () =>
        {
            // Zero-crossing companded codes must decode to (near) silence.
            AssertEq(G711.MuLawToLinear(0xFF), (short)0);
            AssertEq(G711.MuLawToLinear(0x7F), (short)0);
            AssertEq(G711.ALawToLinear(0xD5), (short)8);
            AssertEq(G711.ALawToLinear(0x55), (short)-8);

            // A 440 Hz sine encoded to µ-law then decoded must reconstruct the tone
            // (µ-law is logarithmic, so a few percent RMS error is expected).
            static byte MuLawEncode(short pcm)
            {
                const int bias = 0x84, clip = 32635;
                int sign = (pcm >> 8) & 0x80;
                int mag = Math.Min((sign != 0 ? -pcm : pcm), clip) + bias;
                int exp = 7;
                for (int mask = 0x4000; (mag & mask) == 0 && exp > 0; mask >>= 1) exp--;
                int mantissa = (mag >> (exp + 3)) & 0x0F;
                return (byte)~(sign | (exp << 4) | mantissa);
            }

            const int n = 800; // 100 ms at 8 kHz
            var ulaw = new byte[n];
            var orig = new short[n];
            for (int i = 0; i < n; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * i / 8000.0) * 12000);
                orig[i] = s;
                ulaw[i] = MuLawEncode(s);
            }

            // Wrap the payload in an RTP packet (PT 0, one CSRC, no extension/padding)
            // and run it through the receiver's real depacketizer.
            var packet = new byte[12 + 4 + n];
            packet[0] = 0x81;   // v2, CC=1
            packet[1] = 0x00;   // PT 0 = PCMU
            ulaw.CopyTo(packet, 16);
            var pcm = Rtsp.BackchannelReceiver.Depacketize(packet);
            Assert(pcm != null && pcm.Length == n * 2, "depacketized PCM has one 16-bit sample per code");

            double errSq = 0, sigSq = 0;
            for (int i = 0; i < n; i++)
            {
                short got = (short)(pcm![i * 2] | (pcm[i * 2 + 1] << 8));
                errSq += (double)(orig[i] - got) * (orig[i] - got);
                sigSq += (double)orig[i] * orig[i];
            }
            Assert(Math.Sqrt(errSq / sigSq) < 0.06, "µ-law round-trip reconstructs the tone");

            // A short/garbage frame must be dropped, not throw.
            Assert(Rtsp.BackchannelReceiver.Depacketize(new byte[] { 0x80, 0x00 }) == null, "runt RTP frame ignored");
        });

        Test("rtsp backchannel: DESCRIBE gate → SETUP → talk end-to-end", () =>
        {
            // A real StreamHub made DESCRIBE-ready with one H264 keyframe (SPS+PPS+IDR)
            // plus an ADPCM block so the audio probe resolves immediately.
            var hub = new Streaming.StreamHub("cam");
            var annexB = new byte[]
            {
                0, 0, 0, 1, 0x67, 66, 0, 30,      // SPS
                0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,  // PPS
                0, 0, 0, 1, 0x65, 5, 5, 5,        // IDR
            };
            hub.PublishVideo(new VideoFrame(VideoCodec.H264, true, 0, null, annexB));
            hub.PublishAdpcm(new AdpcmFrame(new byte[] { 0, 0, 0, 0 }));

            var control = new BackchannelStub("cam");
            var server = new Rtsp.RtspServer(new Dictionary<string, string>());
            server.AddMount(new Rtsp.RtspMount { Path = "/cam", Hub = hub, Talk = control });

            int port = FreeTcpPort();
            using var serverCts = new CancellationTokenSource();
            var serverTask = Task.Run(() => server.RunAsync("127.0.0.1", port, serverCts.Token));

            // Explicit IPv4 (the parameterless TcpClient dials through a dual-mode
            // IPv6 socket — ::ffff:127.0.0.1 — which containers without IPv6
            // refuse), and connect with retries: the server task binds on its own
            // schedule, and on a container's cold thread pool a plain Connect
            // reliably beat the Listen and reported "connection refused".
            System.Net.Sockets.TcpClient tcp = null!;
            for (int attempt = 0; ; attempt++)
            {
                tcp = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
                try
                {
                    tcp.Connect(System.Net.IPAddress.Loopback, port);
                    break;
                }
                catch (System.Net.Sockets.SocketException) when (attempt < 50)
                {
                    tcp.Dispose();
                    Thread.Sleep(100);
                }
            }
            using var tcpGuard = tcp;
            var ns = tcp.GetStream();
            ns.ReadTimeout = 5000;
            string baseUri = $"rtsp://127.0.0.1:{port}/cam";
            int cseq = 1;

            void WriteText(string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                ns.Write(b, 0, b.Length);
                ns.Flush();
            }

            (int code, Dictionary<string, string> headers, string body) ReadResponse()
            {
                var hb = new List<byte>();
                while (true)
                {
                    int b = ns.ReadByte();
                    if (b < 0) break;
                    hb.Add((byte)b);
                    int n = hb.Count;
                    if (n >= 4 && hb[n - 4] == 13 && hb[n - 3] == 10 && hb[n - 2] == 13 && hb[n - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(hb.ToArray()).Split("\r\n");
                int code = int.Parse(lines[0].Split(' ')[1]);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int c = line.IndexOf(':');
                    if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
                }
                string body = "";
                if (headers.TryGetValue("Content-Length", out var cls) && int.TryParse(cls, out var cl) && cl > 0)
                {
                    var buf = new byte[cl];
                    int done = 0;
                    while (done < cl) { int r = ns.Read(buf, done, cl - done); if (r <= 0) break; done += r; }
                    body = Encoding.ASCII.GetString(buf);
                }
                return (code, headers, body);
            }

            // DESCRIBE with the ONVIF backchannel Require: the SDP must gain a
            // sendonly µ-law track on trackID=2.
            WriteText($"DESCRIBE {baseUri} RTSP/1.0\r\nCSeq: {cseq++}\r\nAccept: application/sdp\r\n" +
                      "Require: www.onvif.org/ver20/backchannel\r\n\r\n");
            var (dc, _, sdp) = ReadResponse();
            AssertEq(dc, 200);
            Assert(sdp.Contains("m=audio 0 RTP/AVP 0"), "backchannel PCMU track present");
            Assert(sdp.Contains("a=sendonly"), "backchannel is sendonly");
            Assert(sdp.Contains("trackID=2"), "backchannel is trackID=2");

            // A plain DESCRIBE (no Require) must NOT advertise the backchannel.
            WriteText($"DESCRIBE {baseUri} RTSP/1.0\r\nCSeq: {cseq++}\r\nAccept: application/sdp\r\n\r\n");
            var (pdc, _, plainSdp) = ReadResponse();
            AssertEq(pdc, 200);
            Assert(!plainSdp.Contains("a=sendonly"), "plain players get no backchannel track");

            // SETUP the backchannel receive track over TCP interleaved.
            WriteText($"SETUP {baseUri}/trackID=2 RTSP/1.0\r\nCSeq: {cseq++}\r\n" +
                      "Transport: RTP/AVP/TCP;unicast;interleaved=4-5;mode=record\r\n\r\n");
            var (sc, sh, _) = ReadResponse();
            AssertEq(sc, 200);
            Assert(sh["Transport"].Contains("interleaved=4-5"), "echoes the interleaved channel");
            Assert(sh["Transport"].Contains("mode=record"), "backchannel is record mode");
            string session = sh["Session"].Split(';')[0].Trim();

            // PLAY opens the talk session.
            WriteText($"PLAY {baseUri} RTSP/1.0\r\nCSeq: {cseq++}\r\nSession: {session}\r\n\r\n");
            AssertEq(ReadResponse().code, 200);

            // Push one interleaved G.711 µ-law RTP packet on the backchannel channel.
            var ulaw = new byte[] { 0x00, 0x10, 0x40, 0x7F, 0xFF, 0x80, 0xAA, 0x55 };
            var rtp = new byte[12 + ulaw.Length];
            rtp[0] = 0x80; // v2, no CSRC
            rtp[1] = 0x00; // PT 0 = PCMU
            ulaw.CopyTo(rtp, 12);
            var frame = new byte[4 + rtp.Length];
            frame[0] = 0x24;                       // '$'
            frame[1] = 4;                          // interleaved channel 4
            frame[2] = (byte)(rtp.Length >> 8);
            frame[3] = (byte)(rtp.Length & 0xFF);
            rtp.CopyTo(frame, 4);
            ns.Write(frame, 0, frame.Length);
            ns.Flush();

            Assert(control.FirstAudio.Wait(TimeSpan.FromSeconds(5)), "the camera received backchannel audio");
            AssertEq(control.SampleRate, 8000);
            byte[] got;
            lock (control.Received) got = control.Received.ToArray();
            var want = G711.ToPcm16(ulaw, aLaw: false);
            Assert(got.Length >= want.Length, "at least one decoded PCM chunk arrived");
            AssertSeq(got.AsSpan(0, want.Length).ToArray(), want);

            serverCts.Cancel();
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

                // The segment listing reports each file's media length as
                // mtime − the start encoded in its name: the timeline sizes lane
                // coverage with it so a cut-short segment (suspended/offline
                // camera) doesn't claim minutes it doesn't have.
                var segDay = new FileInfo(newSeg).Directory!.Parent!.Name;
                var segStart = DateTime.ParseExact(
                    $"{segDay} {Path.GetFileNameWithoutExtension(newSeg)}", "yyyy-MM-dd HH-mm-ss", null);
                File.SetLastWriteTime(newSeg, segStart.AddSeconds(600));
                var listedSeg = store2.ListSegments("cam1", segDay)
                    .Single(s => s.File == Path.GetFileName(newSeg));
                AssertEq((long)listedSeg.Seconds, 600L);

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

                // On-demand delete: files gone, index gone, size reported, ongoing
                // refused, empty parents pruned.
                var d1 = store3.Create("delcam", DateTime.UtcNow, new[] { "person" });
                d1.Ongoing = false; store3.Save(d1);
                var d1dir = store3.EventDir(d1);
                File.WriteAllText(Path.Combine(d1dir, "clip.mp4"), new string('x', 5000));
                File.WriteAllText(Path.Combine(d1dir, "thumb.jpg"), new string('y', 1000));
                Assert(store3.EventSize(d1.Id) >= 6000, "event size counts its artifacts");

                var ongoing = store3.Create("delcam", DateTime.UtcNow, new[] { "motion" }); // still recording
                Assert(!store3.DeleteEvent(ongoing.Id), "an ongoing event is never deleted");
                Assert(store3.Find(ongoing.Id) != null, "ongoing event survives the refused delete");

                Assert(store3.DeleteEvent(d1.Id), "a finalized event deletes");
                Assert(store3.Find(d1.Id) == null, "deleted event leaves the index");
                Assert(!Directory.Exists(d1dir), "deleted event's folder is gone from disk");
                AssertEq(store3.EventSize(d1.Id), 0L);
                Assert(!store3.DeleteEvent(d1.Id), "deleting an unknown id fails cleanly");
                // The day folder (delcam) still holds the ongoing event, so it stays;
                // deleting a lone event prunes its empty detections + day folders.
                var lone = store3.Create("lonecam", DateTime.UtcNow, new[] { "vehicle" });
                lone.Ongoing = false; store3.Save(lone);
                var loneDay = Path.GetDirectoryName(Path.GetDirectoryName(store3.EventDir(lone)))!;
                Assert(store3.DeleteEvent(lone.Id), "lone event deletes");
                Assert(!Directory.Exists(loneDay), "empty day folder pruned after the last event goes");
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

                // Shared-volume detection: distinct tier paths that report the same
                // capacity (a mis-mounted Docker volume that fell back to root) warn.
                const long GB2 = 1024L * 1024 * 1024;
                var collided = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = mainP, ClipsPath = clipsP, ArchivePath = archiveP },
                    // Main is genuinely separate; clips and archive both fell back to
                    // the same (root) filesystem, so they report byte-identical stats.
                    probe: p => Path.GetFullPath(p) == Path.GetFullPath(mainP)
                        ? (2000 * GB2, 1000 * GB2)
                        : (500 * GB2, 200 * GB2));
                var warnings = collided.SharedVolumeWarnings().ToList();
                AssertEq(warnings.Count, 1);
                Assert(warnings[0].Contains("Clips") && warnings[0].Contains("Archive"),
                    "clips + archive flagged as the same filesystem");
                // Genuinely-distinct capacities raise nothing.
                Assert(!full.SharedVolumeWarnings().Any(), "healthy distinct tiers do not warn");
            }
            finally
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { }
            }
        });

        Test("storage forecast: fill projection, steady state, persistence", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                const long GB = 1024L * 1024 * 1024;
                long free = 500 * GB;
                var locs = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = Path.Combine(dir, "rec") },
                    probe: _ => (1000 * GB, free));
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var fc = new Recording.StorageForecast(locs, dir, () => now);
                var mainPath = locs.MainRoot;

                // Minutes of history extrapolate noise — the verdict stays "measuring".
                fc.SampleNow();
                now = now.AddMinutes(15); free -= GB;
                fc.SampleNow();
                AssertEq(fc.Forecast(mainPath).State, "measuring");

                // A day of losing ~100 GB/day → the projection lands near
                // free/rate (the FULL floor shaves a little off the runway).
                for (int i = 0; i < 96; i++)
                {
                    now = now.AddMinutes(15);
                    free -= 100 * GB / 96;
                    fc.SampleNow();
                }
                var (state, days) = fc.Forecast(mainPath);
                AssertEq(state, "filling");
                Assert(days is > 3 and < 5, $"projected {days:0.00} days to full, expected ≈4");

                // Persistence: a fresh instance (a server restart) reads the same
                // trend from the state dir and reaches the same verdict.
                var reborn = new Recording.StorageForecast(locs, dir, () => now);
                AssertEq(reborn.Forecast(mainPath).State, "filling");

                // Retention keeping up: flat free space over 7+ hours → "steady",
                // never a fictional fill date.
                var dir2 = Path.Combine(dir, "state2");
                Directory.CreateDirectory(dir2);
                var flat = new Recording.StorageForecast(locs, dir2, () => now);
                for (int i = 0; i < 30; i++)
                {
                    now = now.AddMinutes(15);
                    flat.SampleNow();
                }
                AssertEq(flat.Forecast(mainPath).State, "steady");

                // Unknown path (or a location with no samples) stays "measuring".
                AssertEq(fc.Forecast(Path.Combine(dir, "nope")).State, "measuring");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("background-task registry: begin/report/complete lifecycle", () =>
        {
            AssertEq(BackgroundTasks.Active().Count, 0);
            using (var t = BackgroundTasks.Begin("Archiving footage", "measuring", 0))
            {
                var a = BackgroundTasks.Active();
                AssertEq(a.Count, 1);
                AssertEq(a[0].Name, "Archiving footage");
                AssertEq(a[0].Detail!, "measuring");
                t.Report("cam1 · 2026-01-01", 250); // over-100 clamps
                a = BackgroundTasks.Active();
                AssertEq(a[0].Detail!, "cam1 · 2026-01-01");
                AssertEq(a[0].Percent!.Value, 100d);
                t.Report(percent: 42.5); // detail sticks when only percent moves
                a = BackgroundTasks.Active();
                AssertEq(a[0].Percent!.Value, 42.5);
                AssertEq(a[0].Detail!, "cam1 · 2026-01-01");
            }
            AssertEq(BackgroundTasks.Active().Count, 0);
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

                // The progress pre-measure sees exactly the work the pass will do:
                // cam1's event half + continuous half, byte-accurate.
                var policy = new Recording.EventStore.CameraStoragePolicy(
                    EventDays: 7, ContinuousDays: 7,
                    ArchiveEvents: true, ArchiveContinuous: true, ArchiveDeleteDays: 30);
                var plan = store.MeasureArchivePlan(_ => policy);
                AssertEq(plan.Halves, 2);
                long expectBytes = new FileInfo(Path.Combine(oldEvDir, "event.json")).Length
                    + new FileInfo(Path.Combine(oldEvDir, "clip.mp4")).Length
                    + new FileInfo(Path.Combine(oldCont, "08-00-00.mp4")).Length;
                AssertEq(plan.Bytes, expectBytes);

                // Archive on for both types: retention 7 days = footage moves at
                // day 7 (instead of deletion); archive deletes after 30.
                store.Cleanup(_ => policy);
                // The progress entry the pass registered is gone once it finishes.
                AssertEq(BackgroundTasks.Active().Count, 0);

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
                store.SetPageSettings("viewer2", "timeline", "{\"studio\":true}");
                var reloaded = new Web.UserStore(dir);
                Assert(reloaded.Enabled, "accounts persist across restart");
                Assert(reloaded.Verify("admin", "new password!") != null, "password persists");
                Assert(reloaded.GetSettings("viewer2").Contains("grid"), "per-user settings persist");
                Assert(reloaded.GetPageSettings("viewer2", "timeline").Contains("studio"),
                    "per-page settings persist independently");
                Assert(reloaded.GetPageSettings("viewer2", "other") == "{}", "unset page settings read as empty");
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

        Test("mqtt client: caller cancellation can't touch the socket; a stalled write closes it", () =>
            RunMqttCancelledWrite().GetAwaiter().GetResult());

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

        Test("HA storage tiers announced only when configured", () =>
        {
            // "Careful not to send non-existent storage": a single-folder install
            // gets no clips/archive sensors; each optional tier appears only when
            // its path is separately configured.
            var baseDir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            var main = Path.Combine(baseDir, "rec");
            var clips = Path.Combine(baseDir, "clips");
            var arch = Path.Combine(baseDir, "arch");
            try
            {
                (long, long)? probe(string p) => (1000L, 500L);

                var plain = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = main }, probe: probe);
                AssertEq(Mqtt.HomeAssistantMqtt.StorageTierKeys(plain).Count(), 0);

                var clipsOnly = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = main, ClipsPath = clips }, probe: probe);
                var k1 = Mqtt.HomeAssistantMqtt.StorageTierKeys(clipsOnly).Select(t => t.Key).ToList();
                Assert(k1.SequenceEqual(new[] { "clips" }), "clips tier only, no archive");

                var both = new Recording.StorageLocations(
                    new Config.RecordingConfig { Path = main, ClipsPath = clips, ArchivePath = arch }, probe: probe);
                var k2 = Mqtt.HomeAssistantMqtt.StorageTierKeys(both).Select(t => t.Key).ToList();
                Assert(k2.SequenceEqual(new[] { "clips", "archive" }), "both optional tiers");

                AssertEq(Mqtt.HomeAssistantMqtt.StorageTierKeys(null).Count(), 0);
            }
            finally
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { }
            }
        });

        Test("HA availability debounce over a privacy reconnect", () =>
        {
            // A privacy-mode toggle drops the connection for a few seconds; the
            // camera must stay "online" to HA across that blip, but a real outage
            // must still surface after the grace period.
            var grace = TimeSpan.FromSeconds(45);
            var t0 = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
            DateTime? off = null;

            Assert(Mqtt.CameraBridge.AvailabilityOnline(true, true, ref off, t0, grace), "live = online");
            Assert(off == null, "offlineSince cleared while live");

            // Connection drops (privacy toggle / idle close): still online within grace.
            Assert(Mqtt.CameraBridge.AvailabilityOnline(false, true, ref off, t0.AddSeconds(5), grace),
                "brief drop stays online");
            Assert(Mqtt.CameraBridge.AvailabilityOnline(false, true, ref off, t0.AddSeconds(40), grace),
                "still within grace = online");
            // Reconnect before the grace expires: back to a clean online, timer reset.
            Assert(Mqtt.CameraBridge.AvailabilityOnline(true, true, ref off, t0.AddSeconds(44), grace),
                "reconnect = online");
            Assert(off == null, "offlineSince reset on reconnect");

            // A genuine outage past the grace does report offline.
            DateTime? off2 = null;
            Assert(Mqtt.CameraBridge.AvailabilityOnline(false, true, ref off2, t0, grace), "outage starts online");
            Assert(!Mqtt.CameraBridge.AvailabilityOnline(false, true, ref off2, t0.AddSeconds(46), grace),
                "past grace = offline");

            // A camera that has never connected is offline immediately (no false "available").
            DateTime? off3 = null;
            Assert(!Mqtt.CameraBridge.AvailabilityOnline(false, false, ref off3, t0, grace),
                "never-online = offline now");

            // The availability POLICY layered on top: parked-on-purpose (battery
            // doze) counts as alive so retained states stay visible in HA;
            // suspend stays deliberately offline; a camera we WANT but cannot
            // reach is neither live nor asleep and reads offline.
            Assert(Mqtt.CameraBridge.AliveByPolicy(live: true, asleep: false, suspended: false), "streaming = alive");
            Assert(Mqtt.CameraBridge.AliveByPolicy(live: false, asleep: true, suspended: false), "asleep on purpose = alive");
            Assert(!Mqtt.CameraBridge.AliveByPolicy(live: false, asleep: true, suspended: true), "suspended = not alive even if parked");
            Assert(!Mqtt.CameraBridge.AliveByPolicy(live: false, asleep: false, suspended: false), "unreachable = not alive");

            // A parked camera that has NEVER connected (server booted while it
            // slept) is still available — it is healthy, just dozing.
            DateTime? off4 = null;
            Assert(Mqtt.CameraBridge.AvailabilityOnline(
                    Mqtt.CameraBridge.AliveByPolicy(live: false, asleep: true, suspended: false),
                    everOnline: false, ref off4, t0, grace),
                "boot-time-parked battery camera = online in HA");
        });

        Test("sidebar battery icon: fill bar tracks the percentage", () =>
        {
            // The icon must READ the level, not just decorate the number.
            Assert(Neolink.WebClient.UiIcon.RenderBattery(100).Value.Contains("width=\"13\""),
                "full battery fills the whole body");
            Assert(Neolink.WebClient.UiIcon.RenderBattery(50).Value.Contains("width=\"6.5\""),
                "half battery fills half the body");
            Assert(!Neolink.WebClient.UiIcon.RenderBattery(0).Value.Contains("fill=\"currentColor\""),
                "empty battery has no fill bar");
            Assert(Neolink.WebClient.UiIcon.RenderBattery(150).Value.Contains("width=\"13\""),
                "overshoot clamps to full");

            // Wi-Fi readings carry their UNIT — the level is derived from that, never
            // guessed from the number's range (guessing drew full signal as empty).
            static Streaming.WifiReading Dbm(int v) => Streaming.WifiReading.FromDbm(v);
            Assert(Dbm(-45).Level == 4, "strong dBm -> 4 bars");
            Assert(Dbm(-65).Level == 3, "good dBm -> 3 bars (Reolink calls this 'good')");
            Assert(Dbm(-75).Level == 2, "fair dBm -> 2 bars");
            Assert(Dbm(-85).Level == 1, "poor dBm -> 1 bar");
            Assert(Dbm(-95).Level == 0, "very weak dBm -> 0 bars");
            Assert(Dbm(-60).Label == "-60 dBm", "dBm label");

            // The same integer means different things per unit — the bug this fixes.
            Assert(new Streaming.WifiReading(3, Streaming.WifiUnit.Bars).Level == 3, "3 bars -> 3");
            Assert(new Streaming.WifiReading(3, Streaming.WifiUnit.Percent).Level == 0, "3 percent -> 0, not 3 bars");
            Assert(new Streaming.WifiReading(5, Streaming.WifiUnit.Bars).Level == 4,
                "a 0-5 scale's full reading clamps to full, never wraps to empty");
            Assert(new Streaming.WifiReading(4, Streaming.WifiUnit.Bars).Label == "4 / 4 bars", "bars label");
            Assert(new Streaming.WifiReading(85, Streaming.WifiUnit.Percent).Label == "85%", "percent label");

            // HTTP classification: negative is RSSI, 0-4 the documented bars scale,
            // anything higher can only be a percentage.
            Assert(Streaming.WifiReading.FromHttp(-55).Unit == Streaming.WifiUnit.Dbm, "negative HTTP value -> dBm");
            Assert(Streaming.WifiReading.FromHttp(3).Unit == Streaming.WifiUnit.Bars, "0-4 HTTP value -> bars");
            Assert(Streaming.WifiReading.FromHttp(85).Unit == Streaming.WifiUnit.Percent, "large HTTP value -> percent");

            // Link type from the camera's own wording (HTTP "LAN"/"Wifi", Baichuan
            // "ethernet"/"wifi"): 1 = wired, 2 = Wi-Fi, 0 = it didn't say.
            Assert(Streaming.CameraControl.LinkKindOf("LAN") == 1, "LAN -> wired");
            Assert(Streaming.CameraControl.LinkKindOf("ethernet") == 1, "ethernet -> wired");
            Assert(Streaming.CameraControl.LinkKindOf("Wifi") == 2, "Wifi -> wireless");
            Assert(Streaming.CameraControl.LinkKindOf("WIFI") == 2, "case-insensitive");
            // "wlan" CONTAINS "lan" — the Wi-Fi test has to win, or a wireless
            // camera would be drawn with a network jack.
            Assert(Streaming.CameraControl.LinkKindOf("wlan0") == 2, "wlan -> wireless, not wired");
            Assert(Streaming.CameraControl.LinkKindOf("eth0") == 1, "eth0 -> wired");
            Assert(Streaming.CameraControl.LinkKindOf(null) == 0, "silence -> unknown");
            Assert(Streaming.CameraControl.LinkKindOf("  Wifi ") == 2, "surrounding space tolerated");
            // Prefix-matched, not substring-matched: "something" contains "eth" and
            // must NOT read as a wired camera.
            Assert(Streaming.CameraControl.LinkKindOf("something else") == 0, "unrecognized -> unknown");
            Assert(Streaming.CameraControl.LinkKindOf("unplanned") == 0, "a word containing 'lan' is not a LAN");

            // The wired glyph exists and is distinct from the Wi-Fi one.
            Assert(Neolink.WebClient.UiIcon.Render("lan").Value.Contains("<rect"), "lan icon renders");
            Assert(Neolink.WebClient.UiIcon.Render("lan").Value != Neolink.WebClient.UiIcon.RenderWifi(4).Value,
                "wired and Wi-Fi icons differ");

            // The glyph reads the level: 2 lit elements at full opacity for level 2.
            Assert(System.Text.RegularExpressions.Regex.Matches(
                Neolink.WebClient.UiIcon.RenderWifi(2).Value, "opacity=\"1\"").Count == 2,
                "level 2 lights exactly two elements");
            Assert(System.Text.RegularExpressions.Regex.Matches(
                Neolink.WebClient.UiIcon.RenderWifi(9).Value, "opacity=\"1\"").Count == 4,
                "out-of-range level clamps to full");

            // GetWifiSignal reply dialects — firmwares disagree on the shape, and
            // the Video Doorbell WiFi quotes numbers as strings (a bare cast threw,
            // which read as "camera reports no Wi-Fi" and hid the icon).
            static System.Text.Json.Nodes.JsonNode? W(string json) =>
                System.Text.Json.Nodes.JsonNode.Parse(json);
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(W("""{"signal":-55}""")) == -55, "flat signal");
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(W("""{"wifiSignal":3}""")) == 3, "flat wifiSignal");
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(W("""{"WifiSignal":{"signal":2}}""")) == 2, "nested WifiSignal.signal");
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(W("""{"WifiSignal":4}""")) == 4, "flat Pascal WifiSignal");
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(W("""{"signal":"-61"}""")) == -61, "quoted string signal");
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(W("""{"other":1}""")) == null, "unknown shape -> null");
            Assert(Protocol.ReolinkHttpApi.ParseWifiSignal(null) == null, "no value node -> null");
        });

        Test("event-type chips: only disproven types are hidden", () =>
        {
            static bool Vis(string t, IReadOnlyList<string>? ai, bool? bell) =>
                Neolink.WebClient.Components.CameraPanel.EventTypeSupported(t, ai, bell);
            var peopleOnly = new List<string> { "people" };
            // The AI sweep answered, and only for people: person shows, the other
            // AI types are disproven and hide.
            Assert(Vis("person", peopleOnly, null), "answered type shows");
            Assert(!Vis("vehicle", peopleOnly, null), "unanswered AI type hides");
            Assert(!Vis("animal", peopleOnly, null), "dog_cat maps to animal");
            Assert(!Vis("package", peopleOnly, null), "unanswered package hides");
            // No sweep result (offline, no HTTP API): nothing is disproven.
            Assert(Vis("vehicle", null, null), "unprobed camera shows everything");
            Assert(Vis("package", new List<string>(), null),
                "empty sweep result proves nothing");
            // Doorbell chip needs a doorbell; unknown shows.
            Assert(!Vis("doorbell", null, false), "non-doorbell hides the doorbell type");
            Assert(Vis("doorbell", null, true), "doorbell shows it");
            Assert(Vis("doorbell", null, null), "unprobed shows it");
            // No reliable negative signal exists for these: always show.
            Assert(Vis("crying", peopleOnly, false), "crying always shows");
            Assert(Vis("line-crossing", peopleOnly, false), "perimeter types always show");
            Assert(Vis("motion", peopleOnly, false), "motion always shows");
        });

        Test("camera state: detection-caps cache persists and survives suspend", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "neolink-camstate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Web.CameraStateStore(dir);
                Assert(store.DetectionCaps("Cam").AiTypes == null, "unprobed camera has no cached types");
                store.SetDetectionCaps("Cam", aiTypes: new List<string> { "people", "vehicle" });
                store.SetDetectionCaps("Cam", doorbell: false);
                // A partial update keeps the other signal.
                var (ai, bell) = store.DetectionCaps("Cam");
                Assert(ai!.SequenceEqual(new[] { "people", "vehicle" }) && bell == false,
                    "both signals cached independently");
                // The suspend toggle's keep-the-file-minimal pruning must not
                // discard the cached capabilities.
                store.SetSuspended("Cam", true);
                store.SetSuspended("Cam", false);
                var reloaded = new Web.CameraStateStore(dir);
                var (ai2, bell2) = reloaded.DetectionCaps("Cam");
                Assert(ai2 != null && ai2.SequenceEqual(new[] { "people", "vehicle" }) && bell2 == false,
                    "cache survives a suspend round-trip and a reload from disk");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("HTTP-API extras: preset/quick-reply/SD/auto-track/image parsing", () =>
        {
            static System.Text.Json.Nodes.JsonArray Arr(string json) =>
                (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(json)!;
            static System.Text.Json.Nodes.JsonObject Obj(string json) =>
                (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;

            // PTZ presets: free + saved slots kept (ordered by id), junk ids dropped.
            var presets = Streaming.CameraControl.ParsePtzPresets(Arr(
                """
                [{"channel":0,"enable":1,"id":1,"name":"Door"},
                 {"channel":0,"enable":0,"id":0,"name":"pos0"},
                 {"channel":0,"enable":1,"id":-1,"name":"junk"}]
                """));
            AssertEq(presets.Count, 2);
            Assert(presets[0] is { Id: 0, Enabled: false }, "slot 0 is free");
            Assert(presets[1] is { Id: 1, Name: "Door", Enabled: true }, "saved preset parsed");

            // Quick replies: negative ids and blank names dropped, names trimmed.
            var replies = Streaming.CameraControl.ParseQuickReplies(Arr(
                """
                [{"id":0,"fileName":" Leave the package "},{"id":-1,"fileName":"x"},{"id":2,"fileName":"  "}]
                """));
            AssertEq(replies.Count, 1);
            Assert(replies[0] is { Id: 0, Name: "Leave the package" }, "reply parsed + trimmed");

            // SD cards: "capacity" = total MB, "size" = remaining MB.
            var cards = Streaming.CameraControl.ParseSdCards(Arr(
                """
                [{"capacity":30298,"format":1,"id":0,"mount":1,"size":25169,"storageType":1}]
                """));
            AssertEq(cards.Count, 1);
            Assert(cards[0] is { Id: 0, TotalMb: 30298, FreeMb: 25169, Formatted: true, Mounted: true },
                "SD slot parsed");

            // Auto-track flag: either firmware key works; neither = feature absent.
            AssertEq(Streaming.CameraControl.AutoTrackValue(Obj("""{"aiTrack":1,"channel":0}""")), true);
            AssertEq(Streaming.CameraControl.AutoTrackValue(Obj("""{"bSmartTrack":0,"channel":0}""")), false);
            Assert(Streaming.CameraControl.AutoTrackValue(Obj("""{"channel":0}""")) == null,
                "no tracking key -> null");

            // Auto-reply (doorbell default message): fileId -1 = off; a reply
            // without a fileId means the feature is absent.
            AssertEq(Streaming.CameraControl.ParseAutoReply(Obj(
                """{"channel":0,"enable":1,"fileId":2,"timeout":25}""")),
                new Streaming.AutoReplyState(2, 25));
            AssertEq(Streaming.CameraControl.ParseAutoReply(Obj(
                """{"channel":0,"fileId":-1,"timeout":30}""")),
                new Streaming.AutoReplyState(-1, 30));
            Assert(Streaming.CameraControl.ParseAutoReply(Obj("""{"channel":0}""")) == null,
                "no fileId -> feature absent");

            // The auto-track ABILITY gate: only GetAbility's supportAITrack decides —
            // a config that merely carries an aiTrack field must not enable the feature.
            Assert(Streaming.CameraControl.SupportsAiTrack(Obj(
                """{"abilityChn":[{"supportAITrack":{"permit":6,"ver":1}}]}"""), 0),
                "supportAITrack ver>0 = supported");
            Assert(!Streaming.CameraControl.SupportsAiTrack(Obj(
                """{"abilityChn":[{"supportAITrack":{"permit":6,"ver":0}}]}"""), 0),
                "ver 0 = not supported");
            Assert(!Streaming.CameraControl.SupportsAiTrack(Obj(
                """{"abilityChn":[{"videoClip":{"permit":6,"ver":1}}]}"""), 0),
                "no supportAITrack entry = not supported");
            Assert(!Streaming.CameraControl.SupportsAiTrack(Obj("""{}"""), 0),
                "no ability channels = not supported");

            // Wire JSON to the camera must keep "&" literal: the default encoder's
            // & escape is valid JSON, but camera firmware parsers don't decode
            // it and silently ignore the value (observed with dayNight Black&White).
            var wire = new System.Text.Json.Nodes.JsonObject { ["dayNight"] = "Black&White" }
                .ToJsonString(Protocol.ReolinkHttpApi.WireJson);
            Assert(wire.Contains("Black&White"), "ampersand goes over the wire literally");
            Assert(!wire.Contains("u0026"), "no unicode escape in wire JSON");

            // Image settings: the Image half alone still yields sliders; the ISP half
            // adds day/night + flip/mirror when present.
            var img = Obj("""{"bright":128,"contrast":140,"saturation":128,"hue":128,"sharpen":96}""");
            var isp = Obj("""{"dayNight":"Auto","antiFlicker":"Outdoor","rotation":0,"mirroring":1}""");
            var s = Streaming.CameraControl.ParseImageSettings(img, isp);
            Assert(s is { Bright: 128, Contrast: 140, Sharpen: 96, DayNight: "Auto", Flip: false, Mirror: true },
                "both halves parsed");
            var s2 = Streaming.CameraControl.ParseImageSettings(img, null);
            Assert(s2 is { Bright: 128, DayNight: null, Flip: null, Mirror: null },
                "missing ISP half -> null extras");

            // Capability gate: when the range table was read, flip/mirror must
            // appear IN it — firmwares echo rotation/mirroring values in the
            // config on models that can't actually flip (Elite WiFi), and a
            // toggle that silently no-ops is worse than none.
            var rangeWithout = Obj("""{"hdr":{"min":0,"max":1}}""");
            var s3 = Streaming.CameraControl.ParseImageSettings(img, isp, rangeWithout);
            Assert(s3 is { Flip: null, Mirror: null },
                "range without rotation/mirroring hides the toggles");
            var rangeWith = Obj("""{"rotation":[0,1],"mirroring":{"min":0,"max":1}}""");
            var s4 = Streaming.CameraControl.ParseImageSettings(img, isp, rangeWith);
            Assert(s4 is { Flip: false, Mirror: true },
                "range offering them keeps the values");

            // The GetAbility verdict outranks the range heuristic: some firmwares
            // list rotation/mirroring in the RANGE too on models that can't do
            // either (field report: dead toggles survived the range gate), but
            // their ability table carries ispFlip/ispMirror ver 0.
            var abilityNo = Obj("""{"abilityChn":[{"ispFlip":{"permit":6,"ver":0},"ispMirror":{"permit":6,"ver":0}}]}""");
            var abilityYes = Obj("""{"abilityChn":[{"ispFlip":{"permit":6,"ver":1},"ispMirror":{"permit":6,"ver":1}}]}""");
            Assert(Streaming.CameraControl.AbilityFlag(abilityNo, 0, "ispFlip") == false, "ability ver 0 = unsupported");
            Assert(Streaming.CameraControl.AbilityFlag(abilityYes, 0, "ispMirror") == true, "ability ver 1 = supported");
            Assert(Streaming.CameraControl.AbilityFlag(abilityYes, 0, "nosuch") == false,
                "channel block answered, key absent = unsupported");
            Assert(Streaming.CameraControl.AbilityFlag(null, 0, "ispFlip") == null, "no table = no verdict");
            Assert(Streaming.CameraControl.AbilityFlag(Obj("""{}"""), 0, "ispFlip") == null,
                "table without channel blocks = no verdict");
            var s5 = Streaming.CameraControl.ParseImageSettings(img, isp, rangeWith, flipAbility: false, mirrorAbility: false);
            Assert(s5 is { Flip: null, Mirror: null },
                "ability false hides the toggles even when the range lists them");
            var s6 = Streaming.CameraControl.ParseImageSettings(img, isp, rangeWithout, flipAbility: true, mirrorAbility: true);
            Assert(s6 is { Flip: false, Mirror: true },
                "ability true keeps them even when the range omits them");

            // Indoor firmwares (E1 line) report antiFlicker "Off"; the canonical
            // value list must carry it or HA rejects every state publish
            // ("Invalid option for select...anti_flicker: 'Off'").
            Assert(Streaming.ImageSettings.AntiFlickerValues.SequenceEqual(
                new[] { "Off", "Outdoor", "50HZ", "60HZ" }), "antiFlicker values include Off");
            var ispOff = Obj("""{"dayNight":"Auto","antiFlicker":"Off","rotation":0,"mirroring":0}""");
            Assert(Streaming.CameraControl.ParseImageSettings(img, ispOff).AntiFlicker == "Off",
                "antiFlicker Off parses through untouched");
        });

        Test("ONVIF imaging: WS-Security digest, parsing, scaling, write body", () =>
        {
            // WS-Security UsernameToken digest = Base64(SHA1(nonce + created + password)).
            // The classic bug hashes the BASE64 nonce string instead of raw bytes;
            // guard against it explicitly.
            var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            var created = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
            const string createdStr = "2026-01-02T03:04:05.678Z";
            var header = Protocol.OnvifClient.BuildSecurity("admin", "secret", nonce, created);
            var raw = nonce.Concat(System.Text.Encoding.UTF8.GetBytes(createdStr))
                .Concat(System.Text.Encoding.UTF8.GetBytes("secret")).ToArray();
            var expected = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(raw));
            Assert(header.Contains(expected, StringComparison.Ordinal), "digest hashes raw nonce+created+password");
            Assert(header.Contains(createdStr, StringComparison.Ordinal), "header carries the Created timestamp");
            Assert(header.Contains(Convert.ToBase64String(nonce), StringComparison.Ordinal), "header carries the base64 nonce");
            var b64Bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(nonce));
            var wrong = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
                b64Bytes.Concat(System.Text.Encoding.UTF8.GetBytes(createdStr))
                    .Concat(System.Text.Encoding.UTF8.GetBytes("secret")).ToArray()));
            Assert(!header.Contains(wrong, StringComparison.Ordinal), "digest does NOT hash the base64 nonce string");

            static System.Xml.Linq.XElement Xml(string s) => System.Xml.Linq.XDocument.Parse(s).Root!;

            // GetImagingSettings reply → scaled to 0-255 with the camera's ranges.
            var settings = Xml("""
                <tds:GetImagingSettingsResponse xmlns:tds="http://www.onvif.org/ver20/imaging/wsdl">
                  <tt:ImagingSettings xmlns:tt="http://www.onvif.org/ver10/schema">
                    <tt:Brightness>50</tt:Brightness>
                    <tt:Contrast>60</tt:Contrast>
                    <tt:ColorSaturation>70</tt:ColorSaturation>
                    <tt:Sharpness>40</tt:Sharpness>
                    <tt:IrCutFilter>AUTO</tt:IrCutFilter>
                    <tt:WideDynamicRange><tt:Mode>ON</tt:Mode></tt:WideDynamicRange>
                  </tt:ImagingSettings>
                </tds:GetImagingSettingsResponse>
                """);
            var ranges = new Protocol.OnvifImagingRanges((0, 100), (0, 100), (0, 100), (0, 100));
            var imaging = Protocol.OnvifClient.ParseImaging(settings, ranges);
            Assert(imaging is { Brightness: 128, Contrast: 153, Saturation: 179, Sharpness: 102 },
                "imaging fields scaled 0-100 → 0-255");
            Assert(imaging is { IrCutFilter: "AUTO", WideDynamicRange: true }, "IR-cut + WDR parsed");

            // GetOptions ranges.
            var options = Xml("""
                <tds:GetOptionsResponse xmlns:tds="http://www.onvif.org/ver20/imaging/wsdl">
                  <tt:ImagingOptions xmlns:tt="http://www.onvif.org/ver10/schema">
                    <tt:Brightness><tt:Min>0</tt:Min><tt:Max>255</tt:Max></tt:Brightness>
                    <tt:Contrast><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Contrast>
                  </tt:ImagingOptions>
                </tds:GetOptionsResponse>
                """);
            var parsedRanges = Protocol.OnvifClient.ParseRanges(options);
            Assert(parsedRanges.Brightness == (0.0, 255.0) && parsedRanges.Contrast == (0.0, 100.0),
                "GetOptions min/max parsed");
            Assert(parsedRanges.Saturation == null, "an absent range is null");

            // Service discovery + video-source token parsing.
            var caps = Xml("""
                <s:GetCapabilitiesResponse xmlns:s="http://www.onvif.org/ver10/device/wsdl">
                  <tt:Capabilities xmlns:tt="http://www.onvif.org/ver10/schema">
                    <tt:Media><tt:XAddr>http://cam/onvif/media</tt:XAddr></tt:Media>
                    <tt:Imaging><tt:XAddr>http://cam/onvif/imaging</tt:XAddr></tt:Imaging>
                  </tt:Capabilities>
                </s:GetCapabilitiesResponse>
                """);
            Assert(Protocol.OnvifClient.ServiceXAddr(caps, "Media") == "http://cam/onvif/media", "Media XAddr");
            Assert(Protocol.OnvifClient.ServiceXAddr(caps, "Imaging") == "http://cam/onvif/imaging", "Imaging XAddr");
            var vs = Xml("""
                <s:GetVideoSourcesResponse xmlns:s="http://www.onvif.org/ver10/media/wsdl">
                  <tt:VideoSources xmlns:tt="http://www.onvif.org/ver10/schema" token="VS_0"/>
                </s:GetVideoSourcesResponse>
                """);
            Assert(Protocol.OnvifClient.VideoSourceToken(vs) == "VS_0", "video source token from attribute");

            // Scale round-trip and write-body field order (schema sequence: Brightness,
            // ColorSaturation, Contrast, IrCutFilter, Sharpness, WideDynamicRange).
            Assert(Protocol.OnvifClient.ScaleToByte(50, (0, 100)) == 128, "scale to byte");
            Assert(Math.Abs(Protocol.OnvifClient.ScaleFromByte(128, (0, 100)) - 50.2) < 0.5, "scale from byte");
            Assert(Protocol.OnvifClient.ScaleToByte(200, null) == 200, "no range = passthrough clamp");
            var body = Protocol.OnvifClient.BuildSetImaging("VS_0", ranges,
                brightness: 128, contrast: null, saturation: 255, sharpness: null,
                irCutFilter: "ON", wideDynamicRange: null);
            Assert(body.Contains("<tt:Brightness>") && body.Contains("<tt:ColorSaturation>")
                   && body.Contains("<tt:IrCutFilter>ON</tt:IrCutFilter>"), "only-set fields emitted");
            Assert(!body.Contains("<tt:Contrast>") && !body.Contains("<tt:Sharpness>")
                   && !body.Contains("WideDynamicRange"), "unset fields omitted");
            Assert(body.IndexOf("Brightness", StringComparison.Ordinal) < body.IndexOf("ColorSaturation", StringComparison.Ordinal)
                   && body.IndexOf("ColorSaturation", StringComparison.Ordinal) < body.IndexOf("IrCutFilter", StringComparison.Ordinal),
                   "fields in ONVIF schema order");

            // Day/night vocabulary round-trip.
            Assert(Streaming.CameraControl.IrCutToDayNight("ON") == "Color"
                   && Streaming.CameraControl.IrCutToDayNight("OFF") == "Black&White"
                   && Streaming.CameraControl.IrCutToDayNight("AUTO") == "Auto", "IR-cut → day/night");
            Assert(Streaming.CameraControl.DayNightToIrCut("Color") == "ON"
                   && Streaming.CameraControl.DayNightToIrCut("Black&White") == "OFF"
                   && Streaming.CameraControl.DayNightToIrCut("Auto") == "AUTO", "day/night → IR-cut");

            // Endpoint candidates: a bare host tries Reolink's ONVIF port 8000 first,
            // then 80; an explicit port or full URL is taken verbatim.
            var bare = Protocol.OnvifClient.BuildCandidates("10.1.1.29");
            Assert(bare.Length == 2 && bare[0] == "http://10.1.1.29:8000/onvif/device_service"
                   && bare[1] == "http://10.1.1.29/onvif/device_service", "bare host → :8000 then :80");
            var withPort = Protocol.OnvifClient.BuildCandidates("10.1.1.29:8899");
            Assert(withPort.Length == 1 && withPort[0] == "http://10.1.1.29:8899/onvif/device_service",
                "explicit port honoured");
            var full = Protocol.OnvifClient.BuildCandidates("http://cam/onvif/device_service");
            Assert(full.Length == 1 && full[0] == "http://cam/onvif/device_service", "full URL verbatim");

            // XAddr host normalization: a camera reporting an internal host is reached
            // at the address we actually used, keeping its port and path.
            Assert(Protocol.OnvifClient.NormalizeXAddr("http://127.0.0.1:8000/onvif/media", "http://10.1.1.29:8000/onvif/device_service")
                   == "http://10.1.1.29:8000/onvif/media", "internal XAddr host rewritten");
            Assert(Protocol.OnvifClient.NormalizeXAddr("http://10.1.1.29:8000/onvif/media", "http://10.1.1.29:8000/onvif/device_service")
                   == "http://10.1.1.29:8000/onvif/media", "matching host untouched");
            Assert(Protocol.OnvifClient.NormalizeXAddr(null, "http://10.1.1.29/onvif/device_service") == null, "null passes through");
        });

        Test("HTTP extras: detection sensitivity, HDR, OSD, firmware, SD search parsing", () =>
        {
            static System.Text.Json.Nodes.JsonObject Obj(string json) =>
                (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;
            static System.Text.Json.Nodes.JsonNode Val(string json) =>
                System.Text.Json.Nodes.JsonNode.Parse(json)!;

            // Motion sensitivity, new dialect (MdAlarm + useNewSens): sensDef is the
            // user-facing value, 0-50, higher = more sensitive.
            var mdNew = Obj("""
                {"channel":0,"useNewSens":1,"newSens":{"sensDef":34,"sens":[
                    {"beginHour":0,"endHour":23,"sensitivity":34}]}}
                """);
            AssertEq(Streaming.CameraControl.MdSensitivityValue(mdNew, isMdAlarm: true), 34);
            // Old dialect (GetAlarm sens tables): the WIRE value is inverted —
            // 1 = most sensitive — so user 30 is wire 21 and back.
            var mdOld = Obj("""{"channel":0,"type":"md","sens":[{"id":0,"sensitivity":21}]}""");
            AssertEq(Streaming.CameraControl.MdSensitivityValue(mdOld, isMdAlarm: false), 30);
            Assert(Streaming.CameraControl.MdSensitivityValue(Obj("""{"channel":0}"""), false) == null,
                "no sens table -> feature absent");

            // Writes flow back in the same dialect, all time slots following along.
            Assert(Streaming.CameraControl.ApplyMdSensitivity(mdNew, true, 40), "new-dialect write accepted");
            AssertEq((int?)mdNew["newSens"]?["sensDef"], 40);
            AssertEq((int?)((System.Text.Json.Nodes.JsonArray)mdNew["newSens"]!["sens"]!)[0]!["sensitivity"], 40);
            Assert(Streaming.CameraControl.ApplyMdSensitivity(mdOld, false, 40), "old-dialect write accepted");
            AssertEq((int?)((System.Text.Json.Nodes.JsonArray)mdOld["sens"]!)[0]!["sensitivity"], 11); // 51 - 40
            Assert(!Streaming.CameraControl.ApplyMdSensitivity(Obj("""{"channel":0}"""), false, 25),
                "no table -> write refused");

            // AI sensitivity: plain field; missing sensitivity = type unsupported.
            var ai = Streaming.CameraControl.ParseAiSensitivity("people", Obj(
                """{"channel":0,"ai_type":"people","sensitivity":85,"stay_time":2}"""));
            Assert(ai is { Type: "people", Sensitivity: 85, StayTime: 2 }, "AI alarm parsed");
            Assert(Streaming.CameraControl.ParseAiSensitivity("vehicle", Obj("""{"channel":0}""")) == null,
                "no sensitivity -> null");

            // HDR range: {"min","max"} object on most firmwares, option array on some.
            AssertEq(Streaming.CameraControl.HdrRangeMax(Obj("""{"hdr":{"min":0,"max":2}}""")), 2);
            AssertEq(Streaming.CameraControl.HdrRangeMax(Obj("""{"hdr":[0,1]}""")), 1);
            Assert(Streaming.CameraControl.HdrRangeMax(Obj("""{"bright":{"min":0,"max":255}}""")) == null,
                "no hdr in range -> null");
            var ispHdr = Obj("""{"dayNight":"Auto","hdr":2}""");
            var imgHdr = Obj("""{"bright":128}""");
            var withHdr = Streaming.CameraControl.ParseImageSettings(imgHdr, ispHdr,
                Obj("""{"hdr":{"min":0,"max":2}}"""));
            Assert(withHdr is { Hdr: 2, HdrMax: 2 }, "hdr value + range parsed");
            Assert(Streaming.CameraControl.ParseImageSettings(imgHdr, Obj("""{"dayNight":"Auto"}""")).Hdr == null,
                "no hdr field -> null");

            // OSD: both overlays + watermark, position options from the range table.
            var osd = Streaming.CameraControl.ParseOsd(Obj("""
                {"channel":0,"osdChannel":{"enable":1,"name":"Porch","pos":"Lower Right"},
                 "osdTime":{"enable":0,"pos":"Top Center"},"watermark":1}
                """), Obj("""
                {"osdChannel":{"pos":["Upper Left","Top Center","Lower Right"]}}
                """));
            Assert(osd is { ShowName: true, Name: "Porch", NamePos: "Lower Right", ShowTime: false, Watermark: true },
                "OSD parsed");
            Assert(osd.PosOptions.SequenceEqual(new[] { "Upper Left", "Top Center", "Lower Right" }),
                "position options from range");
            Assert(Streaming.CameraControl.ParseOsd(Obj("""{"channel":0,"osdTime":{"enable":1}}"""), null)
                is { ShowName: false, Watermark: null }, "partial OSD tolerated");

            // CheckFirmware: 0/1, version string, or an info object — all firmware-real.
            Assert(Streaming.CameraControl.ParseFirmware(null) == null, "no field -> unknown");
            AssertEq(Streaming.CameraControl.ParseFirmware(Val("0")), new Streaming.FirmwareStatus(false, null));
            AssertEq(Streaming.CameraControl.ParseFirmware(Val("1")), new Streaming.FirmwareStatus(true, null));
            AssertEq(Streaming.CameraControl.ParseFirmware(Val("\"v3.0.0.4348\"")),
                new Streaming.FirmwareStatus(true, "v3.0.0.4348"));
            AssertEq(Streaming.CameraControl.ParseFirmware(Obj("""{"firmVer":"v3.1.0.5000"}""")),
                new Streaming.FirmwareStatus(true, "v3.1.0.5000"));

            // SD search calendar: one digit per day, '1' = recordings that day.
            var cal = Streaming.CameraControl.ParseSdCalendar(Obj("""
                {"channel":0,"Status":[{"year":2026,"mon":7,"table":"0110000000000000010000000000000"}]}
                """), 2026, 7);
            Assert(cal.SequenceEqual(new[] { 2, 3, 18 }), "calendar days decoded");
            Assert(Streaming.CameraControl.ParseSdCalendar(Obj("""{"channel":0}"""), 2026, 7).Count == 0,
                "no Status -> empty");

            // SD file list: named entries only, camera-local times, sorted by start.
            var recs = Streaming.CameraControl.ParseSdRecordings(Obj("""
                {"channel":0,"File":[
                  {"StartTime":{"year":2026,"mon":7,"day":18,"hour":14,"min":30,"sec":0},
                   "EndTime":{"year":2026,"mon":7,"day":18,"hour":14,"min":31,"sec":30},
                   "frameRate":30,"height":1440,"width":2560,
                   "name":"Rec_20260718_143000.mp4","size":52428800,"type":"main"},
                  {"StartTime":{"year":2026,"mon":7,"day":18,"hour":9,"min":0,"sec":5},
                   "EndTime":{"year":2026,"mon":7,"day":18,"hour":9,"min":1,"sec":5},
                   "name":"Rec_20260718_090005.mp4","size":1048576,"type":"main"},
                  {"StartTime":{"year":2026,"mon":7,"day":18,"hour":1,"min":0,"sec":0},
                   "EndTime":{"year":2026,"mon":7,"day":18,"hour":1,"min":1,"sec":0},
                   "name":"","size":5}]}
                """), "main");
            AssertEq(recs.Count, 2); // the nameless entry is undownloadable -> dropped
            Assert(recs[0].Start < recs[1].Start, "sorted by start time");
            Assert(recs[1] is { Name: "Rec_20260718_143000.mp4", SizeBytes: 52428800, StreamType: "main" },
                "file entry parsed");
            AssertEq(recs[1].End - recs[1].Start, TimeSpan.FromSeconds(90));

            // The Video Doorbell WiFi quotes "size" as a STRING — a bare (long) cast
            // threw and failed the WHOLE day's parse ("N entries but none usable").
            var quoted = Streaming.CameraControl.ParseSdRecordings(Obj("""
                {"channel":0,"File":[
                  {"EndTime":{"day":18,"hour":7,"min":46,"mon":7,"sec":30,"year":2026},
                   "StartTime":{"day":18,"hour":7,"min":45,"mon":7,"sec":52,"year":2026},
                   "frameRate":0,"height":0,"width":0,"type":"main",
                   "name":"/mnt/sda/Mp4Record/2026-07-18/RecM0A_20260718_074552_074630.mp4",
                   "size":"16058135"}]}
                """), "main");
            AssertEq(quoted.Count, 1);
            Assert(quoted[0] is { SizeBytes: 16058135L }, "quoted string size parses");
            // The tolerant reader itself.
            Assert(Streaming.CameraControl.AsLong(System.Text.Json.Nodes.JsonValue.Create("42")) == 42, "string number");
            Assert(Streaming.CameraControl.AsLong(System.Text.Json.Nodes.JsonValue.Create(42L)) == 42, "number");
            Assert(Streaming.CameraControl.AsLong(System.Text.Json.Nodes.JsonValue.Create("x")) == 0, "non-numeric string → 0");
            Assert(Streaming.CameraControl.AsLong(null) == 0, "null → 0");
        });

        Test("config editor: camera add/edit/delete round-trip + masking", () =>
        {
            // RTSP password masking: only the userinfo password is hidden, and
            // URLs without one pass through untouched.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://u:secret@10.0.0.5:554/live"),
                "rtsp://u:****@10.0.0.5:554/live");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://10.0.0.5/live"), "rtsp://10.0.0.5/live");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://user@10.0.0.5/live"), "rtsp://user@10.0.0.5/live");
            Assert(Config.ConfigEditor.MaskRtspPassword(null) == null, "null passes through");

            // host[:port] validation.
            Assert(Config.ConfigEditor.HostPortError("192.168.1.50") == null, "bare IP ok");
            Assert(Config.ConfigEditor.HostPortError("cam.local:9000") == null, "host:port ok");
            Assert(Config.ConfigEditor.HostPortError("") != null, "empty rejected");
            Assert(Config.ConfigEditor.HostPortError("http://x") != null, "URL rejected");
            Assert(Config.ConfigEditor.HostPortError("host:99999") != null, "bad port rejected");
            Assert(Config.ConfigEditor.HostPortError("a b") != null, "spaces rejected");

            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            try
            {
                File.WriteAllText(cfgPath, """
                    { "cameras": [ { "name": "Old", "address": "10.0.0.9", "username": "admin", "password": "hunter2" } ] }
                    """);

                // Add a camera; the stored password of "Old" must survive untouched.
                Config.ConfigEditor.Apply(cfgPath, root =>
                {
                    var cams = Config.ConfigEditor.Cameras(root);
                    var cam = new System.Text.Json.Nodes.JsonObject();
                    cams.Add(cam);
                    Config.ConfigEditor.Set(cam, "name", "New");
                    Config.ConfigEditor.Set(cam, "address", "10.0.0.10:9000");
                    Config.ConfigEditor.Set(cam, "username", "admin");
                });
                var cfg = Config.NeolinkConfig.Load(cfgPath);
                AssertEq(cfg.Cameras.Count, 2);
                AssertEq(cfg.Cameras[0].Password, "hunter2");
                AssertEq(cfg.Cameras[1].Name, "New");
                AssertEq(cfg.Cameras[1].Port, 9000);

                // Edit "Old" without sending a password (write-only semantics: the
                // key is simply not touched) — it must still be there afterwards.
                Config.ConfigEditor.Apply(cfgPath, root =>
                {
                    var cams = Config.ConfigEditor.Cameras(root);
                    var cam = Config.ConfigEditor.FindCamera(cams, "old")!; // case-insensitive
                    Config.ConfigEditor.Set(cam, "address", "10.0.0.99");
                });
                cfg = Config.NeolinkConfig.Load(cfgPath);
                AssertEq(cfg.Cameras[0].Host, "10.0.0.99");
                AssertEq(cfg.Cameras[0].Password, "hunter2");

                // A duplicate name must fail validation and leave the file intact.
                bool rejected = false;
                try
                {
                    Config.ConfigEditor.Apply(cfgPath, root =>
                    {
                        var cams = Config.ConfigEditor.Cameras(root);
                        var cam = new System.Text.Json.Nodes.JsonObject();
                        cams.Add(cam);
                        Config.ConfigEditor.Set(cam, "name", "new"); // dupe, other case
                        Config.ConfigEditor.Set(cam, "address", "10.0.0.11");
                        Config.ConfigEditor.Set(cam, "username", "admin");
                    });
                }
                catch (FormatException) { rejected = true; }
                Assert(rejected, "duplicate camera name rejected by validation");
                AssertEq(Config.NeolinkConfig.Load(cfgPath).Cameras.Count, 2);

                // Delete round-trip.
                Config.ConfigEditor.Apply(cfgPath, root =>
                {
                    var cams = Config.ConfigEditor.Cameras(root);
                    cams.Remove(Config.ConfigEditor.FindCamera(cams, "New")!);
                });
                cfg = Config.NeolinkConfig.Load(cfgPath);
                AssertEq(cfg.Cameras.Count, 1);
                AssertEq(cfg.Cameras[0].Name, "Old");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("HA suspend switch: resumable while the camera is offline", () =>
        {
            // The whole point of suspend is that the camera reads OFFLINE — so the
            // HA switch must (1) act without ever touching the camera connection
            // and (2) stay Available in HA, i.e. its availability must be gated on
            // the BRIDGE topic only, never the per-camera topic.
            bool? lastSet = null;
            var cam = new Web.WebCameraInfo("suspendcam",
                new List<Web.WebStreamInfo>(), new StubCameraControl("suspendcam"), null,
                Suspended: () => lastSet ?? true,
                SetSuspended: v => lastSet = v);
            var hub = new Mqtt.HomeAssistantMqtt(
                new Config.MqttConfig { Broker = "127.0.0.1", Port = 1 }, // never connected
                new List<Web.WebCameraInfo> { cam }, "test");
            var bridge = new Mqtt.CameraBridge(cam, hub);

            // Resume command (payload OFF) flips the flag — camera offline throughout,
            // and the state publish failing (no broker) must not break the command.
            bridge.HandleCommandAsync("suspend", "OFF").GetAwaiter().GetResult();
            AssertEq(lastSet, false);
            bridge.HandleCommandAsync("suspend", "ON").GetAwaiter().GetResult();
            AssertEq(lastSet, true);

            // Discovery config: availability must be the bridge topic ONLY.
            var json = System.Text.Json.JsonSerializer.Serialize(
                bridge.SuspendSwitchConfig(), Mqtt.HomeAssistantMqtt.DiscoveryJson);
            Assert(json.Contains("\"availability\":[{\"topic\":\"neolink/bridge/state\"}]"),
                $"suspend switch availability must be bridge-only, got: {json}");
            Assert(!json.Contains("suspendcam/state"),
                "per-camera availability topic must NOT gate the suspend switch");

            // The Asleep sensor must be readable while the camera naps: bridge-only
            // availability too, or it could never say "asleep".
            var asleepJson = System.Text.Json.JsonSerializer.Serialize(
                bridge.AsleepSensorConfig(), Mqtt.HomeAssistantMqtt.DiscoveryJson);
            Assert(asleepJson.Contains("\"availability\":[{\"topic\":\"neolink/bridge/state\"}]"),
                $"asleep sensor availability must be bridge-only, got: {asleepJson}");
        });

        Test("UID-only cameras: allowed with udp, rejected without", () =>
        {
            // A UID can replace the address — but only over UDP, where broadcast
            // discovery finds the camera by UID. TCP has no such lookup.
            string Cfg(string body) => $$"""
                { "cameras": [ { "name": "argus", "username": "admin", "password": "p", {{body}} } ] }
                """;
            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            try
            {
                // UID + udp, no address: loads, empty host, uid kept.
                File.WriteAllText(tmp, Cfg("\"uid\": \"95270000ABCDEFGH\", \"udp\": true"));
                var cfg = NeolinkConfig.Load(tmp);
                AssertEq(cfg.Cameras[0].Host, "");
                Assert(cfg.Cameras[0].Udp && cfg.Cameras[0].Uid == "95270000ABCDEFGH", "uid-only udp camera loads");

                // UID without udp and without address: must be refused with guidance.
                File.WriteAllText(tmp, Cfg("\"uid\": \"95270000ABCDEFGH\""));
                bool rejected = false;
                try { NeolinkConfig.Load(tmp); }
                catch (FormatException ex) { rejected = ex.Message.Contains("udp"); }
                Assert(rejected, "uid without udp and address is refused, pointing at udp");

                // Neither address nor uid: still an error.
                File.WriteAllText(tmp, Cfg("\"channel_id\": 0"));
                rejected = false;
                try { NeolinkConfig.Load(tmp); } catch (FormatException) { rejected = true; }
                Assert(rejected, "no address and no uid is refused");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        });

        Test("camera 'stream' options: the admin editor validates the loader's list", () =>
        {
            // The add-camera screen offers these as a dropdown and the web API
            // validates against the same array. If the two ever drift, the editor
            // happily saves a config the loader then rejects on restart — so every
            // advertised value must actually load, and anything else must not.
            string Cfg(string stream) => $$"""
                {
                  "cameras": [ { "name": "c", "username": "u", "password": "p",
                                 "address": "10.0.0.5", "stream": "{{stream}}" } ]
                }
                """;
            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            try
            {
                Assert(NeolinkConfig.ValidCameraStreams.Length > 0, "the advertised stream list is not empty");
                foreach (var stream in NeolinkConfig.ValidCameraStreams)
                {
                    File.WriteAllText(tmp, Cfg(stream));
                    var cfg = NeolinkConfig.Load(tmp);
                    AssertEq(cfg.Cameras[0].Stream, stream);
                }
                // A value the dropdown never offers must still be refused by the loader.
                File.WriteAllText(tmp, Cfg("bogusStream"));
                bool rejected = false;
                try { NeolinkConfig.Load(tmp); } catch { rejected = true; }
                Assert(rejected, "an unlisted stream value must be rejected by the loader");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        });

        Test("HA status LED switch: ON/OFF writes the lightState toggle", () =>
        {
            // The feature this exists for: turning the little status LED off (and
            // back on) from Home Assistant — e.g. alongside Privacy Mode. It rides
            // LedState's lightState, the same field the floodlight/spotlight use,
            // which is why only one of the three is ever announced per camera.
            var led = new LedRecordingControl("ledcam");
            var cam = new Web.WebCameraInfo("ledcam", new List<Web.WebStreamInfo>(), led, null);
            var hub = new Mqtt.HomeAssistantMqtt(
                new Config.MqttConfig { Broker = "127.0.0.1", Port = 1 }, // never connected
                new List<Web.WebCameraInfo> { cam }, "test");
            var bridge = new Mqtt.CameraBridge(cam, hub);

            bridge.HandleCommandAsync("status_led", "OFF").GetAwaiter().GetResult();
            AssertEq(led.LastLightState, "close");
            bridge.HandleCommandAsync("status_led", "ON").GetAwaiter().GetResult();
            AssertEq(led.LastLightState, "open");
            // It must drive ONLY lightState — never the IR "state" field, which is
            // night vision and would go dark with it.
            Assert(led.LastState == null && led.LastDoorbell == null && led.LastIrBrightness == null,
                "status LED write must touch lightState alone");
        });

        Test("HA detection-events switch: shares the web UI setting + persists", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // A real recorder over a real settings store: the HA switch and the
                // web UI toggle must be the SAME setting, not two flags that drift.
                var hub = new Streaming.StreamHub("detectcam");
                var settings = new Recording.RecordingSettings(dir);
                var recorder = new Recording.EventRecorder("detectcam", hub,
                    new StubCameraControl("detectcam"),
                    new Recording.EventStore(Path.Combine(dir, "rec")),
                    new Config.RecordingConfig(), settings);
                var cam = new Web.WebCameraInfo("detectcam",
                    new List<Web.WebStreamInfo>(), new StubCameraControl("detectcam"), null)
                    { EventRecorder = recorder };
                var mqttHub = new Mqtt.HomeAssistantMqtt(
                    new Config.MqttConfig { Broker = "127.0.0.1", Port = 1 }, // never connected
                    new List<Web.WebCameraInfo> { cam }, "test");
                var bridge = new Mqtt.CameraBridge(cam, mqttHub);

                Assert(recorder.EventsEnabled, "detection events default to on");
                bridge.HandleCommandAsync("detect", "OFF").GetAwaiter().GetResult();
                Assert(!recorder.EventsEnabled, "HA OFF flips the shared setting");
                Assert(!recorder.OnDemandAvailable, "master switch off gates on-demand too");
                Assert(!new Recording.RecordingSettings(dir).Get("detectcam").Events,
                    "the flip persists to settings.json (what the web UI reads)");
                bridge.HandleCommandAsync("detect", "ON").GetAwaiter().GetResult();
                Assert(recorder.EventsEnabled, "HA ON restores event capture");

                // The setting lives on the server, so the switch must stay usable
                // while the camera itself is offline: bridge-only availability.
                var json = System.Text.Json.JsonSerializer.Serialize(
                    bridge.DetectSwitchConfig(), Mqtt.HomeAssistantMqtt.DiscoveryJson);
                Assert(json.Contains("\"availability\":[{\"topic\":\"neolink/bridge/state\"}]"),
                    $"detect switch availability must be bridge-only, got: {json}");
                Assert(!json.Contains("detectcam/state"),
                    "per-camera availability topic must NOT gate the detect switch");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("HA continuous-recording switch: shares the web UI setting + persists", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // Wire the switch exactly as Program.cs does — to the shared settings
                // store — so the HA switch and the web UI's 24/7 toggle are one setting.
                var settings = new Recording.RecordingSettings(dir);
                var cam = new Web.WebCameraInfo("reccam",
                    new List<Web.WebStreamInfo>(), new StubCameraControl("reccam"), null,
                    ContinuousEnabled: () => settings.Get("reccam").Continuous,
                    SetContinuousEnabled: v => settings.Update("reccam", events: null, continuous: v,
                        eventTypes: null, setEventTypes: false));
                var hub = new Mqtt.HomeAssistantMqtt(
                    new Config.MqttConfig { Broker = "127.0.0.1", Port = 1 }, // never connected
                    new List<Web.WebCameraInfo> { cam }, "test");
                var bridge = new Mqtt.CameraBridge(cam, hub);

                Assert(!settings.Get("reccam").Continuous, "24/7 recording defaults off");
                bridge.HandleCommandAsync("continuous", "ON").GetAwaiter().GetResult();
                Assert(settings.Get("reccam").Continuous, "HA ON turns 24/7 recording on");
                Assert(new Recording.RecordingSettings(dir).Get("reccam").Continuous,
                    "the flip persists to settings.json (what the web UI reads)");
                bridge.HandleCommandAsync("continuous", "OFF").GetAwaiter().GetResult();
                Assert(!settings.Get("reccam").Continuous, "HA OFF turns 24/7 recording off");

                // Server-side setting → bridge-only availability, so it stays usable
                // while the camera is offline or asleep.
                var json = System.Text.Json.JsonSerializer.Serialize(
                    bridge.ContinuousSwitchConfig(), Mqtt.HomeAssistantMqtt.DiscoveryJson);
                Assert(json.Contains("\"availability\":[{\"topic\":\"neolink/bridge/state\"}]"),
                    $"continuous switch availability must be bridge-only, got: {json}");
                Assert(!json.Contains("reccam/state"),
                    "per-camera availability topic must NOT gate the continuous switch");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("HA re-publish nudge: a web-UI change reflects at once (no 20s wait)", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // Bridge wired like Program.cs: the switches read the shared settings
                // store, and RepublishCameraAsync is what the web endpoints call after
                // persisting a change — it must publish the CURRENT state immediately.
                var settings = new Recording.RecordingSettings(dir);
                var recorder = new Recording.EventRecorder("nudgecam", new Streaming.StreamHub("nudgecam"),
                    new StubCameraControl("nudgecam"),
                    new Recording.EventStore(Path.Combine(dir, "rec")),
                    new Config.RecordingConfig(), settings);
                var cam = new Web.WebCameraInfo("nudgecam",
                    new List<Web.WebStreamInfo>(), new StubCameraControl("nudgecam"), null,
                    ContinuousEnabled: () => settings.Get("nudgecam").Continuous,
                    SetContinuousEnabled: v => settings.Update("nudgecam", events: null, continuous: v,
                        eventTypes: null, setEventTypes: false))
                    { EventRecorder = recorder };
                var hub = new Mqtt.HomeAssistantMqtt(
                    new Config.MqttConfig { Broker = "127.0.0.1", Port = 1 }, // never connected
                    new List<Web.WebCameraInfo> { cam }, "test");

                // Capture every publish without a broker.
                var published = new Dictionary<string, string>();
                hub.PublishObserver = (topic, payload) => published[topic] = payload;

                // Simulate the web UI turning 24/7 recording ON (persist), then the
                // endpoint nudging the bridge — HA must see ON right away.
                settings.Update("nudgecam", events: null, continuous: true,
                    eventTypes: null, setEventTypes: false);
                hub.RepublishCameraAsync("nudgecam").GetAwaiter().GetResult();

                Assert(published.TryGetValue("neolink/nudgecam/continuous", out var cont) && cont == "ON",
                    $"nudge must publish continuous=ON immediately, got: {(cont ?? "<none>")}");
                Assert(published.ContainsKey("neolink/nudgecam/detect"),
                    "nudge must also refresh the detection-events switch state");

                // Flip back off and nudge again — the new state must land, not the old.
                settings.Update("nudgecam", events: null, continuous: false,
                    eventTypes: null, setEventTypes: false);
                hub.RepublishCameraAsync("nudgecam").GetAwaiter().GetResult();
                Assert(published["neolink/nudgecam/continuous"] == "OFF",
                    "a second nudge must publish the NEW state (OFF), not the stale one");

                // Unknown camera is a safe no-op (must not throw).
                hub.RepublishCameraAsync("does-not-exist").GetAwaiter().GetResult();
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("camera state store: suspend flag round-trip + restart persistence", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Web.CameraStateStore(dir);
                Assert(!store.Suspended("Driveway"), "not suspended by default");

                store.SetSuspended("Driveway", true);
                Assert(store.Suspended("driveway"), "case-insensitive lookup");
                Assert(new Web.CameraStateStore(dir).Suspended("Driveway"),
                    "suspend survives a restart (persisted)");

                store.SetSuspended("Driveway", false);
                Assert(!new Web.CameraStateStore(dir).Suspended("Driveway"),
                    "resume persists (safe default is un-suspended)");

                // A corrupt file must not take the server down — cameras start
                // un-suspended (they stream and record).
                File.WriteAllText(Path.Combine(dir, "camera-state.json"), "{ not json");
                Assert(!new Web.CameraStateStore(dir).Suspended("Driveway"),
                    "corrupt file -> un-suspended, no throw");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("secret key: source + fingerprint reporting", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // File-based: first construction generates secret.key; the report
                // says so, and the fingerprint is stable across restarts.
                var sp = new Notifications.SecretProtector(dir);
                AssertEq(sp.KeySource, "file");
                Assert(sp.KeyFile != null && sp.KeyFile.EndsWith(Notifications.SecretProtector.KeyFileName),
                    "file-based key reports its path");
                Assert(sp.Fingerprint.Length == 12 && sp.Fingerprint.All(Uri.IsHexDigit),
                    "fingerprint is 12 hex chars");
                AssertEq(new Notifications.SecretProtector(dir).Fingerprint, sp.Fingerprint);

                // Env-based: the variable wins over the file, source says "env",
                // and the fingerprint is the SHA-256 prefix of THAT key.
                var envKey = new byte[32];
                for (int i = 0; i < 32; i++) envKey[i] = (byte)(i + 1);
                Environment.SetEnvironmentVariable(Notifications.SecretProtector.KeyEnvVar,
                    Convert.ToBase64String(envKey));
                try
                {
                    var spe = new Notifications.SecretProtector(dir);
                    AssertEq(spe.KeySource, "env");
                    Assert(spe.KeyFile == null, "env key has no file path");
                    AssertEq(spe.Fingerprint,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(envKey))[..12].ToLowerInvariant());
                }
                finally
                {
                    Environment.SetEnvironmentVariable(Notifications.SecretProtector.KeyEnvVar, null);
                }

                // Same-volume detection: a key file inside the recordings root is,
                // by definition, on the same filesystem as the Recordings tier.
                var storage = new Recording.StorageLocations(new Config.RecordingConfig { Path = dir });
                Assert(storage.SharesVolumeWith(Path.Combine(dir, "secret.key"), out var tier)
                    && tier.Length > 0, "key file on the footage volume is detected");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("secret protector: AES-256-GCM round-trip + tamper detection", () =>
        {
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 7 + 1);
            var p = new Notifications.SecretProtector(key);

            var token = p.Protect("hunter2!@#");
            AssertEq(p.Unprotect(token), "hunter2!@#");
            // Ciphertext is not the plaintext, and two encryptions differ (random nonce).
            Assert(!token.Contains("hunter2"), "token does not leak the plaintext");
            Assert(p.Protect("hunter2!@#") != token, "each encryption uses a fresh nonce");
            // Empty round-trips; blank/garbage tokens degrade to null (never throw).
            AssertEq(p.Unprotect(p.Protect("")), "");
            Assert(p.Unprotect(null) == null && p.Unprotect("") == null, "null/empty token -> null");
            Assert(p.Unprotect("not-base64!!") == null, "garbage token -> null");

            // Tampering (flip a ciphertext byte) is rejected by the GCM tag.
            var raw = Convert.FromBase64String(token);
            raw[^1] ^= 0xFF;
            Assert(p.Unprotect(Convert.ToBase64String(raw)) == null, "tampered token -> null");

            // A different key cannot read it.
            var other = new byte[32];
            Array.Fill(other, (byte)9);
            Assert(new Notifications.SecretProtector(other).Unprotect(token) == null, "wrong key -> null");
        });

        Test("notification store: password encrypted + write-only", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var key = new byte[32];
                Array.Fill(key, (byte)5);
                var prot = new Notifications.SecretProtector(key);
                var store = new Notifications.NotificationStore(dir, prot);

                var s = new Notifications.NotificationSettings
                {
                    Enabled = true, Recipient = "a@b.com", SmtpHost = "smtp.x", Username = "u",
                };
                store.Save(s, "topsecret");                    // set the password
                AssertEq(store.SmtpPassword(), "topsecret");
                Assert(store.HasPassword, "password recorded");

                // On disk it is ciphertext, never the plaintext.
                var raw = File.ReadAllText(Path.Combine(dir, "notifications.json"));
                Assert(!raw.Contains("topsecret"), "password not stored in plaintext");
                Assert(raw.Contains("passwordEnc"), "password stored as an encrypted token");

                // null keeps it, "" clears it (write-only semantics).
                store.Save(s.Clone(), null);
                AssertEq(store.SmtpPassword(), "topsecret");
                store.Save(s.Clone(), "");
                AssertEq(store.SmtpPassword(), "");
                Assert(!store.HasPassword, "cleared password");

                // Reload from disk decrypts correctly with the same key.
                store.Save(s.Clone(), "again");
                var reopened = new Notifications.NotificationStore(dir, prot);
                AssertEq(reopened.SmtpPassword(), "again");
                Assert(reopened.Snapshot().Enabled, "settings persisted");

                // Per-camera offline threshold: override wins, else the default.
                var cfg = new Notifications.NotificationSettings { OfflineThresholdMinutes = 10 };
                cfg.CameraOfflineOverrides["Driveway"] = 3;
                AssertEq(cfg.OfflineMinutesFor("Driveway"), 3);
                AssertEq(cfg.OfflineMinutesFor("Backyard"), 10);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
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

        Test("footage encryption: round-trip, seek, patch, tamper, plaintext compat", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i + 1);
            try
            {
                Recording.FootageVault.Configure(key, encryptNew: true);
                var path = Path.Combine(dir, "footage.bin");

                // 200 KB spans four 64 KiB slots (three full + a short tail).
                var data = new byte[200_000];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
                using (var w = Recording.FootageVault.Create(path))
                    w.Write(data);

                var disk = File.ReadAllBytes(path);
                Assert(Encoding.ASCII.GetString(disk, 0, 7) == "NLNKENC", "encrypted magic on disk");
                Assert(disk.AsSpan().IndexOf(data.AsSpan(0, 64)) < 0, "no plaintext run survives on disk");
                Assert(disk.Length > data.Length && disk.Length < data.Length + 4096,
                    $"overhead stays tiny (header + 28 B/slot), got {disk.Length - data.Length}");

                // Full read + random-access seeks, including a slot boundary and the tail.
                using (var r = Recording.FootageVault.OpenRead(path))
                {
                    AssertEq(r.Length, (long)data.Length);
                    var all = new byte[data.Length];
                    r.ReadExactly(all);
                    Assert(all.AsSpan().SequenceEqual(data), "full read round-trips");
                    Span<byte> probe = stackalloc byte[40];
                    foreach (int at in new[] { 65_516, 131_072, 12_345, 199_960 })
                    {
                        r.Seek(at, SeekOrigin.Begin);
                        r.ReadExactly(probe);
                        Assert(probe.SequenceEqual(data.AsSpan(at, 40)), $"seek+read at {at}");
                    }
                }

                // In-place patch (what finalize does): bytes land, everything else intact.
                using (var rw = Recording.FootageVault.OpenReadWrite(path))
                {
                    rw.Seek(12_345, SeekOrigin.Begin);
                    rw.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
                    rw.Seek(199_000, SeekOrigin.Begin); // tail slot too
                    rw.Write(new byte[] { 0x11, 0x22 });
                }
                data[12_345] = 0xAA; data[12_346] = 0xBB; data[12_347] = 0xCC; data[12_348] = 0xDD;
                data[199_000] = 0x11; data[199_001] = 0x22;
                using (var r = Recording.FootageVault.OpenRead(path))
                {
                    var all = new byte[data.Length];
                    r.ReadExactly(all);
                    Assert(all.AsSpan().SequenceEqual(data), "patched file round-trips");
                }

                // A FUTURE format version must be rejected loudly — never sniffed
                // as "not encrypted" and served as plaintext video.
                var vNext = File.ReadAllBytes(path);
                vNext[7] = 2; // bump the version byte in "NLNKENC\x01"
                var vNextPath = Path.Combine(dir, "footage-v2.bin");
                File.WriteAllBytes(vNextPath, vNext);
                bool vRejected = false;
                try { using var _ = Recording.FootageVault.OpenRead(vNextPath); }
                catch (InvalidDataException ex) when (ex.Message.Contains("upgrade")) { vRejected = true; }
                Assert(vRejected, "unknown format version is rejected with an upgrade hint");

                // Tampering a middle slot is DETECTED; a damaged final slot reads
                // as a truncated tail (the live-file trade), never as wrong bytes.
                var tampered = File.ReadAllBytes(path);
                tampered[32 + 28 + 1000] ^= 0x01; // inside slot 0's ciphertext
                File.WriteAllBytes(path, tampered);
                bool threw = false;
                try
                {
                    using var r = Recording.FootageVault.OpenRead(path);
                    r.ReadExactly(new byte[1000]);
                }
                catch (InvalidDataException) { threw = true; }
                Assert(threw, "mid-file tamper fails authentication");
                tampered[32 + 28 + 1000] ^= 0x01; // restore
                int slotSize = 64 * 1024 + 28;
                tampered[32 + 3 * slotSize + 100] ^= 0x01; // final (short) slot
                File.WriteAllBytes(path, tampered);
                using (var r = Recording.FootageVault.OpenRead(path))
                {
                    var got = new byte[data.Length];
                    int n = 0, read;
                    while ((read = r.Read(got, n, got.Length - n)) > 0) n += read;
                    AssertEq(n, 3 * 64 * 1024); // clean slots serve; the torn tail ends the stream
                    Assert(got.AsSpan(0, n).SequenceEqual(data.AsSpan(0, n)), "intact slots still correct");
                }
                tampered[32 + 3 * slotSize + 100] ^= 0x01;
                File.WriteAllBytes(path, tampered); // restore for the wrong-key check

                // The wrong key must fail loudly, not decode garbage.
                var wrong = (byte[])key.Clone();
                wrong[0] ^= 0xFF;
                Recording.FootageVault.Configure(wrong, encryptNew: true);
                threw = false;
                try
                {
                    using var r = Recording.FootageVault.OpenRead(path);
                    r.ReadExactly(new byte[1000]);
                }
                catch (InvalidDataException) { threw = true; }
                Assert(threw, "wrong key fails authentication");

                // Plaintext compatibility: with encryption OFF the vault writes and
                // serves plain files — and still decrypts old encrypted ones.
                Recording.FootageVault.Configure(key, encryptNew: false);
                var plainPath = Path.Combine(dir, "plain.bin");
                using (var w = Recording.FootageVault.Create(plainPath))
                    w.Write(data.AsSpan(0, 1000));
                Assert(File.ReadAllBytes(plainPath).AsSpan(0, 1000).SequenceEqual(data.AsSpan(0, 1000)),
                    "encryption off writes plain bytes");
                using (var r = Recording.FootageVault.OpenRead(plainPath))
                    AssertEq(r.Length, 1000L);
                using (var r = Recording.FootageVault.OpenRead(path))
                {
                    var all = new byte[data.Length];
                    r.ReadExactly(all); // key still configured → old encrypted footage plays
                    Assert(all.AsSpan().SequenceEqual(data), "encrypted footage outlives the toggle");
                }

                // Config parse: recording.encrypt reaches the setting (JSON + TOML).
                var cfgJson = Path.Combine(dir, "c.json");
                File.WriteAllText(cfgJson, $$"""
                    { "cameras": [], "recording": { "path": {{System.Text.Json.JsonSerializer.Serialize(dir)}}, "encrypt": true } }
                    """);
                Assert(Config.NeolinkConfig.Load(cfgJson).Recording!.Encrypt, "json recording.encrypt parses");
                var cfgToml = Path.Combine(dir, "c.toml");
                File.WriteAllText(cfgToml, $"[recording]\npath = \"{dir.Replace("\\", "\\\\")}\"\nencrypt = true\n");
                Assert(Config.NeolinkConfig.Load(cfgToml).Recording!.Encrypt, "toml recording.encrypt parses");
            }
            finally
            {
                Recording.FootageVault.Configure(null, encryptNew: false); // leave no key behind for other tests
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("footage encryption: ClipWriter → playback path end-to-end", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(0x40 + i);
            try
            {
                Recording.FootageVault.Configure(key, encryptNew: true);

                // A real clip through the real writer, encrypted at rest.
                var hub = new Streaming.StreamHub("enctest");
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
                    foreach (var n in nals) { ms.Write(new byte[] { 0, 0, 0, 1 }); ms.Write(n); }
                    return ms.ToArray();
                }
                var sps = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA0 };
                var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
                hub.PublishVideo(new VideoFrame(VideoCodec.H264, Keyframe: true, Microseconds: 0, UnixTime: null,
                    Au(sps, pps, Nal(0x65, 40))));
                var path = Path.Combine(dir, "clip.mp4");
                var writer = Recording.ClipWriter.TryCreate(path, hub)!;
                long index = 0;
                uint ts = 1000;
                writer.Add(new Streaming.HubVideo(index, Au(Nal(0x65, 40)), true, ts));
                for (int i = 0; i < 5; i++)
                    writer.Add(new Streaming.HubVideo(index += 2, Au(Nal(0x41, 25)), false, ts += 3000));
                writer.Dispose();
                Assert(writer.Completion.Wait(TimeSpan.FromSeconds(10)), "writer finalizes");
                Assert(!writer.Faulted, "no write faults");

                // On disk: ciphertext. Through the serving path: a classic MP4.
                var disk = File.ReadAllBytes(path);
                Assert(Encoding.ASCII.GetString(disk, 0, 7) == "NLNKENC", "clip is encrypted at rest");
                byte[] served;
                using (var v = Recording.VirtualMp4.Open(path))
                {
                    served = new byte[v.Length];
                    v.ReadExactly(served);
                    // Range-style read (what HTTP seeking does) matches the full view.
                    Span<byte> slice = stackalloc byte[64];
                    v.Seek(served.Length - 100, SeekOrigin.Begin);
                    v.ReadExactly(slice);
                    Assert(slice.SequenceEqual(served.AsSpan(served.Length - 100, 64)), "ranged read matches");
                }
                AssertEq(Encoding.ASCII.GetString(served, 4, 4), "ftyp");
                var tail = Encoding.ASCII.GetString(served, served.Length - 2048, 2048);
                Assert(tail.Contains("moov") && tail.Contains("stco"), "decrypted clip carries the classic index");

                // Thumbnails ride the same vault.
                var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
                var thumbPath = Path.Combine(dir, "thumb.jpg");
                Recording.FootageVault.WriteAllBytesAsync(thumbPath, jpeg, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(!File.ReadAllBytes(thumbPath).AsSpan().StartsWith(jpeg), "thumb is encrypted at rest");
                using (var r = Recording.FootageVault.OpenRead(thumbPath))
                {
                    var got = new byte[jpeg.Length];
                    r.ReadExactly(got);
                    Assert(got.AsSpan().SequenceEqual(jpeg), "thumb decrypts");
                }

                // An OLD-LAYOUT (fragmented) encrypted file — what a crash leaves
                // behind — must serve through the virtual index AND survive the
                // in-place upgrade, both through the decrypting layer.
                var init = FMp4.BuildInit(VideoCodec.H264, sps, pps, null, 640, 360);
                var fragPath = Path.Combine(dir, "frag.mp4");
                using (var fs = Recording.FootageVault.Create(fragPath))
                {
                    fs.Write(init);
                    ulong dt = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        var sample = new byte[24];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sample, 20);
                        sample[4] = i % 5 == 0 ? (byte)0x65 : (byte)0x41;
                        fs.Write(FMp4.BuildFragment((uint)(i + 1), dt, 3000, sample, i % 5 == 0));
                        dt += 3000;
                    }
                }
                using (var v = Recording.VirtualMp4.Open(fragPath))
                {
                    var head = new byte[8];
                    v.ReadExactly(head);
                    AssertEq(Encoding.ASCII.GetString(head, 4, 4), "ftyp");
                }
                Assert(Recording.ClipWriter.RefinalizeClassic(fragPath), "encrypted fragmented file upgrades in place");
                Assert(!Recording.ClipWriter.RefinalizeClassic(fragPath), "second run is a no-op");
            }
            finally
            {
                Recording.FootageVault.Configure(null, encryptNew: false);
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

        Test("continuous recorder: silence closes the segment (suspend = timeline gap)", () =>
        {
            // A suspended (or offline) camera stops publishing but the hub stays
            // open. The recorder must finalize the open segment instead of gluing
            // resumed footage into it — ClipWriter clamps big timestamp jumps, so
            // gluing would time-compress the gap and misplace everything after it.
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var realRoll = Recording.ContinuousRecorder.SilenceRoll;
            Recording.ContinuousRecorder.SilenceRoll = TimeSpan.FromMilliseconds(250);
            try
            {
                var hub = new Streaming.StreamHub("gapcam");
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
                byte[] KeyAu() => Au(sps, pps, Nal(0x65, 40));
                // Teach the hub its codec parameters before the recorder subscribes.
                hub.PublishVideo(new VideoFrame(VideoCodec.H264, Keyframe: true, Microseconds: 0, UnixTime: null, KeyAu()));

                var settings = new Recording.RecordingSettings(dir);
                settings.Update("gapcam", events: null, continuous: true, eventTypes: null, setEventTypes: false);
                var store = new Recording.EventStore(Path.Combine(dir, "rec"));
                var recorder = new Recording.ContinuousRecorder("gapcam", hub, store, settings,
                    new Config.RecordingConfig { SegmentMinutes = 10, MaxSegmentSizeMb = 256 });

                using var cts = new CancellationTokenSource();
                var run = Task.Run(() => recorder.RunAsync(cts.Token));

                uint us = 0;
                void Frames()
                {
                    hub.PublishVideo(new VideoFrame(VideoCodec.H264, true, us += 33_000, null, KeyAu()));
                    for (int i = 0; i < 4; i++)
                        hub.PublishVideo(new VideoFrame(VideoCodec.H264, false, us += 33_000, null, Au(Nal(0x41, 25))));
                }
                bool Poll(Func<bool> done, int ms = 5000)
                {
                    var until = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < until)
                    {
                        if (done()) return true;
                        Thread.Sleep(25);
                    }
                    return done();
                }
                string[] Segments() => Directory.Exists(Path.Combine(dir, "rec"))
                    ? Directory.GetFiles(Path.Combine(dir, "rec"), "*.mp4", SearchOption.AllDirectories)
                    : Array.Empty<string>();

                // Frames flow → one segment opens. The recorder may still be
                // subscribing, so feed it until the file appears.
                Assert(Poll(() => { Frames(); return recorder.IsWriting; }), "segment opens while frames flow");
                AssertEq(Segments().Length, 1);
                var first = Segments()[0];

                // While writing, the recorder itself names the open segment — the
                // day listing overlays this (an open file's mtime is stale on
                // NTFS/FUSE/network mounts, which made lanes trail behind "now").
                Assert(recorder.ActiveSegment is { } act0
                       && act0.File == Path.GetFileName(first)
                       && act0.Date == new DirectoryInfo(first).Parent!.Parent!.Name,
                    "active segment reports the open file and its day");

                // Silence (the suspension): after SilenceRoll the segment must be
                // CLOSED — bounded at the true end time, gap left on the timeline.
                Assert(Poll(() => !recorder.IsWriting), "silence closes the segment");
                Assert(recorder.ActiveSegment == null, "no active segment once closed");
                // Finalization happens on the writer's own thread: a classic MP4 is
                // exactly ftyp + free + moov (the live header has trailing moofs).
                static List<string> TopBoxes(byte[] b)
                {
                    var list = new List<string>();
                    int pos = 0;
                    while (pos + 8 <= b.Length)
                    {
                        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
                        if (size < 8 || pos + size > (uint)b.Length) break;
                        list.Add(Encoding.ASCII.GetString(b, pos + 4, 4));
                        pos += (int)size;
                    }
                    return list;
                }
                Assert(Poll(() =>
                {
                    // IsWriting drops at Dispose, but the writer's thread may
                    // still hold the handle — "file busy" means "not yet".
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(first); }
                    catch (IOException) { return false; }
                    var boxes = TopBoxes(bytes);
                    return boxes.Count == 3 && boxes[^1] == "moov";
                }), "closed segment is finalized (playable, bounded)");

                // Let the wall clock move past the old file's HH-mm-ss stamp, then
                // resume: footage must land in a NEW segment, not the old file.
                Thread.Sleep(1100);
                Assert(Poll(() => { Frames(); return recorder.IsWriting; }), "recording resumes after the gap");
                AssertEq(Segments().Length, 2);
                Assert(new FileInfo(first).Length > 200, "first segment untouched by the resume");

                cts.Cancel();
                Assert(run.Wait(TimeSpan.FromSeconds(10)), "recorder stops cleanly");
            }
            finally
            {
                Recording.ContinuousRecorder.SilenceRoll = realRoll;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("day listing overlays the live segment (stale-mtime lanes)", () =>
        {
            var listed = new List<(string File, long Size, double Seconds)>
            {
                ("06-00-00.mp4", 1000, 600),
                ("06-10-00.mp4", 500, 3), // the open file: mtime barely moved since creation
            };

            // The recorder's truth wins for the open file: real duration + live flag.
            var merged = Web.WebApi.OverlayActiveSegment(listed, ("2026-07-15", "06-10-00.mp4", 240.0), "2026-07-15");
            AssertEq(merged.Count, 2);
            Assert(merged[1] is { File: "06-10-00.mp4", Seconds: 240.0, Live: true }, "open file gets recorder duration + live");
            Assert(merged[0] is { Live: false, Seconds: 600.0 }, "closed files untouched");

            // Enumeration missed the open file entirely (attribute-cached mounts):
            // it is appended in order.
            merged = Web.WebApi.OverlayActiveSegment(
                new List<(string, long, double)> { ("06-00-00.mp4", 1000, 600) },
                ("2026-07-15", "06-10-00.mp4", 42.0), "2026-07-15");
            AssertEq(merged.Count, 2);
            Assert(merged[1] is { File: "06-10-00.mp4", Seconds: 42.0, Live: true }, "missing open file appended");

            // A different day (or no active segment) leaves the listing as-is.
            merged = Web.WebApi.OverlayActiveSegment(listed, ("2026-07-14", "23-55-00.mp4", 9000.0), "2026-07-15");
            Assert(merged.All(s => !s.Live), "other-day active segment ignored");
            merged = Web.WebApi.OverlayActiveSegment(listed, null, "2026-07-15");
            Assert(merged.All(s => !s.Live), "no active segment, no live flags");
        });

        Test("export: segments combine into one classic MP4", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
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

                string Write(Streaming.StreamHub hub, byte[] sps, byte[] pps, string name, int frames)
                {
                    var path = Path.Combine(dir, name);
                    var w = Recording.ClipWriter.TryCreate(path, hub)!;
                    Assert(w != null, "writer created");
                    uint ts = 0;
                    for (int i = 0; i < frames; i++)
                    {
                        bool key = i % 5 == 0;
                        w!.Add(new Streaming.HubVideo(i, key ? Au(sps, pps, Nal(0x65, 40)) : Au(Nal(0x41, 25 + i)), key, ts));
                        ts += 3000; // 90 kHz, ~30 fps
                    }
                    w!.Dispose();
                    Assert(w.Completion.Wait(TimeSpan.FromSeconds(10)), "segment written");
                    return path;
                }

                var sps = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA0 };
                var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
                var hub = new Streaming.StreamHub("cat");
                hub.PublishVideo(new VideoFrame(VideoCodec.H264, true, 0, null, Au(sps, pps, Nal(0x65, 40))));
                var p1 = Write(hub, sps, pps, "a.mp4", 10);
                var p2 = Write(hub, sps, pps, "b.mp4", 8);

                var plan = Recording.Mp4Export.TryPlan(new[] { (p1, 0.0), (p2, 1000.0) }, 0, 86400, out var reason);
                Assert(plan != null, $"segments are combinable ({reason})");
                // 10 + 8 frames at 3000 ticks each = 54000 ticks = 600 ms.
                AssertEq(plan!.DurationMs, 600ul);

                var outPath = Path.Combine(dir, "combined.mp4");
                using (var os = File.Create(outPath))
                    Recording.Mp4Export.WriteAsync(plan, os, CancellationToken.None).Wait();
                AssertEq(new FileInfo(outPath).Length, plan.TotalBytes);

                // Fast-start classic shape: exactly ftyp · moov · mdat.
                var bytes = File.ReadAllBytes(outPath);
                var boxes = new List<string>();
                int pos = 0;
                while (pos + 8 <= bytes.Length)
                {
                    uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
                    if (size < 8 || pos + size > (uint)bytes.Length) break;
                    boxes.Add(Encoding.ASCII.GetString(bytes, pos + 4, 4));
                    pos += (int)size;
                }
                Assert(boxes.SequenceEqual(new[] { "ftyp", "moov", "mdat" }), $"classic fast-start layout ({string.Join('·', boxes)})");

                // The combined file parses with the same scanner: every sample
                // indexed, duration preserved — proof the tables are coherent.
                var re = Recording.Mp4Export.TryPlan(new[] { (outPath, 0.0) }, 0, 86400, out var reReason);
                Assert(re != null, $"combined file parses ({reReason})");
                AssertEq(re!.DurationMs, plan.DurationMs);
                AssertEq(re.Copies[0].Runs.Count, plan.Copies.Sum(c => c.Runs.Count));

                // Trimmed export: the range starts mid-segment-1 and ends
                // mid-segment-2. Keyframes sit at frames 0 and 5 (15000 ticks):
                // from = 0.2 s (18000 ticks) snaps back to the keyframe at 15000
                // → frames 5..9 of A (15000 ticks of footage); to = 0.2 s into B
                // keeps its frames 0..5 (18000 ticks). 33000 ticks = 366 ms.
                var trimmed = Recording.Mp4Export.TryPlan(new[] { (p1, 0.0), (p2, 10.0) }, 0.2, 10.2, out var tReason);
                Assert(trimmed != null, $"trimmed plan built ({tReason})");
                AssertEq(trimmed!.DurationMs, 366ul);
                AssertEq(trimmed.Copies.Sum(c => c.Runs.Count), 11);
                var tOut = Path.Combine(dir, "trimmed.mp4");
                using (var os = File.Create(tOut))
                    Recording.Mp4Export.WriteAsync(trimmed, os, CancellationToken.None).Wait();
                AssertEq(new FileInfo(tOut).Length, trimmed.TotalBytes);
                var tRe = Recording.Mp4Export.TryPlan(new[] { (tOut, 0.0) }, 0, 86400, out var tReReason);
                Assert(tRe != null && tRe.Copies[0].Runs.Count == 11, $"trimmed file parses ({tReReason})");
                AssertEq(tRe!.DurationMs, 366ul);

                // Byte fidelity: the first sample's payload is copied verbatim.
                var (srcOff, srcSize) = plan.Copies[0].Runs[0];
                var src = new byte[srcSize];
                using (var f = File.OpenRead(p1)) { f.Position = srcOff; f.ReadExactly(src); }
                Assert(bytes.AsSpan(plan.Header.Length, (int)srcSize).SequenceEqual(src), "sample bytes copied verbatim");

                // A segment with a different stream config must be refused with a
                // reason (single MP4 tracks cannot change resolution mid-stream).
                var sps2 = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA1 };
                var hub2 = new Streaming.StreamHub("cat2");
                hub2.PublishVideo(new VideoFrame(VideoCodec.H264, true, 0, null, Au(sps2, pps, Nal(0x65, 40))));
                var p3 = Write(hub2, sps2, pps, "c.mp4", 5);
                Assert(Recording.Mp4Export.TryPlan(new[] { (p1, 0.0), (p3, 1000.0) }, 0, 86400, out var why) == null
                       && why != null && why.Contains("video"),
                    "mixed video config refused with a reason");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("export picker: overlap, live-file exclusion, ordering", () =>
        {
            var day = new List<(string File, long Size, double Seconds)>
            {
                ("06-10-00.mp4", 20, 600), // out of order on purpose
                ("06-00-00.mp4", 10, 600),
                ("06-20-00.mp4", 30, 600),
                ("07-00-00.mp4", 40, 0),   // unknown duration: counts ≥ 1 s
                ("07-10-00.mp4", 50, 300), // the live file (excluded below)
                ("junk.txt", 999, 60),     // unparseable name: ignored
            };

            // Range clips both ends: 06:05–06:15 touches the first two segments only.
            var picked = Web.WebApi.PickExportSegments(day, null, 6 * 3600 + 300, 6 * 3600 + 900, out long bytes);
            Assert(picked.SequenceEqual(new[] { "06-00-00.mp4", "06-10-00.mp4" }), "overlapping segments, oldest first");
            AssertEq(bytes, 30L);

            // A range crossing an unknown-duration file's start still catches it;
            // the live file is excluded even when the range covers it.
            picked = Web.WebApi.PickExportSegments(day, "07-10-00.mp4", 7 * 3600, 8 * 3600, out bytes);
            Assert(picked.SequenceEqual(new[] { "07-00-00.mp4" }), "unknown-duration caught, live file skipped");
            AssertEq(bytes, 40L);

            // Nothing in range → empty (the endpoint answers 404, not an empty zip).
            picked = Web.WebApi.PickExportSegments(day, null, 3 * 3600, 4 * 3600, out bytes);
            AssertEq(picked.Count, 0);
            AssertEq(bytes, 0L);
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
            // A real config file: /api/admin/config reads and describes it.
            File.WriteAllText(Path.Combine(dir, "config.json"), """{ "cameras": [] }""");

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
                // A second, snapshot-capable camera: exercises /snapshot.jpg (the
                // stub above answers null = "no snapshot support").
                var snapControl = new SnapStub("snapcam");
                var snapCam = new Web.WebCameraInfo("snapcam",
                    new List<Web.WebStreamInfo> { new("mainStream", "/snapcam/mainStream", new Streaming.StreamHub("snapcam")) },
                    snapControl, PermittedUsers: null);
                server = Web.WebApi.RunAsync(new Web.WebApiOptions
                {
                    BindAddr = "127.0.0.1",
                    Port = port,
                    WebUi = false,
                    Cameras = new[] { cam, snapCam },
                    // One RTSP user: the snapshot endpoint accepts these over
                    // HTTP Basic, like the rtsp:// stream URLs do.
                    Users = new Dictionary<string, string> { ["rtspuser"] = "rtsp pass" },
                    RtspPort = 8654,
                    Events = store,
                    RecordingSettings = new Recording.RecordingSettings(dir),
                    UserStore = new Web.UserStore(dir),
                    Secrets = new Neolink.Notifications.SecretProtector(dir),
                    // The admin config endpoint reads the file — give it one.
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
                AssertEq(cams.GetArrayLength(), 2);
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

                // Snapshot endpoint: a JPEG from the camera, served through a short
                // per-camera cache so a poll storm reaches the camera once.
                using (var snap = http.GetAsync("/api/cameras/snapcam/snapshot.jpg").Result)
                {
                    AssertEq((int)snap.StatusCode, 200);
                    AssertEq(snap.Content.Headers.ContentType!.MediaType!, "image/jpeg");
                    var body = snap.Content.ReadAsByteArrayAsync().Result;
                    Assert(body.Length == 200 && body[0] == 0xFF && body[1] == 0xD8, "JPEG body served");
                    AssertEq(snap.Headers.GetValues("X-Snapshot-Age").First(), "0");
                }
                AssertEq(snapControl.Calls, 1);
                using (var snap = http.GetAsync("/api/cameras/snapcam/snapshot.jpg").Result)
                    AssertEq((int)snap.StatusCode, 200);
                AssertEq(snapControl.Calls, 1); // second poll inside maxAge = cache hit
                using (var snap = http.GetAsync("/api/cameras/snapcam/snapshot.jpg?maxAge=0").Result)
                    AssertEq((int)snap.StatusCode, 200);
                AssertEq(snapControl.Calls, 2); // maxAge=0 forces a fresh frame

                // Staleness bound. With the camera no longer answering, the handler
                // falls back to the cached frame — but only within maxStale. That
                // bound used to be absent, which is how a dashboard tile could paint
                // an hours-old scene and then jump when the live feed arrived.
                snapControl.Offline = true;
                using (var snap = http.GetAsync("/api/cameras/snapcam/snapshot.jpg?maxAge=0&maxStale=60").Result)
                {
                    AssertEq((int)snap.StatusCode, 200); // seconds old — still useful
                    Assert(snap.Headers.Contains("X-Snapshot-Stale"), "fallback frame is labelled stale");
                }
                // maxStale=0 makes even a one-second-old frame too old: better a
                // black tile than a scene the live feed will jump away from.
                using (var snap = http.GetAsync("/api/cameras/snapcam/snapshot.jpg?maxAge=0&maxStale=0").Result)
                    AssertEq((int)snap.StatusCode, 503);
                snapControl.Offline = false;
                // A camera without snapshot support (SnapshotAsync = null) is a 404,
                // and so is a camera that does not exist.
                AssertEq((int)http.GetAsync("/api/cameras/apicam/snapshot.jpg").Result.StatusCode, 404);

                // SD download contract: the UI sends ?dl=1 — that must reach the
                // handler (the stub camera answers 404 "not supported"), never die
                // in ASP.NET's bool binding as an empty 400 (field report: every
                // SD download failed 400 with nothing in the logs).
                AssertEq((int)http.GetAsync("/api/cameras/apicam/sdcard/download?file=a.mp4&dl=1").Result.StatusCode, 404);
                AssertEq((int)http.GetAsync("/api/cameras/apicam/sdcard/download?file=a.mp4&dl=true").Result.StatusCode, 404);
                AssertEq((int)http.GetAsync("/api/cameras/apicam/sdcard/download?dl=1").Result.StatusCode, 400); // no file: the handler's OWN 400
                AssertEq((int)http.GetAsync("/api/cameras/nope/snapshot.jpg").Result.StatusCode, 404);

                // Background-process feed (no-auth server: open, like other admin
                // surfaces). Empty when idle; a running job shows name+percent.
                AssertEq(GetJson(http, "/api/background").GetArrayLength(), 0);
                using (var job = BackgroundTasks.Begin("Archiving footage", "cam1 · 2026-01-01", 12.5))
                {
                    var bg = GetJson(http, "/api/background");
                    AssertEq(bg.GetArrayLength(), 1);
                    AssertEq(bg[0].GetProperty("name").GetString()!, "Archiving footage");
                    AssertEq(bg[0].GetProperty("detail").GetString()!, "cam1 · 2026-01-01");
                    AssertEq(bg[0].GetProperty("percent").GetDouble(), 12.5);
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
                using (var res = http.GetAsync("/api/cameras/snapcam/snapshot.jpg").Result)
                {
                    AssertEq((int)res.StatusCode, 401);
                    Assert(res.Headers.WwwAuthenticate.Any(h => h.Scheme == "Basic"),
                        "snapshot 401 challenges for Basic (HA generic camera flow)");
                }
                // RTSP Basic credentials open the snapshot even with accounts on —
                // the still-image twin of the rtsp:// stream URLs. Wrong password
                // stays out.
                string BasicHdr(string u, string p) =>
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u}:{p}"));
                using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/cameras/snapcam/snapshot.jpg"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", BasicHdr("rtspuser", "rtsp pass"));
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 200);
                    AssertEq(res.Content.Headers.ContentType!.MediaType!, "image/jpeg");
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/cameras/snapcam/snapshot.jpg"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", BasicHdr("rtspuser", "wrong pass"));
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 401);
                }
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

                // The admin config report includes the live encryption-key facts:
                // source, one-way fingerprint (12 hex chars), never the key.
                var adminCfg = GetJson(http, $"/api/admin/config{tokenQ}");
                var encInfo = adminCfg.GetProperty("encryption");
                AssertEq(encInfo.GetProperty("source").GetString()!, "file");
                var fp = encInfo.GetProperty("fingerprint").GetString()!;
                Assert(fp.Length == 12 && fp.All(Uri.IsHexDigit), "key fingerprint is 12 hex chars");
                Assert(encInfo.GetProperty("file").GetString()!.EndsWith("secret.key"),
                    "file-based key reports its path");

                // With accounts on, the background feed is admin-only: a normal
                // user gets 403, the admin still reads it.
                using (var res = PostRaw(http, $"/api/users{tokenQ}", """{"username":"viewer","password":"viewer pass"}"""))
                    AssertEq((int)res.StatusCode, 200);
                var viewerTok = PostJson(http, "/api/auth/login", """{"username":"viewer","password":"viewer pass"}""")
                    .GetProperty("token").GetString()!;
                AssertEq((int)http.GetAsync($"/api/background?token={Uri.EscapeDataString(viewerTok)}").Result.StatusCode, 403);
                AssertEq((int)http.GetAsync($"/api/background{tokenQ}").Result.StatusCode, 200);

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
    /// <summary>A stub that records what the last LedState write asked for, so the
    /// HA status-LED switch can be checked against the exact field it must drive.</summary>
    private sealed class LedRecordingControl(string name) : StubCameraControl(name)
    {
        public string? LastState, LastLightState, LastDoorbell;
        public int? LastIrBrightness;

        public override Task SetLedStateAsync(string? state, string? lightState,
            string? doorbellLightState, int? irBrightness, CancellationToken ct)
        {
            LastState = state;
            LastLightState = lightState;
            LastDoorbell = doorbellLightState;
            LastIrBrightness = irBrightness;
            return Task.CompletedTask;
        }
    }

    private class StubCameraControl(string name) : Streaming.ICameraControl
    {
        public string CameraName => name;
        public bool Online => true;
        public bool CanSetStreamSettings => false;
        public Task<IReadOnlyList<Streaming.StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Streaming.StreamEncSetting>?>(null);
        public virtual Task<Streaming.CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Bc.Xml.StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct) =>
            Task.FromResult<Bc.Xml.StreamInfoListXml?>(null);
        public Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
            uint? framerate, uint? bitrate, CancellationToken ct) => throw new NotSupportedException();
        public Task<System.Xml.Linq.XElement?> GetBatteryInfoAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public virtual Task<byte[]?> SnapshotAsync(CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<System.Xml.Linq.XElement?> GetLedStateAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public virtual Task SetLedStateAsync(string? state, string? lightState,
            string? doorbellLightState, int? irBrightness, CancellationToken ct) => Task.CompletedTask;
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
        public Task<Streaming.WhiteLedState?> GetWhiteLedAsync(CancellationToken ct) => Task.FromResult<Streaming.WhiteLedState?>(null);
        public Task SetWhiteLedAsync(int? bright, bool? on, int? mode, CancellationToken ct) => Task.CompletedTask;
        public Task<Streaming.HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<Streaming.HttpFeatures?>(null);
        public Task<Streaming.ImageSettings?> GetImageSettingsAsync(CancellationToken ct) =>
            Task.FromResult<Streaming.ImageSettings?>(null);
        public Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue, int? sharpen,
            string? dayNight, string? antiFlicker, bool? flip, bool? mirror, CancellationToken ct) => Task.CompletedTask;
        public Task<int?> GetVolumeAsync(CancellationToken ct) => Task.FromResult<int?>(null);
        public Task SetVolumeAsync(int volume, CancellationToken ct) => Task.CompletedTask;
        public Task<Streaming.WifiReading?> GetWifiSignalAsync(CancellationToken ct) =>
            Task.FromResult<Streaming.WifiReading?>(null);
        public Task<IReadOnlyList<Streaming.PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Streaming.PtzPresetInfo>?>(null);
        public Task PtzToPresetAsync(int id, CancellationToken ct) => Task.CompletedTask;
        public Task SavePtzPresetAsync(int id, string name, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Streaming.QuickReplyFile>?> GetQuickRepliesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Streaming.QuickReplyFile>?>(null);
        public Task PlayQuickReplyAsync(int id, CancellationToken ct) => Task.CompletedTask;
        public Task<Streaming.AutoReplyState?> GetAutoReplyAsync(CancellationToken ct) =>
            Task.FromResult<Streaming.AutoReplyState?>(null);
        public Task SetAutoReplyAsync(int? fileId, int? timeoutSeconds, CancellationToken ct) => Task.CompletedTask;
        public Task<bool?> GetAutoTrackAsync(CancellationToken ct) => Task.FromResult<bool?>(null);
        public Task SetAutoTrackAsync(bool on, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Streaming.SdCardInfo>?> GetSdCardsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Streaming.SdCardInfo>?>(null);
        public virtual Task TalkAsync(int sampleRate, ChannelReader<byte[]> pcm, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>A snapshot-capable camera returning a canned JPEG and counting
    /// calls, so the endpoint's cache/single-flight behavior is observable.</summary>
    private sealed class SnapStub(string name) : StubCameraControl(name)
    {
        public int Calls;
        /// <summary>Set to make the camera stop answering, so the handler falls back
        /// to its cached frame — the path the maxStale bound governs.</summary>
        public bool Offline;

        public override Task<byte[]?> SnapshotAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Offline) throw new Streaming.CameraOfflineException(CameraName);
            var jpeg = new byte[200];
            jpeg[0] = 0xFF; jpeg[1] = 0xD8; // SOI — enough to pass the sanity check
            return Task.FromResult<byte[]?>(jpeg);
        }
    }

    /// <summary>A talk-capable camera that records the PCM streamed to its speaker,
    /// for the RTSP audio-backchannel integration test.</summary>
    private sealed class BackchannelStub(string name) : StubCameraControl(name)
    {
        public int SampleRate;
        public readonly List<byte> Received = new();
        private readonly TaskCompletionSource _got = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstAudio => _got.Task;

        public override Task<Streaming.CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
            Task.FromResult(new Streaming.CameraCapabilities(null, null,
                new Streaming.CameraFeatures(Ptz: false, Led: false, Pir: false, Battery: false, Talk: true)));

        public override async Task TalkAsync(int sampleRate, ChannelReader<byte[]> pcm, CancellationToken ct)
        {
            SampleRate = sampleRate;
            await foreach (var chunk in pcm.ReadAllAsync(ct).ConfigureAwait(false))
            {
                lock (Received) Received.AddRange(chunk);
                _got.TrySetResult();
            }
        }
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

    /// <summary>Wraps the client's connection stream so the test can stall a write:
    /// while <see cref="Stall"/> is set, WriteAsync parks on the token it was given —
    /// exactly what a backpressured socket write looks like. Real kernels buffer
    /// too much to reproduce this with sockets alone (a 1 GB loopback send
    /// "completes" unread).</summary>
    private sealed class StallableStream(Stream inner) : Stream
    {
        public volatile bool Stall;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Stall) await Task.Delay(Timeout.Infinite, ct);
            await inner.WriteAsync(buffer, ct);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            inner.ReadAsync(buffer, ct);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A publish cancelled while its bytes may be partially on the wire must
    /// close the connection: the frame boundary is unknowable afterwards, and
    /// continuing to write (pings, later publishes) desyncs the broker's parser
    /// until it kicks the client with "oversize packet" (observed in the field
    /// against the HA Mosquitto add-on, as a clockwork ~2-minute disconnect loop).
    /// The proof of closure is the stub broker seeing EOF — the buggy behavior
    /// left the socket open and kept writing into it.</summary>
    private static async Task RunMqttCancelledWrite()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var runCts = new CancellationTokenSource();
        StallableStream? wrapped = null;
        var client = new Mqtt.MqttClient(new Mqtt.MqttClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            ClientId = "selftest",
            MaxPacketBytes = 16 * 1024,
        })
        { StreamWrapper = s => wrapped = new StallableStream(s) };
        var run = Task.Run(() => client.RunAsync(runCts.Token));
        try
        {
            using var server = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var ns = server.GetStream();
            // Drain the CONNECT (no need to parse it) and answer with a CONNACK.
            var buf = new byte[512];
            await ns.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await ns.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x00 });
            for (int i = 0; i < 250 && !client.IsConnected; i++) await Task.Delay(20);
            Assert(client.IsConnected, "client connected to the stub broker");

            // A packet over the broker's size limit must be DROPPED, not sent —
            // Mosquitto 2.1+ answers an oversized publish by disconnecting the
            // client (a 4K snapshot would cycle the shared connection every 2 min).
            bool sent = await client.PublishAsync("t/big", new byte[32 * 1024], retain: false, CancellationToken.None);
            Assert(!sent, "oversized publish reports failure");
            Assert(client.IsConnected, "oversized publish leaves the connection intact");

            // A caller cancelling its OWN token must not disturb the shared
            // connection: the write runs under the client's watchdog instead, so a
            // camera session winding down mid-publish can't corrupt the framing.
            using (var callerCts = new CancellationTokenSource())
            {
                callerCts.Cancel();
                bool threw = false;
                try { await client.PublishAsync("t", "x", retain: false, callerCts.Token); }
                catch (OperationCanceledException) { threw = true; }
                Assert(threw, "pre-cancelled caller is refused at the gate");
                Assert(client.IsConnected, "caller cancellation leaves the connection intact");
            }

            // Stall the stream and publish: the write parks like a backpressured
            // socket until the client's own watchdog expires it mid-frame.
            wrapped!.Stall = true;
            client.WriteTimeout = TimeSpan.FromMilliseconds(200);
            bool ok = await client.PublishAsync("t", "x", retain: false, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!ok, "stalled publish reports failure");
            for (int i = 0; i < 250 && client.IsConnected; i++) await Task.Delay(20);
            Assert(!client.IsConnected, "connection reads as down after a mid-frame stall");
            // The socket must actually close (broker sees EOF) — not linger half-open
            // collecting desynced pings until the broker kicks it as "oversize packet".
            int n = await ns.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            AssertEq(n, 0);
        }
        finally
        {
            runCts.Cancel();
            listener.Stop();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
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

    /// <summary>End-to-end transport test: a loopback "camera" performs the UDP
    /// discovery handshake, then sends a real BC message split across two data
    /// packets delivered OUT OF ORDER. Proves the whole path — handshake, inbound
    /// reorder + reassembly, pipe → BcCodec framing, subscription dispatch — plus
    /// that the client's own send is received and acked (no retransmit storm).</summary>
    private static async Task RunBcUdpTransport()
    {
        const string uid = "TESTUID0000000LO";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cam = BindLoopbackUdp(2015);
        int clientDataPackets = 0;
        int clientAckPackets = 0;
        int clientC2dA = 0;
        int clientHbSameTid = 0, clientHbOtherTid = 0;
        int releasedOrphan = 0; // C2D_DISC for the duplicate session (did 8)
        int kaReplies = 0;      // answers to the camera's BC keepalive (msg 234)
        uint hsTid = 0; // the tid of the C2D_C the mock accepted — the session's tid
        // Phase gates so the mock injects the duplicate-session traffic only after
        // the main body has finished its first round of asserts.
        var phase2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The canned message the "camera" will send us: a header-only ping, built
        // and serialized exactly as the real code would (unencrypted session).
        var pingBytes = Neolink.Bc.BcCodec.Serialize(
            Neolink.Bc.BcMessage.HeaderOnly(new Neolink.Bc.BcMeta
            {
                MsgId = Neolink.Bc.BcConstants.MsgIdPing,
                Class = Neolink.Bc.BcConstants.ClassModern,
            }),
            new Neolink.Bc.EncryptionState());

        var mock = Task.Run(async () =>
        {
            System.Net.IPEndPoint? client = null;
            int cid = 0;
            // 1. Handshake: accept the C2D_C, reply D2C_C_R rsp 0 — echoing the
            //    request's tid, as the real firmware does (the camera keys the
            //    session to it).
            while (!cts.IsCancellationRequested && client == null)
            {
                var r = await cam.ReceiveAsync(cts.Token);
                if (!UdpDiscovery.TryParseDiscovery(r.Buffer, out var htid, out var xml, out _)) continue;
                if (!xml.Contains("<C2D_C>", StringComparison.Ordinal)) continue;
                cid = int.Parse(xml.Split("<cid>")[1].Split("</cid>")[0]);
                hsTid = htid;
                var reply = $"<P2P><D2C_C_R><timer><def>3000</def><hb>20000</hb><hbt>60000</hbt></timer>" +
                            $"<rsp>0</rsp><cid>{cid}</cid><did>7</did></D2C_C_R></P2P>";
                await cam.SendAsync(UdpDiscovery.BuildDiscovery(htid, reply), r.RemoteEndPoint, cts.Token);
                client = r.RemoteEndPoint;
            }
            if (client == null) return;

            // 2. Send D2C_T — the camera's session-confirm request; the client must
            //    reply C2D_A or the camera would recycle the session.
            var d2ct = "<P2P><D2C_T><sid>42</sid><conn>local</conn><cid>" +
                       $"{cid}</cid><did>7</did></D2C_T></P2P>";
            await cam.SendAsync(UdpDiscovery.BuildDiscovery(55, d2ct), client, cts.Token);

            // 3. Send the ping as two data packets, OUT OF ORDER (pid 1 before 0),
            //    stamped with the client's cid (the direction the client expects).
            int half = pingBytes.Length / 2;
            var p0 = BcUdp.BuildData(cid, 0, pingBytes.AsSpan(0, half));
            var p1 = BcUdp.BuildData(cid, 1, pingBytes.AsSpan(half));
            await cam.SendAsync(p1, client, cts.Token);
            await Task.Delay(40, cts.Token);
            await cam.SendAsync(p0, client, cts.Token);

            // 3b. Duplicate-session scenario (phase-gated): real battery cameras run
            //     one session per hello they answer, and a duplicate's paperwork
            //     arrives on the same socket. The client must release the duplicate,
            //     ignore ITS disconnect, keep our stream alive — and still honor a
            //     disconnect addressed to OUR did.
            _ = Task.Run(async () =>
            {
                try
                {
                    await phase2.Task;
                    var dup = $"<P2P><D2C_C_R><rsp>0</rsp><cid>{cid}</cid><did>8</did></D2C_C_R></P2P>";
                    await cam.SendAsync(UdpDiscovery.BuildDiscovery(hsTid, dup), client, cts.Token);
                    var foreignDisc = $"<P2P><D2C_DISC><cid>{cid}</cid><did>9</did></D2C_DISC></P2P>";
                    await cam.SendAsync(UdpDiscovery.BuildDiscovery(hsTid, foreignDisc), client, cts.Token);
                    // Prove the stream survived: another ping (pids 2 and 3, in order).
                    await cam.SendAsync(BcUdp.BuildData(cid, 2, pingBytes.AsSpan(0, half)), client, cts.Token);
                    await cam.SendAsync(BcUdp.BuildData(cid, 3, pingBytes.AsSpan(half)), client, cts.Token);
                    // The camera's BC-layer keepalive QUESTION (msg 234): battery
                    // firmware sends this over the data channel and recycles the
                    // session (~8 s) if the client never answers it.
                    var kaReq = Neolink.Bc.BcCodec.Serialize(
                        Neolink.Bc.BcMessage.HeaderOnly(new Neolink.Bc.BcMeta
                        {
                            MsgId = Neolink.Bc.BcConstants.MsgIdUdpKeepAlive,
                            Class = Neolink.Bc.BcConstants.ClassModern,
                            MsgNum = 777,
                        }),
                        new Neolink.Bc.EncryptionState());
                    await cam.SendAsync(BcUdp.BuildData(cid, 4, kaReq), client, cts.Token);
                    await phase3.Task;
                    var ourDisc = $"<P2P><D2C_DISC><cid>{cid}</cid><did>7</did></D2C_DISC></P2P>";
                    await cam.SendAsync(UdpDiscovery.BuildDiscovery(hsTid, ourDisc), client, cts.Token);
                }
                catch { /* cancelled at teardown */ }
            }, cts.Token);

            // 4. Drain: ack client data, and watch for the C2D_A confirm reply and
            //    the tid the client's heartbeats run under.
            while (!cts.IsCancellationRequested)
            {
                var r = await cam.ReceiveAsync(cts.Token);
                if (UdpDiscovery.TryParseDiscovery(r.Buffer, out var dtid, out var dx, out _))
                {
                    if (dx.Contains("<C2D_A>", StringComparison.Ordinal)
                        && dx.Contains("<sid>42</sid>") && dx.Contains("<conn>local</conn>")) clientC2dA++;
                    else if (dx.Contains("<C2D_HB>", StringComparison.Ordinal))
                    {
                        if (dtid == hsTid) clientHbSameTid++; else clientHbOtherTid++;
                    }
                    else if (dx.Contains("<C2D_DISC>", StringComparison.Ordinal)
                             && dx.Contains("<did>8</did>", StringComparison.Ordinal)) releasedOrphan++;
                }
                else if (BcUdp.TryParseData(r.Buffer, out _, out var pid, out var payload))
                {
                    clientDataPackets++;
                    // The keepalive answer: msg 234 echoed with our msgNum and resp 200.
                    if (payload.Length >= 20
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)) == Neolink.Bc.BcConstants.MsgIdUdpKeepAlive
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14)) == 777
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(16)) == 200)
                        kaReplies++;
                    await cam.SendAsync(BcUdp.BuildAck(cid, 0, pid, 0, ReadOnlySpan<byte>.Empty), r.RemoteEndPoint, cts.Token);
                }
                else if (BcUdp.TryParseAck(r.Buffer, out _, out _, out _, out _, out _))
                {
                    clientAckPackets++; // the continuous ack timer — the keepalive the camera needs
                }
            }
        }, cts.Token);

        await using var conn = await Neolink.Protocol.BcUdpConnection.ConnectAsync(
            "127.0.0.1", uid, TimeSpan.FromSeconds(10), cts.Token, "udptest");

        using (var sub = conn.Subscribe(Neolink.Bc.BcConstants.MsgIdPing))
        {
            var got = await sub.ReceiveAsync(TimeSpan.FromSeconds(8), cts.Token);
            AssertEq(got.Meta.MsgId, Neolink.Bc.BcConstants.MsgIdPing);
        }

        // Exercise the outbound path: send a ping; the mock must receive+ack it.
        var outPing = Neolink.Bc.BcMessage.HeaderOnly(new Neolink.Bc.BcMeta
        {
            MsgId = Neolink.Bc.BcConstants.MsgIdPing,
            Class = Neolink.Bc.BcConstants.ClassModern,
        });
        await conn.SendAsync(outPing, cts.Token);
        for (int i = 0; i < 50 && clientDataPackets == 0; i++) await Task.Delay(50, cts.Token);
        Assert(clientDataPackets > 0, "camera received the client's UDP data packet");

        // The continuous ack timer must fire on its own (the keepalive/flow-control
        // the camera needs to keep the session open) — not just when data arrives.
        for (int i = 0; i < 40 && clientAckPackets < 3; i++) await Task.Delay(50, cts.Token);
        Assert(clientAckPackets >= 3, "client sends acks continuously (keepalive), not only on receipt");

        // The client must reply C2D_A to the camera's D2C_T (session confirm) —
        // without it the real camera recycles the session every ~8 s.
        for (int i = 0; i < 40 && clientC2dA == 0; i++) await Task.Delay(50, cts.Token);
        Assert(clientC2dA > 0, "client replies C2D_A to D2C_T (confirms the session)");

        // Discovery-layer keepalives must run under the handshake tid: the camera
        // keys its session to it and treats any other tid as a stranger, re-sending
        // D2C_C_R and then closing with a clean D2C_DISC (~8 s on real hardware).
        for (int i = 0; i < 80 && clientHbSameTid == 0 && clientHbOtherTid == 0; i++)
            await Task.Delay(50, cts.Token);
        Assert(clientHbSameTid > 0, "heartbeats carry the handshake tid (session continuity)");
        AssertEq(clientHbOtherTid, 0);

        // Duplicate-session handling: the camera answers a D2C_C_R for a session
        // that is not ours (did 8) and a D2C_DISC for another session (did 9). The
        // client must release the duplicate, ignore the foreign disconnect, and the
        // stream must keep working — a duplicate's death notice killing a healthy
        // stream was the ~8 s reconnect loop on real battery cameras.
        using (var sub = conn.Subscribe(Neolink.Bc.BcConstants.MsgIdPing))
        {
            phase2.SetResult();
            var got = await sub.ReceiveAsync(TimeSpan.FromSeconds(8), cts.Token);
            AssertEq(got.Meta.MsgId, Neolink.Bc.BcConstants.MsgIdPing);
        }
        for (int i = 0; i < 40 && releasedOrphan == 0; i++) await Task.Delay(50, cts.Token);
        Assert(releasedOrphan > 0, "client releases the duplicate session with C2D_DISC (did 8)");

        // The camera's BC keepalive (msg 234) must be ANSWERED — echoed msgNum,
        // response 200 — or real battery firmware declares the client dead and
        // recycles the session with a clean D2C_DISC after ~8 s.
        for (int i = 0; i < 60 && kaReplies == 0; i++) await Task.Delay(50, cts.Token);
        Assert(kaReplies > 0, "client answers the camera's UDP keepalive (msg 234, resp 200, echoed msgNum)");

        // ...but a D2C_DISC addressed to OUR did must still close the connection,
        // promptly (well before the receive timeout).
        using (var sub = conn.Subscribe(Neolink.Bc.BcConstants.MsgIdPing))
        {
            var t0 = DateTime.UtcNow;
            phase3.SetResult();
            bool closed = false;
            try { await sub.ReceiveAsync(TimeSpan.FromSeconds(8), cts.Token); }
            catch { closed = true; }
            Assert(closed && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5),
                "a D2C_DISC for our own did closes the connection promptly");
        }
    }

    /// <summary>Wake-capture liveness probe: silent when nothing answers (asleep),
    /// true the moment a camera answers discovery (awake).</summary>
    private static async Task RunWakeProbe()
    {
        const string uid = "TESTUID0000000LO";
        // Nothing listening → probe returns false quickly.
        Assert(!await UdpDiscovery.IsReachableAsync("127.0.0.1", uid, TimeSpan.FromMilliseconds(600), CancellationToken.None),
            "liveness probe is false while the camera is asleep");

        // Bring up a responder on 2015 → probe returns true.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cam = BindLoopbackUdp(2015);
        int gotDisc = 0;
        var responder = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.Sockets.UdpReceiveResult r;
                try { r = await cam.ReceiveAsync(cts.Token); } catch { return; }
                if (!UdpDiscovery.TryParseDiscovery(r.Buffer, out _, out var xml, out _)) continue;
                if (xml.Contains("<C2D_C>", StringComparison.Ordinal))
                {
                    int cid = int.Parse(xml.Split("<cid>")[1].Split("</cid>")[0]);
                    var reply = $"<P2P><D2C_C_R><rsp>0</rsp><cid>{cid}</cid><did>3</did></D2C_C_R></P2P>";
                    try { await cam.SendAsync(UdpDiscovery.BuildDiscovery(9, reply), r.RemoteEndPoint, cts.Token); } catch { }
                }
                else if (xml.Contains("<C2D_DISC>", StringComparison.Ordinal)) gotDisc++;
            }
        }, cts.Token);

        Assert(await UdpDiscovery.IsReachableAsync("127.0.0.1", uid, TimeSpan.FromSeconds(3), cts.Token),
            "liveness probe is true the moment the camera answers");
        // The probe must release the session it just created — a C2D_DISC — or the
        // camera would keep retrying D2C_C_R for ~9 s after every 5 s poll.
        for (int i = 0; i < 40 && gotDisc == 0; i++) await Task.Delay(50);
        Assert(gotDisc > 0, "wake probe releases its session with a polite C2D_DISC");
        cts.Cancel();
        try { await responder; } catch { }
    }

    /// <summary>Bind a loopback UDP socket, retrying briefly — the two UDP tests both
    /// use port 2015 back to back, and the OS can hold it a moment after the prior
    /// test releases it, so a bare bind occasionally flakes.</summary>
    private static System.Net.Sockets.UdpClient BindLoopbackUdp(int port)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                var c = new System.Net.Sockets.UdpClient();
                c.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                c.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                return c;
            }
            catch (System.Net.Sockets.SocketException) when (i < 30)
            {
                System.Threading.Thread.Sleep(50);
            }
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
