// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;

namespace Neolink.Desktop;

internal static class Program
{
    /// <summary>Per-SESSION names, not global ones: two people signed in to the
    /// same PC each get their own shell, and the installer's "is it running?"
    /// check looks at processes rather than these.</summary>
    private const string MutexName = @"Local\Neolink.NET.Desktop.Instance";
    private const string WakeEventName = @"Local\Neolink.NET.Desktop.Wake";

    /// <summary>Held for the process's whole life — EXCEPT during RestartSelf,
    /// which must release it before spawning the successor or the new process
    /// would see "already running" and quietly exit.</summary>
    private static Mutex? _instanceMutex;

    [STAThread]
    private static int Main(string[] args)
    {
        // Invariant FORMATTING without invariant MODE: the csproj must not set
        // InvariantGlobalization (WinForms crashes on keyboard-layout switches
        // under it — see the csproj note), so the culture is pinned here instead
        // and every format/parse behaves the same on every machine.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
            System.Globalization.CultureInfo.InvariantCulture;

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);
            return SelfTest.Run() ? 0 : 1;
        }

        if (args.Any(a => a is "--version" or "-v" or "--test-notification"))
            AttachConsole(-1);   // -1 = ATTACH_PARENT_PROCESS

        if (args.Any(a => a is "--version" or "-v"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");
            return 0;
        }

        // Support aid: "my cameras trip but nothing appears" is almost always
        // Windows' own notification settings — Focus Assist, or the app switched
        // off under Settings > Notifications. This proves the path end to end
        // without waiting for a real event, and says which mechanism was used.
        if (args.Any(a => a.Equals("--test-notification", StringComparison.OrdinalIgnoreCase)))
            return TestNotification();

        // A second launch (double-clicking the shortcut while it sits in the tray)
        // should raise the window that already exists, not start a rival that
        // fights it for the same WebView2 data folder. Held in a field, not a
        // using: it lives exactly as long as the process, unless RestartSelf
        // hands it over early.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            WakeExistingInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        var settings = DesktopSettings.Load();

        // An upgrade can move the executable; a Run entry pointing at the old path
        // would silently stop starting the app. Rewrite it on every launch.
        Autostart.RepairIfStale(settings.StartWithWindows, Application.ExecutablePath);

        if (!settings.Configured)
        {
            using var connect = new ConnectForm(settings, reconfiguring: false);
            if (connect.ShowDialog() != DialogResult.OK) return 0;
            settings = DesktopSettings.Load();
        }

        using var link = new ServerLink(settings);
        bool startHidden = settings.StartMinimized
                           && args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        using var form = new MainForm(settings, link, startHidden);
        using var wake = ListenForWake(form);

        // ApplicationContext rather than Run(form): the window can be hidden for
        // the whole session and the message loop must not end when it closes.
        Application.Run(new ShellContext(form, startHidden));
        return 0;
    }

    /// <summary>Keeps the message loop alive independently of the window, so
    /// "close to tray" is a real close and the app still runs.</summary>
    private sealed class ShellContext : ApplicationContext
    {
        private readonly MainForm _form;

        public ShellContext(MainForm form, bool startHidden)
        {
            _form = form;
            _form.Disposed += (_, _) => ExitThread();
            // Showing then hiding is what gives the form a window handle, which the
            // tray icon's click handlers and the WebView both need.
            _form.Show();
            if (startHidden)
            {
                _form.WindowState = FormWindowState.Minimized;
                _form.Visible = false;
                _form.ShowInTaskbar = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _form.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Shows one notification and exits, reporting which mechanism carried
    /// it. Needs a message loop briefly: both a toast's activation callback and a
    /// tray balloon are delivered as window messages.</summary>
    private static int TestNotification()
    {
        ApplicationConfiguration.Initialize();
        var settings = DesktopSettings.Load();
        using var tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true, Text = "Neolink.NET" };
        using var toaster = new Toaster(tray, Application.ExecutablePath);
        toaster.Show(new Alert(AlertKind.Detection, "neolink-test", "Neolink.NET test notification",
            "If you can see this, alerts from your server will reach you.", DeepLink: "/"), settings, null);

        Console.WriteLine(toaster.RichToasts
            ? "sent as a Windows toast (Action Center)"
            : "sent as a tray balloon - no AUMID shortcut, so rich toasts are unavailable");

        // Long enough for the shell to render it, short enough to be a command.
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }

        var held = toaster.HistoryCount();
        if (held > 0)
            Console.WriteLine($"Windows accepted it: {held} notification(s) in this app's Action Center history.");
        else if (toaster.RichToasts)
            Console.WriteLine("Windows did NOT keep it. Check Settings > System > Notifications " +
                              "(and Focus assist / Do not disturb) for Neolink.NET Desktop.");

        tray.Visible = false;
        return 0;
    }

    /// <summary>A WinExe has no console of its own. Borrow the one that launched it
    /// so the diagnostic commands print where the person running them can see.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>Signals the running instance to come to the front.</summary>
    private static void WakeExistingInstance()
    {
        try
        {
            using var wake = EventWaitHandle.OpenExisting(WakeEventName);
            wake.Set();
        }
        catch { /* it is shutting down, or was never listening: nothing to raise */ }
    }

    /// <summary>Watches for a second launch and raises the window when one happens.</summary>
    private static IDisposable ListenForWake(MainForm form)
    {
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (!wake.WaitOne(500)) continue;
                try { form.BeginInvoke(() => form.ShowFromAnotherInstance()); }
                catch { /* the form is going away */ }
            }
        })
        { IsBackground = true, Name = "neolink-wake" };
        thread.Start();
        return new Disposer(() => { cts.Cancel(); wake.Dispose(); cts.Dispose(); });
    }

    private sealed class Disposer(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    /// <summary>Restarts the process — the honest answer to "the server address
    /// changed", which invalidates the WebView's session, the API token and the
    /// alert baseline all at once. The single-instance mutex is released FIRST:
    /// the successor starts while this process is still winding down, and it must
    /// see itself as the first instance rather than waking a dying one.</summary>
    public static void RestartSelf()
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* not the owning thread: still released at exit */ }
        try { _instanceMutex?.Dispose(); } catch { }
        _instanceMutex = null;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath)
            {
                UseShellExecute = true,
            });
        }
        catch { /* fall through and just exit; the user can start it again */ }
        Application.Exit();
    }

    /// <summary>Last-resort crash handling: a tray app that dies silently looks
    /// like a tray app that is working. Say so, and leave a file behind.</summary>
    private static void ReportCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var path = Path.Combine(DesktopSettings.Dir, "crash.log");
            Directory.CreateDirectory(DesktopSettings.Dir);
            File.AppendAllText(path, $"[{DateTime.Now:u}] {ex}\r\n\r\n");
            MessageBox.Show($"Neolink.NET Desktop hit an unexpected error.\r\n\r\n{ex.Message}\r\n\r\n" +
                            $"Details were written to {path}",
                "Neolink.NET", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* nothing left to try */ }
    }
}
