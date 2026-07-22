// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Neolink.Streaming;

/// <summary>
/// Router-fed wake hints for battery cameras: a UDP syslog listener that reads
/// OPNsense/pfSense "filterlog" lines and turns "this camera just opened a TLS
/// connection to the Reolink push service" into an instant, event-grade wake
/// signal for the matching camera's wake-capture scan.
///
/// Why this works (validated live, OPNsense + Argus Solar, 2026-07-22): on a PIR
/// event the camera itself contacts pushx.reolink.com over TCP 443 to deliver the
/// phone push — one new state, at the moment of the event. The router is the
/// camera's gateway, so it sees that unicast where a LAN host never could. The
/// signal even survives a BLOCK rule (the logged attempt is the signal), so
/// cameras firewalled off the internet still produce it.
///
/// Why the filter is TCP/443 ONLY, hardcoded: the same live capture showed the
/// camera bursting NEW UDP states to the Reolink p2p host ~30-45 s AFTER each
/// wake — the wake chip re-registering its remote-wake channel as the camera
/// dozes off. Treating those as hints would reconnect right after every park and
/// never let the camera sleep (the exact loop the ping-pattern scan just
/// escaped). During real sleep the wake chip reuses one long-lived state and
/// creates nothing new, so the event push is the only TCP/443 state a sleeping
/// camera's IP ever originates.
/// </summary>
public sealed class WakeHintListener
{
    private readonly int _port;
    private readonly string _bind;
    private readonly Action<IPAddress, string> _hint;

    /// <param name="hint">Called with (camera source IP, human-readable detail)
    /// for every event-grade line. The dispatcher matches the IP to a camera.</param>
    public WakeHintListener(int port, string bind, Action<IPAddress, string> hint)
    {
        _port = port;
        _bind = bind;
        _hint = hint;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(_bind), _port));
        Log.Info($"Wake hints: listening for router syslog on udp://{_bind}:{_port} " +
                 "(filterlog; only TCP/443 states count — the camera calling the push service)");
        // First datagram per syslog source is announced at Info: "is the router
        // actually sending to us" is the first thing every setup needs to verify,
        // and without this line a working pipe with no matching traffic is
        // indistinguishable from a dead one.
        var senders = new HashSet<IPAddress>();
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException e)
            {
                // A remote ICMP port-unreachable can surface here; never fatal.
                Log.Debug($"Wake hints: receive error ({e.SocketErrorCode}) — continuing");
                continue;
            }
            if (senders.Add(r.RemoteEndPoint.Address))
                Log.Info($"Wake hints: receiving syslog from {r.RemoteEndPoint.Address} — the pipe works");
            string line;
            try { line = Encoding.UTF8.GetString(r.Buffer); }
            catch { continue; }
            if (TryParseEventHint(line, out var src, out var detail))
                _hint(src, detail);
        }
    }

    /// <summary>
    /// Parses one syslog datagram; true only for an event-grade line: a filterlog
    /// entry for a NEW TCP state to port 443 (any action — pass or block, the
    /// attempt is the signal). Handles both RFC3164 ("filterlog: csv") and
    /// RFC5424 ("filterlog 1234 - [meta] csv") framings; anything unparseable is
    /// silently false — a syslog port receives all sorts.
    /// </summary>
    public static bool TryParseEventHint(string payload, out IPAddress sourceIp, out string detail)
    {
        sourceIp = IPAddress.None;
        detail = "";
        int tag = payload.IndexOf("filterlog", StringComparison.OrdinalIgnoreCase);
        if (tag < 0) return false;
        // The CSV starts at the rule number: a digit run DIRECTLY followed by a
        // comma, with a comma-rich remainder. That skips RFC5424's procid (digits
        // followed by a space) and structured-data noise; a misaligned start
        // still fails the strict field checks below, so this only needs to be
        // right, not clever.
        string rest = payload[(tag + "filterlog".Length)..];
        int csvStart = -1;
        for (int i = 0; i < rest.Length && csvStart < 0; i++)
        {
            if (!char.IsDigit(rest[i])) continue;
            int j = i;
            while (j < rest.Length && char.IsDigit(rest[j])) j++;
            if (j < rest.Length && rest[j] == ',' && rest.AsSpan(i).Count(',') >= 15)
                csvStart = i;
            else
                i = j;
        }
        if (csvStart < 0) return false;
        var f = rest[csvStart..].Split(',');
        // pf filterlog layout: 0 rulenr, .. 6 action, 7 dir, 8 ip-version, then
        // v4: 9 tos,10 ecn,11 ttl,12 id,13 offset,14 flags,15 proto-id,
        //     16 proto-name,17 length,18 src,19 dst,20 srcport,21 dstport
        // v6: 9 class,10 flow,11 hoplimit,12 proto-name,13 proto-id,
        //     14 length,15 src,16 dst,17 srcport,18 dstport
        int protoName, srcIdx;
        switch (f.Length > 8 ? f[8] : "")
        {
            case "4": protoName = 16; srcIdx = 18; break;
            case "6": protoName = 12; srcIdx = 15; break;
            default: return false;
        }
        if (f.Length <= srcIdx + 3) return false;
        if (!string.Equals(f[protoName], "tcp", StringComparison.OrdinalIgnoreCase)) return false;
        if (f[srcIdx + 3].Trim() != "443") return false;
        if (!IPAddress.TryParse(f[srcIdx], out var src)) return false;
        sourceIp = src;
        detail = $"router saw {f[srcIdx]} -> {f[srcIdx + 1]}:443 ({f[6]})";
        return true;
    }
}
