// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Desktop;

/// <summary>
/// Everything about what this machine tells you and when.
///
/// The split matters and the window says so: the per-camera and per-label rules
/// belong to the ACCOUNT and are written straight back to the server, so they
/// apply in the browser too; quiet hours, sound and the poll cadence belong to
/// THIS PC and never leave it.
/// </summary>
internal sealed class NotificationsForm : Form
{
    private readonly DesktopSettings _settings;
    private readonly AlertEngine _engine;
    private readonly Toaster _toaster;
    private readonly ServerLink _link;
    private readonly AlertPrefs _prefs;

    private readonly CheckBox _onThisPc = new() { Text = "Show notifications on this PC", AutoSize = true };
    private readonly CheckBox _onAccount = new() { Text = "Alerts enabled for this account (shared with the web UI)", AutoSize = true };
    private readonly ListBox _cameras = new() { IntegralHeight = false };
    private readonly CheckedListBox _labels = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox _offline = new() { Text = "Alert when this camera goes offline", AutoSize = true };
    private readonly NumericUpDown _cooldown = new() { Minimum = 0, Maximum = 3600, Increment = 15 };
    private readonly NumericUpDown _poll = new() { Minimum = 5, Maximum = 300, Increment = 5 };
    private readonly CheckBox _sound = new() { Text = "Play a sound", AutoSize = true };
    private readonly CheckBox _thumb = new() { Text = "Show the event thumbnail", AutoSize = true };
    private readonly CheckBox _click = new() { Text = "Clicking a notification opens the event", AutoSize = true };
    private readonly CheckBox _quiet = new() { Text = "Quiet hours", AutoSize = true };
    private readonly DateTimePicker _quietFrom = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 90 };
    private readonly DateTimePicker _quietTo = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 90 };
    private readonly CheckBox _quietSystem = new() { Text = "Quiet hours also silence camera and server faults", AutoSize = true };
    private readonly CheckBox _sysStorage = new() { Text = "Storage full", AutoSize = true };
    private readonly CheckBox _sysOverload = new() { Text = "Server overloaded", AutoSize = true };
    private readonly CheckBox _sysWrite = new() { Text = "Recording write failures", AutoSize = true };
    private readonly Label _backend = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private string? _current;
    private bool _loading = true;

    public NotificationsForm(DesktopSettings settings, AlertEngine engine, Toaster toaster, ServerLink link)
    {
        _settings = settings;
        _engine = engine;
        _toaster = toaster;
        _link = link;
        _prefs = engine.Prefs.Clone();

        Text = "Neolink.NET — notifications";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 600);
        MinimumSize = new Size(700, 560);
        AutoScaleMode = AutoScaleMode.Dpi;

        Controls.Add(BuildBody());
        Controls.Add(BuildButtons());

        LoadValues();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Only once the handle exists: the fetch ends in a BeginInvoke, which
        // throws into the void on a handle-less form — and starting it here also
        // keeps the selftest's construction pass free of network calls.
        _ = LoadCamerasAsync();
    }

    // ---- layout -----------------------------------------------------------

    private Control BuildBody()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(14, 12, 14, 4),
            AutoScroll = true,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // -- switches
        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        top.Controls.Add(_onThisPc);
        top.Controls.Add(_onAccount);
        _backend.Margin = new Padding(0, 6, 0, 8);
        top.Controls.Add(_backend);
        root.Controls.Add(top);

        // -- per-camera rules
        var cams = new GroupBox
        {
            Text = "Which cameras alert, and for what (saved to your account)",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
        };
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _cameras.Dock = DockStyle.Fill;
        _labels.Dock = DockStyle.Fill;
        _labels.Items.AddRange(AlertPrefs.Labels.Cast<object>().ToArray());
        split.Controls.Add(_cameras, 0, 0);
        split.Controls.Add(_labels, 1, 0);
        var camFoot = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        camFoot.Controls.Add(_offline);
        split.Controls.Add(camFoot, 0, 1);
        split.SetColumnSpan(camFoot, 2);
        cams.Controls.Add(split);
        root.Controls.Add(cams);

        // -- this-PC settings
        var pc = new GroupBox
        {
            Text = "This PC",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10, 8, 10, 10),
        };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _cooldown.Width = 70;
        _poll.Width = 70;
        grid.Controls.Add(new Label { Text = "Repeat no sooner than", AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(_cooldown);
        grid.Controls.Add(new Label { Text = "seconds  (account-wide)", AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(new Label());

        grid.Controls.Add(new Label { Text = "Check the server every", AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(_poll);
        grid.Controls.Add(new Label { Text = "seconds", AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(new Label());

        var quietRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        quietRow.Controls.Add(_quiet);
        quietRow.Controls.Add(new Label { Text = "from", AutoSize = true, Margin = new Padding(8, 6, 4, 0) });
        quietRow.Controls.Add(_quietFrom);
        quietRow.Controls.Add(new Label { Text = "to", AutoSize = true, Margin = new Padding(8, 6, 4, 0) });
        quietRow.Controls.Add(_quietTo);
        grid.Controls.Add(quietRow);
        grid.SetColumnSpan(quietRow, 4);

        foreach (var c in new Control[] { _quietSystem, _sound, _thumb, _click })
        {
            grid.Controls.Add(c);
            grid.SetColumnSpan(c, 4);
        }

        var sys = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
        sys.Controls.Add(new Label { Text = "Server alerts:", AutoSize = true, Margin = new Padding(0, 4, 8, 0) });
        sys.Controls.Add(_sysStorage);
        sys.Controls.Add(_sysOverload);
        sys.Controls.Add(_sysWrite);
        grid.Controls.Add(sys);
        grid.SetColumnSpan(sys, 4);

        pc.Controls.Add(grid);
        root.Controls.Add(pc);
        return root;
    }

    private Control BuildButtons()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(14, 10, 14, 10),
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var test = new Button { Text = "Show a test notification", Width = 180 };
        test.Click += (_, _) => ShowTest();
        bar.Controls.Add(save);
        bar.Controls.Add(cancel);
        bar.Controls.Add(test);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) => Persist();
        return bar;
    }

    // ---- values ------------------------------------------------------------

    private void LoadValues()
    {
        _loading = true;
        _onThisPc.Checked = _settings.NotificationsEnabled;
        _onAccount.Checked = _prefs.Enabled;
        _cooldown.Value = Math.Clamp(_prefs.CooldownSeconds, 0, 3600);
        _poll.Value = Math.Clamp(_settings.PollSeconds, 5, 300);
        _sound.Checked = _settings.Sound;
        _thumb.Checked = _settings.ShowThumbnail;
        _click.Checked = _settings.ClickOpensEvent;
        _quiet.Checked = _settings.QuietFrom != null && _settings.QuietTo != null;
        _quietFrom.Value = ParseTime(_settings.QuietFrom, 22, 0);
        _quietTo.Value = ParseTime(_settings.QuietTo, 7, 0);
        _quietSystem.Checked = _settings.QuietSilencesSystem;
        _sysStorage.Checked = _prefs.SysStorage;
        _sysOverload.Checked = _prefs.SysOverload;
        _sysWrite.Checked = _prefs.SysWriteFailure;
        _backend.Text = _toaster.RichToasts
            ? "Notifications appear as Windows toasts and stay in the Action Center."
            : "Notifications appear as tray balloons — Windows toasts need the Start Menu shortcut the installer creates.";

        _cameras.SelectedIndexChanged += (_, _) => ShowCamera(_cameras.SelectedItem as string);
        _labels.ItemCheck += (_, e) => BeginInvoke(() => CaptureLabels(e));
        _offline.CheckedChanged += (_, _) => CaptureOffline();
        if (!_toaster.RichToasts)
        {
            _thumb.Enabled = false;
            _thumb.Text += " (toasts only)";
        }
        _loading = false;
        SetLabelsEnabled(false);
    }

    private static DateTime ParseTime(string? hhmm, int fallbackHour, int fallbackMinute)
    {
        var today = DateTime.Today;
        return TimeOnly.TryParse(hhmm, out var t)
            ? today.AddHours(t.Hour).AddMinutes(t.Minute)
            : today.AddHours(fallbackHour).AddMinutes(fallbackMinute);
    }

    /// <summary>Fills the camera list from the server, keeping any camera the rules
    /// already mention even when it is gone — deleting a camera should not silently
    /// delete the rule that mentions it.</summary>
    private async Task LoadCamerasAsync()
    {
        var live = await _link.CamerasAsync();
        var names = (live?.Select(c => c.Name) ?? Enumerable.Empty<string>())
            .Concat(_prefs.Cameras.Keys)
            .Concat(_prefs.Offline)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _cameras.Items.Clear();
            _cameras.Items.AddRange(names.Cast<object>().ToArray());
            if (_cameras.Items.Count > 0) _cameras.SelectedIndex = 0;
            else _backend.Text += "  (no cameras returned by the server)";
        });
    }

    private void ShowCamera(string? name)
    {
        _loading = true;
        _current = name;
        var wanted = name != null && _prefs.Cameras.TryGetValue(name, out var list) ? list : new List<string>();
        for (int i = 0; i < _labels.Items.Count; i++)
            _labels.SetItemChecked(i, wanted.Contains((string)_labels.Items[i], StringComparer.OrdinalIgnoreCase));
        _offline.Checked = name != null && _prefs.WantsOffline(name);
        SetLabelsEnabled(name != null);
        _loading = false;
    }

    private void SetLabelsEnabled(bool on)
    {
        _labels.Enabled = on;
        _offline.Enabled = on;
    }

    /// <summary>Reads the check states back into the rules. Runs after the ItemCheck
    /// event has been applied (hence the BeginInvoke at the subscription), so the
    /// list and the model cannot drift by one click.</summary>
    private void CaptureLabels(ItemCheckEventArgs _)
    {
        if (_loading || _current == null) return;
        var chosen = _labels.CheckedItems.Cast<string>().ToList();
        if (chosen.Count == 0) _prefs.Cameras.Remove(_current);
        else _prefs.Cameras[_current] = chosen;
    }

    private void CaptureOffline()
    {
        if (_loading || _current == null) return;
        _prefs.Offline.RemoveAll(c => string.Equals(c, _current, StringComparison.OrdinalIgnoreCase));
        if (_offline.Checked) _prefs.Offline.Add(_current);
    }

    private void ShowTest()
    {
        var alert = new Alert(AlertKind.Detection, "neolink-test",
            "Test notification",
            $"This is how alerts from {_settings.ServerUrl} will look.", DeepLink: "/");
        _toaster.Show(alert, Snapshot(), null);
    }

    /// <summary>The this-PC settings as currently typed, without committing them —
    /// so the test notification honours the sound and click boxes before Save.</summary>
    private DesktopSettings Snapshot() => new()
    {
        ServerUrl = _settings.ServerUrl,
        Sound = _sound.Checked,
        ShowThumbnail = _thumb.Checked,
        ClickOpensEvent = _click.Checked,
    };

    private void Persist()
    {
        _settings.NotificationsEnabled = _onThisPc.Checked;
        _settings.PollSeconds = (int)_poll.Value;
        _settings.Sound = _sound.Checked;
        _settings.ShowThumbnail = _thumb.Checked;
        _settings.ClickOpensEvent = _click.Checked;
        _settings.QuietSilencesSystem = _quietSystem.Checked;
        _settings.QuietFrom = _quiet.Checked ? _quietFrom.Value.ToString("HH:mm") : null;
        _settings.QuietTo = _quiet.Checked ? _quietTo.Value.ToString("HH:mm") : null;

        _prefs.Enabled = _onAccount.Checked;
        _prefs.CooldownSeconds = (int)_cooldown.Value;
        _prefs.SysStorage = _sysStorage.Checked;
        _prefs.SysOverload = _sysOverload.Checked;
        _prefs.SysWriteFailure = _sysWrite.Checked;

        _settings.Save();
        // Fire and forget: the local cache is already written, so a server that is
        // briefly down cannot lose the edit — the next successful poll pushes it.
        _ = _engine.SavePrefsAsync(_prefs);
    }
}
