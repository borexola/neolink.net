// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Xml.Linq;
using Neolink.Bc.Xml;

namespace Neolink.Streaming;

/// <summary>
/// The control surface of a generic RTSP camera: it streams, and that is all.
/// Capabilities report no features (so the UI hides PTZ/LED/PIR/battery and the
/// stream-settings editor), snapshots return null (events store without a thumb),
/// and everything imperative is NotSupported — the web API maps that to 404.
/// </summary>
public sealed class GenericCameraControl : ICameraControl
{
    private readonly IReadOnlyList<RtspCameraService> _services;

    public GenericCameraControl(string cameraName, IReadOnlyList<RtspCameraService> services)
    {
        CameraName = cameraName;
        _services = services;
    }

    public string CameraName { get; }

    public bool Online => _services.Any(s => s.Online);

    public Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new CameraCapabilities(
            Version: null,
            Support: null,
            Features: new CameraFeatures(Ptz: false, Led: false, Pir: false, Battery: false, Talk: false)));

    public Task<StreamInfoListXml?> GetStreamInfoAsync(CancellationToken ct) =>
        Task.FromResult<StreamInfoListXml?>(null);

    public bool CanSetStreamSettings => false;

    public Task<IReadOnlyList<StreamEncSetting>?> GetStreamSettingsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StreamEncSetting>?>(null);

    public Task SetStreamSettingsAsync(string stream, uint? width, uint? height,
        uint? framerate, uint? bitrate, CancellationToken ct) =>
        throw new NotSupportedException("stream settings are not available for generic RTSP cameras");

    public Task<XElement?> GetBatteryInfoAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task<byte[]?> SnapshotAsync(CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public Task<XElement?> GetLedStateAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetLedStateAsync(string? state, string? lightState,
        string? doorbellLightState, int? irBrightness, CancellationToken ct) =>
        throw new NotSupportedException("LED control is not available for generic RTSP cameras");

    public Task<XElement?> GetPirStateAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetPirEnabledAsync(bool enabled, CancellationToken ct) =>
        throw new NotSupportedException("PIR control is not available for generic RTSP cameras");

    public Task PtzAsync(string command, float speed, CancellationToken ct) =>
        throw new NotSupportedException("PTZ is not available for generic RTSP cameras");

    public Task RebootAsync(CancellationToken ct) =>
        throw new NotSupportedException("reboot is not available for generic RTSP cameras");

    public Task<XElement?> GetZoomFocusAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetZoomFocusAsync(string command, uint movePos, CancellationToken ct) =>
        throw new NotSupportedException("zoom/focus is not available for generic RTSP cameras");

    public Task SirenAsync(bool? on, CancellationToken ct) =>
        throw new NotSupportedException("the siren is not available for generic RTSP cameras");

    public Task<bool?> GetPrivacyModeAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task SetPrivacyModeAsync(bool on, CancellationToken ct) =>
        throw new NotSupportedException("privacy mode is not available for generic RTSP cameras");

    public Task<XElement?> GetFloodlightTasksAsync(CancellationToken ct) => Task.FromResult<XElement?>(null);

    public Task SetFloodlightTasksAsync(XElement task, CancellationToken ct) =>
        throw new NotSupportedException("floodlight control is not available for generic RTSP cameras");

    public Task<WhiteLedState?> GetWhiteLedAsync(CancellationToken ct) => Task.FromResult<WhiteLedState?>(null);

    public Task SetWhiteLedAsync(int? bright, bool? on, int? mode, CancellationToken ct) =>
        throw new NotSupportedException("white-LED control is not available for generic RTSP cameras");

    // The HTTP-API extras all need a Reolink HTTP API, which a generic RTSP camera has not.
    public Task<HttpFeatures?> GetHttpFeaturesAsync(CancellationToken ct) => Task.FromResult<HttpFeatures?>(null);

    public Task<ImageSettings?> GetImageSettingsAsync(CancellationToken ct) => Task.FromResult<ImageSettings?>(null);

    public Task SetImageSettingsAsync(int? bright, int? contrast, int? saturation, int? hue, int? sharpen,
        string? dayNight, string? antiFlicker, bool? flip, bool? mirror, CancellationToken ct) =>
        throw new NotSupportedException("picture settings are not available for generic RTSP cameras");

    public Task<int?> GetVolumeAsync(CancellationToken ct) => Task.FromResult<int?>(null);

    public Task SetVolumeAsync(int volume, CancellationToken ct) =>
        throw new NotSupportedException("the speaker volume is not available for generic RTSP cameras");

    public Task<WifiReading?> GetWifiSignalAsync(CancellationToken ct) => Task.FromResult<WifiReading?>(null);

    public Task<IReadOnlyList<PtzPresetInfo>?> GetPtzPresetsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PtzPresetInfo>?>(null);

    public Task PtzToPresetAsync(int id, CancellationToken ct) =>
        throw new NotSupportedException("PTZ presets are not available for generic RTSP cameras");

    public Task SavePtzPresetAsync(int id, string name, CancellationToken ct) =>
        throw new NotSupportedException("PTZ presets are not available for generic RTSP cameras");

    public Task<IReadOnlyList<QuickReplyFile>?> GetQuickRepliesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QuickReplyFile>?>(null);

    public Task PlayQuickReplyAsync(int id, CancellationToken ct) =>
        throw new NotSupportedException("quick replies are not available for generic RTSP cameras");

    public Task<AutoReplyState?> GetAutoReplyAsync(CancellationToken ct) =>
        Task.FromResult<AutoReplyState?>(null);

    public Task SetAutoReplyAsync(int? fileId, int? timeoutSeconds, CancellationToken ct) =>
        throw new NotSupportedException("the auto-reply is not available for generic RTSP cameras");

    public Task<bool?> GetAutoTrackAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task SetAutoTrackAsync(bool on, CancellationToken ct) =>
        throw new NotSupportedException("auto-tracking is not available for generic RTSP cameras");

    public Task<IReadOnlyList<SdCardInfo>?> GetSdCardsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SdCardInfo>?>(null);

    public Task TalkAsync(int sampleRate, System.Threading.Channels.ChannelReader<byte[]> pcm, CancellationToken ct) =>
        throw new NotSupportedException("two-way talk is not available for generic RTSP cameras");
}
