// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Neolink.Streaming;

namespace Neolink.Onvif;

/// <summary>An opt-in ONVIF endpoint offering Reolink cameras' pan/tilt, zoom and presets, for NVRs that drive PTZ
/// only over ONVIF (Frigate): one profile per camera, each named after it. Video stays on RTSP; logins are the RTSP users.</summary>
public sealed partial class OnvifPtzServer
{
    private const int MaxHeader = 16 * 1024;
    private const int MaxBody = 256 * 1024;
    private const int MaxConnections = 16;
    private const int MaxConnectionsPerAddress = 4;
    /// <summary>A new connection must send its first request within this; a kept-alive one may idle longer.</summary>
    private static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How far a signed request's Created time may be from this server's clock.</summary>
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);
    /// <summary>A move with no Stop ends by itself after this, unless the request names its own timeout.</summary>
    internal static readonly TimeSpan DefaultMoveTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaxMoveTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Velocities below this on both axes are a stop.</summary>
    private const double DeadZone = 0.05;
    private const int MaxNonces = 4096;
    private const int MaxRefusalsTracked = 256;

    private readonly string _tag;
    private readonly IReadOnlyDictionary<string, string> _users;
    private readonly IReadOnlyList<OnvifPtzCamera> _cameras;

    private readonly Dictionary<string, DateTime> _nonces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _contacted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _refusalLogged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perAddress = new(StringComparer.Ordinal);

    /// <param name="tag">How the log names this endpoint.</param>
    /// <param name="users">Every configured user and password (the RTSP users).</param>
    public OnvifPtzServer(string tag, IReadOnlyDictionary<string, string> users, IReadOnlyList<OnvifPtzCamera> cameras)
    {
        _tag = tag;
        _users = users;
        _cameras = cameras;
    }

    /// <summary>Whether some camera here needs a login (not every one is open, as it would be over RTSP).</summary>
    private bool AnyLogin => _cameras.Any(c => !NetUtil.Allows(_users, c.Permitted, null));

    // ------------------------------------------------------------------ HTTP

    public async Task RunAsync(string bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(bind), port);
        listener.Start(); // a port in use throws here, for the caller to report
        var host = NetUtil.DisplayHost(bind);
        var login = AnyLogin ? ", user and password: an RTSP user permitted on the camera" : "";
        Log.Info(_cameras.Count == 1
            ? $"{_tag} on http://{host}:{port}/onvif/device_service — in Frigate: onvif host {host}, port {port}{login}"
            : $"{_tag} on http://{host}:{port}/onvif/device_service for {string.Join(", ", _cameras.Select(c => c.Name))} — " +
              $"in Frigate 0.18+: onvif host {host}, port {port}, profile: the camera's name{login}");
        foreach (var cam in _cameras) cam.StartCapabilitiesRead();
        var slots = new SemaphoreSlim(MaxConnections);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                var remote = RemoteOf(client);
                // Over either limit the connection is closed: a PTZ client needs one or two.
                if (!TakeAddressSlot(remote))
                {
                    client.Dispose();
                    continue;
                }
                if (!slots.Wait(0))
                {
                    ReleaseAddressSlot(remote);
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await ServeAsync(client, remote, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    catch (Exception ex) { Log.Debug($"{_tag}: connection failed: {Log.Flatten(ex)}"); }
                    finally
                    {
                        client.Dispose();
                        ReleaseAddressSlot(remote);
                        slots.Release();
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(_cameras.Select(c => c.StopIfMovingAsync())).ConfigureAwait(false);
        }
    }

    private static string RemoteOf(TcpClient client) =>
        (client.Client.RemoteEndPoint as IPEndPoint)?.Address is { } a
            ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() : "?";

    private bool TakeAddressSlot(string remote)
    {
        lock (_perAddress)
        {
            _perAddress.TryGetValue(remote, out var n);
            if (n >= MaxConnectionsPerAddress) return false;
            _perAddress[remote] = n + 1;
            return true;
        }
    }

    private void ReleaseAddressSlot(string remote)
    {
        lock (_perAddress)
            if (_perAddress.TryGetValue(remote, out var n))
            {
                if (n <= 1) _perAddress.Remove(remote);
                else _perAddress[remote] = n - 1;
            }
    }

    private async Task ServeAsync(TcpClient client, string remote, CancellationToken ct)
    {
        client.NoDelay = true;
        var local = client.Client.LocalEndPoint as IPEndPoint;
        lock (_contacted)
            if (_contacted.Count < 64 && _contacted.Add(remote))
                Log.Info($"{_tag}: contacted by {remote}");
        var stream = client.GetStream();
        var buf = new byte[MaxHeader];
        int filled = 0;
        for (bool first = true; !ct.IsCancellationRequested; first = false)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(first ? FirstRequestTimeout : IdleTimeout);
            int end;
            while ((end = buf.AsSpan(0, filled).IndexOf("\r\n\r\n"u8)) < 0)
            {
                if (filled == buf.Length)
                {
                    await RespondAsync(stream, 431, "", close: true, ct).ConfigureAwait(false);
                    return;
                }
                int n = await stream.ReadAsync(buf.AsMemory(filled), idle.Token).ConfigureAwait(false);
                if (n == 0) return;
                filled += n;
            }
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var request = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
            int consumed = end + 4;
            if (request.Length < 3)
            {
                await RespondAsync(stream, 400, "", close: true, ct).ConfigureAwait(false);
                return;
            }
            string connection = headers.GetValueOrDefault("Connection") ?? "";
            bool keepAlive = request[2] == "HTTP/1.1"
                ? !connection.Contains("close", StringComparison.OrdinalIgnoreCase)
                : connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
            if (request[0] != "POST")
            {
                await RespondAsync(stream, 405, "", close: true, ct, "Allow: POST\r\n").ConfigureAwait(false);
                return;
            }
            if (headers.ContainsKey("Transfer-Encoding")
                || !int.TryParse(headers.GetValueOrDefault("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                await RespondAsync(stream, 411, "", close: true, ct).ConfigureAwait(false);
                return;
            }
            if (length > MaxBody)
            {
                await RespondAsync(stream, 413, "", close: true, ct).ConfigureAwait(false);
                return;
            }
            if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);

            var body = new byte[length];
            int have = Math.Min(length, filled - consumed);
            Buffer.BlockCopy(buf, consumed, body, 0, have);
            int rest = filled - consumed - have; // a pipelined next request
            Buffer.BlockCopy(buf, consumed + have, buf, 0, rest);
            filled = rest;
            while (have < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(have), idle.Token).ConfigureAwait(false);
                if (n == 0) return;
                have += n;
            }

            using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
            work.CancelAfter(RequestTimeout);
            var (status, xml) = await HandleAsync(Encoding.UTF8.GetString(body), BaseUrl(headers.GetValueOrDefault("Host"), local),
                headers.GetValueOrDefault("Authorization"), remote, work.Token).ConfigureAwait(false);
            await RespondAsync(stream, status, xml, close: !keepAlive, ct).ConfigureAwait(false);
            if (!keepAlive) return;
        }
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string body, bool close,
        CancellationToken ct, string extraHeaders = "")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                   (bytes.Length > 0 ? "Content-Type: application/soap+xml; charset=utf-8\r\n" : "") +
                   $"Content-Length: {bytes.Length}\r\n" +
                   (close ? "Connection: close\r\n" : "") + extraHeaders + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        if (bytes.Length > 0) await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        405 => "Method Not Allowed",
        411 => "Length Required",
        413 => "Content Too Large",
        431 => "Request Header Fields Too Large",
        _ => "Internal Server Error",
    };

    [GeneratedRegex(@"^[A-Za-z0-9.\-_:\[\]]{1,255}$")]
    private static partial Regex HostHeader();

    /// <summary>The address service URLs are given on: the one the client used, else this socket's own.</summary>
    internal static string BaseUrl(string? host, IPEndPoint? local)
    {
        if (host != null && HostHeader().IsMatch(host)) return "http://" + host;
        if (local == null) return "http://127.0.0.1";
        var ip = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{ip}]:{local.Port}" : $"http://{ip}:{local.Port}";
    }

    // ------------------------------------------------------------------ SOAP

    /// <summary>Answers one SOAP request: the HTTP status and the reply envelope.</summary>
    internal async Task<(int Status, string Body)> HandleAsync(string soap, string baseUrl, string? authorization,
        string remote, CancellationToken ct)
    {
        XElement envelope;
        try { envelope = ParseXml(soap); }
        catch (XmlException) { return Fault(sender: true, "ter:WellFormed", "the request is not well-formed XML"); }
        var op = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();
        if (envelope.Name.LocalName != "Envelope" || op == null)
            return Fault(sender: true, "ter:WellFormed", "the request carries no SOAP body");
        string service = op.Name.NamespaceName, action = op.Name.LocalName;

        // What ONVIF lets anyone ask: a client learns the clock and the services before it signs in.
        if (service == NsDevice)
            switch (action)
            {
                case "GetSystemDateAndTime": return Ok(SystemDateAndTime(DateTime.UtcNow));
                case "GetCapabilities": return Ok(Capabilities(baseUrl));
                case "GetServices": return Ok(Services(baseUrl));
                case "GetServiceCapabilities": return Ok(DeviceServiceCapabilities);
            }

        // Everything else sees only the cameras this login may use; with none, it is refused.
        var (user, refused) = Authenticate(envelope, authorization, DateTime.UtcNow);
        var visible = refused != null ? new List<OnvifPtzCamera>()
            : _cameras.Where(c => NetUtil.Allows(_users, c.Permitted, user)).ToList();
        if (visible.Count == 0)
        {
            refused ??= user == null ? "sign in with an RTSP user permitted on the camera"
                : $"user \"{user}\" is not permitted on any camera here";
            NoteRefusal(remote, refused);
            await Task.Delay(500, ct).ConfigureAwait(false); // slows a password guesser
            return Fault(sender: true, "ter:NotAuthorized", refused);
        }

        switch (service, action)
        {
            case (NsDevice, "GetDeviceInformation"):
                return Ok(DeviceInformation(visible));
            case (NsMedia, "GetProfiles"):
                return Ok($"<trt:GetProfilesResponse>{string.Concat(visible.Select(c => Profile("trt:Profiles", c)))}</trt:GetProfilesResponse>");
            case (NsMedia, "GetVideoSources"):
                return Ok($"<trt:GetVideoSourcesResponse>{string.Concat(visible.Select(VideoSource))}</trt:GetVideoSourcesResponse>");
            case (NsMedia, "GetServiceCapabilities"):
                return Ok(MediaServiceCapabilities(visible.Count));
            case (NsPtz, "GetServiceCapabilities"):
                return Ok(PtzServiceCapabilities);
            case (NsPtz, "GetConfigurations"):
                return Ok($"<tptz:GetConfigurationsResponse>{string.Concat(visible.Where(c => c.OffersPtz()).Select(c => PtzConfiguration("tptz:PTZConfiguration", c)))}</tptz:GetConfigurationsResponse>");
            case (NsPtz, "GetNodes"):
                return Ok($"<tptz:GetNodesResponse>{string.Concat(visible.Where(c => c.OffersPtz()).Select(PtzNode))}</tptz:GetNodesResponse>");
        }

        // The rest act on one camera, named by the request's profile, configuration or node token.
        (string Element, Func<OnvifPtzCamera, string> TokenOf)? named = (service, action) switch
        {
            (NsPtz, "GetConfiguration") => ("PTZConfigurationToken", PtzConfigTokenOf),
            (NsPtz, "GetConfigurationOptions") => ("ConfigurationToken", PtzConfigTokenOf),
            (NsPtz, "GetNode") => ("NodeToken", NodeTokenOf),
            (NsMedia, "GetProfile") or (NsPtz, "GetPresets" or "GotoPreset" or "SetPreset" or "GetStatus" or "ContinuousMove" or "Stop")
                => ("ProfileToken", c => c.ProfileToken),
            _ => null,
        };
        if (named is not { } by)
            return Fault(sender: false, "ter:ActionNotSupported",
                $"{action} is not offered: this endpoint serves the cameras' pan/tilt, zoom and presets only (video is on Neolink's RTSP server)");
        if (Target(op, visible, by.Element, by.TokenOf) is not { } cam)
            return Fault(sender: true, "ter:InvalidArgVal", by.Element == "ProfileToken"
                ? "no such profile: name the camera in the request's ProfileToken (in Frigate, onvif.profile)"
                : $"no such {by.Element}");
        return action switch
        {
            "GetConfiguration" => Ok($"<tptz:GetConfigurationResponse>{PtzConfiguration("tptz:PTZConfiguration", cam)}</tptz:GetConfigurationResponse>"),
            "GetConfigurationOptions" => Ok(PtzConfigurationOptions(cam)),
            "GetNode" => Ok($"<tptz:GetNodeResponse>{PtzNode(cam)}</tptz:GetNodeResponse>"),
            "GetProfile" => Ok($"<trt:GetProfileResponse>{Profile("trt:Profile", cam)}</trt:GetProfileResponse>"),
            "GetPresets" => await GetPresetsAsync(cam, ct).ConfigureAwait(false),
            "GotoPreset" => await GotoPresetAsync(op, cam, ct).ConfigureAwait(false),
            "SetPreset" => await SetPresetAsync(op, cam, ct).ConfigureAwait(false),
            "GetStatus" => Ok(PtzStatus(cam.Moving, cam.Zooming, DateTime.UtcNow)),
            "ContinuousMove" => await ContinuousMoveAsync(op, cam, ct).ConfigureAwait(false),
            _ => await StopRequestAsync(op, cam, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The camera a request's token names; with none given, the only camera this login sees.</summary>
    private static OnvifPtzCamera? Target(XElement op, List<OnvifPtzCamera> visible, string element,
        Func<OnvifPtzCamera, string> tokenOf)
    {
        var token = Child(op, element);
        return string.IsNullOrEmpty(token)
            ? visible.Count == 1 ? visible[0] : null
            : visible.FirstOrDefault(c => tokenOf(c) == token);
    }

    private static XElement? ChildElement(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Child(XElement op, string name) => ChildElement(op, name)?.Value.Trim();

    private static XElement ParseXml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBody * 2,
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XElement.Load(reader);
    }

    /// <summary>The configured user a request signs in as (HTTP Basic, or a WS-Security UsernameToken, digest
    /// or text), null when it carries no login; Refused says why a login it does carry is not accepted.</summary>
    internal (string? User, string? Refused) Authenticate(XElement envelope, string? authorization, DateTime nowUtc)
    {
        if (_users.Count == 0) return (null, null); // no users: nothing to sign in as, as for RTSP
        const string refused = "the ONVIF login was refused";
        if (NetUtil.DecodeBasicAuth(authorization) is { } basic)
            return Verified(basic.User, basic.Pass) ? (basic.User, null) : (null, refused);
        var token = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Header")?
            .Descendants().FirstOrDefault(e => e.Name.LocalName == "UsernameToken");
        if (token == null) return (null, null);
        XElement? Child(string name) => ChildElement(token, name);
        var user = Child("Username")?.Value.Trim() ?? "";
        if (Child("Password") is not { } password || !_users.TryGetValue(user, out var expected)) return (null, refused);
        bool digest = password.Attribute("Type")?.Value.EndsWith("#PasswordDigest", StringComparison.Ordinal) == true;
        if (!digest) return NetUtil.FixedTimeEquals(expected, password.Value) ? (user, null) : (null, refused);

        if (Child("Nonce")?.Value.Trim() is not { Length: > 0 } nonce || Child("Created")?.Value is not { Length: > 0 } created)
            return (null, refused);
        byte[] nonceBytes;
        try { nonceBytes = Convert.FromBase64String(nonce); }
        catch (FormatException) { return (null, refused); }
        if (!DateTime.TryParse(created.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            return (null, refused);
        if ((nowUtc - at).Duration() > ClockTolerance)
            return (null, $"the request was signed {(nowUtc - at).Duration().TotalMinutes:0} min from this server's clock: " +
                          "sync the clocks, or set ignore_time_mismatch in Frigate's onvif section");
        if (!NetUtil.FixedTimeEquals(Protocol.OnvifClient.PasswordDigest(nonceBytes, created, expected), password.Value.Trim()))
            return (null, refused);
        return RememberNonce(nonce + "|" + created, nowUtc) ? (user, null) : (null, "the request was already used");
    }

    private bool Verified(string user, string pass) =>
        _users.TryGetValue(user, out var expected) && NetUtil.FixedTimeEquals(expected, pass);

    /// <summary>False for a nonce already seen: a captured request must not move the camera again. Only
    /// nonces inside the clock window matter, and the oldest go first should that many be current.</summary>
    private bool RememberNonce(string key, DateTime nowUtc)
    {
        lock (_nonces)
        {
            if (_nonces.ContainsKey(key)) return false;
            if (_nonces.Count >= MaxNonces / 2)
                foreach (var old in _nonces.Where(kv => nowUtc - kv.Value > 2 * ClockTolerance).Select(kv => kv.Key).ToList())
                    _nonces.Remove(old);
            if (_nonces.Count >= MaxNonces)
                foreach (var old in _nonces.OrderBy(kv => kv.Value).Take(MaxNonces / 4).Select(kv => kv.Key).ToList())
                    _nonces.Remove(old);
            _nonces[key] = nowUtc;
            return true;
        }
    }

    private void NoteRefusal(string remote, string why)
    {
        var now = DateTime.UtcNow;
        lock (_refusalLogged)
        {
            if (_refusalLogged.TryGetValue(remote, out var at) && now - at < TimeSpan.FromMinutes(10)) return;
            if (_refusalLogged.Count >= MaxRefusalsTracked)
                foreach (var old in _refusalLogged.Where(kv => now - kv.Value >= TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                    _refusalLogged.Remove(old);
            if (_refusalLogged.Count >= MaxRefusalsTracked) return; // a flood of addresses: stay quiet rather than grow
            _refusalLogged[remote] = now;
        }
        Log.Warn($"{_tag}: refused {remote}: {why}");
    }

    // ------------------------------------------------------------------ movement

    private static async Task<(int, string)> ContinuousMoveAsync(XElement op, OnvifPtzCamera cam, CancellationToken ct)
    {
        var velocity = ChildElement(op, "Velocity");
        var panTilt = velocity == null ? null : ChildElement(velocity, "PanTilt");
        var zoom = velocity == null ? null : ChildElement(velocity, "Zoom");
        var timeout = MoveTimeout(Child(op, "Timeout"));
        try
        {
            // Only the axes the request names: Frigate's zoom buttons send a zoom velocity alone.
            if (panTilt != null || zoom == null)
            {
                if (Direction(Num(panTilt?.Attribute("x")), Num(panTilt?.Attribute("y"))) is { } move)
                    await cam.MoveAsync(move.Command, move.Speed, timeout, ct).ConfigureAwait(false);
                else
                    await cam.MoveAsync("stop", 32, timeout, ct).ConfigureAwait(false);
            }
            if (zoom != null)
            {
                var z = Num(zoom.Attribute("x"));
                if (!double.IsFinite(z) || Math.Abs(z) < DeadZone) cam.StopZoom();
                else await cam.ZoomAsync(z, timeout, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { return CommandFault(ex); }
        return Ok("<tptz:ContinuousMoveResponse/>");
    }

    private static async Task<(int, string)> StopRequestAsync(XElement op, OnvifPtzCamera cam, CancellationToken ct)
    {
        // Both flags default to true.
        if (!string.Equals(Child(op, "Zoom"), "false", StringComparison.OrdinalIgnoreCase)) cam.StopZoom();
        if (string.Equals(Child(op, "PanTilt"), "false", StringComparison.OrdinalIgnoreCase)) return Ok("<tptz:StopResponse/>");
        try { await cam.MoveAsync("stop", 32, DefaultMoveTimeout, ct).ConfigureAwait(false); }
        catch (Exception ex) { return CommandFault(ex); }
        return Ok("<tptz:StopResponse/>");
    }

    /// <summary>The presets in use; none when the camera has no HTTP API to keep them, or does not answer.</summary>
    private static async Task<(int, string)> GetPresetsAsync(OnvifPtzCamera cam, CancellationToken ct)
    {
        IReadOnlyList<PtzPresetInfo>? slots;
        try { slots = await cam.PresetsAsync(ct).ConfigureAwait(false); }
        catch (Exception) { slots = null; }
        return Ok($"<tptz:GetPresetsResponse>{string.Concat((slots ?? []).Where(p => p.Enabled).Select(Preset))}</tptz:GetPresetsResponse>");
    }

    private static async Task<(int, string)> GotoPresetAsync(XElement op, OnvifPtzCamera cam, CancellationToken ct)
    {
        try
        {
            // Checked against the list Frigate last read (no round trip per move); unread, the camera judges.
            if (!int.TryParse(Child(op, "PresetToken"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || cam.KnownPresets is { } known && !known.Any(p => p.Id == id && p.Enabled))
                return Fault(sender: true, "ter:InvalidArgVal", "no such preset: GetPresets lists the camera's");
            await cam.GotoPresetAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return CommandFault(ex); }
        return Ok("<tptz:GotoPresetResponse/>");
    }

    /// <summary>Saves where the head points: over the preset the token names, else into the first free slot.
    /// A preset kept without a new name keeps its old one.</summary>
    private static async Task<(int, string)> SetPresetAsync(XElement op, OnvifPtzCamera cam, CancellationToken ct)
    {
        var token = Child(op, "PresetToken");
        try
        {
            if (await cam.PresetsAsync(ct).ConfigureAwait(false) is not { } slots)
                return Fault(sender: false, "ter:Action",
                    "the camera keeps its presets in its HTTP API, which is not answering (if it never does, check http_address)");
            var slot = string.IsNullOrEmpty(token) ? slots.FirstOrDefault(p => !p.Enabled)
                : int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? slots.FirstOrDefault(p => p.Id == id)
                : null;
            if (slot == null)
                return string.IsNullOrEmpty(token)
                    ? Fault(sender: false, "ter:TooManyPresets", "every preset slot on the camera is in use")
                    : Fault(sender: true, "ter:InvalidArgVal", "no such preset: GetPresets lists the camera's");
            var name = Child(op, "PresetName") is { Length: > 0 } given ? given
                : slot.Enabled ? slot.Name : $"preset {slot.Id}";
            await cam.SavePresetAsync(slot.Id, name, ct).ConfigureAwait(false);
            return Ok($"<tptz:SetPresetResponse><tptz:PresetToken>{slot.Id}</tptz:PresetToken></tptz:SetPresetResponse>");
        }
        catch (Exception ex) { return CommandFault(ex); }
    }

    private static (int, string) CommandFault(Exception ex) => ex switch
    {
        OperationCanceledException => Fault(sender: false, "ter:Action", "the camera did not answer in time"),
        CameraOfflineException => Fault(sender: false, "ter:Action", "the camera is offline or asleep"),
        _ => Fault(sender: false, "ter:Action", $"the camera refused it: {ex.Message}"),
    };

    /// <summary>The camera command for an ONVIF velocity: its stronger axis, at a 1-64 speed; null = stop.
    /// Reolink's head moves on one axis at a time.</summary>
    internal static (string Command, float Speed)? Direction(double pan, double tilt)
    {
        if (!double.IsFinite(pan)) pan = 0;
        if (!double.IsFinite(tilt)) tilt = 0;
        double ax = Math.Abs(pan), ay = Math.Abs(tilt), strongest = Math.Max(ax, ay);
        if (strongest < DeadZone) return null;
        var speed = (float)Math.Clamp(Math.Round(strongest * 64, MidpointRounding.AwayFromZero), 1, 64);
        return ax >= ay ? (pan > 0 ? "right" : "left", speed) : (tilt > 0 ? "up" : "down", speed);
    }

    /// <summary>A request's Timeout (xs:duration), within 1-60 s; the default when absent or unreadable.</summary>
    internal static TimeSpan MoveTimeout(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return DefaultMoveTimeout;
        try
        {
            var t = XmlConvert.ToTimeSpan(duration.Trim());
            return t < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : t > MaxMoveTimeout ? MaxMoveTimeout : t;
        }
        catch (FormatException) { return DefaultMoveTimeout; }
        catch (OverflowException) { return MaxMoveTimeout; }
    }

    private static double Num(XAttribute? a) =>
        a != null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
