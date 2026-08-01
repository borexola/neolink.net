# Web UI

The built-in browser UI — no plugins, no transcoding, no GStreamer. Serve it
on `web_port` (default 8655) or turn it off with `"webui": false`.

## Live viewing

- Video arrives as **fMP4 over WebSocket + Media Source Extensions** — ~1 s
  latency for cameras that deliver per-frame, a little more for cameras that
  batch whole GOPs (an adaptive jitter buffer measures each stream's cadence).
- **Audio** for AAC cameras: streams start muted (browser autoplay rules), the
  speaker button unmutes; event clips and 24/7 recordings carry audio too.
  ADPCM-only cameras play audio via RTSP only.
- **H.265 main streams** need hardware decode (Safari, Edge, Chrome with HW
  support; not Firefox) — the UI detects this and suggests the H.264 sub
  stream. The RTSP side serves H.265 regardless.
- **Two-way talk (opt-in)** for cameras with a speaker: a mic button on the
  maximized tile / quick view streams your microphone to the camera
  (resampled and ADPCM-encoded server-side). Enable in *Server settings →
  Web UI*; needs HTTPS or localhost — browsers only expose the mic in secure
  contexts. Talk uses the system-default microphone unless you pick one
  under *Server settings → Connection* (per device — do this when the
  default is a headset amp or virtual cable, which capture only silence; the
  UI warns when a live talk session is carrying no signal). This matters
  especially in the desktop app, whose permission prompt has no device
  picker of its own.

## Layouts

Five wall modes — **Grid** (1–16 tiles), **Focus** (hero + thumbnail strip),
**Mosaic** (classic CCTV wall), **Theater** (one camera), **Free** (draggable
floating windows) — with per-tile stream selection (main/sub), maximize and
browser fullscreen. Signed-in accounts keep their layouts, tiles and filters
server-side, so they follow you across browsers; signed-out browsers fall
back to localStorage.

## Camera settings & controls

The ⚙ panel next to each camera is built from what the camera itself
reports: device info, full stream encode tables, battery status, and — where
supported — PTZ, optical zoom/focus, status LED / floodlight with
brightness, PIR, a latched siren, privacy mode and reboot. Over the camera's
HTTP API (beta) it adds picture sliders, day/night and anti-flicker, HDR,
speaker volume, motion and per-type AI detection sensitivity, the on-screen
display, PTZ presets, doorbell quick replies and a firmware-update badge.
Device settings **stage** and are sent only on "Apply to camera", with an
up-front warning when a change restarts the stream or reboots the camera.
The **PORTS tab** shows the camera's own service switches (HTTP, HTTPS,
RTSP, ONVIF…) read live, and can enable HTTP/ONVIF right from Neolink
(admin only, behind a confirmation).

## Events, timeline, export

- New events land in a **review strip** at the top; the Events page keeps the
  full history grouped by day and is **deep-linkable**
  (`/events/{camera}`, `/events?event={id}`).
- The **Timeline** scrubs all cameras in sync, with coverage bars, event
  marks and a footage calendar. The **Studio button** flips it into a
  video-editor arrangement (monitors on top, tracks docked below); the
  divider between monitors and tracks drags to taste, and every tile's
  camera button (or `S`) saves the frame under the cursor as a PNG.
- **Events only** (beta), on the timeline's toolbar, plays the day as just
  its incidents: from wherever the cursor stands, playback hops to the next
  event, plays through it, hops again, and pauses after the day's last
  event (Play at that point starts the reel over). It obeys the EVENTS
  filter — hide a category and it is skipped too. Off by default; the
  classic continuous play-through is untouched.
- **Export** downloads a chosen period of one camera's day — one combined
  MP4 (joined without re-encoding, trimmed to the range) or a zip of the
  original segments. The dialog pre-fills the zoomed window and shows the
  size first; mind that a full high-bitrate day is tens of gigabytes.

## Camera SD-card playback (preview)

The Events page's **SD card** mode lists and plays the recordings a camera
stored on its own card — footage from when the server was down, and
battery-camera clips that never streamed. Day calendar from the camera,
playback with scrubbing, download. Needs the camera's HTTP API and a mounted
card; what the camera records onto its card is configured in the Reolink
app. *Preview* because it hangs on per-model firmware: the Video Doorbell
WiFi lists recordings its firmware cannot serve (those clips only play in
the Reolink app, and the player says so).

## Install as an app (PWA)

Chrome/Edge show an install icon in the address bar; iPhone/iPad use
Safari's *Share → Add to Home Screen*; macOS Safari has *File → Add to
Dock*. The installed app is the same live UI in its own window — nothing is
cached, so it is always as current as the server. Requires a secure context
(HTTPS or localhost); on plain `http://lan-ip` the install option is hidden
though the UI works as always.

## Browser alerts (per user)

The settings *Alerts* tab picks which detections pop a system notification,
camera by camera, plus per-camera offline alerts and system alerts (storage
full, overload, write failures). Clicking a detection alert opens the exact
clip. Fires while the app is open (tab or PWA, foreground or minimized),
with per-camera cooldowns; preferences follow your account. Needs HTTPS or
localhost, like two-way talk.

## Language

The UI ships in English, French, German, Spanish, Dutch, Polish and
Portuguese. Every language beyond English is AI-translated and cannot be fully
verified for accuracy — corrections are very welcome (the catalogues are plain
JSON). The first-run "Secure this server" dialog
asks for the language alongside the admin account — that choice becomes both
the admin's own and the server default. Afterwards it lives under
⚙ → *Language*, a tab every account gets: your pick applies to the whole UI
**instantly** (no restart, no reload) and is saved to your account, so it
follows you to other browsers. The admin separately sets the *server default*
— what the sign-in screen uses and what accounts that never chose follow. A
cookie carries the choice into the first frame of the next page load, so
there is no English flash. For provisioned deploys, `ui.language` in
`config.json` seeds the default on a server whose state has none yet.

A string a catalogue does not cover falls back to its English original, so a
partial translation is still a working UI. Adding a language is one embedded
JSON file (`src/Neolink.WebClient/Localization/{code}.json`, keyed by the
English source text) plus one entry in `Localization/Lang.cs`.
