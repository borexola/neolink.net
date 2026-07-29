// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Neolink.Desktop;

/// <summary>
/// The window: a WebView2 filling the frame, a tray icon that owns the app's
/// lifetime, and nothing else. Every feature the browser has is the same code
/// running in the same place — this shell adds what a browser tab cannot give a
/// camera system, which is being there when nobody is looking at it.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly DesktopSettings _settings;
    private readonly ServerLink _link;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray;
    private readonly Toaster _toaster;
    private readonly AlertEngine _engine;
    private readonly Panel _error;
    private readonly Label _errorText;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _notificationsItem;

    private bool _reallyClosing;
    private bool _webReady;

    /// <summary>scheme://host:port of the configured server — the only origin the
    /// window may show, and the only one the bootstrap script hands the token to.</summary>
    private readonly string _origin;
    private string? _bootstrapId;
    private string? _bootstrapToken;

    public MainForm(DesktopSettings settings, ServerLink link, bool startHidden)
    {
        _settings = settings;
        _link = link;
        _origin = new Uri(link.BaseUrl).GetLeftPart(UriPartial.Authority);
        _lastOpenState = settings.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;

        Text = "Neolink.NET";
        Icon = LoadIcon();
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(11, 13, 18);   // the web UI's background: no white flash on load
        RestoreGeometry();

        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = "Neolink.NET",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow(null);

        _toaster = new Toaster(_tray, Application.ExecutablePath);
        _engine = new AlertEngine(_link, _settings, _toaster);
        _engine.OpenRequested += ShowWindow;
        _engine.StatusChanged += OnStatusChanged;

        _autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = Autostart.IsEnabled(Application.ExecutablePath),
        };
        _autostartItem.Click += (_, _) =>
        {
            _settings.StartWithWindows = _autostartItem.Checked;
            _settings.Save();
            Autostart.Set(_autostartItem.Checked, Application.ExecutablePath);
        };

        _notificationsItem = new ToolStripMenuItem("Notifications on this PC")
        {
            CheckOnClick = true,
            Checked = _settings.NotificationsEnabled,
        };
        _notificationsItem.Click += (_, _) =>
        {
            _settings.NotificationsEnabled = _notificationsItem.Checked;
            _settings.Save();
            UpdateTrayText();
        };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Neolink.NET", null, (_, _) => ShowWindow(null))
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(new ToolStripMenuItem("Notification settings...", null, (_, _) => OpenNotificationSettings()));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Server connection...", null, (_, _) => OpenConnectDialog()));
        menu.Items.Add(new ToolStripMenuItem("Reload", null, (_, _) => Reload())
        {
            ShortcutKeyDisplayString = "F5",
        });
        menu.Items.Add(new ToolStripMenuItem("Full reload", null, (_, _) => _ = HardReloadAsync())
        {
            ShortcutKeyDisplayString = "Ctrl+F5",
            ToolTipText = "Drops the cached UI (including the offline copy) and fetches everything fresh from the server",
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit Neolink.NET", null, (_, _) => QuitApp()));
        _tray.ContextMenuStrip = menu;

        // The "can't reach the server" screen. The web UI ships one of its own in
        // a service worker, but that only registers on a secure origin, and a LAN
        // server on plain http is the common case — so the shell carries its own.
        _errorText = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(231, 235, 244),
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f),
            Text = "",
        };
        var retry = new Button
        {
            Text = "Retry",
            Dock = DockStyle.Bottom,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(91, 157, 255),
            BackColor = Color.FromArgb(20, 24, 34),
        };
        retry.FlatAppearance.BorderColor = Color.FromArgb(34, 40, 57);
        retry.Click += (_, _) => Reload();
        var settingsBtn = new Button
        {
            Text = "Server connection...",
            Dock = DockStyle.Bottom,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(139, 148, 167),
            BackColor = Color.FromArgb(11, 13, 18),
        };
        settingsBtn.FlatAppearance.BorderColor = Color.FromArgb(34, 40, 57);
        settingsBtn.Click += (_, _) => OpenConnectDialog();
        _error = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(11, 13, 18),
            Visible = false,
            Padding = new Padding(40),
        };
        _error.Controls.Add(_errorText);
        _error.Controls.Add(retry);
        _error.Controls.Add(settingsBtn);

        Controls.Add(_web);
        Controls.Add(_error);
        _error.BringToFront();

        // When a WinForms surface has focus (the error screen), F5 still means
        // reload; inside the page the injected listener handles the chords.
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F5) return;
            e.Handled = true;
            if (e.Control || e.Shift) _ = HardReloadAsync();
            else Reload();
        };

        if (startHidden)
        {
            // The window still has to be Show()n once - that is what creates the
            // handle and runs OnLoad, which starts the WebView and the alert poll.
            // Minimised with no taskbar button is already invisible; Opacity 0
            // guarantees not even a frame of it paints during a logon, when the
            // machine is at its busiest and any flash would be most obvious.
            // ShowWindow puts all three back.
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
            Visible = false;
        }

        UpdateTrayText();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "neolink.ico");
            if (File.Exists(beside)) return new Icon(beside);
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch { return SystemIcons.Application; }
    }

    // ---- WebView ----------------------------------------------------------

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await InitializeWebViewAsync();
        _engine.Start();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // The default user-data folder sits next to the executable, which for an
            // MSI install is Program Files and not writable. Put it with the rest of
            // the app's per-user state instead.
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Neolink.NET", "WebView2");
            Directory.CreateDirectory(dataDir);

            var options = new CoreWebView2EnvironmentOptions
            {
                // Autoplay: a camera wall is nothing but autoplaying video.
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ShowError("The WebView2 runtime could not start.\r\n\r\n" + ex.Message +
                      "\r\n\r\nInstall the Microsoft Edge WebView2 Runtime and try again.");
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;   // it is an app, not a page
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true; // F5 / Ctrl+R stay useful
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess) HideError();
            else if (!_webReady)
                ShowError($"Can't reach {_settings.ServerUrl}\r\n\r\n{Describe(args.WebErrorStatus)}");
            _webReady |= args.IsSuccess;
        };

        // The window is the server's UI and nothing else: any top-level navigation
        // to another origin gets the real browser instead. This is also a security
        // boundary — the bootstrap script below carries the session token, so no
        // foreign page may ever load top-level in this WebView. Server-issued
        // redirects pass (an http address answered by an https redirect is the
        // server talking, not the page wandering off).
        core.NavigationStarting += (_, args) =>
        {
            if (args.IsRedirected || IsServerOrigin(args.Uri)) return;
            args.Cancel = true;
            OpenExternally(args.Uri);
        };

        // A self-signed certificate on a LAN server: honour the same explicit
        // opt-in the API client uses, and nothing more.
        core.ServerCertificateErrorDetected += (_, args) =>
        {
            args.Action = _settings.AllowUntrustedCertificate
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Default;
        };

        // The page asks for notification permission; inside a desktop app the real
        // gate is Windows' own notification settings, so grant it — for the
        // server's own pages. Anything else (a stray iframe) is refused.
        core.PermissionRequested += (_, args) =>
        {
            if (args.PermissionKind == CoreWebView2PermissionKind.Notifications)
                args.State = IsServerOrigin(args.Uri)
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            // Microphone (two-way talk) deliberately keeps the default prompt: it
            // is not the shell's decision to make silently.
        };

        // The web UI's fullscreen button asks for HTML fullscreen; without help
        // that only fills the WebView, which is to say the window it already
        // fills. A camera wall's fullscreen means the SCREEN.
        core.ContainsFullScreenElementChanged += (_, _) =>
            SetFullscreen(core.ContainsFullScreenElement);

        // Web-UI notifications become native toasts, deduplicated against the ones
        // this shell decided on itself — both sides tag detections with the event
        // id, so the same event never notifies twice.
        core.NotificationReceived += (_, args) =>
        {
            var n = args.Notification;
            args.Handled = true;
            var tag = string.IsNullOrEmpty(n.Tag) ? Guid.NewGuid().ToString("N") : n.Tag;
            var link = DeepLinkForTag(tag);
            if (_engine.ShowFromWebView(tag, n.Title ?? "Neolink.NET", n.Body ?? "", link))
                n.ReportShown();
            else
                n.ReportClosed();
        };

        // Links that leave the app (the update banner's release page) belong in the
        // real browser, not in a window with no address bar.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };

        // Ctrl+F5 / Shift+F5 / Ctrl+Shift+R mean "fresh UI from the server",
        // which is more than the browser's own cache bypass: on an https server
        // the PWA service worker keeps serving its cached app shell through an
        // ordinary refresh. The page-side listener claims those chords and hands
        // them to the shell; plain F5 / Ctrl+R stay with the browser.
        await core.AddScriptToExecuteOnDocumentCreatedAsync($$"""
            (() => {
              if (location.origin !== {{JsonSerializer.Serialize(_origin)}}) return;
              window.addEventListener('keydown', e => {
                const hard = (e.key === 'F5' && (e.ctrlKey || e.shiftKey))
                          || (e.ctrlKey && e.shiftKey && (e.key === 'r' || e.key === 'R'));
                if (!hard) return;
                e.preventDefault();
                try { window.chrome.webview.postMessage('hard-reload'); } catch (e2) {}
              }, true);
            })();
            """);
        core.WebMessageReceived += (_, args) =>
        {
            if (!IsServerOrigin(args.Source)) return;
            string? msg = null;
            try { msg = args.TryGetWebMessageAsString(); } catch { /* not a string */ }
            if (msg == "hard-reload") _ = HardReloadAsync();
            // The web UI's alerts panel saved new rules to the account: apply them
            // to this machine's engine now, not on the next poll.
            else if (msg == "alert-prefs-changed") _engine.RefreshPrefs();
        };

        await ApplyBootstrapAsync(core);

        // A re-login mid-session mints a new token; re-register the bootstrap so
        // the NEXT page load carries it instead of the stale one. Raised from the
        // engine's thread, hence the hop.
        _link.TokenRefreshed += _ =>
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(async () =>
                    {
                        if (_web.CoreWebView2 == null) return;
                        bool wasSignedOut = _bootstrapToken == null;
                        await ApplyBootstrapAsync(_web.CoreWebView2);
                        // The script only seeds the NEXT document. When the shell
                        // started without a token the page is sitting on the web
                        // UI's sign-in form while the shell itself is signed in,
                        // so that first token has to re-open it. Later refreshes
                        // leave the page alone - reloading under someone watching
                        // video would be worse than a stale tab.
                        if (wasSignedOut) Navigate("/");
                    });
            }
            catch { /* shutting down */ }
        };

        Navigate("/");
    }

    /// <summary>Registers the current bootstrap script and retires the previous
    /// registration, so exactly one (with the freshest token) runs per document.</summary>
    private async Task ApplyBootstrapAsync(CoreWebView2 core)
    {
        var token = _link.Token;
        var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildBootstrapScript(token, _origin));
        if (_bootstrapId != null)
            try { core.RemoveScriptToExecuteOnDocumentCreated(_bootstrapId); } catch { }
        _bootstrapId = id;
        _bootstrapToken = token;
    }

    private bool IsServerOrigin(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && string.Equals(u.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs before any page script, on every navigation — which is exactly why it
    /// begins with an origin check. The navigation lockdown should keep foreign
    /// pages out of this window entirely, but a script carrying a session token
    /// does not get to ASSUME that: it refuses to run anywhere but the server.
    ///
    /// Two jobs. It hands the web UI the session token the shell already holds, in
    /// the exact shape the UI stores it — so the app opens signed in rather than
    /// on a login form, without a token ever appearing in a URL. And it hides the
    /// service worker's registration from the UI's notification helper, which
    /// makes the helper fall back to the plain Notification API — the one
    /// WebView2 lets the host intercept. Without that, a notification raised on
    /// an https server would go out through the worker and the shell could
    /// neither style it nor stop it duplicating its own.
    /// </summary>
    internal static string BuildBootstrapScript(string? token, string origin)
    {
        var auth = token == null
            ? ""
            : $"try {{ localStorage.setItem('neolink.auth', {JsonSerializer.Serialize(JsonSerializer.Serialize(new { token }))}); }} catch (e) {{}}";
        return $$"""
            (() => {
              if (location.origin !== {{JsonSerializer.Serialize(origin)}}) return;
              window.__neolinkShell = true;
              {{auth}}
              try {
                const sw = navigator.serviceWorker;
                if (sw) sw.getRegistration = () => Promise.resolve(undefined);
              } catch (e) {}
            })();
            """;
    }

    /// <summary>The web UI's notification tags are the event id for detections and
    /// a known prefix for the rest; that is enough to rebuild the click target,
    /// which WebView2 does not pass through.</summary>
    internal static string DeepLinkForTag(string tag) =>
        tag.StartsWith("sys-", StringComparison.Ordinal) || tag.StartsWith("offline-", StringComparison.Ordinal)
            ? "/"
            : $"/events?event={Uri.EscapeDataString(tag)}";

    private void Navigate(string relative)
    {
        if (_web.CoreWebView2 == null) return;
        var url = _link.Url(relative);
        try { _web.CoreWebView2.Navigate(url); }
        catch { ShowError("That server address could not be opened: " + url); }
    }

    private void Reload()
    {
        HideError();
        if (_web.CoreWebView2 == null) return;
        if (_webReady) _web.CoreWebView2.Reload();
        else Navigate("/");
    }

    /// <summary>The Ctrl+F5 path: unregisters the service worker and drops its
    /// caches (an ordinary refresh cannot get past them on an https server),
    /// then reloads bypassing the HTTP cache too.</summary>
    private async Task HardReloadAsync()
    {
        HideError();
        var core = _web.CoreWebView2;
        if (core == null) return;
        if (!_webReady)
        {
            Navigate("/");
            return;
        }
        try
        {
            var cleanup = JsonSerializer.Serialize(new
            {
                expression = """
                    (async () => {
                      try { for (const r of await (navigator.serviceWorker?.getRegistrations?.() ?? [])) await r.unregister(); } catch (e) {}
                      try { for (const k of await (caches?.keys?.() ?? [])) await caches.delete(k); } catch (e) {}
                    })()
                    """,
                awaitPromise = true,
            });
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", cleanup);
            await core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}""");
        }
        catch
        {
            Reload();
        }
    }

    private static void OpenExternally(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true,
            });
        }
        catch { /* no browser, or a scheme the shell refuses: nothing to do */ }
    }

    private static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.ConnectionAborted or
        CoreWebView2WebErrorStatus.ConnectionReset or
        CoreWebView2WebErrorStatus.CannotConnect => "Nothing answered on that address and port.",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "That host name did not resolve.",
        CoreWebView2WebErrorStatus.Timeout => "The server did not answer in time.",
        CoreWebView2WebErrorStatus.ServerUnreachable => "The server is unreachable from this network.",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateIsInvalid or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors =>
            "The TLS certificate was rejected. For a self-signed server, tick " +
            "\"accept an untrusted certificate\" under Server connection.",
        _ => "The connection failed.",
    };

    // ---- window behaviour --------------------------------------------------

    /// <summary>Brings the window up, optionally on a specific page.</summary>
    private void ShowWindow(string? deepLink)
    {
        if (IsDisposed) return;   // a toast can outlive the app and still be clicked
        if (deepLink != null && _settings.ClickOpensEvent) Navigate(deepLink);
        Opacity = 1;                 // undo the start-hidden suppression, once
        ShowInTaskbar = true;
        Visible = true;
        WindowState = _settings.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    // ---- HTML fullscreen -> a truly fullscreen window ----------------------

    private bool _fullscreen;
    private FormWindowState _preFullscreenState;

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _preFullscreenState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            // Through Normal even when already maximised: a style change alone
            // does not re-lay the maximised bounds over the taskbar.
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _preFullscreenState == FormWindowState.Minimized
                ? FormWindowState.Normal
                : _preFullscreenState;
        }
    }

    /// <summary>A second launch of the shortcut asked for the window.</summary>
    public void ShowFromAnotherInstance() => ShowWindow(null);

    private void QuitApp()
    {
        _reallyClosing = true;
        Close();
    }

    /// <summary>The last state that was not "minimised". Minimising throws away
    /// the difference between a maximised and a normal window, and restoring from
    /// the tray has to put back the one the user actually had.</summary>
    private FormWindowState _lastOpenState = FormWindowState.Normal;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // The fullscreen dance maximises the window as an implementation detail;
        // that must not be remembered as the user's preference.
        if (WindowState != FormWindowState.Minimized && !_fullscreen) _lastOpenState = WindowState;
        if (WindowState == FormWindowState.Minimized && _settings.CloseToTray)
        {
            // Minimise means "get out of the way", and in a tray app the tray is
            // where out of the way is. Remember the geometry on the way down, so
            // restoring from the tray brings back the window that was put there
            // rather than the one that was last saved on exit.
            if (Visible) SaveGeometry();
            Visible = false;
            ShowInTaskbar = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The ✕ hides; alerts are the whole reason this app starts with Windows,
        // and a closed window must not stop them. Quit is on the tray menu.
        if (!_reallyClosing && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveGeometry();
            Visible = false;
            ShowInTaskbar = false;
            return;
        }
        SaveGeometry();
        _settings.Save();
        _engine.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void RestoreGeometry()
    {
        Size = new Size(Math.Max(640, _settings.WindowWidth), Math.Max(480, _settings.WindowHeight));
        if (_settings.WindowX >= 0 && _settings.WindowY >= 0
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(
                new Rectangle(_settings.WindowX, _settings.WindowY, Width, Height))))
            Location = new Point(_settings.WindowX, _settings.WindowY);
        else
            StartPosition = FormStartPosition.CenterScreen;
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveGeometry()
    {
        _settings.WindowMaximized = _lastOpenState == FormWindowState.Maximized;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.WindowX = bounds.X;
            _settings.WindowY = bounds.Y;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
    }

    // ---- status, dialogs ---------------------------------------------------

    private void OnStatusChanged(string? message) => UpdateTrayText(message);

    private void UpdateTrayText(string? status = null)
    {
        status ??= _engine.LastStatus;
        var host = _settings.ServerUrl.Length > 0 ? _settings.ServerUrl : "not configured";
        var line = $"Neolink.NET — {status ?? host}";
        if (!_settings.NotificationsEnabled) line += " (alerts off)";
        // The shell tooltip is capped at 63 characters by the shell itself.
        _tray.Text = line.Length <= 63 ? line : line[..60] + "...";
        _notificationsItem.Checked = _settings.NotificationsEnabled;
        _autostartItem.Checked = Autostart.IsEnabled(Application.ExecutablePath);
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _error.Visible = true;
        _error.BringToFront();
    }

    private void HideError()
    {
        _error.Visible = false;
        _web.BringToFront();
    }

    private void OpenNotificationSettings()
    {
        using var dlg = new NotificationsForm(_settings, _engine, _toaster, _link);
        ShowDialogCentered(dlg);
        UpdateTrayText();
    }

    private void OpenConnectDialog()
    {
        using var dlg = new ConnectForm(_settings, reconfiguring: true);
        if (ShowDialogCentered(dlg) != DialogResult.OK) return;
        // The connection changed underneath everything: the simplest correct
        // answer is a clean restart of the process.
        Program.RestartSelf();
    }

    private DialogResult ShowDialogCentered(Form dlg)
    {
        if (Visible) return dlg.ShowDialog(this);
        dlg.StartPosition = FormStartPosition.CenterScreen;
        dlg.TopMost = true;   // launched from the tray with no window behind it
        return dlg.ShowDialog();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Dispose();
            _toaster.Dispose();
            _tray.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
