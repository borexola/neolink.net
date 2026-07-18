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
/// command array). Used only for what Baichuan cannot do: stream encode settings,
/// the white-LED/spotlight, picture/ISP adjustments, speaker volume, Wi-Fi signal,
/// PTZ presets, quick replies, AI auto-tracking and SD-card status.
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

    /// <summary>The channel this client addresses — for callers composing minimal
    /// write payloads ({"channel": n, "field": value}).</summary>
    public byte ChannelId => _channelId;

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

    /// <summary>Reads the white-LED / spotlight config (state 0|1, mode, bright 0-100).
    /// Baichuan can't do this on cameras that lack a FloodlightTask (e.g. Lumus).</summary>
    public async Task<JsonObject> GetWhiteLedAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetWhiteLed", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["WhiteLed"] as JsonObject
            ?? throw new ReolinkApiException("GetWhiteLed reply carries no WhiteLed settings");
    }

    /// <summary>Writes the white-LED config; pass a (modified) object from <see cref="GetWhiteLedAsync"/>.</summary>
    public Task SetWhiteLedAsync(JsonObject whiteLed, CancellationToken ct) =>
        ExecAsync("SetWhiteLed", new JsonObject { ["WhiteLed"] = whiteLed.DeepClone() }, ct);

    /// <summary>Reads the picture adjustments (bright/contrast/saturation/hue/sharpen, 0-255).</summary>
    public async Task<JsonObject> GetImageAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetImage", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["Image"] as JsonObject
            ?? throw new ReolinkApiException("GetImage reply carries no Image settings");
    }

    /// <summary>Writes picture adjustments; pass a (modified) object from <see cref="GetImageAsync"/>.</summary>
    public Task SetImageAsync(JsonObject image, CancellationToken ct) =>
        ExecAsync("SetImage", new JsonObject { ["Image"] = image.DeepClone() }, ct);

    /// <summary>A JPEG snapshot scaled down by the camera itself (cmd=Snap with
    /// width/height). Right-sizes the image at the source for size-limited
    /// consumers (the MQTT camera entity): a dual-lens or 4K camera's full
    /// snapshot easily exceeds broker packet limits. Firmwares that ignore the
    /// scaling parameters return the full-size image; callers still guard the
    /// size. Returns null when the camera answers with anything but a JPEG.</summary>
    public async Task<byte[]?> SnapAsync(int width, int height, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (_token == null || DateTime.UtcNow >= _tokenExpiry)
                    await LoginAsync(ct).ConfigureAwait(false);
                // rs = anti-cache nonce; the official clients send one on every Snap.
                var url = $"{_apiUrl}?cmd=Snap&channel={_channelId}&rs={Guid.NewGuid():N}" +
                          $"&width={width}&height={height}&token={Uri.EscapeDataString(_token!)}";
                using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
                var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 100 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                    return bytes; // JPEG magic — a real image
                // Errors come back as a small JSON body (formatting varies per
                // firmware, so don't pattern-match the rspCode) — most commonly a
                // stale token after a camera reboot. Relogin and retry once.
                if (attempt == 0 && bytes.Length < 4096 && bytes.Length > 0 && bytes[0] is (byte)'[' or (byte)'{')
                {
                    _token = null;
                    continue;
                }
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the ISP config (day/night mode, anti-flicker, flip/mirror, ...).</summary>
    public async Task<JsonObject> GetIspAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetIsp", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["Isp"] as JsonObject
            ?? throw new ReolinkApiException("GetIsp reply carries no Isp settings");
    }

    /// <summary>Writes the ISP config; pass a (modified) object from <see cref="GetIspAsync"/>.</summary>
    public Task SetIspAsync(JsonObject isp, CancellationToken ct) =>
        ExecAsync("SetIsp", new JsonObject { ["Isp"] = isp.DeepClone() }, ct);

    /// <summary>Reads the audio config (speaker volume 0-100, ...).</summary>
    public async Task<JsonObject> GetAudioCfgAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetAudioCfg", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["AudioCfg"] as JsonObject
            ?? throw new ReolinkApiException("GetAudioCfg reply carries no AudioCfg settings");
    }

    /// <summary>Writes the audio config; pass a (modified) object from <see cref="GetAudioCfgAsync"/>.</summary>
    public Task SetAudioCfgAsync(JsonObject audioCfg, CancellationToken ct) =>
        ExecAsync("SetAudioCfg", new JsonObject { ["AudioCfg"] = audioCfg.DeepClone() }, ct);

    /// <summary>The camera's Wi-Fi signal reading, or null when it reports none.
    /// Firmwares differ: some report bars (0-4), others dBm (negative).</summary>
    public async Task<int?> GetWifiSignalAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetWifiSignal", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return (int?)value?["signal"] ?? (int?)value?["WifiSignal"]?["signal"];
    }

    /// <summary>The camera's saved PTZ presets (id/name/enable), or an empty array.</summary>
    public async Task<JsonArray> GetPtzPresetsAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetPtzPreset", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["PtzPreset"] as JsonArray ?? new JsonArray();
    }

    /// <summary>Saves the camera's CURRENT position as preset <paramref name="id"/>.</summary>
    public Task SetPtzPresetAsync(int id, string name, CancellationToken ct) =>
        ExecAsync("SetPtzPreset", new JsonObject
        {
            ["PtzPreset"] = new JsonObject
            {
                ["channel"] = _channelId,
                ["enable"] = 1,
                ["id"] = id,
                ["name"] = name,
            },
        }, ct);

    /// <summary>Drives the camera to a saved preset position.</summary>
    public Task PtzToPresetAsync(int id, int speed, CancellationToken ct) =>
        ExecAsync("PtzCtrl", new JsonObject
        {
            ["channel"] = _channelId,
            ["op"] = "ToPos",
            ["speed"] = speed,
            ["id"] = id,
        }, ct);

    /// <summary>The doorbell's quick-reply audio files (id/fileName), or an empty array.</summary>
    public async Task<JsonArray> GetAudioFileListAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetAudioFileList", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["AudioFileList"] as JsonArray ?? new JsonArray();
    }

    /// <summary>Plays a quick-reply audio file through the camera's speaker.</summary>
    public Task QuickReplyPlayAsync(int id, CancellationToken ct) =>
        ExecAsync("QuickReplyPlay", new JsonObject { ["channel"] = _channelId, ["id"] = id }, ct);

    /// <summary>Reads the doorbell's auto-reply config: which quick-reply file plays
    /// by itself when a ring goes unanswered (fileId -1 = off) and after how long.</summary>
    public async Task<JsonObject> GetAutoReplyAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetAutoReply", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return value?["AutoReply"] as JsonObject
            ?? throw new ReolinkApiException("GetAutoReply reply carries no AutoReply settings");
    }

    /// <summary>Writes the auto-reply config; pass a (modified) object from <see cref="GetAutoReplyAsync"/>.</summary>
    public Task SetAutoReplyAsync(JsonObject autoReply, CancellationToken ct) =>
        ExecAsync("SetAutoReply", new JsonObject { ["AutoReply"] = autoReply.DeepClone() }, ct);

    /// <summary>Reads the camera's identity: model, firmware, hardware, serial,
    /// channel count. Null when the reply carries no DevInfo.</summary>
    public async Task<JsonObject?> GetDevInfoAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetDevInfo", new JsonObject(), ct).ConfigureAwait(false);
        return value?["DevInfo"] as JsonObject;
    }

    /// <summary>Reads the camera's ability table for this account — the authoritative
    /// "does this model actually have the feature" source (config reads often carry
    /// fields for features the hardware lacks).</summary>
    public async Task<JsonObject> GetAbilityAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetAbility",
            new JsonObject { ["User"] = new JsonObject { ["userName"] = _username } }, ct).ConfigureAwait(false);
        return value?["Ability"] as JsonObject
            ?? throw new ReolinkApiException("GetAbility reply carries no Ability table");
    }

    /// <summary>Reads the AI config (auto-tracking et al.). Firmwares differ on whether
    /// the value is wrapped in an "AiCfg" object; the returned object is the unwrapped
    /// config and <paramref name="wrapped"/> says which shape to write back.</summary>
    public async Task<(JsonObject Cfg, bool Wrapped)> GetAiCfgAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetAiCfg", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        if (value?["AiCfg"] is JsonObject wrappedCfg) return (wrappedCfg, true);
        return (value as JsonObject
            ?? throw new ReolinkApiException("GetAiCfg reply carries no AI config"), false);
    }

    /// <summary>Writes the AI config; pass the (modified) object and shape from <see cref="GetAiCfgAsync"/>.</summary>
    public Task SetAiCfgAsync(JsonObject cfg, bool wrapped, CancellationToken ct) =>
        ExecAsync("SetAiCfg", wrapped
            ? new JsonObject { ["AiCfg"] = cfg.DeepClone() }
            : (JsonObject)cfg.DeepClone(), ct);

    /// <summary>The camera's storage (SD card) slots, or an empty array when none.</summary>
    public async Task<JsonArray> GetHddInfoAsync(CancellationToken ct)
    {
        var value = await ExecAsync("GetHddInfo", new JsonObject(), ct).ConfigureAwait(false);
        return value?["HddInfo"] as JsonArray ?? new JsonArray();
    }

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

    /// <summary>Wire serialization for the camera. The default encoder escapes
    /// HTML-sensitive characters — dayNight "Black&amp;White" would go out with a
    /// & escape. Camera firmware JSON parsers don't decode those escapes and
    /// silently ignore the value, so everything must go over as literal UTF-8.</summary>
    internal static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private async Task<Reply> PostAsync(string url, string cmd, JsonNode param, CancellationToken ct)
    {
        var body = new JsonArray(new JsonObject
        {
            ["cmd"] = cmd,
            ["action"] = 0,
            ["param"] = param,
        });
        using var content = new StringContent(body.ToJsonString(WireJson), Encoding.UTF8, "application/json");

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
