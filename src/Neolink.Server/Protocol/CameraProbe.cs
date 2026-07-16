using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Neolink.Config;
using Neolink.Protocol;

namespace Neolink;

/// <summary>
/// Opt-in camera-discovery diagnostic ("udp_probe": true). When a camera can't be
/// reached over the normal TCP Baichuan path, this sweeps every avenue we know of
/// and logs one comprehensive report — so a reporter pastes a single log and we
/// learn, in one go, how the camera is reachable and what it can do, instead of a
/// dozen back-and-forth questions.
///
/// The sweep, all LAN-local (no cloud contact):
///   1. TCP port map      — is anything listening (9000/80/443/554), from the
///                          server's own vantage (more authoritative than a
///                          user's nmap from a different host)
///   2. Reolink HTTP API  — if 80/443 answer, dump DevInfo + the full Ability
///                          table + SD slots: model, firmware and every supported
///                          capability, no reverse engineering (battery cams often
///                          answer this while awake)
///   3. ONVIF WS-Discovery — a standard multicast probe; device service URL + scopes
///   4. Baichuan-over-UDP  — the discovery handshake battery-only models speak
///                          (see <see cref="UdpDiscovery"/>)
/// then a consolidated verdict + recommendation.
///
/// No credentials are logged; the UID and the device serial are masked.
/// </summary>
public static class CameraProbe
{
    private static readonly (int Port, string Name)[] TcpPorts =
    {
        (9000, "Baichuan"), (80, "HTTP"), (443, "HTTPS"), (554, "RTSP"),
    };

    public static async Task SweepAsync(string tag, CameraConfig cfg, CancellationToken ct)
    {
        string Mask(string s) => string.IsNullOrEmpty(cfg.Uid) ? s : UdpDiscovery.MaskUid(s, cfg.Uid!);
        string P(string msg) => $"{tag}: [discover] {Mask(msg)}";

        Log.Info(P($"=== camera discovery sweep — host {cfg.Host}, uid {(string.IsNullOrEmpty(cfg.Uid) ? "(none)" : cfg.Uid)} ==="));

        var ip = await ResolveV4Async(P, cfg.Host, ct).ConfigureAwait(false);
        if (ip == null) return;

        // 1. TCP reachability map.
        var open = await TcpMapAsync(P, ip, cfg.Port, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        // 2. Reolink HTTP API — the capability jackpot when it answers.
        var http = await HttpDumpAsync(P, cfg, ip, open, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        // 3. ONVIF WS-Discovery.
        var onvif = await OnvifProbeAsync(P, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        // 4. Baichuan-over-UDP discovery (needs a UID).
        UdpDiscovery.UdpOutcome udp;
        if (string.IsNullOrEmpty(cfg.Uid))
        {
            Log.Info(P("UDP: skipped — no \"uid\" set (add it from the Reolink app/sticker to probe the UDP handshake)"));
            udp = UdpDiscovery.UdpOutcome.Aborted;
        }
        else
        {
            udp = await UdpDiscovery.ProbeAsync(tag, ip, cfg.Uid!, ct).ConfigureAwait(false);
        }
        if (ct.IsCancellationRequested) return;

        // Consolidated verdict.
        Log.Info(P("SUMMARY: " + Recommend(open, http, onvif.Count, udp) +
                   " — please paste every [discover…] line into GitHub issue #39."));
    }

    // ------------------------------------------------------------------ steps

    private static async Task<IPAddress?> ResolveV4Async(Func<string, string> P, string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            Log.Warn(P("no \"address\" to probe — sweep aborted"));
            return null;
        }
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                Log.Warn(P($"{host} is IPv6; the sweep is IPv4-only — aborted"));
                return null;
            }
            return literal;
        }
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 == null)
            {
                Log.Warn(P($"'{host}' has no IPv4 address — sweep aborted"));
                return null;
            }
            Log.Info(P($"resolved {host} → {v4}"));
            return v4;
        }
        catch (Exception ex)
        {
            Log.Warn(P($"cannot resolve '{host}': {ex.Message} — sweep aborted"));
            return null;
        }
    }

    private static async Task<HashSet<int>> TcpMapAsync(Func<string, string> P, IPAddress ip, int configPort, CancellationToken ct)
    {
        // The camera's configured Baichuan port first, then the well-known ports.
        var ports = new List<(int Port, string Name)> { (configPort, "Baichuan (configured)") };
        ports.AddRange(TcpPorts.Where(p => p.Port != configPort));

        // Probe them concurrently — five filtered ports would otherwise serialize
        // into ~10 s of timeouts before the rest of the sweep even starts.
        async Task<(int Port, string Name, bool Open, string How)> ProbeOne(int port, string name)
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
                return (port, name, true, "OPEN");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (port, name, false, "cancelled"); }
            catch (OperationCanceledException) { return (port, name, false, "no response within 2s — filtered or nothing there"); }
            catch (SocketException ex) { return (port, name, false, ex.SocketErrorCode.ToString()); }
        }

        var results = await Task.WhenAll(ports.Select(p => ProbeOne(p.Port, p.Name))).ConfigureAwait(false);
        var open = new HashSet<int>();
        foreach (var (port, name, isOpen, how) in results.OrderByDescending(r => r.Open).ThenBy(r => r.Port))
        {
            if (isOpen) open.Add(port);
            Log.Info(P($"tcp/{port}: {how} ({name})"));
        }
        return open;
    }

    internal sealed record HttpResult(bool Reached, string? Model, string? Firmware);

    /// <summary>Non-sensitive DevInfo fields worth reporting (serial/UID excluded).</summary>
    private static readonly string[] DevInfoFields =
        { "model", "firmVer", "hardVer", "name", "type", "detailedType", "channelNum", "IOInputNum", "IOOutputNum", "wifi" };

    private static async Task<HttpResult> HttpDumpAsync(Func<string, string> P, CameraConfig cfg,
        IPAddress ip, HashSet<int> open, CancellationToken ct)
    {
        // Try an explicit http_address first; otherwise only the HTTP ports the map
        // found open (no point logging into a closed port).
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.HttpAddress)) candidates.Add(cfg.HttpAddress!);
        else
        {
            if (open.Contains(80)) candidates.Add($"http://{ip}");
            if (open.Contains(443)) candidates.Add($"https://{ip}");
        }
        if (candidates.Count == 0)
        {
            Log.Info(P("http: no HTTP port open and no \"http_address\" set — skipping the capability dump " +
                       "(an awake Reolink usually serves its API on 80/443; a sleeping battery camera does not)"));
            return new HttpResult(false, null, null);
        }

        foreach (var addr in candidates)
        {
            using var api = new ReolinkHttpApi(addr, cfg.Username, cfg.Password, cfg.ChannelId);
            try
            {
                var dev = await api.GetDevInfoAsync(ct).ConfigureAwait(false);
                string? model = (string?)dev?["model"], fw = (string?)dev?["firmVer"];
                if (dev != null)
                {
                    var fields = DevInfoFields
                        .Where(f => dev[f] != null)
                        .Select(f => $"{f}={dev[f]}");
                    Log.Info(P($"http({addr}): DevInfo — {string.Join(", ", fields)}"));
                }
                else
                {
                    Log.Info(P($"http({addr}): logged in but GetDevInfo carried no DevInfo"));
                }

                // The Ability table is the authoritative capability list.
                try
                {
                    var ability = await api.GetAbilityAsync(ct).ConfigureAwait(false);
                    var keys = ability.Select(kv => kv.Key).OrderBy(k => k).ToList();
                    Log.Info(P($"http({addr}): Ability — {keys.Count} capabilities: {string.Join(", ", keys)}"));
                    Log.Debug(P($"http({addr}): Ability(full) {Mask(ability.ToJsonString(), cfg)}"));
                }
                catch (Exception ex) { Log.Info(P($"http({addr}): GetAbility failed: {Log.Flatten(ex)}")); }

                // SD-card slots (many battery cams record locally).
                try
                {
                    var hdd = await api.GetHddInfoAsync(ct).ConfigureAwait(false);
                    Log.Info(P($"http({addr}): storage — {hdd.Count} slot(s)"));
                }
                catch (Exception ex) { Log.Debug(P($"http({addr}): GetHddInfo failed: {Log.Flatten(ex)}")); }

                Log.Info(P($"http({addr}): REACHABLE — the HTTP API answers, so capabilities above are authoritative"));
                return new HttpResult(true, model, fw);
            }
            catch (Exception ex)
            {
                Log.Info(P($"http({addr}): not usable — {Log.Flatten(ex)}"));
            }
        }
        return new HttpResult(false, null, null);
    }

    private static string Mask(string s, CameraConfig cfg) =>
        string.IsNullOrEmpty(cfg.Uid) ? s : UdpDiscovery.MaskUid(s, cfg.Uid!);

    // ------------------------------------------------------------------ ONVIF WS-Discovery

    /// <summary>Standard WS-Discovery: multicast a Probe to 239.255.255.250:3702 and
    /// collect ProbeMatch replies (device service URL + scopes). Returns the parsed
    /// matches (empty when nothing answers — as most battery cams won't).</summary>
    private static async Task<List<(string XAddrs, string Scopes)>> OnvifProbeAsync(Func<string, string> P, CancellationToken ct)
    {
        var matches = new List<(string, string)>();
        var msgId = $"urn:uuid:{Guid.NewGuid()}";
        var probe =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
            "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
            $"<e:Header><w:MessageID>{msgId}</w:MessageID>" +
            "<w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
            "<w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header>" +
            "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body></e:Envelope>";
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var bytes = Encoding.UTF8.GetBytes(probe);
            await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702), ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                foreach (var m in ParseOnvifMatches(Encoding.UTF8.GetString(r.Buffer)))
                {
                    matches.Add(m);
                    Log.Info(P($"onvif: reply from {r.RemoteEndPoint} — service {m.XAddrs}; scopes {m.Scopes}"));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(P($"onvif: probe error {Log.Flatten(ex)}"));
        }
        if (matches.Count == 0)
            Log.Info(P("onvif: no reply (WS-Discovery not answered — typical for battery-only models)"));
        return matches;
    }

    /// <summary>Pulls XAddrs (device service URL) and Scopes out of a WS-Discovery
    /// ProbeMatch envelope. Namespace-agnostic on local names so it tolerates the
    /// several discovery XML dialects cameras emit.</summary>
    internal static IEnumerable<(string XAddrs, string Scopes)> ParseOnvifMatches(string soap)
    {
        XDocument doc;
        try { doc = XDocument.Parse(soap); }
        catch { yield break; }
        foreach (var pm in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var xaddrs = pm.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value.Trim() ?? "";
            var scopes = pm.Descendants().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value.Trim() ?? "";
            // Keep the scopes that name the device (model/hardware/name), drop noise.
            var telling = scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.Contains("/name/") || s.Contains("/hardware/") || s.Contains("/location/"));
            yield return (xaddrs, string.Join(" ", telling));
        }
    }

    // ------------------------------------------------------------------ verdict

    /// <summary>The one-line recommendation, from what the sweep found. Pure so the
    /// decision table can be unit-tested.</summary>
    internal static string Recommend(HashSet<int> open, HttpResult http, int onvifCount, UdpDiscovery.UdpOutcome udp)
    {
        var model = http.Model != null ? $" (model {http.Model}, fw {http.Firmware})" : "";

        if (udp == UdpDiscovery.UdpOutcome.Accepted)
            return $"the camera speaks Baichuan-over-UDP{model} — this is the case that proves full UDP support is buildable; the UDP handshake above is the ground truth for it";
        if (open.Contains(9000))
            return $"TCP 9000 is OPEN{model} — the camera should connect the normal way; the earlier failure is likely credentials, channel, or a transient reboot rather than a UDP-only device";
        if (http.Reached)
            return $"no TCP 9000 and no UDP handshake, but the HTTP API answers{model} — capabilities are known; streaming still needs the Baichuan transport (TCP or, if the UDP handshake later succeeds, UDP)";
        if (udp == UdpDiscovery.UdpOutcome.Partial)
            return $"the UDP handshake got a partial answer{model} — promising; the replies above are what a full implementation needs";
        if (udp == UdpDiscovery.UdpOutcome.Refused)
            return $"the address responds but nothing listens on the Baichuan TCP/UDP ports{model} — check the address/that the camera is awake";
        if (onvifCount > 0)
            return $"only ONVIF answered{model} — the camera may be reachable via ONVIF/RTSP; try a Generic RTSP camera entry with its ONVIF stream URL";
        return "nothing answered on any transport — the camera is unreachable from this host (asleep, wrong address, a VLAN in between, or, in Docker, run with --network host); if it is a cloud-only battery model it may only answer Reolink's P2P relay, which this LAN sweep does not contact";
    }
}
