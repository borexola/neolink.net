# Using with Frigate

```yaml
cameras:
  driveway:
    ffmpeg:
      inputs:
        - path: rtsp://<neolink-host>:8654/driveway/subStream
          roles: [detect]
        - path: rtsp://<neolink-host>:8654/driveway/mainStream
          roles: [record]
```

Neolink.NET keeps exactly one connection per camera stream regardless of how many
Frigate roles/consumers attach, and hands stalled ffmpeg processes a hard disconnect
within 10 s so Frigate's watchdog recovers quickly. For headless Frigate boxes set
`"webui": false` (or `"web_port": 0`).

## PTZ buttons in Frigate

Frigate drives PTZ only over ONVIF, which many Reolink models lack. Neolink.NET
can answer it for them: pan/tilt, the camera's presets, and zoom on zoom lenses.
Turn it on per camera under **Cameras → Edit → External connection**, which also
shows the Frigate config to paste.

- **Shared port** (Frigate 0.18+): `"ptz_share": true`. Each such camera is a
  profile on `ptz_port` (8656), named after the camera, which Frigate's
  `onvif.profile` picks.
- **Own port** (older Frigate): `"ptz_port": 8657` on the camera.

```yaml
onvif:
  host: <neolink-host>
  port: 8656
  user: <RTSP user permitted on the camera>
  password: <its password>
  profile: office
```

- Frigate signs in as an RTSP user. With no users, PTZ only listens on a
  loopback `ptz_bind`.
- Presets come from the camera's HTTP API, or over Baichuan when it has none.
  Zoom is untested on a real zoom camera.
- Video stays on RTSP. In Docker, publish the PTZ port or use host networking.

## Click-to-move and autotracking in Frigate (beta)

> 📖 Setup and a sample Frigate config: **[autotracking.md](autotracking.md)**

Frigate's click-to-move and [autotracking](https://docs.frigate.video/configuration/autotracking/)
ask the camera to move by a fraction of the picture, which a Reolink head cannot
do. Neolink.NET runs each such move as a timed pan followed by a timed tilt, worked
out from the lens's angle of view and the head's speed. Turn it on under **Server
settings → Cameras → Edit**: tick **This camera pans and tilts** in the
beta *Click-to-move and autotracking* section, then give the lens's angle
and how many seconds a full pan and a full tilt take. Or add the settings to a camera
that already has `ptz_share` or `ptz_port`:

```json
{ "name": "office", "ptz_share": true, "ptz_fov_h": 87.5, "ptz_pan_rate": 30 }
```

| Option | Default | Description |
|---|---|---|
| `ptz_fov_h` | *required* | Horizontal angle of view in degrees, from the camera's spec sheet (a zoom lens: at its widest) |
| `ptz_pan_rate` | *required* | Degrees per second the head pans at `ptz_speed`: time a pan over a known angle (end stop to end stop is the spec sheet's pan range) and divide. Frigate's arrow buttons drive at the default speed |
| `ptz_fov_v` | from the picture's shape | Vertical angle of view in degrees |
| `ptz_tilt_rate` | = `ptz_pan_rate` | Degrees per second of tilt. Most heads tilt slower than they pan, so time it too |
| `ptz_lag_ms` | *learned, or 0* | Added to every move for the motor getting under way: raise it if short moves fall short. Left unset, a camera that reports its head position learns it |
| `ptz_speed` | `32` | The camera's PTZ speed (1-64) moves run at, the one the rates are timed at. The Speed slider in the camera panel drives at any speed for timing |
| `ptz_tele_fov_h` | *(none)* | Zoom lenses: the horizontal angle at full zoom, so that moves shrink as the lens zooms in |

- **Tune it with click-to-move first.** Click a spot in Frigate's live view: if
  the head goes past it, raise the rate; if it stops short, lower it. Sideways
  misses are `ptz_pan_rate`, up and down ones `ptz_tilt_rate`.
- **Then autotracking.** Follow Frigate's guide: a `return_preset` saved on the
  camera, and one run with `calibrate_on_startup: true`. With these settings a
  move to a preset counts as under way until the head is back, which the
  calibration waits for. Zoom lenses also offer its `zooming: absolute`.
- **A slow head.** If Frigate logs "Autotracking calibration failed", the head
  is too slow at this speed for Frigate's movement prediction. Raise
  `ptz_speed` and time both rates again at it; Frigate still tracks without the
  prediction meanwhile.
- **Expect limits.** The head turns one axis at a time, so a quick subject gets
  away, and the timing rides on the network: cable the camera if you can. Zoom
  lenses and position-reporting heads are untested on real cameras.
