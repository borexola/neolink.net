// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Security;
using Neolink.Bc.Xml;
using Neolink.Streaming;

namespace Neolink.Onvif;

/// <summary>The SOAP replies, children in ONVIF schema order: a strict parser (zeep) skips what is out of place.</summary>
public sealed partial class OnvifPtzServer
{
    internal const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
    internal const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
    internal const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
    internal const string NsPtz = "http://www.onvif.org/ver20/ptz/wsdl";
    internal const string NsSchema = "http://www.onvif.org/ver10/schema";
    private const string NsError = "http://www.onvif.org/ver10/error";
    private const string VelocitySpace = "http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace";
    private const string SpeedSpace = "http://www.onvif.org/ver10/tptz/PanTiltSpaces/GenericSpeedSpace";
    private const string PositionSpace = "http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace";
    private const string ZoomPositionSpace = "http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace";
    private const string ZoomVelocitySpace = "http://www.onvif.org/ver10/tptz/ZoomSpaces/VelocityGenericSpace";
    private const string ZoomSpeedSpace = "http://www.onvif.org/ver10/tptz/ZoomSpaces/ZoomGenericSpeedSpace";

    private static readonly string AppVersion =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(OnvifPtzServer).Assembly)?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    private static string Envelope(string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<s:Envelope xmlns:s=\"{NsSoap}\" xmlns:tt=\"{NsSchema}\" xmlns:tds=\"{NsDevice}\" " +
        $"xmlns:trt=\"{NsMedia}\" xmlns:tptz=\"{NsPtz}\" xmlns:ter=\"{NsError}\">" +
        $"<s:Body>{body}</s:Body></s:Envelope>";

    private static (int, string) Ok(string body) => (200, Envelope(body));

    /// <summary>A SOAP 1.2 fault: a Sender fault rides HTTP 400, a Receiver fault 500.</summary>
    private static (int, string) Fault(bool sender, string subcode, string reason) =>
        (sender ? 400 : 500, Envelope(
            $"<s:Fault><s:Code><s:Value>{(sender ? "s:Sender" : "s:Receiver")}</s:Value>" +
            $"<s:Subcode><s:Value>{subcode}</s:Value></s:Subcode></s:Code>" +
            $"<s:Reason><s:Text xml:lang=\"en\">{Esc(reason)}</s:Text></s:Reason></s:Fault>"));

    // ------------------------------------------------------------------ device

    internal static string SystemDateAndTime(DateTime utc) =>
        "<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime>" +
        "<tt:DateTimeType>NTP</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings>" +
        "<tt:TimeZone><tt:TZ>UTC0</tt:TZ></tt:TimeZone>" +
        "<tt:UTCDateTime>" +
        $"<tt:Time><tt:Hour>{utc.Hour}</tt:Hour><tt:Minute>{utc.Minute}</tt:Minute><tt:Second>{utc.Second}</tt:Second></tt:Time>" +
        $"<tt:Date><tt:Year>{utc.Year}</tt:Year><tt:Month>{utc.Month}</tt:Month><tt:Day>{utc.Day}</tt:Day></tt:Date>" +
        "</tt:UTCDateTime></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>";

    private static string Capabilities(string baseUrl) =>
        "<tds:GetCapabilitiesResponse><tds:Capabilities>" +
        $"<tt:Device><tt:XAddr>{Esc(baseUrl)}/onvif/device_service</tt:XAddr></tt:Device>" +
        $"<tt:Media><tt:XAddr>{Esc(baseUrl)}/onvif/media_service</tt:XAddr>" +
        "<tt:StreamingCapabilities><tt:RTPMulticast>false</tt:RTPMulticast><tt:RTP_TCP>true</tt:RTP_TCP>" +
        "<tt:RTP_RTSP_TCP>true</tt:RTP_RTSP_TCP></tt:StreamingCapabilities></tt:Media>" +
        $"<tt:PTZ><tt:XAddr>{Esc(baseUrl)}/onvif/ptz_service</tt:XAddr></tt:PTZ>" +
        "</tds:Capabilities></tds:GetCapabilitiesResponse>";

    private static string Services(string baseUrl)
    {
        static string Service(string ns, string url, int major) =>
            $"<tds:Service><tds:Namespace>{ns}</tds:Namespace><tds:XAddr>{Esc(url)}</tds:XAddr>" +
            $"<tds:Version><tt:Major>{major}</tt:Major><tt:Minor>0</tt:Minor></tds:Version></tds:Service>";
        return "<tds:GetServicesResponse>" +
               Service(NsDevice, baseUrl + "/onvif/device_service", 2) +
               Service(NsMedia, baseUrl + "/onvif/media_service", 2) +
               Service(NsPtz, baseUrl + "/onvif/ptz_service", 2) +
               "</tds:GetServicesResponse>";
    }

    private const string DeviceServiceCapabilities =
        "<tds:GetServiceCapabilitiesResponse><tds:Capabilities><tds:Network/>" +
        "<tds:Security UsernameToken=\"true\" HttpDigest=\"false\"/><tds:System/>" +
        "</tds:Capabilities></tds:GetServiceCapabilitiesResponse>";

    /// <summary>One camera's own identity once it has answered; for several, Neolink's.</summary>
    private static string DeviceInformation(IReadOnlyList<OnvifPtzCamera> visible)
    {
        static string Or(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
        var one = visible.Count == 1 ? visible[0] : null;
        VersionInfoXml? v = one?.KnownCapabilities()?.Version;
        return "<tds:GetDeviceInformationResponse>" +
               $"<tds:Manufacturer>{(one == null ? "Neolink.NET" : "Reolink")}</tds:Manufacturer>" +
               $"<tds:Model>{Esc(one == null ? "PTZ for Reolink cameras" : Or(v?.Model, one.Name))}</tds:Model>" +
               $"<tds:FirmwareVersion>{Esc(one == null ? AppVersion : Or(v?.FirmwareVersion, "unknown"))}</tds:FirmwareVersion>" +
               $"<tds:SerialNumber>{Esc(one == null ? "neolink-ptz" : Or(v?.SerialNumber, one.Name))}</tds:SerialNumber>" +
               $"<tds:HardwareId>{Esc(one == null ? "Neolink.NET" : Or(v?.HardwareVersion, "Neolink.NET"))}</tds:HardwareId>" +
               "</tds:GetDeviceInformationResponse>";
    }

    // ------------------------------------------------------------------ media

    /// <summary>A camera's profile: its main stream, with its PTZ configuration unless it has neither pan/tilt nor zoom.
    /// Encoding says H264 whatever the stream is: Media ver10 has no H.265, and video is not served here.</summary>
    private static string Profile(string element, OnvifPtzCamera cam)
    {
        var (w, h) = cam.Size();
        var name = Esc(cam.Name);
        return $"<{element} token=\"{Esc(cam.ProfileToken)}\" fixed=\"true\"><tt:Name>{name}</tt:Name>" +
               $"<tt:VideoSourceConfiguration token=\"video_config_{name}\"><tt:Name>{name}</tt:Name><tt:UseCount>1</tt:UseCount>" +
               $"<tt:SourceToken>video_{name}</tt:SourceToken><tt:Bounds x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\"/>" +
               "</tt:VideoSourceConfiguration>" +
               $"<tt:VideoEncoderConfiguration token=\"encoder_{name}\"><tt:Name>mainStream</tt:Name><tt:UseCount>1</tt:UseCount>" +
               $"<tt:Encoding>H264</tt:Encoding><tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>" +
               "<tt:Quality>5</tt:Quality>" +
               "<tt:Multicast><tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>" +
               "<tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart></tt:Multicast>" +
               "<tt:SessionTimeout>PT60S</tt:SessionTimeout></tt:VideoEncoderConfiguration>" +
               (cam.OffersPtz() ? PtzConfiguration("tt:PTZConfiguration", cam) : "") +
               $"</{element}>";
    }

    private static string VideoSource(OnvifPtzCamera cam)
    {
        var (w, h) = cam.Size();
        return $"<trt:VideoSources token=\"video_{Esc(cam.Name)}\"><tt:Framerate>25</tt:Framerate>" +
               $"<tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution></trt:VideoSources>";
    }

    private static string MediaServiceCapabilities(int profiles) =>
        "<trt:GetServiceCapabilitiesResponse><trt:Capabilities SnapshotUri=\"false\" Rotation=\"false\">" +
        $"<trt:ProfileCapabilities MaximumNumberOfProfiles=\"{profiles}\"/>" +
        "<trt:StreamingCapabilities RTPMulticast=\"false\" RTP_TCP=\"true\" RTP_RTSP_TCP=\"true\"/>" +
        "</trt:Capabilities></trt:GetServiceCapabilitiesResponse>";

    // ------------------------------------------------------------------ PTZ

    private const string PtzServiceCapabilities =
        "<tptz:GetServiceCapabilitiesResponse><tptz:Capabilities EFlip=\"false\" Reverse=\"false\" " +
        "GetCompatibleConfigurations=\"false\" MoveStatus=\"true\" StatusPosition=\"false\"/>" +
        "</tptz:GetServiceCapabilitiesResponse>";

    private static string PtzConfigTokenOf(OnvifPtzCamera cam) => "ptz_" + cam.Name;
    private static string NodeTokenOf(OnvifPtzCamera cam) => "node_" + cam.Name;

    /// <summary>A camera's PTZ configuration: continuous pan/tilt and, on a zoom lens, continuous zoom. Frigate
    /// reads its "pt" and "zoom" features from these default spaces.</summary>
    private static string PtzConfiguration(string element, OnvifPtzCamera cam)
    {
        bool panTilt = cam.HasPanTilt, zoom = cam.HasZoom;
        return $"<{element} token=\"{Esc(PtzConfigTokenOf(cam))}\"><tt:Name>{Esc(cam.Name)}</tt:Name><tt:UseCount>1</tt:UseCount>" +
               $"<tt:NodeToken>{Esc(NodeTokenOf(cam))}</tt:NodeToken>" +
               (panTilt ? $"<tt:DefaultContinuousPanTiltVelocitySpace>{VelocitySpace}</tt:DefaultContinuousPanTiltVelocitySpace>" : "") +
               (zoom ? $"<tt:DefaultContinuousZoomVelocitySpace>{ZoomVelocitySpace}</tt:DefaultContinuousZoomVelocitySpace>" : "") +
               "<tt:DefaultPTZSpeed>" +
               (panTilt ? $"<tt:PanTilt x=\"0.5\" y=\"0.5\" space=\"{SpeedSpace}\"/>" : "") +
               (zoom ? $"<tt:Zoom x=\"0.5\" space=\"{ZoomSpeedSpace}\"/>" : "") +
               "</tt:DefaultPTZSpeed>" +
               $"<tt:DefaultPTZTimeout>{XmlDuration(DefaultMoveTimeout)}</tt:DefaultPTZTimeout>" +
               $"</{element}>";
    }

    private static string Spaces(OnvifPtzCamera cam)
    {
        bool panTilt = cam.HasPanTilt, zoom = cam.HasZoom;
        return (panTilt
                   ? $"<tt:ContinuousPanTiltVelocitySpace><tt:URI>{VelocitySpace}</tt:URI>" +
                     "<tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>" +
                     "<tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange></tt:ContinuousPanTiltVelocitySpace>"
                   : "") +
               (zoom
                   ? $"<tt:ContinuousZoomVelocitySpace><tt:URI>{ZoomVelocitySpace}</tt:URI>" +
                     "<tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange></tt:ContinuousZoomVelocitySpace>"
                   : "") +
               (panTilt
                   ? $"<tt:PanTiltSpeedSpace><tt:URI>{SpeedSpace}</tt:URI>" +
                     "<tt:XRange><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:XRange></tt:PanTiltSpeedSpace>"
                   : "") +
               (zoom
                   ? $"<tt:ZoomSpeedSpace><tt:URI>{ZoomSpeedSpace}</tt:URI>" +
                     "<tt:XRange><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:XRange></tt:ZoomSpeedSpace>"
                   : "");
    }

    private static string PtzConfigurationOptions(OnvifPtzCamera cam) =>
        "<tptz:GetConfigurationOptionsResponse><tptz:PTZConfigurationOptions>" +
        $"<tt:Spaces>{Spaces(cam)}</tt:Spaces>" +
        $"<tt:PTZTimeout><tt:Min>PT1S</tt:Min><tt:Max>{XmlDuration(MaxMoveTimeout)}</tt:Max></tt:PTZTimeout>" +
        "</tptz:PTZConfigurationOptions></tptz:GetConfigurationOptionsResponse>";

    private static string PtzNode(OnvifPtzCamera cam) =>
        $"<tptz:PTZNode token=\"{Esc(NodeTokenOf(cam))}\" FixedHomePosition=\"false\"><tt:Name>{Esc(cam.Name)}</tt:Name>" +
        $"<tt:SupportedPTZSpaces>{Spaces(cam)}</tt:SupportedPTZSpaces>" +
        $"<tt:MaximumNumberOfPresets>{cam.PresetSlots}</tt:MaximumNumberOfPresets><tt:HomeSupported>false</tt:HomeSupported>" +
        "</tptz:PTZNode>";

    private static string Preset(PtzPresetInfo p) =>
        $"<tptz:Preset token=\"{p.Id}\"><tt:Name>{Esc(p.Name)}</tt:Name></tptz:Preset>";

    /// <summary>Whether the head and lens move. The camera reports no position, so it is given as the origin:
    /// Frigate's setup reads Position unguarded.</summary>
    private static string PtzStatus(bool moving, bool zooming, DateTime utc) =>
        "<tptz:GetStatusResponse><tptz:PTZStatus>" +
        $"<tt:Position><tt:PanTilt x=\"0\" y=\"0\" space=\"{PositionSpace}\"/><tt:Zoom x=\"0\" space=\"{ZoomPositionSpace}\"/></tt:Position>" +
        $"<tt:MoveStatus><tt:PanTilt>{(moving ? "MOVING" : "IDLE")}</tt:PanTilt><tt:Zoom>{(zooming ? "MOVING" : "IDLE")}</tt:Zoom></tt:MoveStatus>" +
        $"<tt:UtcTime>{utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</tt:UtcTime>" +
        "</tptz:PTZStatus></tptz:GetStatusResponse>";

    private static string XmlDuration(TimeSpan t) => System.Xml.XmlConvert.ToString(t);
}
