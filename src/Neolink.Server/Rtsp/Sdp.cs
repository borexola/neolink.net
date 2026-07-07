// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text;
using Neolink.Media;
using Neolink.Streaming;

namespace Neolink.Rtsp;

public static class Sdp
{
    public const byte VideoPayloadType = 96;
    public const byte AudioPayloadType = 97;

    public static string Build(IStreamHub hub, string sessionName)
    {
        var sb = new StringBuilder();
        long sid = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        sb.Append("v=0\r\n");
        sb.Append($"o=- {sid} 1 IN IP4 0.0.0.0\r\n");
        sb.Append($"s={sessionName}\r\n");
        sb.Append("c=IN IP4 0.0.0.0\r\n");
        sb.Append("t=0 0\r\n");
        sb.Append("a=control:*\r\n");

        sb.Append($"m=video 0 RTP/AVP {VideoPayloadType}\r\n");
        if (hub.Codec == VideoCodec.H264 && hub.Sps != null && hub.Pps != null)
        {
            string spsB64 = Convert.ToBase64String(hub.Sps);
            string ppsB64 = Convert.ToBase64String(hub.Pps);
            string profile = hub.Sps.Length >= 4 ? Convert.ToHexString(hub.Sps, 1, 3) : "42001E";
            sb.Append($"a=rtpmap:{VideoPayloadType} H264/90000\r\n");
            sb.Append($"a=fmtp:{VideoPayloadType} packetization-mode=1;profile-level-id={profile};sprop-parameter-sets={spsB64},{ppsB64}\r\n");
        }
        else if (hub.Codec == VideoCodec.H265)
        {
            sb.Append($"a=rtpmap:{VideoPayloadType} H265/90000\r\n");
            var parts = new List<string>();
            if (hub.Vps != null) parts.Add($"sprop-vps={Convert.ToBase64String(hub.Vps)}");
            if (hub.Sps != null) parts.Add($"sprop-sps={Convert.ToBase64String(hub.Sps)}");
            if (hub.Pps != null) parts.Add($"sprop-pps={Convert.ToBase64String(hub.Pps)}");
            if (parts.Count > 0)
                sb.Append($"a=fmtp:{VideoPayloadType} {string.Join(";", parts)}\r\n");
        }
        else
        {
            sb.Append($"a=rtpmap:{VideoPayloadType} H264/90000\r\n");
        }
        sb.Append("a=control:trackID=0\r\n");

        var audio = hub.Audio;
        if (audio != null)
        {
            sb.Append($"m=audio 0 RTP/AVP {AudioPayloadType}\r\n");
            if (audio.IsAac && audio.AudioSpecificConfig != null)
            {
                string config = Convert.ToHexString(audio.AudioSpecificConfig);
                sb.Append($"a=rtpmap:{AudioPayloadType} mpeg4-generic/{audio.SampleRate}/{audio.Channels}\r\n");
                sb.Append($"a=fmtp:{AudioPayloadType} streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config={config}\r\n");
            }
            else
            {
                sb.Append($"a=rtpmap:{AudioPayloadType} L16/{audio.SampleRate}/{audio.Channels}\r\n");
            }
            sb.Append("a=control:trackID=1\r\n");
        }

        return sb.ToString();
    }
}
