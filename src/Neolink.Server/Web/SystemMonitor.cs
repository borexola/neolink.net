// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using Neolink.Recording;

namespace Neolink.Web;

/// <summary>One point-in-time resource sample of the server process and its storage disk.</summary>
public readonly record struct SystemSample(
    long UnixMs,
    double CpuPercent,       // this process, normalized to all cores (0..100)
    long WorkingSetBytes,    // OS view of the process
    long ManagedHeapBytes,   // GC view
    double AllocMbPerSec,    // managed allocation rate
    int Threads,
    int Handles,             // OS handles / file descriptors (0 where unsupported)
    long DiskTotalBytes,     // volume holding the recordings (or state dir)
    long DiskFreeBytes,
    long RecordingsBytes,    // total size of the recordings tree; -1 = recording off
    int Viewers,             // external watchers only (RTSP sessions + web players)
    int RecordingCameras,    // cameras writing 24/7 footage right now
    double StorageMbPerSec,  // recorder bytes handed to disk (clips+previews+segments)
    long StorageFiles);      // recording files completed since the server started

/// <summary>
/// Samples the server's resource usage every 2 seconds into a fixed ring buffer
/// (one hour deep) for the web UI's monitor page. Everything is measured from
/// inside the process — no perf counters, no OS-specific dependencies — so it
/// works the same on Windows, Linux and in containers.
/// </summary>
public sealed class SystemMonitor
{
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(2);
    private const int Capacity = 1800;                    // 1 hour at 2s
    private const int RecordingsSizeEveryTicks = 30;      // walking the tree is expensive: once a minute

    private readonly string _diskProbePath;
    private readonly string? _recordingsRoot;
    private readonly Func<int> _viewerCount;
    private readonly Func<int> _recordingCameras;
    private readonly object _gate = new();
    private readonly SystemSample[] _ring = new SystemSample[Capacity];
    private int _count;
    private int _next;

    private TimeSpan _prevCpu;
    private DateTime _prevWall;
    private long _prevAlloc;
    private long _prevStorageBytes;
    private long _recordingsBytes = -1;
    private int _tick;

    private static readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;

    /// <param name="diskProbePath">Directory whose volume is reported (recordings root, else state dir).</param>
    /// <param name="recordingsRoot">Recordings tree to size, or null when recording is off.</param>
    /// <param name="viewerCount">External watchers across all stream hubs (recorders excluded).</param>
    /// <param name="recordingCameras">Cameras actively writing 24/7 footage.</param>
    public SystemMonitor(string diskProbePath, string? recordingsRoot,
        Func<int> viewerCount, Func<int>? recordingCameras = null)
    {
        _diskProbePath = diskProbePath;
        _recordingsRoot = recordingsRoot;
        _viewerCount = viewerCount;
        _recordingCameras = recordingCameras ?? (() => 0);
    }

    /// <summary>Static facts for the monitor page header (measured once).</summary>
    public object Info() => new
    {
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        processors = Environment.ProcessorCount,
        // In containers this is the cgroup limit, which is what actually matters.
        machineMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        startUtcMs = StartedUtc.ToUnixTimeMilliseconds(),
        disk = SafeVolumeName(),
        samplePeriodMs = (long)SamplePeriod.TotalMilliseconds,
    };

    /// <summary>Samples newer than <paramref name="afterUnixMs"/>, oldest first.</summary>
    public List<SystemSample> Since(long afterUnixMs)
    {
        lock (_gate)
        {
            var result = new List<SystemSample>(_count);
            for (int i = 0; i < _count; i++)
            {
                var s = _ring[(_next - _count + i + Capacity) % Capacity];
                if (s.UnixMs > afterUnixMs) result.Add(s);
            }
            return result;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var proc = Process.GetCurrentProcess();
        _prevCpu = proc.TotalProcessorTime;
        _prevWall = DateTime.UtcNow;
        _prevAlloc = GC.GetTotalAllocatedBytes();
        _prevStorageBytes = StorageMetrics.BytesWritten;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(SamplePeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { TakeSample(proc); }
            catch (Exception ex) { Log.Debug($"Monitor: sample failed: {ex.Message}"); }
        }
    }

    private void TakeSample(Process proc)
    {
        proc.Refresh();
        var now = DateTime.UtcNow;
        var cpuTime = proc.TotalProcessorTime;
        double elapsed = (now - _prevWall).TotalSeconds;
        double cpu = elapsed <= 0 ? 0 : Math.Clamp(
            (cpuTime - _prevCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
        _prevCpu = cpuTime;
        _prevWall = now;

        long alloc = GC.GetTotalAllocatedBytes();
        double allocRate = elapsed <= 0 ? 0 : Math.Max(0, alloc - _prevAlloc) / elapsed / (1024.0 * 1024.0);
        _prevAlloc = alloc;

        long storageBytes = StorageMetrics.BytesWritten;
        double storageRate = elapsed <= 0 ? 0
            : Math.Max(0, storageBytes - _prevStorageBytes) / elapsed / (1024.0 * 1024.0);
        _prevStorageBytes = storageBytes;

        long diskTotal = 0, diskFree = 0;
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(_diskProbePath));
            diskTotal = drive.TotalSize;
            diskFree = drive.AvailableFreeSpace;
        }
        catch { /* unmapped/network volume: leave zeros */ }

        // Sizing the recordings tree walks every file — do it once a minute, keep the last answer.
        if (_recordingsRoot != null && _tick++ % RecordingsSizeEveryTicks == 0)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(_recordingsRoot, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                _recordingsBytes = total;
            }
            catch { /* keep the previous figure */ }
        }

        int handles = 0;
        try { handles = proc.HandleCount; } catch { }
        int viewers = 0;
        try { viewers = _viewerCount(); } catch { }
        int recCams = 0;
        try { recCams = _recordingCameras(); } catch { }

        var sample = new SystemSample(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Math.Round(cpu, 2),
            proc.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            Math.Round(allocRate, 2),
            proc.Threads.Count,
            handles,
            diskTotal,
            diskFree,
            _recordingsBytes,
            viewers,
            recCams,
            Math.Round(storageRate, 2),
            StorageMetrics.FilesCompleted);

        lock (_gate)
        {
            _ring[_next] = sample;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    private string? SafeVolumeName()
    {
        try { return new DriveInfo(Path.GetFullPath(_diskProbePath)).Name; }
        catch { return null; }
    }
}
