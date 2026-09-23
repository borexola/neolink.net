using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Neolink.Protocol;

/// <summary>Picture settings read over ONVIF, scaled to the 0-255 range the UI and
/// the Reolink HTTP path both use (128 = neutral). A null field means the camera's
/// imaging service didn't report it. IrCutFilter is the raw ONVIF enum
/// ("ON"|"OFF"|"AUTO"); the day/night vocabulary mapping lives in the caller.</summary>
public sealed record OnvifImaging(int? Brightness, int? Contrast, int? Saturation,
    int? Sharpness, string? IrCutFilter, bool? WideDynamicRange);

/// <summary>The camera's accepted value ranges for the imaging fields (from the
/// ONVIF imaging GetOptions call), used to scale between the camera's native units
/// and the UI's 0-255. A null range means the field wasn't offered.</summary>
public sealed record OnvifImagingRanges(
    (double Min, double Max)? Brightness, (double Min, double Max)? Contrast,
    (double Min, double Max)? Saturation, (double Min, double Max)? Sharpness);

/// <summary>
/// A small ONVIF client covering just the Imaging service: brightness, contrast,
/// color saturation, sharpness, the IR-cut (day/night) filter and wide-dynamic-range.
/// It exists as a STANDARDS-BASED FALLBACK for Reolink models with no HTTP CGI API
/// (the Lumus line, some Argus) — the camera already streams over Baichuan, and the
/// picture sliders that would ride the HTTP API ride ONVIF instead. Strictly
/// additive: the caller only reaches for it when the HTTP path has nothing, so a
/// healthy HTTP camera never touches this code.
///
/// Everything is best-effort and never throws into a feature sweep: a camera with
/// ONVIF disabled, wrong credentials, or a firmware that omits imaging simply yields
/// null. Service endpoints and the video-source token are discovered once and cached.
/// </summary>
public sealed partial class OnvifClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    // ONVIF / WS namespaces.
    private const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
    private const string NsWsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string NsWsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
    private const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
    private const string NsImaging = "http://www.onvif.org/ver20/imaging/wsdl";
    private const string NsPtz = "http://www.onvif.org/ver20/ptz/wsdl";
    // Media2: the successor to the ver10 media service. Newer firmware serves it
    // alongside ver10, and some serves ONLY it — so it is the fallback, never the
    // first choice, and a camera that answers ver10 (every Reolink) never sees it.
    private const string NsMedia2 = "http://www.onvif.org/ver20/media/wsdl";
    private const string NsEvents = "http://www.onvif.org/ver10/events/wsdl";
    // WS-BaseNotification and WS-Addressing: ONVIF's event service is built on them
    // rather than defining its own subscription vocabulary.
    private const string NsWsnt = "http://docs.oasis-open.org/wsn/b-2";
    private const string NsWsa = "http://www.w3.org/2005/08/addressing";
    private const string NsSchema = "http://www.onvif.org/ver10/schema";
    private const string PwDigestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64Type = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    private readonly HttpClient _http;
    private readonly string[] _candidates;   // device-service URLs to try, in order
    private string _deviceUrl;               // the candidate currently being tried / that worked
    private readonly string _username;
    private readonly string _password;
    private readonly string _tag;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _generic;

    private bool _ready;              // discovery succeeded; endpoints + token cached
    private DateTime _retryAfter;     // after a failed discovery, don't re-probe until this time
    private bool _failLogged;         // the "unavailable" reason was logged for this outage
    private string? _lastError;       // the most recent SOAP/transport failure reason
    private string? _imagingUrl;
    private string? _mediaUrl;
    /// <summary>Set once ver10 media has failed and Media2 has answered instead; it
    /// sticks for the run. Every media call reads the three members below rather
    /// than assuming a dialect.</summary>
    private bool _media2;
    private string? _media2Url;
    private string MediaUrl => _media2 ? _media2Url! : _mediaUrl!;
    private string MediaNs => _media2 ? NsMedia2 : NsMedia;
    /// <summary>The prefix CallAsync binds the media namespace to, for use inside
    /// request bodies — they name their own child elements with it.</summary>
    private string Mp => _media2 ? "tr2" : "trt";
    private string? _ptzUrl;
    private string? _videoSourceToken;
    private OnvifImagingRanges? _ranges;
    private bool _hasImaging;
    /// <summary>The camera's clock minus ours, learned from GetSystemDateAndTime
    /// (the one ONVIF call that needs no authentication, precisely so it can be
    /// asked before the clocks are known to agree). Zero until discovery runs.</summary>
    private TimeSpan _clockSkew;
    private IReadOnlyList<OnvifProfile>? _profiles;
    /// <summary>Whether the camera has EVER answered with a profile. Survives the
    /// cache being dropped after a write; see <see cref="HasProfiles"/>.</summary>
    private bool _hadProfiles;
    private string? _ptzProfileToken;
    private IReadOnlyList<string> _presetTokens = Array.Empty<string>();
    private IReadOnlyDictionary<string, string>? _streamUris;
    private OnvifPtzNode? _ptzNode;
    private bool _ptzNodeRefused;
    private string? _ptzNodeToken;

    /// <param name="address">"host", "host:port", or a full "http(s)://host[:port]"
    /// URL. A bare host (no explicit port) is probed on Reolink's ONVIF port 8000
    /// FIRST, then port 80 — Reolink serves ONVIF natively on 8000, and port 80
    /// (the HTTP/CGI web port) only half-proxies it on some firmwares (the device
    /// service answers but the media service 502s or times out). An explicit port
    /// or full URL is taken at its word.</param>
    /// <param name="probePorts">Ports to try for a bare host, in order. Reolink
    /// serves ONVIF on 8000, so that is the default; a non-Reolink camera is far
    /// more likely to answer on 80, and passes its own order.</param>
    /// <param name="generic">True for a camera whose ONVIF is its whole control
    /// surface (a non-Reolink one). That camera's discovery also reads its clock,
    /// looks up GetServices when GetCapabilities says nothing, is ready as soon as
    /// the device service answers, and gives each port a time budget. A Reolink uses
    /// ONVIF only for the picture-settings fallback, and its discovery is exactly
    /// what it always was: none of those, and not ready without a video source.</param>
    public OnvifClient(string address, string username, string? password, string tag,
        IReadOnlyList<int>? probePorts = null, bool generic = false)
    {
        _generic = generic;
        // A login inside the address wins: it is the one place a camera whose ONVIF
        // account differs from its streaming one can be given the right credentials,
        // and it is the field the settings UI already offers. Generic cameras only —
        // a Reolink signs in with its own login, as it always has.
        var (bare, user, pass) = generic ? SplitCredentials(address) : (address, null, null);
        _candidates = BuildCandidates(bare, probePorts);
        _deviceUrl = _candidates[0];
        _username = user ?? username;
        _password = pass ?? password ?? "";
        _tag = tag;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            // Some cameras want HTTP authentication INSTEAD of (or as well as) the
            // WS-Security header every request already carries — Profile S allows
            // either, and a camera configured for Digest answers 401 to a perfectly
            // good SOAP request. Handing the same credentials to the transport lets
            // it answer that challenge, so those cameras work without the user
            // having to know which scheme theirs chose. Costs nothing on a camera
            // that never challenges. Basic is answered as well as Digest: some snapshot
            // CGIs challenge that way. Generic cameras only: a Reolink's picture fallback
            // has only ever used WS-Security, and keeps doing exactly that.
            Credentials = generic ? new System.Net.NetworkCredential(_username, _password) : null,
            PreAuthenticate = false,
        };
        if (_candidates.Any(c => c.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        // No client-wide timeout: each call sets its own, because they differ by an
        // order of magnitude — a settings read must not hang, while an event long
        // poll asks the camera to hold the request until something happens.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Splits "user:pass@" off an address, returning it separately. Only a
    /// full URL can carry one — a bare "host:port" has no room for it without being
    /// ambiguous. Percent-escapes are decoded, so a password with an @ or a / in it
    /// survives the URL it had to be written into.</summary>
    internal static (string Address, string? User, string? Pass) SplitCredentials(string address)
    {
        var a = address.Trim();
        // Parsed by hand, with the admin UI's own LoginSpan: System.Uri rejects an
        // unescaped '@' or '/' in the password, which is common.
        if (Config.ConfigEditor.LoginSpan(a) is not { } span) return (a, null, null);
        int scheme = a.IndexOf("://", StringComparison.Ordinal);
        int start = scheme < 0 ? 0 : scheme + 3;
        var user = Unescape(a[start..(span.PassStart < 0 ? span.At : span.PassStart - 1)]);
        var pass = span.PassStart < 0 ? null : Unescape(a[span.PassStart..span.At]);
        var bare = a[..start] + a[(span.At + 1)..];
        return (scheme >= 0 && !a.EndsWith('/') ? bare.TrimEnd('/') : bare, user.Length == 0 ? null : user, pass);

        // Decoded after the split, so an escaped password works as well as a raw one.
        static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s); }
            catch (UriFormatException) { return s; }
        }
    }

    /// <summary>The ONVIF client for a non-Reolink camera, from its ONVIF address or
    /// the stream URL's host and login. Null when neither names a host.</summary>
    public static OnvifClient? ForGenericCamera(string? onvifAddress, string? rtspUrl, string tag)
    {
        var (host, _, user, pass) = NetUtil.SplitRtspUrl(rtspUrl);
        var address = string.IsNullOrWhiteSpace(onvifAddress) ? host : onvifAddress.Trim();
        return address == null ? null
            : new OnvifClient(address, user ?? "", pass, tag, probePorts: GenericProbePorts, generic: true);
    }

    /// <summary>The ports a non-Reolink camera's ONVIF is probed on for a bare host,
    /// in order: 80, Reolink's 8000, TP-Link Tapo's 2020, XiongMai's 8899.</summary>
    public static readonly int[] GenericProbePorts = { 80, 8000, 2020, 8899 };

    /// <summary>The device-service URL(s) to try, in order. A full URL or an
    /// explicit host:port yields exactly one; a bare host yields :8000 then :80.</summary>
    internal static string[] BuildCandidates(string address, IReadOnlyList<int>? probePorts = null)
    {
        var a = address.Trim();
        if (a.Contains("://", StringComparison.Ordinal))
        {
            var baseUrl = a.TrimEnd('/');
            return new[] { baseUrl.Contains("/onvif/", StringComparison.OrdinalIgnoreCase)
                ? baseUrl : $"{baseUrl}/onvif/device_service" };
        }
        // Bare authority. An explicit ":port" is honoured as-is; a bare host walks
        // the caller's port list (Reolink's 8000 then 80, unless told otherwise).
        var colon = a.LastIndexOf(':');
        bool explicitPort = colon > 0 && int.TryParse(a.AsSpan(colon + 1), out _);
        if (explicitPort)
            return new[] { $"http://{a}/onvif/device_service" };
        return (probePorts is { Count: > 0 } ports ? ports : new[] { 8000, 80 })
            .Select(p => p == 80
                ? $"http://{a}/onvif/device_service"
                : $"http://{a}:{p}/onvif/device_service")
            .ToArray();
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Best-effort imaging read (0-255 scaled), or null when ONVIF is
    /// unavailable on this camera. Never throws.</summary>
    public async Task<OnvifImaging?> TryGetImagingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Not gated on _hasImaging: that flag records only a CONFIRMED yes, and
            // treating its absence as "no imaging" would turn one timed-out probe
            // into a camera with no picture settings for the rest of the run.
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false)) return null;
            if (!await EnsureVideoSourceAsync(ct).ConfigureAwait(false)) return null;
            var xml = await CallAsync(_imagingUrl!, NsImaging, "GetImagingSettings",
                $"<timg:VideoSourceToken>{Esc(ImagingSourceToken)}</timg:VideoSourceToken>", ct)
                .ConfigureAwait(false);
            if (xml == null) return null;
            _hasImaging = true; // a real answer: now it is confirmed
            return ParseImaging(xml, _ranges);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"{_tag}: ONVIF imaging read failed: {Log.Flatten(ex)}");
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Writes the imaging fields the UI can change over ONVIF (0-255 values
    /// scaled back to the camera's native range; IrCutFilter passed through). Null
    /// fields are left untouched. Throws when ONVIF is unavailable, so the caller can
    /// surface a real error on an explicit user action.</summary>
    public async Task SetImagingAsync(int? brightness, int? contrast, int? saturation,
        int? sharpness, string? irCutFilter, bool? wideDynamicRange, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false)
                || !await EnsureVideoSourceAsync(ct).ConfigureAwait(false))
                throw new NotSupportedException("the camera's ONVIF imaging service is not reachable");
            var body = BuildSetImaging(ImagingSourceToken, _ranges,
                brightness, contrast, saturation, sharpness, irCutFilter, wideDynamicRange);
            var xml = await CallAsync(_imagingUrl!, NsImaging, "SetImagingSettings", body, ct).ConfigureAwait(false);
            if (xml == null)
                throw new IOException("the camera did not confirm the ONVIF imaging change");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the camera's clock and records how far it is from ours. Asked
    /// without authentication — ONVIF answers this one call to anyone, precisely so
    /// a client can learn the correction before its first authenticated request.
    /// A camera that will not say leaves the correction as it was.</summary>
    private async Task LearnClockAsync(CancellationToken ct)
    {
        _clockCheckedAt = DateTime.UtcNow;
        var clock = await CallAsync(_deviceUrl, NsDevice, "GetSystemDateAndTime", "", ct,
            authenticate: false).ConfigureAwait(false);
        if (clock == null || ParseUtcTime(clock) is not { } cameraNow) return;
        var skew = cameraNow - DateTime.UtcNow;
        var previous = _clockSkew;
        var corrects = Math.Abs(skew.TotalSeconds) > 5;
        _clockSkew = corrects ? skew : TimeSpan.Zero;
        // Said once when a correction starts, and again only if it has since moved a
        // lot — an hourly re-read of a steadily drifting clock is not news each time.
        if (corrects && (Math.Abs(previous.TotalSeconds) <= 5
                         || Math.Abs((skew - previous).TotalSeconds) > 30))
            Log.Info($"{_tag}: the camera's clock is {skew.TotalSeconds:0}s from this server's — " +
                     "ONVIF requests are stamped in the camera's time so it accepts them.");
    }

    /// <summary>A camera with no working time sync keeps drifting, and a correction
    /// learned at start-up is wrong a few weeks later. Re-read this often — one
    /// small unauthenticated request, far cheaper than the silent auth failures a
    /// stale correction produces.</summary>
    private static readonly TimeSpan ClockRecheck = TimeSpan.FromHours(1);
    private DateTime _clockCheckedAt;
    private bool _emptyLogged;

    /// <summary>The video source the imaging service is keyed on, fetched now if
    /// discovery did not get it. Discovery succeeds as soon as the DEVICE service
    /// answers, so a GetVideoSources that timed out during it would otherwise leave
    /// the token null for the rest of the run — and a Reolink Lumus, whose only
    /// picture settings are these, with none at all until Neolink restarts. Asking
    /// again here costs one round trip, only while it is missing. Caller holds _gate.</summary>
    private async Task<bool> EnsureVideoSourceAsync(CancellationToken ct)
    {
        // After a reboot the profiles are read back before the first per-channel
        // call, so it does not fall back to channel 1's source.
        if (_preferredProfileToken != null && _profiles == null)
            _profiles = await ReadProfilesAsync(ct).ConfigureAwait(false);
        if (_videoSourceToken == null)
        {
            var sources = await CallAsync(_mediaUrl!, NsMedia, "GetVideoSources", "", ct).ConfigureAwait(false);
            _videoSourceToken = VideoSourceToken(sources);
            // GetVideoSources is a ver10 call; a camera that speaks only Media2 names
            // its source inside each profile's VideoSource configuration instead. Only
            // consulted once the camera is KNOWN to be Media2 — a ver10 camera whose
            // GetVideoSources merely timed out must not start probing for a dialect it
            // does not speak (a Reolink never reads its profiles here at all).
            if (_videoSourceToken == null && _media2)
                _videoSourceToken = _profiles?.Select(p => p.SourceToken).FirstOrDefault(t => t != null);
            if (_videoSourceToken == null) return false;
        }
        // Missing ranges are asked for again on a schedule (GetOptions may have timed
        // out), and at once when the imaging source is not the one they were read for.
        var source = ImagingSourceToken;
        bool other = _generic && _rangesFor != source;
        if (other || (_ranges == null && _generic && DateTime.UtcNow - _rangesAskedAt > RangesRetry))
        {
            _rangesAskedAt = DateTime.UtcNow;
            _rangesFor = source;
            var options = await CallAsync(_imagingUrl!, NsImaging, "GetOptions",
                $"<timg:VideoSourceToken>{Esc(source)}</timg:VideoSourceToken>", ct)
                .ConfigureAwait(false);
            // Unscaled beats another channel's scale when the read failed.
            _ranges = options != null ? ParseRanges(options) : null;
        }
        return true;
    }

    private DateTime _rangesAskedAt;
    /// <summary>The video source <see cref="_ranges"/> were read for.</summary>
    private string? _rangesFor;
    private static readonly TimeSpan RangesRetry = TimeSpan.FromSeconds(60);

    /// <summary>The video source the imaging calls address: the preferred profile's
    /// when known, else the device's first — which on an NVR is channel 1's.</summary>
    private string ImagingSourceToken =>
        PreferredProfile?.SourceToken ?? _videoSourceToken!;

    // ------------------------------------------------------------ discovery

    private async Task<bool> EnsureDiscoveredAsync(CancellationToken ct)
    {
        // Cached success is permanent for the run; a failure only pauses re-probing
        // for a cooldown (a camera rebooting during the first probe must not be
        // written off until Neolink restarts) — but not so often it stalls callers.
        if (_ready)
        {
            // Re-read on a schedule, and sooner after a refusal: a camera whose clock
            // stepped rejects every stamped request until the correction is relearned.
            if (_generic && (DateTime.UtcNow - _clockCheckedAt > ClockRecheck
                             || (_authRejected && DateTime.UtcNow - _clockCheckedAt > ClockRecheckAfterRefusal)))
                await LearnClockAsync(ct).ConfigureAwait(false);
            return true;
        }
        if (DateTime.UtcNow < _retryAfter) return false;
        _lastError = null;
        // Once for the whole scan, not per port: a port that rejected the login is
        // the answer even when the ports after it are closed.
        _authRejected = false;
        // Each candidate's device service is bounded, not the list as a whole: an
        // unreachable host must not hold callers for a connect timeout per port.
        try
        {
            // Try each candidate endpoint (Reolink ONVIF port 8000, then 80) until
            // one answers its device service.
            //
            // A port that answered without a video source (a web port half-proxying
            // ONVIF) is kept in reserve until the later ports have had their turn.
            string? partial = null;
            foreach (var candidate in _candidates)
            {
                ct.ThrowIfCancellationRequested();
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_generic) probe.CancelAfter(CandidateBudget);
                _deviceUrl = candidate;
                bool ok;
                try
                {
                    ok = await TryDiscoverAsync(probe.Token, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _lastError = $"no answer from {Authority(candidate)} within " +
                                 $"{CandidateBudget.TotalSeconds:0}s";
                    continue; // this port is not listening; the next one may be
                }
                if (!ok) continue;
                if (_generic && _videoSourceToken == null && partial == null
                    && candidate != _candidates[^1])
                {
                    partial = candidate;
                    continue;
                }
                return Discovered(candidate);
            }
            if (partial != null)
            {
                // Nothing better: the half-answering port is what this camera has.
                _deviceUrl = partial;
                if (await TryDiscoverAsync(ct, ct).ConfigureAwait(false)) return Discovered(partial);
            }
            return Fail(_lastError ?? (_generic
                ? "the camera did not answer the ONVIF device service"
                : "the camera exposed no ONVIF video source"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Fail(Log.Flatten(ex));
        }
    }

    /// <summary>Records a successful discovery on <paramref name="candidate"/> and
    /// says so, once, with what answered.</summary>
    private bool Discovered(string candidate)
    {
        _ready = true;
        _failLogged = false; // a future outage warns again
        if (!_generic)
        {
            // A Reolink's picture fallback logs exactly what it always has.
            Log.Info($"{_tag}: ONVIF imaging fallback ready (video source '{_videoSourceToken}'" +
                     $"{(_candidates.Length > 1 ? $" via {Authority(candidate)}" : "")})");
            return true;
        }
        // Which services answered is the first thing anyone asks when a
        // panel section is missing, so it goes in the one Info line.
        var have = new List<string>();
        if (_hasImaging) have.Add("imaging");
        if (_eventsUrl != null) have.Add("events");
        if (_videoSourceToken != null) have.Add($"video source '{_videoSourceToken}'");
        Log.Info($"{_tag}: ONVIF ready at {Authority(candidate)} — " +
                 (have.Count > 0 ? string.Join(", ", have) : "device service only"));
        // A device that answers its service table and then offers nothing
        // behind it is the case that used to be reported as an outright
        // failure, complete with the advice that fixes it. Discovery now
        // succeeds there (device information is still worth having), so
        // the advice is given here — as what it is: there is no retry
        // coming, and "unavailable, retrying in 5 min" right after "ready"
        // would be wrong twice over.
        if (_authRejected && !_emptyLogged)
        {
            // The service table needs no login, so "ready" can be reached with the
            // wrong one; the first authenticated call is what found out.
            _emptyLogged = true;
            Log.Info($"{_tag}: ONVIF answered but REJECTED the login, so its settings and detections " +
                     "stay unavailable. Check the camera's ONVIF user and password (they may differ from " +
                     "the streaming login — a full URL in 'onvif_address' can carry its own, " +
                     "\"http://user:pass@host/onvif/device_service\"), and that the user has operator rights.");
        }
        else if (have.Count == 0 && !_emptyLogged)
        {
            _emptyLogged = true;
            Log.Info($"{_tag}: ONVIF answered but exposed no video source, so picture and " +
                     "stream settings stay unavailable. If this camera should offer them, check " +
                     "its ONVIF user has operator rights and its media service is enabled.");
        }
        return true;
    }

    /// <summary>Starts discovery in the background, one at a time, when it is neither
    /// done nor cooling down: for a caller that must not block on it.</summary>
    public void KickDiscovery()
    {
        if (_ready || DateTime.UtcNow < _retryAfter) return;
        if (Interlocked.Exchange(ref _discovering, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { await EnsureDiscoveredAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { _gate.Release(); }
            }
            catch (Exception ex) { Log.Debug($"{_tag}: background ONVIF discovery failed: {Log.Flatten(ex)}"); }
            finally { Volatile.Write(ref _discovering, 0); }
        });
    }

    private int _discovering;

    /// <summary>Whether discovery may run right now: not yet done and not cooling
    /// down after a failure. A caller that must not block asks this first.</summary>
    public bool DiscoveryDue => !_ready && DateTime.UtcNow >= _retryAfter;

    /// <summary>Whether a background discovery kicked by <see cref="KickDiscovery"/>
    /// is running at this moment.</summary>
    public bool Discovering => Volatile.Read(ref _discovering) != 0;

    /// <summary>A URL's host:port for a log line; one that does not parse is masked
    /// rather than thrown on.</summary>
    private static string Authority(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : Safe(url);

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : Safe(url);

    /// <summary>A URL with any password it carries masked, for logs and messages.</summary>
    private static string Safe(string url) => Config.ConfigEditor.MaskRtspPassword(url) ?? url;

    /// <summary>One candidate endpoint's discovery: the device's service table, plus
    /// the video source the imaging service is keyed on. Succeeds as soon as the
    /// DEVICE service answers — imaging, PTZ and the media profiles are each optional
    /// and are read LAZILY by whoever wants them, so a Reolink camera (whose only use
    /// of ONVIF is the picture-settings fallback) pays exactly the round trips it used
    /// to and not one more.</summary>
    /// <param name="deviceCt">Bounds the device-service calls that decide whether this
    /// port is listening; the optional calls after them run under <paramref name="ct"/>.</param>
    private async Task<bool> TryDiscoverAsync(CancellationToken deviceCt, CancellationToken ct)
    {
        // 0. The camera's clock, FIRST and unauthenticated — every request after
        //    this one carries a timestamp the camera checks against its own.
        //    Generic cameras only: a Reolink's picture fallback has always been
        //    stamped in this server's time and has worked that way.
        _clockSkew = TimeSpan.Zero;
        if (_generic) await LearnClockAsync(deviceCt).ConfigureAwait(false);

        // 1. The device's service table. GetCapabilities is the Profile S call and
        //    is what Reolink answers; GetServices is the ver10 replacement that
        //    some third-party firmwares offer instead. Either is enough, and a
        //    camera that answers neither is not speaking ONVIF here at all.
        //    Some firmwares return absolute XAddrs on a different host/port; others
        //    return the conventional paths. Fall back to conventions on a miss.
        if (!await ReadServiceTableAsync(deviceCt, ct).ConfigureAwait(false)) return false;

        // 2. Media GetVideoSources → the token the imaging service is keyed on.
        //    This is the one call besides the service table that every user of this
        //    client needs, so it stays in discovery.
        var sources = await CallAsync(_mediaUrl!, NsMedia, "GetVideoSources", "", ct).ConfigureAwait(false);
        _videoSourceToken = VideoSourceToken(sources);
        // For a Reolink the picture fallback IS the use of ONVIF, and it is keyed on
        // the video source: without one there is nothing to be ready for. Failing
        // here keeps the cooldown and the next port's turn, exactly as before — a
        // port that half-proxies ONVIF must not be settled on for the whole run.
        if (!_generic && _videoSourceToken == null) return false;

        // 3. Imaging GetOptions → accepted ranges, so 0-255 UI values scale to the
        //    camera's native units (and back). Optional: without it values pass
        //    through unscaled, which is what a Reolink on 0-255 wants anyway.
        //
        //    A failure here is NOT recorded as "this camera has no imaging". One
        //    timeout would otherwise leave a Lumus without picture settings for the
        //    rest of the run; the imaging calls simply try, and a camera that has
        //    none answers nothing, exactly as before.
        if (_videoSourceToken != null)
        {
            var options = await CallAsync(_imagingUrl!, NsImaging, "GetOptions",
                $"<timg:VideoSourceToken>{Esc(_videoSourceToken)}</timg:VideoSourceToken>", ct)
                .ConfigureAwait(false);
            if (options != null) _hasImaging = true;
            // A Reolink keeps whatever this read gave, even nothing, exactly as it
            // always did; a generic camera leaves the ranges unset so that a later
            // imaging call can ask again.
            if (options != null || !_generic) _ranges = ParseRanges(options);
            _rangesFor = _videoSourceToken;
        }
        return true;
    }

    /// <summary>Reads the device's service table: which services it has and where.
    /// False when neither call answered. Caller holds the gate.</summary>
    /// <param name="deviceCt">Bounds the call that decides whether this port is
    /// listening; a supplementary read runs under <paramref name="ct"/>.</param>
    private async Task<bool> ReadServiceTableAsync(CancellationToken deviceCt, CancellationToken ct)
    {
        _servicesReadAt = DateTime.UtcNow;
        var caps = await CallAsync(_deviceUrl, NsDevice, "GetCapabilities",
            "<tds:Category>All</tds:Category>", deviceCt).ConfigureAwait(false);
        // GetServices is also asked (generic only) when GetCapabilities listed no
        // events or analytics service: some firmwares list those only in the newer table.
        var services = !_generic || (caps != null && ServiceXAddr(caps, "Events") != null
                                     && ServiceXAddr(caps, "Analytics") != null) ? null
            : await CallAsync(_deviceUrl, NsDevice, "GetServices",
                "<tds:IncludeCapability>false</tds:IncludeCapability>", caps == null ? deviceCt : ct)
                .ConfigureAwait(false);
        // A Reolink carries on to the conventional paths even when GetCapabilities
        // said nothing, as it always has; its video source below is what decides.
        if (_generic && caps == null && services == null) return false;

        _mediaUrl = NormalizeXAddr(ServiceXAddr(caps, "Media") ?? ServiceXAddrByNs(services, NsMedia))
                    ?? Conventional("media_service");
        _imagingUrl = NormalizeXAddr(ServiceXAddr(caps, "Imaging") ?? ServiceXAddrByNs(services, NsImaging))
                      ?? Conventional("imaging");
        _ptzUrl = NormalizeXAddr(ServiceXAddr(caps, "PTZ") ?? ServiceXAddrByNs(services, NsPtz))
                  ?? Conventional("ptz_service");
        // The event service is the one thing here that is NOT assumed when the
        // camera does not advertise it: subscribing against a guessed URL on a
        // camera with no events would mean a reconnect loop against a 404 forever.
        _eventsUrl = NormalizeXAddr(ServiceXAddr(caps, "Events") ?? ServiceXAddrByNs(services, NsEvents));
        // Not guessed either: a camera that does not advertise analytics has no grid
        // of its own, and a guessed URL would only turn that lasting answer into a
        // timeout that reads as "ask again".
        _analyticsUrl = NormalizeXAddr(ServiceXAddr(caps, "Analytics") ?? ServiceXAddrByNs(services, NsAnalytics));
        return true;
    }

    private DateTime _servicesReadAt;

    /// <summary>Records a discovery failure: pauses re-probing for a cooldown and
    /// logs the reason ONCE per outage at Info (this is the only breadcrumb a user
    /// gets for why an HTTP-less camera shows no picture settings), with the two
    /// things that actually fix it.</summary>
    private bool Fail(string reason)
    {
        // Misses within the grace after a deliberate reboot are retried quickly, not
        // held for the five-minute "no ONVIF" cooldown.
        bool rebooting = DateTime.UtcNow - _rebootedAt < RebootGrace;
        _retryAfter = DateTime.UtcNow + (rebooting ? RebootRetry : RetryCooldown);
        // No advice while the camera is expected to be away; it comes if the grace runs out.
        if (rebooting)
            Log.Debug($"{_tag}: ONVIF not back since the reboot ({reason}); trying again in " +
                      $"{RebootRetry.TotalSeconds:0}s");
        else
            Advise(reason);
        return false;
    }

    /// <summary>When this client last asked the camera to reboot.</summary>
    private DateTime _rebootedAt = DateTime.MinValue;
    /// <summary>How long after a reboot discovery misses are retried quickly.</summary>
    private static readonly TimeSpan RebootGrace = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RebootRetry = TimeSpan.FromSeconds(20);

    /// <summary>The one breadcrumb a user gets for why a camera's settings are
    /// missing, logged once per outage with the two things that actually fix it.</summary>
    private void Advise(string reason)
    {
        if (!_failLogged && !_generic)
        {
            _failLogged = true;
            Log.Info($"{_tag}: ONVIF imaging fallback unavailable — {reason}. If this camera has no Reolink " +
                     "HTTP API, check that ONVIF is enabled on it (Reolink app > Settings > Network > Advanced > " +
                     "ONVIF/Port Settings), and if ONVIF is on a non-standard port set 'onvif_address' " +
                     $"(e.g. \"{HostOf(_deviceUrl)}:8000\"). Retrying in {RetryCooldown.TotalMinutes:0} min.");
        }
        else if (!_failLogged)
        {
            _failLogged = true;
            Log.Info($"{_tag}: ONVIF unavailable — {reason}. Check that ONVIF is enabled on the camera " +
                     "(a Reolink: app > Settings > Network > Advanced > ONVIF/Port Settings; anything else: " +
                     "its own web page), and if ONVIF is on a port other than " +
                     $"{string.Join("/", GenericProbePorts)} set 'onvif_address' " +
                     $"(e.g. \"{HostOf(_deviceUrl)}:8080\"). Without it the camera streams and records " +
                     $"normally but shows no settings. Retrying in {RetryCooldown.TotalMinutes:0} min.");
        }
    }

    /// <summary>The transport's last failure, for the camera editor's test. Endpoint
    /// URLs are left in; the login never is.</summary>
    public string? LastError => _lastError;

    /// <summary>Whether the camera has refused this client's login or timestamp since
    /// discovery: "ONVIF is off" and "wrong password" look the same otherwise.</summary>
    public bool AuthRejected => _authRejected;
    private bool _authRejected;
    private static readonly TimeSpan ClockRecheckAfterRefusal = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Ceiling on discovering ONE candidate endpoint. Generous enough for a
    /// slow camera to answer its handful of discovery calls; short enough that a port
    /// which silently drops packets gives way to the next candidate.</summary>
    private static readonly TimeSpan CandidateBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long discovery holds off after a reboot this client asked for.</summary>
    private static readonly TimeSpan AfterReboot = TimeSpan.FromSeconds(45);

    private string Conventional(string leaf)
    {
        // Rebuild "scheme://authority/onvif/<leaf>" from the device URL.
        var uri = new Uri(_deviceUrl);
        return $"{uri.Scheme}://{uri.Authority}/onvif/{leaf}";
    }

    /// <summary>A camera-reported service XAddr, forced onto the host we actually
    /// reached the camera at: some firmwares report XAddrs with an internal host
    /// (127.0.0.1 or a LAN name that doesn't resolve from here) but the right
    /// port/path. Null passes through; an unparseable value is used verbatim.</summary>
    internal static string? NormalizeXAddr(string? xaddr, string deviceUrl)
    {
        if (string.IsNullOrEmpty(xaddr)) return null;
        // Some firmwares write the port twice ("http://host:8106:8106/onvif/…"), which
        // no parser accepts; the duplicate is dropped first.
        var repaired = System.Text.RegularExpressions.Regex.Replace(xaddr,
            @"^(https?://[^/:\s]+:(\d+)):\2(?=/|$)", "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!Uri.TryCreate(repaired, UriKind.Absolute, out var u)) return xaddr;
        if (!Uri.TryCreate(deviceUrl, UriKind.Absolute, out var device)) return repaired;
        if (string.Equals(u.Host, device.Host, StringComparison.OrdinalIgnoreCase)) return repaired;
        return new UriBuilder(u) { Host = device.Host }.Uri.ToString();
    }

    private string? NormalizeXAddr(string? xaddr) => NormalizeXAddr(xaddr, _deviceUrl);

    // ------------------------------------------------------------ SOAP transport

    /// <param name="timeout">Overrides the default per-request ceiling. The event
    /// service's PullMessages deliberately asks the camera to HOLD the request until
    /// something happens, so it needs longer than a settings read ever should.</param>
    /// <param name="wsaAction">When set, WS-Addressing To/Action headers are added.
    /// A subscription manager is addressed that way rather than by URL alone, and
    /// cameras that implement the notification spec strictly refuse without them.</param>
    /// <param name="authenticate">False sends no WS-Security header at all. Only the
    /// clock read uses it: it exists to learn the correction every OTHER request
    /// needs, so it cannot itself carry a timestamp the camera might reject.</param>
    /// <param name="extraHeaders">Further SOAP header blocks, verbatim — the
    /// reference parameters a subscription manager must be addressed with.</param>
    private async Task<XElement?> CallAsync(string url, string opNamespace, string op, string innerBody,
        CancellationToken ct, TimeSpan? timeout = null, string? wsaAction = null, bool authenticate = true,
        string? extraHeaders = null) =>
        (await SendAsync(url, opNamespace, op, innerBody, ct, timeout, wsaAction, authenticate, extraHeaders)
            .ConfigureAwait(false)).Root;

    /// <summary>A request's outcome beside the reply: the HTTP status (0 when nothing
    /// came back) and the SOAP fault, if any, whatever the status.</summary>
    internal readonly record struct Reply(XElement? Root, int Status, string? Fault = null)
    {
        public void Deconstruct(out XElement? root, out int status) { root = Root; status = Status; }
    }

    /// <summary><see cref="CallAsync"/> with the HTTP status and fault beside the reply.
    /// Returned, not kept in a field: the event loop calls in here without the gate.</summary>
    private async Task<Reply> SendAsync(string url, string opNamespace, string op,
        string innerBody, CancellationToken ct, TimeSpan? timeout = null, string? wsaAction = null,
        bool authenticate = true, string? extraHeaders = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        // Stamped in the CAMERA's clock, not ours. The digest is only accepted
        // inside a few seconds of the camera's own time, and an IP camera with no
        // working NTP drifting by a minute would otherwise reject every request —
        // which reads as "wrong password" and leaves the whole panel empty.
        var security = authenticate
            ? BuildSecurity(_username, _password, nonce, DateTime.UtcNow + _clockSkew)
            : "";
        var prefix = opNamespace == NsMedia ? "trt"
            : opNamespace == NsMedia2 ? "tr2"
            : opNamespace == NsImaging ? "timg"
            : opNamespace == NsPtz ? "tptz"
            : opNamespace == NsEvents ? "tev"
            : opNamespace == NsWsnt ? "wsnt"
            : opNamespace == NsAnalytics ? "tan"
            : "tds";
        // Sent WITHOUT mustUnderstand. A camera that implements WS-Addressing reads
        // these either way; one that does not would be obliged by mustUnderstand to
        // FAULT the whole request over headers it could otherwise have ignored —
        // turning "works, loosely" into "no events at all".
        var addressing = wsaAction == null ? "" :
            $"<wsa:To xmlns:wsa=\"{NsWsa}\">{Esc(url)}</wsa:To>" +
            $"<wsa:Action xmlns:wsa=\"{NsWsa}\">{Esc(wsaAction)}</wsa:Action>";
        var envelope =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<s:Envelope xmlns:s=\"{NsSoap}\">" +
            $"<s:Header>{security}{addressing}{extraHeaders}</s:Header>" +
            $"<s:Body><{prefix}:{op} xmlns:{prefix}=\"{opNamespace}\" xmlns:tt=\"{NsSchema}\">" +
            innerBody +
            $"</{prefix}:{op}></s:Body></s:Envelope>";

        var limit = timeout ?? RequestTimeout;
        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        HttpResponseMessage res;
        int status;
        string text;
        try
        {
            res = await _http.PostAsync(url, content, deadline.Token).ConfigureAwait(false);
            using (res)
            {
                status = (int)res.StatusCode;
                text = await ReadBodyAsync(res, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { _lastError = $"{op}: no reply within {limit.TotalSeconds:0}s ({Safe(url)})"; return new Reply(null, 0); }
        catch (Exception ex) { _lastError = $"{op}: {ex.Message} ({Safe(url)})"; return new Reply(null, 0); }

        XElement? root = null;
        try { root = XDocument.Parse(text).Root; }
        catch (System.Xml.XmlException) { /* not XML — reported below */ }
        // A SOAP fault is the camera saying no, whatever HTTP status it rode in on
        // (some firmwares send every fault as 200).
        var fault = root == null ? null : SoapFault(root);
        if (fault != null || status is < 200 or > 299)
        {
            _lastError = $"{op} → HTTP {status}: {fault ?? Truncate(text, 200)}";
            // A refused login, or a rejected timestamp, is the one refusal worth
            // acting on: the clock is re-read and the panel told (see AuthRejected).
            if (authenticate && (status is 400 or 401 or 403 || fault != null) && LooksLikeAuthRefusal(fault, status))
                _authRejected = true;
            return new Reply(null, status, fault ?? (status is 401 or 403 ? "NotAuthorized" : null));
        }
        if (root == null) { _lastError = $"{op}: malformed reply"; return new Reply(null, status); }
        // An accepted stamped request is the camera's word that the login is good again.
        if (authenticate) _authRejected = false;
        return new Reply(root, status);
    }

    /// <summary>The reply body as text, read as UTF-8 bytes when its charset label is
    /// one .NET lacks (gb2312, "utf8"), which makes ReadAsStringAsync throw.</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static bool LooksLikeAuthRefusal(string? fault, int status) =>
        status is 401 or 403
        || (fault != null && (fault.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
                              || fault.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                              || fault.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                              || fault.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase)
                              || fault.Contains("InvalidSecurity", StringComparison.OrdinalIgnoreCase)
                              || fault.Contains("timestamp", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The SOAP fault in a parsed reply, subcodes and reason joined, or null
    /// when it is not a fault. The subcode tells a lasting refusal from a passing one.</summary>
    internal static string? SoapFault(XElement root)
    {
        if (root.Name.LocalName != "Envelope") return null;
        var body = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        var fault = body?.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault == null) return null;
        var codes = fault.Descendants().Where(e => e.Name.LocalName is "Value" or "faultcode")
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
        var text = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "Text" or "faultstring")?.Value?.Trim();
        var parts = codes.Concat(string.IsNullOrEmpty(text) ? Array.Empty<string>() : new[] { text });
        var joined = string.Join(": ", parts);
        return joined.Length == 0 ? "SOAP fault" : joined;
    }

    /// <summary>Whether an error reply is one the camera will give again (a missing
    /// endpoint, or a fault naming a missing action or thing), so the caller may stop asking.</summary>
    internal static bool IsLastingRefusal(int status, string? fault = null)
    {
        if (status is 404 or 405) return true;
        if (fault == null) return false;
        if (LooksLikeAuthRefusal(fault, status)) return false;
        foreach (var lasting in new[] { "NotSupported", "NoSuchService", "OperationProhibited", "NoConfig",
                     "NoProfile", "NoToken", "NoSource", "NoVideoSource", "InvalidArgVal", "InvalidArgs",
                     "WellFormed", "TagMismatch", "DataEncodingUnknown", "MissingAttr" })
            if (fault.Contains(lasting, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ------------------------------------------------------------ WS-Security

    /// <summary>The WS-Security UsernameToken header with a password digest:
    /// Base64(SHA1(nonce + created + password)). ONVIF's standard auth.</summary>
    internal static string BuildSecurity(string username, string password, byte[] nonce, DateTime createdUtc)
    {
        var created = createdUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var toHash = new byte[nonce.Length + Encoding.UTF8.GetByteCount(created) + Encoding.UTF8.GetByteCount(password)];
        var pos = 0;
        Buffer.BlockCopy(nonce, 0, toHash, pos, nonce.Length); pos += nonce.Length;
        pos += Encoding.UTF8.GetBytes(created, 0, created.Length, toHash, pos);
        Encoding.UTF8.GetBytes(password, 0, password.Length, toHash, pos);
        var digest = Convert.ToBase64String(SHA1.HashData(toHash));
        return
            $"<wsse:Security s:mustUnderstand=\"1\" xmlns:wsse=\"{NsWsse}\" xmlns:wsu=\"{NsWsu}\">" +
            "<wsse:UsernameToken>" +
            $"<wsse:Username>{Esc(username)}</wsse:Username>" +
            $"<wsse:Password Type=\"{PwDigestType}\">{digest}</wsse:Password>" +
            $"<wsse:Nonce EncodingType=\"{Base64Type}\">{Convert.ToBase64String(nonce)}</wsse:Nonce>" +
            $"<wsu:Created>{created}</wsu:Created>" +
            "</wsse:UsernameToken></wsse:Security>";
    }

    // ------------------------------------------------------------ parsing (local-name based, namespace-agnostic)

    private static XElement? Descendant(XElement? root, string localName) =>
        root?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? DescendantValue(XElement? root, string localName) =>
        Descendant(root, localName)?.Value?.Trim();

    /// <summary>The XAddr of a named capability block ("Media", "Imaging").</summary>
    internal static string? ServiceXAddr(XElement? root, string capabilityLocalName)
    {
        var cap = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == capabilityLocalName);
        var xaddr = cap?.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value?.Trim();
        return string.IsNullOrWhiteSpace(xaddr) ? null : xaddr;
    }

    /// <summary>The token of the first VideoSources element (attribute or child).</summary>
    internal static string? VideoSourceToken(XElement? root)
    {
        var vs = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VideoSources");
        var tok = vs?.Attribute("token")?.Value
                  ?? vs?.Descendants().FirstOrDefault(e => e.Name.LocalName == "token")?.Value;
        return string.IsNullOrWhiteSpace(tok) ? null : tok.Trim();
    }

    /// <summary>Parses an ImagingSettings reply, scaling each field to 0-255 using the
    /// (optional) ranges. IrCutFilter is passed through raw; WDR is on/off.</summary>
    internal static OnvifImaging ParseImaging(XElement? root, OnvifImagingRanges? ranges)
    {
        var settings = Descendant(root, "ImagingSettings") ?? root;
        double? Raw(string name) =>
            double.TryParse(DescendantValue(settings, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : null;
        var wdrMode = DescendantValue(Descendant(settings, "WideDynamicRange"), "Mode")
                      ?? (Descendant(settings, "WideDynamicRange") != null ? "OFF" : null);
        return new OnvifImaging(
            Brightness: ScaleToByte(Raw("Brightness"), ranges?.Brightness),
            Contrast: ScaleToByte(Raw("Contrast"), ranges?.Contrast),
            Saturation: ScaleToByte(Raw("ColorSaturation"), ranges?.Saturation),
            Sharpness: ScaleToByte(Raw("Sharpness"), ranges?.Sharpness),
            IrCutFilter: DescendantValue(settings, "IrCutFilter"),
            WideDynamicRange: wdrMode == null ? null : wdrMode.Equals("ON", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses GetOptions ranges (each is a Min/Max element under the field name).</summary>
    internal static OnvifImagingRanges ParseRanges(XElement? root)
    {
        (double, double)? Range(string name)
        {
            var el = Descendant(root, name);
            if (el == null) return null;
            var min = DescendantValue(el, "Min");
            var max = DescendantValue(el, "Max");
            if (double.TryParse(min, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
                && double.TryParse(max, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)
                && hi > lo)
                return (lo, hi);
            return null;
        }
        return new OnvifImagingRanges(Range("Brightness"), Range("Contrast"),
            Range("ColorSaturation"), Range("Sharpness"));
    }

    // ------------------------------------------------------------ scaling

    /// <summary>Camera-native value → 0-255. With a known range the value maps
    /// linearly onto 0-255 (so a camera's mid-point lands on the UI's 128); without
    /// one the value is clamped through unchanged (Reolink's ONVIF already uses 0-255).</summary>
    internal static int? ScaleToByte(double? value, (double Min, double Max)? range)
    {
        if (value is not { } v) return null;
        if (range is { } r && r.Max > r.Min)
            return (int)Math.Round(Math.Clamp((v - r.Min) / (r.Max - r.Min), 0, 1) * 255,
                MidpointRounding.AwayFromZero);
        return (int)Math.Round(Math.Clamp(v, 0, 255), MidpointRounding.AwayFromZero);
    }

    /// <summary>0-255 → camera-native value for a write.</summary>
    internal static double ScaleFromByte(int b, (double Min, double Max)? range)
    {
        var t = Math.Clamp(b, 0, 255) / 255.0;
        if (range is { } r && r.Max > r.Min)
            return r.Min + t * (r.Max - r.Min);
        return Math.Clamp(b, 0, 255);
    }

    // ------------------------------------------------------------ SetImagingSettings body

    /// <summary>Builds the ImagingSettings body for a write. Fields must appear in the
    /// ONVIF schema's sequence order (Brightness, ColorSaturation, Contrast,
    /// IrCutFilter, Sharpness, WideDynamicRange) or strict parsers reject the request.
    /// Only the fields the caller set are emitted; the rest are left untouched.</summary>
    internal static string BuildSetImaging(string token, OnvifImagingRanges? ranges,
        int? brightness, int? contrast, int? saturation, int? sharpness,
        string? irCutFilter, bool? wideDynamicRange)
    {
        string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append($"<timg:VideoSourceToken>{Esc(token)}</timg:VideoSourceToken>");
        sb.Append("<timg:ImagingSettings>");
        if (brightness is { } b) sb.Append($"<tt:Brightness>{Num(ScaleFromByte(b, ranges?.Brightness))}</tt:Brightness>");
        if (saturation is { } s) sb.Append($"<tt:ColorSaturation>{Num(ScaleFromByte(s, ranges?.Saturation))}</tt:ColorSaturation>");
        if (contrast is { } c) sb.Append($"<tt:Contrast>{Num(ScaleFromByte(c, ranges?.Contrast))}</tt:Contrast>");
        if (irCutFilter is { Length: > 0 } ir) sb.Append($"<tt:IrCutFilter>{Esc(ir)}</tt:IrCutFilter>");
        if (sharpness is { } sh) sb.Append($"<tt:Sharpness>{Num(ScaleFromByte(sh, ranges?.Sharpness))}</tt:Sharpness>");
        if (wideDynamicRange is { } w) sb.Append($"<tt:WideDynamicRange><tt:Mode>{(w ? "ON" : "OFF")}</tt:Mode></tt:WideDynamicRange>");
        sb.Append("</timg:ImagingSettings>");
        sb.Append("<timg:ForcePersistence>true</timg:ForcePersistence>");
        return sb.ToString();
    }

    // ------------------------------------------------------------ misc

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
