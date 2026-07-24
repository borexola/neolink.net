// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.Sockets;

namespace Neolink.Streaming;

/// <summary>
/// DNS-override wake hints for battery cameras: Neolink itself plays the Reolink
/// push service. The user adds a DNS override on their router (or Pi-hole /
/// AdGuard) so pushx.reolink.com resolves to the Neolink host; on a PIR event the
/// camera "delivers" its phone push here, and the mere TCP connection is an
/// instant, event-grade wake signal — no firewall logging or syslog required, so
/// it works on routers (OpenWRT and friends) the filterlog path can't cover.
///
/// The connection is accepted and closed immediately: nothing here speaks TLS or
/// the push protocol — the attempt IS the signal, exactly like the firewall-log
/// path treats a BLOCK rule. Consequence the docs spell out: the real push never
/// reaches Reolink, so app notifications on the phone stop for cameras behind
/// the override; Neolink → Home Assistant becomes the notification channel.
///
/// TCP ONLY, deliberately: the camera's post-wake p2p re-registration bursts are
/// UDP, so even if a user over-broad DNS override (a reolink.com wildcard —
/// docs say don't) lands that chatter on this host, a TCP-only sink stays
/// silent and the park/hint loop the syslog filter guards against can't start.
/// </summary>
public sealed class PushSinkListener
{
    private readonly IReadOnlyList<int> _ports;
    private readonly string _bind;
    private readonly Action<IPAddress, string> _hint;
    private readonly Dictionary<IPAddress, DateTime> _lastHint = new();
    private readonly HashSet<IPAddress> _seen = new();

    /// <param name="hint">Called with (camera source IP, human-readable detail)
    /// for every event-grade connection. The dispatcher matches the IP to a camera.</param>
    public PushSinkListener(IReadOnlyList<int> ports, string bind, Action<IPAddress, string> hint)
    {
        _ports = ports;
        _bind = bind;
        _hint = hint;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Ports bind independently: TCP 53 is commonly owned by a local DNS
        // server (Pi-hole on the same host is exactly the setup that also does
        // the DNS override), and losing 53 must not take 443 down with it.
        var bound = new List<(TcpListener Listener, int Port)>();
        foreach (var port in _ports)
        {
            var l = new TcpListener(IPAddress.Parse(_bind), port);
            try { l.Start(); bound.Add((l, port)); }
            catch (SocketException e)
            {
                Log.Error($"Wake hints: push decoy cannot listen on tcp:{port} ({e.SocketErrorCode}) — " +
                          (port < 1024 ? "ports below 1024 need elevated privileges outside a container; " : "") +
                          "is another service (a local DNS server?) already using it?");
            }
        }
        if (bound.Count == 0)
            throw new InvalidOperationException("none of the wake_hints.push_ports could be bound");
        Log.Info($"Wake hints: decoy push service on tcp://{_bind}:{string.Join(",", bound.Select(b => b.Port))} — " +
                 "add a DNS override so pushx.reolink.com (that hostname ONLY) resolves to this host. " +
                 "Reolink app notifications will stop for cameras behind the override.");
        try
        {
            await Task.WhenAll(bound.Select(b => AcceptLoopAsync(b.Listener, b.Port, ct))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (l, _) in bound) l.Stop();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException e)
            {
                Log.Debug($"Wake hints: push decoy accept error on tcp:{port} ({e.SocketErrorCode}) — continuing");
                continue;
            }
            using (client)
            {
                if ((client.Client.RemoteEndPoint as IPEndPoint)?.Address is not { } ip) continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                bool firstContact, pass;
                lock (_lastHint)
                {
                    firstContact = _seen.Add(ip);
                    pass = ShouldHint(_lastHint, ip, DateTime.UtcNow);
                }
                // First contact per source is announced at Info: "does the DNS
                // override actually send the camera here" is the first thing
                // every setup needs to verify.
                if (firstContact)
                    Log.Info($"Wake hints: push decoy contacted by {ip} (tcp/{port}) — the DNS override works");
                if (pass)
                    _hint(ip, $"{ip} called the decoy push service on tcp/{port}");
            }
            // Dispose closes the socket without a TLS handshake or any reply —
            // the connect attempt was the whole signal.
        }
    }

    /// <summary>
    /// Per-source throttle: the camera's push delivery dies at the TLS handshake
    /// and firmware retries within seconds, so one PIR event arrives as a small
    /// burst of connections. One hint per source per 10 s keeps that burst to a
    /// single signal (the hint-opened session records ~30 s anyway; a genuinely
    /// later push refreshes it through the next hint).
    /// </summary>
    public static bool ShouldHint(Dictionary<IPAddress, DateTime> lastHint, IPAddress source, DateTime now)
    {
        if (lastHint.TryGetValue(source, out var prev) && now - prev < TimeSpan.FromSeconds(10))
            return false;
        lastHint[source] = now;
        return true;
    }
}
