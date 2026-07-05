using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Neolink.Media;

namespace Neolink.Streaming;

public abstract record HubPacket(long Index);
public sealed record HubVideo(long Index, byte[] AnnexB, bool Keyframe, uint RtpTs) : HubPacket(Index);
/// <summary>One raw AAC access unit (no ADTS header). RTP clock = sample rate.</summary>
public sealed record HubAudioAac(long Index, byte[] Au, uint RtpTs) : HubPacket(Index);
/// <summary>16-bit little-endian mono PCM decoded from ADPCM. RTP clock = sample rate.</summary>
public sealed record HubAudioPcm(long Index, byte[] Pcm, uint RtpTs) : HubPacket(Index);

public sealed record AudioTrackInfo(bool IsAac, int SampleRate, int Channels, byte[]? AudioSpecificConfig);

/// <summary>
/// Fan-out point between one camera stream and any number of RTSP sessions.
/// Tracks codec parameters (SPS/PPS/VPS, audio config) needed to answer DESCRIBE.
/// </summary>
public sealed class StreamHub : IStreamHub, IMediaSink
{
    public string Name { get; }

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Channel<HubPacket>> _subscribers = new();
    private long _index;

    // --- Video track info ---
    public VideoCodec? Codec { get; private set; }
    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    public byte[]? Vps { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    // --- Audio track info ---
    public AudioTrackInfo? Audio { get; private set; }

    private readonly TaskCompletionSource _videoReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _firstVideoAt = DateTime.MaxValue;
    private bool _audioProbeDone;

    // Video RTP timestamp tracking (90 kHz)
    private uint _videoRtpTs = (uint)Random.Shared.Next();
    private uint _lastVideoUs;
    private bool _haveVideoTs;
    private readonly Stopwatch _wallClock = Stopwatch.StartNew();
    private TimeSpan _lastWall;

    // Audio RTP timestamps (clock = sample rate)
    private uint _audioRtpTs = (uint)Random.Shared.Next();

    public StreamHub(string name) => Name = name;

    public int SubscriberCount => _subscribers.Count;

    /// <summary>True once codec parameters have been learned (DESCRIBE/init can be answered).</summary>
    public bool VideoReady => _videoReady.Task.IsCompletedSuccessfully;

    // ------------------------------------------------------------------ publish

    public void PublishInfo(MediaInfo info)
    {
        lock (_gate)
        {
            Width = info.Width;
            Height = info.Height;
        }
    }

    public void PublishVideo(VideoFrame frame)
    {
        // Advance the RTP timestamp using the camera's microsecond counter when sane,
        // otherwise fall back to wall clock.
        uint deltaUs;
        var wallNow = _wallClock.Elapsed;
        if (!_haveVideoTs)
        {
            deltaUs = 0;
            _haveVideoTs = true;
        }
        else
        {
            deltaUs = unchecked(frame.Microseconds - _lastVideoUs); // wrap-safe
            if (deltaUs == 0 || deltaUs > 5_000_000)
                deltaUs = (uint)Math.Clamp((wallNow - _lastWall).TotalMicroseconds, 0, 5_000_000);
        }
        _lastVideoUs = frame.Microseconds;
        _lastWall = wallNow;
        _videoRtpTs = unchecked(_videoRtpTs + (uint)((ulong)deltaUs * 90 / 1000));

        // Learn codec parameters from the stream
        bool paramsUpdated = false;
        foreach (var nal in H26x.SplitNals(frame.Data))
        {
            var span = nal.Span;
            if (span.Length == 0) continue;
            if (frame.Codec == VideoCodec.H264)
            {
                switch (H26x.H264NalType(span))
                {
                    case H26x.H264Sps: Sps = nal.ToArray(); paramsUpdated = true; break;
                    case H26x.H264Pps: Pps = nal.ToArray(); paramsUpdated = true; break;
                }
            }
            else
            {
                switch (H26x.H265NalType(span))
                {
                    case H26x.H265Vps: Vps = nal.ToArray(); paramsUpdated = true; break;
                    case H26x.H265Sps: Sps = nal.ToArray(); paramsUpdated = true; break;
                    case H26x.H265Pps: Pps = nal.ToArray(); paramsUpdated = true; break;
                }
            }
        }

        lock (_gate)
        {
            Codec = frame.Codec;
            if (_firstVideoAt == DateTime.MaxValue) _firstVideoAt = DateTime.UtcNow;
        }
        _ = paramsUpdated;

        bool ready = Codec == VideoCodec.H264 ? (Sps != null && Pps != null) : (Sps != null && Pps != null);
        if (ready)
            _videoReady.TrySetResult();

        Broadcast(new HubVideo(Interlocked.Increment(ref _index), frame.Data, frame.Keyframe, _videoRtpTs));
    }

    public void PublishAac(AacFrame frame)
    {
        foreach (var au in Adts.Split(frame.Data))
        {
            if (Audio == null || !Audio.IsAac)
            {
                Audio = new AudioTrackInfo(true, au.SampleRate, au.Channels, au.AudioSpecificConfig);
                Log.Debug($"{Name}: AAC audio detected ({au.SampleRate} Hz, {au.Channels}ch)");
            }
            Broadcast(new HubAudioAac(Interlocked.Increment(ref _index), au.Data, _audioRtpTs));
            _audioRtpTs = unchecked(_audioRtpTs + 1024); // AAC frame = 1024 samples
        }
    }

    public void PublishAdpcm(AdpcmFrame frame)
    {
        byte[] pcm;
        try
        {
            pcm = Adpcm.BlockToPcm(frame.Data);
        }
        catch (Exception ex)
        {
            Log.Debug($"{Name}: bad ADPCM block: {ex.Message}");
            return;
        }
        if (Audio == null)
        {
            // The original Neolink played DVI-4 ADPCM at 8 kHz
            Audio = new AudioTrackInfo(false, 8000, 1, null);
            Log.Debug($"{Name}: ADPCM audio detected (decoding to PCM @ 8 kHz)");
        }
        Broadcast(new HubAudioPcm(Interlocked.Increment(ref _index), pcm, _audioRtpTs));
        _audioRtpTs = unchecked(_audioRtpTs + (uint)(pcm.Length / 2));
    }

    private void Broadcast(HubPacket packet)
    {
        foreach (var (_, ch) in _subscribers)
            ch.Writer.TryWrite(packet); // bounded DropOldest: never blocks
    }

    // ------------------------------------------------------------------ subscribe

    public (Guid id, ChannelReader<HubPacket> reader) Subscribe()
    {
        var ch = Channel.CreateBounded<HubPacket>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
            ch.Writer.TryComplete();
    }

    /// <summary>
    /// Waits until enough is known about the stream to answer a DESCRIBE:
    /// video params present, plus a short grace period to detect audio.
    /// </summary>
    public async Task<bool> WaitForDescribeInfoAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _videoReady.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        // Give audio a moment to appear (it typically arrives within a second of
        // video) — but only probe once per hub. If the camera showed no audio the
        // first time, don't tax every subsequent DESCRIBE with the wait.
        if (Audio == null && !_audioProbeDone)
        {
            for (int i = 0; i < 20 && Audio == null && !cts.IsCancellationRequested; i++)
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            _audioProbeDone = true;
        }
        return true;
    }
}
