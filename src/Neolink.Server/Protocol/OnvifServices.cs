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

/// <summary>One saved PTZ position. Token is the camera's handle for it.</summary>
public sealed record OnvifPreset(string Token, string Name);

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

    /// <summary>The camera's media profiles as discovery read them, or null when
    /// ONVIF is unavailable. Read once per run — profiles are configuration, and a
    /// write refreshes them.</summary>
    public async Task<IReadOnlyList<OnvifProfile>?> TryGetProfilesAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            if (_profiles == null)
                _profiles = await ReadProfilesAsync(ct).ConfigureAwait(false);
            return _profiles;
        }, "media profiles", ct).ConfigureAwait(false);

    /// <summary>Whether this camera answered with a moving head — PTZ calls are
    /// addressed to a profile, so this only means anything once the profiles have
    /// been read (TryGetProfilesAsync).</summary>
    public bool HasPtz => _ptzProfileToken != null;

    /// <summary>Each profile's RTSP URL as the camera reports it, keyed by profile
    /// token — how a configured stream URL is matched to the profile behind it.
    /// Null when ONVIF is unavailable; a profile the camera declines to answer for
    /// is simply absent. Read once per run.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> TryGetStreamUrisAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            if (_streamUris != null) return _streamUris;
            var profiles = _profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false);
            if (profiles == null) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in profiles)
            {
                // Only the URL is wanted here, to match it to a configured stream;
                // cameras answer the same path whichever transport is named. Media2's
                // RtspUnicast is the one every Profile T camera must support; ver10
                // spells out a StreamSetup. Both answer with a Uri the parse finds.
                var xml = await CallAsync(MediaUrl, MediaNs, "GetStreamUri", _media2
                    ? $"<tr2:Protocol>RtspUnicast</tr2:Protocol><tr2:ProfileToken>{Esc(p.Token)}</tr2:ProfileToken>"
                    : "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream>" +
                      "<tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
                      $"<trt:ProfileToken>{Esc(p.Token)}</trt:ProfileToken>", ct).ConfigureAwait(false);
                var uri = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(uri)) map[p.Token] = uri;
            }
            // Only remembered when EVERY profile answered: a partial map read during
            // a blip would otherwise be cached for the run, and the profiles it is
            // missing would fall back to size-ordered guessing for good.
            if (map.Count == profiles.Count) _streamUris = map;
            return map;
        }, "stream URIs", ct).ConfigureAwait(false);

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
    public async Task<byte[]?> TrySnapshotAsync(CancellationToken ct)
    {
        try
        {
            var uri = await SnapshotUriAsync(ct).ConfigureAwait(false);
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
                // A URI that answers 401/404 is not going to start working; forget it
                // and let the caller fall back rather than re-fetching every poll.
                if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                    _snapshotUri = "";
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            return bytes is { Length: > 100 } && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
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
    public bool HasSnapshotUri => _snapshotUri is { Length: > 0 };

    /// <summary>A still is a few hundred KB at most; a camera slower than this to
    /// hand one over is better answered by the last frame on hand.</summary>
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The snapshot URI for the first profile that offers one, asked once
    /// per run. An empty string is the remembered "this camera has none".</summary>
    private async Task<string?> SnapshotUriAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            if (_snapshotUri != null) return _snapshotUri.Length == 0 ? null : _snapshotUri;
            var profiles = _profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false);
            if (profiles == null) return null; // could not ask — try again later
            foreach (var p in profiles)
            {
                var xml = await CallAsync(MediaUrl, MediaNs, "GetSnapshotUri",
                    $"<{Mp}:ProfileToken>{Esc(p.Token)}</{Mp}:ProfileToken>", ct).ConfigureAwait(false);
                var uri = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(uri)) continue;
                // As with the service table, a camera reporting its own idea of its
                // hostname must still be reachable from here.
                _snapshotUri = NormalizeXAddr(uri);
                Log.Info($"{_tag}: ONVIF snapshot available — stills come from the camera rather than its video");
                return _snapshotUri;
            }
            _snapshotUri = ""; // asked, and it has none
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
            var xml = await CallAsync(MediaUrl, MediaNs, "GetVideoEncoderConfigurationOptions",
                $"<{Mp}:ConfigurationToken>{Esc(configToken)}</{Mp}:ConfigurationToken>" +
                $"<{Mp}:ProfileToken>{Esc(profileToken)}</{Mp}:ProfileToken>", ct).ConfigureAwait(false);
            return xml == null ? null : ParseEncoderOptions(xml, encoding);
        }, "encoder options", ct).ConfigureAwait(false);

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
            var profile = (_profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false))
                ?.FirstOrDefault(p => p.Token == profileToken)
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
            var body = new XElement(XName.Get("Configuration", MediaNs));
            foreach (var a in edited.Attributes()) body.SetAttributeValue(a.Name, a.Value);
            body.Add(edited.Elements().Select(e => new XElement(e)));

            // Media2's setter takes the configuration alone: ForcePersistence is a
            // ver10 element, and a strict Media2 parser faults on one it does not know.
            var xml = await CallAsync(MediaUrl, MediaNs, "SetVideoEncoderConfiguration",
                _media2 ? body.ToString() : body + "<trt:ForcePersistence>true</trt:ForcePersistence>", ct)
                .ConfigureAwait(false);
            if (xml == null)
                throw Refused("the camera did not confirm the ONVIF encoder change");
            _profiles = null; // re-read on the next look: the camera is the authority
        }
        finally { _gate.Release(); }
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
            if (_ptzProfileToken == null) return null;
            var (xml, status) = await SendAsync(_ptzUrl!, NsPtz, "GetNodes", "", ct).ConfigureAwait(false);
            _ptzNode = xml == null ? null : ParsePtzNode(xml, _ptzNodeToken);
            // A reply without a usable node, or a refusal the camera will repeat,
            // is its answer; silence, a busy 503 or an auth hiccup are asked again.
            _ptzNodeRefused = _ptzNode == null && (xml != null || IsLastingRefusal(status));
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
            if (_ptzProfileToken is not { } profile) return null;
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
            if (_ptzProfileToken is not { } profile) return null;
            var xml = await CallAsync(_ptzUrl!, NsPtz, "GetPresets",
                $"<tptz:ProfileToken>{Esc(profile)}</tptz:ProfileToken>", ct).ConfigureAwait(false);
            if (xml == null) return null;
            var presets = ParsePresets(xml);
            _presetTokens = presets.Select(p => p.Token).ToList();
            return presets;
        }, "PTZ presets", ct).ConfigureAwait(false);

    /// <summary>Drives to the saved position at 1-based <paramref name="index"/> of
    /// the list <see cref="TryGetPresetsAsync"/> last returned.</summary>
    public async Task GotoPresetAsync(int index, CancellationToken ct)
    {
        var token = await PresetTokenAsync(index, ct).ConfigureAwait(false)
            ?? throw new NotSupportedException($"the camera has no preset {index}");
        await PtzAsync("GotoPreset", $"<tptz:PresetToken>{Esc(token)}</tptz:PresetToken>", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Saves where the camera points now. An index inside the known list
    /// overwrites that preset; anything past it asks the camera for a new one.</summary>
    public async Task SavePresetAsync(int index, string name, CancellationToken ct)
    {
        var token = await PresetTokenAsync(index, ct).ConfigureAwait(false);
        await PtzAsync("SetPreset",
            $"<tptz:PresetName>{Esc(name)}</tptz:PresetName>" +
            (token == null ? "" : $"<tptz:PresetToken>{Esc(token)}</tptz:PresetToken>"), ct)
            .ConfigureAwait(false);
        _presetTokens = Array.Empty<string>(); // a new token is only knowable by re-reading
    }

    private async Task<string?> PresetTokenAsync(int index, CancellationToken ct)
    {
        if (_presetTokens.Count == 0) await TryGetPresetsAsync(ct).ConfigureAwait(false);
        return index >= 1 && index <= _presetTokens.Count ? _presetTokens[index - 1] : null;
    }

    /// <summary>One PTZ operation against the discovered profile. Throws when the
    /// camera has no PTZ or refuses the command — every caller is a user action.</summary>
    private async Task PtzAsync(string op, string innerBody, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false) || _ptzProfileToken == null)
                throw new NotSupportedException("the camera's ONVIF PTZ service is not reachable");
            var xml = await CallAsync(_ptzUrl!, NsPtz, op,
                $"<tptz:ProfileToken>{Esc(_ptzProfileToken)}</tptz:ProfileToken>{innerBody}", ct)
                .ConfigureAwait(false);
            if (xml == null)
                throw Refused($"the camera refused the ONVIF {op}");
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------ OSD

    /// <summary>The camera's text overlays, or null when it exposes none. Image
    /// overlays are left out: there is nothing here to edit about them.</summary>
    public async Task<IReadOnlyList<OnvifOsd>?> TryGetOsdsAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            var scope = await OsdScopeAsync(ct).ConfigureAwait(false); // may switch the dialect
            var xml = await CallAsync(MediaUrl, MediaNs, "GetOSDs", scope, ct)
                .ConfigureAwait(false);
            return xml == null ? null : ParseOsds(xml);
        }, "OSDs", ct).ConfigureAwait(false);

    /// <summary>GetOSDs is keyed on a VideoSource CONFIGURATION token, which is the
    /// one carried by a media profile — not the VideoSource token GetVideoSources
    /// returns. Passing the wrong one makes a strict camera answer with an empty
    /// list or a fault, so when no profile has been read the token is left out
    /// entirely and the camera returns every overlay it has.</summary>
    private async Task<string> OsdScopeAsync(CancellationToken ct)
    {
        var profiles = _profiles ?? await ReadProfilesAsync(ct).ConfigureAwait(false);
        var token = profiles?.Select(p => p.VideoSourceToken).FirstOrDefault(t => !string.IsNullOrEmpty(t));
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

            var body = new XElement(XName.Get("OSD", MediaNs));
            foreach (var a in edited.Attributes()) body.SetAttributeValue(a.Name, a.Value);
            body.Add(edited.Elements().Select(e => new XElement(e)));

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
            _streamUris = null;
            _snapshotUri = null;
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
            // detections for up to ten.
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
            // Every PTZ call is addressed to a media profile, so the head is only
            // known to exist once the profiles have been read.
            var ptzProfile = profiles.FirstOrDefault(p => p.HasPtz);
            _ptzProfileToken = ptzProfile?.Token;
            _ptzNodeToken = ptzProfile?.PtzNodeToken;
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
            PanTilt: Has("ContinuousPanTiltVelocitySpace") || Has("RelativePanTiltTranslationSpace")
                     || Has("AbsolutePanTiltPositionSpace"),
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
