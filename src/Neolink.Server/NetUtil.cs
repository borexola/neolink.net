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

    /// <summary>Constant-time string equality for credential checks (only length can leak).</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

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
