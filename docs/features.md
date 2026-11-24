# Features

**RTSP bridge**
- H.264 / H.265 and AAC are **repackaged, never re-encoded**; ADPCM audio is
  decoded to PCM (L16)
- TCP-interleaved and UDP transports, RTSP Basic auth, per-camera permissions
- One camera connection feeds any number of clients (cameras fall over at ~2–3
  direct connections); slow clients are isolated and can never affect the
  camera or other viewers

**NVR: recording and review (optional)**: full guide in [recording.md](recording.md)
- **Detection-event recording**: the camera's own motion and AI detections become labeled
  clips with thumbnails and pre-roll, and you choose per camera which detection types to keep
- **Continuous 24/7 recording** in rolling segments, browsable by day; both modes switch
  per camera at runtime, with no restart
- **Review**: a strip of new events, a deep-linkable Events page with the full history, and
  a **Timeline** that scrubs all cameras in sync with coverage bars, event marks and a
  footage calendar
- **Export**: a chosen period as one MP4 joined without re-encoding, or a zip of the
  original segments
- **Retention and storage tiers**: separate retention for events and continuous footage, an
  SSD tier for clips, a cold archive tier that moves aged footage instead of deleting it,
  and capacity warnings with fill-date forecasts
- **Footage encryption at rest** (AES-256-GCM)
- **Open storage**: plain fragmented MP4 in per-camera date folders, so backups and other
  tools can read it
- **Slow disks cannot stall live video**: recording runs on its own writer threads and
  drops recorded frames, never live ones, when a disk falls behind
- **Accounts**: each user sees only the cameras they are allowed, in live view, events,
  timeline and search
- No Reolink NVR and no other recording software is needed

**Web UI (optional, built in)**: full tour in [web-ui.md](web-ui.md)
- Live low-latency video (well under a second on a LAN, fMP4 over WebSocket +
  MSE, with no plugins and no transcoding), live audio, opt-in **two-way talk**
- Camera wall with five layouts (Grid, Focus, Mosaic, Theater, Free),
  per-tile stream choice, maximize and fullscreen
- **Camera settings & controls** discovered from the camera itself: PTZ,
  zoom/focus, lights, siren, privacy mode, reboot, detection sensitivity and
  infrared brightness, plus (over the HTTP API, beta) picture settings, HDR,
  volume, OSD, PTZ presets, quick replies and a firmware-update badge. Changes stage and are
  sent only on "Apply to camera"; a **PORTS tab** can enable the camera's own
  HTTP/ONVIF services right from Neolink. [Non-Reolink
  cameras](generic-cameras.md#non-reolink-cameras-generic-rtsp) get the same panel over **ONVIF**:
  identity, stream profiles, picture, pan/tilt with presets, overlay placement
  and reboot
- A **detection zone** on every camera: kept on the camera where it can hold
  one, kept on Neolink where it cannot, and the editor says which
- **Events** review strip and deep-linkable events page, a synced
  multi-camera **Timeline** with footage export, and **camera SD-card
  playback** (preview)
- **Perimeter protection**: line-crossing / intrusion / loitering / object left or taken alerts from
  the Reolink app become their own event types, opt-in per camera under
  *Event types* (an untouched setup records what it always did); they get
  their own icons in the strip
- **AI event descriptions (BETA)**: a vision LLM writes what happened plus a
  GREEN/YELLOW/RED threat level; see
  [AI event descriptions](ai-descriptions.md)
- **AI Search (BETA)**: search events in plain language ("people wearing
  something red last week"). Structured filters parse instantly, the LLM
  matches descriptions; see [AI Search](ai-descriptions.md#ai-search-beta)
- **Live object boxes (PREVIEW)**: while you watch one camera full-size, the
  people, vehicles and animals in frame are outlined as you look at them.
  The outlining runs in your browser, on frames it has already decoded, so the
  server adds nothing and no frame leaves the device. Switch it on under
  Server settings → Experimental
- **Battery cameras** (BETA) auto-detected and sleep-friendly; see
  [Battery cameras](battery-cameras.md)
- **Tiered storage** (SSD clips tier + cold archive, capacity watching and
  fill forecasts; see [Tiered storage](recording.md#tiered-storage-optional)),
  **footage encryption at rest**, **email alerts** for critical
  conditions ([Email notifications](email-notifications.md#email-notifications)) and per-user
  **browser alerts**
- **Multiple languages**: English, French, German, Spanish, Dutch, Polish and
  Portuguese. Picked at first-run setup or under ⚙ → Language, applied live
  (no restart, no reload); each account keeps its own choice and the admin sets
  the server default for the sign-in screen. Non-English languages are
  AI-translated and cannot be fully verified, so corrections are welcome

**Home Assistant / MQTT (optional)**: full guide in
[home-assistant.md](home-assistant.md)
- A **device per camera appears automatically** (MQTT Discovery, no YAML):
  detection sensors driven by the camera's own pushes, controls, battery and
  sleep state, recording switches, record-on-demand, doorbell press events,
  and a Last-event sensor for notification deep links
- Two-level availability with retained state; a dozing battery camera stays
  *available* with an **Asleep** sensor saying why. MQTT 3.1.1 is spoken
  natively, with no external library

**Protocol / robustness**
- Full login handshake including modern encryption: BCEncrypt (XOR),
  AES-128-CFB, and **FullAes** (2023+ firmwares with encrypted media streams)
- Automatic reconnection with backoff; media-stream resynchronization (a
  corrupt packet skips forward instead of tearing the connection down)
- A crash in one camera's pipeline can never take down other cameras or the
  process
- Zero native dependencies, zero NuGet packages, so it builds fully offline

## Lightweight by design: the camera does the heavy lifting

Neolink.NET runs no object detection of its own: it never decodes, transcodes, or
analyses a single video frame for motion or AI. All of that already happens *on
the camera*, whose dedicated silicon detects motion and classifies people,
vehicles and animals in real time. Neolink.NET simply **listens for the alarm
messages the camera pushes** over the Baichuan connection (the same events that
drive Reolink's own app) and relays them to Home Assistant as MQTT sensors, and
doorbell button presses as MQTT events. That means:

- **No GPU, no Coral, no CPU-hungry inference.** Unlike setups where a server
  re-analyses every stream, Neolink.NET adds essentially zero processing load. It
  runs comfortably on a Raspberry Pi or a small NAS container.
- **Event-driven, not polled.** Sensors fire the instant the camera sees
  something, with no scan interval and no per-frame work.
- **AI is only as good as the camera.** Person/vehicle/animal labels come from
  the camera's firmware, so enable the detection types you want in the Reolink
  app and Neolink.NET surfaces exactly those.

The trade-off is that detection quality and available classes are whatever your
camera model provides (rather than a tunable server-side model like Frigate's);
in exchange you get an integration light enough to leave running forever.

One preview feature adds boxes to that picture without changing any of it: **live
object boxes** (Server settings → Experimental) outlines people, vehicles and
animals while you watch a single camera. The camera tells you *what* it saw but
never *where*, so the outlining is done by your BROWSER, on the frames it is
already decoding to show you. The server still never looks at a video frame, and
nothing is sent anywhere. It is a second opinion for the person watching:
recording, notifications and Home Assistant continue to follow the camera.

## Compared to the original Rust neolink

Improvements: built-in web UI, no GStreamer/native dependencies, no transcoding of AAC,
per-client backpressure, in-stream resynchronization.

Not (yet) supported: TLS for RTSP (`rtsps://`; put a TLS-terminating proxy in front)
and the UID/UDP discovery + relay transport (battery cameras work over direct TCP,
see [Battery cameras](battery-cameras.md), but cameras reachable ONLY via
Reolink's P2P relay are not). The auxiliary features (PIR, reboot, status LED,
two-way talk) are covered by the web UI and API instead of CLI subcommands.
