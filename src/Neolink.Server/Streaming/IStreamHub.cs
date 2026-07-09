// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Threading.Channels;
using Neolink.Media;

namespace Neolink.Streaming;

/// <summary>The publish side of a stream hub, fed by one camera stream.</summary>
public interface IMediaSink
{
    void PublishInfo(MediaInfo info);
    void PublishVideo(VideoFrame frame);
    void PublishAac(AacFrame frame);
    void PublishAdpcm(AdpcmFrame frame);
}

/// <summary>
/// The consume side of a stream hub: codec parameters for DESCRIBE/init plus
/// a fan-out subscription of media packets. Used by the RTSP server and web API.
/// </summary>
public interface IStreamHub
{
    string Name { get; }
    /// <summary>Every attached consumer, including the server's own recorders.</summary>
    int SubscriberCount { get; }
    /// <summary>External watchers only (RTSP sessions + web players) — what "viewers" means to a human.</summary>
    int ViewerCount { get; }

    /// <summary>True once codec parameters have been learned (DESCRIBE/init can be answered).</summary>
    bool VideoReady { get; }
    VideoCodec? Codec { get; }
    byte[]? Sps { get; }
    byte[]? Pps { get; }
    byte[]? Vps { get; }
    uint Width { get; }
    uint Height { get; }
    AudioTrackInfo? Audio { get; }

    /// <param name="viewer">True for external watchers (RTSP/web); false for
    /// internal consumers like the recorders, which don't count as viewers.</param>
    (Guid id, ChannelReader<HubPacket> reader) Subscribe(bool viewer = false);
    void Unsubscribe(Guid id);

    /// <summary>
    /// When a would-be viewer last asked for this stream (DESCRIBE/init wait).
    /// Viewers only subscribe once video is ready, so a sleeping battery camera
    /// uses this — not <see cref="ViewerCount"/> — as its wake-up signal.
    /// </summary>
    DateTime LastViewerAskUtc { get; }

    /// <summary>
    /// Waits until enough is known about the stream to answer a DESCRIBE:
    /// video params present, plus a short grace period to detect audio.
    /// </summary>
    Task<bool> WaitForDescribeInfoAsync(TimeSpan timeout, CancellationToken ct);
}
