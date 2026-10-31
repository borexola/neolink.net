// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.Sockets;
using Neolink.WebClient;

namespace Neolink;

/// <summary>Auto discover: a TCP sweep of the Baichuan port over the ranges asked for (or
/// each local /24) and the /24 of every configured camera, plus ONVIF WS-Discovery for
/// model and name — multicast, and unicast to every swept host so routed subnets answer
/// too. Battery models answer neither; they are found by UID only.</summary>
public static class LanScan
{
    /// <summary>One device found: Baichuan port open and/or ONVIF answered.</summary>
    public sealed record Found(IPAddress Address, string? Model, string? Name, bool Baichuan, bool Onvif);

    private const int BaichuanPort = 9000;
    private const int MaxSweepHosts = 2048;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan OnvifWindow = TimeSpan.FromSeconds(3);

    /// <param name="ranges">Ranges to sweep: the ones typed in and each configured camera's /24.</param>
    /// <param name="localToo">Sweep the local /24s as well (when nothing was typed in).</param>
    public static async Task<List<Found>> RunAsync(CancellationToken ct, Action<string>? progress = null,
        IEnumerable<ScanRange>? ranges = null, bool localToo = true)
    {
        var hosts = SweepTargets(UdpDiscovery.LocalNetworks(), ranges, localToo).ToList();
        progress?.Invoke("asking for ONVIF cameras");
        var onvifTask = CameraProbe.WsDiscoverAsync(OnvifWindow, ct, unicast: hosts);

        progress?.Invoke($"checking port {BaichuanPort} on {hosts.Count} addresses");
        var open = await OpenBaichuanAsync(hosts, ct).ConfigureAwait(false);

        var byIp = new Dictionary<IPAddress, Found>();
        foreach (var ip in open) byIp[ip] = new Found(ip, null, null, Baichuan: true, Onvif: false);
        foreach (var m in await onvifTask.ConfigureAwait(false))
        {
            var ip = AddressOf(m) ?? m.From;
            var (model, name) = ParseScopes(m.Scopes);
            byIp[ip] = byIp.TryGetValue(ip, out var had)
                ? had with { Model = model ?? had.Model, Name = name ?? had.Name, Onvif = true }
                : new Found(ip, model, name, Baichuan: false, Onvif: true);
        }
        return byIp.Values.OrderBy(f => f.Address, Comparer<IPAddress>.Create(CompareIps)).ToList();
    }

    // Container and VM bridges: a camera never sits behind one, and they would eat the cap.
    private static readonly string[] VirtualPrefixes = { "docker", "br-", "virbr", "veth", "vmnet", "vboxnet", "lxc", "cni" };

    internal static bool IsVirtual(string iface) =>
        VirtualPrefixes.Any(p => iface.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The ranges asked for first (in full), then (when <paramref name="localToo"/>)
    /// every host of each local /24 (a wider subnet is cut to the host's own /24), real
    /// interfaces only, the ones with a gateway first, capped. The host's own addresses
    /// are never swept.</summary>
    internal static IEnumerable<IPAddress> SweepTargets(IEnumerable<UdpDiscovery.LocalNet> networks,
        IEnumerable<ScanRange>? ranges = null, bool localToo = true)
    {
        var nets = networks.ToList();
        var seen = new HashSet<IPAddress>(nets.Select(n => n.Address));
        var own = localToo
            ? nets.Where(n => !IsVirtual(n.Interface)).OrderByDescending(n => n.HasGateway).Select(n => n.Address).ToList()
            : new List<IPAddress>();
        var all = (ranges ?? Enumerable.Empty<ScanRange>()).Select(r => r.Hosts())
            .Concat(own.Select(UdpDiscovery.SubnetHosts));
        int count = 0;
        foreach (var range in all)
        {
            foreach (var host in range)
            {
                if (!seen.Add(host)) continue;
                if (++count > MaxSweepHosts) yield break;
                yield return host;
            }
        }
    }

    /// <summary>Model ("hardware") and device name out of the telling WS-Discovery scopes.</summary>
    internal static (string? Model, string? Name) ParseScopes(string scopes)
    {
        string? model = null, name = null;
        foreach (var s in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at;
            if ((at = s.IndexOf("/hardware/", StringComparison.Ordinal)) >= 0) model ??= Uri.UnescapeDataString(s[(at + 10)..]);
            else if ((at = s.IndexOf("/name/", StringComparison.Ordinal)) >= 0) name ??= Uri.UnescapeDataString(s[(at + 6)..]);
        }
        return (Blank(model) ? null : model, Blank(name) ? null : name);
    }

    /// <summary>The address in the device-service URL — the camera's own view of
    /// itself, which beats the datagram source behind a relay or an alias.</summary>
    private static IPAddress? AddressOf(CameraProbe.WsMatch m)
    {
        var first = m.XAddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first != null && Uri.TryCreate(first, UriKind.Absolute, out var u)
               && IPAddress.TryParse(u.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? ip : null;
    }

    private static async Task<List<IPAddress>> OpenBaichuanAsync(List<IPAddress> hosts, CancellationToken ct)
    {
        var open = new System.Collections.Concurrent.ConcurrentBag<IPAddress>();
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 128, CancellationToken = ct },
            async (ip, token) =>
            {
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(ConnectTimeout);
                try
                {
                    await tcp.ConnectAsync(ip, BaichuanPort, cts.Token).ConfigureAwait(false);
                    open.Add(ip);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* closed, filtered or nobody there */ }
            }).ConfigureAwait(false);
        return open.ToList();
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static int CompareIps(IPAddress a, IPAddress b)
    {
        var ab = a.GetAddressBytes(); var bb = b.GetAddressBytes();
        for (int i = 0; i < Math.Min(ab.Length, bb.Length); i++)
            if (ab[i] != bb[i]) return ab[i].CompareTo(bb[i]);
        return ab.Length.CompareTo(bb.Length);
    }
}
