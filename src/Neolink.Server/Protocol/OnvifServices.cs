// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Xml.Linq;

namespace Neolink.Protocol;

/// <summary>The camera's own account of itself (ONVIF GetDeviceInformation). Every
/// field is optional — firmwares differ in which they fill in.</summary>
public sealed record OnvifDeviceInfo(string? Manufacturer, string? Model,
    string? Firmware, string? Serial, string? HardwareId);

/// <summary>One ONVIF media profile: a named pairing of a video source with an
/// encoder configuration, and the handle every media and PTZ call is addressed to.
/// <see cref="Encoder"/> is the camera's own configuration element, kept verbatim
/// because a write echoes it back with the changed fields.</summary>
/// <param name="VideoSourceToken">The video source CONFIGURATION's token — what
/// GetOSDs is keyed on.</param>
/// <param name="SourceToken">The video SOURCE's own token, as the configuration
/// names it — what imaging is keyed on, and the only way to learn it on a camera
/// that speaks Media2 (which has no GetVideoSources).</param>
/// <param name="AnalyticsToken">The video analytics configuration's token — what
/// the analytics service's rules and modules are keyed on.</param>
public sealed record OnvifProfile(string Token, string Name, bool HasPtz,
    string? VideoSourceToken, XElement? Encoder, string? SourceToken = null, string? AnalyticsToken = null,
    string? PtzNodeToken = null)
{
    public string? EncoderToken => Encoder?.Attribute("token")?.Value;
    public string? Encoding => Local(Encoder, "Encoding");
    public int Width => Num(Local(Encoder?.Elements().FirstOrDefault(e => e.Name.LocalName == "Resolution"), "Width"));
    public int Height => Num(Local(Encoder?.Elements().FirstOrDefault(e => e.Name.LocalName == "Resolution"), "Height"));
    public int FrameRate => Num(Local(RateControl, "FrameRateLimit"));
    /// <summary>Kbps, as ONVIF counts it.</summary>
    public int Bitrate => Num(Local(RateControl, "BitrateLimit"));
    /// <summary>ver10 keeps it inside the codec block; Media2 makes it an attribute
    /// of the configuration itself.</summary>
    public int GovLength => Num(Local(Codec, "GovLength") ?? Encoder?.Attribute("GovLength")?.Value);

    private XElement? RateControl =>
        Encoder?.Elements().FirstOrDefault(e => e.Name.LocalName == "RateControl");

    /// <summary>The codec-specific block — H264 or (on newer firmware) H265.</summary>
    private XElement? Codec =>
        Encoder?.Elements().FirstOrDefault(e => e.Name.LocalName is "H264" or "H265");

    private static string? Local(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

    /// <summary>Read as a number with a fraction and rounded: Media2 types the frame
    /// rate as a float ("25.0"), which an integer parse would read as zero.</summary>
    private static int Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : 0;
}

/// <summary>What one encoder configuration will accept (ONVIF
/// GetVideoEncoderConfigurationOptions). A null range means the camera offered
/// none, and the value is passed through as the user set it.</summary>
public sealed record OnvifEncoderOptions(IReadOnlyList<(int Width, int Height)> Resolutions,
    (int Min, int Max)? FrameRate, (int Min, int Max)? Bitrate, IReadOnlyList<int>? FrameRatesListed = null);

/// <summary>One saved PTZ position. Token is the camera's handle for it; Id its number for the run.</summary>
public sealed record OnvifPreset(string Token, string Name, int Id = 0);

/// <summary>What a PTZ node can drive (GetNodes/SupportedPTZSpaces). PanTilt =
/// it can be steered continuously; Zoom = it has an absolute zoom position, whose
/// range is <see cref="ZoomRange"/> (0..1 on nearly every camera, but it is the
/// camera's to say). MaxPresets bounds how many positions it can remember.</summary>
public sealed record OnvifPtzNode(bool PanTilt, bool Zoom, (double Min, double Max) ZoomRange, int? MaxPresets,
    string? ZoomSpace = null, bool AnyZoom = false);

/// <summary>One on-screen overlay (ONVIF GetOSDs), kept with its own element so a
/// write can echo the camera's configuration back with only the changed fields.
/// Kind is the text's own type: "Plain", "Date", "Time" or "DateAndTime".</summary>
public sealed record OnvifOsd(string Token, string? Position, string? Kind,
    string? PlainText, XElement Element);

public sealed partial class OnvifClient
{
    // Positions ONVIF names for an OSD, in the order the panel offers them. The
    // camera's own list is not asked for: GetOSDOptions is optional and widely
    // unimplemented, while these four are required of any Profile S device.
    internal static readonly string[] OsdPositions =
        { "UpperLeft", "UpperRight", "LowerLeft", "LowerRight" };

    /// <summary>Everything the camera says about itself, or null when ONVIF is
    /// unavailable. Never throws.</summary>
    public async Task<OnvifDeviceInfo?> TryGetDeviceInfoAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            var xml = await CallAsync(_deviceUrl, NsDevice, "GetDeviceInformation", "", ct)
                .ConfigureAwait(false);
            return xml == null ? null : ParseDeviceInfo(xml);
        }, "device information", ct).ConfigureAwait(false);

    /// <summary>The camera's media profiles, or null when ONVIF is unavailable. Cached
    /// for <see cref="ProfilesMaxAge"/>: the camera's own web page can change them under us.</summary>
    public async Task<IReadOnlyList<OnvifProfile>?> TryGetProfilesAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            if (_profiles == null || DateTime.UtcNow - _profilesAt > ProfilesMaxAge)
                _profiles = await ReadProfilesAsync(ct).ConfigureAwait(false) ?? _profiles;
            return _profiles;
        }, "media profiles", ct).ConfigureAwait(false);

    private DateTime _profilesAt;
    private static readonly TimeSpan ProfilesMaxAge = TimeSpan.FromSeconds(20);

    /// <summary>The profile this camera's streams were matched to; it picks which channel
    /// of a multi-channel device per-channel calls address. Null until bound (first profile).</summary>
    public string? PreferredProfileToken => _preferredProfileToken;

    /// <summary>Sets <see cref="PreferredProfileToken"/> under the gate, so a PTZ move
    /// in flight does not find its profile pulled away.</summary>
    public async Task SetPreferredProfileAsync(string? token, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_preferredProfileToken == token) return;
            _preferredProfileToken = token;
            // The head is addressed per channel, so it is picked again for the new one.
            if (_profiles != null) SelectPtzProfile(_profiles);
        }
        finally { _gate.Release(); }
    }

    private string? _preferredProfileToken;

    private OnvifProfile? PreferredProfile =>
        _profiles?.FirstOrDefault(p => p.Token == _preferredProfileToken);

    /// <summary>The profiles on the preferred profile's video source — one channel's
    /// worth. Every profile when no preference is known or the profile names no source.</summary>
    private IEnumerable<OnvifProfile> OwnChannelProfiles(IReadOnlyList<OnvifProfile> profiles)
    {
        // Until matched, none of a multi-channel device's profiles is known to be this camera's.
        if (ChannelUnknownIn(profiles)) return Array.Empty<OnvifProfile>();
        var preferred = profiles.FirstOrDefault(p => p.Token == _preferredProfileToken);
        if (preferred?.SourceToken is not { } source) return preferred == null ? profiles : Prefer(profiles, preferred);
        var same = profiles.Where(p => p.SourceToken == source).ToList();
        return same.Count == 0 ? Prefer(profiles, preferred) : Prefer(same, preferred);

        static IEnumerable<OnvifProfile> Prefer(IEnumerable<OnvifProfile> all, OnvifProfile first) =>
            new[] { first }.Concat(all.Where(p => p != first));
    }

    /// <summary>How many channels (distinct video sources) the profiles span.</summary>
    internal static int Channels(IEnumerable<OnvifProfile> profiles) =>
        profiles.Select(p => p.SourceToken).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal).Count();

    /// <summary>A generic device with several video sources: an NVR or a multi-sensor camera.</summary>
    private bool MultiChannel(IReadOnlyList<OnvifProfile>? profiles) =>
        _generic && (_sourceCount > 1 || (profiles != null && Channels(profiles) > 1));

    private bool ChannelUnknownIn(IReadOnlyList<OnvifProfile>? profiles) =>
        MultiChannel(profiles) && profiles?.Any(p => p.Token == _preferredProfileToken) != true;

    /// <summary>A multi-channel device whose channel this camera is has not been matched yet, so
    /// per-channel calls (picture, overlays, PTZ, snapshot, motion grid) are held back.</summary>
    public bool ChannelUnknown => ChannelUnknownIn(_profiles);

    /// <summary>The profile PTZ calls go to: one with PTZ on this camera's own channel,
    /// since a multi-sensor device's other head is not this camera's to move.</summary>
    private void SelectPtzProfile(IReadOnlyList<OnvifProfile> profiles)
    {
        var ptzProfile = OwnChannelProfiles(profiles).FirstOrDefault(p => p.HasPtz);
        if (ptzProfile?.Token != _ptzProfileToken)
        {
            _ptzNode = null;
            _ptzNodeRefused = false;
            _presetTokens = Array.Empty<string>();
            lock (_presetIds) _presetIds.Clear(); // another head's tokens
        }
        _ptzProfileToken = ptzProfile?.Token;
        _ptzNodeToken = ptzProfile?.PtzNodeToken;
    }

    /// <summary>Whether this camera answered with a moving head — PTZ calls are
    /// addressed to a profile, so this only means anything once the profiles have
    /// been read (TryGetProfilesAsync).</summary>
    public bool HasPtz => PtzProfile != null;

    /// <summary>The profile PTZ calls address, when there is a PTZ service to send them to.</summary>
    private string? PtzProfile => _ptzUrl == null ? null : _ptzProfileToken;

    /// <summary>What the head can do, once <see cref="TryGetPtzNodeAsync"/> has
    /// answered; null before that.</summary>
    public OnvifPtzNode? PtzNode => _ptzNode;

    /// <summary>Each profile's RTSP URL by token, to match a configured stream to its profile. Null when ONVIF
    /// is unavailable; a profile that has not answered is absent, asked again at most every <see cref="StreamUriRetry"/>.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> TryGetStreamUrisAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            var profiles = _profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false);
            if (profiles == null) return null;
            if (DateTime.UtcNow >= _streamUrisRetryAt)
            {
                foreach (var p in profiles.Where(p => !_streamUris.ContainsKey(p.Token)))
                {
                    // Only the path matters, the same whichever transport is named: Media2's RtspUnicast
                    // (required of Profile T), or ver10's StreamSetup.
                    var reply = await SendAsync(MediaUrl, MediaNs, "GetStreamUri", _media2
                        ? $"<tr2:Protocol>RtspUnicast</tr2:Protocol><tr2:ProfileToken>{Esc(p.Token)}</tr2:ProfileToken>"
                        : "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream>" +
                          "<tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
                          $"<trt:ProfileToken>{Esc(p.Token)}</trt:ProfileToken>", ct).ConfigureAwait(false);
                    var uri = reply.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value?.Trim();
                    // A refusal the camera will repeat is that profile's answer ("" matches no stream).
                    if (!string.IsNullOrWhiteSpace(uri)) _streamUris[p.Token] = uri;
                    else if (reply.Root != null || IsLastingRefusal(reply.Status, reply.Fault)) _streamUris[p.Token] = "";
                }
                _streamUrisRetryAt = profiles.All(p => _streamUris.ContainsKey(p.Token))
                    ? DateTime.MinValue : DateTime.UtcNow + StreamUriRetry;
            }
            StreamUrisComplete = profiles.All(p => _streamUris.ContainsKey(p.Token));
            return profiles.Where(p => _streamUris.TryGetValue(p.Token, out var u) && u.Length > 0)
                .ToDictionary(p => p.Token, p => _streamUris[p.Token], StringComparer.Ordinal);
        }, "stream URIs", ct).ConfigureAwait(false);

    /// <summary>Whether every profile has answered <see cref="TryGetStreamUrisAsync"/> for good.</summary>
    public bool StreamUrisComplete { get; private set; }

    private readonly Dictionary<string, string> _streamUris = new(StringComparer.Ordinal);
    private DateTime _streamUrisRetryAt;
    private static readonly TimeSpan StreamUriRetry = TimeSpan.FromSeconds(30);

    /// <summary>Whether the imaging service answered during discovery.</summary>
    public bool HasImaging => _hasImaging;

    /// <summary>Whether the camera has answered ONVIF at all. False until the first
    /// read runs discovery, and false again after a reboot — so anything gated on it
    /// must be asked for AFTER a read (the capability probe reads device information
    /// first for exactly this reason).</summary>
    public bool Ready => _ready;

    /// <summary>A still straight from the camera, using the snapshot URI ONVIF
    /// advertises — what a Profile S camera is meant to offer, and far cheaper than
    /// decoding a frame out of the video. Null when it offers none, or when the
    /// fetch failed; the caller falls back to the stream. Never throws.</summary>
    public async Task<byte[]?> TrySnapshotAsync(CancellationToken ct) => await TrySnapshotAsync(false, ct).ConfigureAwait(false);

    /// <summary>As <see cref="TrySnapshotAsync(CancellationToken)"/>; with <paramref name="small"/>
    /// the still comes from the channel's smallest profile, for a consumer with a size cap.</summary>
    public async Task<byte[]?> TrySnapshotAsync(bool small, CancellationToken ct)
    {
        try
        {
            var uri = await SnapshotUriAsync(small, ct).ConfigureAwait(false);
            if (uri == null) return null;
            // Its own deadline, covering the body as well as the headers. This client
            // has no timeout of its own (the event long-poll needs far longer than a
            // settings call), so without one a camera that accepts the connection and
            // then stalls would hold this forever — and snapshots are asked for from
            // the Home Assistant refresh, which walks every camera in turn.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(SnapshotTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var res = await _http.SendAsync(req, deadline.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Log.Debug($"{_tag}: ONVIF snapshot fetch returned HTTP {(int)res.StatusCode}");
                // A 404 will not start working, so the URI is forgotten; a 401/403 may be
                // a stale digest nonce or a slow login, and must not cost the still for the run.
                if (res.StatusCode is System.Net.HttpStatusCode.NotFound)
                    ForgetSnapshotUri(uri);
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            return Neolink.Media.FrameGrab.IsJpeg(bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"{_tag}: ONVIF snapshot failed: {Log.Flatten(ex)}");
            return null;
        }
    }

    /// <summary>Whether a snapshot URI is known to exist. Only true once the camera
    /// has actually given one, so the caller keeps its stream-decoded fallback until
    /// then rather than assuming a still it may never get.</summary>
    public bool HasSnapshotUri
    {
        get { lock (_snapshotUris) return _snapshotUris.Values.Any(u => u.Length > 0); }
    }

    /// <summary>A still is a few hundred KB at most; a camera slower than this to
    /// hand one over is better answered by the last frame on hand.</summary>
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Each profile's snapshot URI, keyed by token: "" when the camera said it
    /// has none; absent when it could not be asked, so it is asked again.</summary>
    private readonly Dictionary<string, string> _snapshotUris = new(StringComparer.Ordinal);
    /// <summary>When a "none" that was not a lasting refusal may be asked again.</summary>
    private readonly Dictionary<string, DateTime> _snapshotAskAgainAt = new(StringComparer.Ordinal);
    private static readonly TimeSpan SnapshotUriRetry = TimeSpan.FromMinutes(30);
    private bool _snapshotLogged;

    private void ForgetSnapshotUri(string uri)
    {
        lock (_snapshotUris)
            foreach (var k in _snapshotUris.Where(kv => kv.Value == uri).Select(kv => kv.Key).ToList())
            {
                _snapshotUris[k] = "";
                _snapshotAskAgainAt.Remove(k);
            }
    }

    /// <summary>The preferred profile's snapshot URI, or with <paramref name="small"/>
    /// the channel's smallest profile's; null when no profile of the channel offers one.</summary>
    private async Task<string?> SnapshotUriAsync(bool small, CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            var profiles = _profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false);
            if (profiles == null) return null; // could not ask — try again later
            var order = OwnChannelProfiles(profiles).ToList();
            if (small)
                order = order.OrderBy(p => (long)p.Width * p.Height == 0 ? long.MaxValue : (long)p.Width * p.Height)
                    .ToList();
            foreach (var p in order)
            {
                string? uri;
                lock (_snapshotUris)
                {
                    _snapshotUris.TryGetValue(p.Token, out uri);
                    if (uri == "" && _snapshotAskAgainAt.TryGetValue(p.Token, out var at) && DateTime.UtcNow >= at)
                        uri = null;
                }
                if (uri == null)
                {
                    var reply = await SendAsync(MediaUrl, MediaNs, "GetSnapshotUri",
                        $"<{Mp}:ProfileToken>{Esc(p.Token)}</{Mp}:ProfileToken>", ct).ConfigureAwait(false);
                    var answered = reply.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value?.Trim();
                    // Silence, a busy 503 or an auth hiccup is asked again next time; any other
                    // refusal is remembered, for good when lasting and for a while otherwise.
                    bool lasting = reply.Root != null || IsLastingRefusal(reply.Status, reply.Fault);
                    if (!lasting && (reply.Status is 0 or 503 || LooksLikeAuthRefusal(reply.Fault, reply.Status))) continue;
                    // As with the service table, a camera reporting its own idea of
                    // its hostname must still be reachable from here.
                    uri = string.IsNullOrWhiteSpace(answered) ? "" : NormalizeXAddr(answered) ?? "";
                    lock (_snapshotUris)
                    {
                        _snapshotUris[p.Token] = uri;
                        if (lasting) _snapshotAskAgainAt.Remove(p.Token);
                        else _snapshotAskAgainAt[p.Token] = DateTime.UtcNow + SnapshotUriRetry;
                    }
                    if (uri.Length > 0 && !_snapshotLogged)
                    {
                        _snapshotLogged = true;
                        Log.Info($"{_tag}: ONVIF snapshot available — stills come from the camera rather than its video");
                    }
                }
                if (uri.Length > 0) return uri;
            }
            return null;
        }, "snapshot URI", ct).ConfigureAwait(false);

    /// <summary>Whether the camera reported media profiles — without them there is
    /// no encoder configuration to show or change. Deliberately NOT the cache: a
    /// write drops the cached profiles so the next read comes from the camera, and
    /// reading that as "this camera has no profiles" is what turned the stream
    /// section read-only for the rest of the run after one change.</summary>
    public bool HasProfiles => _hadProfiles;

    /// <summary>What one encoder configuration will accept, or null when the camera
    /// offered no options (its current values are then the only ones known).</summary>
    public async Task<OnvifEncoderOptions?> TryGetEncoderOptionsAsync(string profileToken,
        string configToken, CancellationToken ct, string? encoding = null) =>
        await GuardedAsync(async () =>
        {
            // The options reply is ONVIF's largest and only changes with firmware, so it
            // is remembered per configuration until a write.
            var key = configToken + "|" + encoding;
            if (_encoderOptions.TryGetValue(key, out var known)) return known;
            var xml = await CallAsync(MediaUrl, MediaNs, "GetVideoEncoderConfigurationOptions",
                $"<{Mp}:ConfigurationToken>{Esc(configToken)}</{Mp}:ConfigurationToken>" +
                $"<{Mp}:ProfileToken>{Esc(profileToken)}</{Mp}:ProfileToken>", ct).ConfigureAwait(false);
            var options = xml == null ? null : ParseEncoderOptions(xml, encoding);
            if (options != null) _encoderOptions[key] = options;
            return options;
        }, "encoder options", ct).ConfigureAwait(false);

    private readonly Dictionary<string, OnvifEncoderOptions> _encoderOptions = new(StringComparer.Ordinal);

    /// <summary>Changes one profile's encoder settings. Null fields are left as the
    /// camera has them: ONVIF replaces the whole configuration, so the camera's own
    /// element is echoed back with only the requested fields edited. Throws on
    /// failure — this is an explicit user action, and a silent no-op reads as a bug.</summary>
    public async Task SetVideoEncoderAsync(string profileToken, int? width, int? height,
        int? frameRate, int? bitrateKbps, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false))
                throw new NotSupportedException("the camera's ONVIF media service is not reachable");
            // Read afresh, never from the cache: the whole configuration goes back, and
            // a stale copy would quietly undo a change made in the camera's own web page.
            var profile = (await ReadProfilesAsync(ct).ConfigureAwait(false)
                    ?? throw new IOException("the camera's ONVIF profiles could not be read just now — try again shortly"))
                .FirstOrDefault(p => p.Token == profileToken)
                ?? throw new NotSupportedException($"the camera has no ONVIF profile '{profileToken}'");
            if (profile.Encoder is not { } encoder)
                throw new NotSupportedException($"ONVIF profile '{profileToken}' carries no encoder configuration");

            // A fresh copy: a failed write must leave the cached profile describing
            // what the camera still has, not what we wished for.
            var edited = new XElement(encoder);
            if (width is { } w && height is { } h)
            {
                SetLocal(edited, "Resolution", "Width", w.ToString(CultureInfo.InvariantCulture));
                SetLocal(edited, "Resolution", "Height", h.ToString(CultureInfo.InvariantCulture));
            }
            if (frameRate is { } fps)
                SetLocal(edited, "RateControl", "FrameRateLimit", fps.ToString(CultureInfo.InvariantCulture));
            if (bitrateKbps is { } kbps)
                SetLocal(edited, "RateControl", "BitrateLimit", kbps.ToString(CultureInfo.InvariantCulture));

            // The configuration goes back under the media service's own element
            // name; its children keep the ONVIF schema namespace they were read in.
            var body = Echo(XName.Get("Configuration", MediaNs), edited);

            // Media2's setter takes the configuration alone: ForcePersistence is a
            // ver10 element, and a strict Media2 parser faults on one it does not know.
            var xml = await CallAsync(MediaUrl, MediaNs, "SetVideoEncoderConfiguration",
                _media2 ? body.ToString() : body + "<trt:ForcePersistence>true</trt:ForcePersistence>", ct)
                .ConfigureAwait(false);
            if (xml == null)
                throw Refused("the camera did not confirm the ONVIF encoder change");
            _profiles = null; // re-read on the next look: the camera is the authority
            _encoderOptions.Clear();
        }
        finally { _gate.Release(); }
    }

    /// <summary>The camera's element sent back under the media service's name. Namespace
    /// declarations are dropped: a default one would fight the new name when serialised.</summary>
    private static XElement Echo(XName name, XElement original)
    {
        var body = new XElement(name);
        foreach (var a in original.Attributes().Where(a => !a.IsNamespaceDeclaration))
            body.SetAttributeValue(a.Name, a.Value);
        body.Add(original.Elements().Select(e => new XElement(e)));
        return body;
    }

    // ------------------------------------------------------------ PTZ

    /// <summary>Drives the head at a fraction of full speed on each axis (-1..1),
    /// until <see cref="PtzStopAsync"/> or the camera's own timeout.</summary>
    public Task PtzMoveAsync(double pan, double tilt, double zoom, CancellationToken ct) =>
        PtzAsync("ContinuousMove",
            "<tptz:Velocity>" +
            $"<tt:PanTilt x=\"{Num(pan)}\" y=\"{Num(tilt)}\"/>" +
            // A head known to have no zoom is not sent one: a strict firmware faults
            // the whole move over an axis it does not have.
            (_ptzNode is { AnyZoom: false } ? "" : $"<tt:Zoom x=\"{Num(zoom)}\"/>") +
            "</tptz:Velocity>", ct);

    public Task PtzStopAsync(CancellationToken ct) =>
        PtzAsync("Stop", "<tptz:PanTilt>true</tptz:PanTilt><tptz:Zoom>true</tptz:Zoom>", ct);

    /// <summary>What the head can actually do (GetNodes), read once per run. A PTZ
    /// configuration on a profile says a PTZ node exists, not which axes it has — a
    /// varifocal lens with a motorised zoom has a node with no pan or tilt at all,
    /// and offering it arrows would be offering buttons that do nothing.</summary>
    public async Task<OnvifPtzNode?> TryGetPtzNodeAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            if (_ptzNode != null || _ptzNodeRefused) return _ptzNode;
            if (PtzProfile == null) return null;
            var reply = await SendAsync(_ptzUrl!, NsPtz, "GetNodes", "", ct).ConfigureAwait(false);
            _ptzNode = reply.Root == null ? null : ParsePtzNode(reply.Root, _ptzNodeToken);
            // A reply without a usable node, or a refusal the camera will repeat,
            // is its answer; silence, a busy 503 or an auth hiccup are asked again.
            _ptzNodeRefused = _ptzNode == null && (reply.Root != null || IsLastingRefusal(reply.Status, reply.Fault));
            return _ptzNode;
        }, "PTZ node", ct).ConfigureAwait(false);

    /// <summary>Whether <see cref="TryGetPtzNodeAsync"/> has a lasting answer — the
    /// node, or the camera's refusal to describe one — rather than a silence.</summary>
    public bool PtzNodeSettled => _ptzNode != null || _ptzNodeRefused;

    /// <summary>The lens's current zoom in the node's own absolute space (null when
    /// the camera will not say), and whether it is still moving (null when the
    /// camera does not report that).</summary>
    public async Task<(double? At, bool? Moving)> TryGetZoomAsync(CancellationToken ct)
    {
        var status = await GuardedAsync(async () =>
        {
            if (PtzProfile is not { } profile) return null;
            return await CallAsync(_ptzUrl!, NsPtz, "GetStatus",
                $"<tptz:ProfileToken>{Esc(profile)}</tptz:ProfileToken>", ct).ConfigureAwait(false);
        }, "PTZ status", ct).ConfigureAwait(false);
        return status == null ? (null, null) : (ParseZoomPosition(status), ParseZoomMoving(status));
    }

    /// <summary>Drives the lens to an absolute zoom. Only the Zoom element is sent,
    /// which ONVIF defines as "leave pan and tilt where they are" — a slider for the
    /// lens must never swing the head.</summary>
    public Task ZoomToAsync(double position, CancellationToken ct) =>
        PtzAsync("AbsoluteMove",
            "<tptz:Position>" +
            $"<tt:Zoom x=\"{position.ToString("0.####", CultureInfo.InvariantCulture)}\"" +
            // The space the slider's range was read from, named: left out, the
            // camera would use its configuration's default, which may be another.
            (_ptzNode?.ZoomSpace is { } space ? $" space=\"{Esc(space)}\"" : "") + "/>" +
            "</tptz:Position>", ct);

    /// <summary>The camera's saved positions, or null when it has no PTZ.</summary>
    public async Task<IReadOnlyList<OnvifPreset>?> TryGetPresetsAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            if (PtzProfile is not { } profile) return null;
            var xml = await CallAsync(_ptzUrl!, NsPtz, "GetPresets",
                $"<tptz:ProfileToken>{Esc(profile)}</tptz:ProfileToken>", ct).ConfigureAwait(false);
            if (xml == null) return null;
            List<OnvifPreset> presets;
            lock (_presetIds) presets = ParsePresets(xml).Select(p => p with { Id = PresetId(p.Token) }).ToList();
            _presetTokens = presets.Select(p => p.Token).ToList();
            return presets;
        }, "PTZ presets", ct).ConfigureAwait(false);

    /// <summary>Each preset token's number for the run, so a preset keeps its id when others
    /// are added or removed in the camera's own app. Guarded by its own lock.</summary>
    private readonly Dictionary<string, int> _presetIds = new(StringComparer.Ordinal);

    /// <summary>The token's id: the one it already has, else the lowest never handed out.</summary>
    private int PresetId(string token)
    {
        if (_presetIds.TryGetValue(token, out var id)) return id;
        for (id = 1; _presetIds.ContainsValue(id); id++) { }
        return _presetIds[token] = id;
    }

    /// <summary>Drives to the saved position with this <paramref name="id"/>
    /// (<see cref="OnvifPreset.Id"/>).</summary>
    public async Task GotoPresetAsync(int id, CancellationToken ct)
    {
        var token = await PresetTokenAsync(id, ct).ConfigureAwait(false)
            ?? throw new NotSupportedException($"the camera has no preset {id}");
        await PtzAsync("GotoPreset", $"<tptz:PresetToken>{Esc(token)}</tptz:PresetToken>", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Saves where the camera points now. An id the camera has overwrites that
    /// preset; any other asks the camera for a new one, which then takes that id.</summary>
    public async Task SavePresetAsync(int id, string name, CancellationToken ct)
    {
        var token = await PresetTokenAsync(id, ct).ConfigureAwait(false);
        var reply = await PtzAsync("SetPreset",
            $"<tptz:PresetName>{Esc(name)}</tptz:PresetName>" +
            (token == null ? "" : $"<tptz:PresetToken>{Esc(token)}</tptz:PresetToken>"), ct)
            .ConfigureAwait(false);
        if (token == null
            && reply.Descendants().FirstOrDefault(e => e.Name.LocalName == "PresetToken")?.Value?.Trim() is { Length: > 0 } created)
            lock (_presetIds)
            {
                foreach (var old in _presetIds.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
                    _presetIds.Remove(old);
                _presetIds[created] = id;
            }
        _presetTokens = Array.Empty<string>(); // the list is re-read on the next look
    }

    private async Task<string?> PresetTokenAsync(int id, CancellationToken ct)
    {
        if (_presetTokens.Count == 0) await TryGetPresetsAsync(ct).ConfigureAwait(false);
        var present = _presetTokens;
        lock (_presetIds)
            return _presetIds.Where(kv => kv.Value == id && present.Contains(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
    }

    /// <summary>One PTZ operation against the discovered profile, returning the camera's reply.
    /// Throws when the camera has no PTZ or refuses the command — every caller is a user action.</summary>
    private async Task<XElement> PtzAsync(string op, string innerBody, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false) || PtzProfile is not { } profile)
                throw new NotSupportedException("the camera's ONVIF PTZ service is not reachable");
            return await CallAsync(_ptzUrl!, NsPtz, op,
                    $"<tptz:ProfileToken>{Esc(profile)}</tptz:ProfileToken>{innerBody}", ct)
                .ConfigureAwait(false) ?? throw Refused($"the camera refused the ONVIF {op}");
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------ OSD

    /// <summary>The camera's text overlays, or null when it exposes none. Image
    /// overlays are left out: there is nothing here to edit about them.</summary>
    public async Task<IReadOnlyList<OnvifOsd>?> TryGetOsdsAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            // May switch the dialect; null when only another channel's overlays could be asked for.
            if (await OsdScopeAsync(ct).ConfigureAwait(false) is not { } scope) return null;
            var xml = await CallAsync(MediaUrl, MediaNs, "GetOSDs", scope, ct)
                .ConfigureAwait(false);
            return xml == null ? null : ParseOsds(xml);
        }, "OSDs", ct).ConfigureAwait(false);

    /// <summary>GetOSDs is keyed on a VideoSource CONFIGURATION token, which is the
    /// one carried by a media profile — not the VideoSource token GetVideoSources
    /// returns. Passing the wrong one makes a strict camera answer with an empty
    /// list or a fault, so when no profile has been read the token is left out
    /// entirely and the camera returns every overlay it has.</summary>
    private async Task<string?> OsdScopeAsync(CancellationToken ct)
    {
        var profiles = _profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false);
        var token = profiles == null ? null
            : OwnChannelProfiles(profiles).Select(p => p.VideoSourceToken).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        // Unscoped, a multi-channel device answers with every channel's overlays.
        if (token == null && MultiChannel(profiles)) return null;
        return token == null ? "" : $"<{Mp}:ConfigurationToken>{Esc(token)}</{Mp}:ConfigurationToken>";
    }

    /// <summary>Rewrites one overlay, read with <see cref="TryGetOsdsAsync"/>. Null
    /// fields are left as the camera has them; like the encoder, the whole object
    /// goes back — which is why the overlay is passed in rather than looked up, so
    /// a two-field change costs one read and not three.</summary>
    public async Task SetOsdAsync(OnvifOsd osd, string? position, string? plainText,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false))
                throw new NotSupportedException("the camera's ONVIF media service is not reachable");
            var edited = new XElement(osd.Element);
            if (position is { Length: > 0 })
                SetLocal(edited, "Position", "Type", position);
            if (plainText != null)
                SetLocal(edited, "TextString", "PlainText", plainText);

            var body = Echo(XName.Get("OSD", MediaNs), edited);

            var xml = await CallAsync(MediaUrl, MediaNs, "SetOSD", body.ToString(), ct)
                .ConfigureAwait(false);
            if (xml == null)
                throw Refused("the camera did not confirm the ONVIF overlay change");
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------ maintenance

    /// <summary>Restarts the camera. Throws when ONVIF cannot be reached — the user
    /// pressed a button and is owed an answer either way.</summary>
    public async Task RebootAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false))
                throw new NotSupportedException("the camera's ONVIF device service is not reachable");
            var xml = await CallAsync(_deviceUrl, NsDevice, "SystemReboot", "", ct).ConfigureAwait(false);
            if (xml == null)
                throw Refused("the camera did not accept the ONVIF reboot");
            // A rebooting camera drops every endpoint it just told us about, so all
            // of it is forgotten — not just the profiles. The cooldown goes with it:
            // re-probing a camera that is mid-boot only produces noise.
            _ready = false;
            _profiles = null;
            _streamUris.Clear();
            _streamUrisRetryAt = DateTime.MinValue;
            lock (_snapshotUris)
            {
                _snapshotUris.Clear();
                _snapshotAskAgainAt.Clear();
            }
            CellWritesIgnored = false;
            _encoderOptions.Clear();
            _presetTokens = Array.Empty<string>();
            _ptzProfileToken = null;
            _ptzNode = null;
            _ptzNodeRefused = false;
            _ptzNodeToken = null;
            _videoSourceToken = null;
            _ranges = null;
            _hasImaging = false;
            _failLogged = false;
            // Long enough for a camera to come back from a restart, not the five
            // minutes reserved for "this camera has no ONVIF" — a camera that
            // reboots in 40s would otherwise lose its panel for five and its
            // detections for up to ten. Misses in the minutes after are retried
            // quickly too (see Fail).
            _rebootedAt = DateTime.UtcNow;
            _retryAfter = DateTime.UtcNow + AfterReboot;
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------ shared plumbing

    /// <summary>The read shape every optional lookup shares: take the gate, make
    /// sure discovery ran, and turn any failure into "this camera doesn't offer
    /// that" rather than an exception into a feature sweep.</summary>
    private async Task<T?> GuardedAsync<T>(Func<Task<T?>> read, string what, CancellationToken ct)
        where T : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false)) return null;
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"{_tag}: ONVIF {what} read failed: {Log.Flatten(ex)}");
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The exception for an ONVIF write the camera would not confirm. The
    /// reason the transport recorded goes to the LOG, not into the message: it
    /// carries the endpoint URL and a slice of the camera's raw reply, and this
    /// message reaches any signed-in user through the API's error body.</summary>
    private IOException Refused(string what)
    {
        if (_lastError is { Length: > 0 } why) Log.Info($"{_tag}: {what} — {why}");
        return new IOException(what);
    }

    /// <summary>GetProfiles, kept out of the guarded path because discovery itself
    /// calls it while already holding the gate.</summary>
    private async Task<IReadOnlyList<OnvifProfile>?> ReadProfilesAsync(CancellationToken ct)
    {
        var xml = await CallAsync(MediaUrl, MediaNs, "GetProfiles",
            _media2 ? "<tr2:Type>All</tr2:Type>" : "", ct).ConfigureAwait(false);
        // ver10 said nothing: this may be a camera that only speaks Media2. Tried
        // once per failure, and only here — a camera whose ver10 answers never gets
        // this far, so a Reolink pays nothing for it.
        if (xml == null && !_media2 && await FindMedia2Async(ct).ConfigureAwait(false) is { } url)
        {
            var m2 = await CallAsync(url, NsMedia2, "GetProfiles", "<tr2:Type>All</tr2:Type>", ct)
                .ConfigureAwait(false);
            if (m2 != null && ParseProfiles(m2).Count > 0)
            {
                _media2 = true;
                _media2Url = url;
                xml = m2;
                Log.Info($"{_tag}: ONVIF media answered only in its newer (Media2) dialect — using that");
            }
        }
        var profiles = xml == null ? null : ParseProfiles(xml);
        if (profiles is { Count: > 0 })
        {
            _hadProfiles = true;
            _profilesAt = DateTime.UtcNow;
            // Every PTZ call is addressed to a media profile, so the head is only
            // known to exist once the profiles have been read.
            SelectPtzProfile(profiles);
        }
        return profiles;
    }

    /// <summary>The Media2 service's URL: from the device's service table when it
    /// lists one (GetServices is the only call that does — GetCapabilities predates
    /// Media2), else the paths vendors conventionally serve it on. Asked at most
    /// once per run; null when the camera has no Media2 either.</summary>
    private async Task<string?> FindMedia2Async(CancellationToken ct)
    {
        if (_media2Probed) return _media2Candidate;
        var (services, status) = await SendAsync(_deviceUrl, NsDevice, "GetServices",
            "<tds:IncludeCapability>false</tds:IncludeCapability>", ct).ConfigureAwait(false);
        _media2Candidate = NormalizeXAddr(ServiceXAddrByNs(services, NsMedia2));
        if (_media2Candidate == null && services == null)
            // No service table to consult either: the vendors' own conventions.
            foreach (var leaf in new[] { "media2_service", "Media2" })
            {
                var url = Conventional(leaf);
                if (await CallAsync(url, NsMedia2, "GetProfiles", "<tr2:Type>All</tr2:Type>", ct)
                        .ConfigureAwait(false) != null)
                {
                    _media2Candidate = url;
                    break;
                }
            }
        // Only an ANSWER is remembered. A camera that did not reply at all (still
        // booting, a blip) is asked again next time — a Media2-only one would
        // otherwise have no profiles for the rest of the run.
        _media2Probed = _media2Candidate != null || status != 0;
        return _media2Candidate;
    }

    private bool _media2Probed;
    private string? _media2Candidate;

    /// <summary>A velocity component as ONVIF wants it: a fraction of full speed,
    /// clamped to the generic -1..1 space every Profile S camera accepts.</summary>
    private static string Num(double v) =>
        Math.Clamp(v, -1, 1).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Sets <paramref name="child"/> inside <paramref name="parent"/>'s
    /// named block, creating neither: a field the camera did not report is a field
    /// it does not have, and inventing one is how a whole-object write gets refused.</summary>
    private static void SetLocal(XElement root, string block, string child, string value)
    {
        var b = root.Elements().FirstOrDefault(e => e.Name.LocalName == block);
        var c = b?.Elements().FirstOrDefault(e => e.Name.LocalName == child);
        if (c != null) c.Value = value;
    }

    // ------------------------------------------------------------ parsing

    /// <summary>The UTC instant in a GetSystemDateAndTime reply, or null when the
    /// camera reported none (it may legitimately answer with local time only, or
    /// with a DateTimeType of Manual and no UTC block).</summary>
    internal static DateTime? ParseUtcTime(XElement root)
    {
        var utc = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
        var date = utc?.Elements().FirstOrDefault(e => e.Name.LocalName == "Date");
        var time = utc?.Elements().FirstOrDefault(e => e.Name.LocalName == "Time");
        if (date == null || time == null) return null;
        int? N(XElement p, string n) =>
            int.TryParse(p.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (N(date, "Year") is not { } y || N(date, "Month") is not { } mo || N(date, "Day") is not { } d
            || N(time, "Hour") is not { } h || N(time, "Minute") is not { } mi || N(time, "Second") is not { } s)
            return null;
        try { return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return null; } // a camera with a nonsense clock
    }

    internal static OnvifDeviceInfo ParseDeviceInfo(XElement root)
    {
        string? V(string name) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() is { Length: > 0 } s
                ? s : null;
        return new OnvifDeviceInfo(V("Manufacturer"), V("Model"), V("FirmwareVersion"),
            V("SerialNumber"), V("HardwareId"));
    }

    /// <summary>The XAddr of the service with a given namespace, from a GetServices
    /// reply (the ver10 alternative to GetCapabilities).</summary>
    internal static string? ServiceXAddrByNs(XElement? root, string ns)
    {
        foreach (var svc in root?.Descendants().Where(e => e.Name.LocalName == "Service")
                            ?? Enumerable.Empty<XElement>())
        {
            var ns2 = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "Namespace")?.Value?.Trim();
            if (!string.Equals(ns2, ns, StringComparison.OrdinalIgnoreCase)) continue;
            var xaddr = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(xaddr)) return xaddr;
        }
        return null;
    }

    internal static List<OnvifProfile> ParseProfiles(XElement root)
    {
        var list = new List<OnvifProfile>();
        foreach (var p in root.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            var token = p.Attribute("token")?.Value;
            if (string.IsNullOrWhiteSpace(token)) continue;
            var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
            // ver10 hangs each configuration directly off the profile; Media2 groups
            // them under Configurations and drops the "Configuration" suffix from
            // their names. Both are looked for, so one parse serves both dialects.
            var parts = p.Elements()
                .Concat(p.Elements().Where(e => e.Name.LocalName == "Configurations").SelectMany(c => c.Elements()))
                .ToList();
            XElement? Part(params string[] names) =>
                parts.FirstOrDefault(e => names.Contains(e.Name.LocalName));
            var encoder = Part("VideoEncoderConfiguration", "VideoEncoder");
            var source = Part("VideoSourceConfiguration", "VideoSource");
            var analytics = Part("VideoAnalyticsConfiguration", "Analytics");
            var ptzConfig = Part("PTZConfiguration", "PTZ");
            var sourceToken = source?.Elements().FirstOrDefault(e => e.Name.LocalName == "SourceToken")?.Value?.Trim();
            var nodeToken = ptzConfig?.Elements().FirstOrDefault(e => e.Name.LocalName == "NodeToken")?.Value?.Trim();
            list.Add(new OnvifProfile(token.Trim(), string.IsNullOrWhiteSpace(name) ? token.Trim() : name,
                ptzConfig != null, source?.Attribute("token")?.Value, encoder,
                SourceToken: string.IsNullOrEmpty(sourceToken) ? null : sourceToken,
                AnalyticsToken: analytics?.Attribute("token")?.Value,
                PtzNodeToken: string.IsNullOrEmpty(nodeToken) ? null : nodeToken));
        }
        return list;
    }

    /// <summary>What one encoder configuration accepts, read from the block for the
    /// codec that configuration is actually using. The reply carries a block per
    /// codec (H264, H265, the legacy JPEG one), and they do not agree: taking every
    /// ResolutionsAvailable in the document would offer an H264 profile the
    /// resolutions only its H265 sibling supports, and the camera would refuse the
    /// write. <paramref name="encoding"/> is the profile's own Encoding; when it is
    /// unknown or absent from the reply, the whole document is used as before.</summary>
    internal static OnvifEncoderOptions ParseEncoderOptions(XElement root, string? encoding = null)
    {
        // ver10 nests each codec's options in an element NAMED for it (<H264>);
        // Media2 sends one <Options> per codec with the codec in an <Encoding> child.
        var scope = encoding is { Length: > 0 }
            ? root.Descendants().FirstOrDefault(e =>
                  string.Equals(e.Name.LocalName, encoding, StringComparison.OrdinalIgnoreCase))
              ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Options"
                  && string.Equals(e.Elements().FirstOrDefault(c => c.Name.LocalName == "Encoding")?.Value?.Trim(),
                      encoding, StringComparison.OrdinalIgnoreCase))
              ?? root
            : root;
        var resolutions = new List<(int Width, int Height)>();
        foreach (var r in scope.Descendants().Where(e => e.Name.LocalName == "ResolutionsAvailable"))
        {
            int w = Int(r, "Width"), h = Int(r, "Height");
            if (w > 0 && h > 0 && !resolutions.Contains((w, h))) resolutions.Add((w, h));
        }
        resolutions.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
        // The bitrate range usually hides in an Extension block OUTSIDE the codec
        // block, so it is looked for in the whole document when the codec block has
        // none of its own.
        // The bitrate range usually sits in an Extension block named for the codec
        // (Extension/H264), and the schema puts Extension/JPEG before it — so the
        // codec's own Extension is asked before "the first one in the document".
        var extension = encoding is { Length: > 0 }
            ? root.Descendants().FirstOrDefault(e =>
                  string.Equals(e.Name.LocalName, encoding, StringComparison.OrdinalIgnoreCase)
                  && e.Parent?.Name.LocalName == "Extension")
            : null;
        return new OnvifEncoderOptions(resolutions,
            Range(scope, "FrameRateRange") ?? Range(root, "FrameRateRange") ?? RatesSupported(scope),
            Range(scope, "BitrateRange") ?? (extension == null ? null : Range(extension, "BitrateRange"))
                ?? Range(root, "BitrateRange"),
            RatesListed(scope));

        // Media2's list, kept as the menu itself when it names whole rates: a camera
        // listing "25 12.5 6.25" takes those, not everything the range spans.
        static IReadOnlyList<int>? RatesListed(XElement where)
        {
            var list = where.Attribute("FrameRatesSupported")?.Value;
            if (string.IsNullOrWhiteSpace(list)) return null;
            var whole = list.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
                .Where(d => d >= 1 && Math.Abs(d - Math.Round(d)) < 0.001)
                .Select(d => (int)Math.Round(d)).Distinct().OrderBy(v => v).ToList();
            return whole.Count > 0 ? whole : null;
        }

        // Media2 lists the frame rates it will take ("25 20 15 12.5") instead of a
        // range; the spread of that list is the range the panel's menu stays within.
        static (int, int)? RatesSupported(XElement where)
        {
            var list = where.Attribute("FrameRatesSupported")?.Value;
            if (string.IsNullOrWhiteSpace(list)) return null;
            var rates = list.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
                .Where(d => d > 0).ToList();
            if (rates.Count == 0) return null;
            int lo = (int)Math.Floor(rates.Min()), hi = (int)Math.Ceiling(rates.Max());
            return hi > lo ? (lo, hi) : (Math.Max(1, lo - 1), hi);
        }

        static int Int(XElement parent, string name) =>
            int.TryParse(parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        static (int, int)? Range(XElement where, string name)
        {
            var el = where.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            if (el == null) return null;
            int min = Int(el, "Min"), max = Int(el, "Max");
            return max > min && max > 0 ? (min, max) : null;
        }
    }

    /// <summary>The capabilities of the PTZ node a profile drives — the one named by
    /// <paramref name="nodeToken"/>, else the first. A multi-sensor camera has one
    /// node per head, and they differ. Spaces are recognised by element name, which
    /// the schema fixes; among several absolute zoom spaces the generic one is
    /// preferred, since that is the one every camera must accept.</summary>
    internal static OnvifPtzNode? ParsePtzNode(XElement root, string? nodeToken = null)
    {
        var nodes = root.Descendants().Where(e => e.Name.LocalName == "PTZNode").ToList();
        var node = nodes.FirstOrDefault(n => nodeToken != null && (string?)n.Attribute("token") == nodeToken)
                   ?? nodes.FirstOrDefault();
        if (node == null) return null;
        var spaces = node.Descendants().FirstOrDefault(e => e.Name.LocalName == "SupportedPTZSpaces");
        bool Has(string name) => spaces?.Elements().Any(e => e.Name.LocalName == name) == true;
        var zoomSpaces = spaces?.Elements().Where(e => e.Name.LocalName == "AbsoluteZoomPositionSpace").ToList()
                         ?? new List<XElement>();
        static string? SpaceUri(XElement s) =>
            s.Elements().FirstOrDefault(e => e.Name.LocalName == "URI")?.Value?.Trim();
        var zoomSpace = zoomSpaces.FirstOrDefault(s =>
                            SpaceUri(s)?.EndsWith("PositionGenericSpace", StringComparison.Ordinal) == true)
                        ?? zoomSpaces.FirstOrDefault();
        var xr = zoomSpace?.Elements().FirstOrDefault(e => e.Name.LocalName == "XRange");
        double D(string n, double fallback) =>
            double.TryParse(xr?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var range = (Min: D("Min", 0), Max: D("Max", 1));
        if (range.Max <= range.Min) range = (0, 1);
        var max = int.TryParse(node.Elements().FirstOrDefault(e => e.Name.LocalName == "MaximumNumberOfPresets")
            ?.Value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp) && mp > 0 ? mp : (int?)null;
        return new OnvifPtzNode(
            // The arrows send ContinuousMove, so only a continuous space makes them work (Frigate asks the same).
            PanTilt: Has("ContinuousPanTiltVelocitySpace"),
            Zoom: zoomSpace != null,
            ZoomRange: range,
            MaxPresets: max,
            ZoomSpace: zoomSpace == null ? null : SpaceUri(zoomSpace) is { Length: > 0 } u ? u : null,
            AnyZoom: spaces?.Elements().Any(e => e.Name.LocalName.Contains("Zoom", StringComparison.Ordinal)) == true);
    }

    /// <summary>Whether the lens is still on its way (MoveStatus/Zoom), or null when
    /// the camera does not say.</summary>
    internal static bool? ParseZoomMoving(XElement root)
    {
        var move = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "MoveStatus");
        var zoom = move?.Elements().FirstOrDefault(e => e.Name.LocalName == "Zoom")?.Value?.Trim();
        return zoom switch
        {
            null or "" => null,
            "MOVING" => true,
            _ => false, // IDLE, or UNKNOWN: nothing to wait for
        };
    }

    /// <summary>The zoom coordinate in a GetStatus reply (Position/Zoom x=), or null
    /// when the camera reports no position — some only report their move status.</summary>
    internal static double? ParseZoomPosition(XElement root)
    {
        var position = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Position");
        var zoom = position?.Elements().FirstOrDefault(e => e.Name.LocalName == "Zoom");
        return double.TryParse(zoom?.Attribute("x")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            ? x : null;
    }

    internal static List<OnvifPreset> ParsePresets(XElement root)
    {
        var list = new List<OnvifPreset>();
        foreach (var p in root.Descendants().Where(e => e.Name.LocalName == "Preset"))
        {
            var token = p.Attribute("token")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(token)) continue;
            var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
            list.Add(new OnvifPreset(token, string.IsNullOrWhiteSpace(name) ? token : name));
        }
        return list;
    }

    internal static List<OnvifOsd> ParseOsds(XElement root)
    {
        var list = new List<OnvifOsd>();
        foreach (var o in root.Descendants().Where(e => e.Name.LocalName == "OSDs"))
        {
            var token = o.Attribute("token")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(token)) continue;
            var text = o.Elements().FirstOrDefault(e => e.Name.LocalName == "TextString");
            if (text == null) continue; // an image overlay has nothing to edit here
            var position = o.Elements().FirstOrDefault(e => e.Name.LocalName == "Position")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Type")?.Value?.Trim();
            var kind = text.Elements().FirstOrDefault(e => e.Name.LocalName == "Type")?.Value?.Trim();
            var plain = text.Elements().FirstOrDefault(e => e.Name.LocalName == "PlainText")?.Value;
            list.Add(new OnvifOsd(token, position, kind, plain, o));
        }
        return list;
    }
}
