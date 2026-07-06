# Neolink.NET

**RTSP bridge + web viewer for Reolink cameras that speak the proprietary Baichuan protocol.**

> Inspired by, and a pure C#/.NET reimplementation of, the original
> [Neolink](https://github.com/thirtythreeforty/neolink) project by
> [@thirtythreeforty](https://github.com/thirtythreeforty), whose reverse engineering of
> the Baichuan protocol made all of this possible, and its actively maintained fork
> [QuantumEntangledAndy/neolink](https://github.com/QuantumEntangledAndy/neolink).

Neolink.NET is for Reolink IP cameras that talk the proprietary "Baichuan" protocol on
TCP port 9000 instead of standard RTSP/ONVIF (B800/D800, B400/D400, E1, Lumus, 510A,
Duo, TrackMix, and many others).

Your NVR software (**Frigate**, Blue Iris, Home Assistant, Shinobi, VLC, ffmpeg, …) connects to
Neolink.NET, which logs into the camera, demuxes its media stream, and re-serves it as
standards-compliant RTSP. On top of that, Neolink.NET ships a **built-in browser UI** —
a multi-camera wall with live low-latency video, no plugins, no transcoding, no GStreamer.

The cameras are unmodified and no Reolink NVR is required.

```
┌──────────┐  Baichuan (9000)  ┌─────────────────┐  RTSP (8654)   ┌──────────────────┐
│ Reolink  │ ────────────────► │                 │ ─────────────► │ Frigate / VLC /  │
│ cameras  │                   │   Neolink.NET   │                │ Blue Iris / HA   │
└──────────┘                   │  (one process)  │  HTTP/WS (8655)┌──────────────────┐
                               │                 │ ─────────────► │ Browser web UI   │
                               └─────────────────┘                └──────────────────┘
```

## Features

**RTSP bridge**
- H.264 / H.265 video and AAC audio are **repackaged, not re-encoded**; ADPCM audio is
  decoded to PCM (L16)
- TCP-interleaved and UDP RTP transports, RTSP Basic auth, per-camera user permissions
- One camera connection feeds any number of RTSP clients (cameras fall over at ~2–3
  direct connections — the bridge multiplexes)
- Slow/stalled clients are isolated: they drop to the next keyframe or get disconnected;
  they can never affect the camera connection or other clients

**Web UI (optional, built in)**
- Live video in the browser via **fMP4 over WebSocket + Media Source Extensions** —
  ~1 s latency, no plugins, no ffmpeg
- Camera wall with five layout modes: **Grid** (1–16 tiles), **Focus** (hero + thumbnail
  strip, click to promote), **Mosaic** (classic CCTV wall), **Theater** (one camera,
  center stage), **Free** (draggable, resizable floating windows)
- Per-tile stream selection (main/sub), maximize/restore, browser fullscreen
- **Camera settings & controls panel** (⚙ next to each camera): capabilities are
  discovered from the camera itself — device info (model, firmware, serial), encode
  profiles (resolution, framerate/bitrate options), battery status, and — where the
  camera supports them — PTZ (press-and-hold pad), status LED / floodlight toggles,
  PIR motion sensor on/off, and reboot
- Everything persists in browser localStorage: server address, layout, tile
  assignments, window geometry
- Adaptive jitter buffer that measures each stream's delivery cadence

**Protocol / robustness**
- Full login handshake including modern encryption: BCEncrypt (XOR), AES-128-CFB, and
  **FullAes** (2023+ firmwares where the media stream itself is encrypted)
- Automatic reconnection with exponential backoff; transient auth failures retried
- Media-stream resynchronization: a corrupt packet skips forward instead of tearing
  down the connection
- A crash in one camera's pipeline can never take down other cameras or the process
- Zero native dependencies, zero NuGet packages — builds fully offline

## Quick start (Docker — recommended)

Prebuilt multi-arch images (`linux/amd64` + `linux/arm64`) are published to GitHub
Container Registry on every push to `main` and every `v*` release tag.

### 1. Pull the image

```bash
docker pull ghcr.io/borexola/neolink.net:latest
```

Available tags:

| Tag | Meaning |
|---|---|
| `latest` | most recent build of `main` |
| `0.6.0`, `0.6` | a specific release (created from `v0.6.0` git tags) — pin these in production |
| `main` | same as `latest`, explicit branch tag |

Docker selects the right architecture (x86-64 server, Raspberry Pi 4/5, ARM NAS)
automatically. Verify the pull:

```bash
docker image inspect ghcr.io/borexola/neolink.net:latest --format '{{.Os}}/{{.Architecture}} {{.Created}}'
```

> **`denied` or `unauthorized` when pulling?** The package is public, so no login is
> needed. If you see this on a fresh setup you are likely logged into ghcr.io with an
> expired token — run `docker logout ghcr.io` and pull again.
> **`manifest unknown`?** The tag doesn't exist (typo, or a release tag that hasn't
> been built yet) — check the available tags on the
> [package page](https://github.com/borexola/neolink.net/pkgs/container/neolink.net).

### 2. Create a config

```bash
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
```

Edit it: camera names, IP addresses, and credentials (same login as the Reolink app).

### 3. Run

```bash
# /config is a directory mount: config.json lives in it, and runtime settings
# from the web UI (settings.json) are persisted next to it.
docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Then check it came up:

```bash
docker logs -f neolink     # prints the ready-to-use RTSP and web UI URLs
```

- **Web UI**: http://localhost:8655
- **RTSP**: `rtsp://localhost:8654/<camera-name>`

### Or with compose

Save this as `docker-compose.yml` next to your `config/` directory
(or `curl -O https://raw.githubusercontent.com/borexola/neolink.net/main/docker-compose.yml`):

```yaml
services:
  neolink:
    image: ghcr.io/borexola/neolink.net:latest
    container_name: neolink
    restart: unless-stopped
    ports:
      - "8654:8654"   # RTSP (TCP-interleaved works for ffmpeg/Frigate/VLC)
      - "8655:8655"   # web UI + API; remove if webui:false and API unused
    volumes:
      - ./config:/config   # holds config.json + web-UI settings.json
    # For RTSP over UDP transport, use host networking instead of port maps:
    # network_mode: host
```

Then:

```bash
docker compose up -d
docker compose logs -f    # shows the rtsp:// and web UI URLs
```

### Upgrading

```bash
docker pull ghcr.io/borexola/neolink.net:latest
docker rm -f neolink
docker run -d --name neolink ...   # same run command as above
# or, with compose:
docker compose pull && docker compose up -d
```

### Building the image from source

```bash
git clone https://github.com/borexola/neolink.net.git && cd neolink.net
docker build -t neolink.net .
docker run -d --name neolink -p 8654:8654 -p 8655:8655 \
    -v "$PWD/config:/config" neolink.net
```

Then:
- **Web UI**: http://localhost:8655
- **RTSP**: `rtsp://localhost:8654/<camera-name>`

> RTSP over **UDP** transport needs `network_mode: host` instead of port mapping.
> TCP-interleaved transport (the default for ffmpeg/Frigate, and `--rtsp-tcp` in VLC)
> works fine with plain port mapping.

## Quick start (from source)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/borexola/neolink.net.git
cd neolink.net
cp src/Neolink.Server/config.example.json src/Neolink.Server/config.json  # edit it
dotnet run --project src/Neolink.Server -c Release
```

Single-file, self-contained binaries:

```bash
dotnet publish src/Neolink.Server -c Release -r linux-x64    # or win-x64, linux-arm64, ...
```

## Stream URLs

| URL | Content |
|---|---|
| `rtsp://host:8654/driveway` | main stream (alias) |
| `rtsp://host:8654/driveway/mainStream` | main stream (high resolution) |
| `rtsp://host:8654/driveway/subStream` | sub stream (low resolution) |
| `http://host:8655/` | web UI |
| `http://host:8655/api/cameras` | JSON list of cameras and stream state |
| `ws://host:8655/api/stream?path=/driveway/subStream` | live fMP4 (MSE-compatible) |
| `GET /api/cameras/driveway/capabilities` | device info + discovered features (ptz/led/pir/battery) |
| `GET /api/cameras/driveway/streaminfo` | encode profiles: resolution, framerate/bitrate options |
| `GET /api/cameras/driveway/battery` | battery charge/status (battery cameras) |
| `GET`/`POST /api/cameras/driveway/led` | status LED & floodlight — `{"state":"open"}`, `{"lightState":"close"}` |
| `GET`/`POST /api/cameras/driveway/pir` | PIR motion sensor — `{"enabled":true}` |
| `POST /api/cameras/driveway/ptz` | pan/tilt — `{"command":"left","speed":32}` (`up/down/left/right/stop`) |
| `POST /api/cameras/driveway/reboot` | reboot the camera |

`POST` (control) endpoints require HTTP **Basic auth** when `users` are configured,
honouring the same per-camera `permitted_users` rules as RTSP; with no users
configured they are open, like everything else. Feature discovery is live: the
server probes the camera once per connection and the web UI only shows the
controls the camera actually supports.

## Configuration

JSON with comments and trailing commas allowed — see
[config.example.json](src/Neolink.Server/config.example.json). Legacy TOML configs from
the original Rust neolink are also accepted.

### Top level

| Option | Default | Description |
|---|---|---|
| `bind` | `0.0.0.0` | Address to serve on |
| `bind_port` | `8654` | RTSP port |
| `web_port` | `8655` | Web UI + HTTP/WS API port; `0` disables both |
| `webui` | `true` | Serve the browser UI on `web_port`; `false` = API only |
| `web_bind` | = `bind` | Separate bind address for the web port |
| `users` | *(none)* | RTSP Basic-auth users: `{ "name", "pass" }`. Omit for open access |
| `recording` | *(none)* | Event recording (see below). Omit to disable |

### Recording (`"recording": { ... }`)

> 💾 **Slow disks are handled**: all recording I/O runs on dedicated
> low-priority writer threads behind a bounded memory budget, so an HDD that
> stalls (cache flushes, spin-ups, network shares) can never lag the service or
> the live streams — if the disk falls behind, *recorded* frames are dropped
> (with a log warning) and recording resumes at the next keyframe.

Two recording modes, both switchable **per camera at runtime** from the web UI
(camera ⚙ → RECORDING) — the switches persist in `settings.json` next to your
config file (in Docker: the `/config` mount), so they survive restarts:

- **Detection events**: the camera's own motion/AI detections (person, vehicle,
  animal — pushed over the Baichuan connection, no polling and no server-side ML)
  become labeled events with video clips and thumbnails. New events appear in a
  review strip at the top of the web UI; click to play, ✕ to dismiss. The 🕘
  Events button opens the full history grouped by day. Per camera you can also
  pick **which detection types to record** (🧍 person, 🚗 vehicle, 🐾 animal,
  📦 package, 👁 motion) — detections of disabled types are discarded entirely.
  ⚠ The camera does the detecting: person/vehicle/animal labels only arrive when
  the matching Smart Detection is enabled **in the Reolink app** (camera →
  Settings → Detection). The chips are a Neolink-side filter on what arrives;
  the camera's own settings are never changed.
- **Continuous (24/7)**: classic NVR-style recording into rolling
  `segment_minutes`-long MP4 files, browsable under 🕘 → Recordings (grouped by
  day, click to play). Off by default; enable per camera in the UI.

| Option | Default | Description |
|---|---|---|
| `path` | *required* | Storage directory. In Docker, mount a volume here (e.g. `./recordings:/recordings`) |
| `retention_days` | `7` | Events older than this are deleted (`0` = keep forever) |
| `pre_seconds` | `5` | Video included from before the detection (pre-roll) |
| `post_seconds` | `8` | Quiet time after the last detection before the event closes |
| `max_clip_seconds` | `120` | Hard cap per event; continued activity starts a new event |
| `stream` | `auto` | Stream to record: `auto` (main if served), `mainStream`, `subStream` |
| `segment_minutes` | `10` | Continuous recording: length of one segment file |
| `continuous_retention_days` | = `retention_days` | Days to keep continuous footage (`0` = forever) |

Everything is fragmented MP4 (H.264/H.265 passthrough, video-only) playable in
the browser and by ffmpeg/VLC. Storage layout is plain files —
`recordings/<camera>/<date>/<time>-<id>/{event.json, clip.mp4, thumb.jpg}` for
events, `recordings/<camera>/continuous/<date>/<HH-mm-ss>.mp4` for 24/7 footage —
so backups and external tooling are trivial. Set `"record": false` on a camera
to start with events off (the UI switch can re-enable it).

### Per camera

| Option | Default | Description |
|---|---|---|
| `name` | *required* | Name used in the RTSP URL and web UI |
| `address` | *required* | Camera IP/hostname; port defaults to `9000` |
| `http_address` | *(none)* | The camera's HTTP(S) web interface (`host`, `host:port` or full URL). Enables changing stream profiles (resolution/fps/bitrate) from the web UI via the documented Reolink HTTP API |
| `username` / `password` | *required* | The camera's own login (same as the Reolink app) |
| `stream` | `both` | `mainStream`, `subStream`, `externStream`, `both`, or `all` |
| `channel_id` | `0` | Channel when connecting through a Reolink NVR (0-based) |
| `permitted_users` | all users | Restrict this camera's mounts to specific `users` |
| `record` | `true` | Initial default for this camera's "Detection events" switch (changeable in the web UI) |

## Using with Frigate

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

## Web UI notes

- **H.265 in the browser**: sub streams are H.264 and play everywhere. Main streams on
  many Reolink models are H.265, which browsers only decode with hardware support
  (Safari, Edge, Chrome with HW decode; not Firefox). The UI detects this and suggests
  the sub stream. This is a browser limitation — the RTSP side serves H.265 fine.
- **Latency** adapts to the camera: ~1 s for cameras that deliver per-frame, more for
  cameras that batch whole GOPs (the buffer must cover the delivery gap).
- Audio is not yet carried to the browser (RTSP clients do get AAC/PCM audio).

## Self-tests & development

```bash
dotnet run --project src/Neolink.Server -- selftest
# with protocol samples from the original Rust repository:
dotnet run --project src/Neolink.Server -- selftest --config /path/to/rust/neolink-repo
```

`tools/fake_camera.py` implements enough of the camera side of the protocol to test the
full pipeline without hardware:

```bash
python3 tools/fake_camera.py /path/to/rust-repo/crates/core/src/bcmedia/samples 9000 &
# point a config at address = "127.0.0.1:9000", run neolink, then:
ffprobe -rtsp_transport tcp rtsp://127.0.0.1:8654/testcam
```

### Project layout

```
src/Neolink.Server/          the service (RTSP + web API + optional web UI host)
  Bc/                        Baichuan wire protocol: header codec, BCEncrypt/AES/FullAes, XML
  Protocol/                  camera connection (message routing), login/stream/ping ops
  Media/                     BcMedia demuxer (I/P-frames, AAC, ADPCM), Annex-B utils, fMP4 muxer
  Streaming/                 per-camera reconnect service and the fan-out StreamHub
  Rtsp/                      RTSP server, sessions, RTP packetization, SDP
  Web/                       HTTP/WebSocket API + Blazor host (camera list, live fMP4)
  Config/                    JSON/TOML config (dependency-free mini parser)
src/Neolink.WebClient/       the web UI (Blazor razor class library, hosted in-process)
tools/fake_camera.py         protocol-level camera simulator for tests
```

The protocol implementation is a faithful port of the Rust `neolink_core` crate,
including its odd corners: 31-character MD5 credential mangling, XOR "encryption" keyed
by channel, nonce-derived AES session keys, binary-mode switching via
`<binaryData>1</binaryData>` extensions, `encryptLen`-padded FullAes media payloads, and
8-byte-padded media packets.

## Troubleshooting

- Run with `--verbose` (or `NEOLINK_LOG=debug`) for protocol-level logging.
- **`503 Service Unavailable` on DESCRIBE / web tiles stuck on "connecting…"**: the
  camera is not connected yet (wrong address/credentials, camera booting) — check the
  service logs.
- **"authentication failed … retrying in 30s"**: the camera rejected the configured
  username/password. Cameras also reject transiently while rebooting or when their user
  table is full, so the bridge keeps retrying at a slow pace. Five wrong attempts can
  lock the account for a few minutes.
- **"Connection closed while waiting for message ID 1"**: usually an encryption
  negotiation problem — make sure you're on the latest build (FullAes support).
- Cameras limit concurrent Baichuan clients; if the Reolink app is streaming
  `mainStream`, use `stream: "subStream"` or close the app.
- **Choppy browser video on Firefox for main streams**: that's H.265 — use the sub
  stream or a Chromium/Safari browser with hardware HEVC.

## Compared to the original Rust neolink

Improvements: built-in web UI, no GStreamer/native dependencies, no transcoding of AAC,
per-client backpressure, in-stream resynchronization.

Not (yet) supported: TLS for RTSP (`rtsps://` — put a TLS-terminating proxy in front),
battery/UID cameras that need UDP discovery (Argus etc.), and the auxiliary subcommands
(PIR, reboot, status LED, two-way talk).

## Credits & license

This project would not exist without the original Neolink — it began as a port of it and
remains deeply indebted to both upstream projects:

- [thirtythreeforty/neolink](https://github.com/thirtythreeforty/neolink) — the original
  project and the reverse engineering of the Baichuan protocol; the inspiration for
  Neolink.NET's name, configuration format, and architecture
- [QuantumEntangledAndy/neolink](https://github.com/QuantumEntangledAndy/neolink) — the
  actively maintained fork, reference for AES/FullAes and modern camera behavior

This project is a derivative port and is licensed under the
[GNU Affero General Public License v3.0](LICENSE), the same license as the original.
Neolink.NET is not affiliated with or endorsed by Reolink.
