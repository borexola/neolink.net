# Home Assistant (MQTT)

Add an `mqtt` section and Neolink.NET connects to your broker and publishes
[Home Assistant MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery)
config, so a **device per camera** appears automatically — no YAML in HA.

![A camera's Home Assistant device page: controls (floodlight, night vision, PTZ, privacy mode, siren, reboot), detection sensors and an activity feed — all auto-discovered over MQTT](home-assistant.png)

```json
"mqtt": {
  "broker": "192.168.1.10",
  "username": "neolink",
  "password": "secret"
}
```

| Option | Default | Description |
|---|---|---|
| `broker` | *required* | MQTT broker host (usually the Home Assistant / Mosquitto box) |
| `port` | `1883` | Broker port (`8883` with `tls: true`) |
| `username` / `password` | *(none)* | Broker credentials |
| `client_id` | `neolink` | Client id (must be unique on the broker) |
| `base_topic` | `neolink` | Root of the state/command topics |
| `discovery` | `true` | Publish HA discovery config so entities appear automatically |
| `discovery_prefix` | `homeassistant` | Must match HA's MQTT integration setting |
| `keepalive` | `30` | Keep-alive interval (seconds) |
| `max_packet_size` | `2000000` | Largest MQTT packet the broker accepts, in bytes — see *Packet sizing* below |
| `tls` | `false` | Connect with TLS (certificates are not validated) |

## Packet sizing (camera snapshots vs the broker's limit)

Mosquitto 2.1+ ships a new default `max_packet_size` of **2 MB** and answers a
bigger publish by **disconnecting the client** — it doesn't just drop the one
packet. The only payload Neolink.NET publishes that can get near that limit is
the per-camera **Snapshot** image (base64 JPEG), and a dual-lens or 4K camera's
snapshot easily exceeds it: a Duo's panorama is 3+ MB even at sub-stream
resolution.

Neolink.NET handles this so the shared connection is never at risk:

- The bridge asks the camera for the **smallest snapshot it can produce**
  (extern stream, then sub stream, then the full image as a last resort).
- Any publish still larger than `max_packet_size` is **dropped with a one-time
  warning** naming the topic and size — that camera's HA picture goes stale,
  everything else keeps flowing, and the broker never disconnects the bridge.

If a camera's smallest snapshot is still over 2 MB and you want its picture in
HA anyway, raise the limit **on both sides** (they must agree — the neolink
setting only governs what it is willing to send):

1. **Broker** — for the Home Assistant Mosquitto add-on, enable *customize* in
   the add-on's configuration and drop a file in `/share/mosquitto/`
   (e.g. `neolink.conf`) containing:

   ```
   max_packet_size 8000000
   ```

   then restart the add-on.
2. **Neolink.NET** — in the `mqtt` section:

   ```json
   "max_packet_size": 8000000
   ```

Alternatively, skip MQTT for the picture entirely: point a Home Assistant
**Generic Camera** integration at the full-resolution
[HTTP snapshot endpoint](#snapshots-over-http) — no broker limits apply there.

**Entities** created per camera, according to what it supports:

| Entity | Type | Notes |
|---|---|---|
| Motion / Person / Vehicle / Animal | `binary_sensor` | From the camera's alarm pushes (AI labels need Smart Detection enabled in the Reolink app) |
| Package / Crying / Line crossing / Intrusion / Loitering | `binary_sensor` | Created up front like the core four, so automations can be built before the first event — they stay **Clear** until the camera pushes one (smart/perimeter detection must be configured in the Reolink app for that to ever happen). Crying is the indoor cams' audio detection and uses device class `sound` |
| Doorbell | `event` | Video doorbells: every button press publishes an MQTT event (`event_type: press`, `device_class: doorbell`) — the natural trigger for ring automations |
| Visitor | `binary_sensor` | Momentary doorbell-press pulse; HA clears it itself after a few seconds |
| Record on demand | `switch` | **Record a clip on demand from HA**, regardless of what the camera detects — one clip, stops by itself; see below (appears when the server records events for this camera) |
| Detection events | `switch` | The camera's **master toggle for event capture** — the same "Detection events" switch as the web UI's camera settings, so the two always agree. OFF stops the server recording event clips for this camera (and on-demand capture) until switched back on. The camera keeps detecting, so the detection binary_sensors above still report — this pauses recording, it isn't a sensor disarm. Stays usable while the camera is offline (appears when the server records events for this camera) |
| Continuous recording | `switch` | **24/7 recording on/off** — the same "Record around the clock" toggle as the web UI, so the two always agree. ON tapes continuously (retention still applies); the recorder picks it up at once. Stays usable while the camera is offline (appears when continuous recording is available for this camera) |
| Suspend (beta) | `switch` | ON = Neolink.NET holds no connection to the camera, so it isn't viewed or recorded here (the camera itself keeps running — its own SD/cloud recording is unaffected). Stays usable while the camera is intentionally offline |
| Recording | `binary_sensor` | ON while the server is writing this camera's footage right now — an event clip (detection or on-demand) or a continuous segment |
| Battery | `sensor` | Battery cameras; charge status + temperature as attributes |
| Asleep | `binary_sensor` | Battery cameras: ON while the camera is dozing **on purpose** (parked between viewers). The camera stays *available* in HA with its retained readings while it naps — latched detection sensors are cleared on the way into the nap — and only a genuinely unreachable camera reads Unavailable (diagnostic) |
| Wi-Fi signal | `sensor` | Diagnostic; RSSI in dBm from the camera's own status pushes (Wi-Fi cameras) |
| Siren | `switch` | Sound the siren until turned off (audio-alarm cameras); state follows the camera's own siren pushes, so it stays honest even when the camera's rules trigger it |
| Siren sounding | `binary_sensor` | Read-only: ON while the siren is sounding (appears on the first status push) |
| Night vision | `select` | `auto` / `on` / `off` |
| Floodlight | `light` | Cameras with a spotlight |
| PIR sensor | `switch` | Enable/disable the PIR |
| Reboot, Pan up/down/left/right | `button` | PTZ buttons on pan-tilt cameras |
| Snapshot | `camera` | Latest JPEG, refreshed periodically (when the camera supports snapshots) |
| Volume (beta) | `number` | Speaker volume 0-100 via the camera's HTTP API — governs sirens, alerts and two-way talk |
| Record audio (beta) | `switch` | The camera-side flag that puts the microphone into every stream and recording — only on cameras whose firmware exposes it |
| Auto-tracking (beta) | `switch` | Follow detected subjects, on cameras that support AI tracking |
| PTZ preset (beta) | `select` | The camera's saved positions; picking one moves the camera there |
| Spotlight (beta) | `light` | White-LED cameras (Lumus/Elite): on/off, plus brightness where the HTTP white LED answers |
| IR brightness | `number` | Infrared LED intensity 0-100, on cameras that report it |
| Doorbell light (beta) | `switch` | The doorbell's button light |
| Play quick reply (beta) | `select` | Video doorbells: picking a pre-recorded message plays it through the speaker |
| Picture settings (beta) | `number`/`select`/`switch` | Image brightness/contrast/saturation/hue/sharpness, day/night mode, anti-flicker, flip and mirror — per what the camera reports (config category) |
| Motion sensitivity | `number` | The camera's own motion-detection threshold, 1-50, higher = more sensitive (normalized across firmware dialects) |
| Person / Vehicle / Animal / Face / Package sensitivity | `number` | Per-type AI detection sensitivity 0-100 — one entity per type this camera's firmware answers for |
| HDR (beta) | `select` | Cameras whose ISP reports HDR: `off`/`on`, or `off`/`low`/`high` on three-step firmwares |
| Firmware update | `binary_sensor` (`update`) | Read-only diagnostic: ON when Reolink offers newer firmware for this camera (checked by the camera itself, cached for hours). Update from the Reolink app — Neolink.NET never installs firmware |

A separate **Neolink.NET Server** device carries the server's own health
(published every `stats_interval` seconds): CPU, memory, recordings size, write
rate, viewers, cameras online/recording, uptime, and storage:

| Entity | Type | Notes |
|---|---|---|
| Storage free / Storage used | `sensor` | The main recordings volume (`recording.path`) |
| Clips free / Clips used | `sensor` | Only when a separate `clips_path` is configured |
| Archive free / Archive used | `sensor` | Only when a separate `archive_path` is configured |
| Storage full | `binary_sensor` (`problem`) | ON when any recording drive is out of space and recording to it has stopped — the hook for an unattended "free up space" notification even when nobody has the web UI open |

Only storage that actually exists is published: a plain single-folder install
gets no clips/archive sensors, and removing a tier later clears its sensors from
HA automatically.

**Doorbell presses** are published to `{base_topic}/{camera}/doorbell` with the
payload `{"event_type":"press"}`, so non-HA consumers (Node-RED, scripts) can
subscribe to the same topic. Press events are deliberately **not retained** —
a retained press would re-ring your automations after every broker restart. The
entity is announced on the first detected press, so it appears in HA the first
time someone rings. Presses are also logged and recorded as regular "Doorbell
pressed" events with pre-roll video.

**On-demand recording (the Record on demand switch).** Most Reolink firmwares cannot be
told to record on demand — but Neolink.NET is already the recorder, so it doesn't
need the camera's cooperation. Switching ON records **one clip** for that
camera exactly as if a detection were running: pre-roll included, retention
applies, and the footage appears in the timeline and review strip labeled
**External**. The recording **stops by itself** when the clip reaches
`max_clip_seconds` (the switch flips back OFF — retrigger it for a longer
capture) or when you switch it OFF early. The trigger deliberately ignores the
camera's event-type filter and capture schedule (it is explicit intent, not a
detection) but respects the per-camera events on/off switch.

The same recording can be started from the web UI: press the **⏺ record
button** in any camera tile's toolbar (it sits next to the mic on the
maximized view). A red chip with a countdown sits on the video for as long as the clip records —
whichever side started it, the web UI and the HA switch always show the same
state. A "record while the door is open" automation:

```yaml
automation:
  - alias: Record garage cam while the door is open
    trigger:
      - platform: state
        entity_id: binary_sensor.garage_door
    action:
      - service: >
          {{ 'switch.turn_on' if trigger.to_state.state == 'on' else 'switch.turn_off' }}
        target:
          entity_id: switch.garage_record_on_demand
```

(Setups that added the camera before the switch was renamed keep their original
`switch.garage_record` entity id — discovery reuses the same unique id.)

(A door open longer than `max_clip_seconds` ends the clip at the cap; the
`turn_off` when the door closes is then simply a no-op.)

Non-HA consumers can publish `ON` / `OFF` to `{base_topic}/{camera}/record/set`
directly, or use the web API: `POST /api/cameras/{name}/record` with
`{"active": true|false}`.

Availability is two-level: entities show **unavailable** when either the Neolink.NET
service (a Last-Will topic) or the individual camera goes offline. State and
discovery messages are retained (press events excepted, as above), so Home
Assistant repopulates after a restart.
Commands from HA (toggle the floodlight, reboot, nudge PTZ…) are executed on the
camera over the same Baichuan connection. No external MQTT library is used —
Neolink.NET speaks MQTT 3.1.1 directly, keeping the zero-dependency build.

> Plain MQTT (port 1883) is unencrypted. For a LAN broker that's typical; enable
> `tls` (port 8883) if the broker is remote.

## Tap a phone alert straight to the footage

The web UI's **Events page is deep-linkable**: `…/events/{camera}` opens filtered
to that camera with its newest clip already playing. Point a Home Assistant
notification's tap action at it and a "motion detected" push takes you one tap
from the alert to the recording (and a **Go live** button is right there to jump
to the feed):

```yaml
automation:
  - alias: Notify on driveway motion
    trigger:
      - platform: state
        entity_id: binary_sensor.driveway_motion   # a Neolink.NET camera sensor
        to: "on"
    action:
      - service: notify.mobile_app_your_phone
        data:
          title: Driveway motion
          message: Motion detected on the driveway
          data:
            # HA companion app: tapping the notification opens this URL
            clickAction: https://neolink.example.com/events/Driveway
```

Plain `/events` (no camera) opens the full recent-events list. And when you know
the exact event — its `id` from `GET /api/events` — `/events?event={id}` opens
and plays that specific clip, even one older than the recent list (the page
jumps to its day). All forms require the web UI to be reachable from the phone —
usually via your reverse proxy.

**Linking to the exact event from HA**: with MQTT enabled, every camera that
records events gets a **Last event** sensor (e.g.
`sensor.driveway_last_event`) whose state is the newest event's id — published
the instant the event starts, alongside the motion trigger, so it is already
current inside the automation that the motion fired. Point the tap action at
the exact clip:

```yaml
          data:
            clickAction: >-
              https://neolink.example.com/events?event={{ states('sensor.driveway_last_event') }}
```

(You can also trigger the automation on the Last event sensor itself — a state
change *is* a new recording, and `trigger.to_state.state` is the id.) Clips
that auto-open from such a link start muted; tap the speaker to unmute.

## Snapshots over HTTP

`GET /api/cameras/{name}/snapshot.jpg` returns a current still image, straight
from the camera's own JPEG snapshot command — the classic NVR primitive for
notification thumbnails, wall-mounted dashboards and scripts:

```
http://neolink:8655/api/cameras/Driveway/snapshot.jpg
```

- **Poll it as hard as you like**: the server answers from a short cache
  (default 5 seconds; `?maxAge=` seconds tunes it, `?maxAge=0` forces a fresh
  frame) and collapses simultaneous requests into one camera command, so a
  dashboard refreshing every second still reaches the camera at a gentle pace.
- **Battery cameras are never woken by a poll.** A sleeping camera serves its
  last frame instead, honestly labelled: `X-Snapshot-Age` (seconds) is on every
  response, plus `X-Snapshot-Stale: true` when it's older than requested. No
  frame at all yet → `503` with a JSON error.
- Cameras without the snapshot command (generic RTSP cameras) return `404`.
- **Auth works like the stream URLs**: the same RTSP user credentials you use
  in `rtsp://user:pass@host:8654/{camera}/subStream` open the snapshot over
  HTTP Basic — `http://user:pass@host:8655/api/cameras/{camera}/snapshot.jpg`,
  or the username/password fields of HA's Generic Camera integration — with
  the same per-camera `permitted` rules, and it keeps working when web-UI
  accounts are enabled (a web session or `?token=` works too). With neither
  RTSP users nor accounts configured the URL is open, exactly like the
  streams. (HA users with MQTT also get a **Snapshot** `camera` entity for
  free — `image: /api/camera_proxy/camera.{name}_snapshot` in a notification.)

### Event footage over HTTP

An event's media takes the same RTSP Basic credentials as the snapshot — it is
footage, so the footage rules apply (per-camera `permitted` included):

```
http://user:pass@neolink:8655/api/events/{id}/thumb     (JPEG thumbnail)
http://user:pass@neolink:8655/api/events/{id}/preview   (low-res MP4)
http://user:pass@neolink:8655/api/events/{id}/clip      (full clip MP4)
```

The event `id` arrives in the MQTT **Last event** payload, so an HA automation
can attach the exact thumbnail of the event that fired it to a notification.
The JSON endpoints (`/api/events`, `/api/cameras`, …) are metadata, not
footage: those stay web-account only.
