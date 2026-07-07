// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neolink.Config;
using Neolink.Media;
using Neolink.Protocol;
using Neolink.Recording;
using Neolink.Streaming;

namespace Neolink.Web;

/// <summary>One camera stream as exposed over the web API.</summary>
public sealed record WebStreamInfo(string Kind, string Path, IStreamHub Hub);
/// <param name="ContinuousActive">Probe: is 24/7 footage being written right now? Null when the camera has no continuous recorder.</param>
/// <param name="SupportsEvents">False for generic RTSP cameras: no detection pushes, so event recording can't trigger.</param>
public sealed record WebCameraInfo(string Name, List<WebStreamInfo> Streams, ICameraControl Control,
    HashSet<string>? PermittedUsers, Func<bool>? ContinuousActive = null, bool SupportsEvents = true);

/// <summary>Everything the web API needs from the host.</summary>
public sealed class WebApiOptions
{
    public required string BindAddr { get; init; }
    public required int Port { get; init; }
    public required bool WebUi { get; init; }
    public required IReadOnlyList<WebCameraInfo> Cameras { get; init; }
    public required IReadOnlyDictionary<string, string> Users { get; init; }
    public required int RtspPort { get; init; }
    public EventStore? Events { get; init; }
    public RecordingSettings? RecordingSettings { get; init; }
    /// <summary>Recording defaults (retention etc.) reported to the UI; null when recording is off.</summary>
    public RecordingConfig? Recording { get; init; }
    public required UserStore UserStore { get; init; }
    public bool ResetAdminPassword { get; init; }
    public double TrickleSpeed { get; init; } = 4;
    public required string Version { get; init; }
    public required string ConfigPath { get; init; }
    public UpdateChecker? Updates { get; init; }
    /// <summary>Process/disk resource sampler feeding the UI's monitor page.</summary>
    public SystemMonitor? Monitor { get; init; }
    /// <summary>Captured server log lines for the UI's live log stream (admin only).</summary>
    public LogBuffer? Logs { get; init; }
    /// <summary>Gracefully stops the process; the supervisor (docker/systemd) restarts it.</summary>
    public required Action RestartRequested { get; init; }
}

/// <summary>
/// HTTP/WebSocket API for web clients, optionally serving the Blazor web UI:
///   GET /api/cameras                        — JSON list of cameras and their streams
///   WS  /api/stream?path=...                — live fMP4 video (MSE-compatible)
///   GET /api/cameras/{name}/capabilities    — discovered device info + feature flags
///   GET /api/cameras/{name}/streaminfo      — encode profiles (resolution/fps/bitrate tables)
///   POST/PUT .../settings/stream            — change an encode profile (needs http_address)
///   GET/POST .../led /pir; POST .../ptz /reboot; GET .../battery — camera control
///   GET /api/events[?camera=&amp;reviewed=&amp;limit=] — recorded detection events (when enabled)
///   GET /api/events/{id}/clip /thumb        — event artifacts; POST .../review to (un)dismiss
///   GET/POST /api/cameras/{name}/recording  — per-camera recording switches + event-type filter
///   GET /api/recordings/{camera}[/{date}[/{file}]] — browse/play continuous footage
///   /api/auth/* (status/setup/login/reset-admin), /api/users (admin CRUD),
///   GET/PUT /api/me/settings — web-UI accounts; once any account exists, every
///   other /api route requires a Bearer session token (or ?token= where headers
///   can't go: media elements and the stream WebSocket)
///   /                                       — web UI (when enabled in the config)
/// Mutating endpoints require HTTP Basic auth when users are configured (same
/// credentials and per-camera permissions as RTSP).
/// </summary>
public static class WebApi
{
    private sealed record LedRequest(string? State, string? LightState);
    private sealed record PirRequest(bool? Enabled);
    private sealed record PtzRequest(string? Command, float? Speed);
    private sealed record StreamSettingsRequest(string? Stream, uint? Width, uint? Height,
        uint? Framerate, uint? Bitrate);
    private sealed record ReviewRequest(bool? Reviewed);
    /// <summary>Retention fields: null = unchanged, negative = back to the server default, 0 = keep forever.
    /// RecordStream: null = unchanged, "" = back to the server default, else a served stream kind.</summary>
    private sealed record RecordingSettingsRequest(bool? Events, bool? Continuous, List<string>? EventTypes,
        int? EventRetentionDays = null, int? ContinuousRetentionDays = null, string? RecordStream = null);
    private sealed record CredentialsRequest(string? Username, string? Password);
    private sealed record PasswordRequest(string? Password);
    private sealed record AdminUiSettings(double? TrickleSpeed, string? StateDir, bool? ResetAdminPassword);
    private sealed record AdminRecordingSettings(string? Path, int? RetentionDays, int? PreSeconds,
        int? PostSeconds, int? MaxClipSeconds, string? Stream, int? SegmentMinutes, int? ContinuousRetentionDays);
    private sealed record AdminConfigRequest(string? Bind, int? BindPort, int? WebPort, string? WebBind,
        bool? WebUi, AdminUiSettings? Ui, AdminRecordingSettings? Recording, bool? RemoveRecording);

    public static async Task RunAsync(WebApiOptions o, CancellationToken ct)
    {
        var bindAddr = o.BindAddr;
        var port = o.Port;
        var webUi = o.WebUi;
        var cameras = o.Cameras;
        var users = o.Users;
        var rtspPort = o.RtspPort;
        var events = o.Events;
        var recordingSettings = o.RecordingSettings;
        var userStore = o.UserStore;
        var resetAdminPassword = o.ResetAdminPassword;
        var trickleSpeed = o.TrickleSpeed;

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
            // Circuits must talk to THIS server via loopback, never back out through
            // a reverse proxy's public URL (TLS/hairpin failures behind HAProxy etc.).
            var loopbackHost = bindAddr is "0.0.0.0" or "::" or "[::]" or "*" ? "127.0.0.1" : bindAddr;
            builder.Services.AddSingleton(new Neolink.WebClient.LocalApiInfo($"http://{loopbackHost}:{port}"));
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

        // ------------------------------------------------------------ web-UI accounts

        // The session token travels as a Bearer header, or as ?token= for the
        // places headers can't go (video/img elements, the stream WebSocket).
        UserRecord? SessionUser(HttpContext ctx)
        {
            string? token = null;
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = header["Bearer ".Length..].Trim();
            token ??= ctx.Request.Query["token"];
            return string.IsNullOrEmpty(token) ? null : userStore.ValidateToken(token);
        }

        // Once any account exists, every /api call (except the auth handshake
        // itself) requires a valid session. The Blazor UI shell stays public so
        // the login screen can render.
        app.Use(async (ctx, next) =>
        {
            if (userStore.Enabled
                && ctx.Request.Path.StartsWithSegments("/api")
                && !ctx.Request.Path.StartsWithSegments("/api/auth"))
            {
                var user = SessionUser(ctx);
                if (user == null)
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "authentication required" });
                    return;
                }
                ctx.Items["authUser"] = user;
            }
            await next();
        });

        bool IsAdmin(HttpContext ctx) => ctx.Items["authUser"] is UserRecord { Admin: true };
        string? SessionName(HttpContext ctx) => (ctx.Items["authUser"] as UserRecord)?.Name;

        app.MapGet("/api/auth/status", (HttpContext ctx) =>
        {
            var user = userStore.Enabled ? SessionUser(ctx) : null;
            return Results.Json(new
            {
                enabled = userStore.Enabled,
                setupRequired = !userStore.Enabled,
                resetAvailable = resetAdminPassword && userStore.Enabled,
                user = user?.Name,
                admin = user?.Admin ?? false,
            });
        });

        // First account = the admin; creating it is what turns authentication on.
        app.MapPost("/api/auth/setup", (CredentialsRequest req) =>
        {
            if (userStore.Enabled)
                return Results.Json(new { error = "already set up" }, statusCode: 409);
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return Results.Json(new { error = "provide username and password" }, statusCode: 400);
            try
            {
                var admin = userStore.Add(req.Username.Trim(), req.Password, admin: true);
                Log.Warn($"Web UI authentication ENABLED: admin account '{admin.Name}' created");
                return Results.Json(new { token = userStore.IssueToken(admin), user = admin.Name, admin = true });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/auth/login", async (CredentialsRequest req) =>
        {
            var user = req.Username == null || req.Password == null
                ? null
                : userStore.Verify(req.Username.Trim(), req.Password);
            if (user == null)
            {
                await Task.Delay(Random.Shared.Next(250, 500)); // blunt brute-force pacing
                return Results.Json(new { error = "wrong username or password" }, statusCode: 401);
            }
            return Results.Json(new { token = userStore.IssueToken(user), user = user.Name, admin = user.Admin });
        });

        // Recovery: only while reset_admin_password=true in the config.
        app.MapPost("/api/auth/reset-admin", (PasswordRequest req) =>
        {
            if (!resetAdminPassword || !userStore.Enabled)
                return Results.Json(new { error = "reset not enabled" }, statusCode: 404);
            var admin = userStore.AdminUser();
            if (admin == null || string.IsNullOrEmpty(req.Password))
                return Results.Json(new { error = "provide password" }, statusCode: 400);
            try
            {
                userStore.SetPassword(admin.Name, req.Password);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            Log.Warn($"Admin password for '{admin.Name}' was RESET via reset_admin_password — " +
                     "set the flag back to false now");
            return Results.Json(new { ok = true, user = admin.Name });
        });

        // Account management (admin only). Normal users are added by the admin.
        app.MapGet("/api/users", (HttpContext ctx) =>
            !IsAdmin(ctx)
                ? Results.Json(new { error = "admin only" }, statusCode: 403)
                : Results.Json(userStore.List().Select(u => new { name = u.Name, admin = u.Admin })));

        app.MapPost("/api/users", (CredentialsRequest req, HttpContext ctx) =>
        {
            if (!IsAdmin(ctx))
                return Results.Json(new { error = "admin only" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return Results.Json(new { error = "provide username and password" }, statusCode: 400);
            try
            {
                userStore.Add(req.Username.Trim(), req.Password, admin: false);
                return Results.Json(new { ok = true });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPut("/api/users/{name}", (string name, PasswordRequest req, HttpContext ctx) =>
        {
            if (!IsAdmin(ctx))
                return Results.Json(new { error = "admin only" }, statusCode: 403);
            if (string.IsNullOrEmpty(req.Password))
                return Results.Json(new { error = "provide password" }, statusCode: 400);
            try
            {
                return userStore.SetPassword(name, req.Password)
                    ? Results.Json(new { ok = true })
                    : Results.Json(new { error = "unknown user" }, statusCode: 404);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapDelete("/api/users/{name}", (string name, HttpContext ctx) =>
        {
            if (!IsAdmin(ctx))
                return Results.Json(new { error = "admin only" }, statusCode: 403);
            return userStore.Delete(name)
                ? Results.Json(new { ok = true })
                : Results.Json(new { error = "unknown user (the admin cannot be deleted)" }, statusCode: 400);
        });

        // ------------------------------------------------------------ admin: server settings + restart

        IResult? AdminOnly(HttpContext ctx) => IsAdmin(ctx)
            ? null
            : Results.Json(new
            {
                error = userStore.Enabled ? "admin only" : "create the admin account first (server settings need one)",
            }, statusCode: 403);

        app.MapGet("/api/admin/config", (HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;
            try
            {
                return Results.Json(ConfigEditor.Describe(o.ConfigPath));
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPut("/api/admin/config", (AdminConfigRequest req, HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;
            try
            {
                ConfigEditor.Apply(o.ConfigPath, root =>
                {
                    if (req.Bind != null) ConfigEditor.Set(root, "bind", req.Bind);
                    if (req.BindPort != null) ConfigEditor.Set(root, "bind_port", req.BindPort);
                    if (req.WebPort != null) ConfigEditor.Set(root, "web_port", req.WebPort);
                    if (req.WebBind != null)
                        ConfigEditor.Set(root, "web_bind", req.WebBind.Length == 0 ? null : req.WebBind);
                    if (req.WebUi != null) ConfigEditor.Set(root, "webui", req.WebUi);

                    if (req.Ui is { } u)
                    {
                        var ui = ConfigEditor.Section(root, "ui");
                        if (u.TrickleSpeed != null) ConfigEditor.Set(ui, "trickle_speed", u.TrickleSpeed);
                        if (u.StateDir != null)
                            ConfigEditor.Set(ui, "state_dir", u.StateDir.Length == 0 ? null : u.StateDir);
                        if (u.ResetAdminPassword != null)
                        {
                            ConfigEditor.Set(ui, "reset_admin_password", u.ResetAdminPassword);
                            ConfigEditor.Set(root, "reset_admin_password", null); // retire the legacy spelling
                        }
                    }

                    if (req.RemoveRecording == true)
                    {
                        ConfigEditor.Set(root, "recording", null);
                    }
                    else if (req.Recording is { } r)
                    {
                        var rec = ConfigEditor.Section(root, "recording");
                        if (r.Path != null) ConfigEditor.Set(rec, "path", r.Path);
                        if (r.RetentionDays != null) ConfigEditor.Set(rec, "retention_days", r.RetentionDays);
                        if (r.PreSeconds != null) ConfigEditor.Set(rec, "pre_seconds", r.PreSeconds);
                        if (r.PostSeconds != null) ConfigEditor.Set(rec, "post_seconds", r.PostSeconds);
                        if (r.MaxClipSeconds != null) ConfigEditor.Set(rec, "max_clip_seconds", r.MaxClipSeconds);
                        if (r.Stream != null) ConfigEditor.Set(rec, "stream", r.Stream);
                        if (r.SegmentMinutes != null) ConfigEditor.Set(rec, "segment_minutes", r.SegmentMinutes);
                        if (r.ContinuousRetentionDays != null)
                            ConfigEditor.Set(rec, "continuous_retention_days", r.ContinuousRetentionDays);
                    }
                });
                Log.Warn($"config.json updated via the web UI by '{SessionName(ctx)}' — restart to apply");
                return Results.Json(new { ok = true, requiresRestart = true });
            }
            catch (FormatException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = $"config.json is not writable: {ex.Message}" }, statusCode: 409);
            }
        });

        app.MapPost("/api/admin/restart", (HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;
            Log.Warn($"Restart requested via the web UI by '{SessionName(ctx)}'");
            _ = Task.Run(async () =>
            {
                await Task.Delay(700); // let this response reach the browser first
                o.RestartRequested();
            });
            return Results.Json(new
            {
                ok = true,
                note = "shutting down — the restart policy (docker / systemd) brings the service back",
            });
        });

        // Per-user UI settings: an opaque JSON blob the client owns.
        app.MapGet("/api/me/settings", (HttpContext ctx) =>
            SessionName(ctx) is { } me
                ? Results.Content(userStore.GetSettings(me), "application/json")
                : Results.Json(new { error = "authentication disabled" }, statusCode: 404));

        app.MapPut("/api/me/settings", async (HttpContext ctx) =>
        {
            if (SessionName(ctx) is not { } me)
                return Results.Json(new { error = "authentication disabled" }, statusCode: 404);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                userStore.SetSettings(me, json);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                return Results.Json(new { error = "invalid settings payload" }, statusCode: 400);
            }
        });

        // Feature discovery, so clients can hide UI for what this server won't do.
        app.MapGet("/api/features", () => Results.Json(new
        {
            events = events != null,
            continuous = events != null && RecordingConfig.ContinuousEnabled,
            trickleSpeed,
            version = o.Version,
            latestVersion = o.Updates?.Latest,
            repoUrl = UpdateChecker.RepoUrl,
        }));

        app.MapGet("/api/cameras", () =>
        {
            var payload = cameras.Select(c => new
            {
                name = c.Name,
                online = c.Control.Online,
                // 24/7 footage being written right now (drives the UI's REC badge)
                recording = c.ContinuousActive?.Invoke() ?? false,
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
        // When web-UI accounts exist, the session (already checked by the auth
        // middleware) supersedes the RTSP Basic mapping entirely.
        IResult? CheckAuth(HttpContext ctx, WebCameraInfo cam)
        {
            if (userStore.Enabled)
                return ctx.Items.ContainsKey("authUser")
                    ? null
                    : Results.Json(new { error = "authentication required" }, statusCode: 401);
            if (cam.PermittedUsers == null || users.Count == 0)
                return null;
            var creds = NetUtil.DecodeBasicAuth(ctx.Request.Headers.Authorization);
            if (creds != null
                && users.TryGetValue(creds.Value.User, out var expected)
                && NetUtil.FixedTimeEquals(expected, creds.Value.Pass)
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
            catch (ReolinkApiException ex)
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
                        streamSettings = control.CanSetStreamSettings,
                        // Baichuan-only: a generic RTSP camera can't be told to reboot
                        reboot = control is not GenericCameraControl,
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

        // Stream encode settings ride the camera's Reolink HTTP API (http_address in
        // the config); the Baichuan protocol has no verified setter. The camera
        // restarts the affected stream to apply — CameraService reconnects on its own.
        Task<IResult> SetStreamSettings(string name, StreamSettingsRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                string[] streams = { "mainStream", "subStream", "externStream" };
                if (req.Stream == null || !streams.Contains(req.Stream))
                    return Results.Json(new { error = "provide stream: mainStream|subStream|externStream" }, statusCode: 400);
                if ((req.Width == null) != (req.Height == null))
                    return Results.Json(new { error = "width and height must be given together" }, statusCode: 400);
                if (req.Width == null && req.Framerate == null && req.Bitrate == null)
                    return Results.Json(new { error = "provide width+height, framerate and/or bitrate" }, statusCode: 400);
                await control.SetStreamSettingsAsync(req.Stream, req.Width, req.Height,
                    req.Framerate, req.Bitrate, reqCt);
                return Results.Json(new { ok = true, note = "the camera restarts its stream to apply the change" });
            });
        app.MapPost("/api/cameras/{name}/settings/stream", SetStreamSettings);
        app.MapPut("/api/cameras/{name}/settings/stream", SetStreamSettings);

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

        // ------------------------------------------------------------ recorded events

        if (events != null)
        {
            object Shape(EventRecord r) => new
            {
                id = r.Id,
                camera = r.Camera,
                start = r.StartUtc,
                end = r.EndUtc,
                labels = r.Labels,
                reviewed = r.Reviewed,
                ongoing = r.Ongoing,
                hasClip = r.HasClip,
                hasThumb = r.HasThumb,
                hasPreview = r.HasPreview,
            };

            app.MapGet("/api/events", (string? camera, bool? reviewed, int? limit, string? date) =>
            {
                DateTime? day = null;
                if (date != null)
                {
                    if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var d))
                        return Results.Json(new { error = "date must be yyyy-MM-dd" }, statusCode: 400);
                    day = d;
                }
                return Results.Json(events.List(camera, reviewed, limit ?? 200, day).Select(Shape));
            });

            app.MapGet("/api/events/{id}/clip", (string id) =>
            {
                var path = events.ArtifactPath(id, "clip.mp4");
                return path == null
                    ? Results.Json(new { error = "no clip for this event" }, statusCode: 404)
                    : Results.File(path, "video/mp4", enableRangeProcessing: true);
            });

            app.MapGet("/api/events/{id}/thumb", (string id) =>
            {
                var path = events.ArtifactPath(id, "thumb.jpg");
                return path == null
                    ? Results.Json(new { error = "no thumbnail for this event" }, statusCode: 404)
                    : Results.File(path, "image/jpeg");
            });

            // The clip's low-res sub-stream twin, used by the strip's ambient previews.
            app.MapGet("/api/events/{id}/preview", (string id) =>
            {
                var path = events.ArtifactPath(id, "preview.mp4");
                return path == null
                    ? Results.Json(new { error = "no preview for this event" }, statusCode: 404)
                    : Results.File(path, "video/mp4", enableRangeProcessing: true);
            });

            app.MapPost("/api/events/{id}/review", (string id, ReviewRequest req, HttpContext ctx) =>
            {
                var rec = events.Find(id);
                if (rec == null)
                    return Results.Json(new { error = "unknown event" }, statusCode: 404);
                // Same rules as camera control: reviewing an event needs control rights
                // on its camera. Web-UI sessions (validated by the middleware) always
                // qualify; events of removed cameras fall back to any valid RTSP user.
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, rec.Camera, StringComparison.OrdinalIgnoreCase));
                if (userStore.Enabled)
                {
                    // authenticated by the middleware — allowed
                }
                else if (cam != null)
                {
                    if (CheckAuth(ctx, cam) is { } denied) return denied;
                }
                else if (users.Count > 0)
                {
                    var creds = NetUtil.DecodeBasicAuth(ctx.Request.Headers.Authorization);
                    if (creds == null || !users.TryGetValue(creds.Value.User, out var pw)
                        || !NetUtil.FixedTimeEquals(pw, creds.Value.Pass))
                    {
                        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"neolink\"";
                        return Results.Json(new { error = "authentication required" }, statusCode: 401);
                    }
                }
                events.SetReviewed(id, req.Reviewed ?? true);
                return Results.Json(new { ok = true });
            });
        }

        // ------------------------------------------------------------ recording switches + footage

        if (events != null && recordingSettings != null)
        {
            // The stream recording falls back to when no per-camera override is set:
            // the config's recording.stream if this camera serves it, else main/first.
            string DefaultRecordKind(WebCameraInfo cam)
            {
                var cfg = o.Recording?.Stream ?? "auto";
                if (cfg != "auto" && cam.Streams.Any(s => s.Kind == cfg)) return cfg;
                return (cam.Streams.FirstOrDefault(s => s.Kind == "mainStream")
                        ?? cam.Streams.FirstOrDefault())?.Kind ?? "mainStream";
            }

            object ShapeSettings(WebCameraInfo cam, CameraRecordingSettings s) => new
            {
                events = s.Events,
                eventsAvailable = cam.SupportsEvents,
                continuous = RecordingConfig.ContinuousEnabled && s.Continuous,
                continuousAvailable = RecordingConfig.ContinuousEnabled,
                eventTypes = s.EventTypes,
                knownTypes = CameraRecordingSettings.KnownLabels,
                // Per-camera retention overrides (null = default) + the server defaults
                // so the UI can label what "default" currently means.
                eventRetentionDays = s.EventRetentionDays,
                continuousRetentionDays = s.ContinuousRetentionDays,
                defaultEventRetentionDays = o.Recording?.RetentionDays ?? 7,
                defaultContinuousRetentionDays = o.Recording?.EffectiveContinuousRetentionDays ?? 7,
                // Which stream gets taped: the override, the resolved default, and
                // what this camera actually serves (the UI's dropdown options).
                recordStream = s.RecordStream,
                defaultRecordStream = DefaultRecordKind(cam),
                availableStreams = cam.Streams.Select(x => x.Kind).ToList(),
            };

            app.MapGet("/api/cameras/{name}/recording", (string name, HttpContext ctx) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                return cam == null
                    ? Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404)
                    : Results.Json(ShapeSettings(cam, recordingSettings.Get(cam.Name)));
            });

            app.MapPost("/api/cameras/{name}/recording", (string name, RecordingSettingsRequest req, HttpContext ctx) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (cam == null)
                    return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
                if (CheckAuth(ctx, cam) is { } denied)
                    return denied;

                List<string>? types = null;
                if (req.EventTypes != null)
                {
                    types = req.EventTypes.Select(t => t.Trim().ToLowerInvariant())
                        .Where(t => t.Length > 0).Distinct().ToList();
                }
                // The continuous switch is inert while the feature is disabled.
                var continuous = RecordingConfig.ContinuousEnabled ? req.Continuous : null;
                // Retention: negative = clear the override (use the server default).
                // Capped at 100 years — beyond that is "forever", and unbounded values
                // would overflow the cleanup pass's date arithmetic.
                static int? Retention(int? v) => v switch
                {
                    null or < 0 => null,
                    > 36500 => 36500,
                    _ => v,
                };
                // Record stream: "" clears the override; a value must be served.
                if (req.RecordStream is { Length: > 0 } rs && cam.Streams.All(s => s.Kind != rs))
                    return Results.Json(new
                    {
                        error = $"recordStream must be one of: {string.Join(", ", cam.Streams.Select(s => s.Kind))}",
                    }, statusCode: 400);
                var updated = recordingSettings.Update(cam.Name, req.Events, continuous,
                    types, setEventTypes: req.EventTypes != null,
                    eventRetentionDays: Retention(req.EventRetentionDays),
                    setEventRetention: req.EventRetentionDays != null,
                    continuousRetentionDays: Retention(req.ContinuousRetentionDays),
                    setContinuousRetention: req.ContinuousRetentionDays != null,
                    recordStream: req.RecordStream is { Length: > 0 } v ? v : null,
                    setRecordStream: req.RecordStream != null);
                return Results.Json(ShapeSettings(cam, updated));
            });
        }

        if (events != null && RecordingConfig.ContinuousEnabled)
        {
            app.MapGet("/api/recordings/{camera}", (string camera) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
                return cam == null
                    ? Results.Json(new { error = $"unknown camera '{camera}'" }, statusCode: 404)
                    : Results.Json(events.ListContinuousDays(cam.Name));
            });

            app.MapGet("/api/recordings/{camera}/{date}", (string camera, string date) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
                if (cam == null)
                    return Results.Json(new { error = $"unknown camera '{camera}'" }, statusCode: 404);
                var segments = events.ListSegments(cam.Name, date)
                    .Select(s => new { file = s.File, size = s.Size });
                return Results.Json(segments);
            });

            app.MapGet("/api/recordings/{camera}/{date}/{file}", (string camera, string date, string file) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
                var path = cam == null ? null : events.SegmentPath(cam.Name, date, file);
                return path == null
                    ? Results.Json(new { error = "no such recording" }, statusCode: 404)
                    : Results.File(path, "video/mp4", enableRangeProcessing: true);
            });
        }

        // ------------------------------------------------------------ resource monitor

        if (o.Monitor is { } monitor)
        {
            // Incremental polling: ?after=<unix ms> returns only newer samples, so
            // the 2s poll ships a couple hundred bytes, not the whole hour.
            app.MapGet("/api/system/stats", (long? after) => Results.Json(new
            {
                info = monitor.Info(),
                samples = monitor.Since(after ?? 0).Select(s => new
                {
                    t = s.UnixMs,
                    cpu = s.CpuPercent,
                    ws = s.WorkingSetBytes,
                    heap = s.ManagedHeapBytes,
                    alloc = s.AllocMbPerSec,
                    thr = s.Threads,
                    fd = s.Handles,
                    dTot = s.DiskTotalBytes,
                    dFree = s.DiskFreeBytes,
                    rec = s.RecordingsBytes,
                    view = s.Viewers,
                    recCams = s.RecordingCameras,
                    wMb = s.StorageMbPerSec,
                    wFiles = s.StorageFiles,
                }),
            }));
        }

        if (o.Logs is { } logBuffer)
        {
            // Live server log tail over WebSocket: the backlog as one JSON array,
            // then one JSON entry per line. Logs reveal paths, camera names and
            // usernames, so this is strictly admin — and with authentication off
            // there IS no admin, matching the other admin-only endpoints.
            app.Map("/api/system/logs", async ctx =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("WebSocket endpoint");
                    return;
                }
                if (!IsAdmin(ctx))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        error = userStore.Enabled
                            ? "admin only"
                            : "create the admin account first (live logs need one)",
                    });
                    return;
                }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                try
                {
                    await StreamLogsAsync(ws, logBuffer, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug($"Log stream ended: {Log.Flatten(ex)}");
                }
            });
        }

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

    // ------------------------------------------------------------------ live logs over WebSocket

    /// <summary>Sends the backlog as one JSON array, then one JSON object per new line.</summary>
    private static async Task StreamLogsAsync(WebSocket ws, LogBuffer logs, CancellationToken appCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var ct = cts.Token;

        // Drain (and ignore) incoming messages so we notice the client closing.
        var receiveTask = Task.Run(async () =>
        {
            var buf = new byte[512];
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

        object Shape(LogEntry e) => new { seq = e.Seq, t = e.UnixMs, lvl = e.Level, msg = e.Message };

        async Task SendJsonAsync(object payload) =>
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var (subId, reader) = logs.Subscribe();
        try
        {
            // Subscribe BEFORE snapshotting so no line can fall between the two;
            // the client dedupes the overlap by sequence number.
            await SendJsonAsync(logs.Snapshot().Select(Shape).ToList()).ConfigureAwait(false);
            await foreach (var entry in reader.ReadAllAsync(ct).ConfigureAwait(false))
                await SendJsonAsync(Shape(entry)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            logs.Unsubscribe(subId);
            cts.Cancel();
            await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "bye");
            try { await receiveTask.ConfigureAwait(false); } catch { }
        }
    }

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

        var (subId, reader) = hub.Subscribe(viewer: true);
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
                // Send straight from the stream's buffer — copying it per batch would
                // double the allocation on the per-frame hot path, per viewer.
                var payload = new ArraySegment<byte>(batch.GetBuffer(), 0, (int)batch.Length);
                await ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
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
