// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.Sockets;

namespace Neolink.WebClient;

/// <summary>An IPv4 range the auto-discover sweep covers, as typed by the user. Shared by
/// the dialog (validation as you type) and the server (the sweep itself).</summary>
public readonly record struct ScanRange(uint First, uint Last)
{
    /// <summary>The widest range accepted: a /21's usable hosts.</summary>
    public const int MaxHosts = 2046;

    public int Count => (int)(Last - First + 1);

    /// <summary>The usable hosts of a subnet: network and broadcast left out below /31.</summary>
    public static ScanRange Subnet(IPAddress ip, int prefix)
    {
        uint a = ToUInt(ip);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint first = a & mask, last = first | ~mask;
        if (prefix <= 30) { first++; last--; }
        return new ScanRange(first, last);
    }

    public IEnumerable<IPAddress> Hosts()
    {
        for (uint h = First; ; h++)
        {
            yield return new IPAddress(new[] { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h });
            if (h >= Last) yield break;
        }
    }

    /// <summary>"10.1.1.0/24", a bare address (its /24), "10.1.1.10-10.1.1.60" or "10.1.1.10-60".
    /// Null, with the reason, when it is none of these or wider than <see cref="MaxHosts"/>.</summary>
    public static ScanRange? Parse(string text, out string? error)
    {
        error = null;
        var t = text.Trim();
        ScanRange r;
        if (t.Contains('/'))
        {
            var parts = t.Split('/', 2);
            if (!TryIp(parts[0], out var ip)) { error = $"'{t}' is not an IPv4 subnet (use 10.1.1.0/24)"; return null; }
            if (!int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            { error = $"'{t}' has no valid prefix length (use 10.1.1.0/24)"; return null; }
            r = Subnet(ip, prefix);
        }
        else if (t.Contains('-'))
        {
            var parts = t.Split('-', 2);
            if (!TryIp(parts[0].Trim(), out var from)) { error = $"'{t}' is not an IPv4 range (use 10.1.1.10-10.1.1.60)"; return null; }
            var toText = parts[1].Trim();
            // "10.1.1.10-60": the end is the last octet only.
            if (!toText.Contains('.') && byte.TryParse(toText, out var lastOctet))
            {
                var b = from.GetAddressBytes(); b[3] = lastOctet; toText = new IPAddress(b).ToString();
            }
            if (!TryIp(toText, out var to)) { error = $"'{t}' is not an IPv4 range (use 10.1.1.10-10.1.1.60)"; return null; }
            r = new ScanRange(ToUInt(from), ToUInt(to));
            if (r.First > r.Last) { error = $"'{t}' ends before it starts"; return null; }
        }
        else
        {
            if (!TryIp(t, out var ip)) { error = $"'{t}' is not an IPv4 subnet (use 10.1.1.0/24)"; return null; }
            r = Subnet(ip, 24);
        }
        if (r.Count > MaxHosts) { error = $"'{t}' is too wide to sweep — {MaxHosts} addresses (a /21) is the most"; return null; }
        return r;
    }

    /// <summary>Comma-, semicolon- or space-separated ranges; null with the first problem,
    /// including an empty list.</summary>
    public static List<ScanRange>? ParseList(string text, out string? error)
    {
        var list = new List<ScanRange>();
        foreach (var part in text.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Parse(part, out error) is { } r) list.Add(r);
            else return null;
        }
        error = list.Count == 0 ? "Enter a subnet to scan (10.1.1.0/24)" : null;
        return list.Count == 0 ? null : list;
    }

    private static bool TryIp(string s, out IPAddress ip) =>
        IPAddress.TryParse(s, out ip!) && ip.AddressFamily == AddressFamily.InterNetwork;

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}
