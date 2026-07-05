using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neolink.Media;
using Neolink.Protocol;
using Neolink.Streaming;

namespace Neolink.Web;

/// <summary>One camera stream as exposed over the web API.</summary>
public sealed record WebStreamInfo(string Kind, string Path, IStreamHub Hub);
public sealed record WebCameraInfo(string Name, List<WebStreamInfo> Streams, ICameraControl Control,
    HashSet<string>? PermittedUsers);

/// <summary>
/// HTTP/WebSocket API for web clients, optionally serving the Blazor web UI:
///   GET /api/cameras                        — JSON list of cameras and their streams
///   WS  /api/stream?path=...                — live fMP4 video (MSE-compatible)
///   GET /api/cameras/{name}/capabilities    — discovered device info + feature flags
///   GET /api/cameras/{name}/streaminfo      — encode profiles (resolution/fps/bitrate tables)
///   GET/POST .../led /pir; POST .../ptz /reboot; GET .../battery — camera control
///   /                                       — web UI (when enabled in the config)
/// Mutating endpoints require HTTP Basic auth when users are configured (same
/// credentials and per-camera permissions as RTSP).
/// </summary>
public static class WebApi
{
    private sealed record LedRequest(string? State, string? LightState);
    private sealed record PirRequest(bool? Enabled);
    private sealed record PtzRequest(string? Command, float? Speed);

    public static async Task RunAsync(string bindAddr, int port, bool webUi,
        IReadOnlyList<WebCameraInfo> cameras, IReadOnlyDictionary<string, string> users,
        int rtspPort, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{bindAddr}:{port}");
        if (webUi)
        {
            // Load the static web asset manifest (serves the UI's _content/* files when
            // running from the build output; published output has them physically in wwwroot).
            builder.WebHost.UseStaticWebAssets();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            // The UI's camera-list fetches run server-side (Blazor Server circuits)
            builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
        }
        var app = builder.Build();

        // Permissive CORS: this is a LAN streaming API, the web client may be served from anywhere.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
            if (ctx.Request.Method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                return;
            }
            await next();
        });

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/api/cameras", () =>
        {
            var payload = cameras.Select(c => new
            {
                name = c.Name,
                online = c.Control.Online,
                streams = c.Streams.Select(s => new
                {
                    kind = s.Kind,
                    path = s.Path,
                    ready = s.Hub.VideoReady,
                    codec = s.Hub.Codec?.ToString(),
                    width = s.Hub.Width,
                    height = s.Hub.Height,
                    rtspPort,
                }),
            });
            return Results.Json(payload);
        });

        // ------------------------------------------------------------ camera control

        // Denies a mutating request unless it carries valid Basic credentials of a
        // permitted user. Mirrors the RTSP rules: no configured users = open access.
        IResult? CheckAuth(HttpContext ctx, WebCameraInfo cam)
        {
            if (cam.PermittedUsers == null || users.Count == 0)
                return null;
            var creds = NetUtil.DecodeBasicAuth(ctx.Request.Headers.Authorization);
            if (creds != null
                && users.TryGetValue(creds.Value.User, out var expected)
                && expected == creds.Value.Pass
                && cam.PermittedUsers.Contains(creds.Value.User))
                return null;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"neolink\"";
            return Results.Json(new { error = "authentication required" }, statusCode: 401);
        }

        async Task<IResult> ExecAsync(string name, HttpContext ctx, bool mutating,
            Func<ICameraControl, CancellationToken, Task<IResult>> action)
        {
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
                return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
            if (mutating && CheckAuth(ctx, cam) is { } denied)
                return denied;
            try
            {
                return await action(cam.Control, ctx.RequestAborted);
            }
            catch (CameraOfflineException)
            {
                return Results.Json(new { error = "camera offline (reconnecting)" }, statusCode: 503);
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 404);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (CameraCommandException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
            catch (TimeoutException)
            {
                return Results.Json(new { error = "camera did not reply" }, statusCode: 504);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = $"camera connection error: {ex.Message}" }, statusCode: 502);
            }
        }

        app.MapGet("/api/cameras/{name}/capabilities", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                if (!control.Online)
                    return Results.Json(new { online = false });
                var caps = await control.GetCapabilitiesAsync(reqCt);
                return Results.Json(new
                {
                    online = true,
                    version = caps.Version == null ? null : new
                    {
                        name = caps.Version.Name,
                        model = caps.Version.Model,
                        serial = caps.Version.SerialNumber,
                        firmware = caps.Version.FirmwareVersion,
                        hardware = caps.Version.HardwareVersion,
                        build = caps.Version.BuildDay,
                    },
                    features = new
                    {
                        ptz = caps.Features.Ptz,
                        led = caps.Features.Led,
                        pir = caps.Features.Pir,
                        battery = caps.Features.Battery,
                    },
                    support = caps.Support == null ? null : XmlToJson(caps.Support),
                });
            }));

        app.MapGet("/api/cameras/{name}/streaminfo", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var info = await control.GetStreamInfoAsync(reqCt);
                var profiles = info?.StreamInfos
                    .SelectMany(si => si.EncodeTables.Select(t => new
                    {
                        type = t.Type,
                        width = t.Width,
                        height = t.Height,
                        defaultFramerate = t.DefaultFramerate,
                        defaultBitrate = t.DefaultBitrate,
                        framerates = ParseNumberTable(t.FramerateTable),
                        bitrates = ParseNumberTable(t.BitrateTable),
                    }))
                    .ToList();
                return Results.Json(new { profiles = (object?)profiles ?? Array.Empty<object>() });
            }));

        app.MapGet("/api/cameras/{name}/battery", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var battery = await control.GetBatteryInfoAsync(reqCt);
                return battery == null
                    ? Results.Json(new { error = "no battery info" }, statusCode: 404)
                    : Results.Json(XmlToJson(battery));
            }));

        app.MapGet("/api/cameras/{name}/led", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var led = await control.GetLedStateAsync(reqCt);
                return led == null
                    ? Results.Json(new { error = "no LED state" }, statusCode: 404)
                    : Results.Json(XmlToJson(led));
            }));

        app.MapPost("/api/cameras/{name}/led", (string name, LedRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                string[] allowed = { "open", "close", "auto" };
                if (req.State == null && req.LightState == null)
                    return Results.Json(new { error = "provide state and/or lightState" }, statusCode: 400);
                if ((req.State != null && !allowed.Contains(req.State)) ||
                    (req.LightState != null && !allowed.Contains(req.LightState)))
                    return Results.Json(new { error = "values must be open, close or auto" }, statusCode: 400);
                await control.SetLedStateAsync(req.State, req.LightState, reqCt);
                return Results.Json(new { ok = true });
            }));

        app.MapGet("/api/cameras/{name}/pir", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var pir = await control.GetPirStateAsync(reqCt);
                return pir == null
                    ? Results.Json(new { error = "no PIR settings" }, statusCode: 404)
                    : Results.Json(XmlToJson(pir));
            }));

        app.MapPost("/api/cameras/{name}/pir", (string name, PirRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Enabled == null)
                    return Results.Json(new { error = "provide enabled: true|false" }, statusCode: 400);
                await control.SetPirEnabledAsync(req.Enabled.Value, reqCt);
                return Results.Json(new { ok = true });
            }));

        app.MapPost("/api/cameras/{name}/ptz", (string name, PtzRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (string.IsNullOrEmpty(req.Command))
                    return Results.Json(new { error = "provide command: up|down|left|right|stop" }, statusCode: 400);
                float speed = Math.Clamp(req.Speed ?? 32f, 1f, 64f);
                await control.PtzAsync(req.Command, speed, reqCt);
                return Results.Json(new { ok = true });
            }));

        app.MapPost("/api/cameras/{name}/reboot", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                await control.RebootAsync(reqCt);
                return Results.Json(new { ok = true });
            }));

        app.Map("/api/stream", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("WebSocket endpoint");
                return;
            }
            string? path = ctx.Request.Query["path"];
            var hub = FindHub(cameras, path);
            if (hub == null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                await StreamToWebSocketAsync(ws, hub, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"{hub.Name}: web stream ended: {Log.Flatten(ex)}");
            }
        });

        if (webUi)
        {
            app.UseStaticFiles();     // physical wwwroot (published layout)
            app.UseAntiforgery();
            app.MapStaticAssets();    // manifest assets incl. the framework's blazor.web.js
            app.MapRazorComponents<Neolink.WebClient.Components.App>()
                .AddInteractiveServerRenderMode();
        }

        var displayHost = NetUtil.DisplayHost(bindAddr);
        if (webUi)
            Log.Info($"  Web UI: http://{displayHost}:{port}/");
        Log.Info($"  API:    http://{displayHost}:{port}/api/cameras" + (webUi ? "" : " (web UI disabled)"));
        await using var reg = ct.Register(() => app.Lifetime.StopApplication());
        await app.RunAsync().ConfigureAwait(false);
    }

    private static IStreamHub? FindHub(IReadOnlyList<WebCameraInfo> cameras, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        path = path.TrimEnd('/');
        foreach (var cam in cameras)
            foreach (var s in cam.Streams)
                if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                    return s.Hub;
        return null;
    }

    /// <summary>
    /// Generic camera-XML to JSON-friendly conversion, so newly discovered settings
    /// reach clients without a typed model. Leaves: numbers stay numbers; repeated
    /// element names become arrays; attributes are prefixed with '@'.
    /// </summary>
    private static object? XmlToJson(XElement el)
    {
        if (!el.HasElements)
        {
            var text = el.Value.Trim();
            return long.TryParse(text, out var n) ? n : text;
        }
        var obj = new Dictionary<string, object?>();
        foreach (var attr in el.Attributes())
            obj["@" + attr.Name.LocalName] = attr.Value;
        foreach (var group in el.Elements().GroupBy(e => e.Name.LocalName))
        {
            obj[group.Key] = group.Count() == 1
                ? XmlToJson(group.First())
                : group.Select(XmlToJson).ToList();
        }
        return obj;
    }

    /// <summary>Parses the camera's space-separated option tables ("30 25 20 15 ...").</summary>
    private static List<uint> ParseNumberTable(string table) =>
        table.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => uint.TryParse(s, out var v) ? v : 0u)
            .Where(v => v > 0)
            .ToList();

    // ------------------------------------------------------------------ fMP4 over WebSocket

    /// <summary>
    /// Protocol: one JSON text message (mime/codec/size), then binary messages:
    /// first the init segment, then one moof+mdat fragment per video frame.
    /// </summary>
    private static async Task StreamToWebSocketAsync(WebSocket ws, IStreamHub hub, CancellationToken appCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var ct = cts.Token;

        // Drain (and ignore) incoming messages so we notice the client closing.
        var receiveTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            cts.Cancel();
        }, CancellationToken.None);

        if (!await hub.WaitForDescribeInfoAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
        {
            await TryCloseAsync(ws, WebSocketCloseStatus.EndpointUnavailable, "stream not ready");
            return;
        }

        var codec = hub.Codec ?? VideoCodec.H264;
        var sps = hub.Sps;
        var pps = hub.Pps;
        if (sps == null || pps == null)
        {
            await TryCloseAsync(ws, WebSocketCloseStatus.EndpointUnavailable, "no codec parameters");
            return;
        }

        string codecString = FMp4.CodecString(codec, sps);
        var meta = JsonSerializer.Serialize(new
        {
            type = "init",
            codec = codecString,
            mime = $"video/mp4; codecs=\"{codecString}\"",
            width = hub.Width,
            height = hub.Height,
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(meta), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var init = FMp4.BuildInit(codec, sps, pps, hub.Vps, hub.Width, hub.Height);
        await ws.SendAsync(init, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);

        var (subId, reader) = hub.Subscribe();
        try
        {
            // Cameras deliver video in buffers that may hold anything from a single frame
            // to a whole multi-second GOP. MSE requires exactly one frame per MP4 sample,
            // so each buffer is split into access units. A buffer's true time span is only
            // known when the NEXT buffer arrives (its RTP delta), so hold one buffer back
            // and spread the measured delta evenly across its frames. All fragments of a
            // buffer go out as one WebSocket message.
            const uint NominalFrame = 3000;  // ~1/30s @ 90 kHz
            long lastIndex = -1;
            bool waitKeyframe = true;
            bool haveTs = false;
            uint prevTs = 0;
            ulong decodeTime = 0;
            uint sequence = 1;
            List<(byte[] Sample, bool Keyframe)>? pending = null;

            async Task FlushPendingAsync(uint totalDuration)
            {
                if (pending == null || pending.Count == 0) { pending = null; return; }
                uint per = Math.Clamp(totalDuration / (uint)pending.Count, 900u, 45_000u); // 10..500ms per frame
                using var batch = new MemoryStream();
                foreach (var (sample, key) in pending)
                {
                    batch.Write(FMp4.BuildFragment(sequence++, decodeTime, per, sample, key));
                    decodeTime += per;
                }
                pending = null;
                await ws.SendAsync(batch.ToArray(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }

            await foreach (var packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // The hub index is global across video AND audio packets, so it must be
                // tracked for every packet — otherwise interleaved audio makes every
                // video packet look like a drop (which would discard all P-frames).
                bool gap = lastIndex >= 0 && packet.Index != lastIndex + 1;
                lastIndex = packet.Index;

                if (packet is not HubVideo v) continue;
                if (gap)
                {
                    // Buffers were dropped: flush what we hold at a nominal rate and
                    // restart timing at the next keyframe.
                    waitKeyframe = true;
                    await FlushPendingAsync((uint)(NominalFrame * (pending?.Count ?? 1))).ConfigureAwait(false);
                    haveTs = false;
                }
                if (waitKeyframe)
                {
                    if (!v.Keyframe) continue;
                    waitKeyframe = false;
                }

                if (haveTs && pending != null)
                {
                    uint delta = unchecked(v.RtpTs - prevTs);
                    if (delta == 0 || delta > 30 * FMp4.Timescale)
                        delta = (uint)(NominalFrame * pending.Count); // clock garbage: assume ~30fps
                    await FlushPendingAsync(delta).ConfigureAwait(false);
                }
                prevTs = v.RtpTs;
                haveTs = true;

                var aus = FMp4.SplitAccessUnits(codec, v.AnnexB);
                if (aus.Count > 0)
                    pending = aus;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            hub.Unsubscribe(subId);
            cts.Cancel();
            await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "bye");
            try { await receiveTask.ConfigureAwait(false); } catch { }
        }
    }

    private static async Task TryCloseAsync(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(status, reason, closeCts.Token).ConfigureAwait(false);
            }
        }
        catch { }
    }
}
