// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Xml.Linq;
using Neolink.Bc.Xml;
using Neolink.Protocol;

namespace Neolink.Streaming;

/// <summary>
/// The control surface of a non-Reolink camera. It always streams; everything
/// else it can do, it does over ONVIF — the standard every IP camera worth
/// buying speaks, and the only management protocol Neolink can assume here.
///
/// What appears in the panel is exactly what the camera's own ONVIF services
/// answered for: device identity, the media profiles' encoder settings, the
/// imaging sliders, a moving head with its saved positions, the text overlays'
/// placement, and reboot. A camera with no ONVIF at all (or with it switched
/// off) behaves as this class always did — it streams, and that is all — except
/// that it still gets a detection zone, which Neolink stores on its behalf.
///
/// Every read is best-effort and yields null rather than throwing into a feature
/// sweep; every write throws, because a write is a user action owed an answer.
/// </summary>
public sealed class GenericCameraControl : ICameraControl
{
    /// <summary>The camera's own encoder settings are asked for at most this often:
    /// the panel re-reads on every open, and a SOAP round trip per section is the
    /// one cost this surface can actually feel.</summary>
    private static readonly TimeSpan ProfileCacheFor = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<RtspCameraService> _services;
    private readonly OnvifClient? _onvif;
    /// <summary>The streams Neolink pulls, in config order: the hub name it mounts
    /// them under and the URL it pulls. Used to decide WHICH ONVIF profile is the
    /// one behind "mainStream" — the panel highlights the live profile by that name.</summary>
    private readonly IReadOnlyList<(string Kind, string Url)> _streams;

    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private IReadOnlyList<(string Kind, OnvifProfile Profile)>? _bound;
    private DateTime _boundAt;

    public GenericCameraControl(string cameraName, IReadOnlyList<RtspCameraService> services,
        OnvifClient? onvif = null, IReadOnlyList<(string Kind, string Url)>? streams = null)
    {
        CameraName = cameraName;
        _services = services;
        _onvif = onvif;
        _streams = streams ?? Array.Empty<(string, string)>();
    }

    public string CameraName { get; }

    public bool Online => _services.Any(s => s.Online);

    /// <summary>Every settings panel on this camera is ONVIF's doing, which changes
    /// what the UI may offer (ONVIF can move an overlay but not switch it off, and
    /// there is no Baichuan service table to show). True even when ONVIF turns out
    /// to be unreachable: what this says is that there is no OTHER way in.</summary>
    public bool OnvifOnly => true;

    public bool HasImagingFallback => _onvif?.HasImaging == true;

    /// <summary>Only offered once ONVIF has actually answered: a button that can
    /// only fail is worse than no button. The capability probe reads the device
    /// information first, so this is decided by the time the panel asks.</summary>
    public bool CanReboot => _onvif?.Ready == true;

    private static readonly CameraCapabilities NoCapabilities = new(null, null,
        new CameraFeatures(Ptz: false, Led: false, Pir: false, Battery: false, Talk: false));

    /// <summary>Discovered once and kept, exactly as the Baichuan surface does. This
    /// is not an optimisation: capabilities are asked for on every Home Assistant
    /// refresh tick and every time emergency mode arms, both of which walk the
    /// cameras one after another — so a camera that answers slowly here delays every
    /// camera behind it, Reolink ones included.</summary>
    private CameraCapabilities? _caps;

    public async Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        if (_onvif == null) return NoCapabilities;
        ReprobeZoneIfDue();
        if (_caps is { } cached) return cached;
        var info = await _onvif.TryGetDeviceInfoAsync(ct).ConfigureAwait(false);
        // HasPtz only means something once the profiles have been read, and reading
        // them is what this surface wants anyway.
        await _onvif.TryGetProfilesAsync(ct).ConfigureAwait(false);
        // A PTZ configuration only says there is a head; the node says which axes it
        // drives. When the node cannot be read the head is assumed to pan and tilt —
        // which is what every camera got before the node was asked — and nothing is
        // assumed about zoom, whose slider needs a position to start from.
        var node = _onvif.HasPtz ? await _onvif.TryGetPtzNodeAsync(ct).ConfigureAwait(false) : null;
        var caps = new CameraCapabilities(ToVersion(info), null,
            new CameraFeatures(Ptz: _onvif.HasPtz && (node?.PanTilt ?? true), Led: false, Pir: false,
                Battery: false, Talk: false, Zoom: node?.Zoom == true));
        // Only a camera that actually answered is worth remembering: caching a
        // silence would leave it without settings until Neolink restarts.
        if (_onvif.Ready && (!_onvif.HasPtz || _onvif.PtzNodeSettled)) _caps = caps;
        return caps;
    }

    /// <summary>ONVIF's device information in the shape the panel's identity strip
    /// reads. The make is folded into the model, which is the line it shows big.</summary>
    internal static VersionInfoXml? ToVersion(OnvifDeviceInfo? info)
    {
        if (info == null) return null;
        var model = string.Join(" ", new[] { info.Manufacturer, info.Model }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (model.Length == 0 && info.Firmware == null && info.Serial == null) return null;
        return new VersionInfoXml
        {
            Name = model,
            Model = model,
            FirmwareVersion = info.Firmware ?? "",
            SerialNumber = info.Serial ?? "",
            HardwareVersion = info.HardwareId ?? "",
        };
    }

    // ------------------------------------------------------------ streams

    public async Task<StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct)
    {
        var bound = await BoundProfilesAsync(ct).ConfigureAwait(false);
        if (bound is not { Count: > 0 }) return null;
        var info = new StreamInfoXml();
        foreach (var (kind, profile) in bound)
        {
            var options = _onvif == null || profile.EncoderToken == null ? null
                : await _onvif.TryGetEncoderOptionsAsync(profile.Token, profile.EncoderToken, ct,
                        profile.Encoding)
                    .ConfigureAwait(false);
            // The camera's current resolution is always offered, even when it is
            // missing from the options list (some firmwares report a short one).
            var resolutions = new List<(int Width, int Height)>(options?.Resolutions ?? Array.Empty<(int, int)>());
            if (profile.Width > 0 && profile.Height > 0 && !resolutions.Contains((profile.Width, profile.Height)))
                resolutions.Insert(0, (profile.Width, profile.Height));
            if (resolutions.Count == 0) continue;
            // A camera that LISTS its rates (Media2) is offered exactly those; one
            // that gives a range gets the everyday values inside it.
            var framerates = options?.FrameRatesListed is { Count: > 0 } listed
                ? StepsWithin(listed.ToArray(), null, profile.FrameRate)
                : StepsWithin(FramerateSteps, options?.FrameRate, profile.FrameRate);
            var bitrates = StepsWithin(BitrateSteps, options?.Bitrate, profile.Bitrate);
            foreach (var (w, h) in resolutions)
                info.EncodeTables.Add(new EncodeTableXml
                {
                    Type = kind,
                    Width = (uint)w,
                    Height = (uint)h,
                    // A camera that reports no RateControl would otherwise show "0
                    // fps" / "0 kbps" as its live setting; the middle of what it
                    // will accept is a guess, but it is at least a usable one.
                    DefaultFramerate = (uint)(profile.FrameRate > 0 ? profile.FrameRate : Mid(framerates)),
                    DefaultBitrate = (uint)(profile.Bitrate > 0 ? profile.Bitrate : Mid(bitrates)),
                    FramerateTable = string.Join(" ", framerates),
                    BitrateTable = string.Join(" ", bitrates),
                });
        }
        if (info.EncodeTables.Count == 0) return null;
        var list = new StreamInfoListXml();
        list.StreamInfos.Add(info);
        return list;
    }

    /// <summary>ONVIF reports a RANGE where the panel wants a menu, so the menu is
    /// the everyday values inside that range, plus whatever the camera is actually
    /// set to (which a range can perfectly well exclude).</summary>
    internal static readonly int[] FramerateSteps = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 50, 60 };

    internal static readonly int[] BitrateSteps =
        { 64, 128, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384 };

    /// <summary>The middle of a menu, for a camera that reported no current value.</summary>
    private static int Mid(List<int> values) => values.Count == 0 ? 0 : values[values.Count / 2];

    internal static List<int> StepsWithin(int[] steps, (int Min, int Max)? range, int current)
    {
        var lo = range?.Min ?? 0;
        var hi = range?.Max ?? int.MaxValue;
        var values = steps.Where(v => v >= lo && v <= hi).ToList();
        if (current > 0 && !values.Contains(current)) values.Add(current);
        values.Sort();
        // A range so narrow that nothing everyday fits still has to offer its own
        // bounds, or the menu comes up empty and the section cannot be used.
        if (values.Count == 0 && range is { } r) values.AddRange(new[] { r.Min, r.Max }.Distinct());
        return values;
    }

    public bool CanSetStreamSettings => _onvif is { Ready: true, HasProfiles: true };

    public async Task<IReadOnlyList<StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct)
    {
        var bound = await BoundProfilesAsync(ct).ConfigureAwait(false);
        return bound?.Select(b => new StreamEncSetting(b.Kind,
            (uint)Math.Max(0, b.Profile.Width), (uint)Math.Max(0, b.Profile.Height),
            (uint)Math.Max(0, b.Profile.FrameRate), (uint)Math.Max(0, b.Profile.Bitrate))).ToList();
    }

    public async Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
        uint? framerate, uint? bitrate, CancellationToken ct)
    {
        if (_onvif == null)
            throw new NotSupportedException(
                $"stream settings need the camera's ONVIF service ('{CameraName}' has none)");
        var bound = await BoundProfilesAsync(ct).ConfigureAwait(false);
        // "Could not read the profiles just now" and "this camera has no such
        // stream" are different answers and must not share one message: the first
        // is a retry, the second is permanent, and reporting a blip as the second
        // sent people looking for a stream that is plainly on screen.
        if (bound == null)
            throw new IOException(
                $"{CameraName}: the camera's ONVIF media profiles could not be read just now — try again shortly");
        var profile = bound.FirstOrDefault(b => b.Kind == stream).Profile
            ?? throw new NotSupportedException($"{CameraName} has no ONVIF profile behind '{stream}'");
        await _onvif.SetVideoEncoderAsync(profile.Token, (int?)width, (int?)height,
            (int?)framerate, (int?)bitrate, ct).ConfigureAwait(false);
        Invalidate();
        Log.Info($"{CameraName}: {stream} encoder set over ONVIF" +
                 (width is { } w && height is { } h ? $" ({w}x{h})" : "") +
                 (framerate is { } f ? $" {f} fps" : "") +
                 (bitrate is { } b ? $" {b} kbps" : ""));
    }

    /// <summary>Which ONVIF profile sits behind each stream Neolink pulls. Matched
    /// on the stream URI the camera itself reports, because the profile ORDER means
    /// nothing: plenty of cameras list the sub-stream first. When no URI matches —
    /// a camera that reports them through a different host, or a hand-written URL —
    /// the profiles fall back to biggest-first, which is how the streams were
    /// configured in the first place.</summary>
    private async Task<IReadOnlyList<(string Kind, OnvifProfile Profile)>?> BoundProfilesAsync(
        CancellationToken ct)
    {
        if (_onvif == null || _streams.Count == 0) return null;
        if (_bound != null && DateTime.UtcNow - _boundAt < ProfileCacheFor) return _bound;
        await _profileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bound != null && DateTime.UtcNow - _boundAt < ProfileCacheFor) return _bound;
            var profiles = await _onvif.TryGetProfilesAsync(ct).ConfigureAwait(false);
            if (profiles is not { Count: > 0 }) return null;
            var uris = await _onvif.TryGetStreamUrisAsync(ct).ConfigureAwait(false);
            _bound = Bind(_streams, profiles, uris);
            _boundAt = DateTime.UtcNow;
            return _bound;
        }
        finally { _profileGate.Release(); }
    }

    private void Invalidate()
    {
        _bound = null;
        _boundAt = DateTime.MinValue;
    }

    internal static List<(string Kind, OnvifProfile Profile)> Bind(
        IReadOnlyList<(string Kind, string Url)> streams, IReadOnlyList<OnvifProfile> profiles,
        IReadOnlyDictionary<string, string>? uris)
    {
        var bound = new List<(string Kind, OnvifProfile Profile)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, url) in streams)
        {
            var match = profiles.FirstOrDefault(p => !taken.Contains(p.Token)
                && uris != null && uris.TryGetValue(p.Token, out var u) && SameStream(u, url));
            if (match == null) continue;
            taken.Add(match.Token);
            bound.Add((kind, match));
        }
        if (bound.Count == streams.Count) return bound;
        // Nothing (or not everything) matched: fall back to biggest-first over the
        // profiles still free, in the streams' own order — main before sub.
        var spare = profiles.Where(p => !taken.Contains(p.Token))
            .OrderByDescending(p => (long)p.Width * p.Height).ToList();
        int i = 0;
        foreach (var (kind, _) in streams)
        {
            if (bound.Any(b => b.Kind == kind)) continue;
            if (i >= spare.Count) break;
            bound.Add((kind, spare[i++]));
        }
        // Keep the config's order, so "mainStream" leads the panel's list.
        return streams.Select(s => bound.FirstOrDefault(b => b.Kind == s.Kind))
            .Where(b => b.Profile != null).ToList()!;
    }

    /// <summary>Whether an ONVIF stream URI and a configured RTSP URL name the same
    /// stream. Only the path and query are compared: the host can differ (the camera
    /// may report its own name, or a NAT address), and the credentials live in the
    /// configured URL alone.</summary>
    internal static bool SameStream(string? onvifUri, string? configured)
    {
        if (!Uri.TryCreate(onvifUri, UriKind.Absolute, out var a)
            || !Uri.TryCreate(configured, UriKind.Absolute, out var b))
            return false;
        // PATH only. Plenty of cameras append their own query to the URI they report
        // (session ids, transport hints) that nobody would ever have typed into the
        // config — comparing those would mean no profile ever matched and every
        // camera silently fell back to guessing by frame size.
        return string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ picture / OSD / presets

    public async Task<HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct)
    {
        if (_onvif == null) return null;
        var image = await GetImageSettingsAsync(ct).ConfigureAwait(false);
        var osd = await GetOsdSettingsAsync(ct).ConfigureAwait(false);
        var presets = await GetPtzPresetsAsync(ct).ConfigureAwait(false);
        if (image == null && osd == null && presets == null) return null;
        return new HttpFeatures(image, Volume: null, WifiSignal: null, PtzPresets: presets,
            QuickReplies: null, AutoTrack: null, SdCards: null, Osd: osd);
    }

    public async Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct)
    {
        if (_onvif == null) return null;
        var o = await _onvif.TryGetImagingAsync(ct).ConfigureAwait(false);
        if (o == null) return null;
        // Hue, anti-flicker and flip/mirror have no ONVIF imaging equivalent, and
        // HDR rides a setter this surface has no path for — reported absent so the
        // panel does not offer a control that cannot be applied.
        return new ImageSettings(o.Brightness, o.Contrast, o.Saturation, Hue: null, o.Sharpness,
            CameraControl.IrCutToDayNight(o.IrCutFilter), AntiFlicker: null,
            Flip: null, Mirror: null);
    }

    public async Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue,
        int? sharpen, string? dayNight, string? antiFlicker, bool? flip, bool? mirror,
        CancellationToken ct)
    {
        if (_onvif == null)
            throw new NotSupportedException(
                $"picture settings need the camera's ONVIF service ('{CameraName}' has none)");
        if (hue != null || antiFlicker != null || flip != null || mirror != null)
            throw new NotSupportedException(
                $"{CameraName} exposes picture settings over ONVIF, which can't set hue, anti-flicker or flip/mirror");
        await _onvif.SetImagingAsync(bright, contrast, saturation, sharpen,
            CameraControl.DayNightToIrCut(dayNight), wideDynamicRange: null, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: picture settings changed over ONVIF");
    }

    public async Task<OsdSettings?> GetOsdSettingsAsync(CancellationToken ct)
    {
        if (_onvif == null) return null;
        var osds = await _onvif.TryGetOsdsAsync(ct).ConfigureAwait(false);
        if (osds is not { Count: > 0 }) return null;
        var (name, time) = SplitOsds(osds);
        // ONVIF has no "hide" flag on an overlay — an overlay either exists or it
        // does not — so presence IS visibility here, and the panel is told (through
        // features.onvif) not to offer a switch it cannot honour.
        return new OsdSettings(
            ShowName: name != null, Name: name?.PlainText, NamePos: name?.Position,
            ShowTime: time != null, TimePos: time?.Position,
            Watermark: null, PosOptions: OnvifClient.OsdPositions);
    }

    /// <summary>The two overlays the panel knows about: the plain-text one is the
    /// camera's name, and any date/time one is the timestamp.</summary>
    internal static (OnvifOsd? Name, OnvifOsd? Time) SplitOsds(IReadOnlyList<OnvifOsd> osds)
    {
        var time = osds.FirstOrDefault(o => o.Kind is "Date" or "Time" or "DateAndTime");
        var name = osds.FirstOrDefault(o => o != time && o.Kind is null or "Plain");
        return (name, time);
    }

    public async Task SetOsdSettingsAsync(bool? showName, string? namePos, bool? showTime,
        string? timePos, bool? watermark, CancellationToken ct)
    {
        if (_onvif == null)
            throw new NotSupportedException(
                $"the on-screen display needs the camera's ONVIF service ('{CameraName}' has none)");
        if (showName != null || showTime != null || watermark != null)
            throw new NotSupportedException(
                $"{CameraName} exposes its overlays over ONVIF, which can move them but not switch them off");
        if (namePos == null && timePos == null) return;
        var osds = await _onvif.TryGetOsdsAsync(ct).ConfigureAwait(false)
            ?? throw new NotSupportedException($"{CameraName} reports no ONVIF overlays");
        var (name, time) = SplitOsds(osds);
        if (namePos != null && name != null)
            await _onvif.SetOsdAsync(name, namePos, plainText: null, ct).ConfigureAwait(false);
        if (timePos != null && time != null)
            await _onvif.SetOsdAsync(time, timePos, plainText: null, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: on-screen display moved over ONVIF");
    }

    public async Task<IReadOnlyList<PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct)
    {
        if (_onvif is not { HasPtz: true }) return null;
        var presets = await _onvif.TryGetPresetsAsync(ct).ConfigureAwait(false);
        if (presets == null) return null;
        // The panel saves into the first FREE slot, so a few empty ones are offered
        // past the camera's own — ONVIF creates a preset on demand rather than
        // filling a fixed table, and it refuses once the head is full.
        var list = presets.Select((p, i) => new PtzPresetInfo(i + 1, p.Name, true)).ToList();
        for (int i = presets.Count; i < Math.Min(presets.Count + 4, 16); i++)
            list.Add(new PtzPresetInfo(i + 1, "", false));
        return list;
    }

    public Task PtzToPresetAsync(int id, CancellationToken ct) =>
        _onvif is { HasPtz: true } o
            ? o.GotoPresetAsync(id, ct)
            : throw new NotSupportedException($"{CameraName} reports no ONVIF PTZ");

    public Task SavePtzPresetAsync(int id, string name, CancellationToken ct) =>
        _onvif is { HasPtz: true } o
            ? o.SavePresetAsync(id, name, ct)
            : throw new NotSupportedException($"{CameraName} reports no ONVIF PTZ");

    /// <summary>Drives the head. The panel's 1-64 speed is a fraction of full
    /// speed here — ONVIF's velocities are normalized, not stepped.</summary>
    public Task PtzAsync(string command, float speed, CancellationToken ct)
    {
        if (_onvif is not { HasPtz: true } o)
            throw new NotSupportedException($"{CameraName} reports no ONVIF PTZ");
        var v = Math.Clamp(speed / 64f, 0.05f, 1f);
        return command.ToLowerInvariant() switch
        {
            "up" => o.PtzMoveAsync(0, v, 0, ct),
            "down" => o.PtzMoveAsync(0, -v, 0, ct),
            "left" => o.PtzMoveAsync(-v, 0, 0, ct),
            "right" => o.PtzMoveAsync(v, 0, 0, ct),
            "stop" => o.PtzStopAsync(ct),
            _ => throw new ArgumentException($"unknown PTZ command '{command}' (up|down|left|right|stop)"),
        };
    }

    public Task RebootAsync(CancellationToken ct) =>
        _onvif is { } o
            ? o.RebootAsync(ct)
            : throw new NotSupportedException($"reboot needs the camera's ONVIF service ('{CameraName}' has none)");

    // ------------------------------------------------------------ not on this surface

    public Task<XElement?> GetBatteryInfoAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    /// <summary>ONVIF advertises a snapshot URI, which is what a Profile S camera is
    /// meant to offer and far cheaper than decoding a frame out of the video. A
    /// camera that offers none returns null here and the caller falls back to the
    /// stream (see Media/FrameGrab).</summary>
    public Task<byte[]?> SnapshotAsync(CancellationToken ct) =>
        _onvif?.TrySnapshotAsync(ct) ?? Task.FromResult<byte[]?>(null);

    /// <summary>Only once the camera has actually given a snapshot URI. Until then
    /// the stream-decoded still stands in, which is what a camera with no ONVIF (or
    /// no snapshot service) keeps using for good.</summary>
    public bool HasSnapshot => _onvif?.HasSnapshotUri == true;

    public Task<XElement?> GetLedStateAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetLedStateAsync(string? state, string? lightState,
        string? doorbellLightState, int? irBrightness, CancellationToken ct) =>
        throw new NotSupportedException("LED control is not available over ONVIF");

    public Task<XElement?> GetPirStateAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetPirEnabledAsync(bool enabled, CancellationToken ct) =>
        throw new NotSupportedException("PIR control is not available over ONVIF");

    /// <summary>The lens's zoom, on the 0-100 scale the panel's slider uses. ONVIF
    /// positions are the camera's own range (almost always 0..1); the panel is given
    /// whole steps so it never has to know. Focus is not offered: ONVIF moves focus
    /// through the imaging service in ways too varied between cameras to present as
    /// one absolute slider. Null — and no section — when the camera has no absolute
    /// zoom or will not say where the lens is.</summary>
    public async Task<XElement?> GetZoomFocusAsync(CancellationToken ct)
    {
        if (_onvif is not { HasPtz: true } o) return null;
        if (await o.TryGetPtzNodeAsync(ct).ConfigureAwait(false) is not { Zoom: true } node) return null;
        if ((await o.TryGetZoomAsync(ct).ConfigureAwait(false)).At is not { } at) return null;
        return new XElement("zoomFocus",
            new XElement("zoom",
                new XElement("curPos", ZoomStep(at, node.ZoomRange)),
                new XElement("minPos", 0),
                new XElement("maxPos", ZoomSteps)));
    }

    public async Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct)
    {
        if (command != "zoomPos")
            throw new NotSupportedException("focus is not available over ONVIF");
        if (_onvif is not { HasPtz: true } o
            || await o.TryGetPtzNodeAsync(ct).ConfigureAwait(false) is not { Zoom: true } node)
            throw new NotSupportedException($"{CameraName} reports no ONVIF zoom");
        var target = ZoomPosition(movePos, node.ZoomRange);
        await o.ZoomToAsync(target, ct).ConfigureAwait(false);
        // AbsoluteMove answers as soon as the lens STARTS moving, and the panel
        // reads the position straight back — which would catch the lens on its way
        // and snap the slider to wherever it happened to be. So wait, briefly, for
        // it to arrive: until the camera says it is idle, it is within half a step
        // of the target, or it has stopped moving between two looks.
        var nearEnough = (node.ZoomRange.Max - node.ZoomRange.Min) / ZoomSteps / 2;
        double? last = null;
        for (var until = DateTime.UtcNow + ZoomSettle; DateTime.UtcNow < until;)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            var (at, moving) = await o.TryGetZoomAsync(ct).ConfigureAwait(false);
            if (at is not { } now || moving == false || Math.Abs(now - target) <= nearEnough || now == last)
                break;
            last = now;
        }
    }

    private static readonly TimeSpan ZoomSettle = TimeSpan.FromSeconds(5);

    internal const int ZoomSteps = 100;

    internal static long ZoomStep(double position, (double Min, double Max) range) =>
        (long)Math.Round(Math.Clamp((position - range.Min) / (range.Max - range.Min), 0, 1) * ZoomSteps);

    internal static double ZoomPosition(uint step, (double Min, double Max) range) =>
        range.Min + Math.Min(step, (uint)ZoomSteps) / (double)ZoomSteps * (range.Max - range.Min);

    // ------------------------------------------------------------ detection zone

    // What the camera has said about a motion grid of its own. Holds is kept for the
    // run once seen, exactly as the Reolink surface keeps its own: a camera that
    // showed its grid once and then fails to answer has not stopped having one.
    private const int ZoneNotAsked = 0, ZoneHolds = 1, ZoneNone = 2, ZoneUnsure = 3;
    private volatile int _zone;
    private DateTime _zoneRetryAt;
    private int _zoneProbing;
    private int _zoneSilences;
    private const int ZoneSilenceLimit = 3;
    private static readonly TimeSpan ZoneRetry = TimeSpan.FromMinutes(5);
    /// <summary>How often a camera that said it keeps no grid is asked again. Its
    /// answer is almost always lasting, but not always (a fault that passes, a grid
    /// switched on in its own web page), and asking costs one or two calls.</summary>
    private static readonly TimeSpan ZoneNoneRetry = TimeSpan.FromMinutes(30);

    /// <summary>Whether the zone lives on the camera (its ONVIF cell motion grid) or
    /// on Neolink. Unknown only until the camera has first been asked: a camera that
    /// could not be asked keeps its zone on Neolink meanwhile — which is where every
    /// camera without a Reolink API kept it before this could be asked at all — and
    /// is asked again in the background (see <see cref="GetCapabilitiesAsync"/>).
    /// Once the camera has shown a grid of its own, that is where the zone lives.</summary>
    public bool? CameraHoldsZone => _onvif == null ? false : _zone switch
    {
        ZoneHolds => true,
        ZoneNotAsked => null,
        _ => false,
    };

    /// <summary>The camera's own grid could not be read just now. Only consulted
    /// once it has shown one: a camera whose grid is on Neolink is never asked.</summary>
    public bool HttpPaused => _zone == ZoneHolds && _zoneLastFailed;
    private volatile bool _zoneLastFailed;

    public async Task<DetectionZone?> GetDetectionZoneAsync(string type, CancellationToken ct)
    {
        if (type != "md" || _onvif == null) return null;
        var (answer, zone) = await _onvif.ReadCellZoneAsync(ct).ConfigureAwait(false);
        Settle(answer);
        return zone == null ? null : new DetectionZone("md", zone.Cols, zone.Rows, zone.Table);
    }

    public async Task SetDetectionZoneAsync(string type, string table, CancellationToken ct)
    {
        if (type != "md" || _onvif == null)
            throw new NotSupportedException($"{CameraName} keeps no {type} detection zone of its own");
        await _onvif.WriteCellZoneAsync(table, ct).ConfigureAwait(false);
        Log.Info($"{CameraName}: motion grid written to the camera over ONVIF " +
                 $"({table.Count(ch => ch == '0')} of {table.Length} cells ignored)");
    }

    private void Settle(OnvifZoneAnswer answer)
    {
        _zoneLastFailed = answer == OnvifZoneAnswer.Unknown;
        if (_zone == ZoneHolds) return;
        // Two different silences. ONVIF not answering at all is what a camera with
        // no ONVIF looks like, and those have always kept their zone on Neolink.
        // ONVIF answering while its analytics does not is a camera that may well
        // hold a grid, so it is asked again before its zone is claimed for Neolink
        // — a few times, not forever: an unknown zone holds the live boxes back.
        if (answer == OnvifZoneAnswer.Unknown && _zone == ZoneNotAsked && _onvif!.Ready
            && ++_zoneSilences < ZoneSilenceLimit)
            return;
        var was = _zone;
        // A camera already settled on Neolink stays there through a silence: only
        // an actual answer moves it.
        if (answer == OnvifZoneAnswer.Unknown && was == ZoneNone) return;
        _zone = answer switch
        {
            OnvifZoneAnswer.Holds => ZoneHolds,
            OnvifZoneAnswer.None => ZoneNone,
            _ => ZoneUnsure,
        };
        _zoneRetryAt = DateTime.UtcNow + (_zone == ZoneNone ? ZoneNoneRetry : ZoneRetry);
        if (_zone != was && _zone != ZoneUnsure)
            Log.Info(_zone == ZoneHolds
                ? $"{CameraName}: the camera keeps its own motion grid (ONVIF cell motion) — the detection zone is edited there" +
                  (was is ZoneUnsure or ZoneNone
                      ? " from now on; a zone kept on Neolink for it meanwhile no longer applies (it stays stored)"
                      : "")
                : $"{CameraName}: the camera keeps no motion grid of its own — the detection zone is kept on Neolink");
    }

    /// <summary>Asks again, off the caller's path, a camera that could not be asked
    /// before. At most one at a time, and not more often than <see cref="ZoneRetry"/>.</summary>
    private void ReprobeZoneIfDue()
    {
        // Only once ONVIF has answered: until then this would be discovery itself,
        // up to half a minute holding the client's gate — and a PTZ stop queued
        // behind that would let the head overshoot. The event service or the next
        // panel open brings discovery about soon enough.
        if (_onvif is not { Ready: true } || _zone is not (ZoneUnsure or ZoneNone)
            || DateTime.UtcNow < _zoneRetryAt) return;
        if (Interlocked.Exchange(ref _zoneProbing, 1) != 0) return;
        _zoneRetryAt = DateTime.UtcNow + ZoneRetry;
        _ = Task.Run(async () =>
        {
            try { await GetDetectionZoneAsync("md", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug($"{CameraName}: motion grid re-probe failed: {Log.Flatten(ex)}"); }
            finally { Volatile.Write(ref _zoneProbing, 0); }
        });
    }

    public Task SirenAsync(bool? on, CancellationToken ct) =>
        throw new NotSupportedException("the siren is not available over ONVIF");

    public Task<bool?> GetPrivacyModeAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task SetPrivacyModeAsync(bool on, CancellationToken ct) =>
        throw new NotSupportedException("privacy mode is not available over ONVIF");

    public Task<XElement?> GetFloodlightTasksAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetFloodlightTasksAsync(XElement task, CancellationToken ct) =>
        throw new NotSupportedException("floodlight control is not available over ONVIF");

    public Task<WhiteLedState?> GetWhiteLedAsync(CancellationToken ct) => Task.FromResult<WhiteLedState?>(null);

    public Task SetWhiteLedAsync(int? bright, bool? on, int? mode, CancellationToken ct) =>
        throw new NotSupportedException("white-LED control is not available over ONVIF");

    public Task<int?> GetVolumeAsync(CancellationToken ct) => Task.FromResult<int?>(null);

    public Task SetVolumeAsync(int volume, CancellationToken ct) =>
        throw new NotSupportedException("the speaker volume is not available over ONVIF");

    public Task<WifiReading?> GetWifiSignalAsync(CancellationToken ct) => Task.FromResult<WifiReading?>(null);

    public Task<IReadOnlyList<QuickReplyFile>?> GetQuickRepliesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QuickReplyFile>?>(null);

    public Task PlayQuickReplyAsync(int id, CancellationToken ct) =>
        throw new NotSupportedException("quick replies are not available over ONVIF");

    public Task<AutoReplyState?> GetAutoReplyAsync(CancellationToken ct) =>
        Task.FromResult<AutoReplyState?>(null);

    public Task SetAutoReplyAsync(int? fileId, int? timeoutSeconds, CancellationToken ct) =>
        throw new NotSupportedException("the auto-reply is not available over ONVIF");

    public Task<bool?> GetAutoTrackAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task SetAutoTrackAsync(bool on, CancellationToken ct) =>
        throw new NotSupportedException("auto-tracking is not available over ONVIF");

    public Task<IReadOnlyList<SdCardInfo>?> GetSdCardsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SdCardInfo>?>(null);

    public Task TalkAsync(int sampleRate, System.Threading.Channels.ChannelReader<byte[]> pcm, CancellationToken ct) =>
        throw new NotSupportedException("two-way talk is not available over ONVIF");
}
