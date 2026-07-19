// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
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
    private readonly ConcurrentDictionary<Guid, (Channel<HubPacket> Ch, bool Viewer)> _subscribers = new();
    private int _viewerCount;
    private long _index;

    // GOP cache: the ordered packets since (and including) the most recent video
    // keyframe. A brand-new viewer is primed with this so it has a decodable
    // keyframe IMMEDIATELY, instead of discarding frames until the camera's next
    // keyframe arrives (2-4 s on typical Reolink settings) — the dominant cause of
    // slow first-frame on a multi-tile refresh. Audio packets are cached inline so
    // the replayed indices stay consecutive (the WS sender treats an index gap as a
    // drop) and A/V start together. Guarded by _castGate, which also serializes
    // publish so no packet can slip between a new subscriber's prime and its
    // registration (that would open a one-packet gap right at startup).
    private readonly object _castGate = new();
    private readonly List<HubPacket> _gop = new();
    private int _gopBytes;
    private bool _gopOpen;
    // Safety valves only: a normal 2-4 s GOP sits far below these. They bound
    // memory if a keyframe is pathologically late or the bitrate is huge, and MUST
    // stay under the subscriber channel capacity so priming can never trip
    // DropOldest and evict the leading keyframe.
    private const int GopMaxPackets = 900;
    private const int GopMaxBytes = 6 * 1024 * 1024;
    private const int SubscriberChannelCapacity = 2048;

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

    /// <summary>External watchers only — recorders and other internal taps excluded.</summary>
    public int ViewerCount => Volatile.Read(ref _viewerCount);

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

        // Learn codec parameters from the stream. Parameter sets ride keyframes,
        // so once they are known, P-frames skip the full-buffer NAL scan — that's
        // the hub's per-frame hot path across every stream.
        bool paramsUpdated = false;
        bool needParams = frame.Keyframe || Sps == null || Pps == null
            || (frame.Codec == VideoCodec.H265 && Vps == null);
        if (needParams)
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
            // Sources without a resolution side channel (generic RTSP pulls) get
            // their dimensions from the SPS itself — MSE rejects a 0×0 init.
            if (Width == 0 && paramsUpdated && Sps != null
                && H26x.TryGetDimensions(frame.Codec, Sps, out var w, out var h))
            {
                Width = w;
                Height = h;
            }
        }

        bool ready = Codec == VideoCodec.H264 ? (Sps != null && Pps != null) : (Sps != null && Pps != null);
        if (ready)
            _videoReady.TrySetResult();

        Emit(new HubVideo(Interlocked.Increment(ref _index), frame.Data, frame.Keyframe, _videoRtpTs),
             frame.Keyframe, frame.Data.Length);
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
            Emit(new HubAudioAac(Interlocked.Increment(ref _index), au.Data, _audioRtpTs), false, au.Data.Length);
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
        Emit(new HubAudioPcm(Interlocked.Increment(ref _index), pcm, _audioRtpTs), false, pcm.Length);
        _audioRtpTs = unchecked(_audioRtpTs + (uint)(pcm.Length / 2));
    }

    // Caches the packet into the current GOP (video keyframe starts a fresh one)
    // and fans it out, both under _castGate so a concurrent Subscribe can't
    // interleave. The lock is effectively uncontended: one media loop publishes
    // per hub, and TryWrite on a DropOldest channel never blocks.
    private void Emit(HubPacket packet, bool videoKeyframe, int payloadBytes)
    {
        lock (_castGate)
        {
            if (videoKeyframe)
            {
                _gop.Clear();
                _gopBytes = 0;
                _gopOpen = true;
            }
            if (_gopOpen)
            {
                if (_gop.Count >= GopMaxPackets || _gopBytes + payloadBytes > GopMaxBytes)
                {
                    // GOP outgrew the cache budget (late keyframe / very high bitrate):
                    // stop caching until the next keyframe so a joiner falls back to
                    // waiting for one rather than being primed with a partial,
                    // undecodable GOP.
                    _gop.Clear();
                    _gopBytes = 0;
                    _gopOpen = false;
                }
                else
                {
                    _gop.Add(packet);
                    _gopBytes += payloadBytes;
                }
            }
            foreach (var (_, sub) in _subscribers)
                sub.Ch.Writer.TryWrite(packet); // bounded DropOldest: never blocks
        }
    }

    // ------------------------------------------------------------------ subscribe

    public (Guid id, ChannelReader<HubPacket> reader) Subscribe(bool viewer = false)
    {
        var ch = Channel.CreateBounded<HubPacket>(new BoundedChannelOptions(SubscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        lock (_castGate)
        {
            // Prime with the buffered GOP (keyframe-first) so this viewer can decode
            // immediately. Under _castGate with registration so no live packet slips
            // between the snapshot and the subscribe — that would show as a one-packet
            // index gap and make the WS sender discard back to the next keyframe.
            foreach (var p in _gop)
                ch.Writer.TryWrite(p);
            _subscribers[id] = (ch, viewer);
        }
        if (viewer) Interlocked.Increment(ref _viewerCount);
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var sub))
        {
            if (sub.Viewer) Interlocked.Decrement(ref _viewerCount);
            sub.Ch.Writer.TryComplete();
        }
    }

    // Ticks (UTC) of the last DESCRIBE/init attempt — the wake signal for
    // sleep-friendly battery cameras (see IStreamHub.LastViewerAskUtc).
    private long _lastViewerAskTicks;

    public DateTime LastViewerAskUtc => new(Volatile.Read(ref _lastViewerAskTicks), DateTimeKind.Utc);

    /// <summary>
    /// Waits until enough is known about the stream to answer a DESCRIBE:
    /// video params present, plus a short grace period to detect audio.
    /// </summary>
    public async Task<bool> WaitForDescribeInfoAsync(TimeSpan timeout, CancellationToken ct)
    {
        Volatile.Write(ref _lastViewerAskTicks, DateTime.UtcNow.Ticks);
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
