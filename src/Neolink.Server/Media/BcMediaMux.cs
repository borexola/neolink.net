// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Threading.Channels;

namespace Neolink.Media;

/// <summary>Turns a recorded BcMedia stream (an SD download over Baichuan sends frames,
/// not the MP4 file) into a seekable MP4 with ffmpeg.</summary>
internal static class BcMediaMux
{
    /// <summary>True when the bytes start with a BcMedia info header or video frame.</summary>
    public static bool LooksLikeBcMedia(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) is
            0x31303031 or 0x32303031 or (>= 0x63643030 and <= 0x63643139);

    /// <summary>Frame rate from the frames' own clock, else the info header's, else 25.</summary>
    internal static double FrameRate(uint firstUs, uint lastUs, int frames, byte headerFps)
    {
        if (frames > 1 && lastUs > firstUs)
        {
            double fps = (frames - 1) / ((lastUs - firstUs) / 1_000_000.0);
            if (fps is >= 1 and <= 120) return Math.Round(fps, 2);
        }
        return headerFps is > 0 and <= 120 ? headerFps : 25;
    }

    public static async Task ToMp4Async(string bcPath, string mp4Path, CancellationToken ct)
    {
        var ffmpeg = Ffmpeg.ExePath
                     ?? throw new IOException("recordings fetched over Baichuan need ffmpeg to become MP4");
        string video = bcPath + ".es", audio = bcPath + ".aac";
        try
        {
            VideoCodec? codec = null;
            byte headerFps = 0;
            uint firstUs = 0, lastUs = 0;
            int frames = 0;
            long audioBytes = 0;
            await using (var v = File.Create(video))
            await using (var a = File.Create(audio))
            {
                var chunks = Channel.CreateBounded<byte[]>(8);
                var feed = Task.Run(async () =>
                {
                    try
                    {
                        await using var src = File.OpenRead(bcPath);
                        var buf = new byte[256 * 1024];
                        int n;
                        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                            await chunks.Writer.WriteAsync(buf[..n], ct).ConfigureAwait(false);
                        chunks.Writer.Complete();
                    }
                    catch (Exception ex) { chunks.Writer.Complete(ex); }
                }, ct);
                var reader = new MediaFrameReader(chunks.Reader);
                while (true)
                {
                    MediaFrame frame;
                    try { frame = await reader.ReadFrameAsync(ct).ConfigureAwait(false); }
                    catch (EndOfStreamException) { break; }
                    switch (frame)
                    {
                        case MediaInfo info:
                            if (info.Fps > 0) headerFps = info.Fps;
                            break;
                        case VideoFrame vf when codec == null || vf.Codec == codec:
                            codec ??= vf.Codec;
                            if (frames++ == 0) firstUs = vf.Microseconds;
                            lastUs = vf.Microseconds;
                            await v.WriteAsync(vf.Data, ct).ConfigureAwait(false);
                            break;
                        case AacFrame af:
                            audioBytes += af.Data.Length;
                            await a.WriteAsync(af.Data, ct).ConfigureAwait(false);
                            break;
                    }
                }
                await feed.ConfigureAwait(false);
            }
            if (codec == null || frames == 0)
                throw new IOException("the recording fetched over Baichuan held no video frames");
            var fps = FrameRate(firstUs, lastUs, frames, headerFps).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var args = new List<string> { "-v", "warning", "-y",
                "-f", codec == VideoCodec.H265 ? "hevc" : "h264", "-framerate", fps, "-i", video };
            if (audioBytes > 0) args.AddRange(new[] { "-f", "aac", "-i", audio });
            args.AddRange(new[] { "-map", "0:v" });
            if (audioBytes > 0) args.AddRange(new[] { "-map", "1:a" });
            args.AddRange(new[] { "-c", "copy", "-movflags", "+faststart", "-f", "mp4", mp4Path });
            var (_, err) = await Ffmpeg.RunAsync(ffmpeg, args, Array.Empty<byte[]>(), TimeSpan.FromMinutes(3), ct)
                .ConfigureAwait(false);
            if (!File.Exists(mp4Path) || new FileInfo(mp4Path).Length == 0)
                throw new IOException($"muxing the recording failed: {err.Trim()}");
            var notes = err.Trim();
            Log.Info($"SD recording {Path.GetFileName(bcPath)}: {frames} {codec} frames at {fps} fps, " +
                     $"{audioBytes / 1024} KB AAC, muxed to {new FileInfo(mp4Path).Length / 1024} KB" +
                     (notes.Length > 0 ? $"; ffmpeg: {(notes.Length > 300 ? notes[..300] : notes)}" : ""));
        }
        finally
        {
            try { File.Delete(video); } catch { }
            try { File.Delete(audio); } catch { }
        }
    }
}
