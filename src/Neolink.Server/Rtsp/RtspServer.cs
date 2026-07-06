using System.Net;
using System.Net.Sockets;
using System.Text;
using Neolink.Streaming;

namespace Neolink.Rtsp;

public sealed class RtspMount
{
    public required string Path { get; init; }
    public required IStreamHub Hub { get; init; }
    /// <summary>Users allowed to access this mount; null means no authentication required.</summary>
    public HashSet<string>? PermittedUsers { get; init; }
}

/// <summary>Pure .NET RTSP server (RFC 2326 subset: OPTIONS/DESCRIBE/SETUP/PLAY/PAUSE/GET_PARAMETER/TEARDOWN).</summary>
public sealed class RtspServer
{
    private readonly Dictionary<string, RtspMount> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _users;

    public RtspServer(IReadOnlyDictionary<string, string> users)
    {
        _users = users;
    }

    public void AddMount(RtspMount mount)
    {
        _mounts[Normalize(mount.Path)] = mount;
        Log.Debug($"RTSP mount ready: {mount.Path}");
    }

    public RtspMount? FindMount(string path) =>
        _mounts.TryGetValue(Normalize(path), out var m) ? m : null;

    private static string Normalize(string path)
    {
        path = Uri.UnescapeDataString(path);
        path = path.TrimEnd('/');
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }

    /// <summary>Checks Basic authorization for a mount. Returns true if access is allowed.</summary>
    public bool Authorize(RtspMount mount, string? authorizationHeader)
    {
        if (mount.PermittedUsers == null || _users.Count == 0)
            return true;

        var creds = NetUtil.DecodeBasicAuth(authorizationHeader);
        if (creds == null)
            return false;
        var (user, pass) = creds.Value;

        return _users.TryGetValue(user, out var expected)
            && NetUtil.FixedTimeEquals(expected, pass)
            && mount.PermittedUsers.Contains(user);
    }

    public async Task RunAsync(string bindAddr, int port, CancellationToken ct)
    {
        var ip = bindAddr == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(bindAddr);
        var listener = new TcpListener(ip, port);
        listener.Start();
        Log.Info($"RTSP server listening on rtsp://{bindAddr}:{port}/");

        var host = NetUtil.DisplayHost(bindAddr);
        var credentials = _users.Count > 0 ? "<user>:<pass>@" : "";
        foreach (var mount in _mounts.Values.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
            Log.Info($"  Stream: rtsp://{credentials}{host}:{port}{mount.Path}");

        await using var reg = ct.Register(() => listener.Stop());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }
                client.NoDelay = true;
                var conn = new RtspConnection(client, this);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await conn.RunAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"RTSP connection error: {Log.Flatten(ex)}");
                    }
                }, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}
