// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>
/// The saved-server list: every server this PC has connected to, with switch and
/// remove. The connect dialog handles ADDING (connecting to a new address is
/// what adds it), so this dialog only ever points at entries that once worked.
///
/// Switching and removing the active server both end in a process restart —
/// the WebView session, API token and alert baseline are all keyed on the old
/// server, and a restart is the one honest way to drop them all at once.
/// </summary>
internal sealed class ServersForm : Form
{
    private readonly DesktopSettings _settings;
    private readonly ListBox _list = new() { IntegralHeight = false };
    private readonly Button _switch = new() { Text = "Switch to", AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 2, 6, 2) };
    private readonly Button _remove = new() { Text = "Remove...", AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 2, 6, 2) };
    private readonly Button _add = new() { Text = "Add a server...", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    private readonly Label _hint = new() { AutoSize = false, ForeColor = SystemColors.GrayText };

    /// <summary>True when the caller must restart the process: the active
    /// connection changed (switch, or the active server was removed).</summary>
    public bool RestartNeeded { get; private set; }

    public ServersForm(DesktopSettings settings)
    {
        _settings = settings;
        // Older installs predate the list: the active server seeds it, so the
        // dialog never opens empty on a configured app.
        _settings.RememberCurrent();

        Text = "Neolink.NET — servers";
        WindowTheme.Attach(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 320);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
        };
        _list.Dock = DockStyle.Fill;
        layout.Controls.Add(_list);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _hint.Height = 34;
        _hint.Dock = DockStyle.Bottom;
        _hint.Text = "Connecting to a new address (Add a server) is what saves it here. " +
                     "Removing erases its saved sign-in from this PC only.";
        layout.Controls.Add(_hint);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(16, 10, 16, 10),
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 2, 6, 2) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_remove);
        buttons.Controls.Add(_switch);
        buttons.Controls.Add(_add);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = close;

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => SwitchToSelected();
        _switch.Click += (_, _) => SwitchToSelected();
        _remove.Click += (_, _) => RemoveSelected();
        _add.Click += (_, _) => AddServer();

        Reload();
    }

    private DesktopSettings.SavedServer? Selected => _list.SelectedItem is Entry e ? e.Server : null;

    private bool IsActive(DesktopSettings.SavedServer s) =>
        string.Equals(s.Url, _settings.ServerUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>ListBox row: the server plus how it reads to a person.</summary>
    private sealed record Entry(DesktopSettings.SavedServer Server, string Text)
    {
        public override string ToString() => Text;
    }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (var s in _settings.Servers)
        {
            var who = string.IsNullOrEmpty(s.Username) ? "no sign-in" : s.Username;
            var tag = IsActive(s) ? "   — connected" : "";
            _list.Items.Add(new Entry(s, $"{s.Url}   ({who}){tag}"));
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var sel = Selected;
        _switch.Enabled = sel != null && !IsActive(sel);
        _remove.Enabled = sel != null;
    }

    private void SwitchToSelected()
    {
        var sel = Selected;
        if (sel == null || IsActive(sel)) return;
        _settings.RememberCurrent();     // the outgoing server keeps its place
        _settings.ActivateServer(sel);
        _settings.Save();
        RestartNeeded = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RemoveSelected()
    {
        var sel = Selected;
        if (sel == null) return;
        bool active = IsActive(sel);
        var answer = MessageBox.Show(this,
            $"Remove {sel.Url}?\r\n\r\n" +
            "Its saved sign-in — including the remembered password, if any — is erased " +
            "from this PC. Nothing on the server itself is touched; its recordings and " +
            "accounts stay." +
            (active ? "\r\n\r\nThis is the server you are connected to: Neolink.NET will " +
                      "restart and ask for a server, like a fresh install." : ""),
            "Neolink.NET — remove server",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        _settings.RemoveServer(sel);
        _settings.Save();
        if (active)
        {
            RestartNeeded = true;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        Reload();
    }

    private void AddServer()
    {
        using var dlg = new ConnectForm(_settings, reconfiguring: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // The connect dialog committed a new active server (and saved both old
        // and new into the list) — same restart contract as a switch.
        RestartNeeded = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
