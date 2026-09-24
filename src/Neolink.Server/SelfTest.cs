// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Neolink.Bc;
using Neolink.Config;
using Neolink.Demo;
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
                // The truncating form (FullAes media drops its padding this way)
                // yields exactly the reference's prefix, at every boundary shape.
                foreach (var keep in new[] { 0, Math.Min(1, len), len / 2, Math.Max(0, len - 1), len })
                    AssertSeq(XmlCrypto.AesCfbDecrypt(refCipher, state, keep), plain.AsSpan(0, keep).ToArray());
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

        Test("battery detection heals when the login-time query is missed", () =>
        {
            // The one login-time battery query gets 3s. A slow battery camera (Argus
            // Solar over UDP) misses it, and everything downstream then treats the
            // camera as mains: the LONG idle grace instead of the short battery one,
            // no battery reading anywhere. Two later paths prove the camera is
            // battery-powered, and either must heal the flag.
            var cfg = new Config.CameraConfig
            {
                Name = "solar",
                Host = "127.0.0.1",
                Username = "admin",
                Password = "p",
                AlwaysOn = false,
            };
            var svc = new Streaming.CameraService(cfg, Protocol.StreamKind.Main,
                new Streaming.StreamHub("solar"), TimeSpan.Zero);
            Assert(!svc.BatteryPowered, "missed login query -> starts out looking like a mains camera");

            // Path 1: the capability sweep (longer probe budget) says battery=true.
            svc.BatteryDetected();
            Assert(svc.BatteryPowered, "capability sweep proving battery -> flag latches");

            // Idempotent: a second notification must not re-announce or flap.
            svc.BatteryDetected();
            Assert(svc.BatteryPowered, "repeat notification -> still battery, no flap");
        });

        Test("always_on battery camera is treated exactly like a wired camera", () =>
        {
            // The contract: always_on: true opts a battery camera OUT of every
            // battery behavior — no parking, no wake probing, no short battery
            // idle grace or demand window, no asleep badge / click-to-view gate
            // in the UI (all keyed on SleepFriendly). Only the battery SENSORS
            // remain. Pinned here because every one of those behaviors reads
            // this flag, and a battery-side change must not regress it.
            Streaming.CameraService Make(bool? alwaysOn)
            {
                var cfg = new Config.CameraConfig
                {
                    Name = "batt",
                    Host = "127.0.0.1",
                    Username = "admin",
                    Password = "p",
                    AlwaysOn = alwaysOn,
                };
                var svc = new Streaming.CameraService(cfg, Protocol.StreamKind.Main,
                    new Streaming.StreamHub("batt"), TimeSpan.Zero);
                svc.BatteryDetected(); // it IS a battery model — the setting must win anyway
                return svc;
            }

            Assert(!Make(alwaysOn: true).SleepFriendly,
                "always_on: true + battery -> NOT sleep-friendly (wired treatment)");
            Assert(Make(alwaysOn: null).SleepFriendly,
                "always_on unset + battery -> sleep-friendly (the battery default)");
            Assert(Make(alwaysOn: false).SleepFriendly,
                "always_on: false -> sleep-friendly by explicit choice");
            Assert(!Streaming.CameraService.MediaOnDemandPolicy(true, true),
                "always_on battery camera never rides the on-demand video path either");
        });

        Test("24/7 recording is vetoed for a sleep-friendly battery camera", () =>
        {
            // The web UI disables the toggle and the API refuses the switch, but
            // the recorder's own gate is the backstop: a stale settings.json with
            // continuous=true must never hold a dozing battery camera awake — the
            // on-demand video hold asks ContinuousEnabled to decide frame demand.
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var settings = new Recording.RecordingSettings(dir);
                settings.Update("dozer", events: null, continuous: true, eventTypes: null, setEventTypes: false);
                var store = new Recording.EventStore(Path.Combine(dir, "rec"));
                bool sleepFriendly = true;
                var recorder = new Recording.ContinuousRecorder("dozer",
                    new Streaming.StreamHub("dozer"), store, settings,
                    new Config.RecordingConfig { SegmentMinutes = 10, MaxSegmentSizeMb = 256 },
                    blocked: () => sleepFriendly);
                Assert(!recorder.ContinuousEnabled,
                    "switch ON + sleep-friendly -> effectively OFF (no frame demand, no taping)");
                // What a vetoed camera gets instead: the passive wake tap (default
                // ON) — tapes frames that already flow, but must never register as
                // a consumer, or it would hold the camera awake like 24/7 would.
                Assert(recorder.WakeTapActive,
                    "sleep-friendly -> wake tap active by default (self-wakes land on the timeline)");
                Assert(!recorder.ContinuousEnabled,
                    "the tap NEVER counts as frame demand — only ContinuousEnabled feeds the hold");
                settings.Update("dozer", events: null, continuous: null, eventTypes: null,
                    setEventTypes: false, wakeTimeline: false);
                Assert(!recorder.WakeTapActive, "wake tap is a real switch — off means off");
                settings.Update("dozer", events: null, continuous: null, eventTypes: null,
                    setEventTypes: false, wakeTimeline: true);
                // The veto is live: battery status heals in after connect, and
                // always_on can change on a config reload — no restart needed.
                sleepFriendly = false;
                Assert(recorder.ContinuousEnabled,
                    "veto lifted -> the persisted switch takes effect again");
                Assert(!recorder.WakeTapActive,
                    "the tap is battery-cam-only — a wired/always_on camera uses the real 24/7 switch");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("wake-capture RTT detector: arms on the sleep sawtooth, fires on a fast run", () =>
        {
            // The samples below are a REAL ping trace of a sleeping Argus Solar
            // (2026-07-21): power-save sawtooth ramps while asleep, a dead-flat
            // 2-3 ms run when PIR wakes the main processor. The detector must read
            // patterns, not samples — the trace has mid-sleep dips (37/52/60 ms)
            // that a naive threshold would call a wake.
            var d = new Streaming.WakeRttDetector();

            // Awake camera (flat and fast) never arms — no false asleep.
            foreach (var ms in new double[] { 2, 2, 15, 3, 2, 8, 2, 2, 2, 2, 2, 2 })
                Assert(!d.OnSample(ms), "fast flat samples while awake -> no edge");
            Assert(!d.Armed, "an awake camera's flat pings never read as sleep");

            // The sleep sawtooth arms it (from the trace, verbatim).
            double[] sawtooth = { 257, 782, 282, 294, 307, 323, 338, 350, 358, 371 };
            foreach (var ms in sawtooth) d.OnSample(ms);
            Assert(d.Armed, "sustained power-save sawtooth -> armed");

            // Mid-sleep dips from the trace: 37 is fast, 52/60 are not — no run,
            // no false wake, and the detector stays armed.
            Assert(!d.OnSample(37) && !d.OnSample(52) && !d.OnSample(60),
                "isolated fast dip (37,52,60 from the trace) -> no false wake");
            Assert(d.Armed, "dips do not disarm");
            foreach (var ms in new double[] { 378, 391, 404 }) d.OnSample(ms);

            // The real wake: a flat fast run fires on the third consecutive sample.
            Assert(!d.OnSample(2), "fast 1 of 3");
            Assert(!d.OnSample(2), "fast 2 of 3");
            Assert(d.OnSample(2), "three consecutive fast answers -> the wake edge");

            // Second real capture (2026-07-21, PIR event): the sawtooth, one
            // boundary sample, then single digits the instant the event starts.
            // The detector must fire on the third fast sample — every further
            // second of confirmation is event footage lost (the whole event
            // measured ~27 s flat, sawtooth back the moment it ended).
            var ev = new Streaming.WakeRttDetector();
            foreach (var ms in new double[] { 157, 163, 177, 196, 400, 437, 123, 151, 164 })
                Assert(!ev.OnSample(ms), "pre-event sawtooth -> no edge");
            Assert(ev.Armed, "sawtooth armed it before the event");
            Assert(!ev.OnSample(6), "event begins: fast 1 of 3");
            Assert(!ev.OnSample(4), "fast 2 of 3");
            Assert(ev.OnSample(3), "fast 3 of 3 -> edge, ~3s into the event");

            // Radios that power off entirely: misses arm, first fast run fires.
            var dark = new Streaming.WakeRttDetector();
            for (int i = 0; i < Streaming.WakeRttDetector.NonFastToArm; i++)
                Assert(!dark.OnSample(null), "misses accumulate toward arming");
            Assert(dark.Armed, "sustained silence -> armed (radio-off models)");
            dark.OnSample(3); dark.OnSample(2);
            Assert(dark.OnSample(2), "radio back + fast run -> wake edge");

            // ICMP-blocked network: all misses forever — arms but can never fire
            // (the legacy transport probe is the fallback there).
            var blocked = new Streaming.WakeRttDetector();
            for (int i = 0; i < 50; i++)
                Assert(!blocked.OnSample(null), "pure misses never fire an edge");

            // Adaptive skepticism (live loop 2026-07-21): after fruitless wake
            // connects the probe loop raises ArmThreshold so an idle-awake
            // camera — whose radio power-save also pings as a sawtooth, but
            // whose housekeeping keeps producing flat runs — never arms, and
            // therefore is never re-woken by us. A flat run RESETS the arming
            // count, so the pattern must be truly uninterrupted.
            var wary = new Streaming.WakeRttDetector { ArmThreshold = 16 };
            for (int i = 0; i < 12; i++) wary.OnSample(300 + i * 40);
            Assert(!wary.Armed, "12 sawtooth samples < threshold 16 -> not armed yet");
            wary.OnSample(20); wary.OnSample(22); // idle-awake housekeeping flat
            for (int i = 0; i < 15; i++) wary.OnSample(300 + i * 40);
            Assert(!wary.Armed, "the flat run reset the count — 15 more still short of 16");
            Assert(!wary.OnSample(20) && !wary.OnSample(21) && !wary.OnSample(19),
                "fast run while UNARMED never fires — the idle-awake camera is left alone");

            // FastRun exposure (live miss 2026-07-22 06:27): the probe loop bursts
            // the confirm probes while a fast run is live and logs runs that
            // collapse before confirming — both read FastRun, so its accounting
            // is contract, not detail.
            var burst = new Streaming.WakeRttDetector();
            for (int i = 0; i < Streaming.WakeRttDetector.NonFastToArm; i++) burst.OnSample(400);
            Assert(burst.Armed && burst.FastRun == 0, "armed, no run in progress");
            Assert(!burst.OnSample(4) && burst.FastRun == 1, "fast 1 of 3 -> run length 1 (burst kicks in)");
            Assert(!burst.OnSample(5) && burst.FastRun == 2, "fast 2 of 3 -> run length 2");
            Assert(!burst.OnSample(300) && burst.FastRun == 0,
                "sawtooth back -> the run collapsed short of confirming (a blip, logged)");
            Assert(burst.Armed, "a blip does not disarm");
            burst.OnSample(3); burst.OnSample(3);
            Assert(burst.OnSample(2), "the next sustained run still fires");
        });

        Test("wake diagnostics: verdict separates our-probe wakes from real ones", () =>
        {
            const double timeoutMs = 2000;
            var armed = DateTime.UtcNow;

            // Mirrors the probe loop exactly: probes are fed BEFORE arming is
            // recorded, so the two misses that arm the edge are not counted
            // against it — only probes after "asleep" was declared are.
            static Streaming.WakeDiag Park(DateTime armedAt, int missesAfterArming, double answerMs)
            {
                var d = new Streaming.WakeDiag { ParkedAt = armedAt.AddSeconds(-90) };
                d.OnProbe(false, 2000);           // miss 1 of 2 (pre-arm)
                d.OnProbe(false, 2000);           // miss 2 of 2 -> the loop arms here
                d.ArmedAt = armedAt;
                for (int i = 0; i < missesAfterArming; i++) d.OnProbe(false, 2000);
                d.OnProbe(true, answerMs);        // the answer that fires the edge
                d.EdgeAt = armedAt.AddSeconds(5 * (missesAfterArming + 1));
                return d;
            }

            // Our probe woke it: answered the FIRST probe after arming, nothing
            // detected afterwards. This is the issue #44 signature.
            var ours = Park(armed, missesAfterArming: 0, answerMs: 40);
            ours.SleepStatusOnArrival = true;
            Assert(ours.ProbesSinceArmed == 1, "only post-arming probes count toward the edge");
            Assert(ours.Verdict(timeoutMs).StartsWith("LIKELY OUR PROBE", StringComparison.Ordinal),
                "first probe after arming answers, no detection -> our probe woke it");

            // A detection during the session is what wake-capture exists to catch.
            var real = Park(armed, missesAfterArming: 40, answerMs: 35);
            real.SawDetection = true;
            Assert(real.Verdict(timeoutMs).StartsWith("REAL self-wake", StringComparison.Ordinal),
                "wake far from arming with a detection -> a real self-wake");

            // Regression (live catch 2026-07-21): a REAL wake's edge probe answers
            // SLOWLY — the SoC replies while still booting (4.9 s measured against a
            // 6 s timeout). The detection must outrank the latency heuristic, or a
            // successful catch gets reported as SUSPECT FALSE ASLEEP.
            var slowReal = Park(armed, missesAfterArming: 1, answerMs: timeoutMs * 0.9);
            slowReal.SawDetection = true;
            slowReal.SleepStatusOnArrival = false; // "already up" — it woke before we connected
            Assert(slowReal.Verdict(timeoutMs).StartsWith("REAL self-wake", StringComparison.Ordinal),
                "slow edge answer + detection -> still a REAL self-wake");

            // Answers crowding the timeout mean the "asleep" reading itself is suspect.
            var slow = Park(armed, missesAfterArming: 10, answerMs: 1700);
            slow.SleepStatusOnArrival = false;
            Assert(slow.Verdict(timeoutMs).StartsWith("SUSPECT FALSE ASLEEP", StringComparison.Ordinal),
                "answers near the timeout -> the unanswered probes may be slow answers, not sleep");

            // The camera saying "I was awake" must veto the our-probe verdict even
            // on a first-probe answer — it cannot have been woken if it never slept.
            var awake = Park(armed, missesAfterArming: 0, answerMs: 30);
            awake.SleepStatusOnArrival = false;
            Assert(!awake.Verdict(timeoutMs).StartsWith("LIKELY OUR PROBE", StringComparison.Ordinal),
                "camera reports it was awake -> not blamed on our probe");

            // Hint-triggered wakes (router saw the camera call the push service):
            // a detection makes it REAL; none makes it a rule problem (MISFIRE),
            // never LIKELY OUR PROBE — we sent nothing.
            var hinted = Park(armed, missesAfterArming: 0, answerMs: 30);
            hinted.HintSource = "router saw 10.1.0.13 -> 104.18.25.250:443 (pass)";
            hinted.SawDetection = true;
            Assert(hinted.Verdict(timeoutMs).StartsWith("REAL self-wake (router wake hint)", StringComparison.Ordinal),
                "hint + detection -> REAL self-wake credited to the hint");
            var misfire = Park(armed, missesAfterArming: 0, answerMs: 30);
            misfire.HintSource = "router saw 10.1.0.13 -> 34.205.159.164:443 (pass)";
            Assert(misfire.Verdict(timeoutMs).StartsWith("HINT MISFIRE", StringComparison.Ordinal),
                "hint with no detection -> misfire pointing at the firewall rule");
        });

        Test("wake hints: filterlog parsing keeps push calls, drops the p2p noise", () =>
        {
            // Real shapes from the live OPNsense capture (2026-07-22): on PIR the
            // camera opens ONE TCP/443 state to pushx.reolink.com; ~30-45 s later
            // the wake chip bursts NEW UDP states to the p2p host as the camera
            // dozes off. Only the former may hint, or every park re-connects.
            const string rfc3164 =
                "<134>Jul 22 19:01:45 OPNsense filterlog: 82,,,02f4bab0,igc0,match,pass,in,4," +
                "0x0,,63,25182,0,DF,6,tcp,60,10.1.0.13,104.18.25.250,64082,443,0,S,1234,,64240,,mss";
            Assert(Streaming.WakeHintListener.TryParseEventHint(rfc3164, out var src, out var detail),
                "RFC3164 filterlog TCP/443 line -> hint");
            Assert(src.ToString() == "10.1.0.13", "hint carries the camera source IP");
            Assert(detail.Contains("104.18.25.250"), "hint detail names the destination");

            const string rfc5424 =
                "<134>1 2026-07-22T19:01:45-06:00 OPNsense.local filterlog 21674 - [meta seq=\"1\"] " +
                "82,,,02f4bab0,igc0,match,block,in,4,0x0,,63,25182,0,DF,6,tcp,60," +
                "10.1.0.13,104.18.25.250,64083,443,0,S,1235,,64240,,mss";
            Assert(Streaming.WakeHintListener.TryParseEventHint(rfc5424, out src, out _),
                "RFC5424 framing parses too, and a BLOCK rule still hints (the attempt is the signal)");
            Assert(src.ToString() == "10.1.0.13", "RFC5424: source IP extracted");

            // The p2p re-registration burst: UDP, must NOT hint.
            const string p2pNoise =
                "<134>Jul 22 19:01:50 OPNsense filterlog: 82,,,02f4bab0,igc0,match,pass,in,4," +
                "0x0,,63,25183,0,none,17,udp,44,10.1.0.13,34.205.159.164,52409,57850,24";
            Assert(!Streaming.WakeHintListener.TryParseEventHint(p2pNoise, out _, out _),
                "UDP p2p keep-alive burst -> no hint (it trails every park)");

            // TCP to a non-443 port (some other device chatter): no hint.
            const string otherTcp =
                "<134>Jul 22 19:02:12 OPNsense filterlog: 82,,,02f4bab0,igc0,match,pass,in,4," +
                "0x0,,63,25184,0,DF,6,tcp,60,10.1.0.132,157.90.0.173,58020,8080,0,S,1,,64240,,mss";
            Assert(!Streaming.WakeHintListener.TryParseEventHint(otherTcp, out _, out _),
                "TCP to a non-443 port -> no hint");

            // Garbage a syslog port inevitably receives.
            Assert(!Streaming.WakeHintListener.TryParseEventHint("<13>random syslog chatter", out _, out _),
                "non-filterlog syslog -> ignored");
            Assert(!Streaming.WakeHintListener.TryParseEventHint("", out _, out _), "empty -> ignored");
            Assert(!Streaming.WakeHintListener.TryParseEventHint(
                "<134>Jul 22 19:01:45 OPNsense filterlog: 82,,,short", out _, out _),
                "truncated filterlog CSV -> ignored");
        });

        Test("wake hints: push decoy throttle + push_ports config parse", () =>
        {
            // The decoy push service (DNS-override mode): a camera whose push
            // delivery dies at the TLS handshake retries within seconds, so one
            // PIR event is a burst of connections — one hint per source per 10 s.
            var last = new Dictionary<System.Net.IPAddress, DateTime>();
            var cam = System.Net.IPAddress.Parse("10.1.0.13");
            var other = System.Net.IPAddress.Parse("10.1.0.14");
            var t0 = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
            Assert(Streaming.PushSinkListener.ShouldHint(last, cam, t0), "first connection hints");
            Assert(!Streaming.PushSinkListener.ShouldHint(last, cam, t0.AddSeconds(3)),
                "retry 3 s later (TLS-failure retry) -> suppressed");
            Assert(Streaming.PushSinkListener.ShouldHint(last, other, t0.AddSeconds(3)),
                "a different camera is throttled independently");
            Assert(!Streaming.PushSinkListener.ShouldHint(last, cam, t0.AddSeconds(9.9)),
                "still inside the 10 s window -> suppressed");
            Assert(Streaming.PushSinkListener.ShouldHint(last, cam, t0.AddSeconds(11)),
                "past the window (a genuinely later push) -> hints again");

            // The settings-UI port-list parser: display text in, strict ports out.
            AssertEq(string.Join(",", Config.ConfigEditor.ParsePortList("443, 53")), "443,53");
            AssertEq(string.Join(",", Config.ConfigEditor.ParsePortList(" 443;53 443 ")), "443,53"); // dupes collapse
            AssertEq(Config.ConfigEditor.ParsePortList("").Count, 0);
            bool rejected = false;
            try { Config.ConfigEditor.ParsePortList("443, http"); } catch (FormatException) { rejected = true; }
            Assert(rejected, "junk in the port list -> FormatException");
            rejected = false;
            try { Config.ConfigEditor.ParsePortList("70000"); } catch (FormatException) { rejected = true; }
            Assert(rejected, "out-of-range port -> FormatException");

            // The loader validates wake_hints now — a bad saved config fails at
            // load (and therefore at ConfigEditor.Apply), not at listener start.
            var tmpBad = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmpBad, """
                { "wake_hints": { "push_ports": [443, 99999] }, "cameras": [] }
                """);
            rejected = false;
            try { NeolinkConfig.Load(tmpBad); } catch (FormatException) { rejected = true; }
            Assert(rejected, "push_ports out of range -> config rejected");
            File.WriteAllText(tmpBad, """
                { "wake_hints": { "bind": "not-an-ip" }, "cameras": [] }
                """);
            rejected = false;
            try { NeolinkConfig.Load(tmpBad); } catch (FormatException) { rejected = true; }
            Assert(rejected, "wake_hints.bind must be an IP -> config rejected");
            File.Delete(tmpBad);

            // push_ports parses from both config dialects, list or single spelling.
            var tmpJson = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            var tmpToml = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.toml");
            File.WriteAllText(tmpJson, """
                { "wake_hints": { "syslog_port": 0, "push_ports": [443, 53] },
                  "cameras": [ { "name": "c", "username": "u", "password": "p", "address": "1.2.3.4" } ] }
                """);
            File.WriteAllText(tmpToml, """
                [wake_hints]
                push_port = 443
                [[cameras]]
                name = "c"
                username = "u"
                password = "p"
                address = "1.2.3.4"
                """);
            try
            {
                var wh = NeolinkConfig.Load(tmpJson).WakeHints!;
                AssertEq(wh.SyslogPort, 0); // syslog half off, decoy-only setup
                AssertEq(string.Join(",", wh.PushPorts), "443,53");
                var whToml = NeolinkConfig.Load(tmpToml).WakeHints!;
                AssertEq(whToml.SyslogPort, 5140); // untouched default
                AssertEq(string.Join(",", whToml.PushPorts), "443");
            }
            finally { File.Delete(tmpJson); File.Delete(tmpToml); }
        });

        Test("wake hints: trust window defaults, JSON/TOML parsing and expiry", () =>
        {
            foreach (var ext in new[] { "json", "toml" })
            {
                var path = Path.Combine(Path.GetTempPath(), $"neolink-trust-{Guid.NewGuid():N}.{ext}");
                NeolinkConfig Load(string? value, bool section = true)
                {
                    File.WriteAllText(path, ext == "json"
                        ? section ? "{\"wake_hints\":{" + (value == null ? "" : "\"trust_hours\":" + value) + "}}" : "{}"
                        : section ? "[wake_hints]\n" + (value == null ? "" : "trust_hours = " + value) : "");
                    return NeolinkConfig.Load(path);
                }
                try
                {
                    Assert(Load(null, section: false).WakeHints == null, "absent section stays disabled");
                    AssertEq(Load(null).WakeHints!.TrustHours, 2d);
                    foreach (var hours in new[] { 2d, 72d, 0.5d })
                    {
                        var config = Load(hours.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        AssertEq(config.WakeHints!.TrustHours, hours);
                        var service = new Streaming.CameraService(new CameraConfig { Name = "trust", Username = "admin" },
                            Protocol.StreamKind.Main, new Streaming.StreamHub("trust"), TimeSpan.Zero,
                            TimeSpan.FromHours(config.WakeHints.TrustHours));
                        long hint = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
                        long expiry = hint + TimeSpan.FromHours(hours).Ticks;
                        Assert(!service.IsHintTrusted(0, hint), "no hint after startup -> scan-only");
                        Assert(service.IsHintTrusted(hint, expiry - 1), "trusted until the configured boundary");
                        Assert(!service.IsHintTrusted(hint, expiry), "expires exactly at the boundary");
                        Assert(!service.IsHintTrusted(hint, expiry + 1), "expired -> scan-only fallback");
                        Assert(service.IsHintTrusted(expiry, expiry + 1), "a new hint refreshes trust");
                        AssertEq(service.IsHintTrusted(hint, hint + TimeSpan.FromHours(3).Ticks), hours == 72);
                    }
                    foreach (var value in new[] { "0", "-1", "1e100", "1e-100", "\"72\"", "true" })
                    {
                        bool rejected = false;
                        try { Load(value); } catch (FormatException) { rejected = true; }
                        Assert(rejected, $"{ext}: invalid trust_hours {value} rejected");
                    }
                    if (ext == "toml")
                        foreach (var value in new[] { "nan", "inf", "-inf" })
                        {
                            bool rejected = false;
                            try { Load(value); } catch (FormatException) { rejected = true; }
                            Assert(rejected, $"non-finite trust_hours {value} rejected");
                        }
                }
                finally { File.Delete(path); }
            }
            var defaultService = new Streaming.CameraService(new CameraConfig { Name = "default", Username = "admin" },
                Protocol.StreamKind.Main, new Streaming.StreamHub("default"), TimeSpan.Zero);
            long t0 = DateTime.UtcNow.Ticks;
            Assert(defaultService.IsHintTrusted(t0, t0 + TimeSpan.FromMinutes(119).Ticks), "default trusts for 2 h");
            Assert(!defaultService.IsHintTrusted(t0, t0 + TimeSpan.FromHours(2).Ticks), "default expires at 2 h");
        });

        Test("wake hints: a section left with only trust_hours counts as empty; log spans read in hours", () =>
        {
            System.Text.Json.Nodes.JsonObject Obj(string json) =>
                System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            // Loaded alone, such a section would switch syslog on at 5140.
            Assert(ConfigEditor.OnlyTrustHoursLeft(Obj("{}")), "empty section");
            Assert(ConfigEditor.OnlyTrustHoursLeft(Obj("{\"trust_hours\":72}")), "trust_hours alone");
            Assert(ConfigEditor.OnlyTrustHoursLeft(Obj("{\"trustHours\":72}")), "any spelling");
            Assert(!ConfigEditor.OnlyTrustHoursLeft(Obj("{\"trust_hours\":72,\"syslog_port\":0}")),
                "API-only hints keep the section");
            Assert(!ConfigEditor.OnlyTrustHoursLeft(Obj("{\"trust_hours\":72,\"push_ports\":[8443]}")), "push ports");
            Assert(!ConfigEditor.OnlyTrustHoursLeft(Obj("{\"bind\":\"10.0.0.2\"}")), "bind alone is the syslog default");

            // The settings page's save path (values arrive as the text typed).
            bool Refused(Action edit)
            {
                try { edit(); return false; } catch (FormatException) { return true; }
            }
            var root = Obj("{\"wake_hints\":{\"push_ports\":[8443]}}");
            ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "72");
            AssertEq(root["wake_hints"]!["trust_hours"]!.GetValue<double>(), 72d);
            ConfigEditor.ApplyWakeHintEdit(root, null, "", null, null);
            Assert(root["wake_hints"] == null, "clearing the last hint source drops the trust window with it");

            root = Obj("{\"wake_hints\":{\"push_ports\":[8443],\"trust_hours\":72}}");
            ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "");
            Assert(root["wake_hints"]!["trust_hours"] == null, "a blank window removes the key (default applies)");
            AssertEq(root["wake_hints"]!["push_ports"]!.AsArray().Count, 1);

            root = Obj("{\"wake_hints\":{\"push_ports\":[8443],\"trust_hours\":72}}");
            ConfigEditor.ApplyWakeHintEdit(root, null, "", null, "");
            Assert(root["wake_hints"] == null, "clearing sources and window together drops the section, no refusal");

            root = Obj("{\"cameras\":[]}");
            Assert(Refused(() => ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "24")),
                "a trust window with no hint source is refused");
            root = Obj("{\"wake_hints\":{\"syslog_port\":0}}");
            Assert(Refused(() => ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "72h")),
                "a non-number window is refused");
            Assert(Refused(() => ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "NaN"))
                   && Refused(() => ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "Infinity")),
                "NaN and Infinity are refused, not written into a file JSON cannot hold them in");

            root = Obj("{\"cameras\":[]}");
            ConfigEditor.ApplyWakeHintEdit(root, 0, null, null, " 1.5 ");
            AssertEq(root["wake_hints"]!["syslog_port"]!.GetValue<int>(), 0);
            AssertEq(root["wake_hints"]!["trust_hours"]!.GetValue<double>(), 1.5d);

            root = Obj("{\"wake_hints\":{\"syslog_port\":5140}}");
            ConfigEditor.ApplyWakeHintEdit(root, null, null, null, null);
            Assert(root["wake_hints"]!["trust_hours"] == null, "an edit with no wake fields leaves the section alone");

            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-trust-edit-{Guid.NewGuid():N}.json");
            try
            {
                // The candidate still goes through the loader, which owns the range checks.
                root = Obj("{\"wake_hints\":{\"syslog_port\":0}}");
                ConfigEditor.ApplyWakeHintEdit(root, null, null, null, "0");
                File.WriteAllText(tmp, root.ToJsonString());
                Assert(Refused(() => NeolinkConfig.Load(tmp)), "trust_hours 0 from the settings page is rejected at validation");

                // What the settings page reads back: the default shows as blank.
                string? Described(string wakeHints)
                {
                    File.WriteAllText(tmp, "{\"wake_hints\":" + wakeHints + "}");
                    var json = System.Text.Json.JsonSerializer.SerializeToNode(ConfigEditor.Describe(tmp))!;
                    return json["settings"]!["wakeHints"]!["trustHours"]?.ToJsonString();
                }
                AssertEq(Described("{\"syslog_port\":0,\"trust_hours\":72}"), "72");
                AssertEq(Described("{\"syslog_port\":0,\"trust_hours\":0.5}"), "0.5");
                AssertEq(Described("{\"syslog_port\":0}"), null);
                AssertEq(Described("{\"syslog_port\":0,\"trust_hours\":2}"), null);
            }
            finally { File.Delete(tmp); }
            AssertEq(Streaming.CameraService.Span(TimeSpan.FromMinutes(42)), "42 min");
            AssertEq(Streaming.CameraService.Span(TimeSpan.FromMinutes(119)), "119 min");
            AssertEq(Streaming.CameraService.Span(TimeSpan.FromHours(2)), "2 h");
            AssertEq(Streaming.CameraService.Span(TimeSpan.FromHours(72)), "72 h");
            AssertEq(Streaming.CameraService.Span(TimeSpan.FromHours(36.5)), "36.5 h");
        });

        Test("timeline bookmarks: store round-trip, validation, corrupt-file survival", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-bm-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Recording.BookmarkStore(dir);
                var b = store.Add("2026-07-23", 36000, 36480, "  My son's first walk  ");
                AssertEq(b.Name, "My son's first walk"); // trimmed
                AssertEq(b.Date, "2026-07-23");
                Assert(store.List().Count == 1, "one bookmark after add");

                // Round-trip: a fresh store instance reads the same file back.
                var reread = new Recording.BookmarkStore(dir);
                AssertEq(reread.List().Count, 1);
                AssertEq(reread.List()[0].Name, "My son's first walk");
                AssertEq(reread.List()[0].From, 36000d);
                AssertEq(reread.List()[0].To, 36480d);

                // Sort: newest day first, then start time within the day.
                store.Add("2026-07-24", 100, 200, "later day");
                store.Add("2026-07-23", 100, 200, "same day, earlier");
                var list = store.List();
                AssertEq(list[0].Name, "later day");
                AssertEq(list[1].Name, "same day, earlier");

                // Remove: by id, unknown id is a clean no.
                Assert(store.Remove(b.Id), "existing id removes");
                Assert(!store.Remove(b.Id), "second remove -> false");
                Assert(!store.Remove("nope"), "unknown id -> false");

                // Validation: each bad input names its problem as a FormatException.
                void Rejects(string what, Action a)
                {
                    try { a(); } catch (FormatException) { return; }
                    throw new Exception($"{what}: accepted, should have been rejected");
                }
                Rejects("bad date", () => store.Add("23/07/2026", 0, 10, "x"));
                Rejects("from >= to", () => store.Add("2026-07-23", 50, 50, "x"));
                Rejects("negative from", () => store.Add("2026-07-23", -1, 10, "x"));
                Rejects("past midnight", () => store.Add("2026-07-23", 0, 90000, "x"));
                Rejects("NaN", () => store.Add("2026-07-23", double.NaN, 10, "x"));
                Rejects("empty name", () => store.Add("2026-07-23", 0, 10, "   "));
                Rejects("name too long", () => store.Add("2026-07-23", 0, 10, new string('x', 200)));

                // A corrupt file is set aside, never silently clobbered.
                File.WriteAllText(Path.Combine(dir, "bookmarks.json"), "{ not json");
                var survived = new Recording.BookmarkStore(dir);
                AssertEq(survived.List().Count, 0);
                Assert(File.Exists(Path.Combine(dir, "bookmarks.json.corrupt")),
                    "corrupt file set aside as .corrupt");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
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
                      "keep_alive_hours": 3,
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
                Assert(cfg.Cameras[1].KeepAliveHours == 3 && cfg.Cameras[0].KeepAliveHours == 0,
                    "keep_alive_hours parsed (default 0)");
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

        Test("bc codec: a reply whose payload offset outruns its body stays framed", () =>
        {
            // Reolink IPC_36S8M: the battery query comes back as the request header
            // echoed verbatim with body_len=0. Nothing follows it on the wire, so the
            // next message has to parse off the same connection.
            var enc = new EncryptionState();
            var request = BcCodec.Serialize(new BcMessage
            {
                Meta = new BcMeta
                {
                    MsgId = BcConstants.MsgIdBatteryInfo,
                    MsgNum = 2,
                    Class = BcConstants.ClassModern,
                },
                Extension = new Bc.Xml.ExtensionXml { ChannelId = 0 },
            }, enc);
            AssertEq(request.Length, 123); // 24-byte header + 99-byte <Extension>

            var echo = request[..24];
            BinaryPrimitives.WriteUInt32LittleEndian(echo.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(echo.AsSpan(16), 200);
            AssertEq(BinaryPrimitives.ReadUInt32LittleEndian(echo.AsSpan(20)), 99u);

            var ping = BcCodec.Serialize(BcMessage.HeaderOnly(new BcMeta
            {
                MsgId = BcConstants.MsgIdPing,
                Class = BcConstants.ClassModern,
            }), enc);
            byte[] wireBytes = [.. echo, .. ping];

            using var wire = new MemoryStream(wireBytes);
            var ctx = NewContext();
            var reply = BcCodec.ReadMessageAsync(wire, ctx, CancellationToken.None).GetAwaiter().GetResult();
            AssertEq(reply.Meta.MsgId, BcConstants.MsgIdBatteryInfo);
            Assert(reply.IsEmptyModern, "an echoed header carries no BatteryInfo to parse");
            var next = BcCodec.ReadMessageAsync(wire, ctx, CancellationToken.None).GetAwaiter().GetResult();
            AssertEq(next.Meta.MsgId, BcConstants.MsgIdPing);
        });

        Test("bc codec: the reversed magic heads a snapshot reply, not a desync", () =>
        {
            // The reporter's own 20 header bytes (Reolink IPC_36S8M): msg 109 under
            // MAGIC_HEADER_REV, carrying a 1080p JPEG. Every field after the magic
            // is laid out as usual, so the whole message has to be consumed.
            var head = Convert.FromHexString("A0CBED0F6D000000DE94000000000300C8000000");
            AssertEq(BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0)), BcConstants.MagicHeaderRev);
            uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
            AssertEq(bodyLen, 38110u);

            var body = new byte[4 + (int)bodyLen]; // payload offset 0, then the JPEG
            body[4] = 0xFF;
            body[5] = 0xD8;
            using var wire = new MemoryStream([.. head, .. body]);
            var snap = BcCodec.ReadMessageAsync(wire, new BcContext(new EncryptionState()),
                CancellationToken.None).GetAwaiter().GetResult();
            AssertEq(snap.Meta.MsgId, BcConstants.MsgIdSnap);
            AssertEq(snap.Meta.ResponseCode, (ushort)200);
            AssertEq(snap.Binary?.Length ?? -1, (int)bodyLen);
            AssertEq(wire.Position, wire.Length);
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

        Test("one unusable camera entry is dropped, never fatal to the rest", () =>
        {
            // The Home Assistant add-on merges its options INTO config.json and only
            // ever adds, so it can write an entry its own Options page cannot take
            // back ("udp": true with no uid). That used to fail the whole load and
            // stop every camera, recoverable only by hand-editing config.json.
            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            var toml = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.toml");
            try
            {
                File.WriteAllText(tmp, """
                    { "users": [ { "name": "u", "pass": "p" } ],
                      "cameras": [
                        { "name": "good",    "username": "u", "password": "p", "address": "1.2.3.4" },
                        { "name": "nouid",   "username": "u", "password": "p", "address": "1.2.3.5", "udp": true },
                        { "name": "nowhere", "username": "u", "password": "p" },
                        { "name": "noname_missing_username", "address": "1.2.3.6" },
                        { "name": "badstream", "username": "u", "password": "p", "address": "1.2.3.7", "stream": "nope" },
                        { "name": "GOOD",    "username": "u", "password": "p", "address": "1.2.3.8" },
                        { "name": "ghost",   "username": "u", "password": "p", "address": "1.2.3.9",
                          "permitted_users": [ "deleted-user" ] },
                        { "name": "last",    "username": "u", "password": "p", "address": "1.2.3.10" }
                      ] }
                    """);
                var cfg = NeolinkConfig.Load(tmp);
                AssertEq(string.Join(",", cfg.Cameras.Select(c => c.Name)), "good,last");

                // A camera whose permitted_users is unusable is dropped WHOLE: an
                // empty permitted_users means "anyone", so pruning the list would
                // widen access instead of removing it.
                Assert(cfg.Cameras.All(c => c.Name != "ghost"), "undefined permitted_users drops the camera");

                // TOML takes the same path.
                File.WriteAllText(toml, """
                    [[cameras]]
                    name = "nouid"
                    username = "u"
                    password = "p"
                    address = "1.2.3.5"
                    udp = true

                    [[cameras]]
                    name = "good"
                    username = "u"
                    password = "p"
                    address = "1.2.3.4"
                    """);
                AssertEq(string.Join(",", NeolinkConfig.Load(toml).Cameras.Select(c => c.Name)), "good");

                // Saving is the other half of the contract: the editor must refuse
                // what boot would skip, or the user's camera saves and then vanishes.
                File.WriteAllText(tmp, """
                    { "cameras": [ { "name": "nouid", "username": "u", "password": "p",
                                     "address": "1.2.3.5", "udp": true } ] }
                    """);
                bool rejected = false;
                try { NeolinkConfig.Load(tmp, strict: true); } catch (FormatException) { rejected = true; }
                Assert(rejected, "strict mode still refuses the entry the boot loader skips");

                // ...but a file that ALREADY holds an unusable entry must not freeze
                // the editor: every save would fail, including the one that removes it.
                File.WriteAllText(tmp, """
                    { "web_port": 8655, "cameras": [
                        { "name": "nouid", "username": "u", "password": "p", "address": "1.2.3.5", "udp": true },
                        { "name": "good",  "username": "u", "password": "p", "address": "1.2.3.4" } ] }
                    """);
                Config.ConfigEditor.Apply(tmp, r => r["web_port"] = 8656);
                AssertEq(NeolinkConfig.Load(tmp).WebPort, 8656);
                Config.ConfigEditor.Apply(tmp, r =>
                {
                    var cams = Config.ConfigEditor.Cameras(r);
                    cams.Remove(Config.ConfigEditor.FindCamera(cams, "nouid")!);
                });
                AssertEq(string.Join(",", NeolinkConfig.Load(tmp).Cameras.Select(c => c.Name)), "good");

                // Server-wide settings stay fatal: nothing can run on a bad port.
                File.WriteAllText(tmp, """{ "web_port": 70000, "cameras": [] }""");
                rejected = false;
                try { NeolinkConfig.Load(tmp); } catch (FormatException) { rejected = true; }
                Assert(rejected, "a bad server-wide setting is still fatal");
            }
            finally { File.Delete(tmp); File.Delete(toml); }
        });

        Test("login upgrade framing: the default is unchanged, the overrides are reachable", () =>
        {
            var enc = new EncryptionState();
            byte[] Wire(BcLoginMode m) =>
                BcCodec.Serialize(BcCamera.BuildLoginUpgrade(0, 0, "admin", null, m), enc);

            // THE regression guard for this whole option: every camera that works
            // today logs in with this message, so it must stay byte-for-byte what it
            // was before the override existed — 20-byte header, class 0x6514,
            // response 0xdc12, no body.
            var def = Wire(BcLoginMode.Default);
            AssertEq(def.Length, 20);
            AssertEq(Convert.ToHexString(def),
                "F0DEBC0A" + "01000000" + "00000000" + "00" + "00" + "0000" + "12DC" + "1465");
            Assert(BcLoginMode.Default.IsDefault, "the default mode reports itself as default");
            Assert(BcLoginMode.From(null, false).IsDefault, "an unset config is the default mode");

            // Each override changes exactly the two bytes it should.
            AssertEq(Convert.ToHexString(Wire(BcLoginMode.From("none", false)).AsSpan(16, 2).ToArray()), "00DC");
            AssertEq(Convert.ToHexString(Wire(BcLoginMode.From("bcencrypt", false)).AsSpan(16, 2).ToArray()), "01DC");
            AssertEq(Convert.ToHexString(Wire(BcLoginMode.From("aes", false)).AsSpan(16, 2).ToArray()), "02DC");
            AssertEq(Convert.ToHexString(Wire(BcLoginMode.From("fullaes", false)).AsSpan(16, 2).ToArray()), "12DC");

            // The legacy framing carries the 1836-byte MD5 credential body that the
            // original Rust neolink sends, and a null password is 32 NULs.
            var legacy = Wire(BcLoginMode.From("aes", true));
            AssertEq(legacy.Length, 20 + 1836);
            AssertEq(Encoding.ASCII.GetString(legacy, 20, 32), Md5Utils.Md5String31("admin", zeroLast: true));
            AssertEq(Encoding.ASCII.GetString(legacy, 52, 32), BcConstants.EmptyLegacyPassword);
            var withPass = BcCodec.Serialize(
                BcCamera.BuildLoginUpgrade(0, 0, "admin", "hunter2", BcLoginMode.From("bcencrypt", true)), enc);
            AssertEq(Encoding.ASCII.GetString(withPass, 52, 32), Md5Utils.Md5String31("hunter2", zeroLast: true));

            // Names in, codes out; anything else is null so the loader can name it.
            AssertEq(BcConstants.ParseMaxEncryption("  AES  ")!.Value, (ushort)0xdc02);
            Assert(BcConstants.ParseMaxEncryption("bogus") == null, "an unknown name does not resolve");
            foreach (var n in BcConstants.MaxEncryptionNames)
                Assert(BcConstants.ParseMaxEncryption(n) != null, $"advertised name \"{n}\" resolves");

            // Config round-trip, both dialects, plus rejection of a typo.
            var tmp = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.json");
            var toml = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}.toml");
            try
            {
                File.WriteAllText(tmp, """
                    { "cameras": [ { "name": "d5k", "username": "u", "password": "p", "address": "1.2.3.4",
                                     "max_encryption": "bcencrypt", "legacy_login": true } ] }
                    """);
                var cam = NeolinkConfig.Load(tmp).Cameras[0];
                AssertEq(cam.MaxEncryption!, "bcencrypt");
                Assert(cam.LegacyLogin, "legacy_login parses from JSON");
                AssertEq(BcLoginMode.From(cam.MaxEncryption, cam.LegacyLogin).UpgradeCode, (ushort)0xdc01);

                File.WriteAllText(toml, """
                    [[cameras]]
                    name = "d5k"
                    username = "u"
                    password = "p"
                    address = "1.2.3.4"
                    max_encryption = "none"
                    legacy_login = true
                    """);
                var tcam = NeolinkConfig.Load(toml).Cameras[0];
                AssertEq(tcam.MaxEncryption!, "none");
                Assert(tcam.LegacyLogin, "legacy_login parses from TOML");

                // A camera with neither option set keeps null/false, so From() gives the default.
                File.WriteAllText(tmp, """
                    { "cameras": [ { "name": "plain", "username": "u", "password": "p", "address": "1.2.3.4" } ] }
                    """);
                var plain = NeolinkConfig.Load(tmp).Cameras[0];
                Assert(plain.MaxEncryption == null && !plain.LegacyLogin, "unset options stay unset");
                Assert(BcLoginMode.From(plain.MaxEncryption, plain.LegacyLogin).IsDefault,
                    "a camera with no override logs in exactly as before");

                // A typo is refused when saved and skipped at boot, like any other
                // unusable entry — never silently ignored, which would leave the
                // camera on the framing the user is trying to move it off.
                File.WriteAllText(tmp, """
                    { "cameras": [ { "name": "typo", "username": "u", "password": "p", "address": "1.2.3.4",
                                     "max_encryption": "aes256" } ] }
                    """);
                bool rejected = false;
                try { NeolinkConfig.Load(tmp, strict: true); }
                catch (FormatException ex) { rejected = ex.Message.Contains("max_encryption"); }
                Assert(rejected, "an unknown max_encryption is refused by name when saved");
                AssertEq(NeolinkConfig.Load(tmp).Cameras.Count, 0);
            }
            finally { File.Delete(tmp); File.Delete(toml); }
        });

        Test("h264 annex-b NAL splitting", () =>
        {
            var stream = new byte[] { 0, 0, 0, 1, 0x67, 1, 2, 3, 0, 0, 1, 0x68, 9, 8, 0, 0, 0, 1, 0x65, 5, 5, 5 };
            var nals = H26x.SplitNals(stream);
            AssertEq(nals.Count, 3);
            AssertEq(H26x.H264NalType(nals[0].Span), H26x.H264Sps);
            AssertEq(H26x.H264NalType(nals[1].Span), H26x.H264Pps);
            AssertEq(H26x.H264NalType(nals[2].Span), H26x.H264Idr);

            // A 3-byte code at offset 0, then a run of zeros ahead of a 4-byte one:
            // the start code claims the zero immediately before it, the rest are
            // trailing padding on the NAL that ends there.
            var padded = new byte[] { 0, 0, 1, 0x67, 0xAA, 0, 0, 0, 0, 1, 0x68, 0xBB };
            var pnals = H26x.SplitNals(padded);
            AssertEq(pnals.Count, 2);
            AssertEq(H26x.H264NalType(pnals[0].Span), H26x.H264Sps);
            AssertEq(pnals[0].Length, 2); // 67 AA — the padding zero is trimmed
            AssertEq(H26x.H264NalType(pnals[1].Span), H26x.H264Pps);
            AssertEq(pnals[1].Length, 2); // 68 BB
            AssertEq(H26x.SplitNals(new byte[] { 0, 0 }).Count, 0); // too short to hold a start code
        });

        Test("demo access-unit grouping (AUD split, SPS/PPS repeated onto bare keyframes)", () =>
        {
            static byte[] Nal(params byte[] body)
            {
                var b = new byte[4 + body.Length];
                b[3] = 1;
                body.CopyTo(b, 4);
                return b;
            }
            static byte[] Cat(params byte[][] parts)
            {
                var all = new byte[parts.Sum(p => p.Length)];
                int at = 0;
                foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
                return all;
            }
            var aud = Nal(0x09, 0x10);
            var sps = Nal(0x67, 1, 2, 3);
            var pps = Nal(0x68, 9);
            var idr = Nal(0x65, 5, 5);
            var p = Nal(0x41, 7);

            // The encoder's own shape (aud=1, repeat-headers=1): AUD-delimited, the
            // keyframe already carries its parameter sets — and zerolatency x264
            // slices every frame across its threads, so AUs hold SEVERAL slice
            // NALs. Only the AUD may split them (splitting on slices shipped
            // frames in eleven pieces: 34 corrupt frames per 450 decoded).
            var withAud = DemoRig.ParseAccessUnits(Cat(aud, sps, pps, idr, idr, aud, p, p, aud, p));
            AssertEq(withAud.Count, 3);
            Assert(withAud[0].Keyframe && !withAud[1].Keyframe && !withAud[2].Keyframe,
                "only the IDR access unit is a keyframe");
            Assert(H26x.SplitNals(withAud[0].Data).Any(n => H26x.H264NalType(n.Span) == H26x.H264Sps),
                "keyframe AU keeps its in-band SPS");
            AssertEq(H26x.SplitNals(withAud[1].Data).Count, 2); // both slices of frame 2 stay together
            AssertEq(H26x.SplitNals(withAud[0].Data).Count(n => H26x.H264NalType(n.Span) == H26x.H264Idr), 2);

            // No AUDs (fallback encode): a second slice starts the next frame.
            var noAud = DemoRig.ParseAccessUnits(Cat(sps, pps, idr, p, p));
            AssertEq(noAud.Count, 3);
            Assert(noAud[0].Keyframe, "first AU (SPS+PPS+IDR) is the keyframe");

            // A later bare keyframe (encoder that wrote headers once): the parser
            // must prepend the stashed SPS/PPS — the hub's GOP cache and every
            // late-joining viewer depend on parameter sets riding each keyframe.
            var bare = DemoRig.ParseAccessUnits(Cat(sps, pps, idr, aud, p, aud, idr));
            var lastNals = H26x.SplitNals(bare[^1].Data);
            Assert(bare[^1].Keyframe, "bare IDR still flagged as keyframe");
            AssertEq(H26x.H264NalType(lastNals[0].Span), H26x.H264Sps);
            AssertEq(H26x.H264NalType(lastNals[1].Span), H26x.H264Pps);

            // Parameter-set-only tail (nothing decodable) emits no unit.
            AssertEq(DemoRig.ParseAccessUnits(Cat(sps, pps)).Count, 0);
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

        Test("audio transcode: ogg/opus depacketizer + RTP + SDP", () =>
        {
            // Ogg page: capture pattern + fixed header + lacing table + payload.
            // The reader skips CRC (its source is a local pipe), so zeros do.
            static byte[] Page(byte headerType, int[] laces, byte[] payload)
            {
                var page = new byte[27 + laces.Length + payload.Length];
                page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S';
                page[5] = headerType;
                page[26] = (byte)laces.Length;
                for (int i = 0; i < laces.Length; i++) page[27 + i] = (byte)laces[i];
                payload.CopyTo(page, 27 + laces.Length);
                return page;
            }

            var reader = new OggOpusReader();
            // OpusHead/OpusTags describe the stream, they are not audio — swallowed.
            var head = Encoding.ASCII.GetBytes("OpusHead").Concat(new byte[] { 1, 1 }).ToArray();
            AssertEq(reader.Feed(Page(0x02, new[] { head.Length }, head)).Count, 0);
            // Two packets on one page, arriving split mid-page across two feeds.
            var page2 = Page(0, new[] { 3, 4 }, new byte[] { 10, 11, 12, 20, 21, 22, 23 });
            AssertEq(reader.Feed(page2.AsSpan(0, 10)).Count, 0);
            var got = reader.Feed(page2.AsSpan(10));
            AssertEq(got.Count, 2);
            AssertSeq(got[0], new byte[] { 10, 11, 12 });
            AssertSeq(got[1], new byte[] { 20, 21, 22, 23 });
            // A 255 lacing value continues the packet — across a page boundary too.
            var part1 = new byte[255];
            for (int i = 0; i < part1.Length; i++) part1[i] = (byte)i;
            AssertEq(reader.Feed(Page(0, new[] { 255 }, part1)).Count, 0);
            var joined = reader.Feed(Page(0x01, new[] { 3 }, new byte[] { 1, 2, 3 }));
            AssertEq(joined.Count, 1);
            AssertEq(joined[0].Length, 258);
            AssertSeq(joined[0].Skip(255).ToArray(), new byte[] { 1, 2, 3 });

            // RFC 7587: one Opus packet per RTP packet, payload verbatim, 48 kHz ts.
            var pkt = new Rtsp.RtpPacketizer(97);
            var rtp = pkt.PacketizeOpus(new byte[] { 0xF8, 1, 2 }, 0x1092);
            AssertEq(rtp.Length, 12 + 3);
            AssertEq(rtp[1] & 0x7F, 97);
            AssertEq(rtp[6], 0x10);
            AssertEq(rtp[7], 0x92);
            AssertSeq(rtp.Skip(12).ToArray(), new byte[] { 0xF8, 1, 2 });

            // SDP: an Opus session advertises opus/48000/2 in PLACE of the
            // original audio; an original session keeps the camera's AAC line —
            // the same hub serves both, chosen per URL.
            var hub = new OpusSdpHub { Audio = new Streaming.AudioTrackInfo(true, 16000, 1, new byte[] { 0x14, 0x08 }) };
            var sdp = Rtsp.Sdp.Build(hub, "cam", opus: true);
            Assert(sdp.Contains("opus/48000/2"), "opus rtpmap advertised");
            Assert(sdp.Contains("sprop-stereo=0"), "mono fmtp advertised");
            Assert(!sdp.Contains("mpeg4-generic"), "the original AAC line is replaced");
            Assert(Rtsp.Sdp.Build(hub, "cam").Contains("mpeg4-generic"), "AAC advertised without opus");

            // URL parsing: ?audio= picks the codec. The SDP's control attribute is
            // relative ("trackID=N"), and clients resolve it against Content-Base
            // in BOTH of these shapes once a query is present. Reading the trackID
            // out of only one position turns the audio SETUP into a second video
            // track — the stream then plays with no sound at all.
            var (p1, t1, a1) = Rtsp.RtspConnection.ParseUri("rtsp://h:8654/cam/subStream");
            AssertEq(p1, "/cam/subStream");
            AssertEq(t1, -1);
            Assert(a1 == null, "no query = no audio choice");
            // (a) Content-Base concatenated verbatim: the marker trails the query.
            var (p2, t2, a2) = Rtsp.RtspConnection.ParseUri("rtsp://h/cam?audio=Opus&future=1/trackID=1");
            AssertEq(p2, "/cam");
            AssertEq(t2, 1);
            AssertEq(a2!, "opus"); // case-folded; the unknown "future" KEY is ignored
            // (b) URL-aware resolver: the marker joins the PATH, query stays last.
            var (p3, t3, a3) = Rtsp.RtspConnection.ParseUri("rtsp://h/cam/trackID=1?audio=opus");
            AssertEq(p3, "/cam");
            AssertEq(t3, 1);
            AssertEq(a3!, "opus");
            var (p4, t4, a4) = Rtsp.RtspConnection.ParseUri("rtsp://h/cam/subStream/trackID=0?audio=original&x=2");
            AssertEq(p4, "/cam/subStream");
            AssertEq(t4, 0);
            AssertEq(a4!, "original");
            // No query at all still works (every pre-0.9.8 client).
            var (p5, t5, a5) = Rtsp.RtspConnection.ParseUri("rtsp://h/cam/trackID=1");
            AssertEq(p5, "/cam");
            AssertEq(t5, 1);
            Assert(a5 == null, "no query = mount default");
            Assert(Rtsp.RtspConnection.TryMapAudio("opus", out var mOpus) && mOpus == true, "opus maps to transcode");
            Assert(Rtsp.RtspConnection.TryMapAudio("original", out var mOrig) && mOrig == false, "original maps to camera track");
            Assert(Rtsp.RtspConnection.TryMapAudio(null, out var mNone) && mNone == null, "absent = mount default");
            Assert(!Rtsp.RtspConnection.TryMapAudio("mp3", out _), "unsupported format is refused");
        });

        Test("camera audio settings: Enc record-audio mapping", () =>
        {
            static System.Text.Json.Nodes.JsonObject Enc(string json) =>
                (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;
            // The flag's documented home is the TOP of the Enc object (one switch
            // for the camera, matching the app — confirmed against reolink_aio,
            // 2026-07-27); a per-stream nesting is the fallback. No audio field
            // anywhere reads as "this firmware has no such switch" and hides the row.
            Assert(Streaming.CameraControl.EncRecordAudio(
                Enc("{\"audio\":1,\"mainStream\":{\"size\":\"640*480\"}}")) == true, "top-level flag on");
            Assert(Streaming.CameraControl.EncRecordAudio(
                Enc("{\"audio\":0,\"mainStream\":{\"audio\":1}}")) == false, "top-level flag outranks nested");
            Assert(Streaming.CameraControl.EncRecordAudio(
                Enc("{\"mainStream\":{\"audio\":1},\"subStream\":{\"audio\":0}}")) == true, "nested fallback: any stream on = on");
            Assert(Streaming.CameraControl.EncRecordAudio(
                Enc("{\"mainStream\":{\"audio\":0},\"subStream\":{\"audio\":0}}")) == false, "nested fallback: all off = off");
            Assert(Streaming.CameraControl.EncRecordAudio(
                Enc("{\"mainStream\":{\"size\":\"640*480\"}}")) == null, "no audio flag = feature absent");
        });

        Test("event label mapping", () =>
        {
            var labels = Recording.EventRecorder.LabelsOf(new MotionPush("MD",
                new[] { "people", "dog_cat", "people" }));
            AssertEq(string.Join(",", labels.OrderBy(x => x)), "animal,person");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(new MotionPush("MD", Array.Empty<string>()))),
                "motion");
            // Reolink's non-AI bucket: battery cameras (Argus Solar, 2026-07-22)
            // report PIR wake detections as AI type "other". It must land on the
            // "motion" label — the raw token is not a selectable event type, so
            // left unmapped it silently discarded every wake-capture recording.
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(new MotionPush("MD", new[] { "other" }))),
                "motion");
            AssertEq(string.Join(",", Recording.EventRecorder.LabelsOf(
                new MotionPush("MD", new[] { "people", "other" })).OrderBy(x => x)),
                "motion,person");
            Assert(new Recording.CameraRecordingSettings(Events: true, Continuous: false, EventTypes: null)
                    .AllowsLabel("motion"),
                "default event types allow motion, so PIR-grade wakes are kept out of the box");
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

            // The synthetic self-wake push (wake-capture): starts a PROVISIONAL
            // recording only — the footage is kept solely when a detection the
            // camera's event-type selection allows confirms it, and discarded
            // otherwise. The Status "wake" marker is what the recorder keys the
            // provisional path on; the label itself is never a recordable type.
            var wake = new Protocol.MotionPush("wake", new[] { "wake" }, External: true);
            Assert(wake.Active, "synthetic wake push is an active detection");
            Assert(wake.Status == "wake", "the wake marker rides Status — the recorder's provisional key");
            Assert(Recording.EventRecorder.LabelsOf(wake) is ["wake"], "wake label passes through unmapped");
            Assert(!untouched.AllowsLabel("wake"),
                "no filter ever allows 'wake' itself — only a confirming detection keeps the footage");
            var wakeClear = new Protocol.MotionPush("none", Array.Empty<string>(), External: true);
            Assert(!wakeClear.Active, "synthetic all-clear ends the wake window via the post-roll");

            // Hint marker (sent only when Motion is ticked): keys the
            // confirmed-at-start path; never a recordable type itself.
            var hint = new Protocol.MotionPush("hint", new[] { "wake" }, External: true);
            Assert(hint.Active, "synthetic hint push is an active detection");
            Assert(!untouched.AllowsLabel("hint"), "no filter ever allows 'hint' itself");
        });

        Test("hint wakes kept as motion events; markers split events; plain wakes discard", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Recording.EventStore(Path.Combine(dir, "rec"));
                var recorder = new Recording.EventRecorder("hintcam", new Streaming.StreamHub("hintcam"),
                    new StubCameraControl("hintcam"), store,
                    new Config.RecordingConfig { PostSeconds = 1, MaxClipSeconds = 10 },
                    new Recording.RecordingSettings(dir));
                var started = new List<Recording.EventRecord>();
                var recFlags = new List<bool>();
                recorder.EventStarted += r => { lock (started) started.Add(r); };
                recorder.RecordingChanged += f => { lock (recFlags) recFlags.Add(f); };
                using var cts = new CancellationTokenSource();
                var run = Task.Run(() => recorder.RunAsync(cts.Token));
                bool WaitUntil(Func<bool> cond, int ms)
                {
                    var until = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < until)
                    {
                        if (cond()) return true;
                        Thread.Sleep(25);
                    }
                    return cond();
                }
                int Started() { lock (started) return started.Count; }

                recorder.OnMotion(new Protocol.MotionPush("hint", new[] { "wake" }, External: true));
                Assert(WaitUntil(() => Started() == 1, 5000),
                    "a hint push must start an announced event immediately");
                lock (started)
                    Assert(started[0].Labels.SequenceEqual(new[] { "motion" }),
                        $"a hint event is labeled motion, got: {string.Join("+", started[0].Labels)}");
                lock (recFlags) Assert(recFlags.Contains(true), "a hint event drives the recording sensor");

                // A second marker means a NEW wake session: it must END the open
                // event and start its own.
                recorder.OnMotion(new Protocol.MotionPush("hint", new[] { "wake" }, External: true));
                Assert(WaitUntil(() => Started() == 2, 5000),
                    "a marker inside a wake-opened event must split into a new event");
                lock (started)
                {
                    Assert(started[0].Id != started[1].Id, "the split produces a distinct event");
                    Assert(WaitUntil(() => store.Find(started[0].Id) is { Ongoing: false }, 5000),
                        "the first event closed when the marker split it");
                    Assert(store.Find(started[0].Id) != null, "the first event's footage is kept");
                }
                recorder.OnMotion(new Protocol.MotionPush("none", Array.Empty<string>(), External: true));
                Recording.EventRecord second;
                lock (started) second = started[1];
                Assert(WaitUntil(() => store.Find(second.Id) is { Ongoing: false }, 8000),
                    "the second event closes through the normal post-roll");

                // The plain wake marker is unchanged: tentative, silent, discarded.
                lock (recFlags) recFlags.Clear();
                recorder.OnMotion(new Protocol.MotionPush("wake", new[] { "wake" }, External: true));
                Assert(WaitUntil(() => store.List("hintcam").Count == 3, 5000),
                    "a plain wake push opens a tentative record");
                Assert(Started() == 2, "a plain wake push must not announce an event");
                lock (recFlags) Assert(!recFlags.Contains(true),
                    "a tentative wake must not drive the recording sensor");
                recorder.OnMotion(new Protocol.MotionPush("none", Array.Empty<string>(), External: true));
                Assert(WaitUntil(() => store.List("hintcam").Count == 2, 8000),
                    "an unconfirmed wake is discarded");
                cts.Cancel();
                try { run.GetAwaiter().GetResult(); } catch { }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("event search: deterministic parse, keyword scoring, LLM-plan intake", () =>
        {
            var cams = new List<string> { "FrontDoor", "Back Yard", "TestCam" };
            var now = new DateTime(2026, 8, 25, 14, 0, 0); // deterministic clock for the date grammar

            var p = Recording.EventSearch.Parse("person in FrontDoor yesterday", cams, now);
            Assert(p.Labels is ["person"], "label parsed");
            Assert(p.Cameras is ["FrontDoor"], "camera parsed");
            Assert(p.FromLocal == new DateTime(2026, 8, 24) && p.ToLocal == new DateTime(2026, 8, 25),
                "yesterday resolves to the full prior day");
            Assert(p.Structured, "fully structured — no AI involved");

            p = Recording.EventSearch.Parse("cars in back yard between 2 and 4pm today", cams, now);
            Assert(p.Labels is ["vehicle"], "synonym maps to the canonical label");
            Assert(p.Cameras is ["Back Yard"], "multi-word camera name parsed");
            Assert(p.FromLocal == now.Date.AddHours(14) && p.ToLocal == now.Date.AddHours(16),
                $"between 2 and 4pm parsed, got {p.FromLocal:HH:mm}-{p.ToLocal:HH:mm}");

            // The live miss: "test cam" must bind the TestCam camera, phrasing
            // words must not become keywords, and the query stays structured.
            p = Recording.EventSearch.Parse("show me vehicles detected on test cam today", cams, now);
            Assert(p.Cameras is ["TestCam"], "camel-case camera matches its spaced form");
            Assert(p.Labels is ["vehicle"] && p.Structured,
                $"phrasing words are filler, got keywords: {string.Join(",", p.Keywords)}");

            // Negation excludes, never inverts.
            p = Recording.EventSearch.Parse("no cars today", cams, now);
            Assert(p.NotLabels is ["vehicle"] && p.Labels.Count == 0 && p.Structured,
                "negation lands in NotLabels");

            // Every common time-range syntax resolves to 14:00-16:00 today.
            foreach (var form in new[] { "between 2pm and 4 today", "2-4pm today", "from 2 to 4pm today", "2pm to 4pm today" })
            {
                p = Recording.EventSearch.Parse(form, cams, now);
                Assert(p.FromLocal == now.Date.AddHours(14) && p.ToLocal == now.Date.AddHours(16),
                    $"\"{form}\" → {p.FromLocal:HH:mm}-{p.ToLocal:HH:mm}");
            }
            p = Recording.EventSearch.Parse("between 11 and 12pm today", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(11) && p.ToLocal == now.Date.AddHours(12),
                "bare-first noon range prefers the morning reading over a 13h overnight");

            // One-sided bounds.
            p = Recording.EventSearch.Parse("before 9am", cams, now);
            Assert(p.FromLocal == null && p.ToLocal == now.Date.AddHours(9), "before 9am is an upper bound");
            p = Recording.EventSearch.Parse("since monday", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1) && p.ToLocal > now.AddMinutes(-1),
                "since monday runs through now");

            // Grammar coverage.
            p = Recording.EventSearch.Parse("trucks today", cams, now);
            Assert(p.Labels is ["vehicle"] && p.Structured, "plural synonyms map to labels");
            p = Recording.EventSearch.Parse("deliveries yesterday", cams, now);
            Assert(p.Labels is ["package"], "ies-plurals map too");
            p = Recording.EventSearch.Parse("last month", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 7, 1) && p.ToLocal == new DateTime(2026, 8, 1),
                "last month is calendar July");
            p = Recording.EventSearch.Parse("2 days ago", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-2) && p.ToLocal == now.Date.AddDays(-1),
                "N days ago means that day");
            p = Recording.EventSearch.Parse("the day before yesterday", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-2), "day before yesterday");
            p = Recording.EventSearch.Parse("over the weekend", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 22) && p.ToLocal == new DateTime(2026, 8, 24),
                "weekend is Sat 00:00 to Mon 00:00");
            p = Recording.EventSearch.Parse("yesterday and today", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1) && p.ToLocal == now.Date.AddDays(1),
                "compound days span both");
            p = Recording.EventSearch.Parse("yesterday morning", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(6) && p.ToLocal == now.Date.AddDays(-1).AddHours(12),
                "a day intersects with its time-of-day word");
            p = Recording.EventSearch.Parse("8/20", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 20) && p.ToLocal == new DateTime(2026, 8, 21),
                "US slash date");
            p = Recording.EventSearch.Parse("maybe 2 people at the door", cams, now);
            Assert(p.FromLocal == null && p.Labels is ["person"], "\"maybe 2\" is not May 2nd");
            p = Recording.EventSearch.Parse("line crossing yesterday", cams, now);
            Assert(p.Labels is ["line-crossing"], "spaced label phrase maps");

            // Verification-pass regressions, pinned.
            p = Recording.EventSearch.Parse("did anyone come by yesterday", cams, now);
            Assert(p.Labels is ["person"] && p.FromLocal == now.Date.AddDays(-1) && p.ToLocal == now.Date,
                "phrasal \"come by\" never reads as a before-bound");
            p = Recording.EventSearch.Parse("without the dog", cams, now);
            Assert(p.NotLabels is ["animal"] && p.Labels.Count == 0, "\"without the\" negates");
            p = Recording.EventSearch.Parse("no cars or people today", cams, now);
            Assert(p.NotLabels.Contains("vehicle") && p.NotLabels.Contains("person") && p.Labels.Count == 0,
                "negated lists distribute");
            p = Recording.EventSearch.Parse("no line crossing today", cams, now);
            Assert(p.NotLabels is ["line-crossing"], "spaced label phrase negates too");
            p = Recording.EventSearch.Parse("between 11pm and 1 today", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(23) && p.ToLocal == now.Date.AddDays(1).AddHours(1),
                $"bare second hour crosses midnight sanely, got {p.FromLocal}-{p.ToLocal}");
            p = Recording.EventSearch.Parse("between 9am and 5 today", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(9) && p.ToLocal == now.Date.AddHours(17),
                "bare second hour picks the nearest-forward reading");
            p = Recording.EventSearch.Parse("since 10pm", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(22) && p.ToLocal > now.AddMinutes(-1),
                "a since-time still ahead of now anchors to yesterday");
            p = Recording.EventSearch.Parse("before 9am yesterday", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1) && p.ToLocal == now.Date.AddDays(-1).AddHours(9),
                "before-time on a named day keeps the day's floor");
            p = Recording.EventSearch.Parse("since last month", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 7, 1) && p.ToLocal > now.AddMinutes(-1),
                "since + range phrase opens the bound");
            p = Recording.EventSearch.Parse("friday last week", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 21) && p.ToLocal == new DateTime(2026, 8, 22),
                "a named day inside a range wins over the range");
            p = Recording.EventSearch.Parse("last week at 3pm", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 17) && p.ToLocal == new DateTime(2026, 8, 24),
                "a clock time never collapses a multi-day range");
            p = Recording.EventSearch.Parse("at 3 in the morning yesterday", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(3),
                $"\"in the morning\" overrides the bare-hour daytime rule, got {p.FromLocal}");
            p = Recording.EventSearch.Parse("june 5 2025", cams, now);
            Assert(p.FromLocal == new DateTime(2025, 6, 5), "explicit years are honored");
            p = Recording.EventSearch.Parse("from 8/20 to 8/22", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 20) && p.ToLocal == new DateTime(2026, 8, 23),
                "date pairs span");
            p = Recording.EventSearch.Parse("past week", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-7) && p.ToLocal > now.AddMinutes(-1),
                "digit-less past week rolls");
            p = Recording.EventSearch.Parse("between 2:30 and 4 today", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(14).AddMinutes(30) && p.ToLocal == now.Date.AddHours(16),
                "meridiem-less clock ranges prefer daytime");
            p = Recording.EventSearch.Parse("between 9 and 10", cams, now);
            Assert(p.StrayDigits && p.FromLocal == null,
                "unplaceable digits flag the query for the AI");

            // The six UI languages parse through the translation pre-pass.
            p = Recording.EventSearch.Parse("voitures hier", cams, now);
            Assert(p.Labels is ["vehicle"] && p.FromLocal == now.Date.AddDays(-1) && p.ToLocal == now.Date,
                "french folds into the grammar");
            p = Recording.EventSearch.Parse("keine hunde letzte woche", cams, now);
            Assert(p.NotLabels is ["animal"] && p.FromLocal == new DateTime(2026, 8, 17)
                && p.ToLocal == new DateTime(2026, 8, 24), "german negation and ranges fold");
            p = Recording.EventSearch.Parse("coches la semana pasada", cams, now);
            Assert(p.Labels is ["vehicle"] && p.FromLocal == new DateTime(2026, 8, 17)
                && p.ToLocal == new DateTime(2026, 8, 24), "spanish postpositive \"pasada\" reorders");
            p = Recording.EventSearch.Parse("iemand tussen 14:00 en 16:00 gisteren", cams, now);
            Assert(p.Labels is ["person"] && p.FromLocal == now.Date.AddDays(-1).AddHours(14)
                && p.ToLocal == now.Date.AddDays(-1).AddHours(16), "dutch clock ranges fold");
            p = Recording.EventSearch.Parse("pessoas ontem", cams, now);
            Assert(p.Labels is ["person"] && p.FromLocal == now.Date.AddDays(-1),
                "portuguese folds");
            p = Recording.EventSearch.Parse("psy wczoraj", cams, now);
            Assert(p.Labels is ["animal"] && p.FromLocal == now.Date.AddDays(-1),
                "polish folds");
            p = Recording.EventSearch.Parse("czerwony samochod dzisiaj", cams, now);
            Assert(p.Labels is ["vehicle"] && p.FromLocal == now.Date,
                "unaccented typing still hits the accented vocabulary");
            p = Recording.EventSearch.Parse("coches en el jardín ayer", cams, now);
            Assert(p.Labels is ["vehicle"] && p.Keywords.Contains("jardin") && p.FromLocal == now.Date.AddDays(-1),
                "accented leftovers fold to ascii keywords without ICU");
            p = Recording.EventSearch.Parse("hace 2 horas", cams, now);
            Assert(p.FromLocal == now.AddHours(-2) && p.ToLocal > now.AddMinutes(-1),
                "spanish prefix-ago becomes the postfix form");
            p = Recording.EventSearch.Parse("voitures le 3/8", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 3),
                $"continental queries read 3/8 day-first, got {p.FromLocal}");
            p = Recording.EventSearch.Parse("pies yesterday", cams, now);
            Assert(p.Labels.Count == 0 && p.Keywords.Contains("pies") && p.FromLocal == now.Date.AddDays(-1),
                "an english query is never hijacked by a colliding foreign word");

            // Second review round: word times in ranges, 24h pairs, slang,
            // bare time-of-day anchoring, dash ranges.
            p = Recording.EventSearch.Parse("between midnight and 6am today", cams, now);
            Assert(p.FromLocal == now.Date && p.ToLocal == now.Date.AddHours(6),
                "midnight works as a range start");
            p = Recording.EventSearch.Parse("person from noon to 2pm", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(12) && p.ToLocal == now.Date.AddHours(14),
                "noon works as a range start");
            p = Recording.EventSearch.Parse("noon to midnight yesterday", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(12) && p.ToLocal == now.Date,
                "midnight as a range end means the NEXT midnight");
            p = Recording.EventSearch.Parse("people yesterday between 20 and 22", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(20)
                && p.ToLocal == now.Date.AddDays(-1).AddHours(22), "bare 24h pairs are unambiguous");
            p = Recording.EventSearch.Parse("btwn 2 and 5pm 2day", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(14) && p.ToLocal == now.Date.AddHours(17)
                && p.Keywords.Count == 0, "texting shorthand folds to english");
            p = Recording.EventSearch.Parse("a wk ago", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-7), "\"wk\" reads as week");
            p = Recording.EventSearch.Parse("motion overnight", cams, now);
            Assert(p.Labels is ["motion"] && p.FromLocal == now.Date.AddDays(-1).AddHours(22)
                && p.ToLocal == now.Date.AddHours(6), "bare \"overnight\" means the night just past");
            p = Recording.EventSearch.Parse("person morning", cams, now);
            Assert(p.FromLocal == now.Date.AddHours(6) && p.ToLocal == now.Date.AddHours(12),
                "a bare time-of-day anchors to today");
            p = Recording.EventSearch.Parse("around 9 last night", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(21),
                $"a bare hour inside a night window reads as pm, got {p.FromLocal}");
            p = Recording.EventSearch.Parse("aug 20-22", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 20) && p.ToLocal == new DateTime(2026, 8, 23),
                "month day-day dashes span");
            p = Recording.EventSearch.Parse("mon-fri", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 24) && p.ToLocal == new DateTime(2026, 8, 29),
                "weekday dash ranges resolve to this week");
            p = Recording.EventSearch.Parse("coche ayer por la tarde", cams, now);
            Assert(p.Labels is ["vehicle"] && p.Keywords.Count == 0
                && p.FromLocal == now.Date.AddDays(-1).AddHours(12), "spanish \"por\" folds away");

            // Adversarial-review round: romance dates and articles, ago variants,
            // weekday bounds and ranges, night-window bounds, merge yields.
            p = Recording.EventSearch.Parse("coches el 3 de agosto", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 3) && p.ToLocal == new DateTime(2026, 8, 4),
                "\"3 de agosto\" is one day, never the whole month");
            p = Recording.EventSearch.Parse("coches desde el lunes", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 24) && p.ToLocal > now.AddMinutes(-1),
                "the mandatory spanish article does not break since-bounds");
            p = Recording.EventSearch.Parse("carros ha 2 horas", cams, now);
            Assert(p.Labels is ["vehicle"] && p.FromLocal == now.AddHours(-2),
                "unaccented \"ha\" still reads as ago");
            p = Recording.EventSearch.Parse("avant-hier à 15h", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-2).AddHours(15),
                "french day-before-yesterday folds");
            p = Recording.EventSearch.Parse("cars sept 12", cams, now);
            Assert(p.Labels is ["vehicle"] && p.FromLocal?.Month == 9 && p.FromLocal?.Day == 12,
                "\"sept\" the month abbreviation is never french seven");
            p = Recording.EventSearch.Parse("monday until friday", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 24) && p.ToLocal == new DateTime(2026, 8, 28),
                $"weekday-to-weekday bounds never invert, got {p.FromLocal}-{p.ToLocal}");
            p = Recording.EventSearch.Parse("monday last week", cams, now);
            Assert(p.FromLocal == new DateTime(2026, 8, 17) && p.ToLocal == new DateTime(2026, 8, 18),
                "a weekday resolves INTO the named range");
            p = Recording.EventSearch.Parse("last night before 9pm", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(20)
                && p.ToLocal == now.Date.AddDays(-1).AddHours(21),
                "a clock bound keeps the range phrase as its floor");
            p = Recording.EventSearch.Parse("last night between 10pm and 2am", cams, now);
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(22) && p.ToLocal == now.Date.AddHours(2),
                $"a midnight-crossing clock range keeps its far side, got {p.FromLocal}-{p.ToLocal}");
            p = Recording.EventSearch.Parse("someone at 3:15", cams, now);
            var aiDay = new Recording.EventQuery
            {
                FromLocal = now.Date.AddDays(-1).AddHours(15),
                ToLocal = now.Date.AddDays(-1).AddHours(16),
            };
            var m2 = Recording.EventSearch.Merge(p, aiDay);
            Assert(m2.FromLocal == aiDay.FromLocal && m2.ToLocal == aiDay.ToLocal,
                "a time-only parser date yields to the AI's day");
            var m3 = Recording.EventSearch.Merge(
                Recording.EventSearch.Parse("someone yesterday at 3:15", cams, now), aiDay);
            Assert(m3.FromLocal == now.Date.AddDays(-1).AddHours(15).AddMinutes(15),
                "a day-anchored parser date still wins the merge");
            p = Recording.EventSearch.Parse("people wearing red", cams, now);
            Assert(p.Labels is ["person"] && p.Keywords is ["red"],
                "\"wearing\" is the asking, not the scene");
            var aiCam = new Recording.EventQuery();
            aiCam.Cameras.Add("Back Yard");
            aiCam.Keywords.Add("yard");
            aiCam.Keywords.Add("red");
            var m4 = Recording.EventSearch.Merge(Recording.EventSearch.Parse("person in the yard", cams, now), aiCam);
            Assert(m4.Cameras is ["Back Yard"] && m4.Keywords is ["red"],
                $"a bound camera's place words leave the keywords, got [{string.Join(",", m4.Keywords)}]");

            // The AI judge pass: pool query, prompt shape, reply tolerance.
            var so = Recording.EventSearch.Parse("person in FrontDoor wearing red yesterday", cams, now).StructuralOnly();
            Assert(so.Labels is ["person"] && so.Cameras is ["FrontDoor"]
                && so.FromLocal == now.Date.AddDays(-1) && so.Keywords.Count == 0,
                "the judge pool keeps the hard filters and drops the keywords");
            var jev = new List<Recording.EventRecord>
            {
                new() { Id = "a", Camera = "c", AiDescription = "A person in a gray shirt.\nSecond line." },
                new() { Id = "b", Camera = "c", AiDescription = "A red car." },
            };
            var jp = Recording.EventSearch.JudgeUserPrompt("guy in a gray shirt", jev);
            Assert(jp.Contains("Query: guy in a gray shirt") && jp.Contains("1. A person in a gray shirt. Second line.")
                && jp.Contains("2. A red car."), "judge prompt numbers the descriptions");
            Assert(Recording.EventSearch.ParseJudge("[2,5]", 6) is [2, 5], "clean judge reply");
            Assert(Recording.EventSearch.ParseJudge("```json\n[1, 3]\n```", 6) is [1, 3], "fenced judge reply");
            Assert(Recording.EventSearch.ParseJudge("Events 2 and 4 match.", 6) is [2, 4], "prose judge reply");
            Assert(Recording.EventSearch.ParseJudge("[]", 6) is [], "empty judge reply");
            Assert(Recording.EventSearch.ParseJudge("none of the events match", 6) is [],
                "worded none-reply");
            Assert(Recording.EventSearch.ParseJudge("[0, 3, 99]", 6) is [3], "out-of-range numbers drop");
            Assert(Recording.EventSearch.ParseJudge("I cannot help with that.", 6) == null,
                "garbage reply signals the keyword fallback");
            Assert(Recording.EventSearch.ParseJudge(null, 6) == null,
                "a failed LLM call is a fallback, never a crash");
            Assert(Recording.EventSearch.ParseJudge("as requested, e.g. [2,5]. My answer: [4]", 6) is [4],
                "an echoed example never becomes the answer");
            Assert(Recording.EventSearch.ParseJudge("Events 3 and 5 do not match the query.", 6) is [],
                "negative prose is a no-match, not a pick list");
            var aiGh = new Recording.EventQuery();
            aiGh.Cameras.Add("Greenhouse");
            aiGh.Keywords.Add("green");
            var m5 = Recording.EventSearch.Merge(new Recording.EventQuery(), aiGh);
            Assert(m5.Keywords is ["green"],
                "camera-name shadowing never deletes the last keywords");

            p = Recording.EventSearch.Parse("blue car near the garage last night", cams, now);
            Assert(p.Labels is ["vehicle"] && p.Keywords.Contains("blue") && p.Keywords.Contains("garage"),
                "leftover words become keywords");
            Assert(!p.Structured, "keywords mean the AI may refine");
            Assert(p.FromLocal == now.Date.AddDays(-1).AddHours(20) && p.ToLocal == now.Date.AddHours(6),
                "last night is yesterday 20:00 to 06:00");

            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Recording.EventStore(dir);
                // Anchored inside the local calendar day, not "minutes ago" — a
                // run just after midnight must not push them into yesterday.
                var a = store.Create("FrontDoor", DateTime.Today.AddHours(1).ToUniversalTime(), new[] { "person" });
                a.AiDescription = "A person in a blue jacket walks to the door.";
                a.Ongoing = false;
                store.Save(a);
                var b = store.Create("TestCam", DateTime.Today.AddHours(2).ToUniversalTime(), new[] { "motion" });
                b.AiDescription = "A red car reverses out of the driveway.";
                b.AiLevel = "yellow";
                b.Ongoing = false;
                store.Save(b);

                var hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("person in FrontDoor today", cams, DateTime.Now), store);
                Assert(hits is [{ Camera: "FrontDoor" }], "structured filter hits the right event");

                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("red driveway", cams, DateTime.Now), store);
                Assert(hits.Count == 1 && hits[0].Id == b.Id, "keywords match the stored AI description");

                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("suspicious", cams, DateTime.Now), store);
                Assert(hits.Count == 1 && hits[0].Id == b.Id, "threat vocabulary reaches the AI level");

                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("purple in TestCam today", cams, DateTime.Now), store);
                Assert(hits.Count == 1 && hits[0].Id == b.Id,
                    "unmatched keywords fall back to the structured filters, never to zero");
                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("purple", cams, DateTime.Now), store);
                Assert(hits.Count == 0, "keyword-only queries with no match stay empty");

                var c1 = store.Create("FrontDoor", DateTime.UtcNow.AddMinutes(-8), new[] { "person" });
                c1.AiDescription = "A person in a gray shirt stands by the gate.";
                c1.Ongoing = false;
                store.Save(c1);
                var c2 = store.Create("FrontDoor", DateTime.UtcNow.AddMinutes(-7), new[] { "person" });
                c2.AiDescription = "A person in a light shirt walks past.";
                c2.Ongoing = false;
                store.Save(c2);
                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("gray shirt", cams, DateTime.Now), store,
                    out var km, out var kp);
                Assert(km && !kp && hits.Count == 1 && hits[0].Id == c1.Id,
                    "full keyword matches shut out the partial ones");
                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("green shirt", cams, DateTime.Now), store,
                    out km, out kp);
                Assert(km && kp && hits.Count == 2,
                    "only partial matches left — returned but flagged as closest");

                var old = store.Create("TestCam", DateTime.UtcNow.AddDays(-30), new[] { "person" });
                old.Ongoing = false;
                store.Save(old);
                for (int i = 0; i < 1010; i++)
                {
                    var mo = store.Create("TestCam", DateTime.UtcNow.AddMinutes(-i), new[] { "motion" });
                    mo.Ongoing = false;
                    store.Save(mo);
                }
                hits = Recording.EventSearch.Execute(
                    Recording.EventSearch.Parse("people", cams, DateTime.Now), store, 2000);
                Assert(hits.Any(h => h.Id == old.Id),
                    "an undated search reaches past the newest thousand events");

                var oldRed = store.Create("TestCam", DateTime.UtcNow.AddDays(-35), new[] { "person" });
                oldRed.AiDescription = "A person in a red coat crosses the lawn.";
                oldRed.Ongoing = false;
                store.Save(oldRed);
                for (int i = 0; i < 310; i++)
                {
                    var pn = store.Create("TestCam", DateTime.UtcNow.AddSeconds(-i), new[] { "person" });
                    pn.AiDescription = "A person walks by.";
                    pn.Ongoing = false;
                    store.Save(pn);
                }
                var jpool = Recording.EventSearch.JudgePool(
                    Recording.EventSearch.Parse("people wearing red", cams, DateTime.Now), store);
                Assert(jpool.Any(e => e.Id == oldRed.Id),
                    "a keyword-hit event never ages out of the judge pool");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }

            var t = Recording.EventSearch.ParseTranslated(
                "```json\n{\"labels\":[\"person\"],\"cameras\":[\"frontdoor\"]," +
                "\"from\":\"2026-08-24 00:00\",\"to\":null,\"keywords\":[\"mailman\",\"Uniform\"]}\n```",
                cams, now);
            Assert(t != null && t.Labels is ["person"] && t.Cameras is ["FrontDoor"],
                "translated plan normalizes labels and camera casing");
            Assert(t!.FromLocal == new DateTime(2026, 8, 24) && t.ToLocal == null, "translated dates parse");
            Assert(t.Keywords.Contains("mailman") && t.Keywords.Contains("uniform"), "translated keywords lowercase");
            Assert(Recording.EventSearch.ParseTranslated("no json here", cams, now) == null, "garbage in, null out");

            // Dumb-model tolerance: echoed schema, trailing comma, capitalized
            // keys, scalar fields, word dates, camera variants — one answer.
            var echo = Recording.EventSearch.ParseTranslated(
                "{\"labels\":[],\"cameras\":[],\"from\":null,\"to\":null,\"keywords\":[]}\n" +
                "{\"Labels\":\"car\",\"Cameras\":\"test cam\",\"From\":\"yesterday\",\"To\":\"today\",\"Keywords\":\"blue truck\",}",
                cams, now);
            Assert(echo != null, "echoed schema plus sloppy answer still parses");
            Assert(echo!.Labels is ["vehicle"], "capitalized scalar label folds to canonical");
            Assert(echo.Cameras is ["TestCam"], "camera variant from the model matches");
            Assert(echo.FromLocal == now.Date.AddDays(-1) && echo.ToLocal == now.Date.AddDays(1),
                $"word dates resolve through the grammar, got {echo.FromLocal}→{echo.ToLocal}");
            Assert(echo.Keywords.Contains("blue") && !echo.Keywords.Contains("truck"),
                "scalar keywords split; label synonyms fold out");
            var py = Recording.EventSearch.ParseTranslated(
                "{'labels': ['person'], 'cameras': [], 'from': None, 'to': None, 'keywords': ['jacket']}",
                cams, now);
            Assert(py != null && py.Labels is ["person"] && py.Keywords is ["jacket"],
                "python-dict output repaired");
            var wrapped = Recording.EventSearch.ParseTranslated(
                "{\"filter\":{\"labels\":[\"animal\"],\"cameras\":[],\"from\":null,\"to\":null,\"keywords\":[]}}",
                cams, now);
            Assert(wrapped != null && wrapped.Labels is ["animal"], "wrapper object unwrapped");
            var arrWrapped = Recording.EventSearch.ParseTranslated(
                "{\"results\":[{\"labels\":[\"person\"],\"cameras\":[],\"from\":null,\"to\":null,\"keywords\":[]}]}",
                cams, now);
            Assert(arrWrapped != null && arrWrapped.Labels is ["person"], "array wrapper unwrapped");
            var dup = Recording.EventSearch.ParseTranslated(
                "{\"labels\":[],\"from\":null,\"labels\":[\"vehicle\"],\"from\":\"2026-08-20\",\"to\":\"2026-08-20\"}",
                cams, now);
            Assert(dup != null && dup.Labels is ["vehicle"] && dup.FromLocal == new DateTime(2026, 8, 20),
                "duplicate keys: the filled repeat wins");
            Assert(dup!.ToLocal == new DateTime(2026, 8, 21), "equal date-only from/to widens to the whole day");
            var unm = Recording.EventSearch.ParseTranslated(
                "{\"labels\":[],\"cameras\":[\"the garden\"],\"from\":null,\"to\":null,\"keywords\":[]}",
                cams, now);
            Assert(unm != null && unm.Cameras.Count == 0 && unm.Keywords.Contains("garden"),
                "unmatched camera guesses fall back to keywords");

            // Whole-word scoring: "red" must not match "covered"; plurals do match.
            var cov = new Recording.EventRecord { Id = "x", Camera = "c", AiDescription = "driveway covered in snow" };
            Assert(Recording.EventSearch.Score(cov, new[] { "red" }) == 0, "no substring false positives");
            var dg = new Recording.EventRecord { Id = "y", Camera = "c", AiDescription = "a dog crosses the yard" };
            Assert(Recording.EventSearch.Score(dg, new[] { "dogs" }) > 0, "plural keyword hits singular text");
            var gry = new Recording.EventRecord { Id = "z", Camera = "c", AiDescription = "a man in a gray shirt" };
            Assert(Recording.EventSearch.Score(gry, new[] { "grey", "shirt" }) == 4,
                "British spelling matches the model's American one");

            // AI refinements fill gaps only — the deterministic dates always win.
            var det = Recording.EventSearch.Parse("cars last week", cams, now);
            var sloppy = new Recording.EventQuery { FromLocal = now.AddDays(-7), ToLocal = now };
            sloppy.Keywords.Add("grey");
            var merged = Recording.EventSearch.Merge(det, sloppy);
            Assert(merged.FromLocal == det.FromLocal && merged.ToLocal == det.ToLocal,
                "merge keeps the parser's calendar dates over the model's rolling window");
            Assert(merged.Labels is ["vehicle"] && merged.Keywords.Contains("grey"),
                "merge unions labels and keywords");
            var t2 = Recording.EventSearch.ParseTranslated(
                "{\"labels\":[],\"cameras\":[],\"from\":null,\"to\":null,\"keywords\":[\"car\",\"frontdoor\",\"grey\"]}",
                cams, now);
            Assert(t2 != null && t2.Labels is ["vehicle"] && t2.Keywords is ["grey"],
                "translated keyword hygiene: synonyms become labels, camera names drop");
            var vis = new Recording.EventRecord { Id = "v", Camera = "c", AiDescription = "A worker in a high-vis jacket at 5 o'clock." };
            Assert(Recording.EventSearch.Score(vis, ["high-vis"]) == 2 && Recording.EventSearch.Score(vis, ["o'clock"]) == 2,
                "hyphenated and apostrophised keywords match the description's split words");
            Assert(Recording.EventSearch.Score(vis, ["high-viz"]) == 0, "…but only when every part is there");
        });

        Test("wake window holds through a camera all-clear (no truncated tentatives)", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Recording.EventStore(Path.Combine(dir, "rec"));
                var recorder = new Recording.EventRecorder("holdcam", new Streaming.StreamHub("holdcam"),
                    new StubCameraControl("holdcam"), store,
                    new Config.RecordingConfig { PostSeconds = 1, MaxClipSeconds = 15 },
                    new Recording.RecordingSettings(dir))
                { WakeWindow = TimeSpan.FromSeconds(2) };
                using var cts = new CancellationTokenSource();
                var run = Task.Run(() => recorder.RunAsync(cts.Token));
                bool WaitUntil(Func<bool> cond, int ms)
                {
                    var until = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < until)
                    {
                        if (cond()) return true;
                        Thread.Sleep(25);
                    }
                    return cond();
                }

                recorder.OnMotion(new Protocol.MotionPush("wake", new[] { "wake" }, External: true));
                Assert(WaitUntil(() => store.List("holdcam").Count == 1, 5000), "tentative opened");
                recorder.OnMotion(new Protocol.MotionPush("none", Array.Empty<string>()));
                Thread.Sleep(2200);
                Assert(store.List("holdcam").Count == 1 && store.List("holdcam")[0].Ongoing,
                    "a camera all-clear must not cut the wake window short");
                recorder.OnMotion(new Protocol.MotionPush("none", Array.Empty<string>(), External: true));
                Assert(WaitUntil(() => store.List("holdcam").Count == 0, 8000),
                    "the synthetic closer still ends and discards the unconfirmed wake");
                cts.Cancel();
                try { run.GetAwaiter().GetResult(); } catch { }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
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

                // Dates: one day, a range that holds it, and ranges either side.
                var today = DateTime.Now.Date;
                AssertEq(store2.List(localDate: today).Count, 1);
                AssertEq(store2.List(localDate: today.AddDays(-1)).Count, 0);
                AssertEq(store2.List(localDate: today.AddDays(-6), localTo: today).Count, 1);
                AssertEq(store2.List(localTo: today).Count, 1);
                AssertEq(store2.List(localTo: today.AddDays(-1)).Count, 0);
                AssertEq(store2.List(localDate: today.AddDays(1), localTo: today.AddDays(2)).Count, 0);

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

                // The field scenario (reported on 0.9.9): a camera runs for 60 days
                // at 30-day retention, then the owner shrinks the window to 5. The
                // whole 25-day backlog must go on the very NEXT pass — retention
                // judges what is on disk now, it does not grandfather footage
                // recorded under the old number. Driven through the per-camera
                // policy overload, which is the path the UI's per-camera field uses.
                var shrink = "camShrink";
                var backlog = new List<string>();
                for (int ago = 1; ago <= 60; ago++)
                {
                    var d = DayDir(dir, shrink, DateTime.Now.Date.AddDays(-ago), "detections");
                    Directory.CreateDirectory(Path.Combine(d, $"1200{ago:00}-aaaa"));
                    File.WriteAllText(Path.Combine(d, $"1200{ago:00}-aaaa", "event.json"), "{}");
                    backlog.Add(d);
                }
                // Old policy: nothing older than 30 days existed to collect anyway.
                store2.Cleanup(cam => cam == shrink ? (30, 30) : (3650, 3650));
                Assert(Directory.Exists(backlog[0]) && Directory.Exists(backlog[28]),
                    "30-day window keeps the first 29 days");
                Assert(!Directory.Exists(backlog[59]), "30-day window already collected day 60");
                // The shrink: 30 -> 5, one pass, no restart.
                store2.Cleanup(cam => cam == shrink ? (5, 5) : (3650, 3650));
                Assert(Directory.Exists(backlog[0]) && Directory.Exists(backlog[3]),
                    "days inside the new 5-day window survive");
                for (int ago = 6; ago <= 30; ago++)
                    Assert(!Directory.Exists(backlog[ago - 1]),
                        $"day {ago} (recorded under the old 30-day window) is collected by the new 5-day one");

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

        Test("event list: date-scoped cap + wake exclusion before the limit", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            try
            {
                var store = new Recording.EventStore(dir);
                // Captured once: the assertions and the created events must agree on
                // "today" even if this runs across midnight.
                var day = DateTime.Today;

                // A day busier than a caller's small cap: a date-scoped query must
                // return the day whole, and an undated search asking big must see
                // past it too (search filters run over the WHOLE retained index).
                var baseUtc = day.AddHours(1).ToUniversalTime();
                for (int i = 0; i < 1005; i++)
                    store.Create("busy", baseUtc.AddSeconds(i), new[] { "motion" });
                AssertEq(store.List(camera: "busy", limit: 10_000, localDate: day).Count, 1005);
                AssertEq(store.List(camera: "busy", limit: 10_000).Count, 1005);
                AssertEq(store.List(camera: "busy", limit: 200).Count, 200);

                // Wake-only records excluded inside the limit: newest-first the list
                // is [3 wake, 3 person], so a post-limit filter would return only
                // one real event where three are asked for.
                var wakeBase = day.AddHours(3).ToUniversalTime();
                for (int i = 0; i < 3; i++)
                    store.Create("batt", wakeBase.AddSeconds(i), new[] { "person" });
                for (int i = 0; i < 3; i++)
                    store.Create("batt", wakeBase.AddSeconds(10 + i), new[] { "wake" });
                var top3 = store.List(camera: "batt", limit: 3, excludeWakeOnly: true);
                AssertEq(top3.Count, 3);
                Assert(top3.All(r => !(r.Labels is ["wake"])), "wake-only rows never reach the reply");
                Assert(store.List(camera: "batt", limit: 3).All(r => r.Labels is ["wake"]),
                    "the default listing still surfaces wake records (HA's last-event sensor)");

                // "wake" beside a real label is a real event, not a wake-only record.
                var mixed = store.Create("batt", wakeBase.AddSeconds(20), new[] { "wake", "person" });
                AssertEq(store.List(camera: "batt", limit: 1, excludeWakeOnly: true)[0].Id, mixed.Id);
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
                Assert(store.Verify("nobody", "any password") == null,
                    "unknown user fails closed (against the one-pass timing-equalizer hash)");
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

        Test("sign-in protection: lockout, spray blocking, expiry", () =>
        {
            var settings = new Web.LoginGuardSettings { Enabled = false, MaxAttempts = 3, LockMinutes = 10 };
            var guard = new Web.LoginGuard(() => settings);
            var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            guard.Clock = () => now;

            for (int i = 0; i < 10; i++) guard.RecordFailure("admin", "10.0.0.9");
            Assert(!guard.Blocked("admin", "10.0.0.9", out _), "disabled guard never blocks");

            settings.Enabled = true;
            guard.RecordFailure("admin", "10.0.0.9");
            guard.RecordFailure("admin", "10.0.0.9");
            Assert(!guard.Blocked("admin", "10.0.0.9", out _), "below the limit stays open");
            guard.RecordFailure("admin", "10.0.0.9");
            Assert(guard.Blocked("admin", "10.0.0.9", out var retry), "reaching the limit locks the account");
            Assert(retry is > 0 and <= 600, "retry-after spans the lock window");
            Assert(guard.Blocked("ADMIN", null, out _), "the lock is case-insensitive and address-independent");
            Assert(guard.LockedAccounts().Count == 1, "a locked account is listed for the UI");

            now = now.AddMinutes(11);
            Assert(!guard.Blocked("admin", "10.0.0.9", out _), "a lock expires on its own");

            // Password spraying: no single account reaches its own limit, so the
            // address must trip instead.
            for (int i = 0; i < 12; i++) guard.RecordFailure($"ghost{i}", "10.0.0.66");
            Assert(guard.Blocked("admin", "10.0.0.66", out _), "a spraying address is blocked for every account");
            Assert(!guard.Blocked("admin", "10.0.0.1", out _), "other addresses are unaffected");
            Assert(guard.BlockedAddressCount() == 1, "the blocked address is counted for the UI");

            guard.RecordFailure("viewer", "10.0.0.1");
            guard.RecordFailure("viewer", "10.0.0.1");
            guard.RecordSuccess("viewer");
            guard.RecordFailure("viewer", "10.0.0.1");
            Assert(!guard.Blocked("viewer", "10.0.0.1", out _), "a correct sign-in clears the account's slate");

            guard.UnlockAll();
            Assert(guard.LockedAccounts().Count == 0 && guard.BlockedAddressCount() == 0,
                "unlock-all forgives accounts and addresses alike");

            // The submitted username becomes a durable map key and a log line.
            AssertEq(Web.LoginGuard.TrackKey("admin"), "admin");
            Assert(!Web.LoginGuard.TrackKey("eve\n[WRN] Sign-in protection: address 8.8.8.8 blocked")
                    .Contains('\n'),
                "a newline in a username cannot forge a log line");
            Assert(!Web.LoginGuard.TrackKey("eve\r\n\tx").Any(char.IsControl),
                "every control character is neutralised, not just newlines");
            AssertEq(Web.LoginGuard.TrackKey(new string('A', 200_000)).Length, 64);
            AssertEq(Web.LoginGuard.TrackKey(null), "");

            // Behind a local proxy the forwarded header names the real client;
            // only its last hop is trustworthy.
            static string? Client(string peer, string? xff) =>
                Web.LoginGuard.ClientAddress(System.Net.IPAddress.Parse(peer), xff);
            AssertEq(Client("127.0.0.1", "203.0.113.7"), "203.0.113.7");
            AssertEq(Client("172.30.32.2", "203.0.113.7"), "203.0.113.7");
            AssertEq(Client("127.0.0.1", "1.2.3.4, 203.0.113.7"), "203.0.113.7");
            AssertEq(Client("198.51.100.9", "203.0.113.7"), "198.51.100.9");
            AssertEq(Client("127.0.0.1", null), "127.0.0.1");
            AssertEq(Client("127.0.0.1", "not-an-address"), "127.0.0.1");
            AssertEq(Client("127.0.0.1", "203.0.113.7, junk"), "127.0.0.1");
            AssertEq(Client("::ffff:198.51.100.9", null), "198.51.100.9");
            Assert(Web.LoginGuard.ClientAddress(null, "203.0.113.7") == null,
                "no peer address means nothing to hold responsible");

            // Settings persist in users.json, clamped to sane bounds.
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Web.UserStore(dir);
                var defaults = store.GetSecurity();
                Assert(!defaults.Enabled && defaults.MaxAttempts == 10,
                    "sign-in protection is opt-in: off until asked for, 10 attempts when on");
                store.SetSecurity(new Web.LoginGuardSettings { Enabled = true, MaxAttempts = 99, LockMinutes = 0 });
                var reloaded = new Web.UserStore(dir).GetSecurity();
                Assert(reloaded.Enabled, "turning it on persists across restart");
                AssertEq(reloaded.MaxAttempts, 20);
                AssertEq(reloaded.LockMinutes, 1);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("every shipped language catalogue parses", () =>
        {
            // A malformed catalogue is caught and returns empty, so the language
            // silently renders in English instead of failing: nothing but a count
            // tells the difference.
            foreach (var lang in WebClient.Localization.Lang.All)
            {
                if (lang.Code == WebClient.Localization.Lang.Default) continue;
                Assert(WebClient.Localization.Translations.Count(lang.Code) > 500,
                    $"{lang.English} catalogue parses and is populated");
                Assert(WebClient.Localization.Translations.Get(lang.Code, "Enable recording") != "Enable recording",
                    $"{lang.English} translates a key added by hand this cycle");
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
            Assert(header.Contains("mustUnderstand=\"1\"", StringComparison.Ordinal), "the Reolink fallback's header is unchanged");
            // Generic cameras: no mustUnderstand (as zeep sends it), and the plain-text fallback.
            var unflagged = Protocol.OnvifClient.BuildSecurity("admin", "s<cr&t", nonce, created, mustUnderstand: false);
            Assert(!unflagged.Contains("mustUnderstand", StringComparison.Ordinal), "no mustUnderstand for a generic camera");
            var text = Protocol.OnvifClient.BuildSecurity("admin", "s<cr&t", nonce, created, mustUnderstand: false, plainText: true);
            Assert(text.Contains("#PasswordText\">s&lt;cr&amp;t</wsse:Password>", StringComparison.Ordinal),
                "a plain-text password is sent escaped, as PasswordText");
            System.Xml.Linq.XDocument.Parse($"<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Header>{text}</s:Header></s:Envelope>");

            // How long a subscription was granted, in the camera's own clock.
            var created2 = System.Xml.Linq.XDocument.Parse("""
                <tev:CreatePullPointSubscriptionResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                    xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
                  <wsnt:CurrentTime>2026-01-02T03:04:05Z</wsnt:CurrentTime>
                  <wsnt:TerminationTime>2026-01-02T03:04:15Z</wsnt:TerminationTime>
                </tev:CreatePullPointSubscriptionResponse>
                """).Root!;
            AssertEq(Protocol.OnvifClient.GrantedPeriod(created2), TimeSpan.FromSeconds(10));
            // A Renew reply may leave CurrentTime out: measured against the camera's clock as we know it.
            var renewed = System.Xml.Linq.XDocument.Parse("""
                <wsnt:RenewResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
                  <wsnt:TerminationTime>2026-01-02T03:05:05Z</wsnt:TerminationTime>
                </wsnt:RenewResponse>
                """).Root!;
            AssertEq(Protocol.OnvifClient.GrantedPeriod(renewed, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
                TimeSpan.FromSeconds(60));
            AssertEq(Protocol.OnvifClient.GrantedPeriod(renewed), null);
            AssertEq(Streaming.OnvifEventService.RenewAfter(TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(8));
            AssertEq(Streaming.OnvifEventService.RenewAfter(TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(5));
            // A bare IPv6 address becomes a URL with its brackets.
            AssertEq(Protocol.OnvifClient.BuildCandidates("fe80::1", new[] { 80, 8000 })[1],
                "http://[fe80::1]:8000/onvif/device_service");

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
            // A non-Reolink camera passes its own order — 80 first, and 8899 too.
            var generic = Protocol.OnvifClient.BuildCandidates("10.1.1.29", new[] { 80, 8000, 8899 });
            Assert(generic.Length == 3 && generic[0] == "http://10.1.1.29/onvif/device_service"
                   && generic[1] == "http://10.1.1.29:8000/onvif/device_service"
                   && generic[2] == "http://10.1.1.29:8899/onvif/device_service",
                   "a caller's port order is honoured, and :80 stays implicit");
            Assert(Protocol.OnvifClient.BuildCandidates("10.1.1.29:8899", new[] { 80, 8000 }).Length == 1,
                "an explicit port still wins over any list");
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

        Test("ONVIF services: device info, profiles, encoder options, presets, OSDs", () =>
        {
            static System.Xml.Linq.XElement Xml(string s) => System.Xml.Linq.XDocument.Parse(s).Root!;

            var info = Protocol.OnvifClient.ParseDeviceInfo(Xml("""
                <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <tds:Manufacturer>Hikvision</tds:Manufacturer>
                  <tds:Model>DS-2CD2143G2</tds:Model>
                  <tds:FirmwareVersion>V5.7.3</tds:FirmwareVersion>
                  <tds:SerialNumber>DS0123456789</tds:SerialNumber>
                  <tds:HardwareId>88</tds:HardwareId>
                </tds:GetDeviceInformationResponse>
                """));
            Assert(info is { Manufacturer: "Hikvision", Model: "DS-2CD2143G2", Firmware: "V5.7.3",
                Serial: "DS0123456789", HardwareId: "88" }, "device information parsed");
            // The identity strip shows one model line, so the make is folded in.
            var version = Streaming.GenericCameraControl.ToVersion(info);
            AssertEq(version?.Model, "Hikvision DS-2CD2143G2");
            AssertEq(version?.FirmwareVersion, "V5.7.3");
            Assert(Streaming.GenericCameraControl.ToVersion(null) == null, "no device info -> no version");
            Assert(Streaming.GenericCameraControl.ToVersion(
                new Protocol.OnvifDeviceInfo(null, null, null, null, null)) == null,
                "an empty device info is not an identity");

            // GetServices is the ver10 alternative to GetCapabilities; a camera that
            // answers only that one must still yield its service endpoints.
            var services = Xml("""
                <tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <tds:Service>
                    <tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace>
                    <tds:XAddr>http://cam/onvif/Media</tds:XAddr>
                  </tds:Service>
                  <tds:Service>
                    <tds:Namespace>http://www.onvif.org/ver20/ptz/wsdl</tds:Namespace>
                    <tds:XAddr>http://cam/onvif/PTZ</tds:XAddr>
                  </tds:Service>
                </tds:GetServicesResponse>
                """);
            AssertEq(Protocol.OnvifClient.ServiceXAddrByNs(services, "http://www.onvif.org/ver10/media/wsdl"),
                "http://cam/onvif/Media");
            AssertEq(Protocol.OnvifClient.ServiceXAddrByNs(services, "http://www.onvif.org/ver20/ptz/wsdl"),
                "http://cam/onvif/PTZ");
            Assert(Protocol.OnvifClient.ServiceXAddrByNs(services, "http://www.onvif.org/ver20/imaging/wsdl") == null,
                "a service the camera did not list is absent");

            // GetProfiles: the encoder configuration is kept verbatim because a
            // write echoes the camera's own object back with the changed fields.
            var profiles = Protocol.OnvifClient.ParseProfiles(Xml("""
                <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                         xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:Profiles token="Profile_1" fixed="true">
                    <tt:Name>mainstream</tt:Name>
                    <tt:VideoSourceConfiguration token="VSC_1"/>
                    <tt:VideoEncoderConfiguration token="VEC_1">
                      <tt:Encoding>H264</tt:Encoding>
                      <tt:Resolution><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:Resolution>
                      <tt:Quality>4</tt:Quality>
                      <tt:RateControl>
                        <tt:FrameRateLimit>20</tt:FrameRateLimit>
                        <tt:BitrateLimit>4096</tt:BitrateLimit>
                      </tt:RateControl>
                      <tt:H264><tt:GovLength>40</tt:GovLength></tt:H264>
                    </tt:VideoEncoderConfiguration>
                    <tt:PTZConfiguration token="PTZ_1"/>
                  </trt:Profiles>
                  <trt:Profiles token="Profile_2">
                    <tt:Name>substream</tt:Name>
                    <tt:VideoEncoderConfiguration token="VEC_2">
                      <tt:Encoding>H265</tt:Encoding>
                      <tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution>
                      <tt:RateControl>
                        <tt:FrameRateLimit>15</tt:FrameRateLimit>
                        <tt:BitrateLimit>512</tt:BitrateLimit>
                      </tt:RateControl>
                      <tt:H265><tt:GovLength>30</tt:GovLength></tt:H265>
                    </tt:VideoEncoderConfiguration>
                  </trt:Profiles>
                </trt:GetProfilesResponse>
                """));
            AssertEq(profiles.Count, 2);
            Assert(profiles[0] is { Token: "Profile_1", Name: "mainstream", HasPtz: true,
                VideoSourceToken: "VSC_1" }, "first profile parsed with its PTZ configuration");
            Assert(profiles[0].EncoderToken == "VEC_1" && profiles[0].Encoding == "H264"
                   && profiles[0].Width == 2560 && profiles[0].Height == 1440
                   && profiles[0].FrameRate == 20 && profiles[0].Bitrate == 4096
                   && profiles[0].GovLength == 40, "encoder fields read off the configuration");
            Assert(!profiles[1].HasPtz, "a profile without a PTZ configuration is not a moving head");
            // The codec block is H265 here: GovLength must not be looked for in H264 only.
            Assert(profiles[1].GovLength == 30, "H265 profiles report their GOV length too");

            var encOptions = Protocol.OnvifClient.ParseEncoderOptions(Xml("""
                <trt:GetVideoEncoderConfigurationOptionsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                                                xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:Options>
                    <tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>
                    <tt:H264>
                      <tt:ResolutionsAvailable><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:ResolutionsAvailable>
                      <tt:ResolutionsAvailable><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:ResolutionsAvailable>
                      <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>25</tt:Max></tt:FrameRateRange>
                    </tt:H264>
                    <tt:Extension><tt:H264>
                      <tt:BitrateRange><tt:Min>256</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange>
                    </tt:H264></tt:Extension>
                  </trt:Options>
                </trt:GetVideoEncoderConfigurationOptionsResponse>
                """));
            // Biggest first: the panel lists resolutions the way a person reads them.
            Assert(encOptions.Resolutions.Count == 2 && encOptions.Resolutions[0] == (2560, 1440)
                   && encOptions.Resolutions[1] == (640, 360), "resolutions parsed, largest first");
            Assert(encOptions.FrameRate == (1, 25), "frame-rate range parsed");
            // The bitrate range hides under Extension on most firmwares.
            Assert(encOptions.Bitrate == (256, 8192), "bitrate range found under Extension");

            // Options are scoped to the codec the profile actually uses. A reply
            // carries a block per codec and they do not agree, so offering an H264
            // profile its H265 sibling's resolutions means the camera refuses the
            // write the user just made.
            var twoCodecs = Xml("""
                <trt:GetVideoEncoderConfigurationOptionsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                                                xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:Options>
                    <tt:H264>
                      <tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>
                      <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>25</tt:Max></tt:FrameRateRange>
                    </tt:H264>
                    <tt:H265>
                      <tt:ResolutionsAvailable><tt:Width>3840</tt:Width><tt:Height>2160</tt:Height></tt:ResolutionsAvailable>
                      <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>15</tt:Max></tt:FrameRateRange>
                    </tt:H265>
                    <tt:Extension><tt:H264>
                      <tt:BitrateRange><tt:Min>128</tt:Min><tt:Max>4096</tt:Max></tt:BitrateRange>
                    </tt:H264></tt:Extension>
                  </trt:Options>
                </trt:GetVideoEncoderConfigurationOptionsResponse>
                """);
            var h264 = Protocol.OnvifClient.ParseEncoderOptions(twoCodecs, "H264");
            Assert(h264.Resolutions.Count == 1 && h264.Resolutions[0] == (1920, 1080),
                "an H264 profile is offered only H264 resolutions");
            Assert(h264.FrameRate == (1, 25), "and its own frame-rate range");
            var h265 = Protocol.OnvifClient.ParseEncoderOptions(twoCodecs, "H265");
            Assert(h265.Resolutions.Count == 1 && h265.Resolutions[0] == (3840, 2160)
                   && h265.FrameRate == (1, 15), "and H265 gets H265's");
            // The bitrate range lives outside the codec block, so it is still found.
            Assert(h265.Bitrate == (128, 4096), "a range outside the codec block still applies");
            // An unknown codec falls back to the whole document rather than nothing.
            Assert(Protocol.OnvifClient.ParseEncoderOptions(twoCodecs, "MJPEG").Resolutions.Count == 2,
                "an unrecognised encoding sees everything, as before");

            var presets = Protocol.OnvifClient.ParsePresets(Xml("""
                <tptz:GetPresetsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                         xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:Preset token="1"><tt:Name>Driveway</tt:Name></tptz:Preset>
                  <tptz:Preset token="2"><tt:Name>Gate</tt:Name></tptz:Preset>
                  <tptz:Preset><tt:Name>no token</tt:Name></tptz:Preset>
                </tptz:GetPresetsResponse>
                """));
            Assert(presets.Count == 2 && presets[0].Token == "1" && presets[0].Name == "Driveway"
                   && presets[1].Name == "Gate", "presets parsed; a token-less one is unusable and dropped");

            var osds = Protocol.OnvifClient.ParseOsds(Xml("""
                <trt:GetOSDsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                     xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:OSDs token="OSD_1">
                    <tt:Type>Text</tt:Type>
                    <tt:Position><tt:Type>UpperLeft</tt:Type></tt:Position>
                    <tt:TextString><tt:Type>Plain</tt:Type><tt:PlainText>Front Gate</tt:PlainText></tt:TextString>
                  </trt:OSDs>
                  <trt:OSDs token="OSD_2">
                    <tt:Type>Text</tt:Type>
                    <tt:Position><tt:Type>LowerRight</tt:Type></tt:Position>
                    <tt:TextString><tt:Type>DateAndTime</tt:Type></tt:TextString>
                  </trt:OSDs>
                  <trt:OSDs token="OSD_3"><tt:Type>Image</tt:Type></trt:OSDs>
                </trt:GetOSDsResponse>
                """));
            Assert(osds.Count == 2, "image overlays have nothing to edit and are left out");
            var (osdName, osdTime) = Streaming.GenericCameraControl.SplitOsds(osds);
            Assert(osdName is { Token: "OSD_1", Position: "UpperLeft", PlainText: "Front Gate" },
                "the plain-text overlay is the camera name");
            Assert(osdTime is { Token: "OSD_2", Position: "LowerRight" },
                "the date/time overlay is the timestamp");

            // A login inside the ONVIF address wins over the streaming one — the only
            // way to reach a camera that keeps separate accounts. Escapes are decoded,
            // so a password with an @ in it survives the URL it had to be written into.
            var (addr, user, pass) = Protocol.OnvifClient.SplitCredentials(
                "http://admin:p%40ss%2Fword@10.1.1.7:8000/onvif/device_service");
            AssertEq(addr, "http://10.1.1.7:8000/onvif/device_service");
            AssertEq(user, "admin");
            AssertEq(pass, "p@ss/word");
            var plain = Protocol.OnvifClient.SplitCredentials("10.1.1.7:8000");
            Assert(plain is { Address: "10.1.1.7:8000", User: null, Pass: null },
                "a bare host:port carries no login");
            // A bare AUTHORITY can carry one too. It used to fall through whole, so
            // the password ended up treated as part of the hostname and printed into
            // the log; both halves have to be split off here.
            var bare = Protocol.OnvifClient.SplitCredentials("admin:s3cret@10.1.1.7:8000");
            Assert(bare is { Address: "10.1.1.7:8000", User: "admin", Pass: "s3cret" },
                "a login in a bare authority is split off, not left in the host");
            Assert(Protocol.OnvifClient.SplitCredentials("admin@cam.lan")
                is { Address: "cam.lan", User: "admin", Pass: null }, "a username with no password");
            // ...and the admin API must not hand that password to the browser.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("admin:s3cret@10.1.1.7:8000"),
                "admin:****@10.1.1.7:8000");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("http://admin:s3cret@cam/onvif/device_service"),
                "http://admin:****@cam/onvif/device_service");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("10.1.1.7:8000"), "10.1.1.7:8000");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("http://cam:8000/onvif"), "http://cam:8000/onvif");

            // The stream URL is where a generic camera keeps its host and login.
            var split = NetUtil.SplitRtspUrl("rtsp://bob:s%3Acret@cam.lan:554/Streaming/Channels/101");
            Assert(split is { Host: "cam.lan", Display: "cam.lan", User: "bob", Pass: "s:cret" },
                "RTSP URL split; the standard 554 is not worth showing");
            // The host is where ONVIF is looked for, so it never carries the stream's
            // port; the DISPLAY string does, or two cameras on one box look identical.
            var odd = NetUtil.SplitRtspUrl("rtsp://cam.lan:8554/live");
            Assert(odd is { Host: "cam.lan", Display: "cam.lan:8554" }, "a non-standard port shows, but only in Display");
            Assert(NetUtil.SplitRtspUrl("rtsp://[2001:db8::1]:8554/live") is { Display: "[2001:db8::1]:8554" },
                "an IPv6 literal keeps its brackets");
            Assert(NetUtil.SplitRtspUrl("rtsp://cam.lan/live").User == null, "no login in the URL -> none");
            Assert(NetUtil.SplitRtspUrl(null).Host == null, "no URL -> nothing");
        });

        Test("ONVIF camera: profiles bind to the streams Neolink pulls", () =>
        {
            static Protocol.OnvifProfile P(string token, int w, int h) =>
                new(token, token, false, null, System.Xml.Linq.XElement.Parse(
                    $"""
                     <VideoEncoderConfiguration xmlns="http://www.onvif.org/ver10/schema" token="{token}">
                       <Resolution><Width>{w}</Width><Height>{h}</Height></Resolution>
                       <RateControl><FrameRateLimit>15</FrameRateLimit><BitrateLimit>1024</BitrateLimit></RateControl>
                     </VideoEncoderConfiguration>
                     """));

            var streams = new[]
            {
                ("mainStream", "rtsp://user:pw@10.1.1.7:554/Streaming/Channels/101"),
                ("subStream", "rtsp://user:pw@10.1.1.7:554/Streaming/Channels/102"),
            };
            var profiles = new[] { P("small", 640, 360), P("big", 2560, 1440) };

            // The camera reports its own URIs on its own hostname; only the path is
            // comparable, and the ORDER of the profiles means nothing.
            var uris = new Dictionary<string, string>
            {
                ["big"] = "rtsp://cam.lan:554/Streaming/Channels/101",
                ["small"] = "rtsp://cam.lan:554/Streaming/Channels/102",
            };
            var bound = Streaming.GenericCameraControl.Bind(streams, profiles, uris);
            AssertEq(bound.Count, 2);
            Assert(bound[0].Kind == "mainStream" && bound[0].Profile.Token == "big",
                "the profile whose URI matches rtsp_main is mainStream, whatever its position");
            Assert(bound[1].Kind == "subStream" && bound[1].Profile.Token == "small", "and likewise the sub");

            // No URIs to match on (a camera that will not answer GetStreamUri, or a
            // hand-written URL): biggest first, which is how the config was written.
            var guessed = Streaming.GenericCameraControl.Bind(streams, profiles, null);
            Assert(guessed[0].Profile.Token == "big" && guessed[1].Profile.Token == "small",
                "unmatched profiles fall back to biggest-first in the streams' own order");

            Assert(Streaming.GenericCameraControl.SameStream(
                "rtsp://cam.lan:554/live/main/", "rtsp://u:p@10.0.0.9/live/main"), "host and login ignored");
            // Cameras append their own query to the URI they report (session ids,
            // transport hints) that nobody would have typed into a config. Comparing
            // those would mean no profile ever matched and every camera fell back to
            // guessing by frame size.
            Assert(Streaming.GenericCameraControl.SameStream(
                "rtsp://cam.lan/live/main?tcp&session=42", "rtsp://cam.lan/live/main"),
                "a query the camera added does not break the match");
            Assert(!Streaming.GenericCameraControl.SameStream(
                "rtsp://cam.lan/live/main", "rtsp://cam.lan/live/sub"), "different paths are different streams");
            Assert(!Streaming.GenericCameraControl.SameStream(null, "rtsp://cam/live"), "nothing matches nothing");
            // Dahua and its OEMs tell their streams apart by QUERY alone: the configured
            // URL's parameters must all be there, or every profile would match every stream.
            Assert(Streaming.GenericCameraControl.SameStream(
                "rtsp://cam.lan:554/cam/realmonitor?channel=1&subtype=1",
                "rtsp://u:p@10.0.0.9/cam/realmonitor?channel=1&subtype=1"), "the same Dahua query matches");
            Assert(!Streaming.GenericCameraControl.SameStream(
                "rtsp://cam.lan:554/cam/realmonitor?channel=1&subtype=0",
                "rtsp://u:p@10.0.0.9/cam/realmonitor?channel=1&subtype=1"), "another subtype is another stream");
            Assert(!Streaming.GenericCameraControl.SameStream(
                "rtsp://cam.lan:554/cam/realmonitor?channel=1&subtype=0",
                "rtsp://u:p@10.0.0.9/cam/realmonitor?channel=3&subtype=0"), "another channel is another camera");
            var dahua = new[] { P("MediaProfile00000", 2688, 1520), P("MediaProfile00001", 704, 576) };
            var dahuaUris = new Dictionary<string, string>
            {
                ["MediaProfile00000"] = "rtsp://cam.lan:554/cam/realmonitor?channel=1&subtype=0",
                ["MediaProfile00001"] = "rtsp://cam.lan:554/cam/realmonitor?channel=1&subtype=1",
            };
            var subOnly = Streaming.GenericCameraControl.Bind(
                new[] { ("subStream", "rtsp://u:p@10.0.0.9/cam/realmonitor?channel=1&subtype=1") }, dahua, dahuaUris);
            Assert(subOnly is [{ Kind: "subStream", Profile.Token: "MediaProfile00001" }],
                "a sub-only Dahua config binds the sub profile, not the first one listed");
            // With nothing to match on, a sub stream is guessed as the SMALLEST profile:
            // handed the main one, a sub-only config would edit the main encoder.
            var subGuess = Streaming.GenericCameraControl.Bind(
                new[] { ("subStream", "rtsp://u:p@10.0.0.9/h264/ch1/sub/av_stream") }, profiles, null);
            Assert(subGuess is [{ Kind: "subStream", Profile.Token: "small" }],
                "an unmatched sub stream is guessed as the smallest profile");

            // An NVR: frame size is only a guess within the channel a URI matched, never across channels.
            static Protocol.OnvifProfile C(string token, int w, int h, string source) =>
                P(token, w, h) with { SourceToken = source };
            var nvr = new[] { C("1m", 2560, 1440, "VS_1"), C("1s", 640, 360, "VS_1"),
                              C("3m", 1920, 1080, "VS_3"), C("3s", 704, 576, "VS_3") };
            var ch3 = new[] { ("mainStream", "rtsp://nvr/h264/ch3/main"), ("subStream", "rtsp://nvr/h264/ch3/sub") };
            var nvrUris = new Dictionary<string, string> { ["3m"] = "rtsp://nvr.lan/h264/ch3/main" };
            var mixed = Streaming.GenericCameraControl.Bind(ch3, nvr, nvrUris);
            Assert(mixed is [{ Profile.Token: "3m" }, { Profile.Token: "3s" }],
                "the unmatched sub stream is guessed from the matched channel only");
            AssertEq(Streaming.GenericCameraControl.Bind(ch3, nvr, null).Count, 0);

            // ONVIF reports a RANGE where the panel wants a menu.
            var fps = Streaming.GenericCameraControl.StepsWithin(
                Streaming.GenericCameraControl.FramerateSteps, (1, 25), current: 20);
            Assert(fps.Contains(20) && fps.Contains(25) && !fps.Contains(30), "menu stays inside the range");
            // A camera set to something off the everyday list still shows its own value.
            var odd = Streaming.GenericCameraControl.StepsWithin(
                Streaming.GenericCameraControl.FramerateSteps, (1, 25), current: 7);
            Assert(odd.Contains(7) && odd.SequenceEqual(odd.OrderBy(v => v)), "the live value is always offered, in order");
            // A range so narrow nothing everyday fits must still offer something.
            var narrow = Streaming.GenericCameraControl.StepsWithin(
                Streaming.GenericCameraControl.FramerateSteps, (97, 99), current: 0);
            Assert(narrow.Count > 0, "a narrow range falls back to its own bounds");
        });

        Test("ONVIF events: subscription address, notifications, topic mapping", () =>
        {
            static System.Xml.Linq.XElement Xml(string s) => System.Xml.Linq.XDocument.Parse(s).Root!;

            // The subscription manager's address is NOT the event service's own URL:
            // cameras hand back a different path, and some a different host and port.
            var created = Xml("""
                <tev:CreatePullPointSubscriptionResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                                         xmlns:wsa="http://www.w3.org/2005/08/addressing">
                  <tev:SubscriptionReference>
                    <wsa:Address>http://cam:8000/onvif/Subscription?Idx=7</wsa:Address>
                  </tev:SubscriptionReference>
                  <wsa:Address>http://wrong/should-not-be-picked</wsa:Address>
                </tev:CreatePullPointSubscriptionResponse>
                """);
            AssertEq(Protocol.OnvifClient.SubscriptionAddress(created), "http://cam:8000/onvif/Subscription?Idx=7");
            Assert(Protocol.OnvifClient.SubscriptionAddress(
                Xml("""<tev:X xmlns:tev="http://www.onvif.org/ver10/events/wsdl"/>""")) == null,
                "no reference -> no subscription");

            // Timings go on the wire as xs:duration and nothing else.
            AssertEq(Protocol.OnvifClient.Duration(TimeSpan.FromSeconds(60)), "PT60S");
            AssertEq(Protocol.OnvifClient.Duration(TimeSpan.FromMilliseconds(200)), "PT1S");

            var pulled = Xml("""
                <tev:PullMessagesResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                          xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2"
                                          xmlns:tt="http://www.onvif.org/ver10/schema">
                  <wsnt:NotificationMessage>
                    <wsnt:Topic>tns1:RuleEngine/CellMotionDetector/Motion</wsnt:Topic>
                    <wsnt:Message><tt:Message UtcTime="2026-01-02T03:04:05Z">
                      <tt:Source><tt:SimpleItem Name="VideoSourceConfigurationToken" Value="VSC_1"/></tt:Source>
                      <tt:Data><tt:SimpleItem Name="IsMotion" Value="true"/></tt:Data>
                    </tt:Message></wsnt:Message>
                  </wsnt:NotificationMessage>
                  <wsnt:NotificationMessage>
                    <wsnt:Topic>tns1:RuleEngine/FieldDetector/ObjectsInside</wsnt:Topic>
                    <wsnt:Message><tt:Message UtcTime="2026-01-02T03:04:06Z">
                      <tt:Data>
                        <tt:SimpleItem Name="IsInside" Value="true"/>
                        <tt:SimpleItem Name="ObjectType" Value="Human"/>
                      </tt:Data>
                    </tt:Message></wsnt:Message>
                  </wsnt:NotificationMessage>
                  <wsnt:NotificationMessage>
                    <wsnt:Topic>tns1:Device/HardwareFailure/StorageFailure</wsnt:Topic>
                    <wsnt:Message><tt:Message UtcTime="2026-01-02T03:04:07Z">
                      <tt:Data><tt:SimpleItem Name="Failed" Value="false"/></tt:Data>
                    </tt:Message></wsnt:Message>
                  </wsnt:NotificationMessage>
                  <wsnt:NotificationMessage>
                    <wsnt:Topic>tns1:RuleEngine/CellMotionDetector/Motion</wsnt:Topic>
                    <wsnt:Message><tt:Message UtcTime="2026-01-02T03:04:08Z" PropertyOperation="Deleted">
                      <tt:Source><tt:SimpleItem Name="VideoSourceConfigurationToken" Value="VSC_1"/></tt:Source>
                      <tt:Data><tt:SimpleItem Name="IsMotion" Value="true"/></tt:Data>
                    </tt:Message></wsnt:Message>
                  </wsnt:NotificationMessage>
                </tev:PullMessagesResponse>
                """);
            var notes = Protocol.OnvifClient.ParseNotifications(pulled);
            AssertEq(notes.Count, 4);
            // The camera withdrawing a property is read as such, and only then.
            Assert(notes[3].Deleted && !notes[0].Deleted, "PropertyOperation=Deleted is read off the message");
            Assert(notes[0] is { Active: true } && notes[0].Label == "motion",
                "the ONVIF motion rule is motion");
            // Classification beats the rule that carried it: a field-intrusion rule
            // that says what walked in is worth more than "something moved".
            Assert(notes[1] is { Active: true } && notes[1].Label == "person",
                "an ObjectType of Human makes it a person");
            // A subscription carries no topic filter, so the great majority of what
            // arrives is housekeeping and must not become an event.
            Assert(!notes[2].IsDetection, "a storage-failure notification is not a detection");

            // The end of an event, however the camera names its state item.
            var ended = Protocol.OnvifClient.ParseNotifications(Xml("""
                <tev:PullMessagesResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                          xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2"
                                          xmlns:tt="http://www.onvif.org/ver10/schema">
                  <wsnt:NotificationMessage>
                    <wsnt:Topic>tns1:VideoSource/MotionAlarm</wsnt:Topic>
                    <wsnt:Message><tt:Message UtcTime="2026-01-02T03:04:08Z">
                      <tt:Data><tt:SimpleItem Name="State" Value="false"/></tt:Data>
                    </tt:Message></wsnt:Message>
                  </wsnt:NotificationMessage>
                </tev:PullMessagesResponse>
                """));
            Assert(ended.Count == 1 && ended[0] is { Active: false, Label: "motion" }, "the all-clear parses");

            // A notification with no state at all is a one-shot ("this happened"),
            // which the service treats as a start rather than discarding.
            Assert(Protocol.OnvifClient.ActiveFrom(new Dictionary<string, string>()) == null,
                "no state item -> the camera said neither");

            // Vehicles and animals, and a vendor topic with no classification.
            static Protocol.OnvifNotification N(string topic, params string[] kv)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
                return new Protocol.OnvifNotification(topic, Protocol.OnvifClient.ActiveFrom(d), d);
            }
            AssertEq(N("tns1:RuleEngine/MyRuleDetector/Detect", "ObjectType", "Vehicle").Label, "vehicle");
            AssertEq(N("tns1:RuleEngine/MyRuleDetector/Detect", "ObjectType", "Dog").Label, "animal");
            // A perimeter rule is its own label AND motion, so the default event-type
            // filter (which leaves the perimeter labels opt-in) still records it.
            var crossed = N("tns1:RuleEngine/LineDetector/Crossed", "State", "true");
            AssertEq(crossed.Label, "line-crossing");
            Assert(crossed.Labels.Contains("motion"), "a line crossing is also motion");
            AssertEq(N("tns1:RuleEngine/TamperDetector/Tamper", "State", "true").Label, null);
            AssertEq(N("tns1:Monitoring/ProcessorUsage", "Value", "42").Label, null);

            // What real cameras send, beyond the spec's own examples. Profile M's object
            // detection lists classes in a space-separated ClassTypes (spec spelling "Vehical").
            AssertEq(N("tns1:RuleEngine/MotionRegionDetector/Motion", "State", "true").Label, "motion");
            var objects = N("tns1:RuleEngine/ObjectDetection/Object", "ClassTypes", "Human Vehical");
            Assert(objects.Labels.Contains("person") && objects.Labels.Contains("vehicle"),
                "ObjectDetection's ClassTypes list yields every class in it");
            // Tapo: the class is folded into the state item's name, and a tamper is
            // an alarm but not a detection.
            AssertEq(N("tns1:RuleEngine/CellMotionDetector/People", "IsPeople", "true").Label, "person");
            var tapo = N("tns1:RuleEngine/CellMotionDetector/TpSmartEvent", "IsVehicle", "true", "IsPet", "false");
            Assert(tapo is { Active: true, Label: "vehicle" } && !tapo.Labels.Contains("animal"),
                "a Tapo smart event names what it saw in the item that is true");
            var tapoEnd = N("tns1:RuleEngine/CellMotionDetector/TpSmartEvent", "IsVehicle", "false", "IsPet", "false");
            Assert(tapoEnd is { Active: false, IsDetection: true }, "and every item false is the end of it");
            // A tamper item that is FALSE is not a tamper alarm, and does not silence
            // the detection it rides beside.
            AssertEq(N("tns1:RuleEngine/CellMotionDetector/TpSmartEvent", "IsTamper", "false", "IsPeople", "1").Label,
                "person");
            Assert(!N("tns1:RuleEngine/CellMotionDetector/Tamper", "IsTamper", "true").IsDetection,
                "a Tapo tamper alarm is not a detection");
            // Axis: VMD topics, and states written as 1/0 — valid xs:boolean both.
            Assert(N("tnsaxis:CameraApplicationPlatform/VMD/Camera1ProfileANY", "active", "1")
                is { Active: true, Label: "motion" }, "Axis VMD is motion, and '1' is true");
            Assert(N("tns1:VideoSource/MotionAlarm", "State", "0") is { Active: false, Label: "motion" },
                "'0' is false — an END, not a fresh start");
            // A word is matched at a word boundary: an interface is not a face, a
            // smart card is not a car, and a people counter reports a number.
            Assert(!N("tns1:Device/Network/InterfaceDown", "Value", "1").IsDetection, "InterfaceDown is not a face");
            AssertEq(N("tns1:RuleEngine/FaceDetector/HumanFace", "State", "true").Label, "face");
            Assert(!N("tns1:RuleEngine/SmartCard/Card", "State", "true").Labels.Contains("vehicle"), "a card is not a car");
            AssertEq(N("tns1:RuleEngine/CarDetector/CarDetect", "State", "true").Label, "vehicle");
            Assert(!N("tns1:RuleEngine/PeopleCounter/Count", "Count", "3").IsDetection, "a people counter is not a sighting");
            AssertEq(N("tns1:RuleEngine/CrossRegionDetector/CrossRegion", "State", "true").Label, "intrusion");
            AssertEq(N("tns1:UserAlarm/IVA/HumanShape", "State", "true").Label, "person");
            // A classification item on a housekeeping topic does not make it a detection.
            Assert(!N("tns1:Device/HardwareFailure/StorageFailure", "Type", "Bus").IsDetection,
                "Type=Bus on a storage notification is not a vehicle");
            // The speaker: topic plus the Source items, so two rules that both mean
            // "motion" keep separate state, and channel 2 cannot end channel 1.
            var src = new Dictionary<string, string> { ["VideoSourceConfigurationToken"] = "VSC_2" };
            var spoken = new Protocol.OnvifNotification("tns1:RuleEngine/CellMotionDetector/Motion", true,
                src, src);
            Assert(spoken.Key.Contains("VSC_2") && spoken.SourceToken == "VSC_2", "the key names the source");
            Assert(Protocol.OnvifClient.ParseNotifications(pulled)[0].SourceToken == "VSC_1",
                "the Source block's configuration token is the notification's source");

            // A subscription reference may carry parameters the camera needs echoed
            // back as headers (Axis identifies the subscription by nothing else).
            var referenced = Xml("""
                <tev:CreatePullPointSubscriptionResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                                         xmlns:wsa="http://www.w3.org/2005/08/addressing"
                                                         xmlns:dom0="http://www.axis.com/2009/event">
                  <tev:SubscriptionReference>
                    <wsa:Address>http://cam/onvif/services</wsa:Address>
                    <wsa:ReferenceParameters><dom0:SubscriptionId>7</dom0:SubscriptionId></wsa:ReferenceParameters>
                  </tev:SubscriptionReference>
                </tev:CreatePullPointSubscriptionResponse>
                """);
            var headers = Protocol.OnvifClient.ReferenceParameterHeaders(referenced);
            Assert(headers.Contains("SubscriptionId") && headers.Contains(">7<")
                   && headers.Contains("IsReferenceParameter=\"true\""),
                "reference parameters become header blocks marked as such");
            AssertEq(Protocol.OnvifClient.ReferenceParameterHeaders(created), "");
            // A fault that rides in on HTTP 200 is still a fault.
            Assert(Protocol.OnvifClient.SoapFault(Xml("""
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><s:Fault>
                  <s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode></s:Code>
                  <s:Reason><s:Text xml:lang="en">Sender not Authorized</s:Text></s:Reason>
                </s:Fault></s:Body></s:Envelope>
                """))?.Contains("NotAuthorized") == true, "a SOAP fault is recognised whatever the status");
            Assert(Protocol.OnvifClient.SoapFault(pulled) == null, "a reply is not a fault");
            // A Dahua-family firmware on a non-default port writes the port twice.
            AssertEq(Protocol.OnvifClient.NormalizeXAddr("http://192.168.1.106:8106:8106/onvif/Subscription?Idx=43",
                    "http://192.168.1.106:8106/onvif/device_service"),
                "http://192.168.1.106:8106/onvif/Subscription?Idx=43");

            // The camera's clock, which every request after discovery is stamped in.
            var clock = Protocol.OnvifClient.ParseUtcTime(Xml("""
                <tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                                                  xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tds:SystemDateAndTime>
                    <tt:UTCDateTime>
                      <tt:Time><tt:Hour>3</tt:Hour><tt:Minute>4</tt:Minute><tt:Second>5</tt:Second></tt:Time>
                      <tt:Date><tt:Year>2026</tt:Year><tt:Month>1</tt:Month><tt:Day>2</tt:Day></tt:Date>
                    </tt:UTCDateTime>
                  </tds:SystemDateAndTime>
                </tds:GetSystemDateAndTimeResponse>
                """));
            AssertEq(clock, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            // A camera that reports only local time, or nonsense, must not throw.
            Assert(Protocol.OnvifClient.ParseUtcTime(
                Xml("""<tds:R xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>""")) == null,
                "no UTC block -> no correction");
        });

        Test("a zone only moves to Neolink when the camera provably has none", () =>
        {
            // THE safety property of the whole feature. A Reolink camera holds its
            // zone on the camera; if a failed read could be mistaken for "this
            // camera has no zone", the next save would quietly write the grid to
            // the server instead of the camera and the camera would go on alarming
            // on its old one. So only a definite false migrates a zone.
            Assert(!Web.CameraStateStore.ZoneIsLocal(true, "md", 1), "a camera that holds a zone keeps it");
            Assert(!Web.CameraStateStore.ZoneIsLocal(null, "md", 1), "'not asked yet' is NOT 'has none'");
            Assert(Web.CameraStateStore.ZoneIsLocal(false, "md", 1), "a camera that provably has none gets a local zone");
            // Per-type grids are the camera's own business: a local zone offered
            // beside them would claim to govern what it cannot.
            Assert(!Web.CameraStateStore.ZoneIsLocal(false, "md", 3), "a camera with per-type grids is never local");
            foreach (var t in new[] { "people", "vehicle", "dog_cat", "face", "package" })
                Assert(!Web.CameraStateStore.ZoneIsLocal(false, t, 1), $"only the shared md grid is kept locally, not {t}");

            // The default grid follows the picture's shape so cells stay square.
            AssertEq(Web.CameraStateStore.DefaultZoneGrid(1920, 1080), (32, 18));
            AssertEq(Web.CameraStateStore.DefaultZoneGrid(1280, 960), (24, 18));
            AssertEq(Web.CameraStateStore.DefaultZoneGrid(0, 0), (32, 18));
            Assert(Web.CameraStateStore.DefaultZoneGrid(5120, 1552).Cols == 48, "very wide is clamped");
        });

        Test("a stored zone follows a camera that is renamed; nothing else changes", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "neolink-selftest-rename-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Web.CameraStateStore(dir);
                store.SetZone("Gate", "md", 4, 2, "11001111");
                store.SetSuspended("Gate", true);
                store.SetDetectionCaps("Gate", aiTypes: new[] { "people" });

                store.Rename("Gate", "Front Gate");
                Assert(store.Zone("Gate", "md") == null, "the zone left the old name");
                Assert(store.Zone("Front Gate", "md")?.Table == "11001111", "the zone came along");
                // Everything that existed before stored zones behaves exactly as it
                // always did on a rename: it stays where it was.
                Assert(store.Suspended("Gate") && !store.Suspended("Front Gate"), "the suspend flag did not move");
                Assert(store.DetectionCaps("Gate").AiTypes is { Count: 1 }
                       && store.DetectionCaps("Front Gate").AiTypes == null, "nor the cached capabilities");

                // A camera with no stored zone (every Reolink that keeps its own) is
                // untouched by a rename.
                store.SetSuspended("Porch", true);
                store.Rename("Porch", "Back Porch");
                Assert(store.Suspended("Porch") && !store.Suspended("Back Porch"), "no zone, no change");

                // Deleting clears only the zone.
                store.SetZone("Shed", "md", 2, 1, "10");
                store.SetSuspended("Shed", true);
                store.Forget("Shed");
                Assert(store.Zone("Shed", "md") == null && store.Suspended("Shed"),
                    "a delete drops the zone and leaves the rest as it always was");

                // Renaming to the same name (or only changing its case, which the
                // app treats as the same camera) must not throw the state away.
                store.Rename("Front Gate", "front gate");
                Assert(store.Zone("Front Gate", "md") != null, "a case-only rename keeps the state");
                // A camera with no state to move is a no-op, not a crash.
                store.Rename("Never-seen", "Also-never-seen");

                Assert(new Web.CameraStateStore(dir).Zone("Front Gate", "md")?.Table == "11001111",
                    "and it survives a restart under the new name");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("ONVIF events: the vendor topics Home Assistant's parsers know", () =>
        {
            static List<Protocol.OnvifNotification> Parse(string messages) =>
                Protocol.OnvifClient.ParseNotifications(System.Xml.Linq.XDocument.Parse(
                    "<tev:PullMessagesResponse xmlns:tev=\"http://www.onvif.org/ver10/events/wsdl\" " +
                    "xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" xmlns:tt=\"http://www.onvif.org/ver10/schema\">" +
                    messages + "</tev:PullMessagesResponse>").Root!);
            static string Msg(string topic, string data, string source = "", string op = "", string key = "") =>
                $"<wsnt:NotificationMessage><wsnt:Topic>{topic}</wsnt:Topic><wsnt:Message>" +
                $"<tt:Message UtcTime=\"2026-01-02T03:04:05Z\"{(op.Length > 0 ? $" PropertyOperation=\"{op}\"" : "")}>" +
                (source.Length > 0 ? $"<tt:Source>{source}</tt:Source>" : "") +
                (key.Length > 0 ? $"<tt:Key>{key}</tt:Key>" : "") +
                (data.Length > 0 ? $"<tt:Data>{data}</tt:Data>" : "") +
                "</tt:Message></wsnt:Message></wsnt:NotificationMessage>";
            static string Item(string name, string value) => $"<tt:SimpleItem Name=\"{name}\" Value=\"{value}\"/>";

            var visitor = Parse(Msg("tns1:RuleEngine/MyRuleDetector/Visitor", Item("State", "true"), Item("Source", "000")))[0];
            Assert(visitor is { Active: true, Label: "visitor" }, "Reolink's Visitor topic is the doorbell");
            AssertEq(Parse(Msg("tns1:IVA/EnteringField/Eindringen_in_Feld_1", Item("State", "true")))[0].Label, "intrusion");
            AssertEq(Parse(Msg("tns1:RuleEngine/TPSmartEventDetector/TPSmartEvent",
                Item("IsPackageDeliver", "true")))[0].Label, "package");

            // ObjectDetection: ClassTypes is the state, and an empty list is the all-clear.
            const string objects = "tns1:RuleEngine/ObjectDetection/Object";
            Assert(Parse(Msg(objects, Item("ClassTypes", ""), op: "Changed"))[0].Active == false,
                "an empty ClassTypes ends the detection");
            // A state beside the classes is its own notification, not lost to the split.
            var beside = Parse(Msg("tns1:RuleEngine/TPSmartEventDetector/TPSmartEvent",
                Item("IsPeople", "false") + Item("IsMotion", "true"), op: "Changed"));
            Assert(beside.Count == 2 && beside[0] is { Active: false, Label: "motion" } && beside[1] is { Active: true, Label: "motion" }
                   && beside[1].Key != beside[0].Key, "the motion beside an ended class still starts");
            AssertEq(Parse(Msg("tns1:RuleEngine/TPSmartEventDetector/TPSmartEvent", Item("IsPeople", "true"), op: "Changed")).Count, 1);
            var seen = Parse(Msg(objects, Item("ClassTypes", "Human Vehicle"), op: "Changed"))[0];
            Assert(seen.Active == true && seen.Labels.Contains("person") && seen.Labels.Contains("vehicle"),
                "a property listing classes is on");
            Assert(Parse(Msg(objects, Item("ClassTypes", "Human")))[0].Active == null,
                "sent as an event (no PropertyOperation) it is a one-shot");

            var pushes = new List<Protocol.MotionPush>();
            var svc = new Streaming.OnvifEventService("cam",
                new Protocol.OnvifClient("127.0.0.1:1", "u", "p", "test")) { MotionSink = pushes.Add };
            void Feed(string m) { foreach (var n in Parse(m)) svc.Handle(n); }

            // Tapo reports each class with its own state on one topic: the pet leaving does not end the vehicle.
            const string tapo = "tns1:RuleEngine/TPSmartEventDetector/TPSmartEvent";
            var vsconf = Item("VideoSourceConfigurationToken", "vsconf");
            Feed(Msg(tapo, Item("IsVehicle", "true"), vsconf, "Changed"));
            Feed(Msg(tapo, Item("IsPet", "true"), vsconf, "Changed"));
            Feed(Msg(tapo, Item("IsPet", "false"), vsconf, "Changed"));
            Assert(svc.ActiveLabels.Contains("vehicle") && !svc.ActiveLabels.Contains("animal"),
                "each Tapo class keeps its own state");
            Feed(Msg(tapo, "", vsconf, "Deleted"));
            AssertEq(svc.ActiveLabels.Count, 0); // a withdrawn property ends every class it reported

            // Per-object state (tt:Key ObjectId): object 6 being absent does not end object 5.
            const string field = "tns1:RuleEngine/FieldDetector/ObjectsInside";
            Feed(Msg(field, Item("IsInside", "true"), op: "Changed", key: Item("ObjectId", "5")));
            Feed(Msg(field, Item("IsInside", "false"), op: "Initialized", key: Item("ObjectId", "6")));
            Assert(svc.ActiveLabels.Contains("intrusion"), "object 5 is still inside");
            Feed(Msg(field, Item("IsInside", "false"), op: "Changed", key: Item("ObjectId", "5")));
            AssertEq(svc.ActiveLabels.Count, 0);

            // A one-shot's Initialized message replays the rule's last value; it is not a sighting.
            pushes.Clear();
            Feed(Msg("tns1:RuleEngine/LineDetector/Crossed", Item("ObjectId", "0"), op: "Initialized"));
            AssertEq(pushes.Count, 0);

            // The doorbell rings once: neither a re-push nor another detection repeats it.
            Feed(Msg("tns1:RuleEngine/MyRuleDetector/Visitor", Item("State", "true"), Item("Source", "000"), "Changed"));
            Assert(pushes[^1].AiTypes.Contains("visitor"), "the press goes out");
            int rung = pushes.Count;
            svc.RepushForTest();
            AssertEq(pushes.Count, rung); // only the ring is active: nothing to re-push
            Feed(Msg("tns1:RuleEngine/CellMotionDetector/Motion", Item("IsMotion", "true"), op: "Changed"));
            svc.RepushForTest();
            Assert(pushes.Skip(rung).All(p => !p.AiTypes.Contains("visitor")) && pushes[^1].AiTypes.Contains("motion"),
                "later pushes carry the motion, not the ring");
        });

        Test("rtsp puller: the camera quirks go2rtc handles", () =>
        {
            // The camera may answer SETUP with other interleaved channels than asked.
            AssertEq(Protocol.RtspPuller.InterleavedChannel("RTP/AVP/TCP;unicast;interleaved=10-11;ssrc=10117CB7"), 10);
            AssertEq(Protocol.RtspPuller.InterleavedChannel("RTP/AVP/TCP;unicast"), null);

            // Control URLs: a leading '/', a Content-Base without scheme, Dahua's query URL.
            const string sdp = "v=0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:/media/1/video/1\r\n";
            AssertEq(Protocol.RtspPuller.ParseSdpVideo(sdp, "rtsp://192.168.101.22").Control,
                "rtsp://192.168.101.22/media/1/video/1");
            var track0 = sdp.Replace("/media/1/video/1", "trackID=0");
            AssertEq(Protocol.RtspPuller.ParseSdpVideo(track0, "192.168.253.220:1935/").Control,
                "rtsp://192.168.253.220:1935/trackID=0");
            AssertEq(Protocol.RtspPuller.ParseSdpVideo(track0, "rtsp://h:554/cam/realmonitor?channel=1&subtype=0").Control,
                "rtsp://h:554/cam/realmonitor?channel=1&subtype=0/trackID=0");
            // The video's fmtp filed under the audio section (go2rtc: WebRTC#419).
            var misfiled = "v=0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:trackID=0\r\n" +
                           "m=audio 0 RTP/AVP 8\r\na=fmtp:96 packetization-mode=1;" +
                           "sprop-parameter-sets=Z2QAMqzSAJACjoQAAA+kAAJxoBA=,aOqPLA==\r\n";
            Assert(Protocol.RtspPuller.ParseSdpVideo(misfiled, "rtsp://h/").SpropNals is { Length: > 20 },
                "a misfiled fmtp still yields the parameter sets");

            var sink = new FrameSink();
            var puller = new Protocol.RtspPuller("t", "rtsp://h/x", sink);
            ushort seq = 0;
            byte[] Rtp(uint ts, bool marker, byte[] payload)
            {
                var p = new byte[12 + payload.Length];
                p[0] = 0x80;
                p[1] = (byte)((marker ? 0x80 : 0) | 96);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), seq++);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), ts);
                payload.CopyTo(p, 12);
                return p;
            }
            int[] Types(VideoFrame f) => Media.H26x.SplitNals(f.Data).Select(n => Media.H26x.H264NalType(n.Span)).ToArray();
            var sps = Convert.FromBase64String("Z2QAMqzSAJACjoQAAA+kAAJxoBA=");
            var pps = Convert.FromBase64String("aOqPLA==");
            byte[] idr = { 0x65, 0x88, 0x80, 0x10 };

            // SPS and PPS each sent with the marker bit (Tapo TC70, Reolink Duo 2) lead the IDR.
            puller.FeedRtpForTest(Rtp(1000, true, sps));
            puller.FeedRtpForTest(Rtp(1000, true, pps));
            puller.FeedRtpForTest(Rtp(1000, true, idr));
            Assert(sink.Frames.Count == 1 && sink.Frames[0].Keyframe, "the parameter sets wait for the picture");
            AssertEq(string.Join(",", Types(sink.Frames[0])), "7,8,5");

            // SPS+PPS+IDR packed into one payload behind start codes: the IDR is still found.
            sink.Frames.Clear();
            byte[] sc = { 0, 0, 0, 1 };
            puller.FeedRtpForTest(Rtp(4000, true, sps.Concat(sc).Concat(pps).Concat(sc).Concat(idr).ToArray()));
            Assert(sink.Frames.Count == 1 && sink.Frames[0].Keyframe, "a packed payload's keyframe is recognised");
            AssertEq(string.Join(",", Types(sink.Frames[0])), "7,8,5");

            // A lost fragment drops its frame instead of publishing it with a hole.
            sink.Frames.Clear();
            puller.FeedRtpForTest(Rtp(7000, false, new byte[] { 0x7C, 0x81, 1, 2 }));  // FU-A start of a slice
            seq++;                                                                     // lost in the camera
            puller.FeedRtpForTest(Rtp(7000, true, new byte[] { 0x7C, 0x41, 5, 6 }));   // FU-A end
            AssertEq(sink.Frames.Count, 0);
            puller.FeedRtpForTest(Rtp(10000, true, new byte[] { 0x41, 0x9A, 0x00, 0x10 }));
            AssertEq(sink.Frames.Count, 1); // the next whole frame flows again

            // A frame's last packet lost just before a keyframe costs that frame, not the keyframe.
            sink.Frames.Clear();
            puller.FeedRtpForTest(Rtp(13000, false, new byte[] { 0x41, 0x9A, 0x00, 0x11 }));
            seq++;                                                                     // its marker packet, lost
            puller.FeedRtpForTest(Rtp(16000, true, idr));
            Assert(sink.Frames.Count == 1 && sink.Frames[0].Keyframe, "the keyframe after the loss is kept");

            // A loss right after a finished frame was the next frame's opening: that one is dropped.
            sink.Frames.Clear();
            seq++;
            puller.FeedRtpForTest(Rtp(19000, true, new byte[] { 0x41, 0x9A, 0x00, 0x12 }));
            AssertEq(sink.Frames.Count, 0);

            // A source that never advances the sequence number still streams.
            var stuck = seq;
            for (uint t = 22000; t < 31000; t += 3000)
            {
                seq = stuck;
                puller.FeedRtpForTest(Rtp(t, true, new byte[] { 0x41, 0x9A, 0x00, 0x13 }));
            }
            AssertEq(sink.Frames.Count, 3);
        });

        Test("stream hub: an RTSP source's size follows its SPS, a Baichuan's stays as reported", () =>
        {
            // A Reolink's real main (2304x1296) and sub (640x360) parameter sets.
            var main = Convert.FromBase64String("Z2QAMqzSAJACjoQAAA+kAAJxoBA=");
            var sub = Convert.FromBase64String("Z2QAFqzSAoC/5YQAAA+kAADa+BA=");
            var pps = Convert.FromBase64String("aOqPLA==");
            static byte[] Key(byte[] sps, byte[] pps) => new byte[] { 0, 0, 0, 1 }.Concat(sps)
                .Concat(new byte[] { 0, 0, 0, 1 }).Concat(pps)
                .Concat(new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0x80 }).ToArray();
            static Media.VideoFrame F(byte[] data, uint us) => new(Media.VideoCodec.H264, true, us, null, data);

            // A wrong first SPS (the encoder just starting) must not pin the size.
            var rtsp = new Streaming.StreamHub("rtsp");
            rtsp.PublishVideo(F(Key(sub, pps), 0));
            Assert(rtsp.Width == 640 && rtsp.Height == 360, $"first SPS read ({rtsp.Width}x{rtsp.Height})");
            rtsp.PublishVideo(F(Key(main, pps), 40_000));
            Assert(rtsp.Width == 2304 && rtsp.Height == 1296, $"the next SPS corrects it ({rtsp.Width}x{rtsp.Height})");

            // The camera's own report wins, exactly as before.
            var bc = new Streaming.StreamHub("bc");
            bc.PublishInfo(new Media.MediaInfo(3840, 2160, 25));
            bc.PublishVideo(F(Key(main, pps), 0));
            Assert(bc.Width == 3840 && bc.Height == 2160, $"a reported size is kept ({bc.Width}x{bc.Height})");

            // A group of pictures too big to buffer still reads as live video, until the source stops.
            var big = new Streaming.StreamHub("big");
            big.PublishVideo(F(Key(main, pps), 0));
            var slice = new byte[1 << 20];
            new byte[] { 0, 0, 0, 1, 0x41 }.CopyTo(slice, 0);
            for (uint i = 1; i <= 7; i++) big.PublishVideo(new Media.VideoFrame(Media.VideoCodec.H264, false, i * 40_000, null, slice));
            Assert(!big.HasBufferedGop && big.LiveVideo, "an overflowed group leaves the stream live");
            big.SourceStopped();
            Assert(!big.LiveVideo, "and a stopped source is not");
        });

        Test("Reolink HTTP API: only a lasting refusal counts as the firmware's answer", () =>
        {
            foreach (var code in new[] { -9, -24, -26 })
                Assert(new Protocol.ReolinkApiException("x", code).RejectedByCamera, $"rspCode {code} is lasting");
            foreach (var code in new[] { 0, -1, -5, -12, -17, -31 })
                Assert(!new Protocol.ReolinkApiException("x", code).RejectedByCamera, $"rspCode {code} may pass");
        });

        Test("frame grab: a decodable run always starts on a keyframe", () =>
        {
            static Streaming.HubVideo V(long i, bool key) => new(i, new byte[] { 0, 0, 0, 1, (byte)i }, key, (uint)i);
            var run = new List<Streaming.HubVideo>();

            // Frames before the first keyframe are undecodable and are dropped.
            Media.FrameGrab.Take(run, V(1, false));
            Media.FrameGrab.Take(run, V(2, false));
            AssertEq(run.Count, 0);

            Media.FrameGrab.Take(run, V(3, true));
            Media.FrameGrab.Take(run, V(4, false));
            Assert(run.Count == 2 && run[0].Keyframe, "the run starts at the keyframe");

            // A newer keyframe is a fresher picture than the one in hand.
            Media.FrameGrab.Take(run, V(5, true));
            Assert(run.Count == 1 && run[0].Index == 5, "a later keyframe restarts the run");

            // Audio is not video.
            Media.FrameGrab.Take(run, new Streaming.HubAudioAac(6, new byte[] { 1 }, 6));
            AssertEq(run.Count, 1);

            // The follow limit bounds what is fed to the decoder: the LAST decoded frame
            // is the still, so the run carries on past the keyframe, but not without end.
            for (long i = 7; i < 60; i++) Media.FrameGrab.Take(run, V(i, false));
            AssertEq(run.Count, Media.FrameGrab.MaxFollowing + 1);
            Assert(run[0].Keyframe, "and still starts on the keyframe");
        });
        Test("ONVIF detections: stateful ones last until the camera ends them", () =>
        {
            // ONVIF reports a detection's state only when it CHANGES. A camera watching
            // a long event sends "true" once and then nothing until "false" — so any
            // timer on silence tears one real event into two. Only a one-shot (a
            // notification with no state at all) may lapse on its own.
            static Protocol.OnvifNotification N(string topic, bool? active, params string[] kv)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
                return new Protocol.OnvifNotification(topic, active, d);
            }
            var pushes = new List<Protocol.MotionPush>();
            var svc = new Streaming.OnvifEventService("cam",
                new Protocol.OnvifClient("127.0.0.1:1", "u", "p", "test"))
            { MotionSink = pushes.Add };

            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", true));
            svc.AgeForTest(TimeSpan.FromMinutes(10)); // a long, quiet, ongoing event
            svc.ExpireOneShots();
            Assert(svc.ActiveLabels.Contains("motion"),
                "a stateful detection survives ten quiet minutes — silence is not an end");
            AssertEq(pushes.Count, 1); // and nothing claimed it had ended

            // A person reported alongside: BOTH labels go out. Dropping "motion" here
            // left a camera set to record motion-but-not-people recording nothing.
            svc.Handle(N("tns1:RuleEngine/FieldDetector/ObjectsInside", true, "ObjectType", "Human"));
            var both = pushes[^1];
            Assert(both.Active && both.AiTypes.Contains("motion") && both.AiTypes.Contains("person"),
                "every active label is emitted, motion included");

            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", false));
            Assert(pushes[^1].Active && pushes[^1].AiTypes.Contains("person")
                   && pushes[^1].AiTypes.Contains("intrusion"),
                "ending the cell-motion rule leaves the field rule's person (and its intrusion) active");
            // An end WITHOUT the classification the start carried still ends that rule's
            // detection: starts and ends are matched by the rule, not the label.
            svc.Handle(N("tns1:RuleEngine/FieldDetector/ObjectsInside", false));
            Assert(!pushes[^1].Active && pushes[^1].Status == "none", "the last end is an all-clear");

            // Two rules that both mean "motion" keep separate state: one ending
            // must not end the other.
            pushes.Clear();
            svc.Handle(N("tns1:VideoSource/MotionAlarm", true));
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", true));
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", false));
            Assert(pushes[^1].Active && svc.ActiveLabels.Contains("motion"),
                "the cell-motion end leaves the MotionAlarm property's motion active");
            svc.Handle(N("tns1:VideoSource/MotionAlarm", false));
            Assert(!pushes[^1].Active, "and the last speaker's end is the all-clear");

            // Another channel of the same device (an NVR) is filtered out once the device's
            // other channels are known; its all-clear must not end channel 1's detection.
            static Protocol.OnvifNotification S(string source, bool active)
            {
                var src = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["VideoSourceConfigurationToken"] = source };
                var all = new Dictionary<string, string>(src, StringComparer.OrdinalIgnoreCase)
                    { ["IsMotion"] = active ? "true" : "false" };
                return new Protocol.OnvifNotification("tns1:RuleEngine/CellMotionDetector/Motion", active, all, src);
            }
            svc.UseOtherChannelsForTest(new[] { "VSC_2" });
            pushes.Clear();
            svc.Handle(S("VSC_1", true));
            svc.Handle(S("VSC_2", false));
            svc.Handle(S("VSC_2", true));
            Assert(pushes.Count == 1 && svc.ActiveLabels.Contains("motion"),
                "channel 2's events are ignored and channel 1's detection stands");
            svc.Handle(S("VSC_1", false));
            Assert(!pushes[^1].Active, "channel 1's own end is the all-clear");
            // A source value that names no channel (Foscam: HUMAN_DETECTION_ALARM) is still this camera's.
            svc.Handle(S("HUMAN_DETECTION_ALARM", true));
            Assert(svc.ActiveLabels.Contains("motion"), "an unknown source value is not filtered out");
            svc.Handle(S("HUMAN_DETECTION_ALARM", false));
            // A withdrawn property ends whatever it reported and starts nothing.
            svc.Handle(S("VSC_1", true));
            svc.Handle(new Protocol.OnvifNotification("tns1:RuleEngine/CellMotionDetector/Motion", true,
                new Dictionary<string, string> { ["VideoSourceConfigurationToken"] = "VSC_1" },
                new Dictionary<string, string> { ["VideoSourceConfigurationToken"] = "VSC_1" }, "Deleted"));
            Assert(!svc.ActiveLabels.Contains("motion"), "PropertyOperation=Deleted ends the detection");
            svc.UseOtherChannelsForTest(Array.Empty<string>());
            // One taken from channel 2 before the channels were known ends once they are.
            svc.Handle(S("VSC_2", true));
            svc.UseOtherChannelsForTest(new[] { "VSC_2" });
            Assert(!svc.ActiveLabels.Contains("motion") && !pushes[^1].Active,
                "another channel's detection is ended when the channels become known");
            svc.UseOtherChannelsForTest(Array.Empty<string>());

            // A doorbell rings once, keeps the recording open while pressed, and a re-subscribe's
            // replay of it rings nothing.
            const string bell = "tns1:RuleEngine/MyRuleDetector/Visitor";
            pushes.Clear();
            svc.Handle(N(bell, true));
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", true));
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", false));
            Assert(pushes.Count(p => p.AiTypes.Contains("visitor")) == 1 && pushes[^1].Active,
                "one ring, and motion ending while the doorbell is active sends no all-clear");
            svc.Handle(N(bell, false));
            Assert(!pushes[^1].Active, "the doorbell's own end is the all-clear");
            pushes.Clear();
            svc.Handle(new Protocol.OnvifNotification(bell, true, new Dictionary<string, string>(), null, "Initialized"));
            AssertEq(pushes.Count, 0);
            svc.Handle(N(bell, false));

            // A one-shot never gets an end, so it lapses — on ITS OWN clock, not the
            // camera's: a stateful label kept busy alongside must not hold it open.
            pushes.Clear();
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", true));
            svc.Handle(N("tns1:RuleEngine/MyRuleDetector/Detect", null, "ObjectType", "Vehicle"));
            svc.AgeForTest(TimeSpan.FromSeconds(30));
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", true)); // motion re-reported
            svc.ExpireOneShots();
            Assert(!svc.ActiveLabels.Contains("vehicle"), "the one-shot lapsed although motion kept reporting");
            Assert(svc.ActiveLabels.Contains("motion"), "and the stateful one is untouched");

            // Once a label has been reported WITH state it stays stateful: a camera
            // that also fires one-shots for the same thing must not turn an event it
            // promised to end into one that lapses by itself.
            svc.Handle(N("tns1:RuleEngine/CellMotionDetector/Motion", null));
            svc.AgeForTest(TimeSpan.FromMinutes(5));
            svc.ExpireOneShots();
            Assert(svc.ActiveLabels.Contains("motion"), "a one-shot re-report does not demote a stateful label");
        });

        Test("credentials: the mask and the parser agree on where a password ends", () =>
        {
            // An '@' inside a password is common and people do not escape it. Both
            // the mask and the ONVIF parser must end the login at the LAST '@' of the
            // authority, or the mask shows the tail of the password to the browser.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("admin:p@ss@cam.lan:8000"), "admin:****@cam.lan:8000");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://admin:p@ss@cam/live/main"),
                "rtsp://admin:****@cam/live/main");
            // An '@' in the PATH is not a login.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://cam/live@main"), "rtsp://cam/live@main");
            var split = Protocol.OnvifClient.SplitCredentials("admin:p@ss@cam.lan:8000");
            Assert(split is { Address: "cam.lan:8000", User: "admin", Pass: "p@ss" }, "the parser splits the same way");
            // ...and a FULL URL with the same raw '@' (the form the camera editor's hint
            // shows) splits the same way; System.Uri refuses such a URL outright.
            var full = Protocol.OnvifClient.SplitCredentials("http://admin:p@ss@cam.lan:8000/onvif/device_service");
            Assert(full is { Address: "http://cam.lan:8000/onvif/device_service", User: "admin", Pass: "p@ss" },
                "a raw '@' in a full URL's password is read to the last '@' of the authority");
            // A raw '/' in a password: the mask must not show it and the parser must read
            // it. An '@' in a PATH after a port is still not a login.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("admin:pa/ss@10.0.0.5:8000"), "admin:****@10.0.0.5:8000");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://admin:pa/ss@cam/live"), "rtsp://admin:****@cam/live");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://cam:554/live@main"), "rtsp://cam:554/live@main");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://[fe80::1]:554/live@main"), "rtsp://[fe80::1]:554/live@main");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://admin:pa/ss@[fe80::1]:554/live"), "rtsp://admin:****@[fe80::1]:554/live");
            Assert(Protocol.OnvifClient.SplitCredentials("admin:pa/ss@10.0.0.5:8000")
                is { Address: "10.0.0.5:8000", User: "admin", Pass: "pa/ss" }, "the parser reads the raw '/'");
            Assert(Protocol.OnvifClient.SplitCredentials("http://admin:pa/ss@cam.lan/onvif/device_service")
                is { Address: "http://cam.lan/onvif/device_service", User: "admin", Pass: "pa/ss" },
                "...in a full URL as well, exactly where the mask reads it");
            // A raw '#' or '?' in a password is masked and read the same way.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://admin:Pass#123@192.168.1.10/stream"),
                "rtsp://admin:****@192.168.1.10/stream");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://admin:Pa?ss@cam/live?x=1"), "rtsp://admin:****@cam/live?x=1");
            // ...and an '@' in the query after such a password is not the login's; nor is one after a '/' in it.
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://user:pa?ss@cam/live?token=a@b"), "rtsp://user:****@cam/live?token=a@b");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://user:pa?s/s@cam/live"), "rtsp://user:****@cam/live");
            // The puller reads the same login the mask does, so such a URL streams.
            var raw = new Protocol.RtspPuller("t", "rtsp://admin:Pass#123@10.0.0.5/stream", new FrameSink());
            AssertEq(raw.BareUrl, "rtsp://10.0.0.5:554/stream");
            Assert(Protocol.OnvifClient.SplitCredentials("http://admin:Pass#1@cam/onvif/device_service")
                is { Address: "http://cam/onvif/device_service", User: "admin", Pass: "Pass#1" }, "the parser reads the raw '#'");
            AssertEq(Config.ConfigEditor.MaskRtspPassword("rtsp://10.0.0.5:554?cam@1"), "rtsp://10.0.0.5:554?cam@1");

            // An edit made around a masked password keeps the real one. Before, it was
            // either written as the literal mask (destroying the credential) or
            // silently discarded along with the edit.
            const string stored = "rtsp://admin:s3cret@10.0.0.5:554/live/main";
            var shown = Config.ConfigEditor.MaskRtspPassword(stored)!;
            AssertEq(Config.ConfigEditor.UnmaskPassword(shown, stored), stored);
            AssertEq(Config.ConfigEditor.UnmaskPassword(shown.Replace("10.0.0.5", "10.0.0.9"), stored),
                "rtsp://admin:s3cret@10.0.0.9:554/live/main");
            AssertEq(Config.ConfigEditor.UnmaskPassword("rtsp://admin:newpass@10.0.0.5/live", stored),
                "rtsp://admin:newpass@10.0.0.5/live"); // a typed password is taken as-is
            Assert(Config.ConfigEditor.UnmaskPassword(shown, null) == null,
                "nothing to restore from -> keep what is stored, never write the mask");
            AssertEq(Config.ConfigEditor.UnmaskPassword("", stored), ""); // cleared = removed
        });

        Test("ONVIF login: a refused login is paced, and the password never goes out in clear to a stranger", () =>
        {
            var ct = CancellationToken.None;
            static bool Signed(string raw) => raw.Contains("UsernameToken", StringComparison.Ordinal);
            static bool Anyone(string op) => op is "GetSystemDateAndTime" or "GetCapabilities" or "GetServices"
                or "GetDeviceInformation";

            // A camera that answers the service table to anyone and refuses this login for everything else.
            using (var cam = new FakeOnvif((op, raw) => Anyone(op) ? FakeOnvif.Answer(op) : FakeOnvif.Refused()))
            {
                using var onvif = new Protocol.OnvifClient($"127.0.0.1:{cam.Port}", "admin", "wrong", "t", generic: true);
                var info = onvif.TryGetDeviceInfoAsync(ct).GetAwaiter().GetResult();
                Assert(onvif.Ready && onvif.AuthRejected && info?.Model == "Cam9", "ready, with the refusal noted");
                var refused = cam.Requests.Where(r => r.Op == "GetVideoSources").Select(r => r.Raw).ToList();
                Assert(refused.Count == 2 && refused.Count(r => r.Contains("#PasswordText")) == 1,
                    "one digest try and one plain-text try at a host that answered as ONVIF");
                int before = cam.Requests.Count;
                onvif.TryGetProfilesAsync(ct).GetAwaiter().GetResult();
                onvif.TryGetImagingAsync(ct).GetAwaiter().GetResult();
                onvif.TryGetDeviceInfoAsync(ct).GetAwaiter().GetResult();
                Assert(!cam.Requests.Skip(before).Any(r => Signed(r.Raw)), "no signed request while the login is paused");
                Assert(onvif.AuthRejected, "an unsigned answer does not clear the refusal");
            }

            // A web server that is not a camera: it gets neither the password in clear nor a Basic login.
            using (var web = new FakeOnvif((op, raw) => (401, "<html>sign in</html>", "WWW-Authenticate: Basic realm=\"router\"\r\n")))
            {
                using var probe = new Protocol.OnvifClient($"127.0.0.1:{web.Port}", "admin", "secret", "t", generic: true);
                probe.TryGetDeviceInfoAsync(ct).GetAwaiter().GetResult();
                Assert(web.Requests.Count > 0 && !web.Requests.Any(r => r.Raw.Contains("#PasswordText")
                                                                  || r.Raw.Contains("Authorization: Basic")),
                    "no plain-text password or Basic login to a host that never answered as ONVIF");
            }

            // A camera that takes its password only in plain text (OpenIPC) is signed in that way.
            using (var ipc = new FakeOnvif((op, raw) =>
                       Anyone(op) || raw.Contains("#PasswordText") ? FakeOnvif.Answer(op) : FakeOnvif.Refused()))
            {
                using var onvif = new Protocol.OnvifClient($"127.0.0.1:{ipc.Port}", "admin", "pw", "t", generic: true);
                Assert(onvif.TryGetProfilesAsync(ct).GetAwaiter().GetResult() is { Count: 1 }, "profiles read in plain text");
                Assert(!onvif.AuthRejected, "the plain-text login is accepted");
            }

            // Once the login is proven, a refusal is a right this user lacks: nothing else pauses.
            using (var cam = new FakeOnvif((op, raw) => op == "SystemReboot" ? FakeOnvif.Refused() : FakeOnvif.Answer(op)))
            {
                using var onvif = new Protocol.OnvifClient($"127.0.0.1:{cam.Port}", "operator", "pw", "t", generic: true);
                onvif.TryGetProfilesAsync(ct).GetAwaiter().GetResult();
                try { onvif.RebootAsync(ct).GetAwaiter().GetResult(); } catch (IOException) { }
                int before = cam.Requests.Count;
                onvif.TryGetImagingAsync(ct).GetAwaiter().GetResult();
                Assert(cam.Requests.Skip(before).Any(r => r.Op == "GetImagingSettings" && Signed(r.Raw)),
                    "a refused reboot does not hold the other calls back");
            }
        });

        Test("RTSP users' access rule: one helper for RTSP, the web API's Basic path and ONVIF PTZ", () =>
        {
            var users = new Dictionary<string, string> { ["frigate"] = "pw", ["other"] = "x" };
            var only = new HashSet<string> { "frigate" };
            Assert(NetUtil.Permits(new Dictionary<string, string>(), only, null, null), "no users: open");
            Assert(NetUtil.Permits(users, null, null, null), "a camera open to anonymous: open");
            Assert(NetUtil.Permits(users, only, "frigate", "pw"), "a permitted user with the right password");
            Assert(!NetUtil.Permits(users, only, "frigate", "bad") && !NetUtil.Permits(users, only, "other", "x")
                   && !NetUtil.Permits(users, only, null, null), "a wrong password, an unpermitted user, or no login");
            var rtsp = new Rtsp.RtspServer(users);
            var mount = new Rtsp.RtspMount { Path = "/e1", Hub = new Streaming.StreamHub("e1"), PermittedUsers = only };
            string Basic(string u, string p) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u}:{p}"));
            Assert(rtsp.Authorize(mount, Basic("frigate", "pw")) && !rtsp.Authorize(mount, Basic("other", "x"))
                   && !rtsp.Authorize(mount, null), "RTSP applies the same rule");
        });

        Test("ONVIF PTZ: ptz_share and ptz_port are read, and one that cannot work is turned off, not the camera", () =>
        {
            var json = Path.Combine(Path.GetTempPath(), $"neolink-ptz-{Guid.NewGuid():N}.json");
            var toml = Path.ChangeExtension(json, ".toml");
            File.WriteAllText(json, """
                {
                  "bind_port": 8654, "web_port": 8655, "ptz_bind": "127.0.0.1",
                  "cameras": [
                    { "name": "office", "username": "admin", "address": "10.0.0.2", "ptz_share": true },
                    { "name": "own", "username": "admin", "address": "10.0.0.3", "ptz_port": 8081 },
                    { "name": "twin", "username": "admin", "address": "10.0.0.4", "ptz_port": 8081 },
                    { "name": "rtsp", "username": "admin", "address": "10.0.0.5", "ptz_port": 8654 },
                    { "name": "shared", "username": "admin", "address": "10.0.0.6", "ptz_port": 8656 },
                    { "name": "generic", "rtsp_main": "rtsp://10.0.0.7/s", "ptz_share": true },
                    { "name": "typo", "username": "admin", "address": "10.0.0.8", "ptz_port": "8083" },
                    { "name": "nulled", "username": "admin", "address": "10.0.0.9", "ptz_port": null },
                    { "name": "zero", "username": "admin", "address": "10.0.0.10", "ptz_port": 0 }
                  ]
                }
                """);
            File.WriteAllText(toml, """
                ptz_bind = "127.0.0.1"
                ptz_port = 9100
                [[cameras]]
                name = "office"
                username = "admin"
                address = "10.0.0.2"
                ptz_share = true
                [[cameras]]
                name = "own"
                username = "admin"
                address = "10.0.0.3"
                ptz_port = 8081
                """);
            CameraConfig Cam(NeolinkConfig c, string name) => c.Cameras.Single(x => x.Name == name);
            NeolinkConfig Json(string text) { File.WriteAllText(json, text); return NeolinkConfig.Load(json); }
            try
            {
                var c = NeolinkConfig.Load(json);
                Assert(c.PtzBind == "127.0.0.1" && c.PtzPort == 8656, "the shared port defaults to 8656");
                AssertEq(c.Cameras.Count, 9); // no value, however bad, costs the camera
                Assert(Cam(c, "office") is { PtzMode: "shared", PtzOff: null }, "a profile on the shared port");
                Assert(Cam(c, "own") is { PtzMode: "own", PtzPort: 8081, PtzOff: null }, "a port of its own");
                Assert(new[] { "twin", "rtsp", "shared", "generic", "typo" }.All(n => Cam(c, n).PtzOff != null),
                    "a taken port, the shared port, a camera with ONVIF of its own, or a string turn it off");
                StringAssert(Cam(c, "shared").PtzOff!, "shared PTZ port");
                Assert(Cam(c, "nulled").PtzMode == "off" && Cam(c, "zero").PtzMode == "off", "null and 0 are off");
                bool refused = false;
                try { NeolinkConfig.Load(json, strict: true); } catch (FormatException) { refused = true; }
                Assert(refused, "a save is refused to the admin's face instead");

                // The shared port off, or taken: the cameras sharing it say so.
                const string sharer = """{ "name": "office", "username": "admin", "address": "10.0.0.2", "ptz_share": true }""";
                StringAssert(Cam(Json($$"""{ "ptz_port": 0, "ptz_bind": "127.0.0.1", "cameras": [ {{sharer}} ] }"""), "office").PtzOff!,
                    "ptz_port is 0");
                StringAssert(Cam(Json($$"""{ "web_port": 8656, "ptz_bind": "127.0.0.1", "cameras": [ {{sharer}} ] }"""), "office").PtzOff!,
                    "already the web port");
                File.WriteAllText(json, $$"""{ "web_port": 8656, "cameras": [ {{sharer.Replace("\"ptz_share\": true", "\"record\": true")}} ] }""");
                NeolinkConfig.Load(json, strict: true); // the shared port matters only once a camera uses it

                // With no login it listens on loopback only; with users, anywhere.
                AssertEq(Cam(Json($$"""{ "cameras": [ {{sharer}} ] }"""), "office").PtzOff != null, true);
                AssertEq(Cam(Json($$"""{ "users": [ { "name": "frigate", "pass": "pw" } ], "cameras": [ {{sharer}} ] }"""), "office").PtzOff, null);
                AssertEq(Cam(Json($$"""{ "users": [ { "name": "frigate", "pass": "pw" } ], "cameras": [ {{sharer.Replace("\"ptz_share\"", "\"permitted_users\": [\"anonymous\"], \"ptz_share\"")}} ] }"""),
                    "office").PtzOff != null, true); // a camera open to anonymous has no login either

                var t = NeolinkConfig.Load(toml);
                Assert(t is { PtzBind: "127.0.0.1", PtzPort: 9100 } && Cam(t, "office").PtzMode == "shared"
                       && Cam(t, "own") is { PtzMode: "own", PtzPort: 8081 }, "TOML reads the same keys");
                foreach (var bad in new[] { """{ "ptz_bind": "nvr.lan", "cameras": [] }""", """{ "ptz_port": 70000, "cameras": [] }""" })
                {
                    refused = false;
                    try { Json(bad); } catch (FormatException) { refused = true; }
                    Assert(refused, $"refused: {bad}");
                }
            }
            finally { File.Delete(json); File.Delete(toml); }

            static void StringAssert(string text, string part) => Assert(text.Contains(part, StringComparison.Ordinal), $"\"{part}\" in \"{text}\"");
        });

        Test("ONVIF PTZ: an ONVIF client signs in over TCP and drives the camera's pan/tilt", () =>
        {
            var ct = CancellationToken.None;
            var control = new PtzRecordingControl("e1");
            var users = new Dictionary<string, string> { ["frigate"] = "pw", ["other"] = "x" };
            var cam = new Onvif.OnvifPtzCamera("e1", control, new HashSet<string> { "frigate" }, () => (2560u, 1440u));
            var ptz = new Onvif.OnvifPtzServer("e1: ONVIF pan/tilt", users, new[] { cam });
            int port = FreeTcpPort();
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => ptz.RunAsync("127.0.0.1", port, stop.Token));
            try
            {
                // Listening before the client's discovery, which would otherwise back off for minutes.
                for (int i = 0; ; i++)
                {
                    try { using var probe = new System.Net.Sockets.TcpClient("127.0.0.1", port); break; }
                    catch (System.Net.Sockets.SocketException) when (i < 50) { Thread.Sleep(50); }
                }
                using (var onvif = new Protocol.OnvifClient($"127.0.0.1:{port}", "frigate", "pw", "t", generic: true))
                {
                    Assert(onvif.TryGetProfilesAsync(ct).GetAwaiter().GetResult() is { Count: 1 } && onvif.HasPtz,
                        "one profile, carrying pan/tilt");
                    Assert(onvif.TryGetPtzNodeAsync(ct).GetAwaiter().GetResult() is { PanTilt: true, Zoom: false },
                        "continuous pan/tilt and no zoom");
                    onvif.PtzMoveAsync(-0.5, 0, 0, ct).GetAwaiter().GetResult();
                    onvif.PtzStopAsync(ct).GetAwaiter().GetResult();
                    AssertEq(control.Log(), "left@32,stop@32");
                    cam.CapabilitiesRead.Wait(TimeSpan.FromSeconds(5)); // started when the endpoint came up
                    AssertEq(onvif.TryGetDeviceInfoAsync(ct).GetAwaiter().GetResult()?.Model, "E1");
                }
                // A wrong password, or a user not permitted on this camera, moves nothing.
                foreach (var (user, pass) in new[] { ("frigate", "bad"), ("other", "x") })
                {
                    using var intruder = new Protocol.OnvifClient($"127.0.0.1:{port}", user, pass, "t", generic: true);
                    intruder.TryGetProfilesAsync(ct).GetAwaiter().GetResult();
                    try { intruder.PtzMoveAsync(0.5, 0, 0, ct).GetAwaiter().GetResult(); }
                    catch (Exception ex) when (ex is NotSupportedException or IOException) { }
                }
                AssertEq(control.Log(), "left@32,stop@32");
            }
            finally
            {
                stop.Cancel();
                run.Wait(TimeSpan.FromSeconds(5));
            }
        });

        Test("ONVIF PTZ: Frigate's requests, logins, replays, clocks and the stop watchdog", () =>
        {
            var ct = CancellationToken.None;
            var control = new PtzRecordingControl("e1");
            var users = new Dictionary<string, string> { ["frigate"] = "pw", ["other"] = "x" };
            var cam = new Onvif.OnvifPtzCamera("e1", control, new HashSet<string> { "frigate" }, () => (2560u, 1440u));
            var ptz = new Onvif.OnvifPtzServer("e1", users, new[] { cam });
            var signed = PtzSigned(DateTime.UtcNow);

            // The clock and the services answer anyone; everything else wants a permitted user.
            Assert(PtzCall(ptz, "", "<tds:GetSystemDateAndTime/>") is (200, var clock) && clock.Contains("UTCDateTime"),
                "the clock needs no login");
            var caps = PtzCall(ptz, "", "<tds:GetCapabilities><tds:Category>All</tds:Category></tds:GetCapabilities>");
            AssertEq(PtzFirst(caps.Body, "PTZ").Value, "http://nvr:8081/onvif/ptz_service");
            Assert(PtzCall(ptz, "", "<trt:GetProfiles/>") is (400, var anon) && anon.Contains("ter:NotAuthorized"),
                "an unsigned GetProfiles is refused");

            // GetProfiles as Frigate reads it: named after the camera, an H264 encoder and a continuous pan/tilt space,
            // in schema order.
            var profiles = PtzCall(ptz, signed, "<trt:GetProfiles/>");
            AssertEq(profiles.Status, 200);
            var profile = PtzFirst(profiles.Body, "Profiles");
            AssertEq(profile.Attribute("token")?.Value, "e1");
            AssertEq(string.Join(",", profile.Elements().Select(e => e.Name.LocalName)),
                "Name,VideoSourceConfiguration,VideoEncoderConfiguration,PTZConfiguration");
            AssertEq(PtzFirst(profiles.Body, "Name").Value, "e1");
            AssertEq(PtzFirst(profiles.Body, "Encoding").Value, "H264");
            AssertEq(PtzFirst(profiles.Body, "Width").Value, "2560");
            Assert(PtzFirst(profiles.Body, "DefaultContinuousPanTiltVelocitySpace").Value.EndsWith("VelocityGenericSpace"),
                "Frigate's \"pt\" feature comes from this space");
            Assert(PtzCall(ptz, signed, "<trt:GetProfiles/>") is (400, var replay) && replay.Contains("already used"),
                "a captured request cannot be replayed");
            Assert(PtzCall(ptz, PtzSigned(DateTime.UtcNow.AddMinutes(-10)), "<trt:GetProfiles/>") is (400, var skew)
                   && skew.Contains("ignore_time_mismatch"), "a clock ten minutes out is refused, and says what to do");
            Assert(PtzCall(ptz, PtzSigned(DateTime.UtcNow, "other", "x"), "<trt:GetProfiles/>").Status == 400,
                "a user not permitted on this camera is refused");

            // HTTP Basic works too; the options offer continuous pan/tilt only.
            var auth = PtzBasic("frigate", "pw");
            var options = PtzCall(ptz, "", "<tptz:GetConfigurationOptions><tptz:ConfigurationToken>ptz_e1</tptz:ConfigurationToken></tptz:GetConfigurationOptions>", auth);
            AssertEq(string.Join(",", PtzFirst(options.Body, "Spaces").Elements().Select(e => e.Name.LocalName)),
                "ContinuousPanTiltVelocitySpace,PanTiltSpeedSpace");
            AssertEq(PtzCall(ptz, "", "<tptz:GetPresets/>", PtzBasic("frigate", "wrong")).Status, 400);
            Assert(PtzCall(ptz, "", "<trt:GetStreamUri/>", auth) is (500, var noStream)
                   && noStream.Contains("ter:ActionNotSupported"), "video is not served here");
            Assert(PtzCall(ptz, "", "<tptz:AbsoluteMove/>", auth).Body.Contains("ter:ActionNotSupported"),
                "nor any move but a continuous one");
            Assert(ptz.HandleAsync("<s:Envelope", "http://nvr:8081", null, "test", ct).GetAwaiter().GetResult() is (400, var bad)
                   && bad.Contains("ter:WellFormed"), "malformed XML is a Sender fault");

            // Velocity to a camera command: the stronger axis, at 1-64.
            AssertEq(Onvif.OnvifPtzServer.Direction(0.5, 0), ("right", 32f));
            AssertEq(Onvif.OnvifPtzServer.Direction(0.3, -0.6), ("down", 38f));
            AssertEq(Onvif.OnvifPtzServer.Direction(1.5, 0.2), ("right", 64f));
            AssertEq(Onvif.OnvifPtzServer.Direction(0.01, -0.02), null);
            AssertEq(Onvif.OnvifPtzServer.MoveTimeout("PT0.2S"), TimeSpan.FromSeconds(1));
            AssertEq(Onvif.OnvifPtzServer.MoveTimeout("PT5M"), TimeSpan.FromSeconds(60));
            AssertEq(Onvif.OnvifPtzServer.MoveTimeout("soon"), TimeSpan.FromSeconds(10));

            // A move whose Stop never comes ends by itself after its Timeout.
            AssertEq(PtzCall(ptz, "", "<tptz:ContinuousMove><tptz:ProfileToken>e1</tptz:ProfileToken>" +
                                      "<tptz:Velocity><tt:PanTilt x=\"0.1\" y=\"0.9\"/></tptz:Velocity>" +
                                      "<tptz:Timeout>PT1S</tptz:Timeout></tptz:ContinuousMove>", auth).Status, 200);
            var moveStatus = PtzFirst(PtzCall(ptz, "", "<tptz:GetStatus/>", auth).Body, "MoveStatus");
            Assert(cam.Moving && moveStatus.Elements().First(e => e.Name.LocalName == "PanTilt").Value == "MOVING",
                "the status says it moves");
            for (int i = 0; i < 60 && cam.Moving; i++) Thread.Sleep(50);
            AssertEq(control.Log(), "up@58,stop@32");
            // A zoom-only Stop has nothing to stop; a zero velocity is a stop.
            PtzCall(ptz, "", "<tptz:Stop><tptz:PanTilt>false</tptz:PanTilt><tptz:Zoom>true</tptz:Zoom></tptz:Stop>", auth);
            PtzCall(ptz, "", "<tptz:ContinuousMove><tptz:Velocity><tt:PanTilt x=\"0\" y=\"0\"/></tptz:Velocity></tptz:ContinuousMove>", auth);
            AssertEq(control.Log(), "up@58,stop@32,stop@32");

            // A camera that reports no pan/tilt gets a profile without it, once it has answered (read behind the
            // request, never holding it); with no users, no login is asked.
            var fixedCam = new Onvif.OnvifPtzCamera("fixed", new PtzRecordingControl("fixed", hasPtz: false), null);
            var fixedPtz = new Onvif.OnvifPtzServer("fixed", new Dictionary<string, string>(), new[] { fixedCam });
            Assert(PtzCall(fixedPtz, "", "<trt:GetProfiles/>") is (200, var early) && early.Contains("PTZConfiguration"),
                "before the camera answers, pan/tilt is assumed");
            fixedCam.CapabilitiesRead.Wait(TimeSpan.FromSeconds(5));
            Assert(PtzCall(fixedPtz, "", "<trt:GetProfiles/>") is (200, var plain) && !plain.Contains("PTZConfiguration"),
                "no pan/tilt, no PTZ configuration; and an install with no users asks no login");
        });

        Test("ONVIF PTZ: one shared port serves each camera as a profile named after it, to its permitted users", () =>
        {
            var users = new Dictionary<string, string> { ["frigate"] = "pw", ["garagist"] = "g" };
            var officeControl = new PtzRecordingControl("office");
            var garageControl = new PtzRecordingControl("garage");
            var office = new Onvif.OnvifPtzCamera("office", officeControl, new HashSet<string> { "frigate" });
            var garage = new Onvif.OnvifPtzCamera("garage", garageControl, new HashSet<string> { "frigate", "garagist" });
            var ptz = new Onvif.OnvifPtzServer("shared", users, new[] { office, garage });
            var frigate = PtzBasic("frigate", "pw");
            var garagist = PtzBasic("garagist", "g");
            static string Tokens(string body) => string.Join(",", System.Xml.Linq.XDocument.Parse(body).Descendants()
                .Where(e => e.Name.LocalName == "Profiles").Select(e => e.Attribute("token")?.Value));
            static string Move(string? profile) =>
                "<tptz:ContinuousMove>" + (profile == null ? "" : $"<tptz:ProfileToken>{profile}</tptz:ProfileToken>") +
                "<tptz:Velocity><tt:PanTilt x=\"0.5\" y=\"0\"/></tptz:Velocity></tptz:ContinuousMove>";

            AssertEq(Tokens(PtzCall(ptz, "", "<trt:GetProfiles/>", frigate).Body), "office,garage");
            AssertEq(Tokens(PtzCall(ptz, "", "<trt:GetProfiles/>", garagist).Body), "garage"); // only what it may move
            AssertEq(PtzFirst(PtzCall(ptz, "", "<tds:GetDeviceInformation/>", frigate).Body, "Manufacturer").Value, "Neolink.NET");

            // Each move goes to the camera its profile names.
            AssertEq(PtzCall(ptz, "", Move("garage"), frigate).Status, 200);
            Assert(garageControl.Log() == "right@32" && officeControl.Log() == "", "the garage moved, the office did not");
            Assert(PtzCall(ptz, "", "<tptz:GetStatus><tptz:ProfileToken>garage</tptz:ProfileToken></tptz:GetStatus>", frigate).Body.Contains(">MOVING<")
                   && PtzCall(ptz, "", "<tptz:GetStatus><tptz:ProfileToken>office</tptz:ProfileToken></tptz:GetStatus>", frigate).Body.Contains(">IDLE<"),
                "each profile reports its own camera");
            Assert(PtzCall(ptz, "", Move("office"), garagist) is (400, var hidden) && hidden.Contains("ter:InvalidArgVal"),
                "a camera the user may not move is no profile of theirs");
            Assert(PtzCall(ptz, "", Move(null), frigate) is (400, var unnamed) && unnamed.Contains("onvif.profile"),
                "with several cameras, a move must name one");
            AssertEq(PtzCall(ptz, "", Move(null), garagist).Status, 200); // one camera in view: that one
            AssertEq(garageControl.Log(), "right@32,right@32");
            PtzCall(ptz, "", "<tptz:Stop><tptz:ProfileToken>garage</tptz:ProfileToken></tptz:Stop>", frigate);
            Assert(!garage.Moving && officeControl.Log() == "", "a stop reaches only its camera");
        });

        Test("ONVIF PTZ: presets are the camera's own, listed, gone to and saved", () =>
        {
            var control = new PtzRecordingControl("e1")
            {
                Presets = new() { new(1, "Porch", true), new(2, "Gate", true), new(3, "", false), new(4, "", false) },
            };
            var cam = new Onvif.OnvifPtzCamera("e1", control, null);
            var ptz = new Onvif.OnvifPtzServer("e1", new Dictionary<string, string>(), new[] { cam });
            static string Tokens(string body) => string.Join(",", System.Xml.Linq.XDocument.Parse(body).Descendants()
                .Where(e => e.Name.LocalName == "Preset").Select(e => $"{e.Attribute("token")?.Value}={e.Value}"));
            static string Goto(string token) =>
                $"<tptz:GotoPreset><tptz:ProfileToken>e1</tptz:ProfileToken><tptz:PresetToken>{token}</tptz:PresetToken></tptz:GotoPreset>";
            static string Save(string? token, string? name) => "<tptz:SetPreset><tptz:ProfileToken>e1</tptz:ProfileToken>" +
                (name == null ? "" : $"<tptz:PresetName>{name}</tptz:PresetName>") +
                (token == null ? "" : $"<tptz:PresetToken>{token}</tptz:PresetToken>") + "</tptz:SetPreset>";

            // Only the slots in use are presets, each by the camera's own id.
            AssertEq(Tokens(PtzCall(ptz, "", "<tptz:GetPresets><tptz:ProfileToken>e1</tptz:ProfileToken></tptz:GetPresets>").Body),
                "1=Porch,2=Gate");
            AssertEq(PtzFirst(PtzCall(ptz, "", "<tptz:GetNodes/>").Body, "MaximumNumberOfPresets").Value, "4");
            AssertEq(PtzCall(ptz, "", Goto("2")).Status, 200);
            Assert(PtzCall(ptz, "", Goto("3")) is (400, var free) && free.Contains("ter:InvalidArgVal")
                   && PtzCall(ptz, "", Goto("gate")).Status == 400, "a free slot, or a name, is no preset token");

            // A save without a token fills the first free slot; one over a preset keeps its name unless given one.
            AssertEq(PtzFirst(PtzCall(ptz, "", Save(null, "Door")).Body, "PresetToken").Value, "3");
            AssertEq(PtzCall(ptz, "", Save("1", null)).Status, 200);
            AssertEq(PtzCall(ptz, "", Save(null, null)).Status, 200); // slot 4, named after itself
            Assert(PtzCall(ptz, "", Save(null, "More")) is (500, var full) && full.Contains("ter:TooManyPresets"),
                "with every slot in use, a new preset is refused");
            AssertEq(control.Log(), "preset@2,save@3:Door,save@1:Porch,save@4:preset 4");

            // A preset move retires the watchdog of a continuous move before it.
            PtzCall(ptz, "", "<tptz:ContinuousMove><tptz:Velocity><tt:PanTilt x=\"0.5\" y=\"0\"/></tptz:Velocity>" +
                             "<tptz:Timeout>PT1S</tptz:Timeout></tptz:ContinuousMove>");
            PtzCall(ptz, "", Goto("1"));
            Thread.Sleep(1500);
            Assert(control.Log().EndsWith("right@32,preset@1") && !cam.Moving, "no watchdog stop cuts the preset move short");

            // Before any GetPresets there is no list to check against: the move goes to the camera, which judges.
            var unread = new PtzRecordingControl("e1") { Presets = new() { new(2, "Gate", true) } };
            var unreadPtz = new Onvif.OnvifPtzServer("e1", new Dictionary<string, string>(),
                new[] { new Onvif.OnvifPtzCamera("e1", unread, null) });
            AssertEq(PtzCall(unreadPtz, "", Goto("2")).Status, 200);
            AssertEq(unread.Log(), "preset@2");

            // A camera without the HTTP API that keeps presets lists none, and cannot save one.
            var bare = new Onvif.OnvifPtzServer("bare", new Dictionary<string, string>(),
                new[] { new Onvif.OnvifPtzCamera("e1", new PtzRecordingControl("e1"), null) });
            AssertEq(Tokens(PtzCall(bare, "", "<tptz:GetPresets/>").Body), "");
            Assert(PtzCall(bare, "", Save(null, "Door")) is (500, var noApi) && noApi.Contains("http_address"),
                "saving needs the camera's HTTP API, and says so");
        });

        Test("ONVIF PTZ: zoom steps the lens through its own positions while held (no zoom camera to test on)", () =>
        {
            static string Zoom(double x, string timeout = "PT10S") =>
                "<tptz:ContinuousMove><tptz:Velocity><tt:Zoom x=\"" + x.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                $"\"/></tptz:Velocity><tptz:Timeout>{timeout}</tptz:Timeout></tptz:ContinuousMove>";
            static void WaitFor(Func<bool> done) { for (int i = 0; i < 100 && !done(); i++) Thread.Sleep(50); }
            int Steps(PtzRecordingControl c) => c.Log().Split(',').Count(s => s.StartsWith("zoom@"));

            var control = new PtzRecordingControl("zoomy") { Lens = (0, 100, 50) };
            var cam = new Onvif.OnvifPtzCamera("zoomy", control, null);
            var ptz = new Onvif.OnvifPtzServer("zoomy", new Dictionary<string, string>(), new[] { cam });
            cam.StartCapabilitiesRead();
            cam.CapabilitiesRead.Wait(TimeSpan.FromSeconds(5));

            // A zoom lens adds continuous zoom beside pan/tilt: Frigate's "zoom" feature.
            var profiles = PtzCall(ptz, "", "<trt:GetProfiles/>").Body;
            Assert(profiles.Contains("DefaultContinuousPanTiltVelocitySpace") && profiles.Contains("DefaultContinuousZoomVelocitySpace"),
                "the profile offers pan/tilt and zoom");
            AssertEq(string.Join(",", PtzFirst(PtzCall(ptz, "",
                    "<tptz:GetConfigurationOptions><tptz:ConfigurationToken>ptz_zoomy</tptz:ConfigurationToken></tptz:GetConfigurationOptions>").Body,
                    "Spaces").Elements().Select(e => e.Name.LocalName)),
                "ContinuousPanTiltVelocitySpace,ContinuousZoomVelocitySpace,PanTiltSpeedSpace,ZoomSpeedSpace");

            // Frigate's zoom in: a zoom velocity alone. The lens steps up from where it is; no pan/tilt command goes out.
            AssertEq(PtzCall(ptz, "", Zoom(0.5)).Status, 200);
            Assert(cam.Zooming && PtzCall(ptz, "", "<tptz:GetStatus/>").Body.Contains("<tt:Zoom>MOVING</tt:Zoom>"),
                "the status says the lens moves");
            Thread.Sleep(600);
            PtzCall(ptz, "", "<tptz:Stop><tptz:PanTilt>true</tptz:PanTilt><tptz:Zoom>true</tptz:Zoom></tptz:Stop>");
            var steps = Steps(control);
            Assert(!cam.Zooming && control.Log().StartsWith("zoom@56,zoom@62"), $"stepped in, then stopped: {control.Log()}");
            Thread.Sleep(400);
            AssertEq(Steps(control), steps); // nothing after the stop

            // Zooming out at full speed ends at the lens's end of its own accord.
            PtzCall(ptz, "", Zoom(-1));
            WaitFor(() => !cam.Zooming);
            Assert(!cam.Zooming && control.Log().EndsWith("zoom@0"), $"zoomed out to the end: {control.Log()}");

            // A held zoom whose Stop never comes ends with its timeout.
            var slow = new PtzRecordingControl("slow") { Lens = (0, 1000, 0) };
            var slowCam = new Onvif.OnvifPtzCamera("slow", slow, null);
            var slowPtz = new Onvif.OnvifPtzServer("slow", new Dictionary<string, string>(), new[] { slowCam });
            PtzCall(slowPtz, "", Zoom(0.06, "PT1S")); // 8 of 1000 per step: far from the end in a second
            WaitFor(() => !slowCam.Zooming);
            Assert(!slowCam.Zooming && Steps(slow) is > 0 and <= 6, $"the zoom timed out after about a second: {slow.Log()}");

            // A varifocal camera without pan/tilt still gets a PTZ configuration, for zoom alone.
            var bullet = new Onvif.OnvifPtzCamera("bullet", new PtzRecordingControl("bullet", hasPtz: false) { Lens = (0, 100, 0) }, null);
            bullet.StartCapabilitiesRead();
            bullet.CapabilitiesRead.Wait(TimeSpan.FromSeconds(5));
            var zoomOnly = PtzCall(new Onvif.OnvifPtzServer("bullet", new Dictionary<string, string>(), new[] { bullet }),
                "", "<trt:GetProfiles/>").Body;
            Assert(zoomOnly.Contains("DefaultContinuousZoomVelocitySpace") && !zoomOnly.Contains("DefaultContinuousPanTiltVelocitySpace"),
                "zoom only");
        });

        Test("ONVIF PTZ: the stop watchdog retries, and stands down for another UI's command", () =>
        {
            var ct = CancellationToken.None;
            static string Move(double x, double y, string timeout)
            {
                var (px, py) = (x.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                y.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return $"<s:Envelope xmlns:s=\"{Onvif.OnvifPtzServer.NsSoap}\" xmlns:tptz=\"{Onvif.OnvifPtzServer.NsPtz}\" " +
                       $"xmlns:tt=\"{Onvif.OnvifPtzServer.NsSchema}\"><s:Body><tptz:ContinuousMove><tptz:Velocity>" +
                       $"<tt:PanTilt x=\"{px}\" y=\"{py}\"/></tptz:Velocity><tptz:Timeout>{timeout}</tptz:Timeout>" +
                       "</tptz:ContinuousMove></s:Body></s:Envelope>";
            }
            static void WaitFor(Func<bool> done) { for (int i = 0; i < 100 && !done(); i++) Thread.Sleep(50); }
            (Onvif.OnvifPtzCamera, Onvif.OnvifPtzServer) Endpoint(PtzRecordingControl control)
            {
                var cam = new Onvif.OnvifPtzCamera("e1", control, null) { StopRetryDelay = TimeSpan.FromMilliseconds(100) };
                return (cam, new Onvif.OnvifPtzServer("e1", new Dictionary<string, string>(), new[] { cam }));
            }

            // Two failed stops, then one that lands.
            var flaky = new PtzRecordingControl("e1") { FailStops = 2 };
            var (cam, ptz) = Endpoint(flaky);
            ptz.HandleAsync(Move(0.5, 0, "PT1S"), "http://nvr", null, "test", ct).GetAwaiter().GetResult();
            WaitFor(() => !cam.Moving);
            AssertEq(flaky.Log(), "right@32,stop@32");

            // Every stop fails: after the last try the status stops claiming a move.
            var dead = new PtzRecordingControl("e1") { FailStops = 10 };
            (cam, ptz) = Endpoint(dead);
            ptz.HandleAsync(Move(0, -0.5, "PT1S"), "http://nvr", null, "test", ct).GetAwaiter().GetResult();
            WaitFor(() => !cam.Moving);
            Assert(!cam.Moving && dead.Log() == "down@32", "the watchdog gives up after its tries");

            // The web panel drives the head after an ONVIF move: the watchdog leaves that move alone.
            var shared = new PtzRecordingControl("e1");
            (cam, ptz) = Endpoint(shared);
            ptz.HandleAsync(Move(0.5, 0, "PT1S"), "http://nvr", null, "test", ct).GetAwaiter().GetResult();
            shared.PtzAsync("left", 20, ct).GetAwaiter().GetResult();
            Thread.Sleep(1500);
            Assert(shared.Log() == "right@32,left@20" && cam.Moving, "no stop from the watchdog; the status follows the panel");
            shared.PtzAsync("stop", 32, ct).GetAwaiter().GetResult();
            Assert(!cam.Moving, "and the panel's stop ends it");
        });

        Test("ONVIF PTZ: HTTP framing and connection limits", () =>
        {
            var ptz = new Onvif.OnvifPtzServer("e1", new Dictionary<string, string>(),
                new[] { new Onvif.OnvifPtzCamera("e1", new PtzRecordingControl("e1"), null) });
            int port = FreeTcpPort();
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => ptz.RunAsync("127.0.0.1", port, stop.Token));
            var soap = $"<s:Envelope xmlns:s=\"{Onvif.OnvifPtzServer.NsSoap}\" xmlns:tds=\"{Onvif.OnvifPtzServer.NsDevice}\">" +
                       "<s:Body><tds:GetSystemDateAndTime/></s:Body></s:Envelope>";
            string Post(string version = "HTTP/1.1", string extra = "", string? body = null) =>
                $"POST /onvif/device_service {version}\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/soap+xml\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body ?? soap)}\r\n{extra}\r\n{body ?? soap}";
            System.Net.Sockets.TcpClient Connect()
            {
                for (int i = 0; ; i++)
                {
                    try
                    {
                        var c = new System.Net.Sockets.TcpClient("127.0.0.1", port);
                        c.GetStream().ReadTimeout = 5000;
                        return c;
                    }
                    catch (System.Net.Sockets.SocketException) when (i < 50) { Thread.Sleep(50); }
                }
            }
            static void Send(System.Net.Sockets.TcpClient c, string text) => c.GetStream().Write(Encoding.UTF8.GetBytes(text));
            // One response: its status line and body, or "" once the server has closed the connection.
            static string Read(System.Net.Sockets.TcpClient c)
            {
                var s = c.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                try
                {
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (s.Read(one) == 0) return "";
                        head.Append((char)one[0]);
                    }
                }
                catch (IOException) { return ""; }
                var m = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Content-Length: (\d+)");
                var body = new byte[m.Success ? int.Parse(m.Groups[1].Value) : 0];
                for (int got = 0, n; got < body.Length && (n = s.Read(body, got, body.Length - got)) > 0;) got += n;
                return head.ToString().Split("\r\n")[0] + (head.ToString().Contains("Connection: close") ? " [close]" : "") +
                       " " + Encoding.UTF8.GetString(body);
            }
            try
            {
                // Two requests in one write, answered in order on the same connection.
                using (var c = Connect())
                {
                    Send(c, Post() + Post());
                    Assert(Read(c).StartsWith("HTTP/1.1 200 OK <?xml") && Read(c).StartsWith("HTTP/1.1 200 OK <?xml"),
                        "pipelined requests both answered");
                    Send(c, Post());
                    Assert(Read(c).StartsWith("HTTP/1.1 200 OK <?xml"), "and the connection stays open for more");
                }
                using (var c = Connect())
                {
                    Send(c, $"POST /onvif/device_service HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\n" +
                            $"Content-Length: {Encoding.UTF8.GetByteCount(soap)}\r\n\r\n");
                    AssertEq(Read(c), "HTTP/1.1 100 Continue ");
                    Send(c, soap);
                    Assert(Read(c).StartsWith("HTTP/1.1 200 OK"), "the body follows the 100 Continue");
                }
                using (var c = Connect())
                {
                    Send(c, Post("HTTP/1.0"));
                    Assert(Read(c).StartsWith("HTTP/1.1 200 OK [close]") && Read(c) == "", "HTTP/1.0 without keep-alive closes");
                }
                foreach (var (request, expected) in new[]
                         {
                             ($"GET /onvif/device_service HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 405 Method Not Allowed [close] "),
                             ("POST /onvif/device_service HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 411 Length Required [close] "),
                             ("POST /onvif/device_service HTTP/1.1\r\nHost: x\r\nContent-Length: 999999\r\n\r\n",
                              "HTTP/1.1 413 Content Too Large [close] "),
                         })
                {
                    using var c = Connect();
                    Send(c, request);
                    AssertEq(Read(c), expected);
                }

                // An address holds at most four connections; a fifth is closed until one goes.
                var idle = Enumerable.Range(0, 4).Select(_ => Connect()).ToList();
                Thread.Sleep(200);
                using (var fifth = Connect())
                {
                    try { Send(fifth, Post()); } catch (IOException) { }
                    AssertEq(Read(fifth), "");
                }
                foreach (var c in idle) c.Dispose();
                string answer = "";
                for (int i = 0; i < 40 && answer == ""; i++)
                {
                    Thread.Sleep(50);
                    using var c = Connect();
                    try { Send(c, Post()); } catch (IOException) { continue; }
                    answer = Read(c);
                }
                Assert(answer.StartsWith("HTTP/1.1 200 OK"), "a slot frees once a connection closes");
            }
            finally
            {
                stop.Cancel();
                run.Wait(TimeSpan.FromSeconds(5));
            }
        });

        Test("ONVIF presets keep their ids when others are removed in the camera's own app", () =>
        {
            var ct = CancellationToken.None;
            var presets = new List<string> { "A", "B", "C" };
            var gone = new List<string>();
            using var cam = new FakeOnvif((op, raw) =>
            {
                lock (presets)
                    switch (op)
                    {
                        case "GetCapabilities":
                            var host = System.Text.RegularExpressions.Regex.Match(raw, @"Host: ([^\r\n]+)").Groups[1].Value;
                            return FakeOnvif.Ok("<tds:GetCapabilitiesResponse xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\" " +
                                "xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tds:Capabilities><tt:PTZ><tt:XAddr>" +
                                $"http://{host}/onvif/ptz_service</tt:XAddr></tt:PTZ></tds:Capabilities></tds:GetCapabilitiesResponse>");
                        case "GetPresets":
                            return FakeOnvif.Ok("<tptz:GetPresetsResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                                string.Concat(presets.Select(p => $"<tptz:Preset token=\"{p}\"><tt:Name xmlns:tt=\"http://www.onvif.org/ver10/schema\">{p}</tt:Name></tptz:Preset>")) +
                                "</tptz:GetPresetsResponse>");
                        case "SetPreset":
                            presets.Add("N1");
                            return FakeOnvif.Ok("<tptz:SetPresetResponse xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
                                                "<tptz:PresetToken>N1</tptz:PresetToken></tptz:SetPresetResponse>");
                        case "GotoPreset":
                            gone.Add(System.Text.RegularExpressions.Regex.Match(raw, "PresetToken>([^<]+)<").Groups[1].Value);
                            return FakeOnvif.Answer(op);
                        default:
                            return FakeOnvif.Answer(op);
                    }
            });
            using var onvif = new Protocol.OnvifClient($"127.0.0.1:{cam.Port}", "admin", "pw", "t", generic: true);
            onvif.TryGetProfilesAsync(ct).GetAwaiter().GetResult();
            var first = onvif.TryGetPresetsAsync(ct).GetAwaiter().GetResult()!;
            AssertEq(string.Join(",", first.Select(p => $"{p.Token}={p.Id}")), "A=1,B=2,C=3");
            lock (presets) presets.Remove("A");
            var later = onvif.TryGetPresetsAsync(ct).GetAwaiter().GetResult()!;
            AssertEq(string.Join(",", later.Select(p => $"{p.Token}={p.Id}")), "B=2,C=3");
            onvif.GotoPresetAsync(2, ct).GetAwaiter().GetResult();
            AssertEq(gone.Last(), "B");
            onvif.SavePresetAsync(1, "new", ct).GetAwaiter().GetResult();
            var saved = onvif.TryGetPresetsAsync(ct).GetAwaiter().GetResult()!;
            Assert(saved.Any(p => p is { Token: "N1", Id: 1 }), "a new preset takes the id it was saved into");
        });

        Test("a stored zone's shape cannot overflow into a valid-looking grid", () =>
        {
            // 65536 x 65537 wraps to 65536 in 32 bits — exactly a legal table length.
            var z = new Web.CameraStateStore.StoredZone { Cols = 65536, Rows = 65537, Table = new string('1', 65536) };
            Assert(!z.IsWellFormed, "a wrapped product must not pass as a real grid");
            var ok = new Web.CameraStateStore.StoredZone { Cols = 32, Rows = 18, Table = new string('0', 576) };
            Assert(ok.IsWellFormed, "an ordinary grid still does");
        });

        Test("ONVIF Media2: profiles, encoder and options in the newer dialect", () =>
        {
            static System.Xml.Linq.XElement Xml(string s) => System.Xml.Linq.XDocument.Parse(s).Root!;
            // Media2 groups a profile's parts under Configurations, drops the
            // "Configuration" suffix, moves GovLength to an attribute and allows a
            // fractional frame rate. One parse must read both dialects the same way.
            var profiles = Protocol.OnvifClient.ParseProfiles(Xml("""
                <tr2:GetProfilesResponse xmlns:tr2="http://www.onvif.org/ver20/media/wsdl"
                                         xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tr2:Profiles token="Profile_1" fixed="true">
                    <tr2:Name>mainStream</tr2:Name>
                    <tr2:Configurations>
                      <tr2:VideoSource token="VSC_1"><tt:Name>vs</tt:Name><tt:SourceToken>VS_1</tt:SourceToken></tr2:VideoSource>
                      <tr2:VideoEncoder token="VEC_1" GovLength="50" Profile="Main">
                        <tt:Name>enc</tt:Name>
                        <tt:Encoding>H265</tt:Encoding>
                        <tt:Resolution><tt:Width>3840</tt:Width><tt:Height>2160</tt:Height></tt:Resolution>
                        <tt:RateControl ConstantBitRate="false">
                          <tt:FrameRateLimit>12.5</tt:FrameRateLimit>
                          <tt:BitrateLimit>6144</tt:BitrateLimit>
                        </tt:RateControl>
                        <tt:Quality>3</tt:Quality>
                      </tr2:VideoEncoder>
                      <tr2:Analytics token="VAC_1"><tt:Name>va</tt:Name></tr2:Analytics>
                      <tr2:PTZ token="PTZ_1"><tt:Name>ptz</tt:Name></tr2:PTZ>
                    </tr2:Configurations>
                  </tr2:Profiles>
                </tr2:GetProfilesResponse>
                """));
            Assert(profiles.Count == 1, "the Media2 profile is found");
            var p = profiles[0];
            Assert(p is { Token: "Profile_1", Name: "mainStream", HasPtz: true, VideoSourceToken: "VSC_1",
                SourceToken: "VS_1", AnalyticsToken: "VAC_1" }, "every part is read from under Configurations");
            Assert(p.EncoderToken == "VEC_1" && p.Encoding == "H265" && p.Width == 3840 && p.Height == 2160,
                "the encoder is found by its Media2 name");
            AssertEq(p.GovLength, 50);  // an attribute in Media2
            AssertEq(p.FrameRate, 13);  // 12.5 rounds rather than failing to parse
            AssertEq(p.Bitrate, 6144);

            // Media2's options: one <Options> per codec, the codec named in a child
            // element, and the frame rates as a list rather than a range.
            var options = Xml("""
                <tr2:GetVideoEncoderConfigurationOptionsResponse xmlns:tr2="http://www.onvif.org/ver20/media/wsdl"
                                                                 xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tr2:Options GovLengthRange="1 400" FrameRatesSupported="25 20 15 10 5">
                    <tt:Encoding>H264</tt:Encoding>
                    <tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>
                    <tt:BitrateRange><tt:Min>64</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange>
                  </tr2:Options>
                  <tr2:Options GovLengthRange="1 400" FrameRatesSupported="12.5 6.25 1">
                    <tt:Encoding>H265</tt:Encoding>
                    <tt:ResolutionsAvailable><tt:Width>3840</tt:Width><tt:Height>2160</tt:Height></tt:ResolutionsAvailable>
                    <tt:ResolutionsAvailable><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:ResolutionsAvailable>
                    <tt:BitrateRange><tt:Min>128</tt:Min><tt:Max>16384</tt:Max></tt:BitrateRange>
                  </tr2:Options>
                </tr2:GetVideoEncoderConfigurationOptionsResponse>
                """);
            var h265 = Protocol.OnvifClient.ParseEncoderOptions(options, "H265");
            Assert(h265.Resolutions.Count == 2 && h265.Resolutions[0] == (3840, 2160),
                "an H265 profile gets the H265 block's resolutions, largest first");
            Assert(h265.FrameRate == (1, 13), "the frame-rate list becomes the range it spans");
            Assert(h265.Bitrate == (128, 16384), "and its own bitrate range");
            var h264 = Protocol.OnvifClient.ParseEncoderOptions(options, "H264");
            Assert(h264.Resolutions.Count == 1 && h264.FrameRate == (5, 25) && h264.Bitrate == (64, 8192),
                "the H264 block is kept apart from the H265 one");
            // A list of rates is offered as that list, not as everything it spans:
            // 12.5 and 6.25 cannot be written as whole numbers, so they are left out.
            Assert(h264.FrameRatesListed is { } l264 && l264.SequenceEqual(new[] { 5, 10, 15, 20, 25 }),
                "the listed rates are the menu");
            Assert(h265.FrameRatesListed is { } l265 && l265.SequenceEqual(new[] { 1 }), "whole rates only");

            // ver10 keeps bitrate ranges in Extension blocks named per codec, and the
            // schema puts JPEG's first: an H264 profile must get H264's.
            var extensions = Protocol.OnvifClient.ParseEncoderOptions(Xml("""
                <trt:GetVideoEncoderConfigurationOptionsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                                                 xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:Options>
                    <tt:H264>
                      <tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>
                    </tt:H264>
                    <tt:Extension>
                      <tt:JPEG><tt:BitrateRange><tt:Min>1000</tt:Min><tt:Max>2000</tt:Max></tt:BitrateRange></tt:JPEG>
                      <tt:H264><tt:BitrateRange><tt:Min>32</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange></tt:H264>
                    </tt:Extension>
                  </trt:Options>
                </trt:GetVideoEncoderConfigurationOptionsResponse>
                """), "H264");
            Assert(extensions.Bitrate == (32, 8192), "the codec's own Extension, not JPEG's");
        });

        Test("ONVIF PTZ: which axes the head drives, and the zoom slider's scale", () =>
        {
            static System.Xml.Linq.XElement Xml(string s) => System.Xml.Linq.XDocument.Parse(s).Root!;
            // A motorised varifocal lens: a PTZ node with a zoom and no pan or tilt.
            // Offering it arrows would be offering buttons that do nothing.
            var lens = Protocol.OnvifClient.ParsePtzNode(Xml("""
                <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                       xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZNode token="Node_1" FixedHomePosition="false">
                    <tt:Name>lens</tt:Name>
                    <tt:SupportedPTZSpaces>
                      <tt:AbsoluteZoomPositionSpace>
                        <tt:URI>http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace</tt:URI>
                        <tt:XRange><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      </tt:AbsoluteZoomPositionSpace>
                      <tt:ContinuousZoomVelocitySpace>
                        <tt:URI>http://www.onvif.org/ver10/tptz/ZoomSpaces/VelocityGenericSpace</tt:URI>
                        <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      </tt:ContinuousZoomVelocitySpace>
                    </tt:SupportedPTZSpaces>
                    <tt:MaximumNumberOfPresets>8</tt:MaximumNumberOfPresets>
                    <tt:HomeSupported>false</tt:HomeSupported>
                  </tptz:PTZNode>
                </tptz:GetNodesResponse>
                """));
            Assert(lens is { PanTilt: false, Zoom: true, MaxPresets: 8 }, "a zoom-only node offers no arrows");
            AssertEq(lens!.ZoomRange, (0.0, 1.0));

            var dome = Protocol.OnvifClient.ParsePtzNode(Xml("""
                <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                       xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZNode token="Node_1">
                    <tt:SupportedPTZSpaces>
                      <tt:ContinuousPanTiltVelocitySpace>
                        <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                      </tt:ContinuousPanTiltVelocitySpace>
                    </tt:SupportedPTZSpaces>
                  </tptz:PTZNode>
                </tptz:GetNodesResponse>
                """));
            Assert(dome is { PanTilt: true, Zoom: false, MaxPresets: null, AnyZoom: false },
                "a head that only pans and tilts gets no zoom slider");

            // A multi-sensor camera has a node per head: the profile's own node is
            // the one that counts, and among zoom spaces the generic one is chosen.
            var twoNodes = Xml("""
                <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                       xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZNode token="Fixed">
                    <tt:SupportedPTZSpaces/>
                  </tptz:PTZNode>
                  <tptz:PTZNode token="Dome">
                    <tt:SupportedPTZSpaces>
                      <tt:AbsoluteZoomPositionSpace>
                        <tt:URI>http://vendor.example/ZoomSpaces/Millimetres</tt:URI>
                        <tt:XRange><tt:Min>4</tt:Min><tt:Max>120</tt:Max></tt:XRange>
                      </tt:AbsoluteZoomPositionSpace>
                      <tt:AbsoluteZoomPositionSpace>
                        <tt:URI>http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace</tt:URI>
                        <tt:XRange><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      </tt:AbsoluteZoomPositionSpace>
                      <tt:ContinuousPanTiltVelocitySpace><tt:URI>x</tt:URI></tt:ContinuousPanTiltVelocitySpace>
                    </tt:SupportedPTZSpaces>
                  </tptz:PTZNode>
                </tptz:GetNodesResponse>
                """);
            var domeNode = Protocol.OnvifClient.ParsePtzNode(twoNodes, "Dome");
            Assert(domeNode is { PanTilt: true, Zoom: true, AnyZoom: true }, "the profile's node, not the first one");
            AssertEq(domeNode!.ZoomRange, (0.0, 1.0));
            AssertEq(domeNode.ZoomSpace, "http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace");
            Assert(Protocol.OnvifClient.ParsePtzNode(twoNodes) is { PanTilt: false, Zoom: false },
                "with no token to go by, the first node");
            // The arrows send ContinuousMove: a head with only relative/absolute spaces gets none.
            var stepper = Protocol.OnvifClient.ParsePtzNode(Xml("""
                <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                       xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZNode token="Node_1">
                    <tt:SupportedPTZSpaces>
                      <tt:RelativePanTiltTranslationSpace><tt:URI>x</tt:URI></tt:RelativePanTiltTranslationSpace>
                      <tt:AbsolutePanTiltPositionSpace><tt:URI>y</tt:URI></tt:AbsolutePanTiltPositionSpace>
                    </tt:SupportedPTZSpaces>
                  </tptz:PTZNode>
                </tptz:GetNodesResponse>
                """));
            Assert(stepper is { PanTilt: false }, "no continuous pan/tilt space, no arrows");

            var moving = Xml("""
                <tptz:GetStatusResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                        xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZStatus><tt:Position><tt:Zoom x="0.4"/></tt:Position>
                    <tt:MoveStatus><tt:PanTilt>IDLE</tt:PanTilt><tt:Zoom>MOVING</tt:Zoom></tt:MoveStatus>
                  </tptz:PTZStatus>
                </tptz:GetStatusResponse>
                """);
            Assert(Protocol.OnvifClient.ParseZoomMoving(moving) == true, "a lens on its way says so");
            Assert(Protocol.OnvifClient.ParseZoomMoving(Xml("<a><Position/></a>")) == null,
                "a camera that does not report movement is not guessed at");

            AssertEq(Protocol.OnvifClient.ParseZoomPosition(Xml("""
                <tptz:GetStatusResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                        xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZStatus>
                    <tt:Position>
                      <tt:PanTilt x="0.1" y="-0.2"/>
                      <tt:Zoom x="0.375"/>
                    </tt:Position>
                    <tt:MoveStatus><tt:Zoom>IDLE</tt:Zoom></tt:MoveStatus>
                  </tptz:PTZStatus>
                </tptz:GetStatusResponse>
                """)), 0.375);
            // Some cameras report only whether they are moving. The MoveStatus Zoom
            // must not be mistaken for a position.
            Assert(Protocol.OnvifClient.ParseZoomPosition(Xml("""
                <tptz:GetStatusResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                        xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZStatus><tt:MoveStatus><tt:Zoom>IDLE</tt:Zoom></tt:MoveStatus></tptz:PTZStatus>
                </tptz:GetStatusResponse>
                """)) == null, "no position reported -> no slider, rather than one parked at zero");

            // The panel's slider is whole steps 0..100 whatever the camera's range.
            AssertEq(Streaming.GenericCameraControl.ZoomStep(0.375, (0, 1)), 38L);
            AssertEq(Streaming.GenericCameraControl.ZoomStep(5, (0, 10)), 50L);
            AssertEq(Streaming.GenericCameraControl.ZoomStep(2, (0, 1)), 100L); // out of range clamps
            AssertEq(Streaming.GenericCameraControl.ZoomPosition(25, (0, 1)), 0.25);
            AssertEq(Streaming.GenericCameraControl.ZoomPosition(500, (0, 1)), 1.0); // never past the end
        });

        Test("ONVIF cell motion: the camera's own grid is read and written exactly", () =>
        {
            static System.Xml.Linq.XElement Xml(string s) => System.Xml.Linq.XDocument.Parse(s).Root!;
            // The spec's own worked example: ff ff ff f0 f0 f0 PackBits to fe ff fe f0,
            // which is "/v/+8A==" in base64. If this drifts, every camera reads wrong.
            var packed = Protocol.OnvifClient.PackBits(new byte[] { 0xff, 0xff, 0xff, 0xf0, 0xf0, 0xf0 });
            AssertEq(Convert.ToHexString(packed), "FEFFFEF0");
            AssertEq(Convert.ToBase64String(packed), "/v/+8A==");
            var back = Protocol.OnvifClient.UnpackBits(packed);
            AssertEq(Convert.ToHexString(back!), "FFFFFFF0F0F0");

            // Literals, runs, the 128-byte limit on both, and the -128 no-op.
            var rng = new Random(7);
            foreach (var len in new[] { 1, 2, 3, 127, 128, 129, 300 })
            {
                var data = new byte[len];
                for (int i = 0; i < len; i++) data[i] = (byte)(i % 5 == 0 ? rng.Next(256) : i < len / 2 ? 0xAA : rng.Next(3));
                var rt = Protocol.OnvifClient.UnpackBits(Protocol.OnvifClient.PackBits(data));
                Assert(rt != null && rt.AsSpan().SequenceEqual(data), $"PackBits round trip, {len} bytes");
            }
            var run = new byte[300];
            Array.Fill(run, (byte)0x5A);
            var rp = Protocol.OnvifClient.UnpackBits(Protocol.OnvifClient.PackBits(run));
            Assert(rp != null && rp.All(b => b == 0x5A) && rp.Length == 300, "a run longer than 128 splits cleanly");
            AssertEq(Convert.ToHexString(Protocol.OnvifClient.UnpackBits(new byte[] { 0x80, 0x00, 0x7F })!), "7F");
            Assert(Protocol.OnvifClient.UnpackBits(new byte[] { 0x05, 0x01 }) == null,
                "a header promising more than the data holds is refused, not read past");

            // Cells run left to right, top to bottom, most significant bit first.
            // A 5x3 grid with only the middle row watched:
            // 00000 11111 00000 -> 0000 0111 | 1100 0000 -> 07 C0.
            const string middle = "000001111100000";
            var cells = Protocol.OnvifClient.EncodeActiveCells(middle);
            AssertEq(Convert.ToHexString(Protocol.OnvifClient.UnpackBits(Convert.FromBase64String(cells))!), "07C0");
            AssertEq(Protocol.OnvifClient.DecodeActiveCells(cells, 5, 3), middle);
            // A realistic 22x18 layout survives the round trip.
            var big = new string(Enumerable.Range(0, 22 * 18).Select(i => (i * 7 % 11) < 4 ? '0' : '1').ToArray());
            AssertEq(Protocol.OnvifClient.DecodeActiveCells(Protocol.OnvifClient.EncodeActiveCells(big), 22, 18), big);
            // The spec's example read as an 8x6 grid: three rows watched in full, then
            // three watched on their left half only.
            var spec = Protocol.OnvifClient.DecodeActiveCells("/v/+8A==", 8, 6)!;
            Assert(spec.StartsWith(new string('1', 24)) && spec.Substring(24, 8) == "11110000",
                "the spec's example grid decodes to the cells it describes");
            Assert(Protocol.OnvifClient.DecodeActiveCells("not base64!", 5, 3) == null, "garbage is not a grid");
            // Hikvision's factory default for its 22x18 grid: every cell watched.
            AssertEq(Protocol.OnvifClient.DecodeActiveCells("0P8A8A==", 22, 18),
                new string('1', 22 * 18).Substring(0, 396));
            // Strict about the size: a grid read wrongly would be shown as the
            // camera's and written back over it. Too short and too long both fail —
            // an uncompressed bitmask sent by mistake decodes to the wrong length.
            Assert(Protocol.OnvifClient.DecodeActiveCells("/v/+8A==", 22, 18) == null, "too short for the layout");
            Assert(Protocol.OnvifClient.DecodeActiveCells("0P8A8A==", 5, 3) == null, "too long for the layout");
            var raw = Convert.ToBase64String(Enumerable.Repeat((byte)0x00, 50).ToArray());
            Assert(Protocol.OnvifClient.DecodeActiveCells(raw, 22, 18) == null,
                "an uncompressed mask is not mistaken for a PackBits one");
            // Which replies are the camera's lasting word, and which are asked again.
            // The status alone says little: a rejected login is a 400 and a busy camera a
            // 500, the same codes as "no such action". The fault's SUBCODE tells them apart.
            Assert(Protocol.OnvifClient.IsLastingRefusal(404) && Protocol.OnvifClient.IsLastingRefusal(405),
                "a missing endpoint is lasting");
            Assert(Protocol.OnvifClient.IsLastingRefusal(500, "ter:Receiver: ter:ActionNotSupported")
                   && Protocol.OnvifClient.IsLastingRefusal(400, "ter:Sender: ter:InvalidArgVal: ter:NoConfig"),
                "an unsupported action or a missing configuration is lasting");
            Assert(!Protocol.OnvifClient.IsLastingRefusal(400, "ter:Sender: ter:NotAuthorized: Sender not Authorized")
                   && !Protocol.OnvifClient.IsLastingRefusal(500) && !Protocol.OnvifClient.IsLastingRefusal(400)
                   && !Protocol.OnvifClient.IsLastingRefusal(0) && !Protocol.OnvifClient.IsLastingRefusal(401)
                   && !Protocol.OnvifClient.IsLastingRefusal(403) && !Protocol.OnvifClient.IsLastingRefusal(503)
                   && !Protocol.OnvifClient.IsLastingRefusal(429),
                "silence, an auth refusal (lockout, clock), a bare 4xx/5xx or a busy reply is asked again");

            // Where the grid lives: the layout on the module, the cells on the rule.
            var modules = Xml("""
                <tan:GetAnalyticsModulesResponse xmlns:tan="http://www.onvif.org/ver20/analytics/wsdl"
                                                 xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tan:AnalyticsModule Name="MyTamper" Type="tt:TamperEngine"/>
                  <tan:AnalyticsModule Name="MyCellMotionModule" Type="tt:CellMotionEngine">
                    <tt:Parameters>
                      <tt:SimpleItem Name="Sensitivity" Value="60"/>
                      <tt:ElementItem Name="Layout">
                        <tt:CellLayout Columns="22" Rows="18">
                          <tt:Transformation>
                            <tt:Translate x="-1.0" y="-1.0"/>
                            <tt:Scale x="0.090909" y="0.111111"/>
                          </tt:Transformation>
                        </tt:CellLayout>
                      </tt:ElementItem>
                    </tt:Parameters>
                  </tan:AnalyticsModule>
                </tan:GetAnalyticsModulesResponse>
                """);
            AssertEq(Protocol.OnvifClient.ParseCellLayout(modules), (22, 18));
            Assert(Protocol.OnvifClient.ParseCellLayout(Xml("""
                <tan:GetAnalyticsModulesResponse xmlns:tan="http://www.onvif.org/ver20/analytics/wsdl"
                                                 xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tan:AnalyticsModule Name="MyTamper" Type="tt:TamperEngine"/>
                </tan:GetAnalyticsModulesResponse>
                """)) == null, "no cell motion module -> no grid of its own");

            // The rule is written back WHOLE: only ActiveCells changes, and its Type —
            // a QName whose prefix was declared on the reply's envelope — still
            // resolves once the element travels on its own.
            var rules = Xml("""
                <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope"
                              xmlns:tan="http://www.onvif.org/ver20/analytics/wsdl"
                              xmlns:ns9="http://www.onvif.org/ver10/schema">
                  <env:Body><tan:GetRulesResponse>
                    <tan:Rule Name="MyLineRule" Type="ns9:LineDetector">
                      <ns9:Parameters><ns9:SimpleItem Name="Direction" Value="Any"/></ns9:Parameters>
                    </tan:Rule>
                    <tan:Rule Name="MyMotionDetectorRule" Type="ns9:CellMotionDetector">
                      <ns9:Parameters>
                        <ns9:SimpleItem Name="MinCount" Value="5"/>
                        <ns9:SimpleItem Name="AlarmOnDelay" Value="100"/>
                        <ns9:SimpleItem Name="AlarmOffDelay" Value="1000"/>
                        <ns9:SimpleItem Name="ActiveCells" Value="/v/+8A=="/>
                      </ns9:Parameters>
                    </tan:Rule>
                  </tan:GetRulesResponse></env:Body>
                </env:Envelope>
                """);
            var rule = Protocol.OnvifClient.FindCellRule(rules);
            AssertEq((string?)rule?.Attribute("Name"), "MyMotionDetectorRule");
            var written = Xml(Protocol.OnvifClient.RuleForWrite(rule!, "AAAA"));
            AssertEq(written.Name.NamespaceName, "http://www.onvif.org/ver20/analytics/wsdl");
            AssertEq(written.Name.LocalName, "Rule");
            AssertEq((string?)written.Attribute("Name"), "MyMotionDetectorRule");
            var type = (string)written.Attribute("Type")!;
            AssertEq(written.GetNamespaceOfPrefix(type[..type.IndexOf(':')])?.NamespaceName,
                "http://www.onvif.org/ver10/schema");
            AssertEq(type[(type.IndexOf(':') + 1)..], "CellMotionDetector");
            var items = written.Descendants().Where(e => e.Name.LocalName == "SimpleItem")
                .ToDictionary(e => (string)e.Attribute("Name")!, e => (string)e.Attribute("Value")!);
            Assert(items is { Count: 4 } && items["MinCount"] == "5" && items["AlarmOnDelay"] == "100"
                   && items["AlarmOffDelay"] == "1000" && items["ActiveCells"] == "AAAA",
                "the rule's other parameters go back exactly as the camera had them");
            Assert(Protocol.OnvifClient.FindCellRule(Xml("""
                <tan:GetRulesResponse xmlns:tan="http://www.onvif.org/ver20/analytics/wsdl"
                                      xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tan:Rule Name="Line" Type="tt:LineDetector"/>
                </tan:GetRulesResponse>
                """)) == null, "no cell motion rule -> no grid of its own");
        });

        Test("detection zones Neolink stores for cameras that keep none", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "neolink-selftest-zone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // The default grid follows the picture's shape, so cells stay square.
                AssertEq(Web.CameraStateStore.DefaultZoneGrid(1920, 1080), (32, 18));
                AssertEq(Web.CameraStateStore.DefaultZoneGrid(1280, 960), (24, 18));
                AssertEq(Web.CameraStateStore.DefaultZoneGrid(0, 0), (32, 18)); // nothing streaming yet
                // An ultra-wide panorama would otherwise produce unclickable cells.
                Assert(Web.CameraStateStore.DefaultZoneGrid(5120, 1552).Cols == 48, "very wide is clamped");

                var store = new Web.CameraStateStore(dir);
                Assert(store.Zone("Gate", "md") == null, "no zone stored yet");
                store.SetZone("Gate", "md", 4, 2, "11001111");
                var read = store.Zone("Gate", "md");
                Assert(read is { Cols: 4, Rows: 2, Table: "11001111" }, "zone stored and read back");
                Assert(store.Zone("Gate", "people") == null, "a type with no zone stays absent");

                // Dimensions are the server's to declare; a table that does not add
                // up must never reach the file.
                bool refused = false;
                try { store.SetZone("Gate", "md", 4, 2, "1100"); }
                catch (ArgumentException) { refused = true; }
                Assert(refused, "a wrong-size table is refused");
                Assert(store.Zone("Gate", "md")?.Table == "11001111", "and the stored zone is untouched");

                // It survives a restart, and the type key is matched like a camera
                // name is — a hand-edited file must still find its grid.
                var reopened = new Web.CameraStateStore(dir);
                Assert(reopened.Zone("Gate", "md")?.Table == "11001111", "zone survives a restart");
                Assert(reopened.Zone("gate", "MD")?.Table == "11001111", "camera and type match case-insensitively");

                // The suspend flag and the zone live in the same file without
                // trampling each other, and a camera at its defaults stays out of it.
                reopened.SetSuspended("Gate", true);
                var third = new Web.CameraStateStore(dir);
                Assert(third.Suspended("Gate") && third.Zone("Gate", "md") != null,
                    "suspend and zone coexist");
                Assert(third.Zone("Never-seen", "md") == null, "an unknown camera has no zone");

                // A hand-edited file (case-duplicate zone keys, a zone with no table, a null
                // camera) must not reset EVERY camera's state, nor crash a read.
                File.WriteAllText(Path.Combine(dir, "camera-state.json"), """
                    {
                      "Gate": { "Suspended": true, "Zones": { "md": { "Cols": 2, "Rows": 1, "Table": "10" },
                                                              "MD": { "Cols": 2, "Rows": 1, "Table": "01" } } },
                      "Shed": { "Suspended": false, "Zones": { "md": { "Cols": 2, "Rows": 1, "Table": null } } },
                      "Gone": null
                    }
                    """);
                var edited = new Web.CameraStateStore(dir);
                Assert(edited.Suspended("Gate"), "the suspend flags survive a hand-edited zone");
                Assert(edited.Zone("Gate", "md")?.Table == "10", "the first of two same-named zones wins");
                Assert(edited.Zone("Shed", "md") == null, "a zone with no table reads as absent, not as a crash");
                Assert(!edited.Suspended("Gone"), "a null entry is skipped");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("generic cameras' Detection events switch is migrated once, then left alone", () =>
        {
            // A stored events=off on a generic camera predates ONVIF detections and was never
            // chosen, so the first start that can detect turns it on — exactly once.
            var dir = Path.Combine(Path.GetTempPath(), "neolink-selftest-migrate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "settings.json"),
                    """{ "gate": { "events": false, "continuous": true, "eventTypes": null } }""");
                var settings = new Recording.RecordingSettings(dir);
                Assert(!settings.Get("gate").Events, "the stored value loads as it was written");
                Assert(settings.MigrationDue(Recording.RecordingSettings.OnvifEventsMigration), "not yet migrated");
                settings.Seed("gate", eventsDefault: true);
                Assert(!settings.Get("gate").Events, "seeding never overrides a stored value");
                settings.ResetEvents("gate", eventsDefault: true);
                Assert(settings.Get("gate").Events && settings.Get("gate").Continuous,
                    "the migration switches events on and touches nothing else");
                settings.CompleteMigration(Recording.RecordingSettings.OnvifEventsMigration);
                Assert(!settings.MigrationDue(Recording.RecordingSettings.OnvifEventsMigration), "recorded as done");
                // The user's later choice is theirs, on this and every later start.
                settings.Update("gate", events: false, continuous: null, eventTypes: null, setEventTypes: false);
                var restarted = new Recording.RecordingSettings(dir);
                Assert(!restarted.Get("gate").Events, "the switch turned off after the migration stays off");
                Assert(!restarted.MigrationDue(Recording.RecordingSettings.OnvifEventsMigration),
                    "and the migration does not run again");
                // A state_dir moved later takes the marker along with settings.json.
                var moved = new Recording.RecordingSettings(Directory.CreateDirectory(Path.Combine(dir, "state")).FullName, dir);
                Assert(!moved.Get("gate").Events && !moved.MigrationDue(Recording.RecordingSettings.OnvifEventsMigration),
                    "the migration stays done in the new state directory");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
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

            // The app-style minimal write: channel + named sections only, absent
            // sections skipped — the shape for firmwares that reject their own
            // config round-tripped whole.
            var minimal = Streaming.CameraControl.MinimalMdWrite(Obj(
                """{"channel":2,"useNewSens":1,"newSens":{"sensDef":30},"scope":{"cols":4,"rows":2,"table":"11111111"},"extra":9}"""),
                "useNewSens", "newSens", "absent");
            AssertEq((int?)minimal["channel"], 2);
            AssertEq((int?)minimal["useNewSens"], 1);
            Assert(minimal["newSens"] is System.Text.Json.Nodes.JsonObject, "named section cloned");
            Assert(minimal["scope"] == null && minimal["extra"] == null && minimal["absent"] == null,
                "unnamed and absent sections dropped");

            // AI sensitivity: plain field; missing sensitivity = type unsupported.
            var ai = Streaming.CameraControl.ParseAiSensitivity("people", Obj(
                """{"channel":0,"ai_type":"people","sensitivity":85,"stay_time":2}"""));
            Assert(ai is { Type: "people", Sensitivity: 85, StayTime: 2 }, "AI alarm parsed");
            Assert(Streaming.CameraControl.ParseAiSensitivity("vehicle", Obj("""{"channel":0}""")) == null,
                "no sensitivity -> null");

            // Detection zone: the scope grid rides inside the same objects; '1' =
            // watched cell, '0' = ignored. Dimensions are the camera's to declare.
            var zoneCfg = Obj("""
                {"channel":0,"scope":{"cols":4,"rows":2,"table":"11101111"}}
                """);
            var zone = Streaming.CameraControl.ParseZone("md", zoneCfg);
            Assert(zone is { Type: "md", Cols: 4, Rows: 2, Table: "11101111" }, "zone parsed from scope");
            Assert(Streaming.CameraControl.ParseZone("md", Obj("""{"channel":0}""")) == null,
                "no scope -> feature absent");
            Assert(Streaming.CameraControl.ParseZone("md",
                Obj("""{"scope":{"cols":4,"rows":2,"table":"111"}}""")) == null,
                "table shorter than cols*rows -> rejected");
            Assert(Streaming.CameraControl.ParseZone("md",
                Obj("""{"scope":{"cols":4,"rows":2,"table":"1110111x"}}""")) == null,
                "non-binary cell -> rejected");
            Assert(Streaming.CameraControl.ApplyZone(zoneCfg, "00001111"), "zone write accepted");
            AssertEq((string?)zoneCfg["scope"]?["table"], "00001111");
            Assert(!Streaming.CameraControl.ApplyZone(zoneCfg, "0000"), "wrong-size write refused");
            Assert(!Streaming.CameraControl.ApplyZone(zoneCfg, "00002111"), "non-binary write refused");
            AssertEq((string?)zoneCfg["scope"]?["table"], "00001111"); // refused writes change nothing

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

        Test("mqtt: last-event-time is RFC3339 UTC with Z and no fractional seconds", () =>
        {
            // HA's timestamp device class rejects a value without timezone info or
            // with an unexpected shape — a rejected value leaves the sensor at
            // "unknown", which is exactly the bug this format guards against.
            var utc = Mqtt.CameraBridge.FormatEventTime(
                new DateTime(2026, 7, 23, 12, 5, 9, DateTimeKind.Utc));
            AssertEq(utc, "2026-07-23T12:05:09Z");
            // A StartUtc that deserialized as Unspecified must STILL get a Z, not be
            // treated as local — SpecifyKind(Utc) is what forces that.
            var unspecified = Mqtt.CameraBridge.FormatEventTime(
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
            AssertEq(unspecified, "2026-01-02T03:04:05Z");
            Assert(unspecified.EndsWith("Z", StringComparison.Ordinal) && !unspecified.Contains('.'),
                "must end in Z and carry no fractional seconds");
        });

        Test("battery tiles: only the user starts a stream, never the camera waking", () =>
        {
            // Attaching a tile's stream is what makes the server connect, and connecting
            // is what WAKES a battery camera — or holds an already-awake one up. So the
            // wall streams ONLY on a deliberate request. Field logs (a camera facing a
            // blank wall, woken twice in five minutes by false wake-capture edges) showed
            // the earlier "stream whenever it is awake" rule turning every wake, real or
            // false, into viewer demand. A regression here quietly flattens a battery.
            static bool May(bool released, bool watching) =>
                Neolink.WebClient.Components.Pages.Home.BattMayStream(released, watching);

            // Nobody asked: no stream, whatever the camera is doing.
            Assert(!May(released: false, watching: false),
                "a tile does not stream until the user asks");
            // The user asked: stream it (this is the ONLY route to video).
            Assert(May(released: false, watching: true),
                "the user's request is what starts the stream");
            // Released on budget outranks a stale watching flag, or the release would
            // immediately undo itself.
            Assert(!May(released: true, watching: true),
                "a released tile stays released until resumed");
            Assert(!May(released: true, watching: false),
                "released and unwatched is still no stream");
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

                // UID without udp and without address: refused with guidance when
                // the user is saving it; at boot it is skipped so the rest still run.
                File.WriteAllText(tmp, Cfg("\"uid\": \"95270000ABCDEFGH\""));
                bool rejected = false;
                try { NeolinkConfig.Load(tmp, strict: true); }
                catch (FormatException ex) { rejected = ex.Message.Contains("udp"); }
                Assert(rejected, "uid without udp and address is refused, pointing at udp");
                AssertEq(NeolinkConfig.Load(tmp).Cameras.Count, 0);

                // Neither address nor uid: still an error.
                File.WriteAllText(tmp, Cfg("\"channel_id\": 0"));
                rejected = false;
                try { NeolinkConfig.Load(tmp, strict: true); } catch (FormatException) { rejected = true; }
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
                // A value the dropdown never offers must still be refused when saved.
                File.WriteAllText(tmp, Cfg("bogusStream"));
                bool rejected = false;
                try { NeolinkConfig.Load(tmp, strict: true); } catch { rejected = true; }
                Assert(rejected, "an unlisted stream value must be rejected when saved");
                AssertEq(NeolinkConfig.Load(tmp).Cameras.Count, 0);
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

                Assert(!recorder.EmailEventsEnabled && !recorder.WebhookEventsEnabled,
                    "notification opt-ins default to off");
                bridge.HandleCommandAsync("email_events", "ON").GetAwaiter().GetResult();
                bridge.HandleCommandAsync("webhook_events", "ON").GetAwaiter().GetResult();
                Assert(recorder.EmailEventsEnabled && recorder.WebhookEventsEnabled,
                    "HA ON flips both notification opt-ins");
                var stored = new Recording.RecordingSettings(dir).Get("detectcam");
                Assert(stored.EmailEvents && stored.WebhookEvents,
                    "the notification opt-ins persist to settings.json (what the web UI reads)");
                bridge.HandleCommandAsync("email_events", "OFF").GetAwaiter().GetResult();
                Assert(!recorder.EmailEventsEnabled && recorder.WebhookEventsEnabled,
                    "the opt-ins flip independently");
                var hookJson = System.Text.Json.JsonSerializer.Serialize(
                    bridge.NotifySwitchConfig("Email events", "email_events", "mdi:email-fast"),
                    Mqtt.HomeAssistantMqtt.DiscoveryJson);
                Assert(hookJson.Contains("\"availability\":[{\"topic\":\"neolink/bridge/state\"}]"),
                    $"notification switch availability must be bridge-only, got: {hookJson}");
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

        Test("HA motion sensor ignores the tentative wake marker (confirmed events only)", () =>
        {
            var cam = new Web.WebCameraInfo("wakecam",
                new List<Web.WebStreamInfo>(), new StubCameraControl("wakecam"), null);
            var hub = new Mqtt.HomeAssistantMqtt(
                new Config.MqttConfig { Broker = "127.0.0.1", Port = 1 }, // never connected
                new List<Web.WebCameraInfo> { cam }, "test");
            var published = new Dictionary<string, string>();
            hub.PublishObserver = (topic, payload) => { lock (published) published[topic] = payload; };

            hub.OnMotion("wakecam", new Protocol.MotionPush("wake", new[] { "wake" }, External: true));
            Thread.Sleep(300);
            lock (published)
                Assert(!published.ContainsKey("neolink/wakecam/motion"),
                    "the tentative wake marker must not pulse the motion sensor");

            // The hint marker is a confirmed event and must pulse it.
            hub.OnMotion("wakecam", new Protocol.MotionPush("hint", new[] { "wake" }, External: true));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                lock (published) { if (published.ContainsKey("neolink/wakecam/motion")) break; }
                Thread.Sleep(25);
            }
            lock (published)
                Assert(published.TryGetValue("neolink/wakecam/motion", out var on) && on == "ON",
                    "a hint push is a confirmed event — motion must go ON");
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

                // Event-email knobs survive the clone (the write path clones).
                var ev = new Notifications.NotificationSettings
                    { EventSnapshots = 7, EventCooldownMinutes = 0, EventEmailDelaySeconds = 9 };
                Assert(ev.Clone() is { EventSnapshots: 7, EventCooldownMinutes: 0, EventEmailDelaySeconds: 9 },
                    "event-email knobs ride Clone");
                Assert(new Notifications.NotificationSettings().EventEmailDelaySeconds == 5,
                    "event emails default to 5 seconds into the event");
                var wh = new Notifications.NotificationSettings
                {
                    WebhookEnabled = true, WebhookUrl = "http://x/", WebhookBodyMode = "text",
                    WebhookHeaders = new() { "A: b" }, WebhookServerAlerts = false,
                    WebhookInsecureTls = true, WebhookTokenEnc = "enc",
                };
                Assert(wh.Clone() is { WebhookEnabled: true, WebhookUrl: "http://x/", WebhookBodyMode: "text",
                        WebhookServerAlerts: false, WebhookInsecureTls: true, WebhookTokenEnc: "enc" } wc
                    && wc.WebhookHeaders.Count == 1,
                    "webhook knobs ride Clone");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("event email: N snapshots means N, spread across the clip", () =>
        {
            static List<byte[]> Frames(int n) =>
                Enumerable.Range(0, n).Select(i => new[] { (byte)i }).ToList();
            static string Ids(List<byte[]> f) => string.Join(",", f.Select(x => x[0]));

            // The decode over-produces; the pick must return exactly the request,
            // first and last included, evenly spaced. (A 68 s event asking for 3
            // returned 1 before this: the rate was computed from a duration that
            // included pre-roll the event's own length did not know about.)
            AssertEq(Notifications.EventEmailer.PickEvenly(Frames(30), 3).Count, 3);
            // 14 not 15 at the midpoint: Math.Round is banker's rounding (14.5 -> 14).
            AssertEq(Ids(Notifications.EventEmailer.PickEvenly(Frames(30), 3)), "0,14,29");
            AssertEq(Ids(Notifications.EventEmailer.PickEvenly(Frames(9), 5)), "0,2,4,6,8");
            AssertEq(Notifications.EventEmailer.PickEvenly(Frames(120), 50).Count, 50);
            // Fewer frames than asked: attach them all rather than nothing.
            AssertEq(Ids(Notifications.EventEmailer.PickEvenly(Frames(2), 3)), "0,1");
            // One snapshot is the MIDDLE of the event, not its first frame —
            // the first frame is pre-roll, i.e. the scene before anything happened.
            AssertEq(Ids(Notifications.EventEmailer.PickEvenly(Frames(11), 1)), "5");
            AssertEq(Notifications.EventEmailer.PickEvenly(Frames(0), 3).Count, 0);
            AssertEq(Notifications.EventEmailer.PickEvenly(Frames(5), 0).Count, 0);

            // In-memory capture feed selection: leading P-frames (before any
            // keyframe) can never decode and must go; under the cap everything
            // is fed; over it only keyframes (standalone-decodable), thinned.
            static List<(byte[] Au, bool Key)> Aus(int n, int gop) =>
                Enumerable.Range(0, n).Select(i => (new[] { (byte)i }, i % gop == 0)).ToList();
            AssertEq(Notifications.EventEmailer.SelectForDecode(Aus(50, 10), 300).Count, 50);
            var headless = Aus(50, 10).Skip(3).ToList(); // starts mid-GOP; next key is item 10
            AssertEq(Notifications.EventEmailer.SelectForDecode(headless, 300).Count, 40);
            Assert(Notifications.EventEmailer.SelectForDecode(headless, 300)[0].Key,
                "feed starts on a keyframe");
            var thinned = Notifications.EventEmailer.SelectForDecode(Aus(4000, 10), 300);
            AssertEq(thinned.Count, 300);
            Assert(thinned.All(a => a.Key), "over the cap only keyframes are fed");
            AssertEq(Notifications.EventEmailer.SelectForDecode(
                Aus(50, 10).Select(a => (a.Au, false)).ToList(), 300).Count, 0);
        });

        Test("event email: the decode plan reaches the end of every clip", () =>
        {
            foreach (var (want, sec) in new[]
                { (3, 15.0), (3, 68.0), (3, 600.0), (3, 1800.0), (10, 45.0), (50, 600.0), (1, 30.0) })
            {
                double fps = Notifications.EventEmailer.InitialRate(want, sec);
                Assert(Notifications.EventEmailer.MaxDecodedFrames / fps >= sec * 2,
                    $"cap outlasts a 2x clip (want {want}, {sec}s event)");
                Assert(fps * sec >= want, $"event-length clip fills the request (want {want}, {sec}s)");
            }

            double f = Notifications.EventEmailer.InitialRate(3, 1800);
            int passes = 0;
            while (f < 10 && passes < 10) { f = Notifications.EventEmailer.RetryRate(3, f, 1); passes++; }
            Assert(f >= 10 && passes <= 8, "retry escalation reaches the rate ceiling within 8 passes");
            Assert(Notifications.EventEmailer.RetryRate(3, 0.5, 8) <= 10, "retry rate stays capped");

            // Invariant decimals: a comma-locale server must not emit "trim=start=7,5".
            AssertEq(Notifications.EventEmailer.VideoFilter(7.5, 0.125),
                "trim=start=7.5,setpts=PTS-STARTPTS,fps=0.125,scale=-2:720");
            AssertEq(Notifications.EventEmailer.VideoFilter(0, 0.5), "fps=0.5,scale=-2:720");
        });

        Test("email failures report, never throw (unreachable/misconfigured SMTP)", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var key = new byte[32];
                Array.Fill(key, (byte)7);
                var store = new Notifications.NotificationStore(dir, new Notifications.SecretProtector(key));
                var notifier = new Notifications.Notifier(store, "selftest");

                // A port nothing listens on: connection refused, immediately.
                var broken = new Notifications.NotificationSettings
                {
                    Enabled = true, Recipient = "to@x.com", From = "from@x.com",
                    SmtpHost = "127.0.0.1", SmtpPort = 9, Security = Notifications.SmtpSecurity.None,
                };
                var err = notifier.SendTestAsync(broken, "pw", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(err != null, "an unreachable mail server REPORTS instead of throwing");

                // A malformed address is refused by the sender's own validation
                // (header injection guard) — also as a message, not an exception.
                var inject = new Notifications.NotificationSettings
                {
                    Enabled = true, Recipient = "a@b.com\r\nBcc: c@d.com",
                    From = "from@x.com", SmtpHost = "127.0.0.1", SmtpPort = 9,
                    Security = Notifications.SmtpSecurity.None,
                };
                Assert(notifier.SendTestAsync(inject, "pw", CancellationToken.None).GetAwaiter().GetResult() != null,
                    "a control character in an address is refused as a message");

                var hookErr = notifier.SendTestWebhookAsync(new Notifications.NotificationSettings
                {
                    WebhookEnabled = true, WebhookUrl = "http://127.0.0.1:9/hook",
                }, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(hookErr != null, "an unreachable webhook REPORTS instead of throwing");

                store.Save(new Notifications.NotificationSettings
                    { WebhookEnabled = true, WebhookUrl = "http://x/" }, null, "Bearer tk_secret");
                AssertEq(store.WebhookToken(), "tk_secret");
                Assert(store.HasWebhookToken, "webhook token stored encrypted, scheme prefix stripped");

                // The queue path swallows the same failures: Send() must return
                // immediately and the delivery loop must survive them, so the
                // recorder that raised the event never learns mail is broken.
                store.Save(broken, "pw");
                using var cts = new CancellationTokenSource();
                var loop = notifier.RunAsync(cts.Token);
                for (int i = 0; i < 5; i++)
                    notifier.Send(new Notifications.Alert($"e{i}", false, "s", "h", "b"));
                Assert(!loop.IsFaulted, "the delivery loop does not fault on a bad server");
                cts.Cancel();
                try { loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                Assert(!loop.IsFaulted, "the delivery loop ends cleanly after failed sends");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("notifier: channel gating refuses at the door, a full queue reports", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var key = new byte[32];
                Array.Fill(key, (byte)11);
                var store = new Notifications.NotificationStore(dir, new Notifications.SecretProtector(key));
                var notifier = new Notifications.Notifier(store, "selftest");

                Assert(!notifier.Send(new Notifications.Alert("a", false, "s", "h", "b")),
                    "nothing configured: Send refuses");

                store.Save(new Notifications.NotificationSettings
                    { WebhookEnabled = true, WebhookUrl = "http://127.0.0.1:1/hook" }, null);
                Assert(!notifier.Send(new Notifications.Alert("a", false, "s", "h", "b",
                        Channels: Notifications.AlertChannels.Email)),
                    "an email-only alert with only the webhook ready is refused");

                // No reader is running, so the 100-slot queue must fill and then
                // report full — TryWrite returning true for a DROPPED alert is
                // the regression this pins (DropWrite mode ate the queue-full
                // signal AND the cooldown rollback that rides on it).
                int accepted = 0;
                while (accepted < 500
                       && notifier.Send(new Notifications.Alert($"q{accepted}", false, "s", "h", "b")))
                    accepted++;
                AssertEq(accepted, 100);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        Test("notification settings: Clone copies every property (reflection sweep)", () =>
        {
            // A property added to NotificationSettings but forgotten in Clone
            // silently vanishes from every Snapshot(); this sweep has no list
            // to forget to extend.
            var seed = new Notifications.NotificationSettings();
            var defaults = new Notifications.NotificationSettings();
            var props = typeof(Notifications.NotificationSettings)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanWrite).ToList();
            Assert(props.Count >= 25, $"sweep sees the settings surface ({props.Count} properties)");
            foreach (var p in props)
            {
                object? v = p.GetValue(defaults) switch
                {
                    string sv => sv + "zz",
                    bool bv => !bv,
                    int iv => iv + 13,
                    Notifications.SmtpSecurity ev =>
                        ev == Notifications.SmtpSecurity.None
                            ? Notifications.SmtpSecurity.Ssl : Notifications.SmtpSecurity.None,
                    List<string> => new List<string> { "zz" },
                    Dictionary<string, int> => new Dictionary<string, int> { ["zz"] = 1 },
                    var other => throw new InvalidOperationException(
                        $"extend this sweep for {p.Name} ({other?.GetType().Name ?? "null"})"),
                };
                p.SetValue(seed, v);
            }
            var clone = seed.Clone();
            foreach (var p in props)
            {
                var a = p.GetValue(seed);
                var b = p.GetValue(clone);
                bool same = a switch
                {
                    List<string> la => b is List<string> lb && la.SequenceEqual(lb) && !ReferenceEquals(la, lb),
                    Dictionary<string, int> da => b is Dictionary<string, int> db
                        && da.Count == db.Count && da.All(kv => db.TryGetValue(kv.Key, out var x) && x == kv.Value)
                        && !ReferenceEquals(da, db),
                    _ => Equals(a, b),
                };
                Assert(same, $"Clone must carry {p.Name} (deep for collections)");
            }
        });

        Test("smtp message: attachments nest the text/html pair inside multipart/mixed", () =>
        {
            var s = new Notifications.NotificationSettings
            {
                Recipient = "to@x.com", From = "from@x.com", FromName = "Neolink",
            };
            // No attachments: the classic alternative pair, no mixed wrapper.
            var plain = Notifications.SmtpSender.BuildMessage(s, "subject", "<b>h</b>", "t");
            Assert(plain.Contains("Content-Type: multipart/alternative"), "plain mail is alternative");
            Assert(!plain.Contains("multipart/mixed"), "no mixed wrapper without attachments");

            // With attachments: mixed at the top, the alternative pair nested,
            // and every image present as base64 with name + disposition.
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
            var mail = Notifications.SmtpSender.BuildMessage(s, "Driveway: person", "<b>h</b>", "t",
                new[]
                {
                    new Notifications.EmailAttachment("Driveway-1.jpg", "image/jpeg", jpeg),
                    new Notifications.EmailAttachment("Driveway-2.jpg", "image/jpeg", jpeg),
                });
            Assert(mail.Contains("Content-Type: multipart/mixed"), "attachments force multipart/mixed");
            Assert(mail.Contains("Content-Type: multipart/alternative"), "text/html pair still nested");
            AssertEq(System.Text.RegularExpressions.Regex.Matches(mail,
                "Content-Disposition: attachment").Count, 2);
            Assert(mail.Contains("filename=\"Driveway-2.jpg\""), "attachment names carried");
            Assert(mail.Contains(Convert.ToBase64String(jpeg)), "attachment bytes as base64");
            // The mixed boundary must close AFTER the last attachment.
            var boundary = System.Text.RegularExpressions.Regex.Match(mail,
                "multipart/mixed; boundary=\"([^\"]+)\"").Groups[1].Value;
            Assert(mail.TrimEnd().EndsWith($"--{boundary}--"), "mixed part closed last");

            // Names land in MIME headers unencoded; quotes and control characters must not.
            AssertEq(Notifications.SmtpSender.HeaderSafeName("Drive\"way\r\nBcc: x.jpg"), "DrivewayBcc: x.jpg");
            AssertEq(Notifications.SmtpSender.HeaderSafeName("\r\n"), "attachment");
        });

        Test("webhook: placeholders, headers and body modes build the right request", () =>
        {
            var ev = new Notifications.EventInfo("Driveway", new[] { "person" },
                DateTime.UtcNow.AddSeconds(-30), 30, Ongoing: true);
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 0xFF, 0xD9 };
            var alert = new Notifications.Alert("event:1", false, "subj", "Person — Driveway", "body text",
                Attachments: new[] { new Notifications.EmailAttachment("Driveway-1.jpg", "image/jpeg", jpeg) },
                Channels: Notifications.AlertChannels.Webhook, Event: ev);

            static string Body(HttpRequestMessage r) =>
                r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            var s = new Notifications.NotificationSettings
            {
                WebhookUrl = "http://127.0.0.1:1/hook",
                WebhookMethod = "POST",
                WebhookBodyMode = "text",
                WebhookBodyTemplate = "{\"text\":\"{title}: {message} [{camera}|{labels}|{status}|{duration}]\"}",
                WebhookHeaders = new() { "Content-Type: application/json", "X-Title: {title}", "no-colon-line" },
            };
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", alert, "selftest"))
            {
                // JSON braces survive rendering; only the documented placeholders
                // substitute — and with a JSON Content-Type the VALUES are
                // JSON-escaped (the em-dash becomes —, quotes cannot break out).
                AssertEq(Body(r), "{\"text\":\"Person \\u2014 Driveway: body text [Driveway|person|ongoing|30]\"}");
                AssertEq(r.Content!.Headers.ContentType!.MediaType, "application/json");
                Assert(r.Headers.TryGetValues("X-Title", out var xt) && xt.First() == "Person - Driveway",
                    "header values render placeholders and fold to header-safe ASCII");
                Assert(!r.Headers.Contains("Authorization"), "no token, no Authorization header");
            }

            // A camera named with a quote must not 400 every delivery: escaped
            // into JSON-shaped bodies, untouched in plain text.
            var spicy = alert with
            {
                Headline = "He said \"hi\"",
                Event = ev with { Camera = "Drive\"way" },
            };
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", spicy, "selftest"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(Body(r));
                Assert(doc.RootElement.GetProperty("text").GetString()!.Contains("He said \"hi\""),
                    "escaped values decode back to the original text");
            }
            var savedHeaders = s.WebhookHeaders;
            s.WebhookHeaders = new();
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", spicy, "selftest"))
                Assert(Body(r).Contains("He said \"hi\""),
                    "plain-text mode leaves quotes alone");
            s.WebhookHeaders = savedHeaders;

            // The token field becomes a Bearer header (pasted scheme prefix
            // forgiven); an explicit Authorization line wins over it.
            using (var r = Notifications.WebhookSender.BuildRequest(s, "Bearer tk_abc", alert, "selftest"))
                Assert(r.Headers.TryGetValues("Authorization", out var a) && a.Single() == "Bearer tk_abc",
                    "token becomes a single Bearer header");
            s.WebhookHeaders.Add("Authorization: Basic dXNlcg==");
            using (var r = Notifications.WebhookSender.BuildRequest(s, "tk_abc", alert, "selftest"))
                Assert(r.Headers.TryGetValues("Authorization", out var a) && a.Single() == "Basic dXNlcg==",
                    "an explicit Authorization line wins over the token");
            s.WebhookHeaders.RemoveAt(s.WebhookHeaders.Count - 1);

            s.WebhookBodyMode = "json";
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", alert, "selftest"))
            {
                var body = Body(r);
                Assert(body.Contains("\"type\":\"event\"") && body.Contains("\"camera\":\"Driveway\"")
                    && body.Contains("\"ongoing\":true"), "json mode carries the event schema");
                Assert(body.Contains(Convert.ToBase64String(jpeg)), "json mode carries snapshots as base64");
            }

            s.WebhookBodyMode = "snapshot";
            s.WebhookHeaders = new() { "X-Title: {title}", "X-Message: {message}", "X-Filename: snapshot.jpg" };
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", alert, "selftest"))
            {
                AssertEq(r.Content!.Headers.ContentType!.MediaType, "image/jpeg");
                AssertEq(r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length, jpeg.Length);
                Assert(r.Headers.Contains("X-Message"), "snapshot mode carries the text in headers");
            }
            // No image to attach: the body becomes the text, so the ntfy headers
            // that would re-route it must not survive.
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", alert with { Attachments = null }, "selftest"))
            {
                AssertEq(r.Content!.Headers.ContentType!.MediaType, "text/plain");
                Assert(!r.Headers.Contains("X-Message") && !r.Headers.Contains("X-Filename"),
                    "snapshot fallback drops the attachment headers");
                Assert(r.Headers.Contains("X-Title"), "snapshot fallback keeps the rest");
            }

            s.WebhookBodyMode = "multipart";
            s.WebhookBodyTemplate = "";
            s.WebhookMethod = "PUT";
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", alert, "selftest"))
            {
                AssertEq(r.Method.Method, "PUT");
                var body = Body(r);
                Assert(body.Contains("name=payload_json") || body.Contains("name=\"payload_json\""),
                    "multipart carries the payload_json part");
                Assert(body.Contains("filename=Driveway-1.jpg") || body.Contains("filename=\"Driveway-1.jpg\""),
                    "multipart carries the image file");
            }
            // payload_json is JSON by definition: quotes in the title arrive escaped.
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", spicy, "selftest"))
                Assert(Body(r).Contains("He said \\u0022hi\\u0022"),
                    "multipart payload_json escapes substituted quotes");

            AssertEq(Notifications.WebhookSender.ParseHeaderLines(
                new[] { "A: b", "bad", " : x", "C:d" }).Count, 2);
            // A saved typo in a header NAME is dropped at parse time — it must
            // never reach HttpHeaders, where it threw on every delivery.
            AssertEq(Notifications.WebhookSender.ParseHeaderLines(
                new[] { "X Title: x", "Ünicode: x", "Ok: y" }).Single().Name, "Ok");
            s.WebhookBodyMode = "text";
            s.WebhookHeaders = new() { "X Title: boom", "X-Fine: ok" };
            using (var r = Notifications.WebhookSender.BuildRequest(s, "", alert, "selftest"))
                Assert(r.Headers.Contains("X-Fine") && !r.Headers.Any(h => h.Key == "X Title"),
                    "a malformed header line does not abort the delivery");

            // The CR/LF fold is the header-injection defense, not cosmetics.
            Assert(!Notifications.WebhookSender.HeaderValue("a\r\nX-Evil: b").Any(c => c is '\r' or '\n'),
                "header values cannot smuggle CR/LF");

            // Push-length text: {message} prefers the Brief, {detail} keeps the
            // story, {link} deep-links the event once PublicUrl is set.
            var evAlert = alert with
            {
                Brief = "Person on Driveway at 14:32",
                Event = ev with { Id = "cam/2026-01-01_1" },
            };
            var ls = new Notifications.NotificationSettings
            {
                WebhookUrl = "http://127.0.0.1:1/hook", WebhookBodyMode = "text",
                WebhookBodyTemplate = "{message}|{detail}|{link}",
                PublicUrl = "https://cams.example.com/",
            };
            using (var r = Notifications.WebhookSender.BuildRequest(ls, "", evAlert, "selftest"))
                AssertEq(Body(r), "Person on Driveway at 14:32|body text|" +
                    "https://cams.example.com/events?event=cam%2F2026-01-01_1");
            ls.WebhookBodyMode = "json";
            using (var r = Notifications.WebhookSender.BuildRequest(ls, "", evAlert, "selftest"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(Body(r));
                AssertEq(doc.RootElement.GetProperty("message").GetString()!, "Person on Driveway at 14:32");
                AssertEq(doc.RootElement.GetProperty("detail").GetString()!, "body text");
                Assert(doc.RootElement.GetProperty("link").GetString()!.EndsWith("event=cam%2F2026-01-01_1"),
                    "json mode carries the event link");
            }
            // Snapshot mode is the ntfy shape: the link rides the native Click
            // header — automatically, and an explicit line wins over it.
            ls.WebhookBodyMode = "snapshot";
            using (var r = Notifications.WebhookSender.BuildRequest(ls, "", evAlert, "selftest"))
                Assert(r.Headers.TryGetValues("X-Click", out var c)
                       && c.Single().EndsWith("event=cam%2F2026-01-01_1"),
                    "snapshot mode auto-adds the ntfy Click header");
            ls.WebhookHeaders = new() { "X-Click: https://other.example.com/" };
            using (var r = Notifications.WebhookSender.BuildRequest(ls, "", evAlert, "selftest"))
                Assert(r.Headers.TryGetValues("X-Click", out var c)
                       && c.Single() == "https://other.example.com/",
                    "an explicit Click line wins over the auto link");
            // No PublicUrl: no link anywhere — no Click header, {link} renders "",
            // the json field is omitted entirely.
            ls.PublicUrl = "";
            ls.WebhookHeaders = new();
            using (var r = Notifications.WebhookSender.BuildRequest(ls, "", evAlert, "selftest"))
                Assert(!r.Headers.Contains("X-Click"), "no PublicUrl, no Click header");
            ls.WebhookBodyMode = "json";
            using (var r = Notifications.WebhookSender.BuildRequest(ls, "", evAlert, "selftest"))
                Assert(!Body(r).Contains("\"link\""), "no PublicUrl, no link field");

            // Delivery budget scales with what the body carries.
            Assert(Notifications.WebhookSender.TimeoutFor(alert with { Attachments = null })
                   == TimeSpan.FromSeconds(15), "webhook base budget without attachments");
            var heavy = alert with
            {
                Attachments = new[] { new Notifications.EmailAttachment("a.jpg", "image/jpeg", new byte[8_000_000]) },
            };
            Assert(Notifications.WebhookSender.TimeoutFor(heavy) > TimeSpan.FromMinutes(2),
                "8 MB of snapshots buys minutes, not seconds");
            Assert(Notifications.WebhookSender.TimeoutFor(heavy) <= TimeSpan.FromMinutes(10)
                   && Notifications.SmtpSender.TimeoutFor(heavy.Attachments) <= TimeSpan.FromMinutes(10),
                "budgets stay capped");
            Assert(Notifications.SmtpSender.TimeoutFor(null) == TimeSpan.FromSeconds(25)
                   && Notifications.SmtpSender.TimeoutFor(heavy.Attachments) > TimeSpan.FromMinutes(2),
                "smtp budget scales the same way");

            // {time} stays Gregorian whatever calendar the server locale uses.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("th-TH");
                var t = Notifications.WebhookSender.Render("{time}", alert, "selftest");
                Assert(t.StartsWith(ev.StartUtc.ToLocalTime().Year.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                    $"{{time}} renders the Gregorian year under a Buddhist-calendar locale, got: {t}");
            }
            catch (System.Globalization.CultureNotFoundException) { /* trimmed ICU: nothing to pin */ }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
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
                // The init segment must reach the disk at creation, not a megabyte
                // later: mid-write readers (the event-email sampler, a viewer on a
                // still-recording event) need a parseable file from the start.
                var initDeadline = DateTime.UtcNow.AddSeconds(5);
                long earlyLen = 0;
                while (earlyLen == 0 && DateTime.UtcNow < initDeadline)
                {
                    try { earlyLen = new FileInfo(path).Length; } catch { }
                    if (earlyLen == 0) Thread.Sleep(20);
                }
                Assert(earlyLen > 0, "init segment is flushed to disk before the buffer fills");
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
                Assert(writer.WroteVideo, "writer reports video landed");

                // An empty capture (no frame ever arrived) must say so, so the
                // recorder can strip HasClip/HasPreview instead of sending
                // players to an unplayable stub.
                var empty = Recording.ClipWriter.TryCreate(Path.Combine(dir, "empty.mp4"), hub)!;
                empty.Dispose();
                Assert(empty.Completion.Wait(TimeSpan.FromSeconds(10)), "empty writer completes");
                Assert(!empty.WroteVideo, "empty capture reports no video");

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

        Test("fragment writer path matches an independent reference byte for byte", () =>
        {
            var rng = new Random(20260901);
            // The pre-refactor fragment layout, kept here verbatim as the oracle.
            static byte[] Legacy(uint sequence, ulong decodeTime, uint duration, byte[] sample, bool keyframe, uint trackId = 1)
            {
                var w = new Mp4Writer(108 + sample.Length);
                int trunDataOffsetPos;
                using (w.Box("moof"))
                {
                    using (w.FullBox("mfhd", 0, 0)) w.U32(sequence);
                    using (w.Box("traf"))
                    {
                        using (w.FullBox("tfhd", 0, 0x020000)) w.U32(trackId);
                        using (w.FullBox("tfdt", 1, 0)) w.U64(decodeTime);
                        using (w.FullBox("trun", 0, 0x000305))
                        {
                            w.U32(1);
                            trunDataOffsetPos = w.Position;
                            w.U32(0);
                            w.U32(keyframe ? 0x02000000u : 0x01010000u);
                            w.U32(duration);
                            w.U32((uint)sample.Length);
                        }
                    }
                }
                int moofSize = w.Position;
                w.PatchU32(trunDataOffsetPos, (uint)(moofSize + 8));
                using (w.Box("mdat")) w.Bytes(sample);
                return w.Detach();
            }
            static byte[] Nal(Random r, byte[] header, int len, int flagByte, bool? firstSlice)
            {
                var b = new byte[Math.Max(len, header.Length + 1)];
                r.NextBytes(b);
                for (int i = 0; i < b.Length; i++) if (b[i] == 0) b[i] = 1; // no start-code emulation
                header.CopyTo(b, 0);
                if (firstSlice is { } f)
                    b[flagByte] = f ? (byte)(b[flagByte] | 0x80) : (byte)(b[flagByte] & 0x7F);
                return b;
            }
            byte[] Sc(Random r) => r.Next(2) == 0 ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 };
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
            {
                for (int round = 0; round < 40; round++)
                {
                    // Several pictures per buffer: parameter sets and AUDs (stripped),
                    // key and non-key slices, first-slice flags on and off, some
                    // samples past the 85 KB large-object threshold. The expected
                    // length-prefixed sample per picture is built by the generator
                    // itself, independently of the code under test.
                    using var ms = new MemoryStream();
                    var expected = new List<(byte[] Sample, bool Key)>();
                    int pictures = 1 + rng.Next(4);
                    for (int p = 0; p < pictures; p++)
                    {
                        bool key = rng.Next(3) == 0;
                        int big = rng.Next(3) == 0 ? 100_000 + rng.Next(50_000) : 200 + rng.Next(5000);
                        using var sample = new MemoryStream();
                        void Emit(byte[] nal, bool kept)
                        {
                            ms.Write(Sc(rng)); ms.Write(nal);
                            if (!kept) return;
                            var len = new byte[4];
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)nal.Length);
                            sample.Write(len); sample.Write(nal);
                        }
                        if (codec == VideoCodec.H264)
                        {
                            if (rng.Next(2) == 0) Emit(Nal(rng, new byte[] { 0x67 }, 8, 1, null), kept: false);
                            if (rng.Next(2) == 0) Emit(Nal(rng, new byte[] { 0x68 }, 4, 1, null), kept: false);
                            if (rng.Next(3) == 0) Emit(Nal(rng, new byte[] { 0x09 }, 2, 1, null), kept: false);
                            byte slice = key ? (byte)0x65 : (byte)0x41;
                            Emit(Nal(rng, new[] { slice }, big, 1, true), kept: true);
                            if (rng.Next(2) == 0) Emit(Nal(rng, new[] { slice }, 300 + rng.Next(2000), 1, false), kept: true);
                        }
                        else
                        {
                            static byte[] H(int type) => new[] { (byte)(type << 1), (byte)0x01 };
                            if (rng.Next(2) == 0) Emit(Nal(rng, H(32), 6, 2, null), kept: false);
                            if (rng.Next(2) == 0) Emit(Nal(rng, H(33), 12, 2, null), kept: false);
                            if (rng.Next(2) == 0) Emit(Nal(rng, H(34), 5, 2, null), kept: false);
                            var slice = H(key ? 19 : 1);
                            Emit(Nal(rng, slice, big, 2, true), kept: true);
                            if (rng.Next(2) == 0) Emit(Nal(rng, slice, 300 + rng.Next(2000), 2, false), kept: true);
                        }
                        expected.Add((sample.ToArray(), key));
                    }
                    var annexB = ms.ToArray();
                    var copying = FMp4.SplitAccessUnits(codec, annexB);
                    var raw = FMp4.SplitAccessUnitsRaw(codec, annexB);
                    AssertEq(raw.Count, expected.Count);
                    AssertEq(copying.Count, expected.Count);
                    // Every unit of the buffer streams into ONE writer, so all but the
                    // first start at a non-zero offset — data_offset must stay relative.
                    var batch = new Mp4Writer(1024);
                    using var wanted = new MemoryStream();
                    for (int i = 0; i < raw.Count; i++)
                    {
                        var unit = raw.Units[i];
                        var (sample, key) = expected[i];
                        AssertEq(unit.Keyframe, key);
                        AssertEq(unit.SampleBytes, sample.Length);
                        Assert(copying[i].Sample.AsSpan().SequenceEqual(sample), "copying split == generator's sample");
                        var legacy = Legacy((uint)(i + 1), (ulong)i * 3000, 3000, sample, key);
                        AssertEq(legacy.Length, FMp4.FragmentSize(sample.Length));
                        Assert(legacy.AsSpan().SequenceEqual(FMp4.BuildFragment((uint)(i + 1), (ulong)i * 3000, 3000, sample, key)),
                            "BuildFragment(sample) == legacy layout");
                        Assert(legacy.AsSpan().SequenceEqual(FMp4.BuildFragment((uint)(i + 1), (ulong)i * 3000, 3000, raw, unit)),
                            "BuildFragment(units, unit) == legacy layout");
                        FMp4.WriteFragmentHeader(batch, (uint)(i + 1), (ulong)i * 3000, 3000, unit.SampleBytes, unit.Keyframe);
                        FMp4.WriteSample(batch, raw, unit);
                        wanted.Write(legacy);
                    }
                    Assert(batch.Written.AsSpan().SequenceEqual(wanted.ToArray()), "streamed batch == concatenated legacy fragments");
                }
            }
            // Audio rides the same header writer on track 2 (AAC access units, a
            // few hundred bytes, always flagged as sync samples).
            for (int i = 0; i < 20; i++)
            {
                var au = new byte[40 + rng.Next(600)];
                rng.NextBytes(au);
                var legacy = Legacy((uint)(1000 + i), (ulong)i * 1024, 1024, au, true, FMp4.AudioTrackId);
                Assert(legacy.AsSpan().SequenceEqual(FMp4.BuildFragment((uint)(1000 + i), (ulong)i * 1024, 1024, au,
                    keyframe: true, trackId: FMp4.AudioTrackId)), "audio fragment == legacy layout");
            }
        });

        Test("event store listing matches a full sort: limits, days, ties, deletes, reload", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Recording.EventStore(dir);
                var rng = new Random(7);
                var all = new List<Recording.EventRecord>(); // insertion order = the reference's tie order
                var baseUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
                string[] cams = { "Front", "Back", "Garage" };
                for (int i = 0; i < 300; i++)
                {
                    // Out-of-order arrival, exact ties, wake-only records, mixed cameras.
                    var start = baseUtc.AddMinutes(rng.Next(0, 5 * 24 * 60)).AddSeconds(rng.Next(0, 2) * 30);
                    var labels = rng.Next(10) == 0 ? new[] { "wake" } : new[] { rng.Next(2) == 0 ? "person" : "motion" };
                    var rec = store.Create(cams[rng.Next(cams.Length)], start, labels);
                    rec.Ongoing = false;
                    if (rng.Next(3) == 0) rec.Reviewed = true;
                    store.Save(rec);
                    all.Add(rec);
                }
                foreach (var victim in all.Where((_, i) => i % 7 == 0).ToList())
                {
                    Assert(store.DeleteEvent(victim.Id), "delete an indexed event");
                    all.Remove(victim);
                }
                // Exact ties with a delete in between: equal StartUtc lists in insertion order.
                var tie = baseUtc.AddDays(10);
                Recording.EventRecord Tied(string cam, DateTime at)
                {
                    var r = store.Create(cam, at, new[] { "person" });
                    r.Ongoing = false;
                    store.Save(r);
                    return r;
                }
                var t1 = Tied("Front", tie);
                var gone = Tied("Front", tie.AddMinutes(-5));
                Assert(store.DeleteEvent(gone.Id), "delete between tied inserts");
                var t2 = Tied("Back", tie);
                var t3 = Tied("Front", tie);
                all.Add(t1); all.Add(t2); all.Add(t3);
                Assert(store.List(limit: 3).Select(r => r.Id).SequenceEqual(new[] { t1.Id, t2.Id, t3.Id }),
                    "tied events list in insertion order");
                IEnumerable<string> Reference(string? cam, bool? reviewed, int limit, DateTime? day, bool noWake) =>
                    all.Where(r => cam == null || r.Camera.Equals(cam, StringComparison.OrdinalIgnoreCase))
                       .Where(r => reviewed == null || r.Reviewed == reviewed)
                       .Where(r => day == null || r.StartUtc.ToLocalTime().Date == day.Value.Date)
                       .Where(r => !noWake || r.Labels is not ["wake"])
                       .OrderByDescending(r => r.StartUtc).Take(limit).Select(r => r.Id);
                void Check(string? cam, bool? reviewed, int limit, DateTime? day, bool noWake)
                {
                    var got = store.List(cam, reviewed, limit, day, noWake).Select(r => r.Id);
                    Assert(got.SequenceEqual(Reference(cam, reviewed, limit, day, noWake)),
                        $"list(cam={cam}, reviewed={reviewed}, limit={limit}, day={day:yyyy-MM-dd}, noWake={noWake})");
                }
                Check(null, null, 200, null, false);
                Check(null, null, 10, null, true);
                Check("Back", null, 100_000, null, true);
                Check(null, true, 50, null, false);
                Check(null, false, 5, null, true);
                Check(null, null, 1, null, false);
                var someDay = all[rng.Next(all.Count)].StartUtc.ToLocalTime().Date;
                Check(null, null, 10_000, someDay, true);
                Check("Front", false, 3, someDay, true);
                Check(null, null, 10, someDay.AddDays(-30), false);

                var reloaded = new Recording.EventStore(dir);
                reloaded.Load();
                var fromDisk = reloaded.List(limit: 100_000);
                AssertEq(fromDisk.Count, all.Count);
                Assert(fromDisk.Zip(fromDisk.Skip(1)).All(p => p.First.StartUtc >= p.Second.StartUtc),
                    "reload lists newest first");
                Assert(fromDisk.Select(r => r.Id).ToHashSet().SetEquals(all.Select(a => a.Id)),
                    "reload indexes every surviving event");

                // Retention prunes whole days; they must leave the sorted index too.
                var keepA = Tied("Garage", DateTime.UtcNow.AddMinutes(-2));
                var keepB = Tied("Garage", DateTime.UtcNow.AddMinutes(-1));
                store.Cleanup(retentionDays: 1, continuousRetentionDays: 1);
                Assert(store.List(limit: 100_000).Select(r => r.Id).SequenceEqual(new[] { keepB.Id, keepA.Id }),
                    "retention prune leaves only today's events in the listing");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
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

        Test("virtual classic index clamps a torn tail fragment", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // A growing clip between write-buffer flushes (and a crash-cut file
                // forever) ends with a complete moof but a truncated mdat. Indexing
                // that sample pointed past the fragment region — every browser then
                // read the synthesized moov's own bytes ("trak") as a 1.9 GB NAL
                // length and killed the video track mid-clip.
                var sps = new byte[] { 0x67, 0x42, 0xE0, 0x1F, 0xA0 };
                var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
                var init = FMp4.BuildInit(VideoCodec.H264, sps, pps, null, 640, 360);
                var ms = new MemoryStream();
                ms.Write(init);
                ulong dt = 0;
                for (int i = 0; i < 10; i++)
                {
                    bool key = i % 5 == 0;
                    var sample = new byte[24];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sample, 20);
                    sample[4] = key ? (byte)0x65 : (byte)0x41;
                    ms.Write(FMp4.BuildFragment((uint)(i + 1), dt, 3000, sample, key));
                    dt += 3000;
                }
                var whole = ms.ToArray();
                var path = Path.Combine(dir, "torn.mp4");
                File.WriteAllBytes(path, whole.AsSpan(0, whole.Length - 10).ToArray());

                byte[] served;
                using (var v = Recording.VirtualMp4.Open(path))
                {
                    served = new byte[v.Length];
                    v.ReadExactly(served);
                }
                int tailBase = served.Length - 1024;
                var tail = Encoding.ASCII.GetString(served, tailBase, 1024);
                uint After(string tag, int off) => System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32BigEndian(served.AsSpan(tailBase + tail.IndexOf(tag, StringComparison.Ordinal) + off));
                AssertEq(After("stsz", 12), 9u); // the torn tenth is not indexed
                AssertEq(After("stco", 8), 9u);

                // Every indexed payload must lie inside the mapped file region —
                // never in the synthesized moov that follows it.
                int moovStart = served.Length;
                for (int p = 0; p + 8 <= served.Length;)
                {
                    uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(served.AsSpan(p));
                    if (Encoding.ASCII.GetString(served, p + 4, 4) == "moov") moovStart = p;
                    if (size < 8) break;
                    p += (int)size;
                }
                int stszEntries = tailBase + tail.IndexOf("stsz", StringComparison.Ordinal) + 16;
                int stcoEntries = tailBase + tail.IndexOf("stco", StringComparison.Ordinal) + 12;
                for (int i = 0; i < 9; i++)
                {
                    uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(served.AsSpan(stszEntries + i * 4));
                    uint off = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(served.AsSpan(stcoEntries + i * 4));
                    Assert(off + size <= moovStart, $"sample {i + 1} payload ends inside the file region");
                }

                // The in-place upgrade honours the same boundary.
                Assert(Recording.ClipWriter.RefinalizeClassic(path), "torn file refinalizes");
                var upgraded = File.ReadAllBytes(path);
                int upBase = upgraded.Length - 1024;
                var upTail = Encoding.ASCII.GetString(upgraded, upBase, 1024);
                AssertEq(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    upgraded.AsSpan(upBase + upTail.IndexOf("stsz", StringComparison.Ordinal) + 12)), 9u);
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

                // A growing file's tail slot mid-reseal reads torn. The reader
                // must pin its length to the sealed prefix at open instead of
                // promising bytes it cannot serve — a short body against the
                // Content-Length aborted every mid-write playback of an
                // encrypted clip (and the email sampler with it).
                var tornPath = Path.Combine(dir, "torn-tail.bin");
                File.Copy(path, tornPath);
                using (var f = File.Open(tornPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    f.Seek(-10, SeekOrigin.End);
                    f.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
                }
                using (var r = Recording.FootageVault.OpenRead(tornPath))
                {
                    AssertEq(r.Length, 3L * 65536); // pinned to the sealed full slots
                    var head = new byte[r.Length];
                    r.ReadExactly(head);
                    Assert(head.AsSpan().SequenceEqual(data.AsSpan(0, head.Length)),
                        "sealed prefix serves clean under a torn tail");
                    AssertEq(r.Read(new byte[16], 0, 16), 0); // and ends exactly there
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

            // A suffixed TAG never parses: prerelease tags reached via the
            // tags fallback are not advertised as updates.
            Assert(!chk.IsNewer("v0.7.0-rc1"), "prerelease tag is not offered as an update");
        });

        Test("update checker: newest-tag selection", () =>
        {
            AssertEq(Web.UpdateChecker.PickNewestTag(["v1.9.0", "v1.10.0", "v1.2.0"]) ?? "", "v1.10.0");
            AssertEq(Web.UpdateChecker.PickNewestTag(["V0.9", "v0.10", "junk", null]) ?? "", "v0.10");
            Assert(Web.UpdateChecker.PickNewestTag(["junk", "also-junk"]) == null, "no parseable tag yields null");
            Assert(Web.UpdateChecker.PickNewestTag([]) == null, "empty list yields null");
        });

        Test("ai describe: endpoint normalization + think-block stripping", () =>
        {
            static string? Url(string e) =>
                new Neolink.Ai.AiSettings { Endpoint = e }.CompletionsUrl()?.ToString();
            // The user pastes an API base (LM Studio shows ".../v1"), a bare host
            // or the full path — all must land on one /chat/completions URL.
            AssertEq(Url("http://127.0.0.1:1234/v1") ?? "", "http://127.0.0.1:1234/v1/chat/completions");
            AssertEq(Url("http://127.0.0.1:1234/v1/") ?? "", "http://127.0.0.1:1234/v1/chat/completions");
            AssertEq(Url("http://127.0.0.1:1234/v1/chat/completions") ?? "", "http://127.0.0.1:1234/v1/chat/completions");
            // A bare host gets the OpenAI-convention /v1 — LM Studio answers the
            // unprefixed path with a 200 that isn't a completion (seen live).
            AssertEq(Url("http://127.0.0.1:1234") ?? "", "http://127.0.0.1:1234/v1/chat/completions");
            AssertEq(Url("https://api.example.com") ?? "", "https://api.example.com/v1/chat/completions");
            // An explicit non-/v1 path (proxy, gateway) is respected as typed.
            AssertEq(Url("http://gw.local:8080/openai") ?? "", "http://gw.local:8080/openai/chat/completions");
            Assert(Url("") == null, "blank endpoint is unusable");
            Assert(Url("ftp://x") == null, "non-http scheme rejected");
            Assert(Url("not a url") == null, "junk rejected");

            // Reasoning models leak <think> blocks; answers must come out clean —
            // including an unterminated block from a truncated response.
            AssertEq(Neolink.Ai.AiDescriber.CleanAnswer(
                "<think>hmm, a person\nmaybe two</think>A person walks to the door.") ?? "",
                "A person walks to the door.");
            AssertEq(Neolink.Ai.AiDescriber.CleanAnswer("  plain answer ") ?? "", "plain answer");
            Assert(Neolink.Ai.AiDescriber.CleanAnswer("<think>only thoughts") == null,
                "an unterminated think block with no answer yields null");
            Assert(Neolink.Ai.AiDescriber.CleanAnswer(null) == null, "null in, null out");

            // Threat-level split: the first line carries GREEN/YELLOW/RED per the
            // protocol; markdown litter and same-line continuations must not break it.
            static string LevelOf(string s) => Neolink.Ai.AiDescriber.SplitLevel(s).Level ?? "-";
            static string TextOf(string s) => Neolink.Ai.AiDescriber.SplitLevel(s).Text ?? "-";
            AssertEq(LevelOf("GREEN\nA cat walks by."), "green");
            AssertEq(TextOf("GREEN\nA cat walks by."), "A cat walks by.");
            AssertEq(LevelOf("**RED** — a person is carrying a weapon."), "red");
            AssertEq(TextOf("**RED** — a person is carrying a weapon."), "a person is carrying a weapon.");
            AssertEq(LevelOf("Yellow: someone loiters by the car"), "yellow");
            AssertEq(LevelOf("A person walks to the door."), "-"); // no level line: text stays whole
            AssertEq(TextOf("A person walks to the door."), "A person walks to the door.");
            AssertEq(LevelOf("RED"), "red"); // level-only answer: no description
            AssertEq(TextOf("RED"), "-");
            // "Greenhouse" must not read as a GREEN verdict (word boundary).
            AssertEq(LevelOf("Greenhouse door left open."), "-");
            // The "Threat level: X" spelling some models insist on — prefix only.
            AssertEq(LevelOf("Threat level: YELLOW\nSomeone is loitering."), "yellow");
            AssertEq(TextOf("Threat level: YELLOW\nSomeone is loitering."), "Someone is loitering.");
            AssertEq(LevelOf("A man in a red jacket walks by."), "-"); // mid-sentence color ≠ verdict

            // Object inventory: the line the model was asked for, peeled off however
            // it chose to dress it, leaving the description whole.
            static string ObjOf(string s) =>
                string.Join("|", Neolink.Ai.AiDescriber.SplitObjects(s).Objects);
            static string ObjTextOf(string s) => Neolink.Ai.AiDescriber.SplitObjects(s).Text ?? "-";
            AssertEq(ObjOf("OBJECTS: person, package, white van\nA courier leaves a box."),
                "person|package|white van");
            AssertEq(ObjTextOf("OBJECTS: person, package\nA courier leaves a box."),
                "A courier leaves a box.");
            AssertEq(ObjOf("**Objects detected:** dog; ball\nThe dog plays."), "dog|ball");
            AssertEq(ObjOf("OBJECTS: a person, the dog, 2 cars"), "person|dog|cars");
            AssertEq(ObjOf("OBJECTS: none\nAn empty driveway."), "");
            AssertEq(ObjTextOf("OBJECTS: none\nAn empty driveway."), "An empty driveway.");
            AssertEq(ObjOf("OBJECTS: person, person, PERSON"), "person"); // one thing, named thrice
            // A description that opens with a noun is NOT an inventory: a stolen
            // first sentence costs more than a missing list.
            AssertEq(ObjOf("A person walks past the gate."), "");
            AssertEq(ObjTextOf("A person walks past the gate."), "A person walks past the gate.");
            // Entries stay short and few: a sentence that wandered into the list is
            // not a searchable thing, and ten is the contract.
            AssertEq(ObjOf("OBJECTS: person, a man carrying a large cardboard box up the steps"), "person");
            AssertEq(Neolink.Ai.AiDescriber.SplitObjects(
                "OBJECTS: a, b, c, d, e, f, g, h, i, j, k, l").Objects.Count, 10);
            // The line is found even when the model leads with prose before it.
            AssertEq(ObjOf("Here is what I see.\nOBJECTS: bicycle\nA bike leans on the wall."), "bicycle");

            // Ollama endpoint normalization (native /api/chat).
            static string? OUrl(string e) =>
                new Neolink.Ai.AiSettings { OllamaEndpoint = e }.OllamaUrl()?.ToString();
            AssertEq(OUrl("http://127.0.0.1:11434") ?? "", "http://127.0.0.1:11434/api/chat");
            AssertEq(OUrl("http://127.0.0.1:11434/") ?? "", "http://127.0.0.1:11434/api/chat");
            AssertEq(OUrl("http://127.0.0.1:11434/api/chat") ?? "", "http://127.0.0.1:11434/api/chat");
            Assert(OUrl("") == null, "blank ollama endpoint is unusable");
            // The provider picks which URL is active.
            var prov = new Neolink.Ai.AiSettings
            {
                Provider = "ollama",
                Endpoint = "http://a:1234/v1",
                OllamaEndpoint = "http://b:11434",
            };
            AssertEq(prov.ActiveUrl()?.ToString() ?? "", "http://b:11434/api/chat");
            prov.Provider = "openai";
            AssertEq(prov.ActiveUrl()?.ToString() ?? "", "http://a:1234/v1/chat/completions");

            // Per-frame labels bind each image to its time (interleaved on the
            // OpenAI/Anthropic paths, a numbered list on Ollama's flat array),
            // and the grounding rules pin the two clauses that matter: never
            // invent a return, trust a burned-in camera timestamp.
            AssertEq(Neolink.Ai.AiDescriber.FrameLabel(2, 12, 4), "Frame 3 of 12 — +4s into the event:");
            // Negative offsets are pre-roll and must say so in words — a bare
            // "-3s" reads like a typo to the model.
            AssertEq(Neolink.Ai.AiDescriber.FrameLabel(0, 12, -3),
                "Frame 1 of 12 — 3s BEFORE the trigger (pre-roll):");
            Assert(Neolink.Ai.AiSettings.GroundingProtocol.Contains("left the view")
                && Neolink.Ai.AiSettings.GroundingProtocol.Contains("timestamp"),
                "grounding rules cover the no-invented-return and camera-timestamp clauses");
            // The default prompt turns the trigger labels into an assignment —
            // find the subject, and honest absence beats an invented sighting.
            Assert(Neolink.Ai.AiSettings.DefaultPrompt.Contains("find it first")
                && Neolink.Ai.AiSettings.DefaultPrompt.Contains("do not invent it"),
                "default prompt hunts the detected subject and forbids inventing it");

            // Long events go to the model in ordered parts capped by frame count
            // AND payload bytes (full-res snapshots broke a 210 MB pipe, live
            // 2026-07-26); a single over-budget frame still travels, alone.
            var ct0 = new DateTime(2026, 1, 1);
            var cf = Enumerable.Range(0, 250).Select(i => (ct0, new[] { (byte)i })).ToList();
            var chunked = Neolink.Ai.AiDescriber.ChunkFrames(cf, 100, long.MaxValue);
            AssertEq(string.Join(",", chunked.Select(c => c.Count)), "100,100,50");
            AssertEq((int)chunked[2][0].Jpeg[0], 200); // order preserved across the slices
            var sized = Enumerable.Range(0, 5).Select(_ => (ct0, new byte[10])).ToList();
            AssertEq(string.Join(",", Neolink.Ai.AiDescriber.ChunkFrames(sized, 100, 25)
                .Select(c => c.Count)), "2,2,1");
            AssertEq(Neolink.Ai.AiDescriber.ChunkFrames(
                new List<(DateTime, byte[])> { (ct0, new byte[99]) }, 100, 25).Count, 1);
            AssertEq(Neolink.Ai.AiDescriber.MoreSevere(null, "green") ?? "-", "green");
            AssertEq(Neolink.Ai.AiDescriber.MoreSevere("yellow", "red") ?? "-", "red");
            AssertEq(Neolink.Ai.AiDescriber.MoreSevere("red", null) ?? "-", "red");
            AssertEq(Neolink.Ai.AiDescriber.MoreSevere("green", "yellow") ?? "-", "yellow");

            // The event's ENDING keeps its frames (live complaint 2026-07-27: the
            // car driving away at the close of an event never reached the model).
            // The closing-window merge appends the unpaced final keyframes, paying
            // for the budget out of the MIDDLE — opening and ending both keep
            // full density.
            var kept = Enumerable.Range(0, 28)
                .Select(i => (Utc: ct0.AddSeconds(i * 3), Data: new[] { (byte)i })).ToList();
            var closingBuf = Enumerable.Range(0, 5)
                .Select(i => (Utc: ct0.AddSeconds(100 + i * 2), Data: new[] { (byte)(100 + i) })).ToList();
            Neolink.Ai.AiCapture.MergeClosing(kept, closingBuf, locked: 5, budget: 30);
            Assert(kept.Count <= 30, "budget held after the closing merge");
            AssertEq((int)kept[^1].Data[0], 104);     // very last closing frame survives
            AssertEq((int)kept[^5].Data[0], 100);     // the whole closing window survives
            AssertEq((int)kept[4].Data[0], 4);        // opening prefix untouched
            // A closing frame no newer than the kept set is a duplicate, not an append.
            var dupKept = new List<(DateTime Utc, byte[] Data)> { (ct0.AddSeconds(50), new byte[] { 1 }) };
            Neolink.Ai.AiCapture.MergeClosing(dupKept,
                new List<(DateTime Utc, byte[] Data)> { (ct0.AddSeconds(50), new byte[] { 1 }) }, 0, 30);
            AssertEq(dupKept.Count, 1);
            // Degenerate budget: the opening and the very LAST look both survive.
            var tiny = Enumerable.Range(0, 4)
                .Select(i => (Utc: ct0.AddSeconds(i), Data: new[] { (byte)i })).ToList();
            Neolink.Ai.AiCapture.MergeClosing(tiny,
                new List<(DateTime Utc, byte[] Data)> { (ct0.AddSeconds(9), new byte[] { 9 }) }, 0, 2);
            AssertEq(tiny.Count, 2);
            AssertEq((int)tiny[^1].Data[0], 9);
            // The closing window reaches back past the recorder's post-quiet to
            // the departure that closed the event.
            AssertEq((int)Neolink.Ai.AiCapture.ClosingWindowFor(0).TotalSeconds, 10);
            AssertEq((int)Neolink.Ai.AiCapture.ClosingWindowFor(8).TotalSeconds, 18);
            AssertEq((int)Neolink.Ai.AiCapture.ClosingWindowFor(600).TotalSeconds, 130);
            // The unpaced closing buffer: time-pruned, then halved past its cap.
            var cbuf = Enumerable.Range(0, 10)
                .Select(i => (Utc: ct0.AddSeconds(i), Data: new[] { (byte)i })).ToList();
            Neolink.Ai.AiCapture.PruneClosing(cbuf, ct0.AddSeconds(9), TimeSpan.FromSeconds(5), 100);
            AssertEq(string.Join(",", cbuf.Select(f => f.Data[0])), "4,5,6,7,8,9");
            Neolink.Ai.AiCapture.PruneClosing(cbuf, ct0.AddSeconds(9), TimeSpan.FromSeconds(60), 4);
            AssertEq(string.Join(",", cbuf.Select(f => f.Data[0])), "4,5,7,9");
            // The prompts ask for the ending in both layers: the (replaceable)
            // default prompt and the always-appended grounding rules.
            Assert(Neolink.Ai.AiSettings.DefaultPrompt.Contains("final frames"),
                "default prompt follows the event to its end");
            Assert(Neolink.Ai.AiSettings.GroundingProtocol.Contains("through to the last frame"),
                "grounding rules carry the description to the last frame");
            // And when the tail genuinely is not pictured (snapshot pacing), the
            // request says so instead of letting the model invent an ending.
            var gapRec = new Recording.EventRecord { Id = "t", Camera = "c",
                StartUtc = ct0, EndUtc = ct0.AddSeconds(60), Labels = { "person" } };
            var gapFrames = new List<(DateTime, byte[])> { (ct0.AddSeconds(2), new byte[] { 1 }) };
            Assert(Neolink.Ai.AiDescriber.BuildUserText(gapRec, gapFrames, 1, 1, null)
                .Contains("not pictured"), "a bare tail is declared to the model");
            gapFrames.Add((ct0.AddSeconds(56), new byte[] { 2 }));
            Assert(!Neolink.Ai.AiDescriber.BuildUserText(gapRec, gapFrames, 1, 1, null)
                .Contains("not pictured"), "a covered tail needs no disclaimer");

            // Frame-capture misses back off (4, 8, 16, 30s flat) instead of giving
            // up: three quick failures at a busy event start used to zero out the
            // whole event's frames (live 2026-07-25).
            AssertEq(Neolink.Ai.AiCapture.RetryPause(1).TotalSeconds.ToString("0"), "4");
            AssertEq(Neolink.Ai.AiCapture.RetryPause(2).TotalSeconds.ToString("0"), "8");
            AssertEq(Neolink.Ai.AiCapture.RetryPause(3).TotalSeconds.ToString("0"), "16");
            AssertEq(Neolink.Ai.AiCapture.RetryPause(4).TotalSeconds.ToString("0"), "30");
            AssertEq(Neolink.Ai.AiCapture.RetryPause(50).TotalSeconds.ToString("0"), "30");

            // Sampling: two composable knobs (interval + frame cap), defaults
            // 2s / 10 frames; keep-frames is a strict opt-in. The cap keeps its
            // historic JSON name so pre-rework ai.json files carry it over.
            var sdef = new Neolink.Ai.AiSettings();
            Assert(sdef is { SampleEverySeconds: 2, MaxFrames: 30, KeepFrames: false },
                "sampling defaults: every 2s, 30 frames max, frames not kept");
            var legacy = System.Text.Json.JsonSerializer.Deserialize<Neolink.Ai.AiSettings>(
                "{\"captureSeconds\":25,\"sampleMode\":\"interval\"}",
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                });
            Assert(legacy is { MaxFrames: 25 },
                "historic captureSeconds JSON name loads as the frame cap; dead sampleMode is ignored");

            // Decimation never touches the protected opening window (the event's
            // first seconds at 1 fps) — only the tail halves — and it starts at
            // the SECOND-newest frame, so the freshest look always survives a
            // pass (deleting the newest was part of the missing-ending bug).
            var thin = Enumerable.Range(0, 12)
                .Select(i => (Utc: new DateTime(2026, 1, 1).AddSeconds(i), Jpeg: new[] { (byte)i }))
                .ToList();
            Neolink.Ai.AiCapture.ThinTail(thin, 5);
            AssertEq(string.Join(",", thin.Select(f => f.Jpeg[0])), "0,1,2,3,4,5,7,9,11");
            // No opening frames (tiny budget, or the camera missed all five
            // slots): every-other halving; first AND newest frames always kept.
            var classic = Enumerable.Range(0, 8)
                .Select(i => (Utc: new DateTime(2026, 1, 1).AddSeconds(i), Jpeg: new[] { (byte)i }))
                .ToList();
            Neolink.Ai.AiCapture.ThinTail(classic, 0);
            AssertEq(string.Join(",", classic.Select(f => f.Jpeg[0])), "0,1,3,5,7");

            // The embedded vision-test image must stay a decodable JPEG (SOI…EOI) —
            // a corrupted constant would make every connection test fail confusingly.
            var testJpeg = Neolink.Ai.AiDescriber.TestJpeg();
            Assert(testJpeg.Length > 100 && testJpeg[0] == 0xFF && testJpeg[1] == 0xD8
                && testJpeg[^2] == 0xFF && testJpeg[^1] == 0xD9,
                "embedded AI test image is a valid JPEG");

            // Anthropic-style: blank endpoint means the real API; /v1/messages is
            // appended for bases and /v1 stems; explicit full paths pass through.
            static string? AUrl(string e) =>
                new Neolink.Ai.AiSettings { AnthropicEndpoint = e }.AnthropicUrl()?.ToString();
            AssertEq(AUrl("") ?? "", "https://api.anthropic.com/v1/messages");
            AssertEq(AUrl("http://proxy:4000") ?? "", "http://proxy:4000/v1/messages");
            AssertEq(AUrl("http://proxy:4000/v1") ?? "", "http://proxy:4000/v1/messages");
            AssertEq(AUrl("http://proxy:4000/v1/messages") ?? "", "http://proxy:4000/v1/messages");
            prov.Provider = "anthropic";
            AssertEq(prov.ActiveUrl()?.ToString() ?? "", "https://api.anthropic.com/v1/messages");

            // The per-camera opt-in defaults OFF and round-trips through Update.
            var dir = Path.Combine(Path.GetTempPath(), "neolink-selftest-ai-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            try
            {
                var settings = new Recording.RecordingSettings(dir);
                Assert(!settings.Get("cam").AiDescribe, "AI describe is a strict opt-in (default off)");
                settings.Update("cam", events: null, continuous: null, eventTypes: null,
                    setEventTypes: false, aiDescribe: true);
                Assert(settings.Get("cam").AiDescribe, "opt-in persists");
                settings.Update("cam", events: false, continuous: null, eventTypes: null,
                    setEventTypes: false);
                Assert(settings.Get("cam").AiDescribe, "unrelated updates leave the opt-in alone");
                // Scene notes: set, survive unrelated updates, clear on demand.
                settings.Update("cam", events: null, continuous: null, eventTypes: null,
                    setEventTypes: false, aiContext: "faces the street", setAiContext: true);
                AssertEq(settings.Get("cam").AiContext ?? "-", "faces the street");
                settings.Update("cam", events: true, continuous: null, eventTypes: null,
                    setEventTypes: false);
                AssertEq(settings.Get("cam").AiContext ?? "-", "faces the street");
                settings.Update("cam", events: null, continuous: null, eventTypes: null,
                    setEventTypes: false, aiContext: null, setAiContext: true);
                Assert(settings.Get("cam").AiContext == null, "scene notes clear on demand");

                // Scene notes ride the user text as CONTEXT (framed so they can't
                // pass as a description of the frames); absent notes leave no trace.
                var notesRec = new Recording.EventRecord { Id = "e1", Camera = "cam" };
                notesRec.StartUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                notesRec.EndUtc = notesRec.StartUtc.AddSeconds(10);
                notesRec.Labels.Add("person");
                var oneFrame = new List<(DateTime, byte[])> { (notesRec.StartUtc.AddSeconds(2), new byte[] { 1 }) };
                var withNotes = Neolink.Ai.AiDescriber.BuildUserText(notesRec, oneFrame, 1, 1,
                    prevSummary: null, sceneNotes: "faces the street");
                Assert(withNotes.Contains("faces the street") && withNotes.Contains("not a description"),
                    "scene notes ride the prompt, framed as context");
                Assert(!Neolink.Ai.AiDescriber.BuildUserText(notesRec, oneFrame, 1, 1, null)
                        .Contains("notes"), "no notes, no notes clause");
                // Pre-roll frames announce themselves: negative offsets get the
                // explaining sentence, and the offsets list carries the sign.
                var withPre = new List<(DateTime, byte[])>
                {
                    (notesRec.StartUtc.AddSeconds(-3), new byte[] { 1 }),
                    (notesRec.StartUtc.AddSeconds(2), new byte[] { 2 }),
                };
                var preText = Neolink.Ai.AiDescriber.BuildUserText(notesRec, withPre, 1, 1, null);
                Assert(preText.Contains("pre-roll") && preText.Contains("-3s, +2s"),
                    "pre-roll frames get the explainer and signed offsets");
                Assert(!Neolink.Ai.AiDescriber.BuildUserText(notesRec, oneFrame, 1, 1, null)
                        .Contains("pre-roll"), "no pre-roll, no pre-roll clause");

                // Pre-roll helpers: JPEG splitting on SOI/EOI (truncated tails
                // dropped) and index spreading that always keeps both ends.
                byte[] j1 = { 0xFF, 0xD8, 1, 2, 0xFF, 0xD9 };
                byte[] j2 = { 0xFF, 0xD8, 3, 0xFF, 0xD9 };
                var stitched = j1.Concat(j2).Concat(new byte[] { 0xFF, 0xD8, 9 }).ToArray();
                var split = Neolink.Ai.AiPreroll.SplitJpegs(stitched);
                Assert(split.Count == 2 && split[0].SequenceEqual(j1) && split[1].SequenceEqual(j2),
                    "MJPEG stream splits into whole JPEGs; the truncated tail is dropped");
                AssertEq(string.Join(",", Neolink.Ai.AiPreroll.SpreadIndices(2, 3)), "0,1");
                AssertEq(string.Join(",", Neolink.Ai.AiPreroll.SpreadIndices(9, 3)), "0,4,8");
                AssertEq(string.Join(",", Neolink.Ai.AiPreroll.SpreadIndices(3, 3)), "0,1,2");

                // ffmpeg locator: NEOLINK_FFMPEG override wins when it exists,
                // falls back to the PATH scan (per-OS exe name), null when neither.
                var ffDir = Path.Combine(dir, "ffbin");
                Directory.CreateDirectory(ffDir);
                var ffExe = Path.Combine(ffDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                File.WriteAllBytes(ffExe, new byte[] { 1 });
                AssertEq(Neolink.Media.Ffmpeg.Locate(ffExe, null) ?? "-", ffExe);
                AssertEq(Neolink.Media.Ffmpeg.Locate(null, ffDir) ?? "-", ffExe);
                AssertEq(Neolink.Media.Ffmpeg.Locate(
                    Path.Combine(ffDir, "missing-ffmpeg"), ffDir) ?? "-", ffExe);
                Assert(Neolink.Media.Ffmpeg.Locate(null, Path.Combine(dir, "nowhere")) == null,
                    "no override, nothing on the path: no ffmpeg");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
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

            // Service-port table (msg 37): each element carries its number in a
            // camelCase child of its own name, plus an optional enable flag —
            // missing enable means the firmware exposes no toggle (null).
            var svc = Streaming.CameraControl.MapServicePorts(new[]
            {
                System.Xml.Linq.XElement.Parse(
                    "<ServerPort version=\"1.1\"><serverPort>9000</serverPort></ServerPort>"),
                System.Xml.Linq.XElement.Parse(
                    "<HttpPort version=\"1.1\"><httpPort>80</httpPort><enable>0</enable></HttpPort>"),
                System.Xml.Linq.XElement.Parse(
                    "<OnvifPort version=\"1.1\"><onvifPort>8000</onvifPort><enable>1</enable></OnvifPort>"),
                System.Xml.Linq.XElement.Parse("<NotAService><x>1</x></NotAService>"),
            });
            AssertEq(string.Join(";", svc.Select(s => $"{s.Service}:{s.Port}:{s.Enabled?.ToString() ?? "null"}")),
                "server:9000:null;http:80:False;onvif:8000:True");

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
            // A thumbnail artifact: the event-media endpoints accept RTSP Basic
            // credentials (like snapshots), and that contract needs a file to serve.
            // Deliberately SHORTER than the vault's 8-byte magic sniff: a short read
            // at EOF used to leave the handle's pointer at the end, and the served
            // stream wrote 0 of Length bytes — Kestrel answered 500 for a file that
            // opens fine. FootageVault pins Position back to 0; this file keeps it so.
            File.WriteAllBytes(Path.Combine(evDir, "thumb.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            // A real config file: /api/admin/config reads and describes it. The
            // stored camera lets the connection test be exercised the way the list
            // row uses it — by name alone, resolving every field from config.json.
            // Port 9 is closed, so the dial fails fast instead of timing out.
            File.WriteAllText(Path.Combine(dir, "config.json"),
                """{ "cameras": [ { "name": "storedcam", "username": "stored-admin", "password": "p", "address": "127.0.0.1:9" } ] }""");

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
                    // Mounts /api/admin/notifications and the per-camera gates.
                    Notifier = new Neolink.Notifications.Notifier(
                        new Neolink.Notifications.NotificationStore(
                            dir, new Neolink.Notifications.SecretProtector(dir)), "selftest"),
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
                // Event footage takes the same RTSP Basic credentials (HA shows an
                // event's thumbnail in a notification with the credentials it already
                // holds); wrong password stays out, and the JSON endpoints stay
                // session-only even with valid Basic credentials attached.
                using (var res = http.GetAsync($"/api/events/{Uri.EscapeDataString(evId)}/thumb").Result)
                    AssertEq((int)res.StatusCode, 401);
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{Uri.EscapeDataString(evId)}/thumb"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", BasicHdr("rtspuser", "rtsp pass"));
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 200);
                    AssertEq(res.Content.Headers.ContentType!.MediaType!, "image/jpeg");
                    AssertEq(res.Content.ReadAsByteArrayAsync().Result.Length, 4); // full tiny file served (vault position pin)
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{Uri.EscapeDataString(evId)}/clip"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", BasicHdr("rtspuser", "rtsp pass"));
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 404); // creds accepted, no clip staged
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{Uri.EscapeDataString(evId)}/thumb"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", BasicHdr("rtspuser", "wrong pass"));
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 401);
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/events"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", BasicHdr("rtspuser", "rtsp pass"));
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, 401); // metadata stays session-only
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

                // Recording on/off from the UI. Disabling STASHES the section under
                // recording_disabled instead of deleting it — retention and friends
                // survive the off period — and the stash's path is reported so the
                // enable form prefills. Enabling restores the stash, then applies
                // the request on top. The storage path is write-probed on enable.
                System.Text.Json.JsonElement AdminPut(string body, int expect)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/config{tokenQ}")
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    using var res = http.Send(req);
                    AssertEq((int)res.StatusCode, expect);
                    return GetJson(http, $"/api/admin/config{tokenQ}").GetProperty("settings");
                }
                var recDir = Path.Combine(dir, "rec-enable");
                var recDirJson = System.Text.Json.JsonSerializer.Serialize(recDir);
                var on = AdminPut("""{"recording":{"path":""" + recDirJson + ""","retentionDays":9}}""", 200);
                AssertEq(on.GetProperty("recording").GetProperty("retentionDays").GetInt32(), 9);
                var off = AdminPut("""{"removeRecording":true}""", 200);
                Assert(off.GetProperty("recording").ValueKind == System.Text.Json.JsonValueKind.Null,
                    "recording section absent while disabled");
                AssertEq(off.GetProperty("recordingDisabledPath").GetString()!, recDir);
                Assert(File.ReadAllText(Path.Combine(dir, "config.json")).Contains("recording_disabled"),
                    "disable stashes the section in the file");
                var back = AdminPut("""{"recording":{"path":""" + recDirJson + "}}", 200);
                AssertEq(back.GetProperty("recording").GetProperty("retentionDays").GetInt32(), 9);
                Assert(back.GetProperty("recordingDisabledPath").ValueKind == System.Text.Json.JsonValueKind.Null,
                    "the stash is consumed by the enable");
                // A path that cannot be written is refused while the admin is here
                // to read the reason, not at the next start.
                var blocker = Path.Combine(dir, "rec-blocker.txt");
                File.WriteAllText(blocker, "in the way");
                AdminPut("""{"recording":{"path":"""
                    + System.Text.Json.JsonSerializer.Serialize(Path.Combine(blocker, "sub")) + "}}", 400);

                // Camera connection test: a UDP camera must be tested the way the
                // server actually connects to it. These models never listen on TCP,
                // so testing them the TCP way timed out however healthy they were.
                // The routing shows up in the validation: over UDP an address is
                // optional (the UID finds the camera) and the UID is what's required.
                var udpNoUid = PostJson(http, $"/api/admin/cameras/test{tokenQ}",
                    """{"type":"reolink","udp":true,"username":"admin"}""");
                Assert(!udpNoUid.GetProperty("ok").GetBoolean(), "a UDP test without a UID fails");
                Assert(udpNoUid.GetProperty("message").GetString()!.Contains("UID"),
                    "a UDP test asks for the UID, never for an address: "
                    + udpNoUid.GetProperty("message").GetString());
                // A TCP camera with no address still complains about the address.
                var tcpNoAddr = PostJson(http, $"/api/admin/cameras/test{tokenQ}",
                    """{"type":"reolink","username":"admin"}""");
                Assert(!tcpNoAddr.GetProperty("ok").GetBoolean(), "a TCP test without an address fails");
                Assert(tcpNoAddr.GetProperty("message").GetString()!.Contains("address"),
                    "a TCP test still complains about the address: "
                    + tcpNoAddr.GetProperty("message").GetString());

                // Testing by NAME alone — what the camera-list row sends, so a camera
                // can be checked without opening its editor or touching a field. Every
                // field must come from config.json: reaching the dial at all proves
                // the address and credentials resolved (the port is closed, so it
                // fails, but it must not fail complaining about missing input).
                var stored = PostJson(http, $"/api/admin/cameras/test{tokenQ}",
                    """{"name":"storedcam","type":"reolink"}""");
                var storedMsg = stored.GetProperty("message").GetString()!;
                Assert(!stored.GetProperty("ok").GetBoolean(), "the closed port makes the stored test fail");
                Assert(!storedMsg.Contains("address is required") && !storedMsg.Contains("username is required"),
                    "a name-only test resolves address and credentials from config.json, got: " + storedMsg);

                // Pan/tilt for Frigate through the camera editor: each mode round-trips, and every rule refuses the save.
                var configPath = Path.Combine(dir, "config.json");
                var configBefore = File.ReadAllText(configPath);
                HttpResponseMessage SavePtz(string mode, int? port = null) => PostRaw(http, $"/api/admin/cameras{tokenQ}",
                    $$"""{"originalName":"storedcam","name":"storedcam","type":"reolink","address":"127.0.0.1:9","username":"stored-admin","ptzMode":"{{mode}}"{{(port is { } p ? $",\"ptzPort\":{p}" : "")}}}""");
                string SaveError(HttpResponseMessage res) { using (res) return res.Content.ReadAsStringAsync().Result; }
                Assert(SaveError(SavePtz("shared")).Contains("no login"), "with no RTSP users, the login rule refuses the save");
                File.WriteAllText(configPath, configBefore.Replace("\"cameras\"", "\"users\": [ { \"name\": \"frigate\", \"pass\": \"pw\" } ], \"cameras\""));
                using (var res = SavePtz("own", 18794)) AssertEq((int)res.StatusCode, 200);
                var saved = File.ReadAllText(configPath);
                Assert(saved.Contains("\"ptz_port\": 18794") && !saved.Contains("ptz_share"), "own port saved as ptz_port");
                var listed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    http.GetStringAsync($"/api/admin/cameras{tokenQ}").Result);
                var storedCam = listed.GetProperty("cameras").EnumerateArray().First(c => c.GetProperty("name").GetString() == "storedcam");
                Assert(storedCam.GetProperty("ptzMode").GetString() == "own" && storedCam.GetProperty("ptzPort").GetInt32() == 18794
                       && !storedCam.GetProperty("ptzOpen").GetBoolean(), "the editor reads the mode, port and login back");
                Assert(listed.GetProperty("ptz").GetProperty("sharedPort").GetInt32() == 8656
                       && listed.GetProperty("ptz").GetProperty("users").GetBoolean(), "and what it checks a port against");
                using (var res = SavePtz("shared")) AssertEq((int)res.StatusCode, 200);
                saved = File.ReadAllText(configPath);
                Assert(saved.Contains("\"ptz_share\": true") && !saved.Contains("ptz_port"), "shared replaces the own port");
                Assert(SaveError(SavePtz("own", 8655)).Contains("already the web port"), "a taken port is refused");
                Assert(SaveError(SavePtz("own", 70000)).Contains("1-65535"), "an impossible port is refused");
                Assert(SaveError(SavePtz("sideways")).Contains("off, shared or own"), "an unknown mode is refused");
                using (var res = SavePtz("off")) AssertEq((int)res.StatusCode, 200);
                saved = File.ReadAllText(configPath);
                Assert(!saved.Contains("ptz_share") && !saved.Contains("ptz_port"), "off removes both keys");
                File.WriteAllText(configPath, configBefore);

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

                // Admin notifications API: the PUT sanitization layer, write-only
                // token semantics, and the per-camera channel gates.
                System.Net.Http.HttpResponseMessage NotifPut(string body)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/notifications{tokenQ}")
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    return http.Send(req);
                }
                var notif0 = GetJson(http, $"/api/admin/notifications{tokenQ}");
                Assert(!notif0.GetProperty("hasWebhookToken").GetBoolean(), "no webhook token stored yet");
                Assert(!notif0.GetProperty("hasPassword").GetBoolean(), "no smtp password stored yet");

                var longStr = new string('x', 5000);
                var junkHeaders = string.Join(",",
                    Enumerable.Range(0, 30).Select(i => $"\"H{i}: {new string('v', 600)}\""));
                using (var res = NotifPut(
                    $$"""
                    {"webhookEnabled":false,"webhookMethod":"DELETE","webhookBodyMode":"weird",
                     "webhookBodyTemplate":"{{longStr}}","webhookUrl":"http://x/{{longStr}}",
                     "webhookHeaders":[{{junkHeaders}}],"webhookPreset":"{{longStr}}",
                     "webhookToken":"Bearer tk_apitest","smtpPort":99999}
                    """))
                    AssertEq((int)res.StatusCode, 200);
                var notif1 = GetJson(http, $"/api/admin/notifications{tokenQ}");
                AssertEq(notif1.GetProperty("webhookMethod").GetString()!, "POST");
                AssertEq(notif1.GetProperty("webhookBodyMode").GetString()!, "json");
                AssertEq(notif1.GetProperty("webhookBodyTemplate").GetString()!.Length, 4000);
                AssertEq(notif1.GetProperty("webhookUrl").GetString()!.Length, 2000);
                AssertEq(notif1.GetProperty("webhookPreset").GetString()!.Length, 40);
                AssertEq(notif1.GetProperty("smtpPort").GetInt32(), 65535);
                var hs = notif1.GetProperty("webhookHeaders");
                AssertEq(hs.GetArrayLength(), 20);
                Assert(hs.EnumerateArray().All(h => h.GetString()!.Length <= 500), "header lines capped");
                Assert(notif1.GetProperty("hasWebhookToken").GetBoolean(), "token recorded");
                Assert(!notif1.TryGetProperty("webhookToken", out _), "token never echoed back");
                Assert(!File.ReadAllText(Path.Combine(dir, "notifications.json")).Contains("tk_apitest"),
                    "webhook token never plaintext on disk");

                // Write-only: an absent field keeps the token, an empty one clears it.
                using (var res = NotifPut("""{"enabled":false}""")) AssertEq((int)res.StatusCode, 200);
                Assert(GetJson(http, $"/api/admin/notifications{tokenQ}")
                    .GetProperty("hasWebhookToken").GetBoolean(), "absent token field keeps the stored token");
                using (var res = NotifPut("""{"webhookToken":""}""")) AssertEq((int)res.StatusCode, 200);
                Assert(!GetJson(http, $"/api/admin/notifications{tokenQ}")
                    .GetProperty("hasWebhookToken").GetBoolean(), "empty token field clears it");

                // Channel gates: switching a per-camera opt-in ON while the channel
                // is unconfigured is refused (web-UI courtesy); OFF always lands.
                System.Net.Http.HttpResponseMessage RecPost(string body)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/cameras/apicam/recording{tokenQ}")
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    return http.Send(req);
                }
                using (var res = RecPost("""{"emailEvents":true}""")) AssertEq((int)res.StatusCode, 409);
                using (var res = RecPost("""{"webhookEvents":true}""")) AssertEq((int)res.StatusCode, 409);
                var rec0 = GetJson(http, $"/api/cameras/apicam/recording{tokenQ}");
                Assert(!rec0.GetProperty("emailAvailable").GetBoolean()
                       && !rec0.GetProperty("webhookAvailable").GetBoolean(),
                    "unconfigured channels report unavailable");

                using (var res = NotifPut(
                    """{"webhookEnabled":true,"webhookUrl":"http://127.0.0.1:1/hook","publicUrl":"  https://cams.example.com/  "}"""))
                    AssertEq((int)res.StatusCode, 200);
                AssertEq(GetJson(http, $"/api/admin/notifications{tokenQ}")
                    .GetProperty("publicUrl").GetString()!, "https://cams.example.com/");
                using (var res = RecPost("""{"webhookEvents":true}""")) AssertEq((int)res.StatusCode, 200);
                var rec1 = GetJson(http, $"/api/cameras/apicam/recording{tokenQ}");
                Assert(rec1.GetProperty("webhookAvailable").GetBoolean()
                       && rec1.GetProperty("webhookEvents").GetBoolean(),
                    "configured webhook opens the gate and reports available");

                // Unconfiguring later leaves the stored opt-in alone (it just goes
                // quiet), and OFF must still be accepted so the user can clean up.
                using (var res = NotifPut("""{"webhookEnabled":false}""")) AssertEq((int)res.StatusCode, 200);
                var rec2 = GetJson(http, $"/api/cameras/apicam/recording{tokenQ}");
                Assert(!rec2.GetProperty("webhookAvailable").GetBoolean()
                       && rec2.GetProperty("webhookEvents").GetBoolean(),
                    "stored opt-in survives the channel going away");
                using (var res = RecPost("""{"webhookEvents":false}""")) AssertEq((int)res.StatusCode, 200);

                // The webhook test endpoint reports failure as a body, 502 — the
                // UI shows the reason, nothing throws.
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/admin/notifications/webhook-test{tokenQ}")
                { Content = new StringContent("""{"webhookEnabled":true,"webhookUrl":"http://127.0.0.1:1/hook"}""",
                    Encoding.UTF8, "application/json") })
                using (var res = http.Send(req))
                {
                    AssertEq((int)res.StatusCode, 502);
                    Assert(res.Content.ReadAsStringAsync().Result.Contains("error"),
                        "webhook test failure carries the reason");
                }

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

        Test("mp4 pipe: a trailing index moves ahead of the media for a non-seeking reader", () =>
        {
            static byte[] U32(uint v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
            static byte[] Cat(params byte[][] parts) { var ms = new MemoryStream(); foreach (var p in parts) ms.Write(p); return ms.ToArray(); }
            static byte[] Box(string type, params byte[][] parts) { var body = Cat(parts); return Cat(U32((uint)(8 + body.Length)), Encoding.ASCII.GetBytes(type), body); }
            static byte[] Full(string type, params byte[][] parts) => Box(type, new byte[4], Cat(parts));
            static string TypeAt(byte[] buf, int pos) => Encoding.ASCII.GetString(buf, pos + 4, 4);
            static uint ReadU32(byte[] buf, int pos) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));

            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("iso5"), U32(0));
            var media = Enumerable.Range(0, 100).Select(i => (byte)(i * 7)).ToArray();
            var free = Box("free", media);
            uint chunk0 = (uint)(ftyp.Length + 8), chunk1 = chunk0 + 40;
            var stbl = Box("stbl",
                Full("stss", U32(3), U32(1), U32(5), U32(9)),
                Full("stco", U32(2), U32(chunk0), U32(chunk1)));
            var hdlr = Full("hdlr", U32(0), Encoding.ASCII.GetBytes("vide"), new byte[12]);
            var mvhd = Full("mvhd", U32(0), U32(0), U32(1000), U32(22131), new byte[80]);
            var moov = Box("moov", mvhd, Box("trak", Box("mdia", hdlr, Box("minf", stbl))));
            var classic = Cat(ftyp, free, moov);

            var pipe = Mp4Pipe.Open(new MemoryStream(classic));
            Assert(pipe.Rewritten, "an index behind the media is moved");
            AssertEq(pipe.VideoKeyframes, 3);
            Assert(Math.Abs((pipe.DurationSeconds ?? 0) - 22.131) < 1e-9, "duration read from mvhd");
            var outMs = new MemoryStream();
            pipe.CopyToAsync(outMs, default).GetAwaiter().GetResult();
            var piped = outMs.ToArray();
            AssertEq(piped.Length, classic.Length);
            AssertEq(TypeAt(piped, 0), "ftyp");
            AssertEq(TypeAt(piped, ftyp.Length), "moov");
            AssertEq(TypeAt(piped, ftyp.Length + moov.Length), "mdat");
            Assert(piped.AsSpan(ftyp.Length + moov.Length + 8, media.Length).SequenceEqual(media), "the media bytes follow the index untouched");
            int stco = piped.AsSpan().IndexOf(Encoding.ASCII.GetBytes("stco"));
            AssertEq(ReadU32(piped, stco + 12), (uint)(chunk0 + moov.Length));
            AssertEq(ReadU32(piped, stco + 16), (uint)(chunk1 + moov.Length));
            Assert(ReadU32(classic, classic.AsSpan().IndexOf(Encoding.ASCII.GetBytes("stco")) + 12) == chunk0, "the source index is left alone");

            var fragmented = Cat(ftyp, moov, Box("moof", new byte[16]), Box("mdat", media));
            var asIs = Mp4Pipe.Open(new MemoryStream(fragmented));
            Assert(!asIs.Rewritten && asIs.VideoKeyframes == 3, "an index ahead of the media stays put");
            var out2 = new MemoryStream();
            asIs.CopyToAsync(out2, default).GetAwaiter().GetResult();
            Assert(out2.ToArray().AsSpan().SequenceEqual(fragmented), "copied byte for byte");

            var growing = fragmented[..^7];
            var cut = Mp4Pipe.Open(new MemoryStream(growing));
            var out3 = new MemoryStream();
            cut.CopyToAsync(out3, default).GetAwaiter().GetResult();
            Assert(!cut.Rewritten && out3.ToArray().AsSpan().SequenceEqual(growing), "a file cut mid-box (still being written) copies as-is");

            var tmp = Path.Combine(Path.GetTempPath(), "neolink-selftest-clip-" + Guid.NewGuid().ToString("n") + ".mp4");
            File.WriteAllBytes(tmp, classic);
            try
            {
                var fed = new MemoryStream();
                Notifications.EventEmailer.FeedClipAsync(tmp, fed, default).GetAwaiter().GetResult();
                Assert(fed.ToArray().AsSpan().SequenceEqual(piped), "event-email snapshots feed the clip in pipe order");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        });

        Test("email snapshots: a finalized clip samples through the pipe (NEOLINK_FACE_CLIP)", () =>
        {
            var clip = Environment.GetEnvironmentVariable("NEOLINK_FACE_CLIP");
            if (string.IsNullOrEmpty(clip) || !File.Exists(clip) || Ffmpeg.ExePath is not { } ffmpeg)
            {
                Console.WriteLine("    (NEOLINK_FACE_CLIP unset or no ffmpeg — skipped)");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var frames = Notifications.EventEmailer.SampleClipAsync(ffmpeg, clip, 0, 20, 5).GetAwaiter().GetResult();
            Console.WriteLine($"    {frames.Count} snapshot(s) in {sw.Elapsed.TotalSeconds:0.0}s");
            AssertEq(frames.Count, 5);
            var trimmed = Notifications.EventEmailer.SampleClipAsync(ffmpeg, clip, 5, 15, 3).GetAwaiter().GetResult();
            AssertEq(trimmed.Count, 3);
        });

        Test("emergency mode: settings resolve per camera, persist, and stamp the arm time", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Notifications.EmergencyStore(dir);
                // No cameras: the deterrent pass is a no-op, so this exercises the
                // persistence and arm/disarm bookkeeping on its own.
                var mode = new Notifications.EmergencyMode(store,
                    () => Array.Empty<Notifications.EmergencyCamera>());
                int changed = 0;
                mode.Changed += () => { changed++; return Task.CompletedTask; };

                var next = store.Snapshot();
                Assert(next is { Enabled: false, Email: true, Webhook: true, Siren: false, Lights: false },
                    "a fresh install is disarmed, with the notify channels pre-picked and nothing loud");
                next.Enabled = true;
                next.Siren = true;
                next.Cameras["Quiet"] = new Notifications.EmergencyCameraOptions { Siren = false };
                var armed = mode.ApplyAsync(next, default).GetAwaiter().GetResult();
                AssertEq(changed, 1);
                Assert(armed.ArmedUtc != null, "arming stamps the time");
                Assert(armed.SirenFor("Loud"), "an unlisted camera follows the all-cameras choice");
                Assert(!armed.SirenFor("Quiet"), "a per-camera override wins over it");
                Assert(armed.EmailFor("Quiet"), "an override only covers the fields it sets");

                var reloaded = new Notifications.EmergencyStore(dir).Snapshot();
                Assert(reloaded is { Enabled: true, Siren: true }, "armed state survives a restart");
                Assert(!reloaded.SirenFor("Quiet"), "per-camera overrides survive with it");

                var stamp = armed.ArmedUtc;
                var again = mode.ApplyAsync(reloaded, default).GetAwaiter().GetResult();
                AssertEq(again.ArmedUtc, stamp); // editing while armed must not reset "since"
                var off = mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                Assert(off.ArmedUtc == null, "disarming clears the stamp");
                Assert(off.Siren, "the options it was armed with are kept for next time");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("live object boxes: settings are clamped, persist, and always leave something to outline", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var store = new Detect.DetectStore(dir);
                var fresh = store.Snapshot();
                Assert(!fresh.Enabled, "a fresh install draws no boxes");
                AssertEq(string.Join(",", fresh.EffectiveGroups),
                    string.Join(",", Detect.DetectSettings.DefaultGroups));

                fresh.Enabled = true;
                fresh.MinConfidence = 500;   // a client that ignores the range
                fresh.Fps = 0;               // …in both directions
                fresh.Groups = new List<string> { "people", "unicorns" };
                store.Save(fresh);

                var saved = store.Snapshot();
                AssertEq(saved.MinConfidence, 90);
                AssertEq(saved.Fps, 1);
                AssertEq(string.Join(",", saved.Groups!), "people"); // the invented group is gone
                AssertEq(string.Join(",", saved.EffectiveGroups), "people");

                var reloaded = new Detect.DetectStore(dir).Snapshot();
                Assert(reloaded.Enabled, "the switch survives a restart");
                AssertEq(reloaded.MinConfidence, 90);
                AssertEq(string.Join(",", reloaded.Groups!), "people");
                Assert(!reloaded.Detailed, "the bigger model is not fetched unless it is asked for");
                reloaded.Detailed = true;
                store.Save(reloaded);
                Assert(new Detect.DetectStore(dir).Snapshot().Detailed, "and that choice survives a restart too");

                // Every group unticked would silently mean "the default three" on the
                // way back in, which is the opposite of what the user asked for.
                reloaded.Groups = new List<string>();
                store.Save(reloaded);
                AssertEq(string.Join(",", store.Snapshot().EffectiveGroups),
                    string.Join(",", Detect.DetectSettings.DefaultGroups));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("live object boxes: only a checksum-matching file is ever served, and offline never fetches", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // allowDownload: false — a test suite must never reach the network,
                // and without it this would pull 35 MB.
                var assets = new Detect.DetectAssets(dir, allowDownload: false);
                AssertEq(assets.Current().State, "missing");
                assets.EnsureAsync().GetAwaiter().GetResult();
                AssertEq(assets.Current().State, "missing");
                Assert(!assets.Ready, "nothing is ready before anything is downloaded");

                Assert(assets.Locate("../../secret.key") == null, "the URL cannot name a file off the list");
                Assert(assets.Locate("ort.webgpu.min.js") == null, "an absent file resolves to nothing");

                // The right name and the right LENGTH, wrong bytes: this is the case
                // the checksum exists for, so it must not become servable.
                var planted = Path.Combine(dir, "detect-assets");
                Directory.CreateDirectory(planted);
                File.WriteAllBytes(Path.Combine(planted, Detect.DetectAssets.Runtime.FileName),
                    new byte[Detect.DetectAssets.Runtime.Bytes]);
                // Past the few-second miss cache the lookup really re-reads the file.
                var fresh = new Detect.DetectAssets(dir, allowDownload: false);
                Assert(fresh.Locate(Detect.DetectAssets.Runtime.FileName) == null,
                    "a file that does not match its published checksum is never served");
                Assert(!fresh.Ready, "and never counts towards being ready");

                // The optional model is its own question: missing, it must not make a
                // working feature read as unready, and it is still name-checked.
                Assert(!fresh.DetailedReady, "the bigger model is absent until fetched");
                AssertEq(fresh.DetailedStatus().State, "missing");

                // Switched off, the files go back to the disk they came from — but
                // only the ones this server downloaded.
                var state = Path.Combine(dir, "detect-assets");
                Directory.CreateDirectory(state);
                foreach (var a in Detect.DetectAssets.All)
                    File.WriteAllBytes(Path.Combine(state, a.FileName), new byte[8]);
                var housekeeper = new Detect.DetectAssets(dir, allowDownload: false);
                AssertEq(housekeeper.Tidy(new Detect.DetectSettings { Enabled = true, Detailed = true }), 0);
                AssertEq(housekeeper.Tidy(new Detect.DetectSettings { Enabled = true, Detailed = false }), 1);
                Assert(!File.Exists(Path.Combine(state, Detect.DetectAssets.Detailed.FileName)),
                    "turning the bigger model off gives its 29 MB back");
                Assert(File.Exists(Path.Combine(state, Detect.DetectAssets.Model.FileName)),
                    "and leaves the one still in use alone");
                AssertEq(housekeeper.Tidy(new Detect.DetectSettings { Enabled = false }),
                    Detect.DetectAssets.Core.Length);
                Assert(Directory.GetFiles(state).Length == 0, "switched off, none of it is kept");
                Assert(Detect.DetectAssets.All.Contains(Detect.DetectAssets.Detailed),
                    "but the server can serve it once it is there");
                Assert(!Detect.DetectAssets.Core.Contains(Detect.DetectAssets.Detailed),
                    "and readiness never waits on it");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode latches sirens and lights, and gives every light back on disarm", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var lit = new DeterrentControl("Lit");
                var ownLight = new DeterrentControl("OwnLight");
                var noGear = new DeterrentControl("Bare", siren: false, light: false);
                ownLight.LightState = "open"; // the user had this floodlight on already
                var cams = new List<Notifications.EmergencyCamera>
                {
                    new Notifications.EmergencyCamera("Lit", lit), new Notifications.EmergencyCamera("OwnLight", ownLight), new Notifications.EmergencyCamera("Bare", noGear),
                };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);

                var next = store.Snapshot();
                next.Enabled = true;
                next.Siren = true;
                next.Lights = true;
                // Only "Lit" gets the light; the others must be left untouched by it.
                next.Cameras["OwnLight"] = new Notifications.EmergencyCameraOptions { Lights = false };
                mode.ApplyAsync(next, default).GetAwaiter().GetResult();
                AssertEq(lit.SirenState, true);
                AssertEq(lit.LightState, "open");
                AssertEq(ownLight.LightWrites, 0);
                Assert(noGear.SirenState == null, "a camera with no siren is not asked to sound one");
                AssertEq(noGear.LightWrites, 0);

                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(lit.SirenState, false);
                AssertEq(lit.LightState, "close");
                Assert(ownLight.LightWrites == 0 && ownLight.LightState == "open",
                    "disarming never touches a light emergency mode did not turn on");

                // A restart while armed: fresh instance, empty prior-light memory,
                // and the camera's light is ALREADY forced on. Reading it back as
                // the state to restore would strand it on forever.
                lit.LightState = "open";
                var armed = store.Snapshot();
                armed.Enabled = true;
                armed.Siren = true;
                armed.Lights = true;
                armed.Cameras.Clear();
                store.Save(armed);
                var resumed = new Notifications.EmergencyMode(
                    new Notifications.EmergencyStore(dir), () => cams);
                resumed.ResumeAsync(default).GetAwaiter().GetResult();
                AssertEq(lit.SirenState, true);
                resumed.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(lit.LightState, "close");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode retries only offline cameras and never nags one that has no siren", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var sleepy = new DeterrentControl("Sleepy") { Offline = true };
                // Advertises a siren but rejects the command — a refusal, not an outage.
                var refuser = new DeterrentControl("Refuser", light: false) { RefusesSiren = true };
                var bare = new DeterrentControl("Bare", siren: false, light: false);
                var cams = new List<Notifications.EmergencyCamera>
                {
                    new Notifications.EmergencyCamera("Sleepy", sleepy), new Notifications.EmergencyCamera("Refuser", refuser), new Notifications.EmergencyCamera("Bare", bare),
                };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);

                var next = store.Snapshot();
                next.Enabled = true;
                next.Siren = true;
                next.Lights = true;
                mode.ApplyAsync(next, default).GetAwaiter().GetResult();
                var issues = mode.Issues.OrderBy(i => i.Camera).ToList();
                AssertEq(issues.Count, 2); // Sleepy's siren AND light failed: one line, not two
                AssertEq(issues[0].Camera, "Refuser");
                Assert(issues[0].Reason.StartsWith("siren on:"), "a refusal is reported with the command and reason");
                AssertEq(issues[1].Camera, "Sleepy");
                AssertEq(issues[1].Reason, "offline");
                AssertEq(refuser.SirenWrites, 1);
                AssertEq(bare.SirenWrites, 0);

                // Still asleep: retried, still failing. The refusal is NOT retried,
                // but its issue stays: nothing will ever sound that siren.
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(sleepy.SirenWrites, 0);
                AssertEq(refuser.SirenWrites, 1);
                AssertEq(mode.Issues.Count, 2);

                // Editing options while armed must not wipe the refusal either.
                mode.ApplyAsync(cur => { cur.Email = false; return cur; }, default).GetAwaiter().GetResult();
                Assert(mode.Issues.Any(i => i.Camera == "Refuser"), "a refused siren stays reported while armed");

                // Sleepy wakes up: the sweep latches it and its issue clears.
                sleepy.Offline = false;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(sleepy.SirenState, true);
                AssertEq(sleepy.LightState, "open");
                AssertEq(mode.Issues.Count, 1);

                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(sleepy.SirenState, false);
                AssertEq(sleepy.LightState, "close");
                Assert(refuser.SirenWrites == 1 && bare.SirenWrites == 0,
                    "a camera whose siren never started is never told to stop one, so it cannot refuse and be retried forever");
                AssertEq(mode.Issues.Count, 0);
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(refuser.SirenWrites, 1);

                // Asleep for the whole arm..disarm: nothing was ever sent to it, so
                // nothing is owed, reported, or retried once it wakes.
                int writes = sleepy.SirenWrites;
                sleepy.Offline = true;
                mode.SetEnabledAsync(true, default).GetAwaiter().GetResult();
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(mode.Issues.Count, 0);
                sleepy.Offline = false;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(sleepy.SirenWrites, writes);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode silences a siren that was latched before a restart, even when disarmed before the camera returns", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // Before the restart: armed, siren latched. After it: the camera is
                // unreachable, and the user disarms before it comes back.
                var late = new DeterrentControl("Late") { Offline = true, SirenState = true, LightState = "open" };
                var cams = new List<Notifications.EmergencyCamera> { new Notifications.EmergencyCamera("Late", late) };
                var armed = new Notifications.EmergencySettings { Enabled = true, Siren = true, Lights = true };
                new Notifications.EmergencyStore(dir).Save(armed);

                var mode = new Notifications.EmergencyMode(new Notifications.EmergencyStore(dir), () => cams);
                mode.ResumeAsync(default).GetAwaiter().GetResult();
                AssertEq(mode.Issues.Count, 1);
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(late.SirenWrites, 0); // still unreachable

                late.Offline = false;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(late.SirenState, false);
                AssertEq(late.LightState, "close");
                AssertEq(mode.Issues.Count, 0);
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(late.SirenWrites, 1); // nothing left to retry
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode: a dropped link is retried, a disarm before the resume pass still silences, and a restart while disarmed clears leftovers", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var cam = new DeterrentControl("Cam");
                var noSiren = new DeterrentControl("Mute", siren: false);
                var cams = new List<Notifications.EmergencyCamera> { new Notifications.EmergencyCamera("Cam", cam), new Notifications.EmergencyCamera("Mute", noSiren) };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);
                var armed = store.Snapshot();
                armed.Enabled = true;
                armed.Siren = true;
                armed.Lights = true;
                mode.ApplyAsync(armed, default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, true);

                // The link drops (IOException, not "offline") exactly as the user
                // disarms: the latch must survive and the sweep must finish the job.
                cam.Flaky = 1;
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, true);
                Assert(mode.Issues.Any(i => i.Camera == "Cam" && i.Reason.StartsWith("siren off:")),
                    "an unacknowledged siren-off is reported");
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, false);
                AssertEq(cam.LightState, "close");
                AssertEq(mode.Issues.Count, 0);

                // Restart while ARMED, then disarm in the window before the resume
                // pass has run: the fresh process must still reach the sirens.
                mode.ApplyAsync(armed, default).GetAwaiter().GetResult();
                File.Delete(Path.Combine(dir, "emergency-runtime.json")); // an install from before the file existed
                var fresh = new Notifications.EmergencyMode(new Notifications.EmergencyStore(dir), () => cams);
                fresh.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, false);
                AssertEq(cam.LightState, "close");
                Assert(noSiren.SirenWrites == 0, "a camera without a siren is not told to stop one, even after a restart");
                AssertEq(fresh.Issues.Count, 0);

                // Restart while DISARMED, with a siren still latched on a camera
                // that was offline at the disarm: the new process must remember
                // it and switch it off once the camera returns.
                var again = new Notifications.EmergencyMode(new Notifications.EmergencyStore(dir), () => cams);
                again.ApplyAsync(armed, default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, true);
                cam.Offline = true;
                again.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, true); // unreachable, still sounding
                var afterRestart = new Notifications.EmergencyMode(new Notifications.EmergencyStore(dir), () => cams);
                afterRestart.ResumeAsync(default).GetAwaiter().GetResult();
                cam.Offline = false;
                afterRestart.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, false);
                AssertEq(cam.LightState, "close");
                AssertEq(afterRestart.Issues.Count, 0);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode brings every camera live and hands each state back on disarm", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var suspended = new DeterrentControl("Suspended");
                var dark = new DeterrentControl("Dark") { Privacy = true };
                var dozing = new DeterrentControl("Dozing");
                var normal = new DeterrentControl("Normal");
                bool suspendedIsSuspended = true, dozingHeld = false, normalHeld = false;
                var cams = new List<Notifications.EmergencyCamera>
                {
                    new("Suspended", suspended)
                    {
                        Suspended = () => suspendedIsSuspended,
                        SetSuspended = v => suspendedIsSuspended = v,
                        PrivacyOn = () => suspended.Privacy,
                    },
                    new("Dark", dark) { PrivacyOn = () => dark.Privacy },
                    new("Dozing", dozing)
                    {
                        PrivacyOn = () => dozing.Privacy,
                        SetHoldAwake = v => dozingHeld = v,
                    },
                    // Already live and never dark: must be left completely alone.
                    new("Normal", normal)
                    {
                        Suspended = () => false,
                        SetSuspended = _ => throw new InvalidOperationException("must not touch a live camera"),
                        PrivacyOn = () => normal.Privacy,
                        SetHoldAwake = v => normalHeld = v,
                    },
                };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);

                mode.SetEnabledAsync(true, default).GetAwaiter().GetResult();
                Assert(!suspendedIsSuspended, "a suspended camera reconnects");
                Assert(!dark.Privacy, "a camera sitting in privacy mode can see again");
                Assert(dozingHeld && normalHeld, "battery cameras stop dozing while armed");
                AssertEq(normal.PrivacyWrites, 0);
                AssertEq(suspended.PrivacyWrites, 0); // it was suspended, not dark

                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                Assert(suspendedIsSuspended, "the suspension the user chose comes back");
                Assert(dark.Privacy, "privacy mode comes back");
                Assert(!dozingHeld && !normalHeld, "battery cameras are allowed to doze again");
                AssertEq(normal.PrivacyWrites, 0);
                Assert(!normal.Privacy, "a camera that was never dark is not put INTO privacy mode by disarming");
                AssertEq(mode.Issues.Count, 0);

                // Armed a second time with the states already as emergency mode
                // wants them: nothing to force, nothing to restore afterwards.
                int darkWrites = dark.PrivacyWrites;
                dark.Privacy = false;
                mode.SetEnabledAsync(true, default).GetAwaiter().GetResult();
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                AssertEq(dark.PrivacyWrites, darkWrites);
                Assert(!dark.Privacy, "a camera the user took out of privacy mode stays out of it");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode reaches a camera that was unreachable for the privacy change, both ways", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // A dark battery camera with no siren and no light: nothing else is
                // pending for it, so only a privacy-aware sweep can ever reach it.
                var dark = new DeterrentControl("Dark", siren: false, light: false)
                {
                    Privacy = true,
                    Offline = true,
                };
                var cams = new List<Notifications.EmergencyCamera>
                {
                    new("Dark", dark) { PrivacyOn = () => dark.Privacy },
                };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);

                mode.SetEnabledAsync(true, default).GetAwaiter().GetResult();
                Assert(dark.Privacy, "it could not be reached, so it is still dark");
                Assert(mode.Issues.Any(i => i.Camera == "Dark"), "and that is reported, not silently dropped");
                dark.Offline = false;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                Assert(!dark.Privacy, "the sweep lifts privacy mode once the camera answers");
                AssertEq(mode.Issues.Count, 0);

                // Disarm while it is away again: the restore must be owed, retried,
                // and completed — not forgotten because nothing else was pending.
                dark.Offline = true;
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                Assert(!dark.Privacy, "still unreachable, so not yet restored");
                dark.Offline = false;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                Assert(dark.Privacy, "the privacy mode the user configured comes back");
                AssertEq(mode.Issues.Count, 0);
                // And nothing is owed any more, so the sweep goes quiet.
                int writes = dark.PrivacyWrites;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(dark.PrivacyWrites, writes);

                // A camera that has not yet said whether it is dark must be looked
                // at again, not written off as "not dark".
                bool? pushed = null;
                var quiet = new DeterrentControl("Quiet", siren: false, light: false) { Privacy = true };
                var cams2 = new List<Notifications.EmergencyCamera>
                {
                    new("Quiet", quiet) { PrivacyOn = () => pushed },
                };
                var mode2 = new Notifications.EmergencyMode(
                    new Notifications.EmergencyStore(dir + "b"), () => cams2);
                Directory.CreateDirectory(dir + "b");
                mode2.SetEnabledAsync(true, default).GetAwaiter().GetResult();
                AssertEq(quiet.PrivacyWrites, 0); // nothing known yet
                pushed = true;                    // the camera reports in
                mode2.RetryPendingAsync(default).GetAwaiter().GetResult();
                Assert(!quiet.Privacy, "once it says it is dark, the sweep lifts it");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
                try { Directory.Delete(dir + "b", true); } catch { }
            }
        });

        Test("emergency mode keeps a camera awake while its siren is still sounding", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var cam = new DeterrentControl("Dozing");
                bool held = false;
                var cams = new List<Notifications.EmergencyCamera>
                {
                    new("Dozing", cam) { PrivacyOn = () => cam.Privacy, SetHoldAwake = v => held = v },
                };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);
                var armed = store.Snapshot();
                armed.Enabled = true;
                armed.Siren = true;
                mode.ApplyAsync(armed, default).GetAwaiter().GetResult();
                Assert(held && cam.SirenState == true, "armed: held awake, siren on");

                // The link drops exactly as the user disarms: a camera allowed to
                // doze with its siren latched is one the retry can never reach.
                cam.Flaky = 1;
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                Assert(cam.SirenState == true && held, "disarmed with the siren unreached: still held awake");
                Assert(mode.Issues.Any(i => i.Camera == "Dozing"), "…and reported");

                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                Assert(cam.SirenState == false && !held, "the retry silences it, and only then may it doze");
                AssertEq(mode.Issues.Count, 0);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode does not re-suspend a camera while its siren is still sounding", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var cam = new DeterrentControl("Gate");
                bool suspended = true;
                var cams = new List<Notifications.EmergencyCamera>
                {
                    new("Gate", cam)
                    {
                        Suspended = () => suspended,
                        SetSuspended = v => suspended = v,
                        PrivacyOn = () => cam.Privacy,
                    },
                };
                var store = new Notifications.EmergencyStore(dir);
                var mode = new Notifications.EmergencyMode(store, () => cams);
                var armed = store.Snapshot();
                armed.Enabled = true;
                armed.Siren = true;
                mode.ApplyAsync(armed, default).GetAwaiter().GetResult();
                Assert(!suspended && cam.SirenState == true, "armed: resumed and sounding");

                // It drops off the network exactly as the user disarms. Re-suspending
                // now would cut the only route back to a siren that is still going.
                cam.Offline = true;
                mode.SetEnabledAsync(false, default).GetAwaiter().GetResult();
                Assert(cam.SirenState == true, "still sounding — it could not be reached");
                Assert(!suspended, "so it is deliberately left connected for the retry");

                cam.Offline = false;
                mode.RetryPendingAsync(default).GetAwaiter().GetResult();
                AssertEq(cam.SirenState, false);
                Assert(suspended, "and only once it is silent does the suspension come back");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("emergency mode overrides the per-camera opt-ins and suspends the cooldown", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var protector = new Notifications.SecretProtector(dir);
                var store = new Notifications.NotificationStore(dir, protector);
                var notifier = new Notifications.Notifier(store, "selftest");
                var recSettings = new Recording.RecordingSettings(dir);
                var events = new Recording.EventStore(Path.Combine(dir, "rec"));
                var emailer = new Notifications.EventEmailer(store, notifier, recSettings, events);
                var s = new Notifications.NotificationSettings
                {
                    Enabled = true,
                    Recipient = "someone@example.com",
                    SmtpHost = "mail.example.com",
                    EventCooldownMinutes = 60,
                };
                var rec = events.Create("Cam", DateTime.UtcNow, new[] { "person" });

                Assert(!emailer.Claim(rec, s, out _, out _, out _),
                    "a camera that never opted in sends nothing");

                var em = new Notifications.EmergencySettings { Enabled = true };
                emailer.Emergency = () => em;
                Assert(emailer.Claim(rec, s, out _, out _, out var ch),
                    "armed sends even though the camera never opted in");
                Assert(ch.HasFlag(Notifications.AlertChannels.Email), "the configured channel is forced on");
                Assert(!ch.HasFlag(Notifications.AlertChannels.Webhook),
                    "an unconfigured channel stays off — armed cannot invent a destination");
                Assert(emailer.Claim(rec, s, out _, out _, out _),
                    "the cooldown does not apply while armed: every detection sends");

                em.Cameras["Cam"] = new Notifications.EmergencyCameraOptions { Email = false };
                Assert(!emailer.Claim(rec, s, out _, out _, out _),
                    "a per-camera override can opt a camera out of the emergency");

                em.Enabled = false;
                recSettings.Update("Cam", events: null, continuous: null, eventTypes: null,
                    setEventTypes: false, emailEvents: true);
                var noCooldown = new Notifications.NotificationSettings
                {
                    Enabled = true,
                    Recipient = "someone@example.com",
                    SmtpHost = "mail.example.com",
                    EventCooldownMinutes = 0,
                };
                Assert(emailer.Claim(rec, noCooldown, out _, out _, out _),
                    "disarmed, the camera's own opt-in sends");
                Assert(!emailer.Claim(rec, s, out _, out _, out _),
                    "and its cooldown applies again");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Test("offline alert images stay off by default and respect the lookback", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"neolink-selftest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var protector = new Notifications.SecretProtector(dir);
                var store = new Notifications.NotificationStore(dir, protector);
                var notifier = new Notifications.Notifier(store, "selftest");
                var recSettings = new Recording.RecordingSettings(dir);
                var events = new Recording.EventStore(Path.Combine(dir, "rec"));
                var emailer = new Notifications.EventEmailer(store, notifier, recSettings, events);
                var offline = DateTime.UtcNow;
                events.Create("Cam", offline.AddMinutes(-5), new[] { "person" });

                // The decision itself, both ways — a test that only ever expects
                // "no images" would still pass with the whole feature deleted.
                var off = new Notifications.NotificationSettings();
                var on = new Notifications.NotificationSettings
                {
                    OfflineAttachSnapshots = true,
                    OfflineSnapshotLookbackMinutes = 60,
                };
                Assert(!Notifications.EventEmailer.ShouldAttach(off, offline.AddMinutes(-5), offline),
                    "off by default: an existing install keeps the plain text alert");
                Assert(Notifications.EventEmailer.ShouldAttach(on, offline.AddMinutes(-5), offline),
                    "a detection inside the window is attached");
                Assert(!Notifications.EventEmailer.ShouldAttach(on, offline.AddMinutes(-90), offline),
                    "a detection older than the window is not");
                // Measured from when the camera went offline, not from now: an
                // offline threshold longer than the lookback would otherwise make
                // the feature silently do nothing.
                Assert(Notifications.EventEmailer.ShouldAttach(on,
                        DateTime.UtcNow.AddMinutes(-95), DateTime.UtcNow.AddMinutes(-90)),
                    "the window is anchored to the outage, not to alert time");
                Assert(Notifications.EventEmailer.ShouldAttach(
                        new Notifications.NotificationSettings
                        {
                            OfflineAttachSnapshots = true,
                            OfflineSnapshotLookbackMinutes = 0,
                        }, offline.AddYears(-1), offline),
                    "0 = no time limit");

                AssertEq(emailer.LastDetectionImagesAsync("Cam", offline).GetAwaiter().GetResult().Count, 0);
                store.Save(on, null);
                AssertEq(emailer.LastDetectionImagesAsync("NoSuchCam", offline).GetAwaiter().GetResult().Count, 0);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

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

    /// <summary>An ONVIF camera on loopback for the client tests: every request is recorded
    /// (operation, and the raw headers and body) and answered by the given function.</summary>
    private sealed class FakeOnvif : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly Func<string, string, (int Status, string Body, string? Headers)> _answer;
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Op, string Raw)> Requests = new();
        public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeOnvif(Func<string, string, (int Status, string Body, string? Headers)> answer)
        {
            _answer = answer;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public const string Soap = "http://www.w3.org/2003/05/soap-envelope";
        public static string Env(string inner) => $"<s:Envelope xmlns:s=\"{Soap}\"><s:Body>{inner}</s:Body></s:Envelope>";
        public static (int, string, string?) Ok(string inner) => (200, Env(inner), null);
        public static (int, string, string?) Refused() => (400, Env(
            "<s:Fault><s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode>" +
            "</s:Code><s:Reason><s:Text>Sender not Authorized</s:Text></s:Reason></s:Fault>"), null);

        /// <summary>A plain answer for the calls discovery and the settings reads make.</summary>
        public static (int, string, string?) Answer(string op) => Ok(op switch
        {
            "GetVideoSources" => "<trt:GetVideoSourcesResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\">" +
                                 "<trt:VideoSources token=\"VS_1\"/></trt:GetVideoSourcesResponse>",
            "GetDeviceInformation" => "<tds:GetDeviceInformationResponse xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">" +
                                      "<tds:Manufacturer>Acme</tds:Manufacturer><tds:Model>Cam9</tds:Model></tds:GetDeviceInformationResponse>",
            "GetProfiles" => "<trt:GetProfilesResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" " +
                             "xmlns:tt=\"http://www.onvif.org/ver10/schema\"><trt:Profiles token=\"P1\"><tt:Name>main</tt:Name>" +
                             "<tt:PTZConfiguration token=\"ptz\"><tt:NodeToken>n</tt:NodeToken></tt:PTZConfiguration>" +
                             "</trt:Profiles></trt:GetProfilesResponse>",
            _ => $"<x:{op}Response xmlns:x=\"urn:test\"/>",
        });

        private async Task AcceptAsync()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(System.Net.Sockets.TcpClient c)
        {
            using (c)
            {
                var s = c.GetStream();
                try
                {
                    while (true)
                    {
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (await s.ReadAsync(one) == 0) return;
                            head.Append((char)one[0]);
                        }
                        var len = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Content-Length:\s*(\d+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
                        var body = new byte[len];
                        for (int got = 0; got < len;)
                        {
                            int n = await s.ReadAsync(body.AsMemory(got));
                            if (n == 0) return;
                            got += n;
                        }
                        var raw = head + Encoding.UTF8.GetString(body);
                        var op = System.Text.RegularExpressions.Regex.Match(raw, @"<s:Body><\w+:(\w+)").Groups[1].Value;
                        Requests.Enqueue((op, raw));
                        var (status, reply, headers) = _answer(op, raw);
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/soap+xml\r\n" +
                                                                   $"Content-Length: {bytes.Length}\r\n{headers}\r\n"));
                        await s.WriteAsync(bytes);
                    }
                }
                catch (IOException) { }
            }
        }

        public void Dispose() => _listener.Stop();
    }

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

    // ONVIF pan/tilt requests, as Frigate's client sends them.
    private static (int Status, string Body) PtzCall(Onvif.OnvifPtzServer server, string header, string body, string? auth = null) =>
        server.HandleAsync(
            $"<s:Envelope xmlns:s=\"{Onvif.OnvifPtzServer.NsSoap}\" xmlns:tds=\"{Onvif.OnvifPtzServer.NsDevice}\" " +
            $"xmlns:trt=\"{Onvif.OnvifPtzServer.NsMedia}\" xmlns:tptz=\"{Onvif.OnvifPtzServer.NsPtz}\" " +
            $"xmlns:tt=\"{Onvif.OnvifPtzServer.NsSchema}\"><s:Header>{header}</s:Header><s:Body>{body}</s:Body></s:Envelope>",
            "http://nvr:8081", auth, "test", CancellationToken.None).GetAwaiter().GetResult();

    private static string PtzSigned(DateTime at, string user = "frigate", string pass = "pw") =>
        Protocol.OnvifClient.BuildSecurity(user, pass,
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), at, mustUnderstand: false);

    private static string PtzBasic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    private static System.Xml.Linq.XElement PtzFirst(string xml, string name) =>
        System.Xml.Linq.XDocument.Parse(xml).Descendants().First(e => e.Name.LocalName == name);

    /// <summary>Records the PTZ commands it accepts and announces them, as CameraControl does; reports a
    /// head with pan/tilt (or none), and can refuse the next few stops.</summary>
    private sealed class PtzRecordingControl(string name, bool hasPtz = true) : StubCameraControl(name), Streaming.ICameraControl
    {
        private readonly List<string> _moves = new();
        public int FailStops;
        public string Log() { lock (_moves) return string.Join(",", _moves); }
        public event Action<string>? PtzCommandSent;
        public override Task PtzAsync(string command, float speed, CancellationToken ct)
        {
            if (command == "stop" && Interlocked.Decrement(ref FailStops) >= 0)
                throw new IOException("link dropped");
            lock (_moves) _moves.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{command}@{speed}"));
            PtzCommandSent?.Invoke(command);
            return Task.CompletedTask;
        }
        public override Task<Streaming.CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
            Task.FromResult(new Streaming.CameraCapabilities(
                new Bc.Xml.VersionInfoXml { Model = "E1", FirmwareVersion = "v3.0" }, null,
                new Streaming.CameraFeatures(Ptz: hasPtz, Led: false, Pir: false, Battery: false, Talk: false, Zoom: Lens != null)));

        /// <summary>The preset slots, as the camera's HTTP API lists them; null = no HTTP API.</summary>
        public List<Streaming.PtzPresetInfo>? Presets;
        /// <summary>The zoom lens's range and position; null = a fixed lens.</summary>
        public (long Min, long Max, long Cur)? Lens;

        public new Task<IReadOnlyList<Streaming.PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct)
        {
            lock (_moves) return Task.FromResult<IReadOnlyList<Streaming.PtzPresetInfo>?>(Presets?.ToList());
        }
        public new Task PtzToPresetAsync(int id, CancellationToken ct)
        {
            lock (_moves) _moves.Add($"preset@{id}");
            PtzCommandSent?.Invoke("preset");
            return Task.CompletedTask;
        }
        public new Task SavePtzPresetAsync(int id, string name, CancellationToken ct)
        {
            lock (_moves)
            {
                _moves.Add($"save@{id}:{name}");
                Presets = Presets!.Select(p => p.Id == id ? new Streaming.PtzPresetInfo(id, name, true) : p).ToList();
            }
            return Task.CompletedTask;
        }
        public new Task<System.Xml.Linq.XElement?> GetZoomFocusAsync(CancellationToken ct) =>
            Task.FromResult(Lens is { } l
                ? System.Xml.Linq.XElement.Parse($"<PtzZoomFocus><zoom><maxPos>{l.Max}</maxPos><minPos>{l.Min}</minPos><curPos>{l.Cur}</curPos></zoom></PtzZoomFocus>")
                : null);
        public new Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct)
        {
            lock (_moves)
            {
                _moves.Add($"zoom@{movePos}");
                Lens = Lens!.Value with { Cur = movePos };
            }
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
        public virtual Task<System.Xml.Linq.XElement?> GetLedStateAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public virtual Task SetLedStateAsync(string? state, string? lightState,
            string? doorbellLightState, int? irBrightness, CancellationToken ct) => Task.CompletedTask;
        public Task<System.Xml.Linq.XElement?> GetPirStateAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task SetPirEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public virtual Task PtzAsync(string command, float speed, CancellationToken ct) => Task.CompletedTask;
        public Task RebootAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<System.Xml.Linq.XElement?> GetZoomFocusAsync(CancellationToken ct) =>
            Task.FromResult<System.Xml.Linq.XElement?>(null);
        public Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct) => Task.CompletedTask;
        public virtual Task SirenAsync(bool? on, CancellationToken ct) => Task.CompletedTask;
        public Task<bool?> GetPrivacyModeAsync(CancellationToken ct) => Task.FromResult<bool?>(null);
        public virtual Task SetPrivacyModeAsync(bool on, CancellationToken ct) => Task.CompletedTask;
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

    /// <summary>A camera with a floodlight and a siren that records what emergency
    /// mode asked of it, and reports the light state it was last told to set.</summary>
    private sealed class DeterrentControl(string name, bool siren = true, bool light = true)
        : StubCameraControl(name)
    {
        public string LightState = "close";
        public bool? SirenState;
        public int LightWrites, SirenWrites;
        /// <summary>Every call throws the offline exception, like a dozing battery camera.</summary>
        public bool Offline;
        /// <summary>Answers, but rejects the siren command (a model without one).</summary>
        public bool RefusesSiren;
        /// <summary>The next N calls fail like a dropped link (an IOException, not "offline").</summary>
        public int Flaky;

        private void Gate()
        {
            if (Offline) throw new Streaming.CameraOfflineException(CameraName);
            if (Flaky > 0) { Flaky--; throw new IOException("link dropped"); }
        }

        public override Task<Streaming.CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct)
        {
            Gate();
            return Task.FromResult(new Streaming.CameraCapabilities(null, null,
                new Streaming.CameraFeatures(false, false, false, false, false,
                    Siren: siren, Floodlight: light)));
        }

        public override Task<System.Xml.Linq.XElement?> GetLedStateAsync(CancellationToken ct)
        {
            Gate();
            return Task.FromResult<System.Xml.Linq.XElement?>(
                new System.Xml.Linq.XElement("LedState",
                    new System.Xml.Linq.XElement("lightState", LightState)));
        }

        public override Task SetLedStateAsync(string? state, string? lightState,
            string? doorbellLightState, int? irBrightness, CancellationToken ct)
        {
            Gate();
            if (lightState != null) { LightState = lightState; LightWrites++; }
            return Task.CompletedTask;
        }

        public override Task SirenAsync(bool? on, CancellationToken ct)
        {
            Gate();
            SirenWrites++;
            if (RefusesSiren) throw new NotSupportedException("audio alarm not supported");
            SirenState = on;
            return Task.CompletedTask;
        }

        /// <summary>Privacy mode ("dark" camera): the state emergency mode lifts.</summary>
        public bool Privacy;
        public int PrivacyWrites;

        public override Task SetPrivacyModeAsync(bool on, CancellationToken ct)
        {
            Gate();
            PrivacyWrites++;
            Privacy = on;
            return Task.CompletedTask;
        }
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
    /// <summary>Just enough IStreamHub for Sdp.Build: audio track info + the Opus flag.</summary>
    private sealed class OpusSdpHub : Streaming.IStreamHub
    {
        public string Name => "fake";
        public int SubscriberCount => 0;
        public int ViewerCount => 0;
        public bool VideoReady => true;
        public VideoCodec? Codec => VideoCodec.H264;
        public byte[]? Sps => null;
        public byte[]? Pps => null;
        public byte[]? Vps => null;
        public uint Width => 0;
        public uint Height => 0;
        public Streaming.AudioTrackInfo? Audio { get; set; }
        public (Guid id, ChannelReader<Streaming.HubPacket> reader) Subscribe(bool viewer = false)
            => throw new NotSupportedException();
        public void Unsubscribe(Guid id) { }
        public DateTime LastViewerAskUtc => DateTime.MinValue;
        public Task<bool> WaitForDescribeInfoAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>Collects the frames a puller publishes.</summary>
    private sealed class FrameSink : Streaming.IMediaSink
    {
        public readonly List<VideoFrame> Frames = new();
        public void PublishInfo(MediaInfo info) { }
        public void PublishVideo(VideoFrame frame) => Frames.Add(frame);
        public void PublishAac(AacFrame frame) { }
        public void PublishAdpcm(AdpcmFrame frame) { }
        public void SourceStopped() { }
    }

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
        using var cam = BindLoopbackUdp();
        int camPort = PortOf(cam);
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

        // LongRunning = a dedicated thread NOW. Task.Run queues to the thread
        // pool, and under load (a busy dev box, a saturated CI runner) the mock
        // started SECONDS late — after the client's handshake window had already
        // expired. The dedicated thread posts the first receive immediately; the
        // async body migrates to the pool only after that, which is harmless.
        var mock = Task.Factory.StartNew(async () =>
        {
            System.Net.IPEndPoint? client = null;
            int cid = 0;
            // 1. Handshake: accept the C2D_C, reply D2C_C_R rsp 0 — echoing the
            //    request's tid, as the real firmware does (the camera keys the
            //    session to it).
            while (!cts.IsCancellationRequested && client == null)
            {
                System.Net.Sockets.UdpReceiveResult r;
                try { r = await cam.ReceiveAsync(cts.Token); }
                catch (System.Net.Sockets.SocketException ex)
                {
                    // Windows surfaces a bounced SEND (ICMP port-unreachable) as a
                    // SocketException on the next RECEIVE. For UDP that is noise,
                    // not death — a mock that dies of it fails the handshake with
                    // a message blaming the network.
                    Log.Debug($"udptest: mock receive reset ({ex.SocketErrorCode}); continuing");
                    continue;
                }
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
                System.Net.Sockets.UdpReceiveResult r;
                try { r = await cam.ReceiveAsync(cts.Token); }
                catch (System.Net.Sockets.SocketException ex)
                {
                    Log.Debug($"udptest: mock receive reset ({ex.SocketErrorCode}); continuing");
                    continue;
                }
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
                else if (BcUdp.TryParseData(r.Buffer, out _, out var pid, out var pOff, out var pLen))
                {
                    clientDataPackets++;
                    var payload = r.Buffer.AsSpan(pOff, pLen);
                    // The keepalive answer: msg 234 echoed with our msgNum and resp 200.
                    if (payload.Length >= 20
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) == Neolink.Bc.BcConstants.MsgIdUdpKeepAlive
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]) == 777
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]) == 200)
                        kaReplies++;
                    await cam.SendAsync(BcUdp.BuildAck(cid, 0, pid, 0, ReadOnlySpan<byte>.Empty), r.RemoteEndPoint, cts.Token);
                }
                else if (BcUdp.TryParseAck(r.Buffer, out _, out _, out _, out _, out _))
                {
                    clientAckPackets++; // the continuous ack timer — the keepalive the camera needs
                }
            }
        }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        await using var conn = await Neolink.Protocol.BcUdpConnection.ConnectAsync(
            "127.0.0.1", uid, TimeSpan.FromSeconds(10), cts.Token, "udptest", camPort);

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
        // A bound socket that never answers is exactly what an asleep camera looks
        // like on the wire → probe returns false quickly.
        using (var asleep = BindLoopbackUdp())
            Assert(!await UdpDiscovery.IsReachableAsync("127.0.0.1", uid,
                    TimeSpan.FromMilliseconds(600), CancellationToken.None, camPort: PortOf(asleep)),
                "liveness probe is false while the camera is asleep");

        // Bring up a responder → probe returns true.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cam = BindLoopbackUdp();
        int camPort = PortOf(cam);
        int gotDisc = 0;
        // Dedicated thread for the same reason as the transport mock above: the
        // probe's window is seconds, and a pool-queued responder can start later
        // than that on a busy machine.
        var responder = Task.Factory.StartNew(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.Sockets.UdpReceiveResult r;
                try { r = await cam.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // A dead mock is indistinguishable from a dead network; say why.
                    // (Windows delivers ICMP port-unreachable as a receive-side
                    // SocketException on UDP — a straggler sent to a closed peer
                    // must not end the mock.)
                    Log.Debug($"udptest: mock camera receive error: {ex.Message}");
                    if (ex is System.Net.Sockets.SocketException) continue;
                    return;
                }
                Log.Debug($"udptest: mock camera got {r.Buffer.Length}B from {r.RemoteEndPoint}");
                if (!UdpDiscovery.TryParseDiscovery(r.Buffer, out _, out var xml, out _)) continue;
                if (xml.Contains("<C2D_C>", StringComparison.Ordinal))
                {
                    int cid = int.Parse(xml.Split("<cid>")[1].Split("</cid>")[0]);
                    var reply = $"<P2P><D2C_C_R><rsp>0</rsp><cid>{cid}</cid><did>3</did></D2C_C_R></P2P>";
                    try { await cam.SendAsync(UdpDiscovery.BuildDiscovery(9, reply), r.RemoteEndPoint, cts.Token); } catch { }
                }
                else if (xml.Contains("<C2D_DISC>", StringComparison.Ordinal)) gotDisc++;
            }
        }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        // logTag: every discovery reply logs at Debug — run the selftest with
        // NEOLINK_LOG=debug when this assertion ever flakes to see the wire.
        // 8 s, not 3: the reply itself is instant, but the responder's async
        // continuations still ride the thread pool, and this assertion exists to
        // test the probe, not the pool's injection rate under load.
        Assert(await UdpDiscovery.IsReachableAsync("127.0.0.1", uid, TimeSpan.FromSeconds(8), cts.Token,
                logTag: "udptest", camPort: camPort),
            "liveness probe is true the moment the camera answers");
        // The probe must release the session it just created — a C2D_DISC — or the
        // camera would keep retrying D2C_C_R for ~9 s after every 5 s poll.
        for (int i = 0; i < 40 && gotDisc == 0; i++) await Task.Delay(50);
        Assert(gotDisc > 0, "wake probe releases its session with a polite C2D_DISC");
        cts.Cancel();
        try { await responder; } catch { }
    }

    /// <summary>Bind a loopback UDP socket on an EPHEMERAL port (read it back from
    /// LocalEndPoint). The mock cameras used to take the real Baichuan port 2015
    /// with ReuseAddress — and on a dev box where a live Neolink server's own UDP
    /// machinery touches 2015, both binds "succeeded" and the OS delivered the
    /// test's packets to the other socket: intermittent handshake failures in
    /// waves matching the live server's wake scans. An ephemeral port cannot
    /// collide with anything, so the retry and the reuse flag are gone with it.</summary>
    private static System.Net.Sockets.UdpClient BindLoopbackUdp() =>
        new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));

    private static int PortOf(System.Net.Sockets.UdpClient c) =>
        ((System.Net.IPEndPoint)c.Client.LocalEndPoint!).Port;

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
