// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neolink.Protocol;

/// <summary>The camera's HTTP API rejected a command or sent a reply we cannot use.</summary>
public sealed class ReolinkApiException : Exception
{
    public ReolinkApiException(string message) : base(message) { }
}

/// <summary>
/// Minimal client for the documented Reolink HTTP API (POST /api.cgi with a JSON
/// command array). Used only for what Baichuan cannot do — today that is writing
/// stream encode settings (GetEnc/SetEnc); the BC set-encode message format is
/// unverified, and the reference Rust neolink implements no setter either.
///
/// Sessions are token-based: cmd=Login yields a token with a lease time that every
/// later call carries as a query parameter. The token is cached and renewed shortly
/// before the lease runs out, or when the camera reports it invalid (rspCode -6,
/// e.g. after a reboot). Requests are serialized through one gate; Reolink
/// firmwares are happiest answering API calls one at a time.
/// </summary>
public sealed class ReolinkHttpApi : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Renew the token this long before its lease actually runs out.</summary>
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly byte _channelId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTime _tokenExpiry;

    /// <param name="address">"host", "host:port" or a full "http(s)://host[:port]" URL.</param>
    public ReolinkHttpApi(string address, string username, string? password, byte channelId)
    {
        var baseUrl = (address.Contains("://") ? address : $"http://{address}").TrimEnd('/');
        _apiUrl = $"{baseUrl}/api.cgi";
        _username = username;
        _password = password ?? "";
        _channelId = channelId;

        var handler = new HttpClientHandler();
        if (baseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            // Cameras serve their HTTPS API with a self-signed certificate.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        _http = new HttpClient(handler) { Timeout = RequestTimeout };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Reads the current encode settings (per-stream resolution/framerate/bitrate).</summary>
    public async Task<JsonObject> GetEncAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetEnc", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["Enc"] as JsonObject
            ?? throw new ReolinkApiException("GetEnc reply carries no Enc settings");
    }

    /// <summary>Writes encode settings; pass a (modified) object from <see cref="GetEncAsync"/>.</summary>
    public Task SetEncAsync(JsonObject enc, CancellationToken ct) =>
        ExecAsync("SetEnc", new JsonObject { ["Enc"] = enc.DeepClone() }, ct);

    // ------------------------------------------------------------------ plumbing

    private async Task<JsonNode?> ExecAsync(string cmd, JsonObject param, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (_token == null || DateTime.UtcNow >= _tokenExpiry)
                    await LoginAsync(ct).ConfigureAwait(false);

                var reply = await PostAsync($"{_apiUrl}?cmd={cmd}&token={Uri.EscapeDataString(_token!)}",
                    cmd, param.DeepClone(), ct).ConfigureAwait(false);
                if (reply.Code == 0)
                    return reply.Value;

                // -6: token invalid/expired despite the lease (camera rebooted, session
                // table flushed) — log in again and retry the command once.
                if (reply.RspCode == -6 && attempt == 0)
                {
                    _token = null;
                    continue;
                }
                throw new ReolinkApiException(
                    $"camera HTTP API rejected {cmd}: {reply.Detail ?? "unknown error"} (rspCode {reply.RspCode})");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        var param = new JsonObject
        {
            ["User"] = new JsonObject
            {
                ["Version"] = "0",
                ["userName"] = _username,
                ["password"] = _password,
            },
        };
        var reply = await PostAsync($"{_apiUrl}?cmd=Login", "Login", param, ct).ConfigureAwait(false);
        if (reply.Code != 0)
            throw new ReolinkApiException(
                $"camera HTTP API login failed: {reply.Detail ?? "unknown error"} (rspCode {reply.RspCode})");

        var token = reply.Value?["Token"];
        _token = (string?)token?["name"]
            ?? throw new ReolinkApiException("camera HTTP API login reply carries no token");
        int lease = (int?)token?["leaseTime"] ?? 3600;
        var lifetime = TimeSpan.FromSeconds(lease) - LeaseMargin;
        if (lifetime <= TimeSpan.Zero) lifetime = TimeSpan.FromSeconds(lease / 2.0);
        _tokenExpiry = DateTime.UtcNow + lifetime;
    }

    private sealed record Reply(int Code, int RspCode, string? Detail, JsonNode? Value);

    private async Task<Reply> PostAsync(string url, string cmd, JsonNode param, CancellationToken ct)
    {
        var body = new JsonArray(new JsonObject
        {
            ["cmd"] = cmd,
            ["action"] = 0,
            ["param"] = param,
        });
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"camera HTTP API unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("camera HTTP API did not reply");
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new ReolinkApiException($"camera HTTP API returned HTTP {(int)res.StatusCode} for {cmd}");
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new ReolinkApiException($"camera HTTP API sent malformed JSON for {cmd}");
            }

            // Replies come as a one-element array; tolerate a bare object too.
            var first = root is JsonArray arr ? arr.FirstOrDefault() : root;
            if (first is not JsonObject obj)
                throw new ReolinkApiException($"camera HTTP API sent an unexpected reply for {cmd}");

            var error = obj["error"];
            return new Reply(
                Code: (int?)obj["code"] ?? -1,
                RspCode: (int?)error?["rspCode"] ?? 0,
                Detail: (string?)error?["detail"],
                Value: obj["value"]);
        }
    }
}
