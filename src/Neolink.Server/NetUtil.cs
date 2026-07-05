using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Neolink;

internal static class NetUtil
{
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
