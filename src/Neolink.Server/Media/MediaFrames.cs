// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.Media;

public enum VideoCodec
{
    H264,
    H265,
}

public abstract record MediaFrame;

/// <summary>Stream info header ("1001"/"1002" magic).</summary>
public sealed record MediaInfo(uint Width, uint Height, byte Fps) : MediaFrame;

/// <summary>A complete video access unit (Annex-B) — key frame or predicted frame.</summary>
public sealed record VideoFrame(VideoCodec Codec, bool Keyframe, uint Microseconds, uint? UnixTime, byte[] Data) : MediaFrame;

/// <summary>An AAC frame block (one or more ADTS frames).</summary>
public sealed record AacFrame(byte[] Data) : MediaFrame;

/// <summary>A DVI-4/IMA ADPCM block, including its 4-byte predictor-state header.</summary>
public sealed record AdpcmFrame(byte[] Data) : MediaFrame;
