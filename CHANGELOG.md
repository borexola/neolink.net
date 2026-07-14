# Changelog

Release notes for Neolink.NET. Releasing works by tagging `vX.Y.Z` — the docker
workflow bakes the tag into the app as its version (see "Versioning & releases"
in the README). Paste the matching section below into the GitHub release.

## 0.8.9

### New

- **RTSP audio backchannel (two-way talk for go2rtc / Home Assistant)**: cameras
  with a speaker now expose an ONVIF Profile-T audio backchannel on their existing
  RTSP mount, so go2rtc, Home Assistant's WebRTC Camera and other ONVIF-aware
  clients can talk through the camera without the Neolink web UI. A client that
  sends `Require: www.onvif.org/ver20/backchannel` on DESCRIBE is offered an extra
  sendonly µ-law (PCMU/8000) track; the G.711 audio it streams during PLAY is
  depacketized, decoded and fed to the same talk pipeline the push-to-talk button
  uses (resampled and ADPCM-encoded to the camera). It is gated on two-way talk
  being enabled (`ui.talk = true`) and on the camera actually having a speaker, and
  plain players (VLC, ffmpeg, browsers) are untouched — the extra track only
  appears when a client explicitly asks for it.
- **Spotlight brightness for white-LED cameras**: cameras with a white spotlight
  that don't answer the Baichuan floodlight command (Lumus, Elite) now expose it
  over the Reolink HTTP API. The LIGHTS panel gains a Spotlight on/off toggle and a
  brightness slider that appears only while it is on; on/off still works on cameras
  whose HTTP is closed (it rides the Baichuan light state). The HTTP endpoint is
  derived from the camera's IP when `http_address` isn't set, a capability probe
  plus the ledCtrl spotlight bit gate the control so status-LED-only models (E1
  Pro) and the doorbell are excluded, and a read-modify-write preserves the
  camera's lighting schedule and AI-detect config.
- **Home Assistant sidebar (ingress)**: the add-on now supports HA ingress, so the
  web UI shows up in the Home Assistant sidebar and is served through the
  Supervisor's authenticated proxy — no exposed port needed. The UI reads the
  `X-Ingress-Path` header to render under HA's per-session sub-path (dynamic
  `<base href>`, so assets, the Blazor connection and the API all resolve inside the
  proxy). Direct access on 8655 and the "Open Web UI" button still work. Set
  `panel_admin: false` in the add-on options to show the sidebar entry to non-admin
  users too.
- **Privacy mode leaves beta**: the privacy-mode control drops its BETA tag in the
  camera panel and the README now that it has proven itself.

### Fixed

- **Privacy mode no longer flaps the camera offline**: on cameras that go fully dark
  in privacy mode (E1 Pro and similar), the absence of video was mistaken for a
  stalled stream, so Neolink reconnected every ~15s — flapping the status between
  online and offline and, worse, marking the camera (and every entity, including the
  privacy switch itself) Unavailable in Home Assistant, which made turning privacy
  back off unreliable. When privacy mode is on, the missing video is now treated as
  expected: the connection is held open, the camera stays online, and control stays
  available. A genuine disconnect (a socket error, not just silence) still reconnects.
  The camera-settings panel also now reflects privacy mode correctly — it reads the
  same pushed state the tile does, instead of a live query the camera stops answering
  while it is dark (which made the panel show "off" during an active privacy session).
- **Events mark as reviewed on open**: a clip now counts as reviewed the moment you
  open it, not when you close or advance past it (close/advance stay as a fallback).
- **Home Assistant entity accuracy**: Vehicle and Animal detections get proper
  icons (car, paw) instead of the generic motion glyph; the floodlight light entity
  is announced only for cameras with a real floodlight (not a bare status LED, so
  the E1 Pro no longer shows a phantom light); and cameras that don't support a
  floodlight or privacy mode now clear any discovery config an earlier,
  over-detecting build left behind, so stale switches and lights drop out of HA.
- **Mobile polish**: selecting a stream or the settings gear closes the sidebar
  drawer, the event-preview and camera-settings modals are vertically centered
  instead of anchored to the bottom, and the scheduled-capture summary wraps
  instead of being clipped.

## 0.8.8

### New

- **Email notifications for critical alerts (opt-in)**: configure one recipient
  and your SMTP server under Server settings → Notifications, and Neolink emails
  you when a recording drive fills up, the server is sustainedly overloaded, a
  camera goes offline past its (per-camera) threshold, or recording fails to
  write to disk — each with a "resolved" follow-up and de-duplication so you get
  one email per incident, not a flood. Storage-full alerts are on by default; the
  noisier ones start off so you opt into each. STARTTLS (587) and SSL/TLS (465)
  are both supported via a dependency-free SMTP client, and the whole subsystem
  runs isolated on its own task, swallowing every error so a misconfigured mail
  server can never affect recording, streaming or MQTT. The SMTP password is
  encrypted at rest (AES-256-GCM; key from an owner-only `secret.key` in the
  state dir or the `NEOLINK_SECRET_KEY` env var), write-only in the UI, and never
  returned by the API.
- **Redesigned Server settings**: the panel is organised into tabs — General,
  Recording, Home Assistant and Notifications — and is wider and responsive. Every
  tab shows a live unsaved-change signal (the changed field is highlighted, with a
  header badge and a footer note) so a pending edit is never missed, and the
  notifier save/test buttons stay pinned in the footer no matter how long the
  per-camera list gets. The notification address fields are validated as you type,
  the Record stream setting is a dropdown instead of free text, and the panel only
  closes via its ✕ — a stray click or Esc no longer discards staged edits.
- **Frictionless first run**: if the configured `config.json` doesn't exist yet,
  Neolink now writes a commented starter config and boots straight to the web UI
  instead of exiting. A fresh install (Docker, Unraid, Portainer) comes up with
  an empty camera wall and a "add cameras to config.json" hint rather than
  crash-looping on a missing file — you edit the config to add cameras and
  restart. A configuration with zero cameras is no longer a fatal error, just a
  logged warning.
- **Unraid Community Applications template** ([`unraid/`](unraid/)): a ready
  template plus icon so Neolink.NET can be installed from the Unraid app store
  (or added today via a template-repository URL).
- **Beta channel**: pushes to the `beta` branch publish
  `ghcr.io/borexola/neolink.net:beta`, a rolling pre-release image users can opt
  into to test new features without affecting the stable `latest`/release tags.
- **Storage in Home Assistant**: the Neolink.NET Server MQTT device gains
  per-tier free/used sensors (main always; clips/archive only when those tiers
  are configured — no phantom sensors, and removing a tier clears its sensors
  from HA) plus a **Storage full** `problem` binary_sensor that turns ON when a
  recording drive runs out of space, so an automation can notify even with the
  web UI closed.
- **Startup warning for mis-mounted tiers**: the server logs a warning when
  separately-configured storage tiers report identical capacity — the signature
  of a Docker bind mount that never attached to its drive and fell back to the
  container's root filesystem.

### Fixed

- **Stale "Siren (Press)" button in Home Assistant**: an early build exposed the
  siren as a momentary MQTT button; it has been a latched switch for a while, but
  the old retained discovery config lingered so HA kept showing a dead button.
  The bridge now deletes it on connect. Neolink publishes exactly a **Siren**
  switch (works) and a read-only **Siren sounding** sensor.
- **Privacy mode over-detected** on cameras that advertise `<sleep>` in DeviceInfo
  but don't actually support it (e.g. the RLC "Elite" WiFi line). Detection now
  also requires the channel's `remoteAbility` support flag, mirroring reolink_aio
  (the library Home Assistant ships).

## 0.8.7

### New

- **Tiered storage** — all optional, zero config changes required; a plain
  `recording.path` install behaves exactly as before:
  - **Fast clips tier** (`"clips_path"`): point it at an SSD and new event
    clips are written there for fast review, while continuous 24/7 footage
    stays on the main volume. Existing footage is found on both.
  - **Archive tier** (`"archive_path"`, BETA): with it configured, every camera's
    RECORDING settings gain **Archive event clips** and **Archive continuous
    footage** switches (both off by default). Retention stays the single
    clock: when an enabled type's retention expires, its footage is **moved**
    to the archive instead of deleted — e.g. "Keep event clips: 30" moves
    clips to the archive on day 30. One per-camera knob sets how long the
    archive keeps footage (blank = forever). Events, the timeline and
    continuous playback read archived footage transparently. Deletion from
    the archive keeps honoring the configured window even if archiving is
    later switched off. Use a separate drive for the archive — in Docker,
    map a second volume (e.g. `-v /mnt/bigdisk:/archive`), and map a volume
    for EVERY tier path you configure: an unmapped container path is created
    in the container's writable layer, where footage survives restarts but
    is destroyed with the container. On the Home Assistant add-on, point the
    paths at a NAS share under `/share` or `/media` instead — no mapping
    needed.
  - **Capacity watch**: at 90% used on any configured location the web UI
    shows an amber warning banner (dismissible for the session); when a
    location runs out of space, recording to it halts cleanly with a red
    banner (no partial files, one log line per transition) and resumes
    automatically once space is freed. New `GET /api/storage` reports every
    location; with split storage configured the Monitor page shows a live
    STORAGE section with per-location free space and warn/full states.

### Changed

- **Event playback speed is now a sticky preference**: pick 2× (or any speed)
  in the event player and every event you open afterwards — on the Events
  page or in the live view's popup — starts at that speed. Persisted in the
  browser, shared between both players; the timeline already remembered its
  speed the same way.
- **Camera settings panel redesign**: the dialog is now roomier
  (~1080 px on desktop) with the section cards flowing into two columns,
  each marked with a small icon; cards keep their grouping and collapse to
  the familiar single column on tablets and phones. Archiving controls sit
  under an ARCHIVE sub-heading flagged BETA.
- **RECORDING card restructured around a footage-lifecycle strip**: the card
  is grouped into EVENTS / CONTINUOUS (24/7) / ARCHIVE blocks, the archive's
  purpose is explained up-front (before anything is switched on), and a live
  FOOTAGE LIFECYCLE strip at the bottom traces each recording type through
  colored stages — e.g. "30 days on this server → archive · forever" — built
  from the settings as currently toggled, so flipping a switch shows exactly
  what it changes.

### Fixed

- Disk-write failures can no longer take the service down. The clip writer's
  final buffer flush runs on a raw thread — a volume that filled or vanished
  mid-clip could crash the whole process there; it now logs and marks the
  clip faulted instead. A dead clips volume no longer kills a detection
  event either (the event still registers and reaches Home Assistant,
  metadata-only), and settings/event-metadata persistence failures of any
  kind log a warning instead of bubbling up.

### Compatibility

- Existing `settings.json` files without the new per-camera archive fields
  load unchanged (archiving simply stays off), and servers without
  `archive_path` reject attempts to enable archiving with a clear message.

## 0.8.6

### New

- **Stream settings expose the camera's full encode tables**: each stream now
  offers its complete framerate and bitrate menus per resolution (e.g. up to
  8192 kbps on a 5K main stream) instead of one preset per resolution, and the
  menus preselect the camera's *current* values, read live over its HTTP API
  (new `GET /api/cameras/{name}/settings/stream`). Changes stage per stream;
  a bitrate/framerate-only change warns about the brief stream restart, while
  a resolution change keeps the stronger some-cameras-reboot warning.

- **More camera controls** (each appears only on cameras that support it,
  wire formats follow the reference Rust neolink):
  - **Optical zoom & focus sliders** in the camera panel — absolute position
    with the lens's real range; focus is there for the rare manual override.
  - **Manual siren, on until you stop it** — a confirm-gated control in the
    panel that latches the siren (manual mode, as the Reolink app does) and
    shows a **Stop siren** button while it sounds; one-shot bursts remain
    available through the API. Home Assistant gets a **Siren switch** whose
    state follows the camera's own siren pushes (the old status sensor is
    renamed "Siren sounding").
  - **Privacy mode (read/write, BETA)** — cameras that support it (the app's
    "sleep": lens dark, no video, no detections; E1/E-series, battery doorbell,
    Go PT) get a confirm-gated toggle in the panel, a
    `GET/POST /api/cameras/{name}/privacy` endpoint and a **Privacy mode
    switch** in Home Assistant, state-tracked from the camera's pushes.
    Support is detected from the login DeviceInfo's `<sleep>` advertisement —
    unsupported models answer the state query but ignore writes, so the query
    alone is not trusted. Wire format (read msg 574, write msg 575) follows
    Home Assistant's reolink library. And when a camera is darkened — from
    here or from the Reolink app — the live view says so: tiles show an opaque
    "🔒 privacy mode" cover (it also hides the frozen last frame the player
    would otherwise keep showing), and the sidebar carries a "private" badge
    (state from the camera's own pushes, self-healing the moment video flows
    again).
  - **Floodlight behavior** — brightness and the "turn on with motion at
    night" switch, staged and applied like other device settings (the on/off
    toggle stays under LIGHTS). Unknown camera fields are preserved verbatim
    on write.
- **Monitor page upgrades**:
  - **Recordings get their own card** (size on disk + share of the volume) and
    their own chart, instead of hiding in the DISK FREE footnote.
  - **History now covers 24 hours** — full 2-second detail for the last hour,
    one-minute averages beyond (≈160 KB of memory, so it's effectively free) —
    with new **6h and 24h** window buttons.
  - **The camera health cards obey the selected window**: uptime %, grade,
    outage counts and the timeline ribbon are all computed over the window you
    picked, not a fixed 24 h.

### Changed

- **Event playback fast-forwards the full-quality recording**: speeds above 1×
  now stay on the main-stream clip (audio is muted during fast-forward — the
  same trick that lets the timeline run high-res footage at speed — and comes
  back at 1×). A **16×** step was added, and when the low-res sub-stream twin
  exists an **HD/SD** toggle lets you drop quality on purpose (the choice is
  remembered). Previously anything above 1× silently switched to the low-res
  preview.
- **The event pop-up only closes on ✕**: Esc and clicks outside no longer
  dismiss it — closing marks the event reviewed, so an accidental tap must not
  count as "seen it".
- **Camera settings stage before they apply**: device settings in the camera
  panel (stream profile, night vision, status LED, PIR) no longer fire at the
  camera on every click. Edits collect as *pending* — amber-marked, with an
  Apply / Discard bar pinned to the panel's bottom — and nothing is sent until
  **Apply to camera**. Disruptive changes are called out up front: a profile
  write warns that the stream restarts, and a **resolution change warns that
  some cameras reboot**. Quick settings are applied before the disruptive one,
  and anything that fails stays staged for retry.

## 0.8.5

### New

- **Dedicated Events page (`/events`)** — recent detection events now have their
  own full page instead of only a pop-up, built for the phone-notification flow:
  a Home Assistant "motion detected" push can deep-link straight to
  **`/events/{camera}`**, which pre-filters to that camera and opens its newest
  clip on arrival — one tap from the alert to the footage. A recent-events list
  (thumbnails, camera, time, filters for camera / day / unreviewed) sits beside
  the player on desktop and stacks on phones. Every event and the header carry a
  one-tap **Go live** jump to that camera's feed, and an Events ⇄ Live toggle
  switches between reviewing and watching. The old pop-up is replaced; the live
  view's ambient event strip is unchanged.

- **Timeline calendar date-picker**: the date now opens a month calendar with
  **days that hold footage highlighted** (a dot under the number), so you can
  see at a glance which days have recordings and jump straight to one — no more
  clicking through empty days. Month navigation, a Today shortcut, and future
  days disabled. Backed by a new `GET /api/events/days` endpoint.

- **Deep link to one exact event**: `/events?event={id}` opens the Events page
  with that clip already selected and playing — even for events older than the
  recent list (the page jumps to their day). Event ids come from
  `GET /api/events`; backed by a new `GET /api/events/{id}` lookup. Per-camera
  links (`/events/{camera}`) now always land on that camera's most recent event,
  even when the last 24 hours were quiet.

- **"Last event" sensor in Home Assistant**: cameras that record events get an
  MQTT sensor whose state is the newest event's id, published (retained) the
  instant the event starts — at the same moment as the motion trigger. A
  notification automation can point its tap action at
  `…/events?event={{ states('sensor.<cam>_last_event') }}` for true one-tap
  alert-to-clip. Labels and start time ride along as attributes.

- **Deep-linked clips start muted on a fresh page load**: when the Events page
  auto-opens a clip on a full navigation (a notification tap), playback begins
  muted — no surprise audio, and no autoplay-blocked freeze. Clicking an event
  yourself keeps sound on as before.

### Changed

- **Beta tags retired**: two-way talk, timeline fast playback and scheduled
  capture have proven themselves — the BETA pills and wording are gone. (Talk
  remains opt-in via `ui.talk`, unchanged.)
- **Timeline toolbar tidied**: the controls are grouped into labelled clusters
  (date · transport · speed · view) inside subtle pills instead of one long run
  of buttons, and the clock/zoom/help settle at the right edge — easier to scan
  and it wraps by group on narrow screens.

### Fixed

- Suffixed builds (test images, prereleases like `0.8.5-events-test`) no longer
  see every published release as an "update" — the version comparison now uses
  the numeric prefix instead of silently falling back to 0.0 when the suffix
  didn't parse.

## 0.8.4

### New

- **On-demand clip recording** — record a camera on command, regardless of
  what it detects. Two triggers, one shared session per camera:
  - *Web UI*: every camera tile's toolbar carries a ⏺ record button (next to
    the mic in the maximized view). While recording, a red chip with a
    countdown sits on the video wherever the tile is shown — including for
    recordings started from Home Assistant — and clicking it (or the button
    again) stops early. `POST /api/cameras/{name}/record` for scripts.
  - *Home Assistant*: a `Record on demand` switch per camera (cameras the
    server records events for); `ON`/`OFF` on `{base}/{camera}/record/set`
    works for non-HA consumers too. Typical use: "record while the door is
    open".
  Most Reolink firmwares can't be told to record, but Neolink is the recorder,
  so it doesn't need their cooperation. Each trigger records **one clip** —
  pre-roll included — and **stops by itself** at `max_clip_seconds` (the HA
  switch flips back OFF; retrigger for longer) or when stopped early.
  Retention applies and the footage shows in the timeline/review strip labeled
  **External**. The trigger bypasses the event-type filter and capture
  schedule (explicit intent beats detection rules) but respects the
  per-camera events switch.

- **Home Assistant add-on** — Neolink.NET now installs as a native add-on on
  Home Assistant OS/Supervised: add the repository (one-click badge in the
  README), install, list your cameras in the add-on's Configuration tab,
  start. The MQTT connection to the Mosquitto broker add-on is wired
  automatically at every start, recordings land on the media share so clips
  appear in HA's media browser, and the web UI opens straight from the add-on
  page. Prebuilt amd64/aarch64 add-on images ship from the same release
  workflow; the plain-Docker route is unchanged.

- **Per-camera recording status in Home Assistant**: every recording-capable
  camera now carries a `Recording` binary sensor that is ON while the server
  is actually writing its footage — an event clip (camera detection or
  on-demand) or a continuous segment. The on-demand switch is deliberately
  named *Record on demand* so it can't be mistaken for a recording master
  switch next to a camera that already records continuously (setups that saw
  the earlier `Record` name keep their entity id — same discovery unique id).

### Changed

- **All detection sensors appear in Home Assistant immediately** — package,
  line crossing, intrusion and loitering used to be announced only after
  their first detection, so a freshly connected camera showed just the core
  four (motion/person/vehicle/animal) and automations for the smart types
  couldn't be built until one had already fired. Every detection type now
  gets its binary sensor up front; types the camera never pushes simply stay
  Clear. (The doorbell press event still appears on the first ring — a dead
  doorbell trigger on non-doorbell cameras would be worse than a late one.)
- Phone tile chrome slims down: the camera name now floats translucently over
  the tile's top-right corner instead of occupying the bottom bar, and the
  recording indicators shrink to a blinking dot — no more `REC` label (the
  on-demand chip keeps its countdown).

## 0.8.3

### New

- **The server reports itself to Home Assistant**: alongside the cameras, MQTT
  discovery now creates a "Neolink.NET Server" device carrying the monitor
  page's vitals — CPU, memory, storage free/used, recordings size, recording
  write rate, viewers, cameras online/recording and the start time. Published
  every `mqtt.stats_interval` seconds (default 60, minimum 5, 0 turns the
  device off) — the cadence is also editable in the web UI's server settings
  under *MQTT / Home Assistant*.

- **Timeline precision controls** — frame-level review without leaving the
  browser:
  - *Zoom*: scroll or pinch on the lanes to zoom from the full 24 h down to a
    one-minute window (double-click, the zoom chip or `0` resets; Shift+scroll
    pans). The ruler adapts from hours down to seconds as you go.
  - *Day overview*: a slim always-visible strip of the whole day above the
    lanes — drag its highlighted window to move the zoomed view.
  - *Hover readout*: a hairline with the exact time (tenths when zoomed tight)
    follows the mouse across the lanes.
  - *Transport cluster*: previous/next event hop, −10s/−1s/+1s/+10s around
    play/pause, with 0.1 s nudges for near-frame stepping (paused seeks now
    land within 0.05 s).
  - *Keyboard*: Space/K play-pause, ←/→ step (Shift 10 s, Ctrl 0.1 s), J/L,
    ,/. nudge, [/] event hop, +/−/0 zoom, T to type an exact time — the `?`
    button shows the cheat sheet.
  - *Slow motion*: ¼× and ½× join the playback speeds.
  - The clock is clickable: type 16:39:12 and you're there.

### Changed

- Server settings panel: the header now carries a live "● N unsaved" badge and
  the Save/Restart buttons live in a pinned footer with the unsaved-changes
  notice — no more scrolling to find out whether edits were written. Save
  errors and status also surface in the footer, always visible.
- **Mobile viewing polish**:
  - Pinch-to-zoom on any zoomable video surface (maximized tile, theater,
    quick view, fullscreen) — the HUD pill is now a convenience, not the only
    way in.
  - The zoom pill no longer squats over the picture on phones: it is smaller,
    translucent, and fades away after a moment — tap the video to bring it
    back.
  - Phones no longer get force-fed the main stream: tapping a camera opens its
    SUB stream, and maximizing a tile skips the automatic sub→main upgrade on
    touch devices — decoding 1440p+ into a phone screen is what made previews
    stutter on iPhones. Main remains one tap away in every stream picker.
  - Smoother live playback on iOS: media appends are batched (Safari's
    per-append cost at per-frame granularity starved the decoder), the player
    honors ManagedMediaSource's streaming hints, catch-up is gentler on
    Safari's pipeline, and no seeking happens while the app is backgrounded.
  - While a camera is maximized on a touch device, the hidden tiles pause
    instead of decoding invisibly in the background — that hidden load is what
    made phones feel mushy. Their streams keep flowing, so restoring resumes
    them near-live instantly; desktop keeps everything running as before.

### Fixed

- **Camera sensors going "Unavailable" in Home Assistant, and the missing
  "Neolink.NET Server" device** — one bug: discovery configs were serialized
  with explicit nulls for unset fields ("icon": null on the motion/person/
  vehicle/animal sensors, "device_class": null on PTZ buttons,
  "unit_of_measurement": null on the server sensors). HA rejects such configs
  and silently drops the entity. Existing camera entities coasted on their
  previously-retained configs until the next HA restart replayed the poisoned
  ones — then they flipped to Unavailable; the brand-new server device never
  appeared at all. Every discovery publish now omits unset fields entirely,
  and the fixed build heals HA on its next connect by overwriting the
  retained configs with valid ones — no HA-side action needed.
- **iOS: maximize/minimize buttons respond on the first tap, immediately.**
  `touch-action: none` had been applied to the whole maximized tile for
  pinch-zoom, which degrades WebKit's tap→click synthesis for the buttons
  inside it (taps took seconds to register). It is now scoped to the video
  element only — pinch is unaffected, buttons get native tap semantics back.
- **iOS: minimizing a maximized camera no longer needs two taps.** WebKit
  turns any content-revealing :hover into "first tap hovers, second tap
  clicks", and the tile controls' always-visible override was gated on
  viewport width — a phone held in landscape missed it. It is now gated on
  pointer type (touch), which is what the rule actually meant.
- **iOS: the fullscreen button works.** iPhones have no element-fullscreen API
  at all; the button now falls back to WebKit's native video fullscreen
  (webkitEnterFullscreen) and bridges its exit event so the UI notices, same
  as any other browser.
- Live players now log a per-stream health line to the browser console
  (every 30 s, only when something happened): skips over footage that never
  arrived (server/network drops) vs live-edge resyncs vs stalls — the
  distinction that tells you WHERE playback problems come from.
- **Live view on iPhone/Safari**: Safari on iPhone has no classic MediaSource —
  since iOS 17.1 Apple ships ManagedMediaSource instead — so every stream
  failed with a misleading "codec not supported" message (even plain H.264).
  The player now detects and uses ManagedMediaSource where that's what the
  browser offers, following Apple's contract (remote playback disabled,
  streaming hints drive the buffer pump). Browsers with neither API get an
  honest message; a genuine codec gap (H.265 without hardware decode) still
  suggests the sub stream.
- The timeline's buffering spinner no longer lingers after pausing: the veil
  now follows the video element's own events, so it clears the moment the
  paused frame is ready (and appears instantly on a genuine mid-stream stall).
- Content-free smart-event pushes (an empty `yoloWorldEventList`, sent
  constantly by some firmware) no longer land in the Info log on every
  reconnect — the capture aid only surfaces msg-600 payloads that actually
  carry data. `NEOLINK_DEBUG_ALARMS=1` still logs everything.

## 0.8.2

### Changed

- Timeline: playback starts automatically when the page opens and after
  switching days — previously the day sat paused until Play was pressed,
  which made the tiles look frozen. Pause is one click away as before.

### Fixed

- **Seeking recorded footage no longer takes forever** — two halves, no
  migration required:
  - New recordings: when a segment or event clip closes, it is finalized in
    place into a classic indexed MP4 (one moov with full sample tables)
    instead of remaining a per-frame-fragmented stream. Browsers previously
    had to crawl thousands of tiny fragment headers with range requests to
    build a seek index — barely noticeable on localhost, minutes over a
    remote link. No media bytes are rewritten, and a crash mid-recording
    still leaves a playable fragmented file.
  - Existing footage (and the segment currently being recorded): served
    through an on-the-fly index — the server synthesizes the same classic
    moov in memory (one sequential header scan, ~20 ms for a 256 MB segment,
    cached) and presents the file byte-mapped as a classic MP4. Terabyte
    archives are never modified (files stay bit-identical) and read-only
    storage works. Jumping into a 256 MB segment now lands in ~100 ms in the
    timeline either way, at a handful of range requests.
- The review strip no longer occupies the top of the live view when the strip
  filters hide every pending event — it collapses like it does with nothing to
  review, except while the filter editor is open (so filters can be adjusted
  back).

## 0.8.0

### New

- **Per-camera capture schedule (beta, opt-in)**: switch *Scheduled capture*
  on in camera settings → *Recording* to choose the days of the week and the
  time of day that camera records events — day chips plus a from/until window
  that can span midnight (e.g. 22:00–06:00 for nights-only). Applies to all
  selected event types; anything arriving outside the schedule is discarded.
  Off by default (capture at all times), and switching it off keeps the
  schedule for later instead of deleting it.
- **Home Assistant: every detection type is now a sensor**: package,
  line-crossing, intrusion and loitering join the motion/person/vehicle/animal
  binary sensors. They are announced on their first detection, so cameras
  without those features don't grow dead entities.
- **Home Assistant: "Visitor" doorbell sensor**: the doorbell press now also
  pulses a binary sensor that HA clears by itself a few seconds later — the
  existing *event* entity keeps the press history but always displays the last
  press, which read as a button stuck "pressed" on dashboards. Repeated
  "visitor" pushes while the chime rings no longer count as extra presses.

### Fixed

- Timeline playback shows a buffering spinner while a video is still loading
  under the cursor, instead of looking frozen with no indication.
- The timeline's play button no longer looks like the previous/next-day
  arrows: day navigation uses thin chevrons, play/pause is a solid accent
  pill with a filled glyph.
- On phones, a video tile's controls no longer overflow off the right edge of
  the screen: the camera name shrinks to an ellipsis and the buttons compact,
  so every icon stays reachable.

### Changed

- The server version moved from the sidebar's footer to the right edge of the
  top toolbar, so it stays visible with the sidebar collapsed.

## 0.7.0

### New

- **Two-way talk (beta, opt-in)**: a mic button on talk-capable cameras
  (doorbells etc.) streams your microphone to the camera's speaker — browser
  PCM is resampled and ADPCM-encoded server-side, no plugins. Disabled by
  default; enable in *Server settings → Web UI → Two-way talk*. Requires HTTPS
  (or localhost) for microphone access. While talking, an animated indicator
  overlays the video and the camera auto-unmutes.
- **Battery cameras (Argus etc.)**: auto-detected, with a charge badge in the
  sidebar (bolt while charging) and sleep-friendly behavior — the camera dozes
  while nobody watches and reconnects when a stream is opened, showing a calm
  "asleep" badge instead of offline-red. Per-camera `"always_on": true` holds
  it awake for events/recording. Direct TCP only (UID/P2P relay cameras are
  not supported); a sleeping camera cannot be woken remotely.
- **Perimeter protection events**: line-crossing / intrusion / loitering
  alerts configured in the Reolink app become their own event types with their
  own icons, so recordings can trigger on a crossed line instead of any person
  in view — no non-detection zones needed. **Off by default**: tick the new
  chips in the camera's *Event types* filter to record them; untouched setups
  keep recording exactly what they did before. Verified against real hardware
  (`smartAiTypeList` pushes); older token dialects handled too.
- **Timeline playback speed (beta)**: 1×/2×/4×/8×/16× chips on /timeline,
  persisted per browser. Playback is adaptive: if the server or the browser
  can't sustain the chosen speed, the timeline slows to match the slowest
  healthy stream ("catching up…" pill) instead of seek-storming; broken files
  are skipped rather than freezing the day.
- **Versioning**: the app reports its version (startup log, `--version`,
  `/api/features`, bottom of the web UI sidebar); release images self-report
  their git tag.
- `NEOLINK_DEBUG_ALARMS=1` capture mode: logs every alarm/smart-event push
  with full raw XML at Info level — for mapping new firmware dialects without
  the packet flood of full debug logging.

### Fixed

- **Event clips no longer run to the maximum length on chatty cameras**:
  repeated all-clear pushes kept re-arming the post-roll, so events often
  lasted exactly `max_clip_seconds`. The post-roll now arms once when motion
  stops — clips end ~`post_seconds` after the last detection.
- Opening a camera's settings panel no longer fails while feature discovery is
  slow: capability probes run in parallel and aborted requests are handled
  cleanly.
- The web UI's restart button no longer trips an `ObjectDisposedException` in
  the shutdown path (visible when debugging).
- Timeline fast playback keeps its speed across recording-segment boundaries.

### Changed

- Server settings panel warns about unsaved changes and highlights the save
  button until they are written.
- Camera-side protocol logs are tagged with the camera name instead of the
  ambiguous `ch0`.
- Performance: hot-path logging no longer builds strings at disabled levels;
  codec parameters are only scanned on keyframes; the timeline syncs all
  players in one interop call per tick.
- GitHub Actions pinned to commit SHAs and kept current via Dependabot.
