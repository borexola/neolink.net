// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Neolink.Desktop;

/// <summary>
/// The shell's own connection to the Neolink.NET server: the small set of API
/// calls alerting needs, plus sign-in.
///
/// It is separate from the WebView on purpose. The web UI raises notifications
/// from a poll that lives on its Home page, so alerts stop the moment the user
/// navigates to Timeline — acceptable in a browser tab, useless in a tray app
/// that starts on boot. This client keeps polling whatever the window is showing
/// and whether or not the window exists at all.
///
/// A 401 mid-session means the token expired: every call goes through
/// <see cref="SendAsync"/>, which re-authenticates once with the saved
/// credentials and retries. Nothing here ever throws at the caller — callers get
/// null and decide, because a poll that throws is a poll that stops.
/// </summary>
internal sealed class ServerLink : IDisposable
{
    private readonly DesktopSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly bool _persistLogin;
    private string? _token;

    /// <summary>A sign-in produced a fresh token. The WebView listens: its
    /// bootstrap script embeds the token, and a page reloaded after a mid-session
    /// re-login must get the new one, not the one the app started with.</summary>
    public event Action<string>? TokenRefreshed;

    /// <summary>PascalCase out, case-insensitive in — byte-for-byte what the web
    /// UI writes to /api/me/settings/notifications, so the two clients read each
    /// other's rules.</summary>
    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    /// <param name="persistLogin">Whether a successful sign-in writes the settings
    /// file. The app's own link persists; the connect dialog probes a CANDIDATE
    /// settings object and must not let a mere test overwrite the real file —
    /// it commits explicitly once the user says Connect.</param>
    public ServerLink(DesktopSettings settings, bool persistLogin = true)
    {
        _settings = settings;
        _persistLogin = persistLogin;
        var handler = new HttpClientHandler { UseProxy = false };
        if (settings.AllowUntrustedCertificate)
        {
            // Explicitly opted in: a LAN server behind a self-signed certificate is
            // the normal case for this product, and the alternative is an app that
            // simply cannot connect. Never the default, and never silent — the
            // connect dialog states what it means.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _token = settings.Token;
    }

    public string BaseUrl => _settings.ServerUrl.TrimEnd('/');
    public string? Token => _token;

    /// <summary>Set when the last call failed at the transport level, for the tray
    /// tooltip and the status line. Null while things are healthy.</summary>
    public string? LastError { get; private set; }

    public string Url(string relative) => BaseUrl + relative;

    // ---- authentication ---------------------------------------------------

    public async Task<ApiAuthStatus?> AuthStatusAsync(CancellationToken ct = default) =>
        await GetAsync<ApiAuthStatus>("/api/auth/status", ct).ConfigureAwait(false);

    /// <summary>Signs in and keeps the token. Returns null on success, otherwise a
    /// message fit to show a person.</summary>
    public async Task<string?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(Url("/api/auth/login"),
                new { username, password }, ApiJson, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var err = TryRead<ApiLogin>(body)?.Error;
                // The server checks WEB UI accounts (users.json) here. Camera and
                // RTSP logins live in the config's own user list and are rejected
                // by this endpoint, which is the usual reason a password its owner
                // knows is right comes back wrong.
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                    return (err ?? "wrong username or password") +
                           " — this is the web UI account (the server's Settings > Users), " +
                           "not the camera or RTSP login.";
                return err ?? $"sign-in failed ({(int)res.StatusCode} {res.ReasonPhrase})";
            }
            var login = TryRead<ApiLogin>(body);
            if (string.IsNullOrEmpty(login?.Token)) return "the server returned no session token";
            _token = login.Token;
            _settings.Token = login.Token;
            _settings.Username = username;
            if (_settings.RememberPassword) _settings.Password = password;
            if (_persistLogin) _settings.Save();
            LastError = null;
            TokenRefreshed?.Invoke(login.Token);
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Re-authenticates with the saved password after a 401. Silent: the
    /// caller is a background poll, not a person.</summary>
    private async Task<bool> TryReloginAsync(CancellationToken ct)
    {
        var user = _settings.Username;
        var pass = _settings.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return false;
        await _loginGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoginAsync(user, pass, ct).ConfigureAwait(false) == null; }
        finally { _loginGate.Release(); }
    }

    // ---- the polled endpoints --------------------------------------------

    public Task<List<ApiEvent>?> EventsAsync(int limit, CancellationToken ct = default) =>
        GetAsync<List<ApiEvent>>($"/api/events?limit={limit}", ct);

    public Task<ApiFeatures?> FeaturesAsync(CancellationToken ct = default) =>
        GetAsync<ApiFeatures>("/api/features", ct);

    public Task<List<ApiCamera>?> CamerasAsync(CancellationToken ct = default) =>
        GetAsync<List<ApiCamera>>("/api/cameras", ct);

    /// <summary>The account's alert rules. Null means "the server has nothing to
    /// say" — no accounts configured, or nothing saved yet — which is different
    /// from an empty rule set and must not overwrite the local cache.</summary>
    public async Task<AlertPrefs?> GetAlertPrefsAsync(CancellationToken ct = default)
    {
        var body = await GetStringAsync("/api/me/settings/notifications", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body) || body.Trim() is "{}" or "null") return null;
        try { return JsonSerializer.Deserialize<AlertPrefs>(body, PrefsJson); }
        catch { return null; }
    }

    /// <summary>Writes the rules back to the account, so a change made here shows
    /// up in the browser. False when the server has no accounts (nothing to write
    /// them against) or the write failed.</summary>
    public async Task<bool> PutAlertPrefsAsync(AlertPrefs prefs, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put, Url("/api/me/settings/notifications"))
                {
                    Content = new StringContent(JsonSerializer.Serialize(prefs, PrefsJson),
                        Encoding.UTF8, "application/json"),
                };
                return req;
            }, ct).ConfigureAwait(false);
            return res?.IsSuccessStatusCode == true;
        }
        catch { return false; }
    }

    /// <summary>Saves a binary artifact (an event thumbnail) to disk and returns the
    /// path, or null if it could not be fetched. Toasts on an unpackaged app can
    /// only show LOCAL images, so the picture has to land on disk before the
    /// notification can carry it.</summary>
    public async Task<string?> DownloadAsync(string relative, string destPath, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, Url(relative)), ct).ConfigureAwait(false);
            if (res == null || !res.IsSuccessStatusCode) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var tmp = destPath + ".part";
            await using (var file = File.Create(tmp))
                await res.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            File.Move(tmp, destPath, overwrite: true);
            return destPath;
        }
        catch { return null; }
    }

    /// <summary>A cheap reachability probe for the connect dialog and the
    /// reconnect banner. Returns null when reachable, else why not.</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync(Url("/api/auth/status"), ct).ConfigureAwait(false);
            // 401 still proves a Neolink server answered; it just wants a token.
            if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Unauthorized) return null;
            return $"the server answered {(int)res.StatusCode} {res.ReasonPhrase}";
        }
        catch (Exception ex) { return Describe(ex); }
    }

    // ---- plumbing ---------------------------------------------------------

    private async Task<T?> GetAsync<T>(string relative, CancellationToken ct) where T : class
    {
        var body = await GetStringAsync(relative, ct).ConfigureAwait(false);
        return body == null ? null : TryRead<T>(body);
    }

    private async Task<string?> GetStringAsync(string relative, CancellationToken ct)
    {
        try
        {
            using var res = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, Url(relative)), ct).ConfigureAwait(false);
            if (res == null || !res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            return null;
        }
    }

    /// <summary>Sends with the bearer token attached; on 401, re-authenticates once
    /// and sends the request again. The factory exists because an HttpRequestMessage
    /// cannot be sent twice.</summary>
    private async Task<HttpResponseMessage?> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        try
        {
            var res = await SendOnceAsync(factory(), ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                res.Dispose();
                if (!await TryReloginAsync(ct).ConfigureAwait(false))
                {
                    LastError = "not signed in";
                    return null;
                }
                res = await SendOnceAsync(factory(), ct).ConfigureAwait(false);
            }
            LastError = res.IsSuccessStatusCode ? null : $"server answered {(int)res.StatusCode}";
            return res;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            return null;
        }
    }

    private Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static T? TryRead<T>(string body) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(body, ApiJson); }
        catch { return null; }
    }

    /// <summary>Exception to a sentence a person can act on — "connection refused"
    /// beats a stack of nested socket errors in a tray tooltip.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "the server did not answer in time",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException s } =>
            s.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => "connection refused — is the web UI enabled on that port?",
                System.Net.Sockets.SocketError.HostNotFound => "that host name did not resolve",
                System.Net.Sockets.SocketError.TimedOut => "the connection timed out",
                _ => s.Message,
            },
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            "the TLS certificate was rejected — tick \"accept an untrusted certificate\" for a self-signed server",
        HttpRequestException h => h.Message,
        _ => ex.Message,
    };

    public void Dispose()
    {
        _http.Dispose();
        _loginGate.Dispose();
    }
}
