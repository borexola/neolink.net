// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Threading.Channels;
using Neolink.Media;
using Neolink.Streaming;

namespace Neolink.Rtsp;

/// <summary>
/// Receives the RTSP audio backchannel (ONVIF Profile-T two-way talk): G.711 RTP
/// packets the client pushes during PLAY are depacketized, decoded to 16-bit PCM
/// and streamed into the camera's <see cref="ICameraControl.TalkAsync"/> pipeline,
/// which resamples and ADPCM-encodes them to the speaker. One per RTSP session.
/// </summary>
internal sealed class BackchannelReceiver
{
    // G.711 is always 8 kHz; TalkAsync resamples to the camera's talk rate.
    private const int SampleRate = 8000;

    // Talk is real-time: if the camera side stalls, dropping the oldest audio is
    // the right backpressure — never let the queue grow without bound.
    private readonly Channel<byte[]> _pcm = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly ICameraControl _control;
    private CancellationTokenSource? _cts;
    private Task? _talk;

    public BackchannelReceiver(ICameraControl control) => _control = control;

    /// <summary>Opens the talk session (idempotent). Lives until <see cref="Stop"/>.</summary>
    public void Start(CancellationToken ct)
    {
        if (_talk != null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _talk = Task.Run(async () =>
        {
            try
            {
                await _control.TalkAsync(SampleRate, _pcm.Reader, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (TalkBusyException) { Log.Debug($"{_control.CameraName}: backchannel busy (another talk session is active)"); }
            catch (NotSupportedException) { Log.Debug($"{_control.CameraName}: backchannel unsupported (no speaker)"); }
            catch (Exception ex) { Log.Debug($"{_control.CameraName}: backchannel talk ended: {Log.Flatten(ex)}"); }
        }, CancellationToken.None);
        Log.Info($"{_control.CameraName}: RTSP audio backchannel opened");
    }

    /// <summary>Closes the talk session and releases the camera's talk channel.</summary>
    public void Stop()
    {
        if (_talk == null) return;
        _pcm.Writer.TryComplete();
        _cts?.Cancel();
        _talk = null;
    }

    /// <summary>Feeds one interleaved RTP packet (12-byte header + G.711 payload).</summary>
    public void OnRtp(byte[] packet)
    {
        var pcm = Depacketize(packet);
        if (pcm != null) _pcm.Writer.TryWrite(pcm);
    }

    /// <summary>Strips the RTP header and G.711-decodes the payload to 16-bit LE PCM.</summary>
    internal static byte[]? Depacketize(byte[] p)
    {
        if (p.Length < 12 || (p[0] >> 6) != 2) return null; // need a full RTP v2 header
        int cc = p[0] & 0x0F;
        bool ext = (p[0] & 0x10) != 0;
        bool pad = (p[0] & 0x20) != 0;
        int payloadType = p[1] & 0x7F;

        int off = 12 + cc * 4;
        if (ext)
        {
            if (off + 4 > p.Length) return null;
            int words = (p[off + 2] << 8) | p[off + 3];
            off += 4 + words * 4;
        }
        int end = p.Length;
        if (pad && end > off) end -= p[end - 1];
        if (end <= off) return null;

        // PT 8 = A-law (PCMA); everything else we advertise/expect is µ-law (PCMU).
        return G711.ToPcm16(p.AsSpan(off, end - off), aLaw: payloadType == 8);
    }
}
