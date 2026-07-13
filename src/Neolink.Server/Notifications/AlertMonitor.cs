// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Recording;
using Neolink.Web;

namespace Neolink.Notifications;

/// <summary>A camera's health as the alert monitor sees it.</summary>
public readonly record struct CameraHealth(string Name, bool Online, bool Asleep);

/// <summary>
/// Polls the server's health every 30s and reports each critical condition to the
/// <see cref="Notifier"/> (which handles edge-detection, de-duplication and email).
/// Level-triggered and idempotent: it just states the CURRENT truth each tick, so
/// there is no state to get out of sync. Every tick is wrapped — a probe hiccup
/// can never stop the loop or touch anything else.
/// </summary>
public sealed class AlertMonitor
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);
    private const double OverloadCpuPercent = 90;
    private static readonly TimeSpan OverloadWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WriteErrorWindow = TimeSpan.FromMinutes(2);

    private readonly Notifier _notifier;
    private readonly StorageLocations? _storage;
    private readonly SystemMonitor? _monitor;
    private readonly Func<IEnumerable<CameraHealth>> _cameras;
    private readonly RecordingHealth? _recording;
    private readonly Dictionary<string, DateTime> _offlineSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _server;

    public AlertMonitor(Notifier notifier, string serverName, StorageLocations? storage,
        SystemMonitor? monitor, Func<IEnumerable<CameraHealth>> cameras, RecordingHealth? recording)
    {
        _notifier = notifier;
        _server = string.IsNullOrWhiteSpace(serverName) ? "server" : serverName;
        _storage = storage;
        _monitor = monitor;
        _cameras = cameras;
        _recording = recording;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Log.Debug($"Alert monitor tick failed: {ex.Message}"); }
            try { await Task.Delay(Period, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Tick()
    {
        var cfg = _notifier.Store.Snapshot();
        if (!cfg.Enabled) return;

        if (cfg.AlertStorage && _storage != null) ReportStorage();
        if (cfg.AlertOverload && _monitor != null) ReportOverload();
        if (cfg.AlertCameraOffline) ReportCameras(cfg);
        if (cfg.AlertWriteFailure && _recording != null) ReportWriteFailures();
    }

    private void ReportStorage()
    {
        var full = _storage!.Sample().FirstOrDefault(s => s.Full);
        _notifier.Report("storage", full != null,
            () => new Alert("storage", false, $"{_server}: storage full",
                "Recording has stopped — a drive is out of space",
                $"The \"{full!.Label}\" volume ({full.Path}) has no room left, so recording to it has halted. " +
                "Free up space, or lower retention / enable archiving to another drive.",
                $"{full!.Label}: {Gb(full.FreeBytes)} free of {Gb(full.TotalBytes)}"),
            () => new Alert("storage", true, $"{_server}: storage recovered",
                "Recording resumed — storage has space again",
                "A recording drive that had filled up now has free space, and recording has resumed."));
    }

    private void ReportOverload()
    {
        var since = DateTimeOffset.UtcNow.Subtract(OverloadWindow).ToUnixTimeMilliseconds();
        var samples = _monitor!.Since(since);
        double avg = samples.Count > 0 ? samples.Average(s => s.CpuPercent) : 0;
        bool overloaded = samples.Count >= 5 && avg >= OverloadCpuPercent;
        _notifier.Report("overload", overloaded,
            () => new Alert("overload", false, $"{_server}: sustained high load",
                "The server has been running at very high CPU",
                "CPU has stayed near maximum for several minutes — live streams or recording may be lagging. " +
                "Check for a stuck camera reconnect loop, or a host that's overcommitted.",
                $"~{avg:0}% CPU averaged over {OverloadWindow.TotalMinutes:0} minutes"),
            () => new Alert("overload", true, $"{_server}: load back to normal",
                "Server load has returned to normal", "CPU usage has dropped back to normal levels."));
    }

    private void ReportCameras(NotificationSettings cfg)
    {
        foreach (var cam in _cameras())
        {
            int minutes = cfg.OfflineMinutesFor(cam.Name);
            bool active;
            if (cam.Asleep || minutes <= 0 || cam.Online)
            {
                // Battery doze is intentional, not an outage; minutes<=0 disables it.
                _offlineSince.Remove(cam.Name);
                active = false;
            }
            else
            {
                if (!_offlineSince.ContainsKey(cam.Name)) _offlineSince[cam.Name] = DateTime.UtcNow;
                active = DateTime.UtcNow - _offlineSince[cam.Name] >= TimeSpan.FromMinutes(minutes);
            }

            var name = cam.Name; var min = minutes;
            _notifier.Report($"camera:{name}", active,
                () => new Alert($"camera:{name}", false, $"{_server}: {name} is offline",
                    $"Camera \"{name}\" is offline",
                    $"{name} has been unreachable for more than {min} minute(s). Check its power and network connection.",
                    name),
                () => new Alert($"camera:{name}", true, $"{_server}: {name} is back online",
                    $"Camera \"{name}\" is back online", $"{name} has reconnected and is streaming again.", name));
        }
    }

    private void ReportWriteFailures()
    {
        var bad = _recording!.CamerasWithRecentErrors(WriteErrorWindow);
        _notifier.Report("writefail", bad.Count > 0,
            () => new Alert("writefail", false, $"{_server}: recording write errors",
                "Recording is failing to write to disk",
                "The server hit errors writing footage to disk — a failing or disconnected drive, or a permissions " +
                "problem. Recent recordings may be incomplete. (This is different from a drive simply being full.)",
                string.Join(", ", bad)),
            () => new Alert("writefail", true, $"{_server}: recording writes recovered",
                "Recording writes have recovered", "Footage is being written to disk normally again."));
    }

    private static string Gb(long bytes) =>
        bytes >= 1024L * 1024 * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024 * 1024):0.#} TB"
        : $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
}
