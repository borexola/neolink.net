// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Neolink;

internal static class NetUtil
{
    /// <summary>Decodes an HTTP/RTSP Basic authorization header into user/pass, or null.</summary>
    public static (string User, string Pass)? DecodeBasicAuth(string? authorizationHeader)
    {
        const string prefix = "Basic ";
        if (authorizationHeader == null ||
            !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorizationHeader[prefix.Length..].Trim()));
        }
        catch
        {
            return null;
        }
        int colon = decoded.IndexOf(':');
        if (colon < 0) return null;
        return (decoded[..colon], decoded[(colon + 1)..]);
    }

    /// <summary>The RTSP users' access rule (RTSP, the web API's Basic path, ONVIF PTZ): open when no
    /// users apply to the camera, else a permitted user with the right password.</summary>
    public static bool Permits(IReadOnlyDictionary<string, string> users, IReadOnlySet<string>? permitted,
        string? user, string? pass) =>
        Allows(users, permitted, user != null && pass != null && users.TryGetValue(user, out var expected)
                                 && FixedTimeEquals(expected, pass) ? user : null);

    /// <summary><see cref="Permits"/> for a user whose password is already verified (null = no login).</summary>
    public static bool Allows(IReadOnlyDictionary<string, string> users, IReadOnlySet<string>? permitted,
        string? verifiedUser) =>
        permitted == null || users.Count == 0 || (verifiedUser != null && permitted.Contains(verifiedUser));

    /// <summary>Constant-time string equality for credential checks (only length can leak).</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// The host (with its port when non-default) and login carried by an RTSP URL.
    /// A generic camera keeps everything in that one string, so it is also where its
    /// ONVIF endpoint and credentials are looked for. All three are null when the
    /// URL is missing or unparseable; percent-escapes in the login are decoded, so a
    /// password containing an @ or a / survives being written into a URL.
    /// </summary>
    /// <param name="url">An RTSP URL, or null.</param>
    /// <returns>Host: the bare hostname, which is where OTHER services on that
    /// camera are looked for — never carrying the stream's port, since ONVIF is not
    /// on it. Display: the same host plus its port when that port is not RTSP's own
    /// 554, for showing a person which camera this is (two cameras behind one
    /// address differ only by it). User/Pass: the login the URL carries, decoded.
    /// All null when the URL is missing or unparseable.</returns>
    public static (string? Host, string? Display, string? User, string? Pass) SplitRtspUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)
            || u.Host.Length == 0)
            return (null, null, null, null);
        string? user = null, pass = null;
        if (u.UserInfo.Length > 0)
        {
            int split = u.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(split < 0 ? u.UserInfo : u.UserInfo[..split]);
            if (split >= 0) pass = Uri.UnescapeDataString(u.UserInfo[(split + 1)..]);
            if (user.Length == 0) user = null;
        }
        // Uri.Host already brackets an IPv6 literal, which is what an authority
        // wants — both of these go back into URLs or into a host:port field.
        var display = u.IsDefaultPort || u.Port == 554 ? u.Host : $"{u.Host}:{u.Port}";
        return (u.Host, display, user, pass);
    }

    /// <summary>
    /// Turns a bind address into something a user can actually click: for wildcard
    /// binds (0.0.0.0 / ::) returns this machine's primary LAN IPv4, else localhost.
    /// </summary>
    public static string DisplayHost(string bindAddr)
    {
        if (bindAddr is not ("0.0.0.0" or "::" or "[::]" or "*"))
            return bindAddr;

        // Preferred: the address used for outbound traffic (no packet is sent for UDP connect)
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                return ep.Address.ToString();
        }
        catch { }

        // Fallback: first up, non-loopback interface with an IPv4 address
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
            }
        }
        catch { }

        return "localhost";
    }
}
