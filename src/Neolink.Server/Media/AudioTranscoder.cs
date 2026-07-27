// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using System.Threading.Channels;

namespace Neolink.Media;

/// <summary>
/// A persistent ffmpeg pipe that turns one stream's camera audio (AAC/ADTS or
/// raw PCM) into 20 ms Opus packets — what WebRTC ecosystems (go2rtc, browsers,
/// Home Assistant cards) take natively instead of running their own transcode.
/// One instance per publishing stream: source blocks go in via <see cref="Feed"/>
/// (never blocking — the camera loop must not feel ffmpeg), finished packets
/// come back on the reader task through the callback. A dead ffmpeg is
/// relaunched with backoff and can never take the stream down.
/// </summary>
public sealed class AudioTranscoder : IDisposable
{
    public enum SourceKind
    {
        /// <summary>AAC in ADTS framing, as the camera sends it.</summary>
        AdtsAac,
        /// <summary>16-bit little-endian mono PCM at 8 kHz (decoded ADPCM).</summary>
        Pcm16le8k,
    }

    /// <summary>Samples one 20 ms Opus packet advances the 48 kHz RTP clock by.</summary>
    public const int SamplesPerPacket = 960;

    private const int MaxQueuedBlocks = 256;

    private readonly string _name;
    private readonly SourceKind _kind;
    private readonly Action<byte[]> _onPacket;
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxQueuedBlocks)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // live audio: stale blocks lose
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _cts = new();
    private Process? _proc;

    public AudioTranscoder(string name, SourceKind kind, Action<byte[]> onPacket)
    {
        _name = name;
        _kind = kind;
        _onPacket = onPacket;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Queues one source block. Never blocks; when ffmpeg falls behind,
    /// the oldest block is dropped rather than backpressuring the camera loop.</summary>
    public void Feed(byte[] data) => _input.Writer.TryWrite(data);

    private async Task RunAsync(CancellationToken ct)
    {
        int strikes = 0;
        while (!ct.IsCancellationRequested)
        {
            bool produced = false;
            try
            {
                produced = await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn($"{_name}: audio transcoder crashed: {Log.Flatten(ex)}");
            }
            if (ct.IsCancellationRequested) return;
            strikes = produced ? 1 : strikes + 1;
            if (strikes >= 5)
            {
                Log.Warn($"{_name}: ffmpeg keeps exiting without producing Opus — giving up " +
                         "for this stream session (its RTSP clients hear silence). The lines " +
                         "above say why; a build without libopus is the usual culprit.");
                return;
            }
            int pause = Math.Min(30, 2 << strikes);
            Log.Info($"{_name}: audio transcoder restarting in {pause}s");
            try { await Task.Delay(TimeSpan.FromSeconds(pause), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One ffmpeg lifetime. Returns true when it produced any audio.</summary>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (Ffmpeg.ExePath is not { } exe)
            throw new InvalidOperationException("no ffmpeg found"); // caller checks first

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var a = psi.ArgumentList;
        a.Add("-hide_banner"); a.Add("-loglevel"); a.Add("error"); a.Add("-nostats");
        // Start decoding on the first block instead of buffering a probe window.
        a.Add("-probesize"); a.Add("2048");
        a.Add("-analyzeduration"); a.Add("0");
        a.Add("-fflags"); a.Add("nobuffer");
        if (_kind == SourceKind.AdtsAac)
        {
            a.Add("-f"); a.Add("aac");
        }
        else
        {
            a.Add("-f"); a.Add("s16le");
            a.Add("-ar"); a.Add("8000");
            a.Add("-ac"); a.Add("1");
        }
        a.Add("-i"); a.Add("pipe:0");
        a.Add("-ac"); a.Add("1");
        a.Add("-ar"); a.Add("48000");
        a.Add("-c:a"); a.Add("libopus");
        a.Add("-b:a"); a.Add("32k");
        a.Add("-vbr"); a.Add("on");
        a.Add("-application"); a.Add("voip"); // camera audio is speech; keeps latency low
        a.Add("-frame_duration"); a.Add("20"); // SamplesPerPacket depends on this
        a.Add("-f"); a.Add("ogg");
        // One page per packet — the muxer would otherwise batch ~1 s per page.
        a.Add("-page_duration"); a.Add("20000");
        a.Add("-flush_packets"); a.Add("1");
        a.Add("pipe:1");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg would not start");
        _proc = proc;
        long packetsOut = 0;
        var errTail = new List<string>();
        try
        {
            var errPump = Task.Run(async () =>
            {
                try
                {
                    while (await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        if (errTail.Count >= 8) errTail.RemoveAt(0);
                        errTail.Add(line);
                    }
                }
                catch { /* stderr closes with the process */ }
            }, CancellationToken.None);

            var stdinPump = Task.Run(async () =>
            {
                try
                {
                    var stdin = proc.StandardInput.BaseStream;
                    await foreach (var block in _input.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        await stdin.WriteAsync(block, ct).ConfigureAwait(false);
                        await stdin.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                catch { /* ffmpeg died or we're stopping; stdout EOF decides */ }
            }, CancellationToken.None);

            var ogg = new OggOpusReader();
            var buf = new byte[4096];
            var stdout = proc.StandardOutput.BaseStream;
            int n;
            while ((n = await stdout.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                foreach (var pkt in ogg.Feed(buf.AsSpan(0, n)))
                {
                    packetsOut++;
                    _onPacket(pkt);
                }
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await errPump.ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                Log.Warn($"{_name}: ffmpeg audio transcode exited (code {proc.ExitCode}, " +
                         $"{packetsOut} packet(s) produced)" +
                         (errTail.Count > 0 ? $" — {string.Join(" | ", errTail)}" : ""));
            _ = stdinPump; // completes on its own once the channel or process closes
            return packetsOut > 0;
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            _proc = null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _input.Writer.TryComplete();
        try { if (_proc is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
