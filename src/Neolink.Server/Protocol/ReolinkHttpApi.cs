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
/// PTZ presets, quick replies, AI auto-tracking, detection sensitivity, OSD,
/// firmware-update checks and the SD card (status, search, download).
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
        _http = new HttpClient(NewHandler()) { Timeout = RequestTimeout };
    }

    /// <summary>Connection hygiene for camera HTTP. A Wi-Fi camera drops idle
    /// keep-alive connections SILENTLY (power save, AP roam, embedded-server
    /// amnesia) — no RST, so a pooled connection can be a corpse: requests
    /// written into one wait out the whole command cap for a reply that never
    /// comes, reading as "did not reply" streaks against a perfectly healthy
    /// camera (field report: 10s stalls while curl from the same network
    /// namespace answered in 69ms on a fresh connection). Idle connections are
    /// therefore retired after 30s — a sweep after a quiet period starts on
    /// fresh TCP, which costs ~1ms on a LAN — and connects get their own 5s
    /// bound so a dropped SYN fails fast instead of masquerading as a slow
    /// reply for the full cap.</summary>
    private HttpMessageHandler NewHandler()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        if (_apiUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            // Cameras serve their HTTPS API with a self-signed certificate.
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }
        return handler;
    }

    public void Dispose() { _http.Dispose(); _httpLong?.Dispose(); }

    /// <summary>The channel this client addresses — for callers composing minimal
    /// write payloads ({"channel": n, "field": value}).</summary>
    public byte ChannelId => _channelId;

    /// <summary>Whether the next call can ride the cached session token. False
    /// means it pays for a LOGIN round-trip first — which on a Wi-Fi camera busy
    /// pushing streams (dual-lens Duos especially) can take several seconds by
    /// itself, so callers should budget accordingly.</summary>
    public bool HasLiveToken => _token != null && DateTime.UtcNow < _tokenExpiry;

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
    public async Task<byte[]?> SnapAsync(int width, int height, CancellationToken ct) =>
        await SnapAsync("sub", width, height, ct).ConfigureAwait(false);

    /// <summary>As above with an explicit stream tier: "main", "sub" or "ext" (the
    /// extern stream — the smallest tier, present on dual-lens and newer models).
    /// Firmwares without the requested tier answer with an error JSON → null.</summary>
    public async Task<byte[]?> SnapAsync(string snapType, int width, int height, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (_token == null || DateTime.UtcNow >= _tokenExpiry)
                    await LoginAsync(ct).ConfigureAwait(false);
                // rs = anti-cache nonce; the official clients send one on every Snap.
                // snapType=sub asks for the SUB-stream-resolution image — honored
                // far more widely than width/height (dual-lens Duos ignore those
                // but do respect snapType); firmwares that know neither ignore
                // unknown query parameters and send the full-size image.
                var url = $"{_apiUrl}?cmd=Snap&channel={_channelId}&rs={Guid.NewGuid():N}" +
                          $"&snapType={snapType}&width={width}&height={height}&token={Uri.EscapeDataString(_token!)}";
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

    /// <summary>Reads the motion-detection config. Newer firmwares answer GetMdAlarm
    /// (sensitivity in newSens.sensDef, 0-50, higher = more sensitive); older ones
    /// only know GetAlarm type "md" (per-slot sens list where the wire value is
    /// INVERTED: 51 - sensitivity). <paramref name="isMdAlarm"/> tells the caller
    /// which dialect came back so the write goes out the same way.</summary>
    public async Task<(JsonObject Cfg, bool IsMdAlarm)> GetMdConfigAsync(CancellationToken ct)
    {
        try
        {
            var value = await ExecAsync("GetMdAlarm", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
            if (value?["MdAlarm"] is JsonObject md) return (md, true);
        }
        catch (ReolinkApiException)
        {
            // Old firmware: "not support"/"not exist" — fall through to GetAlarm.
        }
        var alarm = await ExecAsync("GetAlarm",
            new JsonObject { ["Alarm"] = new JsonObject { ["channel"] = _channelId, ["type"] = "md" } },
            ct).ConfigureAwait(false);
        return (alarm?["Alarm"] as JsonObject
            ?? throw new ReolinkApiException("GetAlarm reply carries no Alarm settings"), false);
    }

    /// <summary>Writes the motion-detection config; pass the (modified) object and
    /// dialect flag from <see cref="GetMdConfigAsync"/>.</summary>
    public Task SetMdConfigAsync(JsonObject cfg, bool isMdAlarm, CancellationToken ct) =>
        isMdAlarm
            ? ExecAsync("SetMdAlarm", new JsonObject { ["MdAlarm"] = cfg.DeepClone() }, ct)
            : ExecAsync("SetAlarm", new JsonObject { ["Alarm"] = cfg.DeepClone() }, ct);

    /// <summary>Reads one AI detection type's alarm config (sensitivity 0-100,
    /// stay_time, target sizes). Types the firmware knows: "people", "vehicle",
    /// "dog_cat", "face", "package" — unsupported ones are rejected (throws).</summary>
    public async Task<JsonObject> GetAiAlarmAsync(string aiType, CancellationToken ct)
    {
        var value = await ExecAsync("GetAiAlarm",
            new JsonObject { ["channel"] = _channelId, ["ai_type"] = aiType }, ct).ConfigureAwait(false);
        return value?["AiAlarm"] as JsonObject
            ?? throw new ReolinkApiException($"GetAiAlarm({aiType}) reply carries no AiAlarm settings");
    }

    /// <summary>Writes an AI alarm config; pass a (modified) object from <see cref="GetAiAlarmAsync"/>.</summary>
    public Task SetAiAlarmAsync(JsonObject aiAlarm, CancellationToken ct) =>
        ExecAsync("SetAiAlarm", new JsonObject { ["AiAlarm"] = aiAlarm.DeepClone() }, ct);

    /// <summary>Reads the ISP config together with its RANGE table (action 1): the
    /// range says which optional fields (hdr, binningMode, ...) this firmware
    /// actually has and what values they accept.</summary>
    public async Task<(JsonObject Isp, JsonObject? Range)> GetIspWithRangeAsync(CancellationToken ct)
    {
        var (value, range) = await ExecRangeAsync("GetIsp", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return (value?["Isp"] as JsonObject
            ?? throw new ReolinkApiException("GetIsp reply carries no Isp settings"),
            range?["Isp"] as JsonObject);
    }

    /// <summary>Reads the on-screen-display config (camera-name / timestamp overlay
    /// visibility + position, watermark) with its range (position option lists).</summary>
    public async Task<(JsonObject Osd, JsonObject? Range)> GetOsdAsync(CancellationToken ct)
    {
        var (value, range) = await ExecRangeAsync("GetOsd", new JsonObject { ["channel"] = _channelId }, ct).ConfigureAwait(false);
        return (value?["Osd"] as JsonObject
            ?? throw new ReolinkApiException("GetOsd reply carries no Osd settings"),
            range?["Osd"] as JsonObject);
    }

    /// <summary>Writes the OSD config; pass a (modified) object from <see cref="GetOsdAsync"/>.</summary>
    public Task SetOsdAsync(JsonObject osd, CancellationToken ct) =>
        ExecAsync("SetOsd", new JsonObject { ["Osd"] = osd.DeepClone() }, ct);

    /// <summary>Asks the camera whether newer firmware is available online (read-only;
    /// never installs anything). The reply is firmware-shaped: 0/"" = up to date,
    /// 1 = update available, or an info object naming the new version.</summary>
    public async Task<JsonNode?> CheckFirmwareAsync(CancellationToken ct)
    {
        var value = await ExecAsync("CheckFirmware", new JsonObject(), ct).ConfigureAwait(false);
        return value?["newFirmware"];
    }

    // ------------------------------------------------------------------ SD-card recordings

    /// <summary>Searches the camera's SD card. With <paramref name="onlyStatus"/> the
    /// reply is a per-month calendar (Status[].table, one digit per day); without it,
    /// the File[] list for the window (max ~2000 entries per call — keep windows to
    /// a day). Times are camera-local. The FILE search walks the card's file table —
    /// on a busy day that alone can take longer than the 10s command cap, so it runs
    /// on the uncapped client and only the caller's token bounds it.</summary>
    public async Task<JsonObject> SearchAsync(string streamType, DateTime start, DateTime end,
        bool onlyStatus, CancellationToken ct)
    {
        var value = (await ExecCoreAsync("Search", new JsonObject
        {
            ["Search"] = new JsonObject
            {
                ["channel"] = _channelId,
                ["onlyStatus"] = onlyStatus ? 1 : 0,
                ["streamType"] = streamType,
                ["StartTime"] = TimeObject(start),
                ["EndTime"] = TimeObject(end),
            },
        }, action: 0, ct, longRunning: !onlyStatus).ConfigureAwait(false)).Value;
        return value?["SearchResult"] as JsonObject
            ?? throw new ReolinkApiException("Search reply carries no SearchResult");
    }

    private static JsonObject TimeObject(DateTime t) => new()
    {
        ["year"] = t.Year, ["mon"] = t.Month, ["day"] = t.Day,
        ["hour"] = t.Hour, ["min"] = t.Minute, ["sec"] = t.Second,
    };

    /// <summary>An open download of one SD-card recording; dispose to drop the connection.</summary>
    public sealed class SdDownload : IDisposable
    {
        private readonly HttpResponseMessage _response;
        internal SdDownload(HttpResponseMessage response, Stream stream)
        { _response = response; Stream = stream; }
        public Stream Stream { get; }
        public long? Length => _response.Content.Headers.ContentLength;
        public void Dispose() { Stream.Dispose(); _response.Dispose(); }
    }

    /// <summary>Streams one recording file off the SD card (cmd=Download). The name
    /// comes from <see cref="SearchAsync"/> File entries. Runs on a connection with
    /// no overall timeout — a full clip takes minutes over Wi-Fi — so the caller's
    /// token governs its lifetime; only the token handshake holds the command gate.</summary>
    public async Task<SdDownload> DownloadAsync(string fileName, CancellationToken ct)
    {
        // Ensure a live token under the gate, then stream outside it: holding the
        // gate for a minutes-long transfer would freeze every other API call.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token == null || DateTime.UtcNow >= _tokenExpiry)
                await LoginAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var url = $"{_apiUrl}?cmd=Download&source={Uri.EscapeDataString(fileName)}" +
                  $"&output={Uri.EscapeDataString(fileName)}&token={Uri.EscapeDataString(_token!)}";
        var res = await LongHttp().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            if (!res.IsSuccessStatusCode)
                throw new ReolinkApiException($"camera HTTP API returned HTTP {(int)res.StatusCode} for Download");
            // Errors (bad name, stale token) come back as a small JSON body.
            var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ReolinkApiException($"camera refused the download: {Truncate(text, 200)}");
            }
            var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new SdDownload(res, stream);
        }
        catch
        {
            res.Dispose();
            throw;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ------------------------------------------------------------------ plumbing

    private async Task<JsonNode?> ExecAsync(string cmd, JsonObject param, CancellationToken ct) =>
        (await ExecCoreAsync(cmd, param, action: 0, ct).ConfigureAwait(false)).Value;

    /// <summary>As <see cref="ExecAsync"/> with action 1: the camera includes its
    /// RANGE table (valid values / min-max per field) alongside the current value.</summary>
    private Task<(JsonNode? Value, JsonNode? Range)> ExecRangeAsync(string cmd, JsonObject param, CancellationToken ct) =>
        ExecCoreAsync(cmd, param, action: 1, ct);

    private async Task<(JsonNode? Value, JsonNode? Range)> ExecCoreAsync(string cmd, JsonObject param, int action,
        CancellationToken ct, bool longRunning = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (_token == null || DateTime.UtcNow >= _tokenExpiry)
                    await LoginAsync(ct).ConfigureAwait(false);

                var reply = await PostAsync($"{_apiUrl}?cmd={cmd}&token={Uri.EscapeDataString(_token!)}",
                    cmd, param.DeepClone(), ct, action, longRunning).ConfigureAwait(false);
                if (reply.Code == 0)
                    return (reply.Value, reply.Range);

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

    private HttpClient? _httpLong;

    /// <summary>Lazy second client sharing nothing with the 10-second command client:
    /// SD-card downloads run for minutes and are paced by the caller's token.</summary>
    private HttpClient LongHttp()
    {
        if (_httpLong != null) return _httpLong;
        return _httpLong = new HttpClient(NewHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        // The documented login carries User.Version "0", and most firmwares accept
        // it — but some (observed on the dual-lens Duo line) reject that shape as
        // "password wrong" while accepting the bare User object that reolink_aio
        // (Home Assistant's Reolink integration) sends. Try documented first so
        // known-good cameras are untouched, bare second on rejection only.
        var reply = await PostLoginAsync(withVersion: true, ct).ConfigureAwait(false);
        if (reply.Code != 0)
            reply = await PostLoginAsync(withVersion: false, ct).ConfigureAwait(false);
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

    private Task<Reply> PostLoginAsync(bool withVersion, CancellationToken ct)
    {
        var user = new JsonObject();
        if (withVersion) user["Version"] = "0";
        user["userName"] = _username;
        user["password"] = _password;
        return PostAsync($"{_apiUrl}?cmd=Login", "Login", new JsonObject { ["User"] = user }, ct);
    }

    private sealed record Reply(int Code, int RspCode, string? Detail, JsonNode? Value, JsonNode? Range);

    /// <summary>Wire serialization for the camera. The default encoder escapes
    /// HTML-sensitive characters — dayNight "Black&amp;White" would go out with a
    /// & escape. Camera firmware JSON parsers don't decode those escapes and
    /// silently ignore the value, so everything must go over as literal UTF-8.</summary>
    internal static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private async Task<Reply> PostAsync(string url, string cmd, JsonNode param, CancellationToken ct,
        int action = 0, bool longRunning = false)
    {
        var body = new JsonArray(new JsonObject
        {
            ["cmd"] = cmd,
            ["action"] = action,
            ["param"] = param,
        });
        using var content = new StringContent(body.ToJsonString(WireJson), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            // Long-running commands (SD file searches) escape the 10s command
            // cap; the caller's cancellation token is their only bound.
            res = await (longRunning ? LongHttp() : _http).PostAsync(url, content, ct).ConfigureAwait(false);
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
                Value: obj["value"],
                Range: obj["range"]);
        }
    }
}
