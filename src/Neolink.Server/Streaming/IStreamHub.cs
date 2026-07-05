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
    int SubscriberCount { get; }

    /// <summary>True once codec parameters have been learned (DESCRIBE/init can be answered).</summary>
    bool VideoReady { get; }
    VideoCodec? Codec { get; }
    byte[]? Sps { get; }
    byte[]? Pps { get; }
    byte[]? Vps { get; }
    uint Width { get; }
    uint Height { get; }
    AudioTrackInfo? Audio { get; }

    (Guid id, ChannelReader<HubPacket> reader) Subscribe();
    void Unsubscribe(Guid id);

    /// <summary>
    /// Waits until enough is known about the stream to answer a DESCRIBE:
    /// video params present, plus a short grace period to detect audio.
    /// </summary>
    Task<bool> WaitForDescribeInfoAsync(TimeSpan timeout, CancellationToken ct);
}
