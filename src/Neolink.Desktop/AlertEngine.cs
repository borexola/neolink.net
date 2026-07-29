// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>
/// The always-on half of the desktop app: a poll against the server that turns
/// new events, camera faults and server conditions into native notifications,
/// running for as long as the process does — window open, window hidden in the
/// tray, or the user parked on some other page entirely.
///
/// The rules it applies belong to the ACCOUNT, not to this machine: they are
/// fetched from and written back to /api/me/settings/notifications, the same
/// blob the web UI's alert panel edits, so the two never disagree. What the
/// machine owns is in <see cref="DesktopSettings"/> — the master switch for this
/// PC, quiet hours, sound, poll cadence.
/// </summary>
internal sealed class AlertEngine : IDisposable
{
    private readonly ServerLink _link;
    private readonly DesktopSettings _settings;
    private readonly Toaster _toaster;
    private readonly AlertRules _rules = new();
    private readonly SynchronizationContext? _ui;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Tags shown recently, so a notification the WEB UI raised inside the
    /// WebView and one this engine decided on independently collapse into one.
    /// Both sides tag detections with the event id, which makes the match exact.</summary>
    private readonly Dictionary<string, DateTime> _recentTags = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(90);

    private AlertPrefs _prefs = new();

    /// <summary>A notification was clicked: show the window on this deep link.</summary>
    public event Action<string?>? OpenRequested;

    /// <summary>Connection state changed — drives the tray tooltip and the status
    /// line. Null message means healthy.</summary>
    public event Action<string?>? StatusChanged;

    public AlertEngine(ServerLink link, DesktopSettings settings, Toaster toaster)
    {
        _link = link;
        _settings = settings;
        _toaster = toaster;
        _ui = SynchronizationContext.Current;
        _prefs = settings.CachedAlertPrefs ?? new AlertPrefs();
        _toaster.Activated += link2 => Post(() => OpenRequested?.Invoke(link2));
    }

    /// <summary>The account's alert rules as last loaded. Edited by the
    /// notifications window and pushed back with <see cref="SavePrefsAsync"/>.</summary>
    public AlertPrefs Prefs => _prefs;

    public string? LastStatus { get; private set; }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Pulls the account's rules from the server, falling back to the
    /// cached copy when the server has none (or cannot be reached). The cache is
    /// rewritten only when the rules actually changed — this runs on every poll
    /// for the app's whole life, and that must not mean a disk write every few
    /// seconds.</summary>
    private DateTime _lastLocalSaveUtc = DateTime.MinValue;

    private async Task LoadPrefsAsync(CancellationToken ct)
    {
        // Just saved from this machine: our own PUT may still be in flight, and a
        // GET racing it would briefly resurrect the rules we just replaced.
        if (DateTime.UtcNow - _lastLocalSaveUtc < TimeSpan.FromSeconds(10)) return;
        var server = await _link.GetAlertPrefsAsync(ct).ConfigureAwait(false);
        if (server != null)
        {
            _prefs = server;
            if (System.Text.Json.JsonSerializer.Serialize(server)
                != System.Text.Json.JsonSerializer.Serialize(_settings.CachedAlertPrefs))
            {
                _settings.CachedAlertPrefs = server;
                _settings.Save();
            }
        }
        else _prefs = _settings.CachedAlertPrefs ?? _prefs;
    }

    /// <summary>Saves edited rules to the account (so the browser sees them) and to
    /// the local cache (so a server that is down cannot silence this machine).</summary>
    public async Task SavePrefsAsync(AlertPrefs prefs, CancellationToken ct = default)
    {
        _lastLocalSaveUtc = DateTime.UtcNow;
        _prefs = prefs;
        _settings.CachedAlertPrefs = prefs;
        _settings.Save();
        await _link.PutAlertPrefsAsync(prefs, ct).ConfigureAwait(false);
    }

    /// <summary>The web UI inside the shell's WebView saved new rules: re-read
    /// them now instead of waiting for the next poll.</summary>
    public void RefreshPrefs()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => LoadPrefsAsync(ct), ct);
    }

    // ---- the loop ---------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        var lastGood = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // In parallel: sequentially, a dead server would cost three full
                // HTTP timeouts per cycle before the next sleep. The account rules
                // ride along every cycle so an edit made in the web UI applies
                // within one poll, not minutes later.
                var eventsTask = _link.EventsAsync(60, ct);
                var camerasTask = _link.CamerasAsync(ct);
                var featuresTask = _link.FeaturesAsync(ct);
                var prefsTask = LoadPrefsAsync(ct);
                var events = await eventsTask.ConfigureAwait(false);
                var cameras = await camerasTask.ConfigureAwait(false);
                var features = await featuresTask.ConfigureAwait(false);
                await prefsTask.ConfigureAwait(false);

                if (events == null && cameras == null && features == null)
                {
                    // Nothing answered: the server is down or we are not signed in.
                    // Once that has gone on longer than the event list's own window,
                    // the baseline is meaningless — drop it so the reconnect seeds
                    // quietly instead of firing an hour of history.
                    if (DateTime.UtcNow - lastGood > AlertRules.MaxEventAge)
                        _rules.Reset();
                    Status(_link.LastError ?? "cannot reach the server");
                }
                else
                {
                    lastGood = DateTime.UtcNow;
                    Status(null);

                    var alerts = _rules.Evaluate(_prefs, events, cameras, features, DateTime.UtcNow,
                        DesktopLog.Write);
                    if (!_settings.NotificationsEnabled && alerts.Count > 0)
                        DesktopLog.Write($"{alerts.Count} alert(s) suppressed — notifications are OFF on this PC (tray menu)");
                    if (_settings.NotificationsEnabled)
                        foreach (var alert in alerts)
                            await RaiseAsync(alert, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Status(ex.Message); }

            try
            {
                var seconds = Math.Clamp(_settings.PollSeconds, 5, 300);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Applies quiet hours and the duplicate window, fetches the thumbnail
    /// and shows the notification.</summary>
    private async Task RaiseAsync(Alert alert, CancellationToken ct)
    {
        if (_settings.InQuietHours(DateTime.Now)
            && AlertRules.QuietCanSuppress(alert.Kind, _settings.QuietSilencesSystem))
        {
            DesktopLog.Write($"alert {alert.Tag}: suppressed — quiet hours");
            return;
        }
        if (!MarkShown(alert.Tag))
        {
            DesktopLog.Write($"alert {alert.Tag}: suppressed — already shown moments ago (web UI or this engine)");
            return;
        }

        string? image = null;
        if (_settings.ShowThumbnail && alert.ThumbPath != null)
            image = await FetchThumbAsync(alert, ct).ConfigureAwait(false);

        DesktopLog.Write($"alert {alert.Tag}: showing \"{alert.Title}\"");
        Post(() => _toaster.Show(alert, _settings, image));
    }

    /// <summary>
    /// A notification the WEB UI raised inside the WebView, handed over so it comes
    /// out as a native toast instead of a browser popup the shell would otherwise
    /// have to allow. Returns false when this engine already showed the same thing
    /// — which is the normal case, and exactly why the two are matched by tag.
    /// The kind is read back off the web UI's tag scheme so quiet hours treat a
    /// storage-full alert here exactly as they treat the engine's own.
    /// </summary>
    public bool ShowFromWebView(string tag, string title, string body, string? deepLink)
    {
        if (!_settings.NotificationsEnabled) return false;
        var kind = tag.StartsWith("sys-", StringComparison.Ordinal) ? AlertKind.ServerCondition
            : tag.StartsWith("offline-", StringComparison.Ordinal) ? AlertKind.CameraOffline
            : AlertKind.Detection;
        if (_settings.InQuietHours(DateTime.Now)
            && AlertRules.QuietCanSuppress(kind, _settings.QuietSilencesSystem)) return false;
        if (!MarkShown(tag)) return false;
        _toaster.Show(new Alert(kind, tag, title, body, deepLink), _settings, null);
        return true;
    }

    /// <summary>Records a tag as shown; false when it was shown moments ago.</summary>
    private bool MarkShown(string tag)
    {
        lock (_recentTags)
        {
            var now = DateTime.UtcNow;
            if (_recentTags.TryGetValue(tag, out var when) && now - when < DedupWindow) return false;
            _recentTags[tag] = now;
            if (_recentTags.Count > 200)
                foreach (var stale in _recentTags.Where(kv => now - kv.Value > DedupWindow)
                             .Select(kv => kv.Key).ToList())
                    _recentTags.Remove(stale);
            return true;
        }
    }

    private static string ThumbDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Neolink.NET", "thumbs");

    private async Task<string?> FetchThumbAsync(Alert alert, CancellationToken ct)
    {
        try
        {
            var dest = Path.Combine(ThumbDir, Toaster.SanitizeTag(alert.Tag) + ".jpg");
            var path = await _link.DownloadAsync(alert.ThumbPath!, dest, ct).ConfigureAwait(false);
            PruneThumbs();
            return path;
        }
        catch { return null; }
    }

    /// <summary>Keeps the thumbnail cache to the most recent 100 files. They exist
    /// only so a toast can show a picture; nothing reads them afterwards.</summary>
    private static void PruneThumbs()
    {
        try
        {
            var dir = new DirectoryInfo(ThumbDir);
            if (!dir.Exists) return;
            foreach (var f in dir.GetFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc).Skip(100))
                try { f.Delete(); } catch { }
        }
        catch { }
    }

    private void Status(string? message)
    {
        if (LastStatus == message) return;
        DesktopLog.Write(message == null
            ? "server connection: healthy"
            : $"server connection: {message}");
        LastStatus = message;
        Post(() => StatusChanged?.Invoke(message));
    }

    /// <summary>Hops to the UI thread — the poll runs on the thread pool and every
    /// consumer of these events touches WinForms.</summary>
    private void Post(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else action();
    }

    public void Dispose() => Stop();
}
