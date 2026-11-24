# Stream URLs and API

| URL | Content |
|---|---|
| `rtsp://host:8654/driveway` | main stream (alias) |
| `rtsp://host:8654/driveway/mainStream` | main stream (high resolution) |
| `rtsp://host:8654/driveway/subStream` | sub stream (low resolution) |
| `rtsp://host:8654/driveway?audio=opus` | same stream, audio transcoded to **Opus** (WebRTC-friendly; needs ffmpeg). Works on any camera path; `?audio=original` forces the camera's own track |
| `http://host:8655/` | web UI |
| `http://host:8655/api/cameras` | JSON list of cameras and stream state |
| `ws://host:8655/api/stream?path=/driveway/subStream` | live fMP4 (MSE-compatible) |
| `GET /api/cameras/driveway/capabilities` | device info + discovered features (ptz/led/pir/battery) |
| `GET /api/cameras/driveway/streaminfo` | encode profiles: resolution, framerate/bitrate options |
| `GET /api/cameras/driveway/battery` | battery charge/status (battery cameras) |
| `GET`/`POST /api/cameras/driveway/led` | status LED & floodlight: `{"state":"open"}`, `{"lightState":"close"}` |
| `GET`/`POST /api/cameras/driveway/pir` | PIR motion sensor: `{"enabled":true}` |
| `POST /api/cameras/driveway/ptz` | pan/tilt: `{"command":"left","speed":32}` (`up/down/left/right/stop`) |
| `POST /api/cameras/driveway/reboot` | reboot the camera |
| `POST /api/cameras/driveway/wake-hint` | external "the camera is up for an event" signal (battery cameras; see the battery guide) |

`POST` (control) endpoints require HTTP **Basic auth** when `users` are configured,
honouring the same per-camera `permitted_users` rules as RTSP; with no users
configured they are open, like everything else. Feature discovery is live: the
server probes the camera once per connection and the web UI only shows the
controls the camera actually supports.

## RTSP audio backchannel (two-way talk without the web UI)

Cameras with a speaker also expose an **ONVIF Profile-T audio backchannel** on the
same RTSP mount, so [go2rtc](https://github.com/AlexxIT/go2rtc), Home Assistant's
WebRTC Camera and other ONVIF-aware clients can talk through the camera. A client
that sends `Require: www.onvif.org/ver20/backchannel` on `DESCRIBE` is offered an
extra sendonly `PCMU/8000` track; the G.711 audio it streams is decoded and fed to
the same talk pipeline the web UI's mic button uses. Plain players (VLC, ffmpeg)
never see the extra track; it only appears when a client asks for it.

It rides the two-way-talk opt-in: set `"ui": { "talk": true }` (or *Server settings
→ Web UI → Two-way talk*). Example go2rtc source:

```yaml
streams:
  driveway:
    - rtsp://<neolink-host>:8654/driveway#backchannel=1
```

## WebRTC in Home Assistant

The [WebRTC Camera](https://github.com/AlexxIT/WebRTC) custom card (HACS) plays
Neolink streams over WebRTC with near-zero latency, straight from the RTSP URL:
its embedded go2rtc does the WebRTC lifting. Ask for `?audio=opus` so the audio
arrives in the codec WebRTC takes natively (no browser-side silence, no extra
ffmpeg hop on the HA box), and add `microphone` to get two-way talk through the
ONVIF backchannel above:

```yaml
type: custom:webrtc-camera
grid_options:
  columns: full
  rows: 12
streams:
  - url: rtsp://admin:password@<neolink-ip>:8654/FrontDoor?audio=opus
    name: FrontDoor
    mode: webrtc
    media: video,audio,microphone
```

Use your RTSP credentials from `users` in the URL (omit `admin:password@` when
none are configured). `?audio=opus` needs an ffmpeg next to Neolink (the Docker
image and Home Assistant add-on ship one); the `microphone` line needs two-way
talk enabled and a camera with a speaker.
