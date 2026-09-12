// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Security.Cryptography;

namespace Neolink.Detect;

/// <summary>One file the browser needs: where it downloads from, and the sha256
/// it must hash to before it is ever served.</summary>
public sealed record DetectAsset(string FileName, string Url, string Sha256, long Bytes);

/// <summary>
/// The object detector runs in the BROWSER — the server never decodes a frame for
/// it — so all this holds is the files the page loads: ONNX Runtime Web and the
/// model. They are not in the repository or the image: 35 MB every install would
/// carry for a preview feature only some switch on. They download once into the
/// state dir the first time the feature is enabled, pinned by sha256 — a file
/// that does not hash right is deleted rather than served. An install with no
/// internet can drop the same filenames into a "detect-assets" folder next to the
/// executable instead, which is then used as-is.
/// </summary>
public sealed class DetectAssets
{
    private const string OrtVersion = "1.29.0";
    private const string Cdn = "https://cdn.jsdelivr.net/npm/onnxruntime-web@" + OrtVersion + "/dist/";

    /// <summary>The runtime: the WebGPU bundle plus the wasm it loads (which also
    /// carries the CPU fallback, so one pair covers both execution providers).</summary>
    public static readonly DetectAsset Runtime = new(
        "ort.webgpu.min.js", Cdn + "ort.webgpu.min.js",
        "2d0bac4406b97d87c2ee2f279a0e6ad089567e62283d41e7e535a40e5c03d2f5", 66_416);

    public static readonly DetectAsset RuntimeLoader = new(
        "ort-wasm-simd-threaded.asyncify.mjs", Cdn + "ort-wasm-simd-threaded.asyncify.mjs",
        "5d25483158d53d8f34d0e9c06a654d56c8dca4ebdf370ea0982ef11315a00e0e", 51_407);

    public static readonly DetectAsset RuntimeWasm = new(
        "ort-wasm-simd-threaded.asyncify.wasm", Cdn + "ort-wasm-simd-threaded.asyncify.wasm",
        "503d17cb7411b79781b9fad1cf0978f03cf06b050c7d399c730e914f473bf549", 25_749_873);

    /// <summary>YOLOv10-nano as onnx-community publishes it (AGPL-3.0, same licence
    /// as this project). v10 rather than a v8: it is end-to-end, so the page reads
    /// 300 finished boxes off the output instead of running non-maximum suppression
    /// over 8400 candidates in JavaScript on every frame.</summary>
    public static readonly DetectAsset Model = new(
        "yolov10n.onnx",
        "https://huggingface.co/onnx-community/yolov10n/resolve/"
            + "57657320425ee34056408a57ad9d29c4d4815bd8/onnx/model.onnx",
        "a77dd863933f184a19e84361c64b788228a7c7dacc2c78939239a96ad3efca3b", 9_386_116);

    public static readonly DetectAsset[] All = { Runtime, RuntimeLoader, RuntimeWasm, Model };

    public static long TotalBytes => All.Sum(a => a.Bytes);

    /// <summary>"ready" | "missing" | "downloading" | "failed".</summary>
    public sealed record Status(string State, int Percent, string? Error);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly string _bundledDir;
    private readonly string _stateDir;
    private readonly bool _allowDownload;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastMiss = new(StringComparer.Ordinal);
    private Task? _download;
    private int _percent;
    private string? _error;

    /// <param name="allowDownload">False makes every fetch a no-op, so missing files
    /// stay "missing" instead of becoming "downloading" — the self-tests pass false,
    /// since a test suite must not reach the network.</param>
    public DetectAssets(string stateDir, bool allowDownload = true)
    {
        _bundledDir = Path.Combine(AppContext.BaseDirectory, "detect-assets");
        _stateDir = Path.Combine(stateDir, "detect-assets");
        _allowDownload = allowDownload;
    }

    /// <summary>The verified file to serve for this name, or null when it is absent
    /// or fails its checksum. Unknown names return null — this is what a public URL
    /// resolves against, so it can only ever name one of <see cref="All"/>.</summary>
    public string? Locate(string fileName)
    {
        var asset = All.FirstOrDefault(a => a.FileName.Equals(fileName, StringComparison.Ordinal));
        return asset == null ? null : Locate(asset);
    }

    public string? Locate(DetectAsset asset)
    {
        lock (_gate) return LocateLocked(asset);
    }

    private string? LocateLocked(DetectAsset asset)
    {
        if (_verified.TryGetValue(asset.FileName, out var known)) return known;
        // A miss is re-checked at most every few seconds: the status endpoint polls
        // while a download runs, and hashing 35 MB on each poll would be silly.
        if (_lastMiss.TryGetValue(asset.FileName, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(5))
            return null;
        foreach (var dir in new[] { _bundledDir, _stateDir })
        {
            var path = Path.Combine(dir, asset.FileName);
            if (Verified(path, asset))
            {
                _verified[asset.FileName] = path;
                return path;
            }
        }
        _lastMiss[asset.FileName] = DateTime.UtcNow;
        return null;
    }

    public bool Ready
    {
        get { lock (_gate) return All.All(a => LocateLocked(a) != null); }
    }

    public Status Current()
    {
        if (Ready) return new Status("ready", 100, null);
        lock (_gate)
        {
            if (_download is { IsCompleted: false }) return new Status("downloading", _percent, null);
            return _error != null ? new Status("failed", 0, _error) : new Status("missing", 0, null);
        }
    }

    /// <summary>Starts the download unless one is running or the files are already
    /// there. Returns the running task; never throws.</summary>
    public Task EnsureAsync(CancellationToken ct = default)
    {
        if (Ready || !_allowDownload) return Task.CompletedTask;
        lock (_gate)
        {
            if (_download is { IsCompleted: false }) return _download;
            _error = null;
            _percent = 0;
            return _download = Task.Run(() => DownloadAsync(ct), CancellationToken.None);
        }
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            long done = 0;
            foreach (var asset in All)
            {
                bool have;
                lock (_gate) have = LocateLocked(asset) != null;
                if (!have)
                {
                    long offset = done;
                    await FetchAsync(asset,
                        got => { lock (_gate) _percent = (int)Math.Clamp((offset + got) * 100 / TotalBytes, 0, 99); },
                        ct).ConfigureAwait(false);
                    lock (_gate) _verified[asset.FileName] = Path.Combine(_stateDir, asset.FileName);
                }
                done += asset.Bytes;
            }
            lock (_gate) _percent = 100;
            Log.Info("Live object boxes: browser detector files ready");
        }
        catch (Exception ex)
        {
            lock (_gate) _error = Log.Flatten(ex);
            Log.Warn($"Live object boxes: download failed — {Log.Flatten(ex)}");
        }
    }

    private async Task FetchAsync(DetectAsset asset, Action<long> progress, CancellationToken ct)
    {
        var path = Path.Combine(_stateDir, asset.FileName);
        var tmp = path + ".part";
        Log.Info($"Live object boxes: downloading {asset.FileName} ({asset.Bytes / 1_000_000.0:0.#} MB)");
        using (var res = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                got += n;
                progress(got);
            }
        }
        if (!Verified(tmp, asset))
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"{asset.FileName} did not match its published checksum");
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static bool Verified(string path, DetectAsset asset)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != asset.Bytes) return false;
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
