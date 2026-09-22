// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Xml.Linq;

namespace Neolink.Protocol;

/// <summary>
/// One thing a camera told us over ONVIF's event service. <see cref="Topic"/> is
/// the raw topic path ("tns1:RuleEngine/CellMotionDetector/Motion"), kept verbatim
/// because vendors invent their own and the log is where an unrecognised one has to
/// be visible. <see cref="Active"/> is the state the notification carries: true =
/// something is happening, false = it stopped, null = the camera said neither (a
/// one-shot event, which is treated as a start).
/// </summary>
public sealed record OnvifNotification(string Topic, bool? Active,
    IReadOnlyDictionary<string, string> Items)
{
    /// <summary>Whether this is a DETECTION rather than housekeeping. ONVIF cameras
    /// publish a great deal that is not motion — recording state, storage, network
    /// changes, clock sync — and a subscription without a filter receives all of it.</summary>
    public bool IsDetection => Label != null;

    /// <summary>The event label this maps onto in Neolink's own vocabulary, or null
    /// when the topic is not a detection at all.
    ///
    /// The topics are matched by the SEGMENT that carries the meaning rather than by
    /// the whole path: every vendor nests them differently, and the ONVIF-defined
    /// analytics topics (CellMotionDetector, MotionAlarm, the field/line/intrusion
    /// rules) are the ones every Profile S camera that detects anything publishes.</summary>
    public string? Label
    {
        get
        {
            var t = Topic;
            // Object classification, where the camera offers it, is worth more than
            // "something moved" — the events page, notifications and Home Assistant
            // all key off these.
            if (Has("ObjectType", "Type", "ClassType", "Classification") is { } cls)
            {
                if (Is(cls, "human", "person", "people", "pedestrian")) return "person";
                if (Is(cls, "vehicle", "car", "truck", "bus", "motorcycle", "bike", "bicycle")) return "vehicle";
                if (Is(cls, "animal", "dog", "cat", "pet")) return "animal";
                if (Is(cls, "face")) return "face";
            }
            if (Contains(t, "Face")) return "face";
            if (Contains(t, "PeopleDetect", "HumanDetect", "PersonDetect")) return "person";
            if (Contains(t, "VehicleDetect", "CarDetect")) return "vehicle";
            // Everything else that means "the picture changed in a way the camera
            // was told to care about" is motion.
            if (Contains(t, "CellMotionDetector", "MotionAlarm", "MotionDetect", "VideoMotion",
                    "FieldDetector", "LineDetector", "ObjectsInside", "Intrusion", "Crossed",
                    "LoiteringDetector"))
                return "motion";
            return null;
        }
    }

    private static bool Contains(string topic, params string[] needles) =>
        needles.Any(n => topic.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static bool Is(string value, params string[] options) =>
        options.Any(o => string.Equals(value, o, StringComparison.OrdinalIgnoreCase));

    private string? Has(params string[] names)
    {
        foreach (var n in names)
            if (Items.TryGetValue(n, out var v) && v.Length > 0)
                return v;
        return null;
    }
}

/// <summary>The outcome of asking for a pull-point subscription. NoEventService =
/// a lasting answer (the camera has none, or ONVIF is not reachable at all), worth
/// re-checking only rarely. Otherwise a null Address is a refusal of THIS attempt,
/// worth retrying soon; Reason says why, for the one line that reports it.</summary>
public sealed record PullPointResult(string? Address, bool NoEventService, string? Reason);

public sealed partial class OnvifClient
{
    // WS-Addressing actions. A subscription manager is addressed by these, not by
    // the URL alone, and strict implementations fault without them.
    private const string ActCreate = "http://www.onvif.org/ver10/events/wsdl/EventPortType/CreatePullPointSubscriptionRequest";
    private const string ActPull = "http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest";
    private const string ActRenew = "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/RenewRequest";
    private const string ActUnsubscribe = "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest";

    private string? _eventsUrl;

    /// <summary>The URL of the event service, or null when the camera has none.
    /// Only meaningful after discovery.</summary>
    public string? EventsUrl => _eventsUrl;

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
            eventsUrl = _eventsUrl;
        }
        finally { _gate.Release(); }
        if (eventsUrl == null)
            return new PullPointResult(null, NoEventService: true, "the camera advertises no event service");

        var xml = await CallAsync(eventsUrl, NsEvents, "CreatePullPointSubscription",
            $"<tev:InitialTerminationTime>{Duration(termination)}</tev:InitialTerminationTime>", ct,
            null, ActCreate).ConfigureAwait(false);
        if (xml == null)
            return new PullPointResult(null, NoEventService: false, _lastError);
        var address = SubscriptionAddress(xml);
        if (address == null)
            return new PullPointResult(null, NoEventService: false, "the reply carried no subscription address");
        return new PullPointResult(NormalizeXAddr(address), NoEventService: false, null);
    }

    /// <summary>Waits for the camera to report something, for up to
    /// <paramref name="hold"/>. Returns an empty list when nothing happened (which
    /// is the normal case and not an error) and null when the subscription is no
    /// longer usable, which the caller answers by making a new one.</summary>
    public async Task<IReadOnlyList<OnvifNotification>?> PullMessagesAsync(string subscription,
        TimeSpan hold, CancellationToken ct)
    {
        // The camera holds the request for up to `hold`; the transport is allowed a
        // margin on top so a camera answering right at its deadline is not cut off
        // by us a moment before it speaks.
        var xml = await CallAsync(subscription, NsEvents, "PullMessages",
            $"<tev:Timeout>{Duration(hold)}</tev:Timeout><tev:MessageLimit>64</tev:MessageLimit>",
            ct, hold + TimeSpan.FromSeconds(10), ActPull).ConfigureAwait(false);
        return xml == null ? null : ParseNotifications(xml);
    }

    /// <summary>Extends the subscription. False means it is gone and a new one is
    /// needed — cameras drop subscriptions on reboot, on their own timeout, and
    /// sometimes for no reason at all.</summary>
    public async Task<bool> RenewSubscriptionAsync(string subscription, TimeSpan termination,
        CancellationToken ct) =>
        await CallAsync(subscription, NsWsnt, "Renew",
            $"<wsnt:TerminationTime>{Duration(termination)}</wsnt:TerminationTime>", ct,
            null, ActRenew).ConfigureAwait(false) != null;

    /// <summary>Best-effort tidy-up. A camera holds a dropped subscription until it
    /// times out, and a handful of those is a real cost on small hardware.</summary>
    public async Task UnsubscribeAsync(string subscription, CancellationToken ct)
    {
        try
        {
            await CallAsync(subscription, NsWsnt, "Unsubscribe", "", ct, TimeSpan.FromSeconds(4),
                ActUnsubscribe).ConfigureAwait(false);
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

    /// <summary>The notifications in a PullMessages reply. Every SimpleItem in the
    /// message — from Source, Key and Data alike — is flattened into one bag, because
    /// which of the three a vendor puts the interesting value in is not something
    /// worth predicting.</summary>
    internal static List<OnvifNotification> ParseNotifications(XElement root)
    {
        var list = new List<OnvifNotification>();
        foreach (var m in root.Descendants().Where(e => e.Name.LocalName == "NotificationMessage"))
        {
            var topic = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "Topic")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(topic)) continue;
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var si in m.Descendants().Where(e => e.Name.LocalName == "SimpleItem"))
            {
                var n = si.Attribute("Name")?.Value;
                var v = si.Attribute("Value")?.Value;
                if (!string.IsNullOrEmpty(n) && v != null) items[n] = v;
            }
            list.Add(new OnvifNotification(topic, ActiveFrom(items), items));
        }
        return list;
    }

    /// <summary>The on/off an analytics notification carries. Cameras name it
    /// differently per rule (State, IsMotion, IsInside…), so any of the known names
    /// counts; null means the notification said nothing either way.</summary>
    internal static bool? ActiveFrom(IReadOnlyDictionary<string, string> items)
    {
        foreach (var name in new[] { "State", "IsMotion", "IsInside", "IsTamper", "Motion", "Active", "IsPeople" })
            if (items.TryGetValue(name, out var v) && bool.TryParse(v.Trim(), out var b))
                return b;
        return null;
    }
}
