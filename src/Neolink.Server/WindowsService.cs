// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Runtime.InteropServices;

namespace Neolink;

/// <summary>
/// Makes the server a real Windows service (--service) without taking a
/// dependency: the Service Control Manager speaks a small C API —
/// StartServiceCtrlDispatcher, a handler callback, SetServiceStatus — and the
/// packages that wrap it (ServiceBase, Microsoft.Extensions.Hosting.WindowsServices)
/// exist to hide these ~10 P/Invokes. The project's no-third-party rule says:
/// write the 10 P/Invokes.
///
/// The shape is inverted from the textbook service. Normally ServiceMain IS the
/// program; here the program is Program.cs's top-level flow, which cannot be
/// re-entered from a callback. So the dispatcher runs on a side thread, its
/// ServiceMain does nothing but report status and relay the SCM's stop request
/// into the same CancellationTokenSource Ctrl+C uses, and the main thread runs
/// the server exactly as it would in a console. One code path, two launchers.
///
/// Launched from a console anyway (testing, or someone runs the installed
/// command by hand), the dispatcher fails with ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
/// and <see cref="TryStart"/> says so and reports false — the caller keeps
/// running as a plain console app, which is exactly what a person at a prompt
/// wanted.
/// </summary>
internal static class WindowsService
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int StartPending = 2, StopPending = 3, Running = 4, Stopped = 1;
    private const int AcceptStop = 1, AcceptShutdown = 4;
    private const int ControlStop = 1, ControlShutdown = 5, ControlInterrogate = 4;
    private const int ErrorNotConnected = 1063;         // ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
    private const int ErrorCallNotImplemented = 1051;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                   ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? Name;
        public ServiceMainDelegate? Main;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerExDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerExDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    // The SCM keeps calling these for the life of the process; delegates that
    // only live in a local would be collected under it.
    private static readonly ServiceMainDelegate MainDelegate = ServiceMain;
    private static readonly HandlerExDelegate HandlerDelegate = Handler;

    private static IntPtr _statusHandle;
    private static ServiceStatus _status;
    private static readonly object StatusGate = new();
    private static readonly ManualResetEventSlim Decided = new();   // connected-or-not is known
    private static readonly ManualResetEventSlim Exited = new();    // main flow finished, ServiceMain may return
    private static bool _connected;
    private static volatile bool _stopRequested;
    private static Action? _onStop;

    /// <summary>Where the SCM's stop lands: Program.cs points this at the same
    /// shutdown CancellationTokenSource Ctrl+C cancels, as soon as it exists. A
    /// stop that arrives first (a very fast "sc stop") is latched and delivered
    /// on attach, so it cannot fall between the two.</summary>
    public static Action OnStop
    {
        set
        {
            _onStop = value;
            if (_stopRequested) value();
        }
    }

    /// <summary>Connects to the SCM. True: this process was launched as a service
    /// and now reports RUNNING — the caller must call <see cref="NotifyStopped"/>
    /// (or just exit; a ProcessExit hook covers every early-return path) when done.
    /// False: no SCM on the other end, keep behaving like a console app.</summary>
    public static bool TryStart()
    {
        int dispatcherError = 0;
        var thread = new Thread(() =>
        {
            var table = new[]
            {
                new ServiceTableEntry { Name = "Neolink.NET", Main = MainDelegate },
                new ServiceTableEntry { Name = null, Main = null },
            };
            // Blocks until the service stops when launched by the SCM; returns
            // false immediately when launched from a console.
            if (!StartServiceCtrlDispatcherW(table))
                dispatcherError = Marshal.GetLastWin32Error();
            Decided.Set();
        })
        { IsBackground = true, Name = "neolink-scm" };
        thread.Start();

        // ServiceMain fires within milliseconds of a successful dispatch; the
        // timeout only guards against an SCM that never calls back at all.
        Decided.Wait(TimeSpan.FromSeconds(30));
        if (_connected)
        {
            // Every exit path — including Fail() before the server is built —
            // must end at SERVICE_STOPPED, or the SCM logs the death as a crash.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                NotifyStopped(Environment.ExitCode);
            return true;
        }
        if (dispatcherError != 0 && dispatcherError != ErrorNotConnected)
            Log.Warn($"--service: the service dispatcher failed (win32 error {dispatcherError}); running as a console app");
        else
            Log.Info("--service: not launched by the service control manager — running as a console app");
        return false;
    }

    private static volatile bool _exitForRestart;

    /// <summary>A UI-requested restart while running as a service. The SCM has no
    /// "restart me" call and never revives a CLEAN stop — its recovery actions
    /// (which the installer configures to restart the service) fire when the
    /// process dies WITHOUT reporting SERVICE_STOPPED. So a restart-intent exit
    /// deliberately skips that report and lets the process death be the signal:
    /// the SCM logs an unexpected termination and brings the service back up.
    /// (Reporting STOPPED with an error code only counts as a failure behind
    /// SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, which the installer cannot set.)</summary>
    public static void ExitForRestart() => _exitForRestart = true;

    /// <summary>The server has wound down: tell the SCM, with the process's exit
    /// code so a failed start shows as failed. Safe to call more than once.</summary>
    public static void NotifyStopped(int exitCode)
    {
        if (!_connected || Exited.IsSet) return;
        // Restart path: no SERVICE_STOPPED and ServiceMain stays parked — the
        // process death itself must be what the SCM sees. See ExitForRestart.
        if (_exitForRestart) return;
        Exited.Set();
        Report(Stopped, accepted: 0, win32ExitCode: exitCode == 0 ? 0 : 1066 /* ERROR_SERVICE_SPECIFIC_ERROR */,
            serviceExitCode: exitCode);
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW("Neolink.NET", HandlerDelegate, IntPtr.Zero);
        _connected = _statusHandle != IntPtr.Zero;
        Decided.Set();
        if (!_connected) return;
        Report(StartPending, accepted: 0, waitHint: 30_000);
        // RUNNING right away rather than after the cameras connect: cameras keep
        // reconnecting for the process's whole life, so there is no later moment
        // that is more "started" than this one.
        Report(Running, AcceptStop | AcceptShutdown);
        // Held open so the dispatcher keeps relaying controls; NotifyStopped has
        // already told the SCM we are gone by the time this returns.
        Exited.Wait();
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop or ControlShutdown:
                // The cameras' wind-down is a WhenAll over tasks that honour
                // cancellation within seconds; the hint is headroom, not hope.
                Report(StopPending, accepted: 0, waitHint: 60_000);
                _stopRequested = true;
                try { _onStop?.Invoke(); } catch { /* the stop must reach the SCM regardless */ }
                return 0;
            case ControlInterrogate:
                lock (StatusGate) SetServiceStatus(_statusHandle, ref _status);
                return 0;
            default:
                return ErrorCallNotImplemented;
        }
    }

    private static void Report(int state, int accepted, int waitHint = 0, int win32ExitCode = 0, int serviceExitCode = 0)
    {
        lock (StatusGate)
        {
            _status = new ServiceStatus
            {
                ServiceType = ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = accepted,
                Win32ExitCode = win32ExitCode,
                ServiceSpecificExitCode = serviceExitCode,
                WaitHint = waitHint,
            };
            SetServiceStatus(_statusHandle, ref _status);
        }
    }
}
