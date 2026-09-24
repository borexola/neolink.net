// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Xml.Linq;

namespace Neolink.Protocol;

/// <summary>One thing a camera reported over ONVIF events: the raw topic, its state (null = a one-shot),
/// the Source block (which channel or rule), the PropertyOperation, and the subject one rule reports on.</summary>
public sealed record OnvifNotification(string Topic, bool? Active,
    IReadOnlyDictionary<string, string> Items,
    IReadOnlyDictionary<string, string>? Source = null,
    string? Operation = null,
    string? Subject = null)
{
    /// <summary>Whether this is a DETECTION rather than housekeeping. ONVIF cameras
    /// publish a great deal that is not motion — recording state, storage, network
    /// changes, clock sync — and a subscription without a filter receives all of it.</summary>
    public bool IsDetection => Labels.Count > 0;

    /// <summary>The camera withdrew this property (a rule was deleted or
    /// reconfigured). Whatever it reported is over, but it is not a detection.</summary>
    public bool Deleted => string.Equals(Operation, "Deleted", StringComparison.OrdinalIgnoreCase);

    /// <summary>The primary label, for callers that want one; null when the topic
    /// is not a detection at all. <see cref="Labels"/> carries the full set.</summary>
    public string? Label => Labels.Count == 0 ? null : Labels[0];

    private IReadOnlyList<string>? _labels;

    /// <summary>The labels this maps onto in Neolink's vocabulary, empty when not a
    /// detection; a perimeter rule also yields "motion" so the default filter records it.</summary>
    public IReadOnlyList<string> Labels => _labels ??= Classify(Topic, Items);

    /// <summary>Identifies the speaker (topic, Source items, subject), so a start and its
    /// end are matched to each other and never to another rule's, channel's or object's.</summary>
    public string Key => Subject == null ? Speaker : Speaker + "|" + Subject;

    /// <summary>The rule on its channel, without the subject: what a Deleted message withdraws.</summary>
    public string Speaker
    {
        get
        {
            if (Source is not { Count: > 0 } src) return Topic;
            var parts = src.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + "=" + kv.Value);
            return Topic + "|" + string.Join(";", parts);
        }
    }

    /// <summary>A state item named for a class ("IsVehicle", "IsPet", "IsPackageDeliver"):
    /// Tapo reports each class with its own state on one topic.</summary>
    internal static bool IsClassItem(string name)
    {
        if (name.Length < 3 || !name.StartsWith("Is", StringComparison.OrdinalIgnoreCase)) return false;
        var what = name[2..];
        return Is(what, PersonWords) || Is(what, VehicleWords) || Is(what, AnimalWords) || Is(what, FaceWords)
               || what.StartsWith("package", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The video source the Source block names, or null; a multi-channel
    /// device sends every channel's events down one subscription.</summary>
    public string? SourceToken =>
        Source == null ? null
        : Source.TryGetValue("VideoSourceConfigurationToken", out var c) && c.Length > 0 ? c
        : Source.TryGetValue("VideoSourceToken", out var s) && s.Length > 0 ? s
        : Source.TryGetValue("VideoSource", out var v) && v.Length > 0 ? v
        : null;

    // ------------------------------------------------------------ classification

    private static readonly string[] PersonWords = { "human", "person", "people", "pedestrian" };
    private static readonly string[] VehicleWords =
        { "vehicle", "vehical", "car", "truck", "bus", "motorcycle", "motorbike", "bike", "bicycle" };
    private static readonly string[] AnimalWords = { "animal", "dog", "cat", "pet", "dogcat", "dog_cat" };
    private static readonly string[] FaceWords = { "face", "humanface" };

    /// <summary>Topic families about the picture; only these are read as detections, so
    /// a "Type" item on a storage notification cannot become a vehicle.</summary>
    private static bool IsAnalyticsTopic(string topic) =>
        Contains(topic, "RuleEngine", "VideoAnalytics", "Analytics", "Detect", "MotionAlarm",
            "Motion", "VMD", "IVA", "SmartEvent", "LineCross", "CrossRegion", "Intrusion", "Tripwire");

    internal static IReadOnlyList<string> Classify(string topic, IReadOnlyDictionary<string, string> items)
    {
        if (!IsAnalyticsTopic(topic)) return Array.Empty<string>();
        // Tamper is an alarm, not a detection — as "motion" it would open a recording
        // that lasts until the lens is uncovered. A counter reports a number, not a sighting.
        if (Segment(topic, "Tamper") || (items.TryGetValue("IsTamper", out var tamper) && IsTrue(tamper)))
            return Array.Empty<string>();
        if (Segment(topic, "Counter") || Segment(topic, "Count") || Segment(topic, "Counting"))
            return Array.Empty<string>();

        var labels = new List<string>();
        void Add(string l) { if (!labels.Contains(l)) labels.Add(l); }

        // ClassTypes is a space-separated list, ObjectType one word: each word is mapped alone.
        foreach (var name in new[] { "ObjectType", "ClassTypes", "ClassType", "Classification", "Type" })
        {
            if (!items.TryGetValue(name, out var value) || value.Length == 0) continue;
            foreach (var word in value.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Is(word, PersonWords)) Add("person");
                else if (Is(word, VehicleWords)) Add("vehicle");
                else if (Is(word, AnimalWords)) Add("animal");
                else if (Is(word, FaceWords)) Add("face");
            }
        }
        // Some vendors fold the class into the state item's name (IsVehicle, IsPet):
        // each one that is true names what was seen.
        foreach (var (name, value) in items)
        {
            if (!name.StartsWith("Is", StringComparison.OrdinalIgnoreCase) || !IsTrue(value)) continue;
            var what = name[2..];
            if (Is(what, PersonWords)) Add("person");
            else if (Is(what, VehicleWords)) Add("vehicle");
            else if (Is(what, AnimalWords)) Add("animal");
            else if (Is(what, FaceWords)) Add("face");
            else if (what.StartsWith("package", StringComparison.OrdinalIgnoreCase)) Add("package"); // Tapo: IsPackageDeliver/-Pickup
        }
        // The topic's own words, matched at a word boundary so that "Interface"
        // is not a face.
        if (Segment(topic, "Face")) Add("face");
        if (Segment(topic, "People") || Segment(topic, "Human") || Segment(topic, "Person")
            || Segment(topic, "Pedestrian")) Add("person");
        if (Segment(topic, "Vehicle") || Segment(topic, "Car")) Add("vehicle");
        if (Segment(topic, "DogCat") || Segment(topic, "Animal") || Segment(topic, "Pet")) Add("animal");
        if (Segment(topic, "Package")) Add("package");
        if (Segment(topic, "Visitor")) Add("visitor"); // Reolink's doorbell press (MyRuleDetector/Visitor)
        // Perimeter rules: their own label, and motion beside it (see Labels).
        bool perimeter = false;
        if (Contains(topic, "LineDetector", "LineCross", "Crossed", "Tripwire"))
        {
            Add("line-crossing"); perimeter = true;
        }
        // Bosch IVA names its field rule tns1:IVA/EnteringField/<user's rule name>.
        if (Contains(topic, "FieldDetector", "ObjectsInside", "Intrusion", "CrossRegion", "EnteringField"))
        {
            Add("intrusion"); perimeter = true;
        }
        if (Contains(topic, "Loitering"))
        {
            Add("loitering"); perimeter = true;
        }
        if (perimeter) Add("motion");
        if (labels.Count > 0) return labels;
        // Everything else the camera was told to care about is motion.
        if (Contains(topic, "CellMotionDetector", "MotionRegionDetector", "MotionAlarm", "MotionDetect",
                "VideoMotion", "VMD", "ObjectDetection", "SmartEvent")
            || Segment(topic, "Motion"))
            return new[] { "motion" };
        return Array.Empty<string>();
    }

    private static bool Contains(string topic, params string[] needles) =>
        needles.Any(n => topic.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="word"/> appears in the topic at a word or
    /// CamelCase boundary: "FaceDetect" matches "Face", "Interface" does not.</summary>
    internal static bool Segment(string topic, string word)
    {
        int from = 0;
        while (true)
        {
            int i = topic.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool starts = i == 0 || !char.IsLetter(topic[i - 1])
                          || (char.IsLower(topic[i - 1]) && char.IsUpper(topic[i]));
            int end = i + word.Length;
            bool ends = end == topic.Length || !char.IsLower(topic[end])
                        || (topic[end] == 's' && (end + 1 == topic.Length || !char.IsLower(topic[end + 1])));
            if (starts && ends) return true;
            from = i + 1;
        }
    }

    private static bool Is(string value, string[] options) =>
        options.Any(o => string.Equals(value.Trim(), o, StringComparison.OrdinalIgnoreCase));

    private static bool IsTrue(string value) => ParseBool(value) == true;

    /// <summary>xs:boolean, which is "true"/"false" OR "1"/"0" — cameras use both.</summary>
    internal static bool? ParseBool(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }
}

/// <summary>Where a pull-point subscription is addressed: the manager's URL, and the
/// reference parameters, as SOAP header blocks, that every request must carry back.</summary>
public sealed record PullPointSubscription(string Address, string Headers)
{
    public override string ToString() => Address;
}

/// <summary>The outcome of asking for a pull-point subscription. NoEventService =
/// a lasting answer (the camera has none, or ONVIF is not reachable at all), worth
/// re-checking only rarely. Otherwise a null Subscription is a refusal of THIS
/// attempt, worth retrying soon; Reason says why, for the one line that reports it.</summary>
public sealed record PullPointResult(PullPointSubscription? Subscription, bool NoEventService, string? Reason,
    TimeSpan Granted = default)
{
    /// <summary>The subscription manager's address, when one was created.</summary>
    public string? Address => Subscription?.Address;
}

public sealed partial class OnvifClient
{
    // WS-Addressing actions. A subscription manager is addressed by these, not by
    // the URL alone, and strict implementations fault without them.
    private const string ActCreate = "http://www.onvif.org/ver10/events/wsdl/EventPortType/CreatePullPointSubscriptionRequest";
    private const string ActPull = "http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest";
    private const string ActRenew = "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/RenewRequest";
    private const string ActUnsubscribe = "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest";

    private string? _eventsUrl;

    /// <summary>How often a camera that advertised no event service is asked for its
    /// service table again, in case events were switched on in its own web page.</summary>
    private static readonly TimeSpan ServiceRecheck = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Opens a pull-point subscription and returns the address of the subscription
    /// manager the camera created for it, or null when the camera has no event
    /// service (or refused). The address is where PullMessages, Renew and
    /// Unsubscribe are then sent — it is NOT the event service's own URL, and some
    /// cameras hand back a completely different host and port for it.
    ///
    /// No topic filter is sent. A filter would cut the traffic down, but the dialect
    /// for expressing one is where implementations differ most, and a camera that
    /// dislikes the filter refuses the whole subscription — so everything is
    /// received and the uninteresting majority is dropped here instead.
    /// </summary>
    public async Task<PullPointResult> CreatePullPointAsync(TimeSpan termination, CancellationToken ct)
    {
        // Discovery touches shared state (the service URLs, the clock skew), and the
        // settings calls run it under the gate — so this does too, rather than racing
        // them. Only the create itself is inside it: the long poll that follows runs
        // without the gate, or it would hold every settings call up for 20 seconds.
        string? eventsUrl;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Not reachable RIGHT NOW is not the same as "has no event service": it
            // covers a camera mid-reboot and discovery's own cooldown, both of which
            // pass. Reported as a transient refusal, so the caller retries on its
            // ordinary backoff — cheap, since discovery answers instantly while it is
            // cooling down — rather than parking detections for five minutes.
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false))
                return new PullPointResult(null, NoEventService: false, "ONVIF is not reachable just now");
            // Discovery is otherwise permanent for the run; see ServiceRecheck.
            if (_eventsUrl == null && DateTime.UtcNow - _servicesReadAt > ServiceRecheck)
                await ReadServiceTableAsync(ct, ct).ConfigureAwait(false);
            eventsUrl = _eventsUrl;
        }
        finally { _gate.Release(); }
        if (eventsUrl == null)
            return new PullPointResult(null, NoEventService: true, "the camera advertises no event service");

        var xml = await CallAsync(eventsUrl, NsEvents, "CreatePullPointSubscription",
            $"<tev:InitialTerminationTime>{TerminationValue(termination)}</tev:InitialTerminationTime>", ct,
            null, ActCreate).ConfigureAwait(false);
        if (xml == null)
        {
            // Refused in absolute time: the camera's clock may have stepped, so durations get another go.
            _absoluteTermination = false;
            return new PullPointResult(null, NoEventService: false, _lastError);
        }
        var address = SubscriptionAddress(xml);
        if (address == null)
            return new PullPointResult(null, NoEventService: false, "the reply carried no subscription address");
        var subscription = new PullPointSubscription(NormalizeXAddr(address)!, ReferenceParameterHeaders(xml));
        var granted = GrantedPeriod(xml, DateTime.UtcNow + _clockSkew);
        // Far less than asked may mean the camera misread the duration: an absolute time in its own
        // clock is tried (onvif-zeep-async's fix) and kept only if it granted more, so a camera that
        // merely caps subscriptions keeps getting durations, which do not depend on the clock skew.
        if (!_absoluteTermination && granted is { } g && g < termination / 2)
        {
            _absoluteTermination = true;
            var (renewed, _) = await RenewSubscriptionAsync(subscription, termination, ct).ConfigureAwait(false);
            if (renewed is { } r && r > g)
            {
                Log.Debug($"{_tag}: the camera granted an event subscription {g.TotalSeconds:0}s of " +
                          $"{termination.TotalSeconds:0}s, and {r.TotalSeconds:0}s asked in absolute time — using that");
                granted = r;
            }
            else _absoluteTermination = false;
        }
        return new PullPointResult(subscription, NoEventService: false, null, granted ?? termination);
    }

    private bool _absoluteTermination;

    /// <summary>A termination time as this camera takes it: a duration, or an absolute time
    /// in its own clock for one that misreads durations.</summary>
    private string TerminationValue(TimeSpan period) => !_absoluteTermination ? Duration(period)
        : (DateTime.UtcNow + _clockSkew + period).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>How long the camera grants a subscription: TerminationTime minus its CurrentTime, or minus
    /// <paramref name="cameraNow"/> when a Renew reply leaves CurrentTime out. Null when it does not say.</summary>
    internal static TimeSpan? GrantedPeriod(XElement root, DateTime? cameraNow = null)
    {
        static DateTime? At(XElement root, string name) =>
            DateTime.TryParse(root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim(),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var t) ? t : null;
        return (At(root, "CurrentTime") ?? cameraNow) is { } now && At(root, "TerminationTime") is { } end && end > now
            ? end - now : null;
    }

    /// <summary>A PullMessages outcome: the messages, or null with whether the subscription is gone.
    /// HungUp: the camera closed the poll without a reply, read as "nothing happened".</summary>
    public sealed record PullResult(IReadOnlyList<OnvifNotification>? Messages, bool SubscriptionLost,
        bool HungUp = false);

    /// <summary>Waits up to <paramref name="hold"/> for the camera to report something (empty = nothing
    /// happened, as is a hang-up). Only a fault or a missing address loses the subscription.</summary>
    public async Task<PullResult> PullMessagesAsync(PullPointSubscription subscription,
        TimeSpan hold, CancellationToken ct)
    {
        // The camera holds the request for up to `hold`; the transport is allowed a
        // margin on top so a camera answering right at its deadline is not cut off
        // by us a moment before it speaks.
        var reply = await SendAsync(subscription.Address, NsEvents, "PullMessages",
            $"<tev:Timeout>{Duration(hold)}</tev:Timeout><tev:MessageLimit>64</tev:MessageLimit>",
            ct, hold + TimeSpan.FromSeconds(10), ActPull, extraHeaders: subscription.Headers).ConfigureAwait(false);
        if (reply.Root != null) return new PullResult(ParseNotifications(reply.Root), false);
        if (reply.HungUp) return new PullResult(Array.Empty<OnvifNotification>(), false, HungUp: true);
        return new PullResult(null, reply.Fault != null || reply.Status is 400 or 404 or 405);
    }

    /// <summary>Extends the subscription: the period granted, or null when the camera would not
    /// (Refused: it said so, which a conformant pull point survives, since its polls keep it alive).</summary>
    public async Task<(TimeSpan? Granted, bool Refused)> RenewSubscriptionAsync(PullPointSubscription subscription,
        TimeSpan termination, CancellationToken ct)
    {
        var reply = await SendAsync(subscription.Address, NsWsnt, "Renew",
            $"<wsnt:TerminationTime>{TerminationValue(termination)}</wsnt:TerminationTime>", ct,
            null, ActRenew, extraHeaders: subscription.Headers).ConfigureAwait(false);
        if (reply.Root != null) return (GrantedPeriod(reply.Root, DateTime.UtcNow + _clockSkew) ?? termination, false);
        return (null, reply.Fault != null || IsLastingRefusal(reply.Status, reply.Fault));
    }

    /// <summary>Best-effort tidy-up. A camera holds a dropped subscription until it
    /// times out, and a handful of those is a real cost on small hardware.</summary>
    public async Task UnsubscribeAsync(PullPointSubscription subscription, CancellationToken ct)
    {
        try
        {
            await CallAsync(subscription.Address, NsWsnt, "Unsubscribe", "", ct, TimeSpan.FromSeconds(4),
                ActUnsubscribe, extraHeaders: subscription.Headers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"{_tag}: ONVIF unsubscribe failed: {Log.Flatten(ex)}");
        }
    }

    /// <summary>An xs:duration, which is the only way these times may be written.</summary>
    internal static string Duration(TimeSpan t) =>
        "PT" + Math.Max(1, (int)Math.Round(t.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "S";

    /// <summary>The subscription manager's address out of a CreatePullPointSubscription
    /// reply: the wsa:Address inside SubscriptionReference, never any other Address
    /// in the document.</summary>
    internal static string? SubscriptionAddress(XElement root)
    {
        var reference = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "SubscriptionReference");
        var address = reference?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value?.Trim();
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    /// <summary>The SubscriptionReference's ReferenceParameters as SOAP header blocks,
    /// each marked wsa:IsReferenceParameter="true"; empty when the camera attached none.</summary>
    internal static string ReferenceParameterHeaders(XElement root)
    {
        var reference = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "SubscriptionReference");
        var parameters = reference?.Elements().FirstOrDefault(e => e.Name.LocalName == "ReferenceParameters");
        if (parameters == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var p in parameters.Elements())
        {
            var copy = new XElement(p);
            copy.SetAttributeValue(XName.Get("IsReferenceParameter", NsWsa), "true");
            sb.Append(copy.ToString(SaveOptions.DisableFormatting));
        }
        return sb.ToString();
    }

    /// <summary>The notifications in a PullMessages reply. Every SimpleItem in the
    /// message — from Source, Key and Data alike — is flattened into one bag, because
    /// which of the three a vendor puts the interesting value in is not something
    /// worth predicting; the Source block is also kept apart, as it identifies the speaker.</summary>
    internal static List<OnvifNotification> ParseNotifications(XElement root)
    {
        var list = new List<OnvifNotification>();
        foreach (var m in root.Descendants().Where(e => e.Name.LocalName == "NotificationMessage"))
        {
            var topic = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "Topic")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(topic)) continue;
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keys = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var si in m.Descendants().Where(e => e.Name.LocalName == "SimpleItem"))
            {
                var n = si.Attribute("Name")?.Value;
                var v = si.Attribute("Value")?.Value;
                if (string.IsNullOrEmpty(n) || v == null) continue;
                items[n] = v;
                var block = si.Parent?.Name.LocalName;
                if (block == "Source") source[n] = v;
                else if (block == "Data") data[n] = v;
                else if (block == "Key") keys[n] = v; // one object among several a rule tracks (ObjectId)
            }
            var operation = m.Descendants().Select(e => e.Attribute("PropertyOperation")?.Value)
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var src = source.Count > 0 ? source : null;
            string? subject = keys.Count == 0 ? null : string.Join(";", keys.Select(kv => kv.Key + "=" + kv.Value));

            // Each class a Tapo reports is its own state on the shared topic: one ending must not end the others.
            var classes = data.Keys.Where(OnvifNotification.IsClassItem).ToList();
            if (classes.Count > 0)
            {
                foreach (var name in classes)
                {
                    var own = new Dictionary<string, string>(items, StringComparer.OrdinalIgnoreCase);
                    foreach (var other in classes) if (other != name) own.Remove(other);
                    list.Add(new OnvifNotification(topic, OnvifNotification.ParseBool(data[name]), own, src,
                        operation, subject == null ? name : subject + ";" + name));
                }
                // A state beside the classes (IsMotion, IsLineCross) is a notification of its own.
                var rest = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
                foreach (var name in classes) { rest.Remove(name); items.Remove(name); }
                if (ActiveFrom(rest) == null) continue;
                data = rest;
            }

            var active = ActiveFrom(data.Count > 0 ? data : items);
            // ObjectDetection's ClassTypes is the state itself: empty means nothing is in view.
            if (active == null && data.TryGetValue("ClassTypes", out var classTypes))
                active = string.IsNullOrWhiteSpace(classTypes) ? false : operation != null ? true : null;
            list.Add(new OnvifNotification(topic, active, items, src, operation, subject));
        }
        return list;
    }

    /// <summary>The names cameras give the on/off of a rule's Data item. Checked
    /// first, in this order; any other "IsSomething" boolean counts after them.</summary>
    private static readonly string[] StateItemNames =
    {
        "State", "IsMotion", "IsInside", "Motion", "Active", "IsPeople", "IsHuman", "IsPerson",
        "IsVehicle", "IsPet", "IsAnimal", "IsIntrusion", "IsLineCross", "IsCrossed", "Detected", "active",
    };

    /// <summary>The on/off an analytics notification carries: any known state name, then
    /// any "Is…" item, OR-ed. Null when the notification said nothing either way.</summary>
    internal static bool? ActiveFrom(IReadOnlyDictionary<string, string> items)
    {
        bool? any = null;
        foreach (var name in StateItemNames)
            if (items.TryGetValue(name, out var v) && OnvifNotification.ParseBool(v) is { } b)
            {
                if (b) return true;
                any = false;
            }
        foreach (var (name, value) in items)
            if (name.StartsWith("Is", StringComparison.OrdinalIgnoreCase)
                && OnvifNotification.ParseBool(value) is { } b)
            {
                if (b) return true;
                any = false;
            }
        return any;
    }
}
