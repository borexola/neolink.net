using Neolink.Media;
using Neolink.Streaming;

namespace Neolink.Recording;

/// <summary>
/// Writes hub video packets into a fragmented-MP4 file (playable by browsers and
/// ffmpeg alike). Same pacing trick as the live web player: a camera buffer's true
/// duration is only known when the NEXT buffer arrives (RTP delta), so one buffer
/// is held back and its measured delta spread evenly across its frames.
/// Video-only by design — detection clips don't need the audio track.
/// </summary>
public sealed class ClipWriter : IDisposable
{
    private const uint NominalFrame = 3000; // ~1/30s @ 90 kHz

    private readonly FileStream _file;
    private readonly VideoCodec _codec;

    private uint _sequence = 1;
    private ulong _decodeTime;
    private bool _haveTs;
    private uint _prevTs;
    private long _lastIndex = -1;
    private bool _waitKeyframe;
    private List<(byte[] Sample, bool Keyframe)>? _pending;

    private ClipWriter(FileStream file, VideoCodec codec)
    {
        _file = file;
        _codec = codec;
    }

    /// <summary>Seconds of video written so far (upper bound; pending frames included at nominal rate).</summary>
    public double DurationSeconds =>
        (_decodeTime + (ulong)(_pending?.Count ?? 0) * NominalFrame) / (double)FMp4.Timescale;

    /// <summary>Creates the file and writes the init segment; null when codec params are not known yet.</summary>
    public static ClipWriter? TryCreate(string path, IStreamHub hub)
    {
        var codec = hub.Codec;
        var sps = hub.Sps;
        var pps = hub.Pps;
        if (codec == null || sps == null || pps == null)
            return null;

        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        try
        {
            file.Write(FMp4.BuildInit(codec.Value, sps, pps, hub.Vps, hub.Width, hub.Height));
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new ClipWriter(file, codec.Value);
    }

    /// <summary>
    /// Feeds the next video packet. Must be called with packets in hub order;
    /// drops (index gaps) are handled by resuming at the next keyframe.
    /// </summary>
    public void Add(HubVideo v)
    {
        bool gap = _lastIndex >= 0 && v.Index != _lastIndex + 1;
        _lastIndex = v.Index;
        if (gap)
        {
            FlushPending((uint)(NominalFrame * (_pending?.Count ?? 1)));
            _haveTs = false;
            _waitKeyframe = true;
        }
        if (_waitKeyframe)
        {
            if (!v.Keyframe) return;
            _waitKeyframe = false;
        }

        if (_haveTs && _pending != null)
        {
            uint delta = unchecked(v.RtpTs - _prevTs);
            if (delta == 0 || delta > 30 * FMp4.Timescale)
                delta = (uint)(NominalFrame * _pending.Count); // clock garbage: assume ~30fps
            FlushPending(delta);
        }
        _prevTs = v.RtpTs;
        _haveTs = true;

        var aus = FMp4.SplitAccessUnits(_codec, v.AnnexB);
        if (aus.Count > 0)
            _pending = aus;
    }

    private void FlushPending(uint totalDuration)
    {
        if (_pending == null || _pending.Count == 0) { _pending = null; return; }
        uint per = Math.Clamp(totalDuration / (uint)_pending.Count, 900u, 45_000u); // 10..500ms per frame
        foreach (var (sample, key) in _pending)
        {
            _file.Write(FMp4.BuildFragment(_sequence++, _decodeTime, per, sample, key));
            _decodeTime += per;
        }
        _pending = null;
    }

    /// <summary>Writes the held-back frames and closes the file.</summary>
    public void Dispose()
    {
        try
        {
            FlushPending((uint)(NominalFrame * (_pending?.Count ?? 1)));
            _file.Flush();
        }
        catch (IOException) { }
        finally
        {
            _file.Dispose();
        }
    }
}
