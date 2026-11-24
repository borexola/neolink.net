# Configuration

JSON with comments and trailing commas allowed; see
[config.example.json](../src/Neolink.Server/config.example.json). Legacy TOML configs from
the original Rust neolink are also accepted.

Cameras that are not Reolink take stream URLs instead of an address and login: see
[Non-Reolink cameras](generic-cameras.md). Battery models have their own guide:
[Battery cameras](battery-cameras.md).

## Top level

| Option | Default | Description |
|---|---|---|
| `bind` | `0.0.0.0` | Address to serve on |
| `bind_port` | `8654` | RTSP port |
| `web_port` | `8655` | Web UI + HTTP/WS API port; `0` disables both |
| `webui` | `true` | Serve the browser UI on `web_port`; `false` = API only |
| `web_bind` | = `bind` | Separate bind address for the web port |
| `ptz_port` | `8656` | Shared ONVIF PTZ port for Frigate ([PTZ buttons in Frigate](frigate.md#ptz-buttons-in-frigate)); `0` = off |
| `ptz_bind` | = `bind` | Bind address for the PTZ ports |
| `users` | *(none)* | **RTSP** Basic-auth users: `{ "name", "pass" }`. Omit for open access. Separate from web-UI accounts! |
| `recording` | *(none)* | Event recording (see below). Omit to disable |
| `mqtt` | *(none)* | MQTT / Home Assistant integration ([guide](home-assistant.md)). Omit to disable |
| `ui` | *(defaults)* | Web-UI specific settings (see below) |

## Web-UI settings (`"ui": { ... }`)

| Option | Default | Description |
|---|---|---|
| `enabled` / `port` / `bind` | = `webui` / `web_port` / `web_bind` | Grouped aliases of the top-level web options |
| `state_dir` | config dir | Where the UI's server-side state persists: `users.json` (sign-in accounts) and `settings.json` (per-user layouts/filters/recording switches) |
| `reset_admin_password` | `false` | Recovery: while `true`, the login screen allows setting a new admin password from the LAN; the flag turns itself off after one reset |
| `remote_setup` | `false` | The first-run admin setup and the admin-password reset only accept LAN addresses; `true` lets a public address do them (set up over a VPN that looks public) |
| `trickle_speed` | `4` | Playback speed of the review strip's ambient clip previews |
| `language` | *(none)* | Seeds the default UI language (`en`, `fr`, `de`, `es`, `nl`, `pl`, `pt`) on a server whose state has none yet. Only a seed: the first-run dialog and ⚙ → Language own the setting afterwards (stored in `users.json`, applied live) |

> **Persistence across deployments**: three locations must live on volumes or
> your state resets every deploy: **(1)** the config directory (or `ui.state_dir`)
> holding `users.json` + `settings.json`: lose it and accounts, layouts and
> filters reset; **(2)** the `recording.path` directory: lose it and footage
> *and the reviewed/dismissed state* (stored in each event's `event.json`) reset,
> so previously dismissed events reappear; **(3)** `config.json` itself. The
> docker-compose example mounts (1)+(3) via `./config:/config`; uncomment its
> `./recordings:/recordings` line for (2) when you turn on recording.

## Web UI sign-in

Authentication is **off by default**: no database, no config required. The
first visitor is prompted to create the **admin** account (or dismiss and do it
later via ⚙ → "Enable login…"); creating it turns sign-in on for the whole UI
and API. Accounts live in `users.json` next to your config: passwords are
stored as PBKDF2-SHA256 (210k iterations, per-user salt, constant-time
verification, safe for an open-source, file-based setup), and sessions are
HMAC-signed tokens that expire after 30 days and are invalidated the moment a
password changes.

The admin manages accounts from ⚙ → Users…: add normal users, change any
password, delete users (the admin itself can't be deleted). **cameras…** limits
a normal user to chosen cameras; the rest vanish from their wall, events,
timeline and search, and the server refuses them. **Every account
keeps its own UI settings** (layout, tiles, review-strip filters), stored
server-side, so people don't fight over one shared view. Forgot the admin
password? Set `"reset_admin_password": true` in the config, restart, and use
"Reset admin password…" on the login screen from the LAN; the flag turns
itself off again after one reset.

The admin also gets ⚙ → **Server settings…**: a form that edits most of
`config.json` (network ports, web UI, recording) and writes it back to the file
(atomically, keeping a `.bak`; comments are not preserved). The **Users** tab
also edits the RTSP `users` (passwords write-only). The **Cameras** tab adds, edits and deletes cameras
from the same panel, Reolink and generic RTSP alike, with live validation, a
**Test connection** button (a real Baichuan login for Reolink; an RTSP
round-trip for generic URLs), and write-only passwords: a stored password is
never sent to the browser, and leaving the field blank keeps it. Saved changes
apply on the next restart, which the admin can trigger with **Restart
service…**: the process exits and your container/systemd restart policy brings
it back within seconds while the UI reconnects on its own. Running the Home
Assistant add-on, that policy is the add-on's **Watchdog** toggle (on its Info
page): enable it, or the restart button stops the add-on and nothing starts it
again. When a newer release
exists on GitHub, a dismissable banner links to it.

## Recording (`"recording": { ... }`)

> ### 📖 Full guide: **[recording.md](recording.md)**: every option,
> tiered storage (SSD clips tier, cold archive), capacity forecasts, and
> footage encryption at rest.

One line turns it on:

```json
"recording": { "path": "/recordings" }
```

Two modes, each switchable per camera at runtime from the web UI (camera ⚙ →
RECORDING): **detection events** (the camera's own motion/AI detections
become labeled clips with thumbnails, reviewable from the events strip) and
**continuous (24/7)** NVR-style segments browsable by day. Defaults: 7-day
retention, 5 s of pre-roll, rolling 10-minute segments; everything is plain
fragmented MP4 in per-camera date folders, so backups and external tooling
are trivial. The full guide covers the rest: an SSD fast tier for clips, a
cold archive tier that moves aged footage instead of deleting it, 90%-full
warnings with fill-date forecasts, and AES-256-GCM footage encryption.

## Per camera

| Option | Default | Description |
|---|---|---|
| `name` | *required* | Name used in the RTSP URL and web UI |
| `address` | *required* | Camera IP/hostname; port defaults to `9000` |
| `http_address` | *derived from `address`* | The camera's HTTP(S) web interface (`host`, `host:port` or full URL). Only needed to override the host/port: the HTTP API is otherwise reached on the `address` host, port 80. Unlocks picture settings, volume, PTZ presets, OSD, detection sensitivity, SD-card browsing and stream-profile changes |
| `onvif_address` | *derived from `address`* | The camera's ONVIF device service (`host`, `host:port` or full URL). Only needed to override the port: ONVIF is otherwise probed on port 8000, then 80. A picture-settings fallback for models with no HTTP API (Lumus line) |
| `username` / `password` | *required* | The camera's own login (same as the Reolink app) |
| `stream` | `both` | `mainStream`, `subStream`, `externStream`, `both`, or `all` |
| `channel_id` | `0` | Which channel of the device this entry is (0-based): a Reolink NVR's camera, or a lens of a [dual-lens camera](#dual-lens-cameras-trackmix-duo) |
| `permitted_users` | all users | Restrict this camera's mounts to specific `users` |
| `ptz_share` | `false` | Offer this camera's PTZ to Frigate on the shared port ([PTZ buttons in Frigate](frigate.md#ptz-buttons-in-frigate)) |
| `ptz_port` | *(none)* | Give this camera's PTZ its own port instead (Frigate 0.17 and older) |
| `ptz_fov_h`, `ptz_pan_rate` | *(none)* | **Beta.** The lens's angle of view and the head's pan speed; together they turn on [click-to-move and autotracking in Frigate](frigate.md#click-to-move-and-autotracking-in-frigate-beta), which lists the optional `ptz_fov_v`, `ptz_tilt_rate`, `ptz_lag_ms`, `ptz_speed` and `ptz_tele_fov_h` |
| `record` | `true` | Initial default for this camera's "Detection events" switch (changeable in the web UI) |
| `max_encryption` | `fullaes` | **Diagnostic.** Caps the encryption the login advertises: `none`, `bcencrypt`, `aes`, `fullaes`. Only for firmware that will not answer the default; see [troubleshooting](troubleshooting.md) |
| `legacy_login` | `false` | **Diagnostic.** Opens the login with the older credential framing instead of the header-only one. Pairs with `max_encryption`; see [troubleshooting](troubleshooting.md) |

> **Keep camera passwords alphanumeric.** Reolink's HTTP API (the one behind
> `http_address`, picture settings, volume, PTZ presets and scaled snapshots)
> is far pickier about the password than the video protocol. Some special
> characters (`@ : / % & + #` and others, varying by firmware) make the HTTP
> login fail with `password wrong` **even though the exact same password works
> for live video and in the camera's own web page**. The result is a camera
> that streams and records perfectly but whose HTTP-backed features silently go
> missing (the log warns once that the HTTP API "REJECTED the login"). If you
> hit this, set the camera's password to letters and digits only (`a–z A–Z
> 0–9`) in the Reolink app. This is a camera-firmware quirk, not a Neolink.NET
> limitation, and the reference Reolink libraries recommend the same. Passwords
> over 31 characters can fail for the same reason.

## Finding cameras and following IP changes

- **Auto discover.** Settings → Cameras → *Auto discover…* scans a subnet or
  address range (the server's own by default, a camera VLAN if you type it)
  for plug-in Reolink cameras, with model and name where ONVIF is on; *Add…*
  fills in the add-camera form, so the password is all you type. Battery
  models never answer a scan: add those by UID (see the battery guide).
- **A camera that gets a new IP is followed.** At the first login Neolink.NET
  asks each camera for its UID and remembers it (`camera-state.json`). When
  the configured address stops answering, it asks the LAN which camera has
  that UID (a UID-keyed discovery hello, no credentials sent) and connects
  to wherever it answers from. `config.json` is not touched: the camera
  settings and the camera list show *now at &lt;address&gt;* with a
  **Use new address** button that writes it to the file. Cameras connected
  over UDP need none of this (they are found by UID on every connect). If a
  camera will not report its UID, set `uid` in its config entry.
- In Docker both need host networking (`--network host`): a bridged container
  gets no broadcasts or multicast.

## Changing a camera's password

Settings → Cameras → edit a Reolink camera → **Change the password on the
camera…** sets a new login password on the camera itself. Neolink.NET then
signs in again with it on every stream of that camera, a second-lens entry
included, and writes it to `config.json` only once the camera has accepted a
login with it. No restart is needed.

- The camera has to be connected: a suspended or offline camera, or a sleeping
  battery one, cannot take the change.
- The password is 6 to 31 characters, without spaces.
- If the camera does not take it, Neolink.NET goes back to the old password,
  says so, and leaves `config.json` alone.
- If the camera accepts neither password right away (it is restarting, say),
  Neolink.NET keeps trying both for 15 minutes and saves the new one as soon
  as the camera takes it. The log says how it ended.
- Anything else that signs in to the camera (the Reolink app, an NVR, Home
  Assistant) needs the new password too.
- In the Home Assistant add-on, a camera listed in the add-on's Options with a
  password there gets that password back at the next start: change it, or
  blank it, in Options too.

## Dual-lens cameras (TrackMix, Duo)

A camera with two lenses that each have their own picture (TrackMix: wide and
telephoto; the first Duo: left and right) serves one channel per lens. Each
lens is its own camera entry: same `address` and login, `channel_id` `0` for
the first lens and `1` for the second.

```json
{ "name": "driveway",      "address": "192.168.1.50", "username": "admin", "password": "…" },
{ "name": "driveway_tele", "address": "192.168.1.50", "username": "admin", "password": "…", "channel_id": 1 }
```

- Neolink.NET reads the lens count at login. A lens with no entry gets a line
  in the log and an **Add lens** button in Settings → Cameras, which fills in
  the form with the first entry's address and login.
- If no button appears, add the second entry by hand.
- Each entry has its own tile, RTSP paths and recordings. On a TrackMix only
  the first lens detects, so the second entry records on the first one's
  detections. Use the first entry for pan/tilt, lights and the siren.
- Every entry holds its own connections to the camera (one per stream). If the
  camera starts refusing logins, open fewer streams per entry (`stream`).
- Cameras that stitch both lenses into one panorama (Duo 2, Duo 3) have a
  single channel and need none of this.

## Enable HTTP and ONVIF on the camera for the full feature set

Neolink.NET streams and records over Baichuan (port 9000), which needs nothing
extra, but the camera's *optional* HTTP and ONVIF services unlock the rest of
the control surface, and both are worth turning on. **You can do it from
Neolink itself**: the camera's ⚙ panel has a **PORTS tab** that reads the
camera's live service table and can enable HTTP or ONVIF right there (admin
only, behind a confirmation; the Baichuan port itself is never touchable).
The same switches live in the Reolink app under *Settings → Network → Advanced
→ Port Settings*. The `http_address` / `onvif_address` config keys are only
needed for non-standard hosts/ports; otherwise both services are found on the
camera's own IP automatically.

- **HTTP (or HTTPS)** is where most settings live: picture sliders, day-night,
  HDR, speaker volume, Wi-Fi signal, PTZ presets, the on-screen display,
  detection sensitivity, SD-card browsing/playback, stream-profile changes,
  and right-sized snapshots. Off = the camera still streams and records
  perfectly; those panels are simply absent. If HTTP is on but features are
  missing, the log says exactly why (a rejected login usually means a special
  character in the password; see the note above).
- **ONVIF** matters most on models with no HTTP API (the Lumus line): its
  imaging service is the **fallback** for the picture sliders and day-night
  mode. Reolink serves it on port 8000; leave the default and it is found
  automatically. It covers imaging basics only, so it complements HTTP rather
  than replacing it.

Neither interface weakens LAN security beyond what the Reolink app already
uses (same camera login), and neither is required for core video or recording.

## Detection zones

Every camera has one, under camera ⚙ → **Camera settings** → **Detection zone**.
Drag boxes over the camera's own picture to mark what is worth watching.

Where the zone is KEPT depends on the camera, and the editor says which:

- **On the camera**, for a Reolink with its HTTP API. It is the same grid the
  Reolink app edits, at whatever dimensions that model reports, and it governs
  the camera's own motion and AI alarms, so it changes its app notifications
  and this server's event recording alike.
- **On the camera**, for a non-Reolink camera whose ONVIF analytics runs a
  **cell motion detector** (Hikvision, Dahua, Axis and many others do). The grid
  has the camera's own dimensions, and it decides where the camera's *motion*
  alarms fire; any other analytics rules it runs (line crossing, intrusion…)
  keep their own areas, and the rule's other settings (how many cells must
  move, the alarm delays) are left exactly as they were.
- **On Neolink**, for every camera that keeps no zone of its own: a generic
  RTSP camera with no such detector (or no reachable ONVIF), and Reolink models
  whose firmware carries no grid. Nothing is
  written to the camera and its own alerts are unchanged; the zone governs what
  *Neolink* watches, which today is the live object boxes. It is stored in
  `camera-state.json` alongside the other per-camera settings and survives
  restarts.

The editor draws the grid over the camera's latest still. A generic RTSP
camera with no ONVIF snapshot, or whose ONVIF snapshot fails, gets that still
from the stream Neolink is already carrying, decoded with ffmpeg; without
ffmpeg the grid is drawn over an empty frame instead. A Reolink camera is
untouched by this: when its snapshot fails, the answer is the last frame,
honestly labelled, or nothing.
