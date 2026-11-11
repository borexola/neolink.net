// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;

namespace Neolink.WebClient;

/// <summary>The addresses of a camera's RTSP and snapshot links, as the camera panel's rtsp dialog builds them.</summary>
public static class RtspLink
{
    /// <summary>Addresses the link may name, IP addresses only: a host name can point at a reverse
    /// proxy that carries the web UI but not RTSP. The page's own address leads when it is one.</summary>
    public static List<string> Hosts(string? pageUrl, string? serverHost)
    {
        var page = Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) ? u.Host.Trim('[', ']') : "";
        bool pageIp = IPAddress.TryParse(page, out var ip) && !IPAddress.IsLoopback(ip);
        var hosts = new[] { pageIp ? page : null, serverHost }
            .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The server named no address of its own: the page's host is all there is.
        if (hosts.Count == 0 && page.Length > 0) hosts.Add(page);
        return hosts;
    }

    /// <summary>host[:port]/path: an IPv6 host bracketed, the RTSP default port 554 left out, the path percent-encoded.</summary>
    public static string Address(string host, int port, string path)
    {
        if (host.Contains(':')) host = $"[{host}]";
        var encoded = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        return $"{host}{(port is 554 or <= 0 ? "" : $":{port}")}{encoded}";
    }

    /// <summary>Base URLs a snapshot link may start with: the page's own, since the browser reached the web port
    /// through it (proxy and all), then the server's direct one. A page opened on the server itself offers the direct one.</summary>
    public static List<string> SnapshotBases(string? pageUrl, string? serverHost, int webPort)
    {
        string? page = null;
        bool local = false;
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
        {
            page = $"{u.Scheme}://{u.Authority}{u.AbsolutePath.TrimEnd('/')}";
            local = u.IsLoopback;
        }
        string? direct = null;
        if (!string.IsNullOrWhiteSpace(serverHost))
        {
            var host = serverHost.Trim();
            if (host.Contains(':')) host = $"[{host}]";
            direct = $"http://{host}{(webPort is 80 or <= 0 ? "" : $":{webPort}")}";
        }
        return (local && direct != null ? new[] { direct } : new[] { page, direct })
            .Where(b => b != null).Select(b => b!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A camera's snapshot URL on <paramref name="baseUrl"/>; login is "user:password" (percent-encoded) or empty.</summary>
    public static string Snapshot(string baseUrl, string login, string camera)
    {
        int at = baseUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        return $"{baseUrl[..at]}{(login.Length > 0 ? login + "@" : "")}{baseUrl[at..]}" +
               $"/api/cameras/{Uri.EscapeDataString(camera)}/snapshot.jpg";
    }
}
