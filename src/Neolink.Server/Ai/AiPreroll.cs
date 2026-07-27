// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using Neolink.Media;

namespace Neolink.Ai;

/// <summary>
/// A frozen copy of the recorder's pre-roll video at the instant an event
/// started: the compressed packets (references, not copies — a few hundred KB of
/// sub-stream for the seconds before the trigger) plus the codec parameters an
/// out-of-band decoder needs. Taken BEFORE StartClip drains the buffers into the
/// clip writer, because these seconds are the one part of the event the live
/// snapshot burst can never reach — and they usually contain whatever caused it.
/// </summary>
public sealed record AiPrerollVideo(VideoCodec Codec, byte[]? Vps, byte[]? Sps, byte[]? Pps,
    IReadOnlyList<(byte[] AnnexB, bool Keyframe, uint RtpTs)> Packets);

/// <summary>
/// Turns an <see cref="AiPrerollVideo"/> into a handful of JPEGs for the AI
/// frame set by piping the raw Annex-B stream through an ffmpeg found on PATH —
/// the server itself stays decoder-free, exactly like the thumbnail and
/// snapshot paths. No ffmpeg, no pre-roll frames: the feature quietly sits out
/// (one Info line says what installing it would add). Runs on the describe
/// worker only, never on the event path.
/// </summary>
public static class AiPreroll
{
    /// <summary>At most this many pre-roll frames join the set (oldest, spread,
    /// and always the newest — the frame nearest the trigger instant).</summary>
    public const int MaxFrames = 3;

    private static readonly Lazy<string?> FfmpegLazy = new(() =>
        Locate(Environment.GetEnvironmentVariable("NEOLINK_FFMPEG"),
               Environment.GetEnvironmentVariable("PATH")));

    /// <summary>The NEOLINK_FFMPEG env var wins when it points at an existing
    /// file (the escape hatch for nonstandard installs); otherwise the PATH is
    /// scanned for ffmpeg(.exe). Null = no ffmpeg, feature sits out.</summary>
    internal static string? Locate(string? envOverride, string? pathVar)
    {
        if (envOverride is { Length: > 0 })
        {
            try
            {
                if (File.Exists(envOverride)) return envOverride;
                Log.Warn($"NEOLINK_FFMPEG points at '{envOverride}', which does not " +
                         "exist — falling back to the PATH scan");
            }
            catch { /* unusable override: same fallback */ }
        }
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (pathVar ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* an unparsable PATH entry is somebody else's problem */ }
        }
        return null;
    }

    /// <summary>Full path of the ffmpeg binary, or null when none was found.</summary>
    public static string? FfmpegPath => FfmpegLazy.Value;

    private static int _missingLogged;

    /// <summary>
    /// Decodes the pre-roll and returns up to <see cref="MaxFrames"/> JPEGs,
    /// oldest first, each stamped with its real wall-clock instant (derived from
    /// the RTP timestamps, anchored to <paramref name="triggerUtc"/> = the last
    /// pre-roll packet). Empty on any failure — pre-roll frames are a bonus, and
    /// nothing about describing an event may ever depend on them.
    /// </summary>
    public static async Task<List<(DateTime Utc, byte[] Jpeg)>> ExtractAsync(
        AiPrerollVideo video, DateTime triggerUtc, CancellationToken ct)
    {
        var none = new List<(DateTime, byte[])>();
        if (FfmpegPath is not { } ffmpeg)
        {
            if (Interlocked.Exchange(ref _missingLogged, 1) == 0)
                Log.Info("AI describe: no ffmpeg found — event descriptions run without " +
                         "pre-roll frames (the seconds before the trigger). Installing ffmpeg " +
                         "on PATH (or pointing NEOLINK_FFMPEG at a binary) adds them " +
                         "automatically.");
            return none;
        }

        // A decoder must enter on a keyframe; drop anything before the first.
        int first = 0;
        while (first < video.Packets.Count && !video.Packets[first].Keyframe) first++;
        var packets = video.Packets.Skip(first).ToList();
        if (packets.Count == 0) return none;

        // Parameter sets first (raw NALs from the hub, so they need their own
        // start codes); keyframe packets usually repeat them inline, but
        // "usually" is not a decode guarantee.
        var stdinChunks = new List<byte[]>();
        byte[] startCode = { 0, 0, 0, 1 };
        foreach (var nal in new[] { video.Vps, video.Sps, video.Pps })
        {
            if (nal is not { Length: > 0 }) continue;
            stdinChunks.Add(startCode);
            stdinChunks.Add(nal);
        }
        foreach (var (annexB, _, _) in packets) stdinChunks.Add(annexB);

        try
        {
            var (outBytes, stderr) = await RunFfmpegAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", video.Codec == VideoCodec.H265 ? "hevc" : "h264",
                "-i", "pipe:0",
                // Height-based on purpose: a 16:9 frame lands at the classic
                // 640×360, while an ultra-wide dual-lens panorama keeps its
                // proportional width (5120×1552 → 1188×360) instead of being
                // crushed to 640×194 — where a rabbit on the lawn dissolved
                // into ~5 unfindable pixels (live 2026-07-27).
                "-vf", "scale=-2:360",
                "-q:v", "5",
                "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
            }, stdinChunks, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            var jpegs = SplitJpegs(outBytes);
            if (jpegs.Count == 0)
            {
                Log.Info($"AI pre-roll: ffmpeg produced no frames from {packets.Count} packet(s)" +
                          $"{(stderr.Length > 0 ? $": {stderr[..Math.Min(300, stderr.Length)]}" : "")}");
                return none;
            }

            // Each decoded frame's wall clock: the i-th of M outputs maps onto the
            // i-th of K fed packets proportionally (cam streams carry no B-frames,
            // so decode order IS display order; the lerp only absorbs non-VCL
            // packets that produced no picture). The LAST packet is the trigger.
            uint lastTs = packets[^1].RtpTs;
            var result = new List<(DateTime, byte[])>();
            foreach (int i in SpreadIndices(jpegs.Count, MaxFrames))
            {
                int p = jpegs.Count <= 1
                    ? packets.Count - 1
                    : (int)Math.Round(i * (double)(packets.Count - 1) / (jpegs.Count - 1));
                var utc = triggerUtc - TimeSpan.FromSeconds(
                    unchecked(lastTs - packets[p].RtpTs) / (double)FMp4.Timescale);
                result.Add((utc, jpegs[i]));
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info("AI pre-roll: ffmpeg exceeded its 20s deadline — skipped");
            return none;
        }
        catch (Exception ex)
        {
            Log.Info($"AI pre-roll extraction failed: {Log.Flatten(ex)}");
            return none;
        }
    }

    /// <summary>Decodes a run of self-contained keyframe access units (the
    /// stream tap's harvest — one IDR each, so every AU yields one picture)
    /// into model-sized JPEGs, each keeping its packet's wall-clock stamp.
    /// Output count normally matches input 1:1; a decoder hiccup falls back to
    /// proportional mapping so no frame ever wears another frame's time.
    /// Empty on any failure — the caller has its own fallbacks.</summary>
    public static async Task<List<(DateTime Utc, byte[] Jpeg)>> DecodeFramesAsync(
        VideoCodec codec, byte[]? vps, byte[]? sps, byte[]? pps,
        IReadOnlyList<(DateTime Utc, byte[] AnnexB)> aus, CancellationToken ct)
    {
        var none = new List<(DateTime, byte[])>();
        if (FfmpegPath is not { } ffmpeg || aus.Count == 0) return none;
        var stdinChunks = new List<byte[]>();
        byte[] startCode = { 0, 0, 0, 1 };
        foreach (var nal in new[] { vps, sps, pps })
        {
            if (nal is not { Length: > 0 }) continue;
            stdinChunks.Add(startCode);
            stdinChunks.Add(nal);
        }
        foreach (var (_, au) in aus) stdinChunks.Add(au);
        try
        {
            var (outBytes, stderr) = await RunFfmpegAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", codec == VideoCodec.H265 ? "hevc" : "h264",
                "-i", "pipe:0",
                "-vf", "scale=-2:360",
                "-q:v", "5",
                "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
            }, stdinChunks, TimeSpan.FromSeconds(Math.Max(20, aus.Count)), ct).ConfigureAwait(false);
            var jpegs = SplitJpegs(outBytes);
            if (jpegs.Count == 0)
            {
                Log.Info($"AI stream-tap decode: ffmpeg produced no frames from {aus.Count} AU(s)" +
                          $"{(stderr.Length > 0 ? $": {stderr[..Math.Min(300, stderr.Length)]}" : "")}");
                return none;
            }
            var result = new List<(DateTime, byte[])>();
            for (int i = 0; i < jpegs.Count; i++)
            {
                int p = jpegs.Count == aus.Count ? i
                    : jpegs.Count <= 1 ? aus.Count - 1
                    : (int)Math.Round(i * (double)(aus.Count - 1) / (jpegs.Count - 1));
                result.Add((aus[p].Utc, jpegs[i]));
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info("AI stream-tap decode: ffmpeg exceeded its deadline — skipped");
            return none;
        }
        catch (Exception ex)
        {
            Log.Info($"AI stream-tap decode failed: {Log.Flatten(ex)}");
            return none;
        }
    }

    /// <summary>A JPEG bigger than this is not the sub-stream thumbnail it was
    /// meant to be — some cameras answer the "small" snapshot command with the
    /// full-resolution picture (~5 MB each; a 50-frame event became a 234 MB
    /// payload and broke the endpoint's pipe, live 2026-07-26).</summary>
    public const int OversizeBytes = 300_000;

    /// <summary>Rescales every oversized JPEG in <paramref name="frames"/> to
    /// model size (360 tall, width proportional) through ONE ffmpeg pass, leaving small frames
    /// untouched and every timestamp in place. Returns the original list when
    /// ffmpeg is missing or anything goes wrong — shrinking is an optimization,
    /// never a gate; the describer's byte-capped parts carry the fallback.</summary>
    public static async Task<List<(DateTime Utc, byte[] Jpeg)>> ShrinkAsync(
        List<(DateTime Utc, byte[] Jpeg)> frames, CancellationToken ct)
    {
        if (FfmpegPath is not { } ffmpeg) return frames;
        var big = new List<int>();
        for (int i = 0; i < frames.Count; i++)
            if (frames[i].Jpeg.Length > OversizeBytes) big.Add(i);
        if (big.Count == 0) return frames;
        try
        {
            var (outBytes, stderr) = await RunFfmpegAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "image2pipe", "-c:v", "mjpeg", "-i", "pipe:0",
                "-vf", "scale=-2:360",
                "-q:v", "5",
                "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
            }, big.Select(i => frames[i].Jpeg).ToList(),
                TimeSpan.FromSeconds(Math.Max(30, 2 * big.Count)), ct).ConfigureAwait(false);
            var scaled = SplitJpegs(outBytes);
            if (scaled.Count != big.Count)
            {
                // A miscount means frames would pair with the wrong timestamps —
                // originals are oversized but at least honest.
                Log.Info($"AI frame downscale: expected {big.Count} JPEG(s) back, got {scaled.Count}" +
                          $"{(stderr.Length > 0 ? $" ({stderr[..Math.Min(200, stderr.Length)]})" : "")}" +
                          " — sending the originals");
                return frames;
            }
            var result = new List<(DateTime, byte[])>(frames);
            for (int i = 0; i < big.Count; i++)
                result[big[i]] = (frames[big[i]].Utc, scaled[i]);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Info($"AI frame downscale failed ({Log.Flatten(ex)}) — sending the originals");
            return frames;
        }
    }

    /// <summary>Runs ffmpeg with stdin fed from <paramref name="stdinChunks"/> and
    /// stdout collected whole; the process is killed at <paramref name="deadline"/>
    /// (a wedged ffmpeg must not stall the describe worker). stderr rides along
    /// for diagnostics — callers decide what a failure means.</summary>
    private static async Task<(byte[] Out, string Err)> RunFfmpegAsync(string ffmpeg,
        IReadOnlyList<string> args, IReadOnlyList<byte[]> stdinChunks,
        TimeSpan deadline, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("ffmpeg would not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(deadline);
        try
        {
            var stdin = proc.StandardInput.BaseStream;
            var feed = Task.Run(async () =>
            {
                foreach (var chunk in stdinChunks)
                    await stdin.WriteAsync(chunk, limit.Token).ConfigureAwait(false);
                stdin.Close(); // EOF flushes whatever the codec still buffers
            }, limit.Token);
            using var outBuf = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(outBuf, limit.Token).ConfigureAwait(false);
            try { await feed.ConfigureAwait(false); }
            catch (Exception) { /* stdin may close early if ffmpeg bailed; stdout decides */ }
            var stderr = await proc.StandardError.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return (outBuf.ToArray(), stderr);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    /// <summary>Splits a concatenated MJPEG byte stream (image2pipe output) into
    /// individual JPEGs by their SOI/EOI markers.</summary>
    internal static List<byte[]> SplitJpegs(byte[] buf)
    {
        var frames = new List<byte[]>();
        int i = 0;
        while (i < buf.Length - 1)
        {
            if (buf[i] != 0xFF || buf[i + 1] != 0xD8) { i++; continue; }
            int start = i;
            i += 2;
            while (i < buf.Length - 1 && !(buf[i] == 0xFF && buf[i + 1] == 0xD9)) i++;
            if (i >= buf.Length - 1) break; // truncated final frame: drop it
            i += 2;
            frames.Add(buf[start..i]);
        }
        return frames;
    }

    /// <summary>Up to <paramref name="max"/> indices out of <paramref name="count"/>,
    /// always including the first and the last, evenly spread between.</summary>
    internal static int[] SpreadIndices(int count, int max)
    {
        if (count <= max) return Enumerable.Range(0, count).ToArray();
        return Enumerable.Range(0, max)
            .Select(i => (int)Math.Round(i * (double)(count - 1) / (max - 1)))
            .Distinct().ToArray();
    }
}
