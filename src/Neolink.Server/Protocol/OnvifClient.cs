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
public sealed class OnvifClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    // ONVIF / WS namespaces.
    private const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
    private const string NsWsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string NsWsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
    private const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
    private const string NsImaging = "http://www.onvif.org/ver20/imaging/wsdl";
    private const string NsSchema = "http://www.onvif.org/ver10/schema";
    private const string PwDigestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64Type = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    private readonly HttpClient _http;
    private readonly string _deviceUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _tag;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _ready;              // discovery succeeded; endpoints + token cached
    private DateTime _retryAfter;     // after a failed discovery, don't re-probe until this time
    private string? _imagingUrl;
    private string? _mediaUrl;
    private string? _videoSourceToken;
    private OnvifImagingRanges? _ranges;

    /// <param name="address">"host", "host:port", or a full "http(s)://host[:port]"
    /// URL. The device service path (/onvif/device_service) is appended when the
    /// address is a bare host/authority.</param>
    public OnvifClient(string address, string username, string? password, string tag)
    {
        var a = address.Trim();
        string baseUrl;
        if (a.Contains("://", StringComparison.Ordinal))
            baseUrl = a.TrimEnd('/');
        else
            baseUrl = $"http://{a}".TrimEnd('/');
        // A bare authority gets the conventional device-service path; a caller that
        // passed a full service URL (onvif_address) is taken at its word.
        _deviceUrl = baseUrl.Contains("/onvif/", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/onvif/device_service";
        _username = username;
        _password = password ?? "";
        _tag = tag;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        if (_deviceUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        _http = new HttpClient(handler) { Timeout = RequestTimeout };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Best-effort imaging read (0-255 scaled), or null when ONVIF is
    /// unavailable on this camera. Never throws.</summary>
    public async Task<OnvifImaging?> TryGetImagingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false)) return null;
            var xml = await CallAsync(_imagingUrl!, NsImaging, "GetImagingSettings",
                $"<timg:VideoSourceToken>{Esc(_videoSourceToken!)}</timg:VideoSourceToken>", ct)
                .ConfigureAwait(false);
            if (xml == null) return null;
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
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false))
                throw new NotSupportedException("the camera's ONVIF imaging service is not reachable");
            var body = BuildSetImaging(_videoSourceToken!, _ranges,
                brightness, contrast, saturation, sharpness, irCutFilter, wideDynamicRange);
            var xml = await CallAsync(_imagingUrl!, NsImaging, "SetImagingSettings", body, ct).ConfigureAwait(false);
            if (xml == null)
                throw new IOException("the camera did not confirm the ONVIF imaging change");
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------ discovery

    private async Task<bool> EnsureDiscoveredAsync(CancellationToken ct)
    {
        // Cached success is permanent for the run; a failure only pauses re-probing
        // for a cooldown (a camera rebooting during the first probe must not be
        // written off until Neolink restarts) — but not so often it stalls callers.
        if (_ready) return true;
        if (DateTime.UtcNow < _retryAfter) return false;
        try
        {
            // 1. Device GetCapabilities → the Media and Imaging service URLs. Some
            //    firmwares return absolute XAddrs on a different host/port; others
            //    return the conventional paths. Fall back to conventions on a miss.
            var caps = await CallAsync(_deviceUrl, NsDevice, "GetCapabilities",
                "<tds:Category>All</tds:Category>", ct).ConfigureAwait(false);
            _mediaUrl = ServiceXAddr(caps, "Media") ?? Conventional("media_service");
            _imagingUrl = ServiceXAddr(caps, "Imaging") ?? Conventional("imaging");

            // 2. Media GetVideoSources → the token the imaging service is keyed on.
            var sources = await CallAsync(_mediaUrl, NsMedia, "GetVideoSources", "", ct).ConfigureAwait(false);
            _videoSourceToken = VideoSourceToken(sources);
            if (_videoSourceToken == null)
            {
                Log.Debug($"{_tag}: ONVIF exposed no video source — imaging fallback unavailable");
                _retryAfter = DateTime.UtcNow + RetryCooldown;
                return false;
            }

            // 3. Imaging GetOptions → accepted ranges, so 0-255 UI values scale to the
            //    camera's native units (and back). Optional: without it we pass values
            //    through unscaled.
            var options = await CallAsync(_imagingUrl, NsImaging, "GetOptions",
                $"<timg:VideoSourceToken>{Esc(_videoSourceToken)}</timg:VideoSourceToken>", ct)
                .ConfigureAwait(false);
            _ranges = ParseRanges(options);
            _ready = true;
            Log.Info($"{_tag}: ONVIF imaging fallback ready (video source '{_videoSourceToken}')");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"{_tag}: ONVIF discovery failed ({Log.Flatten(ex)}) — imaging fallback paused for " +
                      $"{RetryCooldown.TotalMinutes:0} min");
            _retryAfter = DateTime.UtcNow + RetryCooldown;
            return false;
        }
    }

    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(5);

    private string Conventional(string leaf)
    {
        // Rebuild "scheme://authority/onvif/<leaf>" from the device URL.
        var uri = new Uri(_deviceUrl);
        return $"{uri.Scheme}://{uri.Authority}/onvif/{leaf}";
    }

    // ------------------------------------------------------------ SOAP transport

    private async Task<XElement?> CallAsync(string url, string opNamespace, string op, string innerBody,
        CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var security = BuildSecurity(_username, _password, nonce, DateTime.UtcNow);
        var prefix = op.StartsWith("GetCapabilities", StringComparison.Ordinal) ? "tds"
            : opNamespace == NsMedia ? "trt"
            : opNamespace == NsImaging ? "timg"
            : "tds";
        var envelope =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<s:Envelope xmlns:s=\"{NsSoap}\">" +
            $"<s:Header>{security}</s:Header>" +
            $"<s:Body><{prefix}:{op} xmlns:{prefix}=\"{opNamespace}\" xmlns:tt=\"{NsSchema}\">" +
            innerBody +
            $"</{prefix}:{op}></s:Body></s:Envelope>";

        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        using var res = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            // A SOAP fault body carries the reason — surface it at Debug.
            Log.Debug($"{_tag}: ONVIF {op} → HTTP {(int)res.StatusCode}: {Truncate(text, 300)}");
            return null;
        }
        try { return XDocument.Parse(text).Root; }
        catch (System.Xml.XmlException) { return null; }
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
