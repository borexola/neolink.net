// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.

using System.Text.Json;

namespace Neolink.Desktop;

/// <summary>
/// `Neolink.Desktop.exe --selftest` — the decision logic exercised without a
/// window, a server or a person. Everything that decides WHETHER to notify lives
/// behind pure functions precisely so this can run in CI on a machine with no
/// cameras attached.
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    public static bool Run()
    {
        UrlNormalization();
        QuietHours();
        DetectionAlerts();
        OfflineAlerts();
        ServerConditionAlerts();
        ToastPayload();
        BootstrapScript();
        EventTitles();
        PrefsRoundTrip();
        SecretRoundTrip();
        DialogsConstruct();
        RealCulturesAvailable();

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"selftest OK — {_passed} assertions passed");
            return true;
        }
        Console.WriteLine($"selftest FAILED — {Failures.Count} of {_passed + Failures.Count} assertions:");
        foreach (var f in Failures) Console.WriteLine("  " + f);
        return false;
    }

    private static void Assert(bool condition, string what)
    {
        if (condition) _passed++;
        else Failures.Add(what);
    }

    private static void Section(string name) => Console.WriteLine($"-- {name}");

    /// <summary>The build must NOT be globalization-invariant: WinForms resolves a
    /// real culture on every keyboard-layout switch (WM_INPUTLANGCHANGE), and in
    /// invariant mode that lookup throws inside WndProc and takes the app down —
    /// seen live with an en-CA layout (LCID 0x1009). The csproj must never get
    /// the server's InvariantGlobalization flag; this is the tripwire.</summary>
    private static void RealCulturesAvailable()
    {
        Section("real cultures (input-language switch survival)");
        try
        {
            var c = System.Globalization.CultureInfo.GetCultureInfo(0x1009);
            Assert(c.Name.Length > 0, "LCID 0x1009 resolves to a real culture");
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            Assert(false, "globalization-invariant build: a keyboard-layout switch would crash the app");
        }
    }

    // ---- settings ----------------------------------------------------------

    private static void UrlNormalization()
    {
        Section("url normalization");
        Assert(DesktopSettings.NormalizeUrl("10.1.0.60:8000") == "http://10.1.0.60:8000",
            "a bare host:port becomes an http URL");
        Assert(DesktopSettings.NormalizeUrl("  neolink.lan  ") == "http://neolink.lan",
            "surrounding space is trimmed");
        Assert(DesktopSettings.NormalizeUrl("https://cams.example.com/") == "https://cams.example.com",
            "a trailing slash is dropped");
        Assert(DesktopSettings.NormalizeUrl("https://host/neolink/") == "https://host/neolink",
            "a proxy base path survives");
        Assert(DesktopSettings.NormalizeUrl("http://host:8000/?x=1") == "http://host:8000",
            "a query string is dropped");
        Assert(DesktopSettings.NormalizeUrl("") == null, "empty text has no reading");
        Assert(DesktopSettings.NormalizeUrl("   ") == null, "blank text has no reading");
        Assert(DesktopSettings.NormalizeUrl("ftp://host") == null, "a non-web scheme is refused");
        Assert(DesktopSettings.NormalizeUrl("rtsp://host:8654/cam") == null, "the RTSP port is not the web UI");
    }

    private static void QuietHours()
    {
        Section("quiet hours");
        var s = new DesktopSettings { QuietFrom = "22:00", QuietTo = "07:00" };
        var day = new DateTime(2026, 7, 28);
        Assert(s.InQuietHours(day.AddHours(23)), "23:00 is inside 22:00-07:00");
        Assert(s.InQuietHours(day.AddHours(3)), "03:00 is inside a window that wraps midnight");
        Assert(s.InQuietHours(day.AddHours(22)), "the start of the window is inside it");
        Assert(!s.InQuietHours(day.AddHours(7)), "the end of the window is outside it");
        Assert(!s.InQuietHours(day.AddHours(12)), "midday is outside 22:00-07:00");

        var daytime = new DesktopSettings { QuietFrom = "13:00", QuietTo = "17:00" };
        Assert(daytime.InQuietHours(day.AddHours(15)), "15:00 is inside 13:00-17:00");
        Assert(!daytime.InQuietHours(day.AddHours(2)), "02:00 is outside 13:00-17:00");

        Assert(!new DesktopSettings().InQuietHours(day.AddHours(3)),
            "no quiet hours configured means never quiet");
        Assert(!new DesktopSettings { QuietFrom = "nonsense", QuietTo = "07:00" }.InQuietHours(day.AddHours(3)),
            "an unparseable time must not silence alerts forever");
        Assert(!new DesktopSettings { QuietFrom = "08:00", QuietTo = "08:00" }.InQuietHours(day.AddHours(8)),
            "an empty window is not a whole day");

        Assert(AlertRules.QuietCanSuppress(AlertKind.Detection, quietSilencesSystem: false),
            "quiet hours always cover detections");
        Assert(!AlertRules.QuietCanSuppress(AlertKind.ServerCondition, quietSilencesSystem: false),
            "a disk filling up at 3am still gets through by default");
        Assert(AlertRules.QuietCanSuppress(AlertKind.CameraOffline, quietSilencesSystem: true),
            "faults go quiet too when the user asks for that");
    }

    // ---- alert rules -------------------------------------------------------

    private static ApiEvent Event(string id, string camera, DateTime start, params string[] labels) =>
        new(id, camera, start, start.AddSeconds(20), labels.ToList(), false, false, HasThumb: true);

    private static AlertPrefs FrontDoorPerson() => new()
    {
        Enabled = true,
        CooldownSeconds = 60,
        Cameras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FrontDoor"] = new() { "person" },
        },
    };

    private static void DetectionAlerts()
    {
        Section("detection alerts");
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var prefs = FrontDoorPerson();
        var rules = new AlertRules();

        // First poll is history.
        var seeded = rules.Evaluate(prefs, new[] { Event("e1", "FrontDoor", now.AddSeconds(-30), "person") },
            null, null, now);
        Assert(seeded.Count == 0, "the first poll seeds silently instead of firing history");
        Assert(rules.Seeded, "the baseline is established after one poll");

        // A genuinely new event fires.
        var fired = rules.Evaluate(prefs,
            new[] { Event("e1", "FrontDoor", now.AddSeconds(-30), "person"),
                    Event("e2", "FrontDoor", now.AddSeconds(-5), "person") },
            null, null, now);
        Assert(fired.Count == 1 && fired[0].Tag == "e2", "a new matching event raises exactly one alert");
        Assert(fired[0].Title == "Human detected", "the title matches the web UI's wording");
        Assert(fired[0].DeepLink == "/events?event=e2", "the alert deep-links to the event");
        Assert(fired[0].ThumbPath == "/api/events/e2/thumb", "an event with a thumbnail offers its picture");

        // Cooldown holds the same camera+label combination.
        var soon = rules.Evaluate(prefs,
            new[] { Event("e3", "FrontDoor", now.AddSeconds(-1), "person") }, null, null, now.AddSeconds(30));
        Assert(soon.Count == 0, "the cooldown suppresses a repeat within 60s");
        var later = rules.Evaluate(prefs,
            new[] { Event("e4", "FrontDoor", now.AddSeconds(59), "person") }, null, null, now.AddSeconds(61));
        Assert(later.Count == 1, "the same combination alerts again once the cooldown expires");

        // A camera with no rule, and a label the rule does not want.
        var other = new AlertRules();
        other.Evaluate(prefs, Array.Empty<ApiEvent>(), null, null, now);
        var ignored = other.Evaluate(prefs,
            new[] { Event("x1", "Driveway", now, "person"), Event("x2", "FrontDoor", now, "vehicle") },
            null, null, now);
        Assert(ignored.Count == 0, "a camera without a rule and an unwanted label both stay silent");

        // Firmware dialects fold into the chip vocabulary.
        var dialect = new AlertRules();
        var crossPrefs = new AlertPrefs
        {
            Enabled = true,
            Cameras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Gate"] = new() { "line-crossing" },
            },
        };
        dialect.Evaluate(crossPrefs, Array.Empty<ApiEvent>(), null, null, now);
        var crossed = dialect.Evaluate(crossPrefs, new[] { Event("c1", "Gate", now, "crossline") }, null, null, now);
        Assert(crossed.Count == 1, "\"crossline\" from the firmware matches a line-crossing rule");

        // An unlabelled event is plain motion.
        var motion = new AlertRules();
        var motionPrefs = new AlertPrefs
        {
            Enabled = true,
            Cameras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Shed"] = new() { "motion" },
            },
        };
        motion.Evaluate(motionPrefs, Array.Empty<ApiEvent>(), null, null, now);
        Assert(motion.Evaluate(motionPrefs, new[] { Event("m1", "Shed", now) }, null, null, now).Count == 1,
            "an event with no labels counts as motion");

        // Catch-up after a laptop wakes must not fire an hour of backlog.
        var stale = new AlertRules();
        stale.Evaluate(prefs, Array.Empty<ApiEvent>(), null, null, now);
        Assert(stale.Evaluate(prefs, new[] { Event("s1", "FrontDoor", now.AddMinutes(-30), "person") },
            null, null, now).Count == 0, "an event older than the age limit is catch-up, not news");

        // The account switch being off silences everything but still advances state.
        var off = new AlertRules();
        var offPrefs = FrontDoorPerson();
        offPrefs.Enabled = false;
        off.Evaluate(offPrefs, Array.Empty<ApiEvent>(), null, null, now);
        Assert(off.Evaluate(offPrefs, new[] { Event("o1", "FrontDoor", now, "person") }, null, null, now).Count == 0,
            "the account switch off means no alerts");
        offPrefs.Enabled = true;
        Assert(off.Evaluate(offPrefs, new[] { Event("o1", "FrontDoor", now, "person") }, null, null, now).Count == 0,
            "an event seen while switched off does not fire when it is switched back on");

        // Several new events arrive oldest-first.
        var burst = new AlertRules();
        var burstPrefs = FrontDoorPerson();
        burstPrefs.CooldownSeconds = 0;
        burst.Evaluate(burstPrefs, Array.Empty<ApiEvent>(), null, null, now);
        var ordered = burst.Evaluate(burstPrefs, new[]
        {
            Event("b2", "FrontDoor", now.AddSeconds(-10), "person"),
            Event("b1", "FrontDoor", now.AddSeconds(-40), "person"),
        }, null, null, now);
        Assert(ordered.Count == 2 && ordered[0].Tag == "b1", "a burst notifies in the order it happened");

        // Reset drops the baseline.
        burst.Reset();
        Assert(!burst.Seeded, "Reset drops the baseline so the next poll seeds again");
    }

    private static void OfflineAlerts()
    {
        Section("camera offline alerts");
        var prefs = new AlertPrefs { Enabled = true, Offline = { "Driveway" } };
        var rules = new AlertRules();
        var now = DateTime.UtcNow;

        var online = new[] { new ApiCamera("Driveway", Online: true) };
        var down = new[] { new ApiCamera("Driveway") };

        Assert(rules.Evaluate(prefs, null, online, null, now).Count == 0, "the first camera poll is silent");
        Assert(rules.Evaluate(prefs, null, down, null, now).Count == 0,
            "one bad poll is a blip, not an outage");
        var alert = rules.Evaluate(prefs, null, down, null, now);
        Assert(alert.Count == 1 && alert[0].Title == "Driveway offline",
            "a sustained outage alerts on the second consecutive poll");
        Assert(alert[0].Kind == AlertKind.CameraOffline, "an outage is a fault, not a detection");
        Assert(rules.Evaluate(prefs, null, down, null, now).Count == 0,
            "an outage alerts once, not on every poll");
        var back = rules.Evaluate(prefs, null, online, null, now);
        Assert(back.Count == 1 && back[0].Title == "Driveway back online", "recovery alerts too");

        // A dozing battery camera and a suspended one are off on purpose.
        var sleeping = new AlertRules();
        var doze = new[] { new ApiCamera("Argus", Asleep: true) };
        var prefsArgus = new AlertPrefs { Enabled = true, Offline = { "Argus" } };
        sleeping.Evaluate(prefsArgus, null, doze, null, now);
        sleeping.Evaluate(prefsArgus, null, doze, null, now);
        Assert(sleeping.Evaluate(prefsArgus, null, doze, null, now).Count == 0,
            "a dozing battery camera is not an outage");

        var suspendedRules = new AlertRules();
        var suspended = new[] { new ApiCamera("Old", Suspended: true) };
        var prefsOld = new AlertPrefs { Enabled = true, Offline = { "Old" } };
        suspendedRules.Evaluate(prefsOld, null, suspended, null, now);
        suspendedRules.Evaluate(prefsOld, null, suspended, null, now);
        Assert(suspendedRules.Evaluate(prefsOld, null, suspended, null, now).Count == 0,
            "a suspended camera is not an outage");

        // A camera already down at startup is remembered, not announced.
        var late = new AlertRules();
        var latePrefs = new AlertPrefs { Enabled = true, Offline = { "Driveway" } };
        late.Evaluate(latePrefs, null, down, null, now);
        Assert(late.Evaluate(latePrefs, null, down, null, now).Count == 0,
            "a camera that was already down when the app started does not alert");
        Assert(late.Evaluate(latePrefs, null, online, null, now).Count == 1,
            "...but it does say so when it comes back");

        // A camera with no offline rule stays silent.
        var unwatched = new AlertRules();
        var noRule = new AlertPrefs { Enabled = true };
        unwatched.Evaluate(noRule, null, online, null, now);
        unwatched.Evaluate(noRule, null, down, null, now);
        Assert(unwatched.Evaluate(noRule, null, down, null, now).Count == 0,
            "a camera nobody asked about does not report its outage");
    }

    private static void ServerConditionAlerts()
    {
        Section("server condition alerts");
        var prefs = new AlertPrefs { Enabled = true };
        var rules = new AlertRules();
        var now = DateTime.UtcNow;

        var healthy = new ApiFeatures(Storage: new ApiStorage("SSD", 40));
        var full = new ApiFeatures(Storage: new ApiStorage("SSD", 100, Full: true));

        Assert(rules.Evaluate(prefs, null, null, healthy, now).Count == 0, "the first features poll is silent");
        var alert = rules.Evaluate(prefs, null, null, full, now);
        Assert(alert.Count == 1 && alert[0].Title == "Storage full", "storage filling up alerts on the edge");
        Assert(rules.Evaluate(prefs, null, null, full, now).Count == 0, "it does not repeat while it stays full");
        Assert(rules.Evaluate(prefs, null, null, healthy, now)[0].Title == "Storage recovered",
            "recovery alerts on the way back down");

        var overload = new AlertRules();
        overload.Evaluate(prefs, null, null, healthy, now);
        Assert(overload.Evaluate(prefs, null, null, new ApiFeatures(Overload: true), now)[0].Title == "Server overloaded",
            "sustained CPU load alerts");

        var writes = new AlertRules();
        writes.Evaluate(prefs, null, null, healthy, now);
        Assert(writes.Evaluate(prefs, null, null, new ApiFeatures(WriteFailure: true), now)[0].Title
                   == "Recording write failures", "failing disk writes alert");

        // Each condition can be switched off on its own.
        var muted = new AlertRules();
        var mutedPrefs = new AlertPrefs { Enabled = true, SysStorage = false };
        muted.Evaluate(mutedPrefs, null, null, healthy, now);
        Assert(muted.Evaluate(mutedPrefs, null, null, full, now).Count == 0,
            "an unwanted server alert stays off");
    }

    // ---- presentation ------------------------------------------------------

    private static void ToastPayload()
    {
        Section("toast payload");
        Assert(Toaster.SanitizeTag("evt-2026-07-28_12:00:00") == "evt-2026-07-28_120000",
            "a tag keeps only characters the shell accepts");
        Assert(Toaster.SanitizeTag("!!!") == "neolink", "a tag that sanitizes to nothing gets a fallback");
        Assert(Toaster.SanitizeTag(new string('a', 100)).Length == 64, "a long tag is capped at 64 characters");

        var alert = new Alert(AlertKind.Detection, "e1", "Human & Vehicle detected",
            "FrontDoor <test>", DeepLink: "/events?event=e1");
        var loud = Toaster.BuildToastXml(alert, new DesktopSettings { Sound = true, ClickOpensEvent = true }, null);
        Assert(loud.Contains("Human &amp; Vehicle detected"), "the title is XML-escaped");
        Assert(loud.Contains("FrontDoor &lt;test&gt;"), "the body is XML-escaped");
        Assert(loud.Contains("launch=\"neolink-desktop:/events?event=e1\""),
            "the deep link rides the toast as a protocol URI");
        Assert(loud.Contains("activationType=\"protocol\""),
            "click-to-open toasts use protocol activation — the only kind an Action Center click can deliver");
        Assert(loud.Contains("ms-winsoundevent"), "sound on means a sound is named");

        var quiet = Toaster.BuildToastXml(alert, new DesktopSettings { Sound = false, ClickOpensEvent = false }, null);
        Assert(quiet.Contains("silent=\"true\""), "sound off means a silent toast");
        Assert(!quiet.Contains("launch="), "click-to-open off means no launch target");
        Assert(quiet.Contains("activationType=\"foreground\""),
            "a linkless toast stays a plain foreground one");

        // The protocol handler is invokable by anything on the machine, so the
        // payload is held to one in-app path; everything else opens the dashboard.
        Assert(ProtocolLink.Sanitize("/events?event=Front~2026-07-31~abcd") == "/events?event=Front~2026-07-31~abcd",
            "a real event link passes through untouched");
        Assert(ProtocolLink.Sanitize("https://evil.example/x") == "/", "an absolute URL is refused");
        Assert(ProtocolLink.Sanitize("//evil.example/x") == "/", "a protocol-relative URL is refused");
        Assert(ProtocolLink.Sanitize(@"/..\..\x") == "/", "backslashes are refused");
        Assert(ProtocolLink.Sanitize("") == "/", "an empty payload opens the dashboard");
        Assert(ProtocolLink.FromArgs(new[] { "neolink-desktop:/events?event=e1" }) == "/events?event=e1",
            "a protocol launch yields its deep link");
        Assert(ProtocolLink.FromArgs(new[] { "--minimized" }) == null,
            "an ordinary launch carries no deep link");

        // Navigating a raw launch string lands in the system browser, so a click's
        // arguments must reduce to an in-app path.
        Assert(ProtocolLink.FromToastArguments("neolink-desktop:/events?event=e1") == "/events?event=e1",
            "a protocol toast click reduces to its in-app path");
        Assert(ProtocolLink.FromToastArguments("/events?event=e1") == "/events?event=e1",
            "an old foreground toast's plain path passes through");
        Assert(ProtocolLink.FromToastArguments("neolink-desktop:https://evil.example/x") == "/",
            "a protocol click smuggling an absolute URL opens the dashboard");
        Assert(ProtocolLink.FromToastArguments("") == null && ProtocolLink.FromToastArguments(null) == null,
            "an argumentless click carries no link");
        Assert(ProtocolLink.FromToastArguments("garbage") == null,
            "non-path arguments carry no link");

        var withImage = Toaster.BuildToastXml(alert,
            new DesktopSettings { ShowThumbnail = true }, @"C:\temp\thumb one.jpg");
        Assert(withImage.Contains("file:///C:/temp/thumb%20one.jpg"),
            "a local thumbnail path becomes a file URI");
        Assert(!Toaster.BuildToastXml(alert, new DesktopSettings { ShowThumbnail = false }, @"C:\t.jpg")
            .Contains("<image"), "thumbnails off means no image element");
    }

    private static void BootstrapScript()
    {
        Section("webview bootstrap");
        const string origin = "http://10.1.0.60:8655";
        var withToken = MainForm.BuildBootstrapScript("abc123", origin, pauseVideoWhenHidden: true);
        Assert(withToken.Contains("neolink.auth"), "the session is seeded into the web UI's own storage");
        Assert(withToken.Contains("abc123"), "the token is the one the shell holds");
        Assert(withToken.Contains("getRegistration"),
            "the service worker is hidden so notifications take the interceptable path");
        Assert(!MainForm.BuildBootstrapScript(null, origin, true).Contains("neolink.auth"),
            "no token means nothing is seeded");
        Assert(withToken.Contains("__neolinkShellPauseHidden = true"),
            "pause-when-hidden ON reaches the page before any of its scripts run");
        Assert(MainForm.BuildBootstrapScript(null, origin, pauseVideoWhenHidden: false)
            .Contains("__neolinkShellPauseHidden = false"),
            "pause-when-hidden OFF is stated, not merely absent — the page must not guess");

        // The origin guard is the security boundary: the token-seeding script runs
        // on every document, so it must refuse to run anywhere but the server.
        Assert(withToken.Contains($"location.origin !== \"{origin}\""),
            "the script refuses to run on any origin but the server's");
        int guard = withToken.IndexOf("location.origin", StringComparison.Ordinal);
        int seed = withToken.IndexOf("neolink.auth", StringComparison.Ordinal);
        Assert(guard >= 0 && guard < seed, "the origin check comes before the token touches storage");

        Assert(MainForm.DeepLinkForTag("evt1") == "/events?event=evt1", "a detection tag deep-links to its event");
        Assert(MainForm.DeepLinkForTag("sys-storage") == "/", "a server-condition tag opens the dashboard");
        Assert(MainForm.DeepLinkForTag("offline-Driveway") == "/", "an outage tag opens the dashboard");
    }

    private static void EventTitles()
    {
        Section("event titles");
        var now = DateTime.UtcNow;
        Assert(Event("a", "Cam", now, "person").Title == "Human detected", "person reads as Human");
        Assert(Event("a", "Cam", now, "person", "vehicle").Title == "Human + Vehicle detected",
            "two labels join with a plus");
        Assert(Event("a", "Cam", now, "doorbell").Title == "Doorbell pressed",
            "a lone doorbell event is a button press");
        Assert(Event("a", "Cam", now, "external").Title == "Externally triggered",
            "a lone external event was commanded, not detected");
        Assert(Event("a", "Cam", now).Title == "Motion detected", "no labels reads as motion");
    }

    // ---- persistence -------------------------------------------------------

    private static void PrefsRoundTrip()
    {
        Section("alert prefs");
        var prefs = FrontDoorPerson();
        prefs.Offline.Add("Driveway");

        // The web UI writes PascalCase with the default policy; the desktop app
        // has to produce the same bytes or the two clients cannot read each other.
        var json = JsonSerializer.Serialize(prefs);
        Assert(json.Contains("\"Enabled\""), "the blob uses the web UI's PascalCase names");
        Assert(json.Contains("\"CooldownSeconds\""), "cooldown carries its web UI name");

        var back = JsonSerializer.Deserialize<AlertPrefs>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert(back.Enabled && back.CooldownSeconds == 60, "the web UI's case-insensitive read gets it back");
        Assert(back.WantsLabel("frontdoor", "PERSON"), "camera and label matching ignores case");
        Assert(back.WantsOffline("DRIVEWAY"), "the offline list ignores case");

        // A blob written by an older version, missing the newer fields.
        var old = JsonSerializer.Deserialize<AlertPrefs>(
            """{"Enabled":true,"Cameras":{"Cam":["person"]}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert(old.SysStorage && old.SysOverload && old.SysWriteFailure,
            "fields an older blob omits keep their defaults");
        Assert(old.CooldownSeconds == 60, "a missing cooldown keeps the default");

        var clone = prefs.Clone();
        clone.Cameras["FrontDoor"].Add("vehicle");
        clone.Offline.Clear();
        Assert(prefs.Cameras["FrontDoor"].Count == 1 && prefs.Offline.Count == 1,
            "Clone is deep enough that editing the copy cannot touch the original");
    }

    /// <summary>
    /// Builds both dialogs and throws them away. They are laid out in code rather
    /// than a designer and are only reachable from the tray menu, so a bad
    /// constructor would otherwise sit undiscovered until a user went looking for
    /// their notification settings. This does not check that the layout LOOKS
    /// right - only that opening it cannot crash the app.
    /// </summary>
    private static void DialogsConstruct()
    {
        Section("dialogs");
        var settings = new DesktopSettings { ServerUrl = "http://127.0.0.1:1", QuietFrom = "22:00", QuietTo = "07:00" };
        try
        {
            using var connect = new ConnectForm(settings, reconfiguring: false);
            Assert(connect.Text.Length > 0, "the connect dialog builds");

            using var servers = new ServersForm(settings);
            Assert(servers.Text.Length > 0, "the servers dialog builds");
            Assert(settings.Servers.Count == 1 && settings.Servers[0].Url == settings.ServerUrl,
                "opening the manager seeds the saved list with the active server");

            using var link = new ServerLink(settings, persistLogin: false);
            using var tray = new NotifyIcon();
            using var toaster = new Toaster(tray, Application.ExecutablePath, ensureShortcut: false);
            using var engine = new AlertEngine(link, settings, toaster);
            // Rules with a camera the server will never return: the window has to
            // keep a rule whose camera is gone rather than quietly dropping it.
            engine.Prefs.Cameras["GoneCamera"] = new List<string> { "person" };
            engine.Prefs.Offline.Add("GoneCamera");
            using var notifications = new NotificationsForm(settings, engine, toaster, link);
            Assert(notifications.Text.Length > 0, "the notifications dialog builds");
            Assert(notifications.Controls.Count > 0, "the notifications dialog has its layout");
        }
        catch (Exception ex)
        {
            Failures.Add($"a dialog threw while constructing: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SecretRoundTrip()
    {
        Section("secret storage");
        const string secret = "a password with spaces and \u00fcmlauts";
        var blob = Dpapi.Protect(secret);
        Assert(blob != null && blob != secret, "the stored form is not the plaintext");
        Assert(Dpapi.Unprotect(blob) == secret, "protect and unprotect round-trip");
        Assert(Dpapi.Protect(null) == null && Dpapi.Protect("") == null, "nothing in, nothing out");
        Assert(Dpapi.Unprotect("not base64 at all") == null, "junk decrypts to null rather than throwing");
        Assert(Dpapi.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })) == null,
            "a blob from another machine decrypts to null rather than throwing");

        var settings = new DesktopSettings { Password = "hunter2", Token = "tok" };
        Assert(settings.ProtectedPassword != "hunter2", "the settings file never holds the plaintext password");
        Assert(settings.Password == "hunter2" && settings.Token == "tok", "the accessors round-trip");
        settings.Password = null;
        Assert(settings.ProtectedPassword == null, "clearing the password clears the stored blob");
    }
}
