// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <param name="Battery">Latest battery reading, or null (mains-powered / generic RTSP / unknown yet).</param>
/// <param name="Asleep">Probe: is the camera intentionally disconnected so it can sleep (battery doze)?</param>
/// <param name="SirenOn">Latest siren state the camera pushed, or null (no push yet / unsupported).</param>
/// <param name="PrivacyOn">Latest privacy-mode state the camera pushed, or null (no push yet / unsupported).</param>
/// <param name="Suspended">Probe: has the user suspended this camera in Neolink (no connection held)?</param>
/// <param name="SetSuspended">Suspends/resumes the camera at runtime and persists it; null when unsupported.</param>
/// <param name="ActiveSegment">Probe: the continuous segment being written right now (day, file, media seconds) — the recorder's in-memory truth for the day listing.</param>
/// <param name="ContinuousEnabled">Probe: is 24/7 recording switched ON for this camera (the persisted setting, not whether a segment is open right now)? Null when the camera has no continuous recorder.</param>
/// <param name="SetContinuousEnabled">Turns 24/7 recording on/off at runtime and persists it (same setting as the web UI toggle); null when unsupported.</param>
public sealed record WebCameraInfo(string Name, List<WebStreamInfo> Streams, ICameraControl Control,
    HashSet<string>? PermittedUsers, Func<bool>? ContinuousActive = null, bool SupportsEvents = true,
    Func<BatteryPush?>? Battery = null, Func<bool>? Asleep = null,
    Func<bool?>? SirenOn = null, Func<bool?>? PrivacyOn = null,
    Func<bool>? Suspended = null, Action<bool>? SetSuspended = null,
    Func<(string Date, string File, double Seconds)?>? ActiveSegment = null,
    Func<bool>? ContinuousEnabled = null, Action<bool>? SetContinuousEnabled = null)
{
    /// <summary>The camera's event recorder when server-side event recording runs —
    /// the shared entry point for on-demand clips (web UI button and HA switch).</summary>
    public EventRecorder? EventRecorder { get; set; }

    /// <summary>The configured network address (host, or host:port when non-default) —
    /// shown on the camera-settings identity strip. Null for generic RTSP cameras
    /// (their address lives in the stream URL) or when unknown.</summary>
    public string? Address { get; init; }

    /// <summary>Baichuan-over-UDP transport (beta): UDP-only battery models
    /// (Argus family). Surfaced so the UI can badge these cameras as beta.</summary>
    public bool Udp { get; init; }
}

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
    /// <summary>An archive storage tier is configured, so per-camera archiving can be offered.</summary>
    public bool ArchiveAvailable { get; init; }
    /// <summary>Configured storage tiers and their capacity (feeds the monitor and the full-storage banners).</summary>
    public StorageLocations? Storage { get; init; }
    /// <summary>Free-space trend per location — the "~N days until full" forecast on the monitor.</summary>
    public StorageForecast? Forecast { get; init; }
    /// <summary>The server secret (key source/fingerprint reporting for admins).</summary>
    public Neolink.Notifications.SecretProtector? Secrets { get; init; }
    public required UserStore UserStore { get; init; }
    public bool ResetAdminPassword { get; init; }
    public double TrickleSpeed { get; init; } = 4;
    /// <summary>Beta: two-way talk (browser mic → camera speaker). Off unless ui.talk enables it.</summary>
    public bool TalkEnabled { get; init; }
    /// <summary>Show the admin background-process strip in the web UI (ui.show_background_tasks).</summary>
    public bool ShowBackgroundTasks { get; init; } = true;
    public required string Version { get; init; }
    public required string ConfigPath { get; init; }
    public UpdateChecker? Updates { get; init; }
    /// <summary>Process/disk resource sampler feeding the UI's monitor page.</summary>
    public SystemMonitor? Monitor { get; init; }
    /// <summary>Email-notification service (config store + test send); admin only.</summary>
    public Neolink.Notifications.Notifier? Notifier { get; init; }
    /// <summary>Captured server log lines for the UI's live log stream (admin only).</summary>
    public LogBuffer? Logs { get; init; }
    /// <summary>Gracefully stops the process; the supervisor (docker/systemd) restarts it.</summary>
    public required Action RestartRequested { get; init; }
    /// <summary>Invoked (fire-and-forget) with a camera name after a web-UI/API change
    /// to one of its settings, so the Home Assistant bridge can re-publish that
    /// camera's state at once instead of waiting for its periodic refresh. Null when
    /// MQTT isn't configured.</summary>
    public Func<string, Task>? OnCameraChanged { get; init; }
}

/// <summary>
/// HTTP/WebSocket API for web clients, optionally serving the Blazor web UI:
///   GET /api/cameras                        — JSON list of cameras and their streams
///   WS  /api/stream?path=...                — live fMP4 video (MSE-compatible)
///   GET /api/cameras/{name}/capabilities    — discovered device info + feature flags
///   GET /api/cameras/{name}/streaminfo      — encode profiles (resolution/fps/bitrate tables)
///   GET/POST/PUT .../settings/stream        — current encode selection / change it (needs http_address)
///   GET/POST .../led /pir /zoomfocus /floodlight /siren /privacy /whiteled;
///   POST .../ptz /reboot; GET .../battery   — camera control
///   GET .../httpfeatures — combined HTTP-API extras (picture/volume/Wi-Fi/presets/
///   quick replies/auto-track/SD); POST .../image /volume /ptzpreset /quickreply /autotrack
///   GET /api/cameras/{name}/snapshot.jpg     — a current still (server-cached; ?maxAge= seconds)
///   GET /api/events[?camera=&amp;reviewed=&amp;limit=] — recorded detection events (when enabled)
///   GET /api/events/{id}[/clip /thumb]      — one event / its artifacts; POST .../review to (un)dismiss
///   POST /api/events/delete {ids[],estimate} — bulk delete events + files (admin; ?estimate summarizes)
///   GET/POST /api/cameras/{name}/recording  — per-camera recording switches + event-type filter
///   POST /api/cameras/{name}/record         — start/stop an on-demand clip (one clip, auto-capped)
///   GET /api/recordings/{camera}[/{date}[/{file}]] — browse/play continuous footage
///   GET .../{date}/export?from=&to=[&format=mp4][&estimate=1] — a range (≤ one day) as one MP4 or a zip
///   /api/auth/* (status/setup/login/reset-admin), /api/users (admin CRUD),
///   GET/PUT /api/me/settings[/{page}] — web-UI accounts; once any account exists, every
///   other /api route requires a Bearer session token (or ?token= where headers
///   can't go: media elements and the stream WebSocket)
///   /                                       — web UI (when enabled in the config)
/// Mutating endpoints require HTTP Basic auth when users are configured (same
/// credentials and per-camera permissions as RTSP).
/// </summary>
public static class WebApi
{
    private sealed record LedRequest(string? State, string? LightState,
        string? DoorbellLightState, int? IrBrightness);
    private sealed record PirRequest(bool? Enabled);
    private sealed record PtzRequest(string? Command, float? Speed);
    private sealed record ZoomFocusRequest(uint? Zoom, uint? Focus);
    private sealed record FloodlightRequest(int? Brightness, bool? Auto);
    private sealed record WhiteLedRequest(int? Brightness, bool? On, int? Mode);
    private sealed record ImageRequest(int? Bright, int? Contrast, int? Saturation, int? Hue, int? Sharpen,
        string? DayNight, string? AntiFlicker, bool? Flip, bool? Mirror);
    private sealed record VolumeRequest(int? Volume);
    private sealed record PtzPresetRequest(int? Id, string? Name, bool? Save);
    private sealed record QuickReplyRequest(int? Id);
    private sealed record AutoReplyRequest(int? FileId, int? Timeout);
    /// <summary>Camera add/edit. Password is WRITE-ONLY: null keeps the stored one,
    /// "" clears it, a value sets it. RTSP URLs sent back masked ("****") mean keep.</summary>
    private sealed record AdminCameraRequest(string? OriginalName, string? Name, string? Type,
        string? Address, string? Username, string? Password, int? ChannelId, string? HttpAddress,
        string? RtspMain, string? RtspSub);
    private sealed record AdminCameraTestRequest(string? Name, string? Type, string? Address,
        string? Username, string? Password, int? ChannelId, string? RtspMain, string? RtspSub);
    private sealed record AutoTrackRequest(bool? On);
    private sealed record MdSensitivityRequest(int? Sensitivity);
    private sealed record AiSensitivityRequest(string? Type, int? Sensitivity);
    private sealed record HdrRequest(int? Value);
    private sealed record OsdRequest(bool? ShowName, string? NamePos, bool? ShowTime, string? TimePos, bool? Watermark);
    private sealed record SirenRequest(bool? On);
    private sealed record PrivacyRequest(bool? On);
    private sealed record SuspendRequest(bool? Suspended);
    private sealed record RecordOnDemandRequest(bool Active);
    private sealed record StreamSettingsRequest(string? Stream, uint? Width, uint? Height,
        uint? Framerate, uint? Bitrate);
    private sealed record ReviewRequest(bool? Reviewed);
    private sealed record EventDeleteRequest(List<string>? Ids, bool? Estimate);
    /// <summary>Notification settings update. Password is WRITE-ONLY: null keeps the
    /// stored one, "" clears it, a value sets it. It is never returned by GET.</summary>
    private sealed record NotificationRequest(bool? Enabled, string? Recipient,
        string? SmtpHost, int? SmtpPort, string? Security, string? Username, string? Password,
        string? From, string? FromName,
        bool? AlertStorage, bool? AlertOverload, bool? AlertCameraOffline, bool? AlertWriteFailure,
        int? OfflineThresholdMinutes, Dictionary<string, int>? CameraOfflineOverrides);
    /// <summary>Retention fields: null = unchanged, negative = back to the server default, 0 = keep forever.
    /// RecordStream: null = unchanged, "" = back to the server default, else a served stream kind.
    /// Capture schedule: applied only while ScheduleEnabled; ScheduleDays null = unchanged,
    /// empty or all seven = every day; ScheduleStart/ScheduleEnd null = unchanged,
    /// "" = midnight (both cleared = all day).</summary>
    private sealed record RecordingSettingsRequest(bool? Events, bool? Continuous, List<string>? EventTypes,
        int? EventRetentionDays = null, int? ContinuousRetentionDays = null, string? RecordStream = null,
        List<string>? ScheduleDays = null, string? ScheduleStart = null, string? ScheduleEnd = null,
        bool? ScheduleEnabled = null,
        bool? ArchiveEvents = null, bool? ArchiveContinuous = null, int? ArchiveRetentionDays = null);
    private sealed record CredentialsRequest(string? Username, string? Password);

    /// <summary>
    /// Serves a recording segment or event clip. Old-format (fragmented) files —
    /// pre-upgrade archives and the segment currently being recorded — are
    /// presented through <see cref="VirtualMp4"/> with a classic seek index
    /// synthesized in memory, so jumping around them costs a couple of range
    /// requests instead of a crawl over thousands of per-frame fragment headers.
    /// Files that don't parse as our own fragmented shape fall back to plain
    /// file serving, the pre-existing behavior.
    /// </summary>
    private static IResult ServeMp4(string path)
    {
        try
        {
            return Results.Stream(VirtualMp4.Open(path), "video/mp4", enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"Recordings: no virtual index for {Path.GetFileName(path)} ({Log.Flatten(ex)}); serving raw");
            // Still through the vault: an encrypted file must decrypt on this
            // fallback path too (a plaintext file comes back as a raw FileStream).
            return Results.Stream(FootageVault.OpenRead(path), "video/mp4", enableRangeProcessing: true);
        }
    }
    private sealed record PasswordRequest(string? Password);
    private sealed record AdminUiSettings(double? TrickleSpeed, string? StateDir, bool? ResetAdminPassword,
        bool? Talk = null, bool? ShowBackgroundTasks = null);
    private sealed record AdminRecordingSettings(string? Path, int? RetentionDays, int? PreSeconds,
        int? PostSeconds, int? MaxClipSeconds, string? Stream, int? SegmentMinutes, int? ContinuousRetentionDays,
        bool? Encrypt = null);
    /// <summary>Only the cadence is UI-editable; broker/credentials stay file-only.</summary>
    private sealed record AdminMqttSettings(int? StatsInterval);
    private sealed record AdminConfigRequest(string? Bind, int? BindPort, int? WebPort, string? WebBind,
        bool? WebUi, AdminUiSettings? Ui, AdminRecordingSettings? Recording, bool? RemoveRecording,
        AdminMqttSettings? Mqtt = null);

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
            // The UI's camera-list fetches run server-side (Blazor Server circuits).
            // 15s, not snappier: first-open capability discovery probes the camera
            // for optional features and silent probes legitimately take seconds —
            // a shorter client timeout aborts the request mid-probe.
            builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            // Circuits must talk to THIS server via loopback, never back out through
            // a reverse proxy's public URL (TLS/hairpin failures behind HAProxy etc.).
            builder.Services.AddSingleton(new Neolink.WebClient.LocalApiInfo(LoopbackBase(bindAddr, port)));
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
        // the login screen can render. Exception: snapshot URLs may instead carry
        // RTSP Basic credentials — they are the still-image twin of the rtsp://
        // stream URLs, consumed by the same kind of client (HA generic camera,
        // scripts) that already holds those credentials; the endpoint validates
        // them itself (per-camera permissions included).
        app.Use(async (ctx, next) =>
        {
            if (userStore.Enabled
                && ctx.Request.Path.StartsWithSegments("/api")
                && !ctx.Request.Path.StartsWithSegments("/api/auth"))
            {
                var user = SessionUser(ctx);
                if (user == null)
                {
                    if (IsSnapshotPath(ctx.Request.Path)
                        && ctx.Request.Headers.Authorization.ToString()
                            .StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    {
                        await next(); // the snapshot handler checks the Basic credentials
                        return;
                    }
                    if (IsSnapshotPath(ctx.Request.Path))
                        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"neolink\"";
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
                // Which encryption key the RUNNING server uses (source + one-way
                // fingerprint, never the key), and whether the key file sits on
                // the same disk as the footage it protects — the admin must be
                // able to see both without shell access.
                object? enc = null;
                if (o.Secrets is { } sec)
                {
                    bool onFootageDisk = sec.KeyFile is { } kf
                        && o.Storage != null && o.Storage.SharesVolumeWith(kf, out _);
                    enc = new
                    {
                        enabled = Recording.FootageVault.EncryptingNew,
                        source = sec.KeySource,
                        fingerprint = sec.Fingerprint,
                        file = sec.KeyFile,
                        onFootageDisk,
                    };
                }
                return Results.Json(ConfigEditor.Describe(o.ConfigPath, enc));
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
                        if (u.Talk != null) ConfigEditor.Set(ui, "talk", u.Talk);
                        if (u.ShowBackgroundTasks != null) ConfigEditor.Set(ui, "show_background_tasks", u.ShowBackgroundTasks);
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
                        if (r.Encrypt != null) ConfigEditor.Set(rec, "encrypt", r.Encrypt);
                    }

                    if (req.Mqtt is { StatsInterval: { } statsInterval })
                    {
                        // Never conjure a broker-less mqtt section; the knob only
                        // exists once MQTT is configured in the file.
                        if (ConfigEditor.TryGetSection(root, "mqtt") is { } mq)
                            ConfigEditor.Set(mq, "stats_interval", statsInterval);
                        else
                            throw new FormatException(
                                "mqtt is not configured — add the mqtt section (broker etc.) to config.json first");
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

        // -------------------------------------------------- admin: camera editing
        // Add/edit/delete cameras in config.json from the UI. Passwords are
        // write-only (never returned; blank keeps the stored one), every candidate
        // file is validated through the normal loader before it replaces the
        // config, and changes apply on the next restart.

        app.MapGet("/api/admin/cameras", (HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;
            try
            {
                var cfg = NeolinkConfig.Load(o.ConfigPath);
                return Results.Json(new
                {
                    writable = ConfigEditor.IsWritable(o.ConfigPath),
                    cameras = cfg.Cameras.Select(c => new
                    {
                        name = c.Name,
                        type = c.IsGenericRtsp ? "rtsp" : "reolink",
                        address = c.IsGenericRtsp ? null
                            : c.Port == 9000 ? c.Host : $"{c.Host}:{c.Port}",
                        username = c.IsGenericRtsp ? null : c.Username,
                        hasPassword = !string.IsNullOrEmpty(c.Password),
                        channelId = (int)c.ChannelId,
                        httpAddress = c.HttpAddress,
                        rtspMain = ConfigEditor.MaskRtspPassword(c.RtspMain),
                        rtspSub = ConfigEditor.MaskRtspPassword(c.RtspSub),
                    }).ToList(),
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/admin/cameras", (AdminCameraRequest req, HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;

            var name = (req.Name ?? "").Trim();
            if (name.Length is 0 or > 64)
                return Results.Json(new { error = "name is required (max 64 characters)" }, statusCode: 400);
            // The name becomes RTSP mount paths and recording directories.
            if (name.Any(ch => char.IsControl(ch) || "/\\:*?\"<>|".Contains(ch)))
                return Results.Json(new { error = "name must not contain / \\ : * ? \" < > |" }, statusCode: 400);
            bool isRtsp = req.Type == "rtsp";
            if (!isRtsp && req.Type != "reolink")
                return Results.Json(new { error = "type must be reolink or rtsp" }, statusCode: 400);
            if (!isRtsp)
            {
                if (req.Address is not { } addr || ConfigEditor.HostPortError(addr) is { } addrErr)
                    return Results.Json(new { error = ConfigEditor.HostPortError(req.Address ?? "") }, statusCode: 400);
                if (string.IsNullOrWhiteSpace(req.Username))
                    return Results.Json(new { error = "username is required for a Reolink camera" }, statusCode: 400);
                if (req.ChannelId is { } cid && cid is < 0 or > 255)
                    return Results.Json(new { error = "channel id must be 0-255" }, statusCode: 400);
                if (req.HttpAddress is { Length: > 0 } ha && ha.Any(char.IsWhiteSpace))
                    return Results.Json(new { error = "HTTP address must not contain spaces" }, statusCode: 400);
            }
            else
            {
                foreach (var url in new[] { req.RtspMain, req.RtspSub })
                {
                    if (url is { Length: > 0 } && !url.Contains("****")
                        && (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                            || !Uri.TryCreate(url, UriKind.Absolute, out _)))
                        return Results.Json(new { error = $"\"{url}\" is not a valid rtsp:// URL" }, statusCode: 400);
                }
                if (req.OriginalName == null
                    && string.IsNullOrWhiteSpace(req.RtspMain) && string.IsNullOrWhiteSpace(req.RtspSub))
                    return Results.Json(new { error = "provide at least one rtsp:// URL (main and/or sub)" }, statusCode: 400);
            }

            try
            {
                ConfigEditor.Apply(o.ConfigPath, root =>
                {
                    var cams = ConfigEditor.Cameras(root);
                    var existing = req.OriginalName == null ? null
                        : ConfigEditor.FindCamera(cams, req.OriginalName)
                          ?? throw new FormatException($"unknown camera \"{req.OriginalName}\"");
                    // Friendlier than the loader's duplicate error after the fact.
                    if (ConfigEditor.FindCamera(cams, name) is { } clash && !ReferenceEquals(clash, existing))
                        throw new FormatException($"a camera named \"{name}\" already exists");

                    var cam = existing;
                    if (cam == null)
                    {
                        cam = new JsonObject();
                        cams.Add(cam);
                    }
                    ConfigEditor.Set(cam, "name", name);
                    if (!isRtsp)
                    {
                        ConfigEditor.Set(cam, "address", req.Address!.Trim());
                        ConfigEditor.Set(cam, "username", req.Username!.Trim());
                        // Write-only password: null keeps whatever the file has.
                        if (req.Password != null)
                            ConfigEditor.Set(cam, "password", req.Password.Length == 0 ? null : req.Password);
                        if (req.ChannelId is { } chan)
                            ConfigEditor.Set(cam, "channel_id", chan == 0 ? null : chan);
                        if (req.HttpAddress != null)
                            ConfigEditor.Set(cam, "http_address",
                                req.HttpAddress.Length == 0 ? null : req.HttpAddress.Trim());
                        // A type switch must not leave generic-RTSP keys behind.
                        ConfigEditor.Set(cam, "rtsp_main", null);
                        ConfigEditor.Set(cam, "rtsp_sub", null);
                        ConfigEditor.Set(cam, "rtsp", null);
                    }
                    else
                    {
                        // Masked value = unchanged; null = keep; "" = remove.
                        void SetUrl(string key, string? url)
                        {
                            if (url == null || url.Contains("****")) return;
                            ConfigEditor.Set(cam, key, url.Length == 0 ? null : url.Trim());
                        }
                        SetUrl("rtsp_main", req.RtspMain);
                        SetUrl("rtsp_sub", req.RtspSub);
                        ConfigEditor.Set(cam, "rtsp", null); // retire the legacy spelling
                        ConfigEditor.Set(cam, "address", null);
                        ConfigEditor.Set(cam, "username", null);
                        ConfigEditor.Set(cam, "password", null);
                        ConfigEditor.Set(cam, "channel_id", null);
                        ConfigEditor.Set(cam, "http_address", null);
                    }
                });
                Log.Warn($"config.json cameras updated via the web UI by '{SessionName(ctx)}' " +
                         $"({(req.OriginalName == null ? "added" : "edited")} \"{name}\") — restart to apply");
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

        app.MapDelete("/api/admin/cameras/{name}", (string name, HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;
            try
            {
                ConfigEditor.Apply(o.ConfigPath, root =>
                {
                    var cams = ConfigEditor.Cameras(root);
                    var cam = ConfigEditor.FindCamera(cams, name)
                        ?? throw new FormatException($"unknown camera \"{name}\"");
                    cams.Remove(cam);
                });
                Log.Warn($"config.json camera \"{name}\" deleted via the web UI by '{SessionName(ctx)}' — restart to apply");
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

        // Connectivity test WITHOUT saving. Reolink: full Baichuan connect + login
        // (blank password falls back to the stored one, so an existing camera can
        // be tested without retyping it). Generic: an RTSP OPTIONS round-trip.
        app.MapPost("/api/admin/cameras/test", async (AdminCameraTestRequest req, HttpContext ctx) =>
        {
            if (AdminOnly(ctx) is { } denied) return denied;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(12));

            CameraConfig? stored = null;
            if (req.Name is { Length: > 0 } storedName)
            {
                try
                {
                    stored = NeolinkConfig.Load(o.ConfigPath).Cameras
                        .FirstOrDefault(c => string.Equals(c.Name, storedName, StringComparison.OrdinalIgnoreCase));
                }
                catch { /* config unreadable — test with what was sent */ }
            }

            try
            {
                if (req.Type == "rtsp")
                {
                    var url = req.RtspMain is { Length: > 0 } m && !m.Contains("****") ? m
                        : req.RtspSub is { Length: > 0 } s && !s.Contains("****") ? s
                        : stored?.RtspMain ?? stored?.RtspSub;
                    if (url == null || !url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                        || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        return Results.Json(new { ok = false, message = "provide a valid rtsp:// URL to test" });
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554, cts.Token);
                    var stream = tcp.GetStream();
                    var probe = System.Text.Encoding.ASCII.GetBytes($"OPTIONS {url} RTSP/1.0\r\nCSeq: 1\r\n\r\n");
                    await stream.WriteAsync(probe, cts.Token);
                    var buf = new byte[256];
                    int n = await stream.ReadAsync(buf, cts.Token);
                    var reply = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    return reply.StartsWith("RTSP/", StringComparison.Ordinal)
                        ? Results.Json(new { ok = true, message = "RTSP endpoint answered (credentials are verified when streaming starts)" })
                        : Results.Json(new { ok = false, message = "the host answered, but not with RTSP — check the URL and port" });
                }

                var address = req.Address is { Length: > 0 } a ? a
                    : stored != null && !stored.IsGenericRtsp
                        ? (stored.Port == 9000 ? stored.Host : $"{stored.Host}:{stored.Port}")
                        : null;
                if (address == null || ConfigEditor.HostPortError(address) is { } addrErr2)
                    return Results.Json(new { ok = false, message = ConfigEditor.HostPortError(address ?? "") });
                var username = req.Username is { Length: > 0 } u ? u : stored?.Username;
                if (string.IsNullOrWhiteSpace(username))
                    return Results.Json(new { ok = false, message = "username is required to test" });
                var password = req.Password is { Length: > 0 } p ? p : stored?.Password;

                int colon = address.LastIndexOf(':');
                var (host, port) = colon > address.LastIndexOf(']')
                    ? (address[..colon].Trim('[', ']'), int.Parse(address[(colon + 1)..]))
                    : (address.Trim('[', ']'), 9000);
                byte channel = (byte)Math.Clamp(req.ChannelId ?? stored?.ChannelId ?? 0, 0, 255);

                await using var camera = await Protocol.BcCamera.ConnectAsync(host, port, channel, cts.Token, tag: "test");
                await camera.LoginAsync(username!, password, cts.Token);
                var di = camera.DeviceInfo;
                return Results.Json(new
                {
                    ok = true,
                    message = "Connected and logged in" +
                        (di is { Width: > 0 } ? $" — the camera reports {di.Width}x{di.Height}" : ""),
                });
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { ok = false, message = "timed out — is the address reachable from this server?" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = Log.Flatten(ex) });
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

        // Email notifications (admin only). The SMTP password is never returned —
        // GET reports only whether one is stored; PUT takes it write-only.
        if (o.Notifier is { } notifier)
        {
            object ShapeNotifications()
            {
                var s = notifier.Store.Snapshot();
                return new
                {
                    enabled = s.Enabled,
                    recipient = s.Recipient,
                    smtpHost = s.SmtpHost,
                    smtpPort = s.SmtpPort,
                    security = s.Security.ToString().ToLowerInvariant(),
                    username = s.Username,
                    hasPassword = notifier.Store.HasPassword,
                    from = s.From,
                    fromName = s.FromName,
                    alertStorage = s.AlertStorage,
                    alertOverload = s.AlertOverload,
                    alertCameraOffline = s.AlertCameraOffline,
                    alertWriteFailure = s.AlertWriteFailure,
                    offlineThresholdMinutes = s.OfflineThresholdMinutes,
                    cameraOfflineOverrides = s.CameraOfflineOverrides,
                    cameras = cameras.Select(c => c.Name).ToList(),
                };
            }

            static Neolink.Notifications.SmtpSecurity ParseSecurity(string? v, Neolink.Notifications.SmtpSecurity fallback) =>
                v?.ToLowerInvariant() switch
                {
                    "starttls" => Neolink.Notifications.SmtpSecurity.StartTls,
                    "ssl" => Neolink.Notifications.SmtpSecurity.Ssl,
                    "none" => Neolink.Notifications.SmtpSecurity.None,
                    _ => fallback,
                };

            Neolink.Notifications.NotificationSettings MergedFrom(NotificationRequest req)
            {
                var cur = notifier.Store.Snapshot();
                return new Neolink.Notifications.NotificationSettings
                {
                    Enabled = req.Enabled ?? cur.Enabled,
                    Recipient = (req.Recipient ?? cur.Recipient).Trim(),
                    SmtpHost = (req.SmtpHost ?? cur.SmtpHost).Trim(),
                    SmtpPort = Math.Clamp(req.SmtpPort ?? cur.SmtpPort, 1, 65535),
                    Security = ParseSecurity(req.Security, cur.Security),
                    Username = req.Username ?? cur.Username,
                    From = (req.From ?? cur.From).Trim(),
                    FromName = req.FromName ?? cur.FromName,
                    AlertStorage = req.AlertStorage ?? cur.AlertStorage,
                    AlertOverload = req.AlertOverload ?? cur.AlertOverload,
                    AlertCameraOffline = req.AlertCameraOffline ?? cur.AlertCameraOffline,
                    AlertWriteFailure = req.AlertWriteFailure ?? cur.AlertWriteFailure,
                    OfflineThresholdMinutes = Math.Clamp(req.OfflineThresholdMinutes ?? cur.OfflineThresholdMinutes, 0, 1440),
                    CameraOfflineOverrides = req.CameraOfflineOverrides != null
                        ? new(req.CameraOfflineOverrides, StringComparer.OrdinalIgnoreCase)
                        : cur.CameraOfflineOverrides,
                };
            }

            app.MapGet("/api/admin/notifications", (HttpContext ctx) =>
                AdminOnly(ctx) ?? Results.Json(ShapeNotifications()));

            app.MapPut("/api/admin/notifications", (NotificationRequest req, HttpContext ctx) =>
            {
                if (AdminOnly(ctx) is { } denied) return denied;
                notifier.Store.Save(MergedFrom(req), req.Password); // password write-only
                return Results.Json(ShapeNotifications());
            });

            app.MapPost("/api/admin/notifications/test", async (NotificationRequest req, HttpContext ctx) =>
            {
                if (AdminOnly(ctx) is { } denied) return denied;
                // Test with the posted (possibly unsaved) settings so the user can
                // verify before saving; password null = use the stored one.
                var error = await notifier.SendTestAsync(MergedFrom(req), req.Password, ctx.RequestAborted);
                return error == null
                    ? Results.Json(new { ok = true })
                    : Results.Json(new { error }, statusCode: 502);
            });
        }

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

        // Per-page variant: each page keeps its own blob (e.g. "timeline"), so
        // pages never read-modify-write — and thus never clobber — each other.
        static bool ValidPageKey(string page) =>
            page.Length is > 0 and <= 32 && page.All(c => char.IsAsciiLetterLower(c) || c == '-');

        app.MapGet("/api/me/settings/{page}", (string page, HttpContext ctx) =>
            !ValidPageKey(page)
                ? Results.Json(new { error = "invalid page key" }, statusCode: 400)
                : SessionName(ctx) is { } me
                    ? Results.Content(userStore.GetPageSettings(me, page), "application/json")
                    : Results.Json(new { error = "authentication disabled" }, statusCode: 404));

        app.MapPut("/api/me/settings/{page}", async (string page, HttpContext ctx) =>
        {
            if (!ValidPageKey(page))
                return Results.Json(new { error = "invalid page key" }, statusCode: 400);
            if (SessionName(ctx) is not { } me)
                return Results.Json(new { error = "authentication disabled" }, statusCode: 404);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                userStore.SetPageSettings(me, page, json);
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
            talk = o.TalkEnabled, // beta, opt-in via ui.talk
            showBackgroundTasks = o.ShowBackgroundTasks,

            version = o.Version,
            latestVersion = o.Updates?.Latest,
            repoUrl = UpdateChecker.RepoUrl,
            // Footage encryption active on this server — the UI swaps the brand
            // dot for a padlock so anyone signed in can see recordings are
            // protected at rest.
            encrypted = Recording.FootageVault.EncryptingNew,
            // Worst storage tier state, for the live view's banner: "warn" when
            // any tier is >= 90% used, "full" when one is out of space (recording
            // to it has halted). Rides this endpoint so the wall needs no extra poll.
            storage = ShapeStorage(o.Storage),
        }));

        // Background jobs the admin should know about (footage archiving, ...),
        // with progress — feeds the web UI's background-process strip. Admin only
        // once accounts exist; open on a no-auth server, like the other admin
        // surfaces there.
        app.MapGet("/api/background", (HttpContext ctx) =>
            userStore.Enabled && !IsAdmin(ctx)
                ? Results.Json(new { error = "admin only" }, statusCode: 403)
                : Results.Json(BackgroundTasks.Active().Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    detail = t.Detail,
                    percent = t.Percent is double p ? Math.Round(p, 1) : (double?)null,
                    startedUtc = t.StartedUtc,
                })));

        // Every configured storage location and its capacity (monitor page).
        app.MapGet("/api/storage", () =>
            o.Storage == null
                ? Results.Json(new { error = "recording is not configured" }, statusCode: 404)
                : Results.Json(o.Storage.Sample().Select(s =>
                {
                    // "When does it fill?" from the persisted free-space trend:
                    // measuring (no verdict yet) / steady (retention keeping up) /
                    // filling with the projected days remaining.
                    var (state, days) = o.Forecast?.Forecast(s.Path) ?? ("measuring", null);
                    return new
                    {
                        role = s.Role.ToString().ToLowerInvariant(),
                        label = s.Label,
                        path = s.Path,
                        totalBytes = s.TotalBytes,
                        freeBytes = s.FreeBytes,
                        usedPercent = Math.Round(s.UsedPercent, 1),
                        online = s.Online,
                        warn = s.Warn,
                        full = s.Full,
                        forecastState = state,
                        forecastDays = days is { } d ? Math.Round(d, 1) : (double?)null,
                    };
                })));

        app.MapGet("/api/cameras", () =>
        {
            var payload = cameras.Select(c => new
            {
                name = c.Name,
                online = c.Control.Online,
                // The configured host (for the settings identity strip) — already
                // known to anyone who can reach this API; not sensitive on a LAN.
                address = c.Address,
                // Baichuan-over-UDP transport (beta) — the UI badges these cameras.
                udp = c.Udp,
                // 24/7 footage being written right now (drives the UI's REC badge)
                recording = c.ContinuousActive?.Invoke() ?? false,
                // Battery cameras: intentionally disconnected (dozing) vs offline,
                // plus the latest battery reading when the camera reports one.
                asleep = c.Asleep?.Invoke() ?? false,
                // Camera dark on purpose (privacy mode) — set from this UI OR the
                // Reolink app; the pushes keep it current either way.
                privacy = c.PrivacyOn?.Invoke() ?? false,
                // Suspended in Neolink (beta): no connection held, so it can't be
                // viewed or recorded here. Distinct from offline-because-unreachable.
                suspended = c.Suspended?.Invoke() ?? false,
                canSuspend = c.SetSuspended != null,
                battery = c.Battery?.Invoke() is { } b
                    ? new { percent = b.Percent, charging = b.Charging }
                    : null,
                // On-demand clip capture (record button / HA switch). Null when the
                // camera has no event recorder or its events switch is off — the
                // UI hides the button entirely in that case.
                onDemand = c.EventRecorder is { } rec && rec.OnDemandAvailable
                    ? new
                    {
                        active = rec.OnDemand != null,
                        remainingSeconds = rec.OnDemand?.RemainingSeconds ?? 0,
                        maxSeconds = rec.OnDemandMaxSeconds,
                    }
                    : null,
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
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // The client gave up (panel closed, page navigated, HTTP timeout) —
                // nobody is listening for this response. Swallowing it here keeps
                // an aborted request from surfacing as an unhandled exception.
                return Results.StatusCode(499); // nginx's "client closed request"
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
                // Also logged: HTTP-API rejections (wrong payload shape, unsupported
                // command) must be diagnosable from the server log, not only from
                // the panel's transient error banner.
                Log.Warn($"{name}: camera HTTP API rejected the request — {ex.Message}");
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

        // Once a web-UI/API change has actually applied to a camera setting, nudge the
        // Home Assistant bridge to re-publish that camera's state right away
        // (fire-and-forget) — otherwise HA only catches up on its ~20s periodic
        // refresh, and an automation could act on a stale switch in the meantime.
        // No-op when MQTT isn't configured; a failed publish heals on the next refresh.
        void NudgeHa(string cameraName) => _ = o.OnCameraChanged?.Invoke(cameraName);

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
                        // Gated on the server-wide beta switch: with ui.talk off,
                        // the UI never shows a mic button even on capable cameras.
                        talk = o.TalkEnabled && caps.Features.Talk,
                        zoom = caps.Features.Zoom,
                        siren = caps.Features.Siren,
                        floodlight = caps.Features.Floodlight,
                        whiteLed = caps.Features.WhiteLed,
                        spotlight = caps.Features.Spotlight,
                        doorbell = caps.Features.Doorbell,
                        privacy = caps.Features.Privacy,
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

        // The CURRENT encode selection per stream (what the Reolink app shows) —
        // lets the UI preselect the real fps/bitrate, not the table defaults.
        app.MapGet("/api/cameras/{name}/settings/stream", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var enc = await control.GetStreamSettingsAsync(reqCt);
                return enc == null
                    ? Results.Json(new { error = "reading stream settings requires the camera's http_address" }, statusCode: 404)
                    : Results.Json(enc.Select(s => new
                    {
                        stream = s.Stream,
                        width = s.Width,
                        height = s.Height,
                        framerate = s.Framerate,
                        bitrate = s.Bitrate,
                    }));
            }));

        app.MapGet("/api/cameras/{name}/battery", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var battery = await control.GetBatteryInfoAsync(reqCt);
                return battery == null
                    ? Results.Json(new { error = "no battery info" }, statusCode: 404)
                    : Results.Json(XmlToJson(battery));
            }));

        // A current still image, straight from the camera's own JPEG snapshot
        // command — the NVR primitive notification thumbnails and dashboards poll.
        // Served from a short per-camera cache (default 5 s, ?maxAge= overrides)
        // with a single-flight gate, so a poll storm reaches the camera once; a
        // sleeping battery camera is NEVER woken for a poll (control commands
        // require the live connection), it serves the last frame marked stale.
        var snapCache = new System.Collections.Concurrent.ConcurrentDictionary<
            string, (byte[] Jpeg, DateTime AtUtc)>(StringComparer.OrdinalIgnoreCase);
        var snapGates = new System.Collections.Concurrent.ConcurrentDictionary<
            string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        // Snapshot auth: a web-UI session qualifies (the middleware validated it),
        // and so do the RTSP user credentials over HTTP Basic — the snapshot is
        // the still-image twin of the rtsp:// stream URLs: same users, same
        // per-camera permissions, so a client that already plays
        // rtsp://user:pass@host/cam can fetch http://user:pass@host/api/cameras/cam/snapshot.jpg.
        IResult? SnapshotAuth(HttpContext ctx, WebCameraInfo cam)
        {
            if (ctx.Items.ContainsKey("authUser"))
                return null;
            var creds = NetUtil.DecodeBasicAuth(ctx.Request.Headers.Authorization);
            if (creds != null
                && users.TryGetValue(creds.Value.User, out var expected)
                && NetUtil.FixedTimeEquals(expected, creds.Value.Pass)
                && (cam.PermittedUsers == null || cam.PermittedUsers.Contains(creds.Value.User)))
                return null;
            // Nothing configured to authenticate against → open, like the streams.
            if (!userStore.Enabled && (cam.PermittedUsers == null || users.Count == 0))
                return null;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"neolink\"";
            return Results.Json(new { error = "authentication required" }, statusCode: 401);
        }

        async Task<IResult> SnapshotAsync(string name, HttpContext ctx)
        {
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
                return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
            if (SnapshotAuth(ctx, cam) is { } denied)
                return denied;
            double maxAge = 5;
            if (double.TryParse(ctx.Request.Query["maxAge"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var q) && q >= 0)
                maxAge = q;

            IResult? Cached(bool allowStale)
            {
                if (!snapCache.TryGetValue(cam.Name, out var c))
                    return null;
                var age = DateTime.UtcNow - c.AtUtc;
                if (!allowStale && age.TotalSeconds > maxAge)
                    return null;
                ctx.Response.Headers["X-Snapshot-Age"] = ((long)age.TotalSeconds).ToString();
                if (age.TotalSeconds > maxAge)
                    ctx.Response.Headers["X-Snapshot-Stale"] = "true";
                // The server-side cache is the only cache: a browser/HA re-fetch
                // must reach it, not a stored copy with the same URL.
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Bytes(c.Jpeg, "image/jpeg");
            }

            if (Cached(allowStale: false) is { } fresh)
                return fresh;
            var gate = snapGates.GetOrAdd(cam.Name, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ctx.RequestAborted);
            try
            {
                if (Cached(allowStale: false) is { } won)
                    return won; // refreshed by the request we queued behind
                byte[]? jpeg = null;
                string? unavailable = null;
                try
                {
                    jpeg = await cam.Control.SnapshotAsync(ctx.RequestAborted);
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                {
                    return Results.StatusCode(499);
                }
                catch (CameraOfflineException)
                {
                    unavailable = cam.Asleep?.Invoke() == true
                        ? "camera is asleep (battery) — a snapshot poll does not wake it"
                        : "camera offline (reconnecting)";
                }
                catch (TimeoutException) { unavailable = "camera did not reply"; }
                catch (CameraCommandException ex) { unavailable = ex.Message; }
                if (jpeg is { Length: > 100 } && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                {
                    snapCache[cam.Name] = (jpeg, DateTime.UtcNow);
                    ctx.Response.Headers["X-Snapshot-Age"] = "0";
                    ctx.Response.Headers.CacheControl = "no-store";
                    return Results.Bytes(jpeg, "image/jpeg");
                }
                if (jpeg != null)
                    unavailable ??= "camera returned an invalid snapshot";
                if (unavailable == null)
                    return Results.Json(new { error = "this camera does not support snapshots" }, statusCode: 404);
                // An old frame beats no frame for a dashboard tile — serve the last
                // one we have, honestly labelled (X-Snapshot-Age / X-Snapshot-Stale).
                if (Cached(allowStale: true) is { } stale)
                    return stale;
                return Results.Json(new { error = unavailable }, statusCode: 503);
            }
            finally
            {
                gate.Release();
            }
        }
        app.MapGet("/api/cameras/{name}/snapshot.jpg", SnapshotAsync);
        app.MapGet("/api/cameras/{name}/snapshot", SnapshotAsync);

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
                if (req.State == null && req.LightState == null
                    && req.DoorbellLightState == null && req.IrBrightness == null)
                    return Results.Json(new { error = "provide state, lightState, doorbellLightState and/or irBrightness" }, statusCode: 400);
                if ((req.State != null && !allowed.Contains(req.State)) ||
                    (req.LightState != null && !allowed.Contains(req.LightState)) ||
                    (req.DoorbellLightState != null && !allowed.Contains(req.DoorbellLightState)))
                    return Results.Json(new { error = "values must be open, close or auto" }, statusCode: 400);
                if (req.IrBrightness is { } irb && irb is < 0 or > 100)
                    return Results.Json(new { error = "irBrightness must be 0-100" }, statusCode: 400);
                await control.SetLedStateAsync(req.State, req.LightState,
                    req.DoorbellLightState, req.IrBrightness, reqCt);
                NudgeHa(name);
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
                NudgeHa(name);
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

        // Optical zoom & focus (zoom-lens cameras): absolute positions with ranges.
        app.MapGet("/api/cameras/{name}/zoomfocus", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var zf = await control.GetZoomFocusAsync(reqCt);
                if (zf == null)
                    return Results.Json(new { error = "no zoom/focus on this camera" }, statusCode: 404);
                static object? Pos(XElement? el) => el == null ? null : new
                {
                    cur = (long?)el.Element("curPos") ?? 0,
                    min = (long?)el.Element("minPos") ?? 0,
                    max = (long?)el.Element("maxPos") ?? 0,
                };
                return Results.Json(new { zoom = Pos(zf.Element("zoom")), focus = Pos(zf.Element("focus")) });
            }));

        app.MapPost("/api/cameras/{name}/zoomfocus", (string name, ZoomFocusRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Zoom == null && req.Focus == null)
                    return Results.Json(new { error = "provide zoom and/or focus (absolute position)" }, statusCode: 400);
                if (req.Zoom is { } z) await control.SetZoomFocusAsync("zoomPos", z, reqCt);
                if (req.Focus is { } f) await control.SetZoomFocusAsync("focusPos", f, reqCt);
                return Results.Json(new { ok = true });
            }));

        // Manual siren. POST {on:true} latches it until {on:false}; a body without
        // "on" plays one burst. GET answers the last state the camera pushed.
        app.MapGet("/api/cameras/{name}/siren", (string name, HttpContext ctx) =>
        {
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
                return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
            if (CheckAuth(ctx, cam) is { } denied)
                return denied;
            return Results.Json(new { on = cam.SirenOn?.Invoke() });
        });

        app.MapPost("/api/cameras/{name}/siren", (string name, SirenRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                await control.SirenAsync(req.On, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, on = req.On });
            }));

        // Privacy mode: the camera goes dark (no video, no detections) until
        // switched back. Prefer the pushed state (what the tile shows): while the
        // camera is dark it often stops answering the live sleep-state read, which
        // would make the settings panel wrongly show "off". Fall back to a live read
        // only when nothing has been pushed yet.
        app.MapGet("/api/cameras/{name}/privacy", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var pushed = cameras
                    .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?.PrivacyOn?.Invoke();
                var on = pushed ?? await control.GetPrivacyModeAsync(reqCt);
                return on == null
                    ? Results.Json(new { error = "privacy mode is not supported by this camera" }, statusCode: 404)
                    : Results.Json(new { on });
            }));

        app.MapPost("/api/cameras/{name}/privacy", (string name, PrivacyRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.On == null)
                    return Results.Json(new { error = "provide on: true|false" }, statusCode: 400);
                await control.SetPrivacyModeAsync(req.On.Value, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, on = req.On });
            }));

        // Suspend (beta): a Neolink-side "off" switch. Suspending drops Neolink's
        // connection and holds it closed, so the camera can't be VIEWED or RECORDED
        // here — without editing the config or restarting. The camera itself is
        // untouched: its own SD-card/cloud recording and any other system pulling
        // its stream directly keep working. Persisted across restarts. Reads are
        // open; writing is admin-only once accounts exist (it's a server-side state
        // change that stops recording, like the recording settings).
        app.MapGet("/api/cameras/{name}/suspend", (string name, HttpContext ctx) =>
        {
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
                return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
            if (CheckAuth(ctx, cam) is { } denied)
                return denied;
            return cam.Suspended == null
                ? Results.Json(new { error = "suspend is not available for this camera" }, statusCode: 404)
                : Results.Json(new { suspended = cam.Suspended() });
        });

        app.MapPost("/api/cameras/{name}/suspend", (string name, SuspendRequest req, HttpContext ctx) =>
        {
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
                return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
            if (CheckAuth(ctx, cam) is { } denied)
                return denied;
            if (userStore.Enabled && !IsAdmin(ctx))
                return Results.Json(new { error = "admin only — suspending stops recording, a server setting" }, statusCode: 403);
            if (cam.SetSuspended == null)
                return Results.Json(new { error = "suspend is not available for this camera" }, statusCode: 404);
            if (req.Suspended is not { } s)
                return Results.Json(new { error = "provide suspended: true|false" }, statusCode: 400);
            cam.SetSuspended(s);
            NudgeHa(name);
            return Results.Json(new { ok = true, suspended = s });
        });

        // Floodlight behavior (cameras with a spotlight): brightness and the
        // "turn on with motion at night" switch. Read-modify-write of the
        // camera's own FloodlightTask XML — unknown fields ride along verbatim.
        app.MapGet("/api/cameras/{name}/floodlight", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var task = await control.GetFloodlightTasksAsync(reqCt);
                return task == null
                    ? Results.Json(new { error = "no floodlight tasks on this camera" }, statusCode: 404)
                    : Results.Json(ShapeFloodlight(task));
            }));

        app.MapPost("/api/cameras/{name}/floodlight", (string name, FloodlightRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Brightness == null && req.Auto == null)
                    return Results.Json(new { error = "provide brightness (percent) and/or auto (bool)" }, statusCode: 400);
                var task = await control.GetFloodlightTasksAsync(reqCt);
                if (task == null)
                    return Results.Json(new { error = "no floodlight tasks on this camera" }, statusCode: 404);
                if (req.Brightness is { } b)
                {
                    long min = (long?)task.Element("brightness_min") ?? 1;
                    long max = (long?)task.Element("brightness_max") ?? 100;
                    task.SetElementValue("brightness_cur", Math.Clamp(b, min, max));
                }
                if (req.Auto is { } auto)
                    task.SetElementValue("enable", auto ? 1 : 0);
                await control.SetFloodlightTasksAsync(task, reqCt);
                NudgeHa(name);
                return Results.Json(ShapeFloodlight(task));
            }));

        // White LED / spotlight (cameras that expose it over the HTTP API, e.g. the
        // Lumus / Elite lines that answer no Baichuan FloodlightTask): brightness,
        // on/off, and the auto mode. Read-modify-write preserves the schedule.
        app.MapGet("/api/cameras/{name}/whiteled", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var wl = await control.GetWhiteLedAsync(reqCt);
                return wl == null
                    ? Results.Json(new { error = "no white LED on this camera (or its HTTP API is unreachable)" }, statusCode: 404)
                    : Results.Json(new { bright = wl.Bright, on = wl.On, mode = wl.Mode });
            }));

        app.MapPost("/api/cameras/{name}/whiteled", (string name, WhiteLedRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Brightness == null && req.On == null && req.Mode == null)
                    return Results.Json(new { error = "provide brightness (0-100), on (bool) and/or mode (int)" }, statusCode: 400);
                await control.SetWhiteLedAsync(req.Brightness, req.On, req.Mode, reqCt);
                NudgeHa(name);
                var wl = await control.GetWhiteLedAsync(reqCt);
                return wl == null
                    ? Results.Json(new { ok = true })
                    : Results.Json(new { bright = wl.Bright, on = wl.On, mode = wl.Mode });
            }));

        // ------------------------------------------------ HTTP-API extras (beta)

        // One combined read: picture settings, speaker volume, Wi-Fi signal, PTZ
        // presets, quick replies, auto-tracking and SD cards. 404 = the camera has
        // no HTTP API; features the camera lacks come back null.
        app.MapGet("/api/cameras/{name}/httpfeatures", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var f = await control.GetHttpFeaturesAsync(reqCt);
                if (f == null)
                    return Results.Json(new { error = "this camera has no HTTP API" }, statusCode: 404);
                return Results.Json(new
                {
                    image = f.Image == null ? null : new
                    {
                        bright = f.Image.Bright,
                        contrast = f.Image.Contrast,
                        saturation = f.Image.Saturation,
                        hue = f.Image.Hue,
                        sharpen = f.Image.Sharpen,
                        dayNight = f.Image.DayNight,
                        antiFlicker = f.Image.AntiFlicker,
                        flip = f.Image.Flip,
                        mirror = f.Image.Mirror,
                        hdr = f.Image.Hdr,
                        hdrMax = f.Image.HdrMax,
                    },
                    volume = f.Volume,
                    wifiSignal = f.WifiSignal,
                    ptzPresets = f.PtzPresets?.Select(p => new { id = p.Id, name = p.Name, enabled = p.Enabled }),
                    quickReplies = f.QuickReplies?.Select(q => new { id = q.Id, name = q.Name }),
                    autoTrack = f.AutoTrack,
                    sdCards = f.SdCards?.Select(s => new
                    {
                        id = s.Id,
                        totalMb = s.TotalMb,
                        freeMb = s.FreeMb,
                        formatted = s.Formatted,
                        mounted = s.Mounted,
                    }),
                    mdSensitivity = f.MdSensitivity,
                    aiSensitivities = f.AiSensitivities?.Select(a => new
                    {
                        type = a.Type,
                        sensitivity = a.Sensitivity,
                        stayTime = a.StayTime,
                    }),
                    osd = f.Osd == null ? null : new
                    {
                        showName = f.Osd.ShowName,
                        name = f.Osd.Name,
                        namePos = f.Osd.NamePos,
                        showTime = f.Osd.ShowTime,
                        timePos = f.Osd.TimePos,
                        watermark = f.Osd.Watermark,
                        posOptions = f.Osd.PosOptions,
                    },
                });
            }));

        // Picture adjustments (0-255 sliders) + ISP config (day/night, anti-flicker,
        // flip/mirror). Read-modify-write on the camera's own JSON.
        app.MapPost("/api/cameras/{name}/image", (string name, ImageRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Bright == null && req.Contrast == null && req.Saturation == null
                    && req.Hue == null && req.Sharpen == null && req.DayNight == null
                    && req.AntiFlicker == null && req.Flip == null && req.Mirror == null)
                    return Results.Json(new { error = "provide at least one picture setting" }, statusCode: 400);
                string[] dayNights = { "Auto", "Color", "Black&White" };
                if (req.DayNight != null && !dayNights.Contains(req.DayNight))
                    return Results.Json(new { error = "dayNight must be Auto, Color or Black&White" }, statusCode: 400);
                if (req.AntiFlicker != null && !Streaming.ImageSettings.AntiFlickerValues.Contains(req.AntiFlicker))
                    return Results.Json(new { error = "antiFlicker must be Off, Outdoor, 50HZ or 60HZ" }, statusCode: 400);
                await control.SetImageSettingsAsync(req.Bright, req.Contrast, req.Saturation,
                    req.Hue, req.Sharpen, req.DayNight, req.AntiFlicker, req.Flip, req.Mirror, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true });
            }));

        // Speaker volume (0-100) — also what two-way talk comes out at.
        app.MapPost("/api/cameras/{name}/volume", (string name, VolumeRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Volume is not { } vol || vol is < 0 or > 100)
                    return Results.Json(new { error = "provide volume: 0-100" }, statusCode: 400);
                await control.SetVolumeAsync(vol, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, volume = vol });
            }));

        // PTZ presets: {id} recalls a saved position; {id, name, save:true} saves
        // the camera's CURRENT position into that slot.
        app.MapPost("/api/cameras/{name}/ptzpreset", (string name, PtzPresetRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Id is not { } id || id is < 0 or > 63)
                    return Results.Json(new { error = "provide id: 0-63" }, statusCode: 400);
                if (req.Save == true)
                {
                    var presetName = (req.Name ?? "").Trim();
                    if (presetName.Length is 0 or > 31)
                        return Results.Json(new { error = "provide name: 1-31 characters" }, statusCode: 400);
                    await control.SavePtzPresetAsync(id, presetName, reqCt);
                }
                else
                {
                    await control.PtzToPresetAsync(id, reqCt);
                }
                return Results.Json(new { ok = true });
            }));

        // Doorbell quick reply: plays a pre-recorded message through the speaker.
        app.MapPost("/api/cameras/{name}/quickreply", (string name, QuickReplyRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Id is not { } id || id < 0)
                    return Results.Json(new { error = "provide id (from httpfeatures.quickReplies)" }, statusCode: 400);
                await control.PlayQuickReplyAsync(id, reqCt);
                return Results.Json(new { ok = true });
            }));

        // Doorbell auto-reply: the default message played by itself when a ring
        // goes unanswered. fileId -1 turns it off; timeout is the wait in seconds.
        app.MapPost("/api/cameras/{name}/autoreply", (string name, AutoReplyRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.FileId == null && req.Timeout == null)
                    return Results.Json(new { error = "provide fileId (-1 = off) and/or timeout (seconds)" }, statusCode: 400);
                if (req.FileId is { } fid && fid < -1)
                    return Results.Json(new { error = "fileId must be -1 (off) or a quick-reply id" }, statusCode: 400);
                if (req.Timeout is { } t && t is < 1 or > 60)
                    return Results.Json(new { error = "timeout must be 1-60 seconds" }, statusCode: 400);
                await control.SetAutoReplyAsync(req.FileId, req.Timeout, reqCt);
                return Results.Json(new { ok = true });
            }));

        // AI auto-tracking (PTZ cameras that follow detected subjects).
        app.MapPost("/api/cameras/{name}/autotrack", (string name, AutoTrackRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.On == null)
                    return Results.Json(new { error = "provide on: true|false" }, statusCode: 400);
                await control.SetAutoTrackAsync(req.On.Value, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, on = req.On });
            }));

        // Motion-detection sensitivity, normalized to 1-50 (higher = more
        // sensitive) across the two firmware dialects.
        app.MapPost("/api/cameras/{name}/mdsensitivity", (string name, MdSensitivityRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Sensitivity is not { } sens || sens is < 1 or > 50)
                    return Results.Json(new { error = "provide sensitivity: 1-50" }, statusCode: 400);
                await control.SetMdSensitivityAsync(sens, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, sensitivity = sens });
            }));

        // Per-type AI detection sensitivity (0-100). Valid types are whatever
        // httpfeatures.aiSensitivities listed for this camera.
        app.MapPost("/api/cameras/{name}/aisensitivity", (string name, AiSensitivityRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Type == null || !CameraControl.AiAlarmTypes.Contains(req.Type))
                    return Results.Json(new { error = $"provide type: {string.Join(", ", CameraControl.AiAlarmTypes)}" },
                        statusCode: 400);
                if (req.Sensitivity is not { } sens || sens is < 0 or > 100)
                    return Results.Json(new { error = "provide sensitivity: 0-100" }, statusCode: 400);
                await control.SetAiSensitivityAsync(req.Type, sens, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, type = req.Type, sensitivity = sens });
            }));

        // ISP HDR: 0 = off; the top value comes from httpfeatures.image.hdrMax
        // (1 = plain on/off, 2 = off/low/high).
        app.MapPost("/api/cameras/{name}/hdr", (string name, HdrRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.Value is not { } value || value is < 0 or > 2)
                    return Results.Json(new { error = "provide value: 0-2" }, statusCode: 400);
                await control.SetHdrAsync(value, reqCt);
                NudgeHa(name);
                return Results.Json(new { ok = true, value });
            }));

        // On-screen display: camera-name / timestamp overlay visibility + position
        // and the Reolink watermark. Positions from httpfeatures.osd.posOptions.
        app.MapPost("/api/cameras/{name}/osd", (string name, OsdRequest req, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: true, async (control, reqCt) =>
            {
                if (req.ShowName == null && req.NamePos == null && req.ShowTime == null
                    && req.TimePos == null && req.Watermark == null)
                    return Results.Json(new { error = "provide at least one OSD setting" }, statusCode: 400);
                foreach (var pos in new[] { req.NamePos, req.TimePos })
                    if (pos != null && (pos.Length is 0 or > 32 || pos.Any(char.IsControl)))
                        return Results.Json(new { error = "positions must be one of osd.posOptions" }, statusCode: 400);
                await control.SetOsdSettingsAsync(req.ShowName, req.NamePos, req.ShowTime, req.TimePos,
                    req.Watermark, reqCt);
                return Results.Json(new { ok = true });
            }));

        // Firmware-update check (read-only — nothing is ever installed from here).
        // The camera itself asks Reolink's servers; the verdict is cached hours.
        app.MapGet("/api/cameras/{name}/firmware", (string name, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var status = await control.CheckFirmwareAsync(reqCt);
                if (status == null)
                    return Results.Json(new { error = "this camera cannot check for firmware updates" }, statusCode: 404);
                return Results.Json(new { updateAvailable = status.UpdateAvailable, newVersion = status.NewVersion });
            }));

        // ------------------------------------------------- camera SD-card recordings
        // Footage the CAMERA recorded onto its own SD card — including anything from
        // when neolink was down and battery-camera clips that never streamed.

        // Which days of a month have recordings (the calendar of the day picker).
        app.MapGet("/api/cameras/{name}/sdcard/days", (string name, int? year, int? month, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                if (year is not { } y || y is < 2000 or > 2100 || month is not { } m || m is < 1 or > 12)
                    return Results.Json(new { error = "provide year and month" }, statusCode: 400);
                var days = await control.GetSdRecordingDaysAsync(y, m, reqCt);
                if (days == null)
                    return Results.Json(new { error = "this camera's SD card cannot be searched" }, statusCode: 404);
                return Results.Json(new { year = y, month = m, days });
            }));

        // The recordings of one (camera-local) day.
        app.MapGet("/api/cameras/{name}/sdcard/recordings", (string name, string? date, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
                    return Results.Json(new { error = "provide date: yyyy-MM-dd" }, statusCode: 400);
                var files = await control.GetSdRecordingsAsync(day, reqCt);
                if (files == null)
                    return Results.Json(new { error = "this camera's SD card cannot be searched" }, statusCode: 404);
                return Results.Json(new
                {
                    date = day.ToString("yyyy-MM-dd"),
                    recordings = files.Select(f => new
                    {
                        file = f.Name,
                        start = f.Start,
                        end = f.End,
                        sizeBytes = f.SizeBytes,
                        streamType = f.StreamType,
                    }),
                });
            }));

        // Streams one recording straight off the camera (no server-side copy).
        // Inline for the <video> player; ?dl=1 turns it into a download. The
        // camera serves the file sequentially, so there is no seeking/ranges.
        app.MapGet("/api/cameras/{name}/sdcard/download", (string name, string? file, bool? dl, HttpContext ctx) =>
            ExecAsync(name, ctx, mutating: false, async (control, reqCt) =>
            {
                var fileName = (file ?? "").Trim();
                if (fileName.Length is 0 or > 255 || fileName.Any(char.IsControl))
                    return Results.Json(new { error = "provide file: a name from /sdcard/recordings" }, statusCode: 400);
                var download = await control.OpenSdRecordingAsync(fileName, reqCt);
                ctx.Response.RegisterForDispose(download);
                string contentType = fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? "video/mp4" : "application/octet-stream";
                return Results.Stream(download.Stream, contentType,
                    fileDownloadName: dl == true ? Path.GetFileName(fileName) : null);
            }));

        // On-demand clip capture: start records ONE clip capped at
        // recording.max_clip_seconds (it stops by itself), stop ends it early.
        // The same session backs the Home Assistant "Record" switch.
        app.MapPost("/api/cameras/{name}/record", (string name, RecordOnDemandRequest req, HttpContext ctx) =>
        {
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
                return Results.Json(new { error = $"unknown camera '{name}'" }, statusCode: 404);
            if (CheckAuth(ctx, cam) is { } denied)
                return denied;
            if (cam.EventRecorder is not { } rec)
                return Results.Json(new { error = "event recording is not available for this camera" }, statusCode: 409);
            if (req.Active && !rec.OnDemandAvailable)
                return Results.Json(new { error = "event recording is switched off for this camera" }, statusCode: 409);
            if (req.Active) rec.StartOnDemand();
            else rec.StopOnDemand();
            var od = rec.OnDemand; // idempotent: always answer with the session as it now stands
            return Results.Json(new
            {
                active = od != null,
                remainingSeconds = od?.RemainingSeconds ?? 0,
                maxSeconds = rec.OnDemandMaxSeconds,
            });
        });

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

            // Days that hold any footage (events or continuous) — the timeline's
            // calendar highlights these. Literal "days" beats the {id} routes.
            app.MapGet("/api/events/days", () => Results.Json(events.ListContentDays()));

            // Single-event lookup: notification deep links (/events?event={id})
            // resolve the exact event even after it ages out of the 24h list.
            app.MapGet("/api/events/{id}", (string id) =>
            {
                var rec = events.Find(id);
                return rec == null
                    ? Results.Json(new { error = "unknown event" }, statusCode: 404)
                    : Results.Json(Shape(rec));
            });

            app.MapGet("/api/events/{id}/clip", (string id) =>
            {
                var path = events.ArtifactPath(id, "clip.mp4");
                return path == null
                    ? Results.Json(new { error = "no clip for this event" }, statusCode: 404)
                    : ServeMp4(path);
            });

            app.MapGet("/api/events/{id}/thumb", (string id) =>
            {
                var path = events.ArtifactPath(id, "thumb.jpg");
                return path == null
                    ? Results.Json(new { error = "no thumbnail for this event" }, statusCode: 404)
                    : Results.Stream(FootageVault.OpenRead(path), "image/jpeg"); // decrypts when encrypted
            });

            // The clip's low-res sub-stream twin, used by the strip's ambient previews.
            app.MapGet("/api/events/{id}/preview", (string id) =>
            {
                var path = events.ArtifactPath(id, "preview.mp4");
                return path == null
                    ? Results.Json(new { error = "no preview for this event" }, statusCode: 404)
                    : ServeMp4(path);
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

            // Bulk delete: permanently removes the selected events and their files.
            // Destructive, so it is admin-only once accounts exist (like the server
            // settings and user management); with no accounts it needs a valid RTSP
            // user, same as review. ?estimate answers "what would this delete?"
            // (count, total size, per-camera and time span) so the UI can confirm
            // with a real summary before anything is removed.
            app.MapPost("/api/events/delete", (EventDeleteRequest req, HttpContext ctx) =>
            {
                if (userStore.Enabled)
                {
                    if (!IsAdmin(ctx)) return Results.Json(new { error = "admin only" }, statusCode: 403);
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

                var ids = req.Ids?.Distinct().ToList() ?? new List<string>();
                if (ids.Count == 0)
                    return Results.Json(new { error = "no events selected" }, statusCode: 400);
                if (ids.Count > 1000)
                    return Results.Json(new { error = "too many events in one request (max 1000)" }, statusCode: 400);

                // Resolve to real, deletable events (skip unknown and still-recording).
                var found = ids.Select(events.Find).OfType<EventRecord>().ToList();
                var deletable = found.Where(r => !r.Ongoing).ToList();
                int ongoing = found.Count(r => r.Ongoing);
                int unknown = ids.Count - found.Count;

                if (req.Estimate == true)
                {
                    long bytes = deletable.Sum(r => events.EventSize(r.Id));
                    var perCamera = deletable
                        .GroupBy(r => r.Camera, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new { camera = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count).ThenBy(x => x.camera)
                        .ToList();
                    return Results.Json(new
                    {
                        count = deletable.Count,
                        bytes,
                        cameras = perCamera,
                        ongoing,
                        unknown,
                        earliest = deletable.Count > 0 ? deletable.Min(r => r.StartUtc) : (DateTime?)null,
                        latest = deletable.Count > 0 ? deletable.Max(r => r.StartUtc) : (DateTime?)null,
                    });
                }

                long freed = 0;
                int deleted = 0, failed = 0;
                foreach (var rec in deletable)
                {
                    long size = events.EventSize(rec.Id);
                    if (events.DeleteEvent(rec.Id)) { deleted++; freed += size; }
                    else failed++;
                }
                if (deleted > 0)
                    Log.Info($"Events: deleted {deleted} event(s) on request ({FormatBytes(freed)} freed)");
                return Results.Json(new { deleted, freed, failed, ongoing, unknown });
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
                // Always the EFFECTIVE list (never null): unset means the default
                // set, which excludes the opt-in perimeter labels — the UI chips
                // must show those as off until the user ticks them.
                eventTypes = (IEnumerable<string>?)s.EventTypes ?? CameraRecordingSettings.DefaultLabels,
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
                // Capture schedule, always in effective form: the day list is never
                // null (the UI chips render it directly) and "" means midnight.
                // It only takes effect while scheduleEnabled (opt-in).
                scheduleEnabled = s.ScheduleEnabled,
                scheduleDays = (IEnumerable<string>?)s.ScheduleDays ?? CameraRecordingSettings.WeekDays,
                scheduleStart = s.ScheduleStart ?? "",
                scheduleEnd = s.ScheduleEnd ?? "",
                // Archiving: available only when the server config maps an archive
                // tier; per camera AND per type (strict opt-in). Footage moves to
                // the archive when its normal retention above expires.
                archiveAvailable = o.ArchiveAvailable,
                archiveEvents = s.ArchiveEvents,
                archiveContinuous = s.ArchiveContinuous,
                archiveRetentionDays = s.ArchiveRetentionDays,
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
                // These persist SERVER-side (retention, schedules, archive routing,
                // what gets recorded) — once accounts exist, changing them is admin
                // work, like every other server setting.
                if (userStore.Enabled && !IsAdmin(ctx))
                    return Results.Json(new { error = "admin only — recording settings are server settings" }, statusCode: 403);

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
                // Capture schedule: unknown day tokens are dropped; an empty or
                // complete day list stores as null (= every day). Times must be
                // HH:mm — anything else (including "") clears to midnight.
                List<string>? schedDays = null;
                if (req.ScheduleDays != null)
                {
                    schedDays = req.ScheduleDays.Select(d => d.Trim().ToLowerInvariant())
                        .Where(CameraRecordingSettings.WeekDays.Contains).Distinct().ToList();
                    if (schedDays.Count == 0 || schedDays.Count == CameraRecordingSettings.WeekDays.Length)
                        schedDays = null;
                }
                static string? SchedTime(string? v) =>
                    v != null && CameraRecordingSettings.ParseMinutes(v) is int m && m > 0 ? v : null;
                // Archiving is inert without the server-side archive tier — the
                // switches can't be turned on for a destination that doesn't exist.
                var archiveEvents = o.ArchiveAvailable ? req.ArchiveEvents : null;
                var archiveContinuous = o.ArchiveAvailable ? req.ArchiveContinuous : null;
                if ((req.ArchiveEvents == true || req.ArchiveContinuous == true) && !o.ArchiveAvailable)
                    return Results.Json(new
                    {
                        error = "archiving requires \"archive_path\" in the server recording config " +
                                "(ideally a different drive — in Docker, map a second volume)",
                    }, statusCode: 409);
                var updated = recordingSettings.Update(cam.Name, req.Events, continuous,
                    types, setEventTypes: req.EventTypes != null,
                    eventRetentionDays: Retention(req.EventRetentionDays),
                    setEventRetention: req.EventRetentionDays != null,
                    continuousRetentionDays: Retention(req.ContinuousRetentionDays),
                    setContinuousRetention: req.ContinuousRetentionDays != null,
                    recordStream: req.RecordStream is { Length: > 0 } v ? v : null,
                    setRecordStream: req.RecordStream != null,
                    scheduleDays: schedDays, setScheduleDays: req.ScheduleDays != null,
                    scheduleStart: SchedTime(req.ScheduleStart), setScheduleStart: req.ScheduleStart != null,
                    scheduleEnd: SchedTime(req.ScheduleEnd), setScheduleEnd: req.ScheduleEnd != null,
                    scheduleEnabled: req.ScheduleEnabled,
                    archiveEvents: archiveEvents,
                    archiveContinuous: archiveContinuous,
                    archiveRetentionDays: Retention(req.ArchiveRetentionDays),
                    setArchiveRetention: req.ArchiveRetentionDays != null);
                NudgeHa(cam.Name);
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
                var segments = OverlayActiveSegment(
                        events.ListSegments(cam.Name, date), cam.ActiveSegment?.Invoke(), date)
                    .Select(s => new { file = s.File, size = s.Size, seconds = Math.Round(s.Seconds, 1), live = s.Live });
                return Results.Json(segments);
            });

            app.MapGet("/api/recordings/{camera}/{date}/{file}", (string camera, string date, string file) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
                var path = cam == null ? null : events.SegmentPath(cam.Name, date, file);
                return path == null
                    ? Results.Json(new { error = "no such recording" }, statusCode: 404)
                    : ServeMp4(path);
            });

            // Bulk export: every segment overlapping [from, to] of one day (so at
            // most 24 h by construction), streamed as a STORED zip — video doesn't
            // compress, and store-level means no CPU and no temp files. Segments
            // ship as-is (lossless, each named by its start time and playable on
            // its own); the one still being written is skipped — it would be an
            // unfinalized, unplayable file. ?estimate=1 returns {files, bytes} so
            // the UI can show the damage before the user commits to gigabytes.
            app.MapGet("/api/recordings/{camera}/{date}/export", (string camera, string date,
                string? from, string? to, string? estimate, string? format, HttpContext ctx) =>
            {
                var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
                if (cam == null)
                    return Results.Json(new { error = $"unknown camera '{camera}'" }, statusCode: 404);
                if (!TimeSpan.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, out var fromT)
                    || !TimeSpan.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, out var toT)
                    || fromT < TimeSpan.Zero || toT > TimeSpan.FromDays(1) || toT <= fromT)
                    return Results.Json(new { error = "invalid range — need from=HH:mm[:ss] < to=HH:mm[:ss] within the day" }, statusCode: 400);

                var active = cam.ActiveSegment?.Invoke();
                var picked = PickExportSegments(events.ListSegments(cam.Name, date),
                    active is { } a && a.Date == date ? a.File : null,
                    fromT.TotalSeconds, toT.TotalSeconds, out long bytes);
                // The single-MP4 planner needs each file's start-of-day offset to
                // trim the output to the requested range.
                var inputs = picked
                    .Select(f => (Path: events.SegmentPath(cam.Name, date, f), File: f))
                    .Where(p => p.Path != null)
                    .Select(p => (p.Path!,
                        TimeSpan.TryParseExact(Path.GetFileNameWithoutExtension(p.File), @"hh\-mm\-ss", null, out var st)
                            ? st.TotalSeconds : 0))
                    .ToList();

                if (estimate is "1" or "true")
                {
                    // Also answer "can this range become ONE file?" — the planner
                    // reads only each segment's index, so this stays cheap.
                    Mp4Export.Plan? est = null;
                    string? mp4Reason = null;
                    if (inputs.Count > 0)
                    {
                        try { est = Mp4Export.TryPlan(inputs, fromT.TotalSeconds, toT.TotalSeconds, out mp4Reason); }
                        catch (Exception ex) { mp4Reason = ex.Message; }
                    }
                    return Results.Json(new
                    {
                        files = picked.Count,
                        bytes,
                        mp4Bytes = est?.TotalBytes,
                        mp4DurationMs = est?.DurationMs,
                        mp4Reason,
                    });
                }
                if (picked.Count == 0)
                    return Results.Json(new { error = "no footage in this range" }, statusCode: 404);
                // One export at a time: bulk sequential reads compete with the
                // recorders' writes for the same disk; a second stream doubles it.
                if (!ExportGate.Wait(0))
                    return Results.Json(new { error = "another export is already running — try again when it finishes" }, statusCode: 503);

                var baseName = $"{cam.Name} {date} {fromT:hh\\-mm\\-ss}-{toT:hh\\-mm\\-ss}";
                if (format == "mp4")
                {
                    // Single combined MP4: concatenated without re-encoding. The
                    // plan is built before streaming starts, so failures are clean
                    // errors and the response carries an exact Content-Length.
                    Mp4Export.Plan? plan;
                    string? reason;
                    try { plan = Mp4Export.TryPlan(inputs, fromT.TotalSeconds, toT.TotalSeconds, out reason); }
                    catch (Exception ex) { plan = null; reason = ex.Message; }
                    if (plan == null)
                    {
                        ExportGate.Release();
                        return Results.Json(new { error = $"cannot combine into one file: {reason} — export as zip instead" },
                            statusCode: 409);
                    }
                    return Results.Stream(async body =>
                    {
                        try
                        {
                            ctx.Response.ContentLength = plan.TotalBytes; // real download progress
                            await Mp4Export.WriteAsync(plan, body, ctx.RequestAborted).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { /* download cancelled */ }
                        catch (IOException) { /* client went away, or a segment was pruned mid-copy */ }
                        finally { ExportGate.Release(); }
                    }, "video/mp4", $"{baseName}.mp4");
                }

                return Results.Stream(async body =>
                {
                    try
                    {
                        // ZipArchive writes entry headers and the central directory
                        // with SYNCHRONOUS stream writes, which Kestrel rejects by
                        // default (aborting the download mid-stream). Allow them for
                        // this response — the gate above caps it to one busy thread.
                        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } bodyCtl)
                            bodyCtl.AllowSynchronousIO = true;
                        using var zip = new System.IO.Compression.ZipArchive(
                            body, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
                        foreach (var f in picked)
                        {
                            var path = events.SegmentPath(cam.Name, date, f);
                            if (path == null) continue; // archived/pruned mid-export
                            var entry = zip.CreateEntry(f, System.IO.Compression.CompressionLevel.NoCompression);
                            try { entry.LastWriteTime = File.GetLastWriteTime(path); } catch { }
                            await using var es = entry.Open();
                            // Through the vault: an export is a plaintext download by
                            // definition, so encrypted segments decrypt on the way out.
                            await using var fs = FootageVault.OpenRead(path);
                            await fs.CopyToAsync(es, ctx.RequestAborted).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { /* download cancelled */ }
                    catch (IOException) { /* client went away mid-stream */ }
                    finally { ExportGate.Release(); }
                }, "application/zip", $"{baseName}.zip");
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
                // Per-camera availability: run-length transitions are tiny (a
                // healthy camera is one run), so ship the full picture each poll.
                avail = monitor.Availability
                    .Snapshots(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .Select(a => new
                    {
                        cam = a.Camera,
                        on = a.Online,
                        pct = a.UptimePct,
                        obs = a.ObservedMs,
                        outs = a.Outages,
                        longest = a.LongestOutageMs,
                        since = a.CurrentSinceMs,
                        runs = a.Runs,
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

        // Two-way talk: microphone PCM in over a WebSocket, camera speaker out.
        // Auth rides the same session middleware as every other /api route.
        app.Map("/api/talk", async ctx =>
        {
            if (!o.TalkEnabled)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "two-way talk is disabled — enable it in Server settings (ui.talk)",
                });
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("WebSocket endpoint");
                return;
            }
            string? name = ctx.Request.Query["camera"];
            var cam = cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cam == null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                await TalkFromWebSocketAsync(ws, cam.Name, cam.Control, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"{cam.Name}: talk session ended: {Log.Flatten(ex)}");
            }
        });

        if (webUi)
        {
            // The PWA service worker ships as an RCL asset under _content/, but it
            // must control the whole app ("/"). Browsers cap a worker's scope at
            // its script's directory unless the response carries this header.
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.Equals("/_content/Neolink.WebClient/sw.js", StringComparison.OrdinalIgnoreCase))
                    ctx.Response.Headers["Service-Worker-Allowed"] = "/";
                await next();
            });
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

    /// <summary>Serializes footage exports: bulk reads off the recordings disk
    /// compete with the recorders' writes, so only one zip streams at a time.</summary>
    private static readonly SemaphoreSlim ExportGate = new(1, 1);

    /// <summary>Human-readable byte size for log lines (KB/MB/GB).</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    /// <summary>
    /// The segment files a [from, to] seconds-of-day export should contain: every
    /// file whose footage overlaps the range, oldest first, with the total byte
    /// count for the pre-flight estimate. The file still being written is excluded
    /// (it has no moov yet — an unplayable torso); it exports once it closes.
    /// A file with an unknown duration (exotic filesystems) counts as at least a
    /// second long so it is still caught when the range crosses its start.
    /// </summary>
    internal static List<string> PickExportSegments(
        List<(string File, long Size, double Seconds)> listed, string? activeFile,
        double fromSec, double toSec, out long bytes)
    {
        var picked = new List<(double Start, string File, long Size)>();
        foreach (var s in listed)
        {
            if (activeFile != null && string.Equals(s.File, activeFile, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TimeSpan.TryParseExact(Path.GetFileNameWithoutExtension(s.File), @"hh\-mm\-ss", null, out var start))
                continue;
            double startSec = start.TotalSeconds;
            double endSec = startSec + Math.Max(s.Seconds, 1);
            if (startSec < toSec && endSec > fromSec)
                picked.Add((startSec, s.File, s.Size));
        }
        picked.Sort((a, b) => a.Start.CompareTo(b.Start));
        bytes = picked.Sum(p => p.Size);
        return picked.Select(p => p.File).ToList();
    }

    /// <summary>
    /// Overlays the recorder's in-memory truth for the segment being written right
    /// now onto a filesystem day listing. While a file is held open its directory
    /// mtime — the listing's duration source — can be minutes stale (NTFS updates
    /// it lazily on close; FUSE and network mounts cache attributes), which made a
    /// recording camera's lane trail "now" by up to a whole segment and look
    /// stopped, worst on high-bitrate cameras whose big segments stay open longest.
    /// The live file gets its real written duration and a live flag, and is
    /// appended if enumeration missed it entirely.
    /// </summary>
    internal static List<(string File, long Size, double Seconds, bool Live)> OverlayActiveSegment(
        List<(string File, long Size, double Seconds)> listed,
        (string Date, string File, double Seconds)? active, string date)
    {
        var result = listed.Select(s => (s.File, s.Size, s.Seconds, Live: false)).ToList();
        if (active is not { } a || !string.Equals(a.Date, date, StringComparison.Ordinal))
            return result;
        int i = result.FindIndex(s => string.Equals(s.File, a.File, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            result[i] = (result[i].File, result[i].Size, Math.Max(result[i].Seconds, a.Seconds), true);
        else
        {
            result.Add((a.File, 0, a.Seconds, true));
            result.Sort((x, y) => string.CompareOrdinal(x.File, y.File));
        }
        return result;
    }

    /// <summary>The base URL the server's own Blazor circuits use to reach the API:
    /// wildcard binds map to plain loopback, a concrete bind address is used as-is.</summary>
    internal static string LoopbackBase(string bindAddr, int port)
    {
        var host = bindAddr is "0.0.0.0" or "::" or "[::]" or "*" ? "127.0.0.1" : bindAddr;
        return $"http://{host}:{port}";
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
    /// <summary>True for /api/cameras/{name}/snapshot[.jpg] — the paths whose auth
    /// additionally accepts RTSP Basic credentials (see the session middleware).</summary>
    private static bool IsSnapshotPath(PathString path) =>
        path.StartsWithSegments("/api/cameras")
        && (path.Value!.EndsWith("/snapshot.jpg", StringComparison.OrdinalIgnoreCase)
            || path.Value.EndsWith("/snapshot", StringComparison.OrdinalIgnoreCase));

    /// <summary>The worst storage-tier state for the wall banner: null when recording
    /// is off or every tier is healthy, else the most urgent tier's summary.</summary>
    private static object? ShapeStorage(StorageLocations? storage)
    {
        if (storage == null) return null;
        var sample = storage.Sample();
        var worst = sample.FirstOrDefault(s => s.Full) ?? sample.FirstOrDefault(s => s.Warn);
        return worst == null ? null : new
        {
            label = worst.Label,
            usedPercent = Math.Round(worst.UsedPercent, 1),
            full = worst.Full,
        };
    }

    /// <summary>The floodlight-task fields the UI binds to, out of the camera's
    /// (much larger) FloodlightTask XML. Field names follow the wire format.</summary>
    private static object ShapeFloodlight(XElement task) => new
    {
        brightness = (long?)task.Element("brightness_cur") ?? 0,
        brightnessMin = (long?)task.Element("brightness_min") ?? 1,
        brightnessMax = (long?)task.Element("brightness_max") ?? 100,
        // "enable" arms the camera's own turn-on-with-motion-at-night behavior.
        auto = ((long?)task.Element("enable") ?? 0) == 1,
    };

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

        // AAC audio rides along as MP4 track 2. ADPCM cameras stay video-only in
        // the browser (MSE can't play raw PCM) — their audio is RTSP-only.
        var audio = hub.Audio is { IsAac: true, AudioSpecificConfig: not null } a ? a : null;

        string codecString = FMp4.CodecString(codec, sps);
        string mimeCodecs = audio != null
            ? $"{codecString}, {FMp4.AacCodecString(audio.AudioSpecificConfig!)}"
            : codecString;
        var meta = JsonSerializer.Serialize(new
        {
            type = "init",
            codec = codecString,
            mime = $"video/mp4; codecs=\"{mimeCodecs}\"",
            width = hub.Width,
            height = hub.Height,
            audio = audio != null,
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(meta), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var init = FMp4.BuildInit(codec, sps, pps, hub.Vps, hub.Width, hub.Height,
            audio?.AudioSpecificConfig, audio?.SampleRate ?? 0, audio?.Channels ?? 0);
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

            // Audio fragments accumulate here and ship inside the next video batch:
            // sending each ~21ms AAC frame as its own WebSocket message would wreck
            // the client's delivery-cadence measurement (its jitter buffer sizing).
            var audioFrags = new List<byte[]>();
            ulong audioDt = 0;
            uint prevAudioTs = 0;
            bool haveAudioTs = false;

            async Task FlushPendingAsync(uint totalDuration)
            {
                if ((pending == null || pending.Count == 0) && audioFrags.Count == 0) { pending = null; return; }
                using var batch = new MemoryStream();
                if (pending is { Count: > 0 })
                {
                    uint per = Math.Clamp(totalDuration / (uint)pending.Count, 900u, 45_000u); // 10..500ms per frame
                    foreach (var (sample, key) in pending)
                    {
                        batch.Write(FMp4.BuildFragment(sequence++, decodeTime, per, sample, key));
                        decodeTime += per;
                    }
                }
                pending = null;
                foreach (var frag in audioFrags)
                    batch.Write(frag);
                audioFrags.Clear();
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

                // Audio: fixed 1024-sample AUs; the decode-time advance comes from the
                // RTP delta so hub drops shift the timeline instead of desyncing it.
                // Held until the first video keyframe so both tracks start together.
                if (packet is HubAudioAac aac && audio != null)
                {
                    if (waitKeyframe) continue;
                    if (haveAudioTs)
                    {
                        uint d = unchecked(aac.RtpTs - prevAudioTs);
                        audioDt += (d > 0 && d < 30u * (uint)audio.SampleRate) ? d : FMp4.AacSamplesPerAu;
                    }
                    prevAudioTs = aac.RtpTs;
                    haveAudioTs = true;
                    audioFrags.Add(FMp4.BuildFragment(sequence++, audioDt, FMp4.AacSamplesPerAu, aac.Au,
                        keyframe: true, trackId: FMp4.AudioTrackId));
                    continue;
                }

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

    // ------------------------------------------------------------------ two-way talk over WebSocket

    /// <summary>
    /// Talk session protocol: the client opens with one JSON text message
    /// ({"sampleRate": 48000}) describing its microphone PCM, then sends raw
    /// 16-bit LE mono PCM as binary messages until it closes the socket. Errors
    /// are reported in the close reason ("talk busy", "talk unsupported", ...).
    /// </summary>
    private static async Task TalkFromWebSocketAsync(WebSocket ws, string name, ICameraControl control, CancellationToken appCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var ct = cts.Token;
        var buf = new byte[32 * 1024];

        var first = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
        if (first.MessageType != WebSocketMessageType.Text)
        {
            await TryCloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "expected a JSON hello first");
            return;
        }
        int sampleRate = 16000;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, first.Count));
            if (doc.RootElement.TryGetProperty("sampleRate", out var sr))
                sampleRate = sr.GetInt32();
        }
        catch { /* malformed hello: keep the default */ }

        var pcm = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var talk = control.TalkAsync(sampleRate, pcm.Reader, ct);

        var receive = Task.Run(async () =>
        {
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (res.MessageType == WebSocketMessageType.Binary && res.Count > 0)
                        pcm.Writer.TryWrite(buf.AsSpan(0, res.Count).ToArray());
                }
            }
            finally
            {
                pcm.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var finished = await Task.WhenAny(talk, receive).ConfigureAwait(false);
        if (finished == receive)
        {
            // Client hung up: give the tail a moment to flush and release the
            // camera's talk channel, then force the session down.
            await Task.WhenAny(talk, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        }

        var status = WebSocketCloseStatus.NormalClosure;
        var reason = "bye";
        if (talk.IsCompleted)
        {
            try { await talk.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TalkBusyException) { (status, reason) = (WebSocketCloseStatus.PolicyViolation, "talk busy"); }
            catch (NotSupportedException) { (status, reason) = (WebSocketCloseStatus.PolicyViolation, "talk unsupported"); }
            catch (CameraOfflineException) { (status, reason) = (WebSocketCloseStatus.EndpointUnavailable, "camera offline"); }
            catch (Exception ex)
            {
                Log.Debug($"{name}: talk failed: {Log.Flatten(ex)}");
                (status, reason) = (WebSocketCloseStatus.InternalServerError, "talk failed");
            }
        }

        // Close gracefully BEFORE cancelling: cancelling a pending ReceiveAsync
        // aborts the socket, and the client would see code 1006 with no reason
        // instead of the message above.
        await TryCloseAsync(ws, status, reason);
        cts.Cancel();
        try { await talk.ConfigureAwait(false); } catch { }
        try { await receive.ConfigureAwait(false); } catch { }
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
