// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Streaming;

namespace Neolink.Media;

/// <summary>
/// A still taken from the stream Neolink is already carrying, for cameras that
/// have no snapshot command of their own — every generic RTSP camera, and any
/// Baichuan model whose snap is unavailable. The hub primes each new subscriber
/// with its buffered GOP, so one keyframe-led group is on hand the moment we
/// subscribe; ffmpeg turns it into a JPEG.
///
/// Best-effort throughout: no ffmpeg, no live stream, or a decode that yields
/// nothing all return null and the caller carries on without a picture. The
/// subscription is INTERNAL (never a viewer), so a battery camera parked asleep
/// is not woken by someone loading a page.
/// </summary>
public static class FrameGrab
{
    /// <summary>How long to wait for a keyframe when the hub's GOP cache turns out
    /// to be empty after all — a stream that started between the caller's check and
    /// the subscribe. Short on purpose: the caller is an HTTP request holding a
    /// per-camera gate, and a live stream costs none of this because its buffered
    /// group is handed over the moment we subscribe.</summary>
    private static readonly TimeSpan KeyframeWait = TimeSpan.FromSeconds(2);

    /// <summary>At most this many decodes run at once across every camera. A wall of
    /// tiles refreshing together would otherwise start one ffmpeg per camera at the
    /// same instant, and the decode is the one part of a snapshot that costs real
    /// CPU. Queued callers wait their turn rather than being refused: their own
    /// request timeout bounds the wait, and the per-camera throttle upstream keeps
    /// the queue short.</summary>
    private static readonly SemaphoreSlim Decoders = new(2, 2);

    /// <summary>Frames fed to the decoder after the keyframe; the still is the LAST of them,
    /// since the keyframe alone can be a whole GOP old. Bounded because every frame is a decode.</summary>
    internal const int MaxFollowing = 16;

    private static int _missingLogged;

    /// <summary>Whether these bytes are a JPEG worth serving: cameras answer a failed snapshot
    /// with an empty body or an HTML error page more often than with an error status.</summary>
    public static bool IsJpeg(byte[]? b) => b is { Length: > 100 } && b[0] == 0xFF && b[1] == 0xD8;

    /// <summary>A JPEG of the stream's current picture, or null when one can't be
    /// made. Never throws (except on the caller's own cancellation).</summary>
    public static async Task<byte[]?> FromHubAsync(IStreamHub hub, int maxHeight, CancellationToken ct)
    {
        if (Ffmpeg.ExePath is not { } ffmpeg)
        {
            if (Interlocked.Exchange(ref _missingLogged, 1) == 0)
                Log.Info("No ffmpeg found — cameras without a snapshot command of their own " +
                         "(generic RTSP) show no still image. Installing ffmpeg on PATH (or " +
                         "pointing NEOLINK_FFMPEG at a binary) gives them one automatically.");
            return null;
        }
        if (!hub.VideoReady || hub.Codec is not { } codec) return null;

        var (id, reader) = hub.Subscribe();
        List<HubVideo> packets;
        try
        {
            packets = await CollectAsync(reader, ct).ConfigureAwait(false);
        }
        finally
        {
            hub.Unsubscribe(id);
        }
        if (packets.Count == 0) return null;

        // Parameter sets first: a keyframe usually repeats them inline, but
        // "usually" is not a decode guarantee.
        var chunks = new List<byte[]>();
        byte[] startCode = { 0, 0, 0, 1 };
        foreach (var nal in new[] { hub.Vps, hub.Sps, hub.Pps })
        {
            if (nal is not { Length: > 0 }) continue;
            chunks.Add(startCode);
            chunks.Add(nal);
        }
        foreach (var p in packets) chunks.Add(p.AnnexB);

        await Decoders.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (outBytes, stderr) = await Ffmpeg.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", codec == VideoCodec.H265 ? "hevc" : "h264",
                "-i", "pipe:0",
                // Height-bounded, width proportional: an ultra-wide panorama keeps
                // its shape instead of being squeezed into a 16:9 box. A frame
                // already shorter than the bound is left alone.
                "-vf", $"scale=-2:'min({maxHeight},ih)'",
                // Every buffered frame, and the last one is the still (see MaxFollowing).
                "-frames:v", packets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-q:v", "4",
                "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
            }, chunks, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            var jpeg = Ai.AiPreroll.SplitJpegs(outBytes).LastOrDefault();
            if (jpeg is not { Length: > 100 })
            {
                Log.Debug($"{hub.Name}: frame grab produced no JPEG from {packets.Count} packet(s)" +
                          $"{(stderr.Length > 0 ? $": {stderr[..Math.Min(200, stderr.Length)]}" : "")}");
                return null;
            }
            return jpeg;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"{hub.Name}: frame grab failed: {Log.Flatten(ex)}");
            return null;
        }
        finally
        {
            Decoders.Release();
        }
    }

    /// <summary>The keyframe-led run of video packets to decode: whatever the hub
    /// primed us with, or — when it primed nothing — the next keyframe to arrive.
    /// Returns empty when no keyframe shows up inside the wait.</summary>
    private static async Task<List<HubVideo>> CollectAsync(
        System.Threading.Channels.ChannelReader<HubPacket> reader, CancellationToken ct)
    {
        var run = new List<HubVideo>();
        // Everything already buffered (the primed GOP) is available without
        // awaiting; take it first so a live stream costs no wall clock at all.
        while (reader.TryRead(out var packet))
            Take(run, packet);
        if (run.Count > 0) return run;

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(KeyframeWait);
        try
        {
            // Stop at the KEYFRAME, not at a packet count. One decodable picture is
            // all that leaves this method (the last frame decoded), so waiting
            // on for frames that will be thrown away would spend the whole timeout
            // on a stream that had already given us what we came for. Anything that
            // happens to be buffered alongside it comes along for free.
            while (run.Count == 0
                   && await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                while (reader.TryRead(out var packet))
                    Take(run, packet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { /* no keyframe inside the wait */ }
        catch (System.Threading.Channels.ChannelClosedException) { /* source stopped */ }
        return run;

    }

    /// <summary>Adds one packet to the run being collected. A run always STARTS on a
    /// keyframe — anything before one is undecodable, and a later keyframe starts a
    /// fresher run than the one in hand. Audio is not video and is ignored; frames
    /// past the follow limit are dropped rather than buffered.</summary>
    internal static void Take(List<HubVideo> run, HubPacket packet)
    {
        if (packet is not HubVideo v) return;
        if (v.Keyframe) run.Clear();
        else if (run.Count == 0 || run.Count > MaxFollowing) return; // the keyframe plus MaxFollowing
        run.Add(v);
    }
}
