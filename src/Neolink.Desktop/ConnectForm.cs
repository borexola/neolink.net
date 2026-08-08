// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>
/// Where the server lives and who you are on it. Shown on first run, and again
/// from the tray whenever any of that changes.
///
/// It signs in here rather than leaving it to the web page, because the shell
/// needs a token of its own: the alert poll runs whether or not a page is
/// loaded, and it cannot borrow the browser's session.
/// </summary>
internal sealed class ConnectForm : Form
{
    private readonly DesktopSettings _settings;
    private readonly TextBox _url = new();
    private readonly TextBox _user = new();
    private readonly TextBox _pass = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _remember = new() { Text = "Remember the password (encrypted for this Windows account)" };
    private readonly CheckBox _insecure = new() { Text = "Accept an untrusted TLS certificate (self-signed LAN server)" };
    // Tall enough for the three-line prefill hint; error messages run long too.
    private readonly Label _status = new() { AutoSize = false, Height = 64 };
    private Font? _statusBold;
    // AutoSize: the default 75px button clips "Test connection" at any DPI, and
    // fixed widths just move the clipping point. MinimumSize keeps "Connect" and
    // "Cancel" from shrinking into tabs; the flow panel lays them out either way.
    private readonly Button _test = new()
        { Text = "Test connection", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    private readonly Button _ok = new()
        { Text = "Connect", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 2, 6, 2) };

    public ConnectForm(DesktopSettings settings, bool reconfiguring)
    {
        _settings = settings;

        Text = reconfiguring ? "Neolink.NET — server connection" : "Neolink.NET — connect to your server";
        WindowTheme.Attach(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 400);
        // Font scaling with an explicit 96-dpi baseline (Segoe UI 9pt = 7x15).
        // AutoScaleMode alone does nothing for a code-built form: without
        // designer-recorded AutoScaleDimensions the runtime DPI becomes the
        // baseline, no scaling ever runs, and on a 125%/150% display the text
        // grows into fixed pixel sizes — clipped labels and buttons.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;

        // A first run beside the installer's local server should not open with an
        // empty box asking for an address the user never chose: the service on
        // this PC is the server. Prefilled, not forced — still editable.
        bool localServer = string.IsNullOrEmpty(settings.ServerUrl) && LocalServerInstalled();
        _url.Text = localServer ? "http://localhost:8655" : settings.ServerUrl;
        _url.PlaceholderText = "10.1.0.60:8000";
        _user.Text = settings.Username ?? "";
        _pass.Text = settings.Password ?? "";
        _remember.Checked = settings.RememberPassword;
        _insecure.Checked = settings.AllowUntrustedCertificate;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control field)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) });
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(field);
        }

        Row("Server address", _url);
        layout.Controls.Add(new Label());
        layout.Controls.Add(new Label
        {
            Text = "The web UI's address — the same one you type in a browser. https:// if you use TLS.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
        });
        Row("Username", _user);
        Row("Password", _pass);

        layout.Controls.Add(new Label());
        _remember.AutoSize = true;
        layout.Controls.Add(_remember);
        layout.Controls.Add(new Label());
        _insecure.AutoSize = true;
        layout.Controls.Add(_insecure);

        layout.Controls.Add(new Label());
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = SystemColors.GrayText;
        _status.Margin = new Padding(0, 14, 0, 0);
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(16, 10, 16, 10),
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 2, 6, 2),
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_test);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        _test.Click += async (_, _) => await TestAsync(commit: false);
        FormClosing += OnClosingAsync;
        Disposed += (_, _) => _statusBold?.Dispose();

        // A no-auth server needs no credentials at all; say so instead of leaving
        // two boxes looking mandatory.
        if (!reconfiguring)
            _status.Text = localServer
                ? "The server installed on this PC is filled in — leave the boxes blank and press " +
                  "Connect. It will ask you to create your admin account right after."
                : "Leave the username and password blank if your server has no accounts.";
    }

    /// <summary>The installer's local-server feature leaves this marker; its
    /// presence means "http://localhost:8655 is (about to be) a Neolink server".
    /// The service may still be starting when the dialog opens, so this reads the
    /// registry rather than probing the port — Test connection does the probing.</summary>
    private static bool LocalServerInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Neolink.NET");
            return key?.GetValue("LocalServer") is int and not 0;
        }
        catch
        {
            return false;
        }
    }

    private DesktopSettings Candidate()
    {
        var clone = new DesktopSettings
        {
            ServerUrl = DesktopSettings.NormalizeUrl(_url.Text) ?? "",
            Username = _user.Text.Trim(),
            RememberPassword = _remember.Checked,
            AllowUntrustedCertificate = _insecure.Checked,
        };
        return clone;
    }

    /// <summary>Proves the typed connection works, against a CANDIDATE settings
    /// object with a non-persisting link — a mere Test must leave the real
    /// settings file untouched. Only <paramref name="commit"/> (the Connect
    /// button's path) copies the verified values in and saves.</summary>
    private async Task<bool> TestAsync(bool commit)
    {
        var url = DesktopSettings.NormalizeUrl(_url.Text);
        if (url == null)
        {
            Say("That is not an address I can use — try \"10.1.0.60:8000\".", error: true);
            return false;
        }
        _url.Text = url;
        Enabled = false;
        Say("Connecting...");
        try
        {
            var candidate = Candidate();
            using var link = new ServerLink(candidate, persistLogin: false);
            var reachable = await link.ProbeAsync();
            if (reachable != null)
            {
                Say(reachable, error: true);
                return false;
            }
            var status = await link.AuthStatusAsync();
            if (status == null)
            {
                // Something answered the probe but this is not an API we can read.
                // Treating it as "no accounts" would close the dialog on a
                // connection that cannot work.
                Say($"{candidate.ServerUrl} answered, but not like a Neolink.NET web UI — " +
                    "check the port is the web UI's, not RTSP.", error: true);
                return false;
            }
            if (status is { Enabled: true })
            {
                if (_user.Text.Trim().Length == 0)
                {
                    Say("This server has accounts — a username and password are required.", error: true);
                    return false;
                }
                var failure = await link.LoginAsync(_user.Text.Trim(), _pass.Text);
                if (failure != null)
                {
                    Say(failure, error: true);
                    return false;
                }
                Say($"Success — signed in as {candidate.Username}.", success: true);
            }
            else if (status.SetupRequired)
            {
                Say("Success — connected. The next screen is the server's own: it will ask you to create the admin account.", success: true);
            }
            else
            {
                Say("Success — connected. This server has no accounts, so no sign-in is needed.", success: true);
            }

            if (!commit) return true;

            // Only a verified connection is worth keeping — and only Connect keeps it.
            _settings.ServerUrl = candidate.ServerUrl;
            _settings.Username = string.IsNullOrEmpty(candidate.Username) ? null : candidate.Username;
            _settings.RememberPassword = candidate.RememberPassword;
            _settings.AllowUntrustedCertificate = candidate.AllowUntrustedCertificate;
            _settings.Token = candidate.Token;
            _settings.Password = candidate.RememberPassword && _pass.Text.Length > 0 ? _pass.Text : null;
            _settings.Save();
            return true;
        }
        finally
        {
            Enabled = true;
            _url.Focus();
        }
    }

    private async void OnClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;
        // Connect means connect: prove it works before the dialog goes away, so
        // nobody lands in a tray app that silently talks to nothing.
        e.Cancel = true;
        if (await TestAsync(commit: true))
        {
            FormClosing -= OnClosingAsync;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void Say(string message, bool error = false, bool success = false)
    {
        _statusBold ??= new Font(Font, FontStyle.Bold);
        _status.ForeColor = error ? Color.FromArgb(200, 50, 50)
            : success ? Color.FromArgb(30, 130, 60)
            : SystemColors.GrayText;
        _status.Font = error || success ? _statusBold : Font;
        _status.Text = message;
        _status.Refresh();
    }
}
