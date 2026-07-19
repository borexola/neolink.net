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
/// Samples the server's resource usage every 2 seconds for the web UI's monitor
/// page. Two fixed rings hold the history: full 2s detail for the last hour,
/// plus one-minute aggregates covering the last 24 hours (≈160 KB total — the
/// deep history is effectively free). Everything is measured from inside the
/// process — no perf counters, no OS-specific dependencies — so it works the
/// same on Windows, Linux and in containers.
/// </summary>
public sealed class SystemMonitor
{
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan History = TimeSpan.FromHours(24);
    private const int Capacity = 1800;                    // 1 hour at 2s (full detail)
    private const int MinuteCapacity = 1440;              // 24 hours at 1 min (aggregates)
    private const int TicksPerMinute = 30;                // fine samples folded into one aggregate
    private const int RecordingsSizeEveryTicks = 30;      // walking the tree is expensive: once a minute

    private readonly string _diskProbePath;
    private readonly string? _recordingsRoot;
    private readonly Func<int> _viewerCount;
    private readonly Func<int> _recordingCameras;
    private readonly Func<IEnumerable<(string Name, bool Online)>> _cameraStates;
    private readonly object _gate = new();
    private readonly SystemSample[] _ring = new SystemSample[Capacity];
    private int _count;
    private int _next;

    // Minute tier: rates are averaged over the minute (a spike still shows as a
    // raised average), gauges keep their last value. Feeds windows beyond 1 h.
    private readonly SystemSample[] _minuteRing = new SystemSample[MinuteCapacity];
    private int _minuteCount;
    private int _minuteNext;
    private double _aggCpu, _aggAlloc, _aggStorage;
    private int _aggN;

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
    /// <param name="cameraStates">Current online/offline state of every configured camera.</param>
    public SystemMonitor(string diskProbePath, string? recordingsRoot,
        Func<int> viewerCount, Func<int>? recordingCameras = null,
        Func<IEnumerable<(string Name, bool Online)>>? cameraStates = null)
    {
        _diskProbePath = diskProbePath;
        _recordingsRoot = recordingsRoot;
        _viewerCount = viewerCount;
        _recordingCameras = recordingCameras ?? (() => 0);
        _cameraStates = cameraStates ?? (() => []);
    }

    /// <summary>Uptime/outage history per camera, sampled on the same 2s tick.</summary>
    public CameraAvailability Availability { get; } = new();

    /// <summary>When this server process started (feeds the HA "Started" sensor).</summary>
    public static DateTimeOffset Started => StartedUtc;

    /// <summary>The most recent sample, if one has been taken yet.</summary>
    public SystemSample? Latest()
    {
        lock (_gate)
        {
            return _count == 0 ? null : _ring[(_next - 1 + Capacity) % Capacity];
        }
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
        historyMs = (long)History.TotalMilliseconds,
    };

    // Overload = sustained near-max CPU. These mirror the email AlertMonitor's
    // thresholds so the web "server overloaded" signal and the email alert agree.
    public const double OverloadCpuPercent = 90;
    public static readonly TimeSpan OverloadWindow = TimeSpan.FromMinutes(5);

    /// <summary>True when this process has averaged near-max CPU across the overload
    /// window — a few samples are required so a brief spike doesn't trip it.</summary>
    public bool Overloaded() =>
        OverloadedFrom(Since(DateTimeOffset.UtcNow.Subtract(OverloadWindow).ToUnixTimeMilliseconds()));

    /// <summary>Pure overload decision over a sample set (testable).</summary>
    public static bool OverloadedFrom(IReadOnlyList<SystemSample> samples) =>
        samples.Count >= 5 && samples.Average(s => s.CpuPercent) >= OverloadCpuPercent;

    /// <summary>
    /// Samples newer than <paramref name="afterUnixMs"/>, oldest first: minute
    /// aggregates for the deep past, full 2s detail once the fine ring covers it.
    /// </summary>
    public List<SystemSample> Since(long afterUnixMs)
    {
        lock (_gate)
        {
            var result = new List<SystemSample>(_count + _minuteCount);
            long fineOldest = _count == 0 ? long.MaxValue
                : _ring[(_next - _count + Capacity) % Capacity].UnixMs;
            for (int i = 0; i < _minuteCount; i++)
            {
                var s = _minuteRing[(_minuteNext - _minuteCount + i + MinuteCapacity) % MinuteCapacity];
                if (s.UnixMs > afterUnixMs && s.UnixMs < fineOldest) result.Add(s);
            }
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

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            foreach (var (name, online) in _cameraStates())
                Availability.Update(name, online, nowMs);
        }
        catch { /* availability is best-effort */ }

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

            // Fold this minute's rates into one aggregate for the 24h tier.
            _aggCpu += sample.CpuPercent;
            _aggAlloc += sample.AllocMbPerSec;
            _aggStorage += sample.StorageMbPerSec;
            if (++_aggN >= TicksPerMinute)
            {
                _minuteRing[_minuteNext] = sample with
                {
                    CpuPercent = Math.Round(_aggCpu / _aggN, 2),
                    AllocMbPerSec = Math.Round(_aggAlloc / _aggN, 2),
                    StorageMbPerSec = Math.Round(_aggStorage / _aggN, 2),
                };
                _minuteNext = (_minuteNext + 1) % MinuteCapacity;
                if (_minuteCount < MinuteCapacity) _minuteCount++;
                _aggCpu = _aggAlloc = _aggStorage = 0;
                _aggN = 0;
            }
        }
    }

    private string? SafeVolumeName()
    {
        try { return new DriveInfo(Path.GetFullPath(_diskProbePath)).Name; }
        catch { return null; }
    }
}
