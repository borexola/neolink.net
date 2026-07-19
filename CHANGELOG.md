# Changelog

Release notes for Neolink.NET. Releasing works by tagging `vX.Y.Z` — the docker
workflow bakes the tag into the app as its version (see "Versioning & releases"
in the README). Paste the matching section below into the GitHub release.

## 0.9.5 — unreleased

### Fixed

- **SD playback failures show WHY** instead of a silently black player: a fetch
  the camera refuses now overlays "Playback failed" on the player with a pointer
  to the server log, and the log names the reason. Notably: the Video Doorbell
  WiFi's firmware advertises recording download but its HTTP Download handler
  crashes (verified against the camera: it drops the connection even for a
  parameterless Download, while Search and Snap on the same session work) — so
  its SD list shows, but the clips themselves can only be viewed in the Reolink
  app until Reolink fixes the firmware. Other models are unaffected.

- **SD-card recordings actually play (and download)**: two independent bugs.
  Downloads failed with an empty 400 — the UI sends `?dl=1` and ASP.NET's
  boolean binding rejects `1` before the handler even runs, so nothing hit the
  logs. And playback never started at all: the camera serves a recording
  strictly sequentially (no byte ranges) while its MP4s keep the index (moov)
  at the END of the file — a browser streaming from the front never finds the
  index and waits forever. Normal-size recordings now spool to a server temp
  file first and are served with full range support: playback starts after a
  short fetch, scrubbing works, and the browser's range probes hit the same
  spooled copy instead of re-downloading from the camera (kept 10 minutes,
  cleaned automatically; very large files stream directly as before).

- **SD-card browsing works on the Video Doorbell WiFi**: that firmware returns
  each recording's `size` as a quoted string (`"16058135"`) rather than a
  number, and the parser's numeric cast threw on the first entry — failing the
  whole day's search and reading as "N entries but none usable" even though the
  recordings were right there. Size (and the SD-card capacity/free fields) are
  now read tolerantly, so a number or a numeric string both parse.

- **HTTP features no longer vanish on Wi-Fi cameras that are actually fine**:
  Wi-Fi cameras drop idle keep-alive connections silently (power save, AP
  roam) — no TCP reset, so a pooled connection can be a corpse. Requests
  written into one waited out the whole 10s command cap for a reply that
  never came, reading as "camera HTTP API did not reply" streaks against a
  camera that answers a fresh connection in milliseconds. Idle camera
  connections are now retired after 30 seconds (a sweep after a quiet period
  starts on fresh TCP — about 1ms on a LAN), and connects get their own 5s
  bound so a dropped SYN fails fast. A camera command that fails on a reused
  (dropped) connection is also retried once on a fresh one — the retirement
  above is preventive; the retry recovers the miss that slips through. The
  "HTTP API is not answering" warning stops claiming "the port is open" — a
  no-reply timeout can't tell a slow camera from dropped packets, and the
  advice now mentions firewall/VLAN/Docker networking as suspects.

- **HTTP features never fail silently again**: the camera-settings panel shows
  every HTTP-backed section (picture, volume, OSD, sensitivity…) blank when
  the feature sweep reads nothing. A recent change that fails the sweep fast on
  an unreachable HTTP API also, unintentionally, stopped the "not answering"
  warning from firing — leaving "no settings, no error" with no clue why (the
  camera still streams fine over Baichuan, which masks it). The sweep now logs
  one clear warning when it comes back completely empty on a real transport
  failure, naming the likely causes (HTTP disabled on the camera, or
  firewall/VLAN/Docker dropping HTTP traffic).

- **Camera settings no longer randomly load "limited"**: the panel's HTTP
  feature sweep (picture, OSD, quick replies, volume, presets, sensitivity…)
  stopped at the first slow answer — one mid-sweep timeout armed the general
  60-second HTTP backoff, blanking every remaining section AND the entire next
  panel open. The sweep now forgives its first two failures (the remaining
  sections still get their try; a dead camera costs two timeouts, not ten),
  and the last good sweep is cached per camera: whatever a sweep can't read is
  filled from the cache, so a busy camera costs freshness of near-static
  config, not whole panel sections. The whole sweep also answers within 25
  seconds no matter what (late steps fill from the cache), and failures are
  only forgiven once a login has succeeded — a camera with no HTTP API at all
  fails fast instead of stacking login timeouts.

- **No more "Cannot reach http://127.0.0.1:8655 … 15 seconds elapsing"**: the
  web UI gave every request to its own server a blanket 15-second budget —
  below what a legitimate SD-card file search or a slow settings sweep takes,
  so the UI cancelled its own server mid-work and blamed the connection.
  Each call now brings a budget matched to its work (settings 30s, SD file
  search 95s — always above the server's own internal budget), and a genuine
  timeout reads like one: "the camera is taking too long to answer", not a
  loopback URL.

- **Flip/mirror changes show in the panel reliably**: the camera applies
  flip/mirror by restarting its video pipeline, and the panel's re-read could
  race that restart — the camera echoed the pre-write value and the toggle
  looked like it hadn't taken. A confirmed write now outranks the camera's
  echo, in the panel and in the server's feature cache.

- **Flip/mirror toggles only show when the camera can actually flip**: the
  panel offered them whenever the camera's ISP config carried rotation and
  mirroring values — but firmwares echo those fields even on models that
  don't support flipping (the Elite WiFi line), leaving toggles that silently
  did nothing. The gate is now the camera's GetAbility table (ispFlip /
  ispMirror — the authoritative "does this model actually have it" source,
  the same one the auto-track control trusts); some firmwares list the fields
  even in their ISP range table, so the range alone wasn't enough. Cameras
  whose ability table can't be read fall back to the range check, then to the
  old behavior. The Home Assistant flip/mirror switches follow the same gate
  automatically.

- **SD-card browsing works on busy cameras**: the file search asked the camera
  to walk its ENTIRE day in one query — an event-heavy doorbell that is also
  encoding streams can't finish that inside any budget, so the calendar
  answered and the file list never did. Worse, the timeout armed the general
  HTTP backoff, making the "hit refresh to retry" advice silently do nothing
  for a minute. The search is now paged into four 6-hour windows (short walks;
  a failed window costs a gap, not the day, and a partial day still shows),
  failures log at Info so "the server log has details" is actually true, and a
  failed search no longer poisons the backoff — refresh really retries. SD
  browsing is an explicit user action, so it now also runs even while the
  general HTTP backoff is armed, and a camera with no working HTTP API at all
  (some models simply have none) answers with the "can't be browsed" banner
  immediately instead of walking every search window into a timeout.

### Changed

- **The camera rail gains a settings button**: the on-video rail on the
  maximized tile (below the zoom pill) now ends in a gear that opens that
  camera's settings dialog — no trip back to the sidebar. Because settings
  apply to every camera, the rail now shows on every maximized tile, not just
  PTZ/talk-capable ones. On touch the rail also fades after a moment of
  stillness exactly like the zoom pill (any tap on the video brings both
  back) — a rail in use, pad open or talking, never fades away.

- **On-video controls get out of the way like a video player's**: on the
  maximized (or browser-fullscreen) tile, a still mouse now fades ALL the
  on-video chrome after a moment — the toolbar, the camera rail (PTZ + mic),
  the zoom pill, and the cursor itself — and any mouse movement brings it
  straight back. The camera rail is also hover-revealed like the rest of the
  chrome instead of sitting ghosted on the video permanently, and the zoom
  pill now shows GHOSTED on every zoom surface (live tiles, quick view, the
  event pop-up, the Events page, timeline monitors) reaching full strength
  only while the mouse is actually on it. Touch keeps its tap-revealed
  behavior everywhere — there is no cursor to go still.

### New

- **Picture settings over ONVIF for cameras without the Reolink HTTP API**
  (beta): models like the Lumus line expose no Reolink HTTP CGI, so the picture
  sliders (which ride that API) were simply absent. When a camera has no HTTP
  API — or its HTTP API can't be reached — brightness, contrast, saturation,
  sharpness and the day/night (IR-cut) filter are now read and written over
  ONVIF instead, the standards-based imaging service the camera already speaks.
  Strictly a fallback: a healthy HTTP camera never touches the ONVIF path.
  Values are scaled to the same 0-255 the HTTP path uses (from the camera's own
  ONVIF ranges). The endpoint is the Baichuan host — probed on Reolink's ONVIF
  port 8000 first, then port 80 — overridable with a per-camera `onvif_address`
  (port 80 only half-proxies ONVIF on many Reolink firmwares: the device service
  answers but the media service 502s or times out, so 8000 is tried first). Home Assistant gets the same entities over MQTT (brightness,
  contrast, saturation, sharpness numbers + a day/night select) — the reads,
  states and writes all flow through the shared imaging path, so a slider change
  in HA routes to ONVIF automatically. Hue, anti-flicker, flip/mirror and HDR
  have no ONVIF path in this release and stay hidden for ONVIF-only cameras.
  Requires ONVIF enabled on the camera with valid credentials.

- **Event-type chips only list what the camera can emit**: the recording tab's
  event types used to offer every known detection on every camera. The list is
  now capability-filtered where a definitive signal exists — the per-type
  AI-alarm probe proves which AI detections (person, vehicle, animal, package)
  the firmware actually answers for, and the doorbell type needs a doorbell.
  Types with no reliable capability signal (crying, the perimeter trio,
  motion) always show, as does everything while the camera is offline or has
  no HTTP API — only the disproven is hidden, never the unknown. A note says
  when types were hidden. The verdicts are cached server-side
  (camera-state.json), so the panel renders the filtered list IMMEDIATELY on
  open instead of showing every type and pruning when the camera answers; the
  live probe refreshes the cache, so the list only shifts when the camera's
  answers actually changed (firmware update, camera swapped).

- **Cameras with nothing to record idle at near-zero CPU**: a wired camera
  with nothing consuming its frames used to stream and process video around
  the clock even with nobody watching. Such cameras now hold a control-only
  connection: every sensor, detection push, control and the settings panel
  stay fully live, and the video stream starts the moment someone actually
  watches (sub-second — measured 330 ms from RTSP request to playing, since
  the session is already logged in) and drops again 60 s after the last
  consumer leaves. "Consuming" is judged live from the per-camera switches:
  detection events on, 24/7 on, or a running clip capture each keep the video
  flowing exactly as today — and flipping the last one off lets the camera go
  idle, no restart needed. An explicit `always_on` keeps the old behavior, and
  battery cameras keep their full park (stronger medicine). No configuration
  needed.

- **Camera SD-card playback (preview)**: the Events page grew a labelled
  **Source** switch — *Server* (this server's recorded events) or *SD card* —
  that browses the recordings a camera stored on its own SD card — the
  footage that exists even when this server was down, plus the clips battery
  cameras record locally without ever streaming. Pick a camera and a day
  (days with recordings are offered as chips, read from the camera's own
  calendar), and play or download any recording. Needs the camera's Reolink
  HTTP API (`http_address` / HTTP enabled) and a mounted SD card. Behind it:
  `Search` (calendar + file list) and a gate-free streaming `Download` in the
  HTTP client, and `GET /api/cameras/{name}/sdcard/{days|recordings|download}`.
  Labelled PREVIEW (one notch below beta) deliberately: whether playback works
  at all depends on per-model firmware — see the Video Doorbell WiFi note in
  Fixed below.

- **Detection sensitivity (beta)**: motion sensitivity (1-50, normalized
  across the two firmware dialects — newer `MdAlarm` firmwares carry it
  straight, older `Alarm` tables store it inverted) and per-type AI
  sensitivity (person/vehicle/animal/face/package, 0-100) are now sliders in
  the camera panel and `number` entities in Home Assistant. These tune the
  camera's own thresholds, so its app notifications and this server's event
  recording both follow.

- **HDR and OSD control (beta)**: cameras whose ISP reports an `hdr` field
  get an HDR select in the picture section (plain on/off or off/low/high —
  the camera's own range table decides) and an HA `select`; the on-screen
  display — camera-name and timestamp overlays with their positions, and the
  Reolink watermark — is editable in a new panel section, with the position
  options read from the camera's own range table.

- **Firmware-update check (read-only)**: cameras that answer `CheckFirmware`
  show an "Update" badge in the panel's device bar when Reolink offers newer
  firmware, and Home Assistant gets a diagnostic `binary_sensor` with
  device-class `update`. Nothing is ever installed from here — updating stays
  in the Reolink app; the verdict is cached for hours so panel opens don't
  turn into cloud traffic.

- **The camera settings dialog is draggable and resizable (desktop)**: grab
  the header to move it, the corner grip to resize — so a staged change (OSD,
  picture) can be watched applying on the video *beneath* the modal instead of
  behind it. It stays a modal (only ✕ dismisses), can't leave the screen's
  usable area (dragging and resizing both clamp to an 8 px margin), phones
  keep the fixed sheet, and closing discards the geometry — a reopened dialog
  is always back to the centered default.

- **Digital zoom on the Timeline monitors**: every visible monitor on the
  Timeline page gets the same digital zoom as the live views — mouse-wheel on
  the video zooms around the cursor (the studio's focused monitor included),
  drag pans while zoomed, and each tile carries the familiar side HUD
  (+ / − / 1:1 with a percentage badge). HUD clicks never toggle the studio
  focus, and the studio's thumbnail rail hides the HUD (focus a monitor to
  zoom it, like the snapshot button).

- **SD-card file searches no longer masquerade as "no recordings"**: the file
  search walks the camera's card — on an event-heavy day (doorbells) that
  takes far longer than a config read, and it was tripping the 10-second
  per-command cap; the failure then displayed as an innocent "No recordings
  on the camera's SD card" even though the calendar showed the day as
  recorded. File searches now run uncapped under their own 45-second budget,
  a stream the firmware rejects searching no longer aborts the other one, a
  failed search shows as a retryable error instead of an empty day, and a
  day that parses to zero usable files logs what the camera actually
  answered so unmapped firmware dialects are diagnosable from the log.

- **Camera HTTP reads now budget for the login round-trip**: a config read
  without a live session token pays for a `Login` before the command — and on
  a Wi-Fi camera busy pushing streams (dual-lens Duos especially) the login
  alone can take most of the old 6-second budget, so first reads kept timing
  out, armed the retry backoff, and the panel's HTTP-backed sections went
  missing while the camera's API was actually fine (the "responds too slowly"
  warning at startup). Login-bearing calls now get the roomy 20-second
  budget; calls riding a cached token keep the tight one.

- **Digital zoom on the Events page**: the clip player on the dedicated
  `/events` page now has the same digital zoom as the live views and the event
  pop-up — mouse-wheel or pinch to zoom around the cursor, drag to pan while
  zoomed, double-click for 1:1, with the familiar side HUD (+ / − / 1:1 and a
  percentage badge; hover-revealed on desktop, always available on touch).
- **On-video PTZ pad**: maximizing a PTZ camera (clicking it in the sidebar)
  now shows a small translucent **PTZ** pill in the bottom-right corner of the
  video. Clicking it opens a compact pan/tilt pad — hold an arrow to move,
  release to stop, square to force-stop — without opening the camera's
  settings dialog. The pad is deliberately non-intrusive: ghosted until
  hovered or opened, collapsed back to the pill per camera, and it claims the
  one tile corner no other overlay uses. Cameras without PTZ never show it.

- **On-video camera rail (PTZ + talk)**: maximizing a camera now shows a
  quiet glass pill on the right edge of the video, directly below the zoom
  HUD — the two read as one column of controls. It carries a **PTZ** button
  on pan/tilt cameras (opens a compact hold-to-pan pad that flies out to the
  left — hold an arrow to move, release to stop, square to force-stop) and a
  **mic** button on talk-capable cameras (same two-way talk as the toolbar
  mic, with the red pulse while live). Deliberately non-intrusive: ghosted
  until hovered, opened or talking, collapsed per camera, and clear of every
  other overlay — the earlier corner placement collided with the camera name
  and the resize grip. Cameras with neither feature never show it.

### Changed

- **Fewer syscalls and copies on every stream**: three structural costs in the
  per-frame path are gone. The camera read loop now reads through a 128 KB
  buffer, collapsing the header-sized reads (one recv syscall each, hundreds
  per second per stream — expensive under virtualization) into few large ones.
  An RTSP viewer's video is written one access unit per send instead of one
  RTP packet per send (~700 writes a second per 4K viewer before). And a media
  message's payload is no longer copied and allocated a second time during
  parsing — allocations per streamed byte measured 2.01 before, 1.01 after —
  with frame headers now parsed in place instead of through throwaway arrays.

- **Media decryption is dramatically cheaper on FullAes cameras**: firmwares
  that negotiate full-stream encryption (level 0x12 — most current Reolink
  models) AES-encrypt every video byte on the wire, and Neolink.NET decrypted
  it 16 bytes at a time, rebuilding the AES key schedule for every message.
  On an idle 2K camera that alone burned 15-20% of a CPU core with nobody
  watching. Decryption now produces the whole keystream in a single AES pass
  per message (CFB decryption has no serial dependency) with the keyed AES
  cached for the life of the session, and XORs it out a word at a time; the
  frame reader also stopped allocating a string per video frame. Cameras on
  older encryption levels (XML-only AES, BCEncrypt, none) were never paying
  this cost and are unaffected. The selftest proves the fast path
  byte-identical to the reference implementation across every length shape.

### Fixed

- **Applying a camera setting no longer makes its panel section vanish**: after
  "Apply to camera" the panel re-reads every HTTP-API feature in one combined
  sweep — and a camera still busy applying the write (an OSD/watermark change
  re-renders the burned-in overlays; flip/mirror restarts the video pipeline)
  can time out parts of that re-read, which then reported those features as
  absent. The setting had applied fine, but its section disappeared from the
  panel right after. The refresh now keeps the previous reading for any
  feature the re-read couldn't reach instead of dropping the section.

- **The event pop-up no longer goes black on the first zoom while playing**:
  the first zoom promotes the video to its own composited layer, and the frame
  already on screen doesn't reliably land in the new layer. The existing
  repaint nudge (re-presenting the frame with a zero seek) only covered paused
  clips — but a playing clip can sit black too when no fresh frame happens to
  be due (a stalled buffer, a long GOP, the end of the clip). File-backed
  players (the event pop-up and the /events page) now always get the nudge on
  zoom-in; live tiles keep the paused-only rule, since a playing live stream
  repaints on the next arriving frame anyway and a zero seek would jolt it.

- **A 4K camera snapshot no longer disconnects the Home Assistant bridge every
  2 minutes**: the bridge publishes each camera's JPEG snapshot to MQTT
  (base64) for the HA camera entity, and Mosquitto 2.1+ ships a new default
  2 MB `max_packet_size` — it answers a bigger publish by DISCONNECTING the
  client ("oversize packet"), so one high-resolution camera (a dual-lens Duo's
  snapshot easily exceeds 2 MB in base64) cycled the connection every camera
  shares, exactly on the 2-minute snapshot refresh. The client now knows the
  broker's limit (`mqtt.max_packet_size`, default 2 MB to match modern
  Mosquitto) and drops an oversized publish with a one-time warning naming the
  topic and size instead of ever sending it. And so the HA picture keeps
  working on those cameras rather than going stale, the bridge now asks the
  camera itself for a SMALL snapshot: the HTTP API's Snap command scales the
  image at the source (the Baichuan snap already requests the sub stream, but
  dual-lens firmwares ignore that and send the full panorama). Any camera —
  dual-lens, 4K, whatever comes next — now feeds HA a right-sized preview;
  the full-resolution HTTP snapshot endpoint is unaffected. To push large
  images over MQTT anyway, raise `max_packet_size` both on the broker (a
  Mosquitto customize file) and in the neolink `mqtt` config.
- **An unreachable camera HTTP API is now called out, once, with the remedy**:
  when a camera's HTTP port doesn't answer (many models ship with HTTP
  disabled), the features that ride it — picture settings, volume, Wi-Fi
  signal, PTZ presets, scaled snapshots — used to just silently not appear.
  The server now logs one clear warning per outage naming the camera, the
  missing features and the fix (enable HTTP in the Reolink app under
  Settings > Network > Advanced > Port Settings, or set `http_address`), and
  an info line when the API becomes reachable again. The warning waits for
  three consecutive failures — a single timeout during startup, when every
  camera is juggling logins and stream starts at once, is routine — and
  connection-refused errors are now properly treated as "unreachable" for
  the retry backoff instead of surfacing as raw errors. The snapshot fetch
  also gets a roomier timeout (20 s instead of the 6 s meant for small JSON
  reads): it pays for a login plus an image download over the camera's
  Wi-Fi, and the tight cap was cancelling perfectly healthy cameras into
  the full-size fallback. A REJECTED login gets its own
  one-time warning carrying the camera's error text — and pauses retries for
  15 minutes, because Reolink locks the account after a handful of failed
  logins and the old silent 2-minute retry cadence fed that counter forever.
- **Beta builds now self-report `X.Y.Z-beta.<build>`**: each beta push gets a
  unique, identifiable version without consuming stable patch numbers, so
  stable releases increment by exactly one from the previous release.
- **De-flaked the RTP fragmentation self-test**: it filled its test NAL with
  random bytes, which about once in a few thousand runs contained a spurious
  `00 00 01` start code — the packetizer rightly split there and the test's
  byte count came up short. The fill is now deterministic and start-code-free
  (real encoders emulation-prevent start codes inside a NAL).

## 0.9.3

### Fixed

- **MQTT no longer degenerates into a clockwork disconnect loop**: if an MQTT
  send was interrupted after part of the packet was already on the wire — for
  example a publish cancelled by its caller mid-write — the client kept the
  connection open and kept writing. Every subsequent packet then landed at a
  wrong frame boundary, and the broker, after absorbing the garbage for a while,
  killed the session with `oversize packet` — observed against the Home
  Assistant Mosquitto add-on as a disconnect every ~2 minutes, reliably, with
  a clean reconnect each time (state republish on reconnect masked it, so the
  main symptom was log churn and brief entity gaps). Two changes close it for
  good. First, socket writes no longer run under the caller's cancellation at
  all: once a frame starts, it completes, so a camera session winding down (a
  flaky camera reconnecting, a timed-out probe) can never abort a publish
  partway and poison the connection every other camera shares — this was
  triggered in the field by adding one unstable camera to an otherwise healthy
  set. Second, a write that does fail (or stalls for 30 s against a wedged
  broker) closes the socket immediately — the frame boundary is unknowable, so
  the only safe move is a clean reconnect. A self-test drives both rules
  against a stub broker socket.

- **Home Assistant no longer rejects the anti-flicker state on indoor cameras**:
  indoor firmwares (the E1 line among them) report a fourth anti-flicker value,
  `Off`, which the bridge's select entity didn't announce — so every periodic
  state publish made HA log `Invalid option for select....anti_flicker: 'Off'`
  (hundreds of occurrences over a few days). `Off` is now a first-class option
  everywhere: the HA select announces it (and defensively keeps any novel value
  a camera currently reports), the HA command handler accepts it, the web API
  validates it, and the camera panel's anti-flicker dropdown offers it.

## 0.9.0

### New

- **Camera settings show the camera's address**: the identity strip at the top
  of each camera's settings (next to model, firmware, hardware, serial) now
  includes its configured network address (host, or host:port on a non-default
  port). It stays visible even while the camera is offline — that's exactly when
  knowing which IP to go check is useful.
- **Camera settings open instantly**: the panel no longer sits on a single
  "Querying camera…" screen until every capability probe finishes. The parts
  the server already knows — the recording settings and the camera's address —
  render immediately, and the device info and controls (which have to be read
  from the camera) fill in behind a small "Querying the camera…" hint. The
  recording tab is usable while that runs, and even while the camera is offline.
- **Browser alerts — per-camera, per-detection notifications**: the new
  *Alerts* tab in settings (visible to every user) picks which detections pop
  a system notification, camera by camera — person, vehicle, animal, package,
  doorbell, crying, perimeter types, motion. Clicking a notification opens the
  exact clip. Works while the app is open — tab or installed PWA, foreground
  or minimized (nothing arrives once it is fully closed); a per-camera-and-type
  cooldown (default 60 s) keeps a busy driveway from firing twenty times, and
  a page load never replays history as a burst of stale alerts. Preferences
  persist per account (`/api/me/settings/notifications`) and follow you across
  browsers; the notification permission itself is per device. Like two-way
  talk, browsers only allow notifications on HTTPS or localhost. The tab is
  honest about its nature: alerts save instantly (its footer says so — no
  config.json, no restart, and the server Save/Restart buttons never appear
  there), the per-camera picker only shows while alerts are on, and each
  camera is its own card — name on top, its detection chips below, with an
  accent highlight and an "N on" count on cameras that alert.
- **Snapshot endpoint — `GET /api/cameras/{name}/snapshot.jpg`**: a current
  still image straight from the camera's own JPEG snapshot command, for
  notification thumbnails, wall dashboards and scripts. Poll-friendly by
  design: a short per-camera cache (default 5 s, `?maxAge=` tunes it) plus a
  single-flight gate collapse a poll storm into one camera command, and a
  sleeping battery camera is **never woken by a poll** — it serves its last
  frame, honestly labelled via `X-Snapshot-Age` / `X-Snapshot-Stale` headers.
  Cameras without the command (generic RTSP) return 404. Auth works like the
  stream URLs: the RTSP user credentials open it over HTTP Basic — same
  per-camera permitted rules, and they keep working when web-UI accounts are
  on — so `http://user:pass@host:8655/api/cameras/{name}/snapshot.jpg` pairs
  with the rtsp:// URL you already use (a web session or `?token=` works
  too). See "Snapshots over HTTP" in the README.
- **Storage forecast — "fills in ~23 days at the current rate"**: the Monitor's
  DISK FREE card and per-location storage cards now project when each disk runs
  out. Free space is sampled every 15 minutes and persisted in the state dir
  (storage-trend.json), so the trend survives restarts and covers up to a week.
  The projection is the net trend, not the raw write rate — once retention
  deletes as fast as the cameras record, it says "not filling at the current
  rate" instead of inventing a fill date; a fresh install stays quiet for its
  first ~6 hours while it gathers data. `GET /api/storage` carries the same
  numbers (`forecastState`, `forecastDays`).
- **Home Assistant: a sleeping battery camera is no longer "Unavailable"**:
  per-camera availability used to mean "connected right now", so a battery
  camera parked on purpose between viewers dragged every entity — battery
  reading, switches, detection sensors — to Unavailable within a minute of
  each nap. Parked-on-purpose now counts as alive: the device keeps its
  retained readings visible while it dozes, latched detection sensors are
  cleared on the way into the nap (no phantom "Detected" frozen for hours),
  and a new diagnostic **Asleep** sensor says why readings are paused.
  Nothing else moves: a suspended camera still reads offline (deliberate), a
  genuinely unreachable camera still goes Unavailable after the same 45 s
  grace, and mains/TCP cameras are untouched. Detections while asleep still
  need the camera awake — enable `wake_capture` to reconnect the moment the
  camera wakes itself on PIR.
- **The brand dot becomes a padlock when footage encryption is on**: the glowing
  dot next to the NEOLINK.NET logo turns into a padlock in the same accent glow
  whenever the server encrypts footage at rest, so anyone signed in can see at
  a glance that recordings are protected (hover it for the explanation).
- **The encryption key is visible and sanity-checked**: the Recording settings
  tab now reports which key the running server uses — `NEOLINK_SECRET_KEY`
  (environment) or the state dir's `secret.key` with its full path — plus a
  one-way fingerprint (SHA-256 prefix; the key itself is never shown) to match
  against a backup. If the key file sits on the **same disk** as the footage it
  protects, the tab and the startup log warn plainly: a stolen disk would carry
  its own key — move `ui.state_dir` or use `NEOLINK_SECRET_KEY`. An ephemeral
  key (unwritable state dir) is also called out before it can eat footage.
- **Server settings: Save & restart, plus Discard**: with unsaved changes, the
  Restart button becomes *Save & restart* — it writes the staged edits to
  config.json first and only restarts when the save succeeded — and a new
  *Discard* button drops the staged edits, snapping every field back to what
  the file actually says. With nothing staged the buttons behave as before.
  Confirming a restart now shows a clear full-screen **"Restarting the
  server…"** screen instead of the generic "connection lost" flash: it is
  browser-owned (the server stops talking the instant it goes down), waits for
  the server to go down and come back, then reloads the page automatically.
- **Background work is visible to admins**: while the server archives aged
  footage, the live view's sidebar shows a progress card — what is moving
  (camera and day), how much (bytes moved of the total, measured up front) and
  a percentage with a live progress bar — and it disappears when the pass
  completes. It is **pinned just above the account row** (so it never scrolls
  away), **dismissible** per run (the ✕ hides this run; the next run shows
  again), and can be turned off entirely with `ui.show_background_tasks: false`
  (Server settings → General). The strip is a general home for background
  processes admins should know about, fed by a new admin-only `GET /api/background`
  endpoint (open on a no-auth server, like the other admin surfaces), so future
  long-running jobs can report there too.
- **The camera list scrolls on its own**: the sidebar's camera list now scrolls
  in its own container, so the account row (and the background-process strip
  above it) stay pinned at the bottom no matter how many cameras you have —
  previously a long list pushed them off the bottom into the scroll.
- **Footage encryption at rest (beta, opt-in)**: turn on *Server settings →
  Recording → Encrypt footage* (or `"recording": { "encrypt": true }`) and new
  event clips, previews, thumbnails and 24/7 segments are written as chunked
  AES-256-GCM — a stolen disk or copied backup exposes nothing, and tampering
  is detected on read. Playback, timeline seeking and exports are unaffected
  (files decrypt transparently, hardware-accelerated, chunk-by-chunk on
  demand), and plaintext footage from before the switch keeps playing side by
  side, forever. The key is the server secret (NEOLINK_SECRET_KEY or the state
  dir's secret.key) — back it up; without it encrypted footage is unrecoverable.
- **Home Assistant reflects camera changes instantly**: changing a camera setting
  in the web UI — detection events, 24/7 recording, suspend, night vision,
  floodlight/spotlight, PIR, siren, privacy, volume, auto-tracking or the picture
  settings — now re-publishes that camera's state to Home Assistant the moment it
  applies, instead of on the bridge's periodic (~20s) refresh. Previously a web-UI
  change could take up to ~20 seconds to show in HA — long enough for an automation
  to act on a stale switch. Changes made from HA already applied immediately; this
  closes the gap for changes made in the web UI, so the two never disagree for long.
- **Continuous (24/7) recording switch in Home Assistant**: the web UI's "Record
  around the clock" toggle is now also a switch on the camera's HA device, so
  around-the-clock recording can be turned on and off from HA and automations. It
  is the same server-side setting the web UI flips — the two stay in sync — and
  the recorder picks up the change at once. Available whenever continuous
  recording runs for the camera (Baichuan or generic RTSP), and, like Suspend, it
  stays usable while the camera is offline or asleep.
- **Detection events switch in Home Assistant**: the "Detection events" master
  toggle from the web UI's camera settings is now also a switch on the camera's
  HA device, so automations can pause and resume event capture (say, stop
  recording clips while someone is home). It is the same server-side setting the
  web UI flips — the two stay in sync — and OFF stops the server recording event
  clips for the camera (and on-demand capture); the camera keeps detecting, so
  the detection binary_sensors still report. Like Suspend, the switch stays
  usable while the camera is offline or asleep.
- **Install the web UI as an app (PWA)**: Chrome/Edge offer an install icon in
  the address bar, iOS Safari has *Share → Add to Home Screen*, macOS Safari
  *File → Add to Dock*. The installed app is the same server-rendered UI in its
  own window with the Neolink icon — nothing is cached, so it is always exactly
  as current as the server. When the server is unreachable a branded screen
  takes the place of the browser error page and reconnects automatically.
  Installing needs a secure context (HTTPS or `localhost`); on plain
  `http://lan-ip` the UI works as before, browsers just hide the install option.
- **Wake-capture for battery cameras (opt-in)**: a sleep-friendly battery camera
  normally only connects when a viewer opens its stream, so motion events while
  nobody is watching are missed. `"wake_capture": true` keeps a cheap liveness
  poll running while the camera sleeps and connects the instant it wakes itself
  (motion), captures the event, then lets it sleep again — catching events without
  holding the camera awake like `always_on`. The poll can't reach a sleeping
  camera (radio off), so it costs no battery and is silent in the log. Default off
  (unchanged park-until-viewer behavior); no effect with `always_on` or on
  non-battery cameras.
- **Baichuan-over-UDP transport for battery-only cameras (beta, opt-in)**:
  some battery models (parts of the Argus line) never listen on TCP — they speak
  Baichuan over UDP — so they could never connect. Setting a camera's `uid`
  (Reolink app → device info, or the sticker) and `"udp": true` connects it over
  UDP instead: a discovery handshake followed by a reliable ordered-datagram layer
  (sequencing, acks, retransmission, heartbeats) beneath the identical BC framing,
  so video, events, battery and controls all work exactly as on the TCP path. The
  default is unchanged TCP; the UDP path runs only when `"udp": true` is set. The
  camera must still be awake and reachable by `address` on the LAN — UDP can't wake
  a sleeping camera (pair with `wake_capture` to catch its self-wakes). UDP
  cameras carry a **UDP BETA badge** in the sidebar and on their settings panel,
  so it's always clear which transport a camera runs on. Validated on real
  hardware (Argus Eco Pro) thanks to Rihan9's patient testing and log captures
  across many beta rounds (issue #39).
- **Studio layout for the timeline (opt-in)**: a Studio toggle in the timeline
  toolbar switches the page to a video-editor arrangement — the camera monitors
  fill the top of the screen and the editing desk (transport controls, camera
  chips and the tracks) docks at the bottom, like Premiere or Resolve. Clicking
  a monitor promotes it to the program monitor with the other cameras in a
  thumbnail rail; clicking it again restores the equal grid. The classic layout
  stays the default and is unchanged. The choice is per user: saved to the
  signed-in account, so it follows you across browsers and devices, with
  localStorage as the signed-out fallback.
- **Frame snapshots on the timeline**: every timeline tile gains a camera
  button — and the `S` key targets the focused monitor (or a lone camera) —
  that saves the frame under the cursor as a PNG named after the camera, date
  and time.
- **Per-page account settings in the API**: `GET/PUT /api/me/settings/{page}`
  stores an independent settings blob per page for the signed-in user (the
  timeline uses `timeline`), so pages never overwrite each other's state. The
  existing `/api/me/settings` blob is untouched.
- **Footage export from the timeline**: an Export button in the timeline toolbar
  downloads a chosen period of one camera's day — up to the full 24 hours
  (`GET /api/recordings/{camera}/{date}/export?from=HH:mm:ss&to=HH:mm:ss`
  `[&format=mp4]`). Two formats, both lossless and re-encode-free:
  - **Single MP4** (the default when possible): the segments' sample tables are
    merged and the media bytes stream-copied into one fast-start file with an
    exact Content-Length (real download progress). The file is trimmed to the
    requested range — it begins at the nearest keyframe at or before the From
    time (at most one GOP early, a few seconds) and ends at To. Coverage gaps
    become hard cuts, like any NVR export. Old fragmented recordings combine
    transparently via the virtual classic index. A range whose stream
    configuration changes mid-way (record stream switched, so resolution/codec
    differ) can't share one container — the dialog says why and offers the zip
    instead.
  - **Zip of segments**: the original files as-is (whole segments, so coverage
    can start a few minutes early), each named by its start time and playable on
    its own, streamed with store-level compression.
  The dialog pre-fills the zoomed-in window, shows size (and footage duration
  for the MP4) before the download starts (`&estimate=1`), and warns above 2 GB.
  The segment still being written is excluded until it closes, and one export
  runs at a time so bulk reads never crowd out the recorders.

- **Delete events in bulk from the Events page**: a Select button turns on
  checkboxes (with select-all); pick any number of events and Delete asks the
  server for an exact summary first — how many, roughly how much disk it frees,
  a per-camera breakdown and the time span — then removes the events and every
  file they own (clip, thumbnail, preview) on confirmation. Events still being
  recorded are kept and called out; empty day folders are pruned afterward.
  Deletion is admin-only once web-UI accounts exist
  (`POST /api/events/delete {ids[], estimate}`; `?estimate` returns the summary
  without deleting).
- **Sidebar cameras can be reordered**: drag a camera card onto another and it
  lands in front of it (an insertion line shows where; a drop zone at the list's
  end moves a camera last). The order is saved with the rest of the view — per
  account when signed in — and it drives every camera list the live view shows:
  the sidebar, the review-strip camera filters and the tile right-click picker.
  Dragging a card onto a tile still shows the camera there, exactly as before;
  newly added cameras append at the end until placed.
- **Timeline zoom is discoverable**: a quiet hint under the lanes — "Scroll or
  pinch here to zoom into the day" — points out the timeline's best trick; it
  disappears the moment you are zoomed in.
- **Crying-sound detection is a first-class event type**: indoor cameras (E1
  series and friends) that listen for crying push a `cry` AI type; it now maps
  to a proper "crying" label — its own icon and color on the Events page,
  review strip and timeline, a chip in the per-camera detection filter, and a
  Home Assistant `Crying` binary sensor (device class `sound`) alongside the
  other detections. Crying records by default when the filter is untouched:
  it is audio-only, so no other detection type would catch the moment (turn
  the detector itself on or off in the Reolink app; previously these pushes
  were kept only as a raw label and the default filter dropped them).
- **The timeline is resizable**: a divider on the boundary between the monitors
  and the tracks — below them in the classic view, above the docked desk in
  Studio — drags to trade height between the two, in both layouts and on touch.
  The tracks share out whatever height the board is given, so pulling it open
  makes the lanes themselves thicker (easier to read, easier to hit) rather than
  adding empty space; the footage blocks and event marks scale with them. A
  board dragged shorter than its tracks scrolls instead of squashing them.
  Double-click the divider for the layout's default height. The size is saved
  per layout (the two want very different ones) and per user — to the account
  when signed in, so it follows you across browsers. In Studio with a focused
  monitor, a second, vertical divider between the program monitor and the
  thumbnail rail drags the same way to trade width between the two (and
  double-clicks back to the default rail width); it is saved and restored with
  the rest.

### Fixed

- **Zooming a paused clip no longer blacks out the video**: digitally zooming
  in the event pop-up (or any zoom surface) while the video was paused could
  show black until the mouse was dragged over it — applying the first zoom
  promotes the video to its own compositor layer, and with no new frames
  arriving the last frame never landed in it. Zoomable surfaces now keep their
  video pre-composited (`will-change: transform`), and the current frame is
  re-presented once on zoom-in as a belt-and-braces repaint.
- **Watching only the sub stream no longer reads as "camera offline"**: the
  camera's online state, Home Assistant availability, camera controls, and the
  motion/status listeners were all bound to the MAIN stream's connection — so a
  viewer watching only the sub stream had live video playing while the sidebar
  showed OFFLINE, HA marked everything Unavailable, and detections went
  unheard. The camera is one device: it now reads online when ANY of its
  sessions is live, commands ride whichever session exists (primary preferred),
  battery/siren/privacy readings come from any session, and every stream
  service listens for detection pushes and may forward them — cameras deliver
  alarm pushes on one OR all of their sessions depending on firmware, so no
  single session can be trusted to hear everything. Cross-session duplicates
  (copies of one event arriving within milliseconds on two connections) are
  suppressed by a short identical-push window, so a doorbell press still rings
  automations exactly once.
- **Wake-capture no longer keeps the battery camera awake**: parking and
  probing immediately was a self-defeating loop — the just-released camera
  (or one whose other stream was still viewed) answered the first probe, was
  treated as "woke itself", and got reconnected 50 ms after "disconnecting so
  the battery camera can sleep"; the probes themselves also reset the
  camera's doze timer. The trigger is now the **sleep→wake edge**: after
  parking there is a probe-free settle window (90 s) so the camera can doze
  off, then sparse probes (30 s) until it is seen asleep once — and only a
  probe answered AFTER that counts as "the camera woke itself" (PIR/motion)
  and connects to catch the event. A viewer opening the stream still connects
  immediately at any point.
- **The sidebar battery icon now shows the level**: it was a fixed empty
  outline no matter the charge; the body now fills in proportion to the
  percentage (a bolt still replaces it while charging).
- **Day/night mode switches stop nagging the log**: cameras announce every
  IR day↔night flip with a `DayNightEventList` push on the motion channel. The
  server treated any non-alarm push as an unmapped event and logged it at INF
  with a "please report if this coincides with a doorbell press" note, so a
  camera that switched to night mode each evening produced a scary-looking line
  that had nothing to do with doorbells. Day/night pushes are now recognized as
  benign housekeeping and kept at Debug; the report note is reserved for pushes
  that are genuinely unmapped.
- **Timeline Studio: focusing the only camera no longer leaves dead space**:
  with a single camera loaded, clicking its monitor used to switch to the
  program-monitor-plus-thumbnail-rail layout anyway — shoving the lone monitor
  to the left with an empty rail column on the right. Focus now needs at least
  two monitors (there is nothing to promote a lone camera against), so a single
  camera just fills the whole area; the multi-camera focus behavior is unchanged.
- **The timeline's "catching up" pill no longer reflows the toolbar**: it used
  to appear and disappear from the layout, shoving the clock and export
  controls onto another line at some window widths and snapping them back
  moments later. Its space is now always reserved; only its visibility changes,
  so the toolbar never jumps.
- **UDP battery cameras no longer drop every ~8 seconds**: the camera runs one
  UDP session per connect-hello it answers — and the handshake retransmits its
  hello (two ports, every 500 ms) until the camera wakes, so the camera can end
  up with duplicate sessions for the same client, all sharing one socket. The
  duplicates never carry traffic, so the camera starves them out and sends
  their `D2C_DISC` death notices to that shared socket — and Neolink.NET
  honored *any* `D2C_DISC`, tearing down its healthy stream on a duplicate's
  paperwork, reliably ~8.5 s in (the duplicate's ~3×3 s retry budget). A
  disconnect now only closes the session it is addressed to (matching `did`);
  a foreign one is logged and ignored, and a duplicate session the camera
  re-offers mid-stream (`D2C_C_R` with another `did`) is released immediately
  with a polite `C2D_DISC` so the camera stops nursing it. Hardening from the
  same investigation rides along: acks at the official client's 10 ms cadence,
  every discovery-layer packet under the handshake's transaction id, the first
  heartbeat sent immediately on connect, and the wake-capture liveness probe
  releasing the session it opens instead of leaving it half-open. The
  disconnect diagnostic now records each control message's session id and
  arrival time, so any remaining close is attributable at a glance.
- **UDP battery cameras: the camera's own keepalive is now ANSWERED** — the
  actual root cause of the stubborn 8-second on / 4-second off cycle that
  survived every transport fix above. The field diagnostics finally showed it:
  the camera periodically asks a BC-layer keepalive question (message 234,
  "UDP keep alive") over the data channel, and expects the client to echo it
  back with response 200. Neolink.NET treated it as an unknown push and
  dropped it, so battery firmware concluded the client was dead and recycled
  the session with a clean `D2C_DISC` after ~8.5 s — media and acks flowing
  the whole time. Every message 234 is now answered immediately; the
  disconnect diagnostic counts keepalives received/answered so the exchange
  is visible in the logs. Field testing then showed a second, longer timer:
  with all keepalives answered the camera still recycled an "idle" client
  after ~1-2 minutes, because we never ASKED it anything after login. The
  official client pings the camera (message 93) every 5 seconds; UDP
  sessions now do the same — the request itself is the "a human is here"
  signal, so firmware that never answers pings is tolerated.
- **Timeline lanes no longer trail behind "now" on some filesystems**: the day
  listing derived every segment's duration from the file's mtime, but an OPEN
  file's directory mtime is stale on NTFS (updated lazily on close) and on
  FUSE/network mounts (attribute caches) — so a recording camera's lane could
  sit minutes behind the clock and look like recording had stopped, then jump
  forward when the segment closed. High-bitrate cameras were hit worst: their
  big segments stay open the longest between rolls. The recorder now reports
  the segment it is writing (file, day, real media seconds) from its own
  memory; the day listing overlays that as `live: true` (appending the file if
  directory enumeration missed it), and the timeline extends a live lane to
  "now" whenever the server says so, with the old mtime heuristic kept as the
  fallback for older servers. Also, a just-opened live segment no longer
  borrows the typical-length guess, which could briefly project its lane into
  the future.

## 0.8.9

### New

- **RTSP audio backchannel (two-way talk for go2rtc / Home Assistant)**: cameras
  with a speaker now expose an ONVIF Profile-T audio backchannel on their existing
  RTSP mount, so go2rtc, Home Assistant's WebRTC Camera and other ONVIF-aware
  clients can talk through the camera without the Neolink.NET web UI. A client that
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
- **Camera controls over the Reolink HTTP API (beta)**: building on the HTTP client
  the spotlight work introduced, the camera panel gains a set of BETA-tagged
  controls, discovered per camera (a camera that rejects a query simply doesn't
  show that section):
  - **Picture**: brightness/contrast/saturation/hue/sharpness sliders (0-255, 128
    neutral), day/night mode, anti-flicker, and flip / mirror — finally a way to
    turn an upside-down camera's image around without the Reolink app.
  - **Speaker volume**: one 0-100 slider that governs sirens, alerts and two-way
    talk loudness. Also exposed to Home Assistant as a number entity.
  - **PTZ presets**: saved positions appear as buttons in the PAN/TILT section
    (click to move there), with a "save current" box that names the camera's
    current position. Home Assistant gets a PTZ-preset select for automations
    ("point at the driveway when the doorbell rings").
  - **Auto-tracking**: an on/off toggle for cameras that follow detected subjects,
    also a Home Assistant switch. Gated on the camera's GetAbility table
    (`supportAITrack`), never on config fields alone — firmwares include an
    `aiTrack` field on models without the feature.
  - **Doorbell quick reply**: pick one of the doorbell's pre-recorded messages and
    play it through its speaker — from the panel or from Home Assistant (a "Play
    quick reply" select on the doorbell's device). The auto-reply (default message)
    remains settable via the REST API (`POST .../autoreply`) but has no UI.
  - **Doorbell light + adjustable IR brightness**: the doorbell-light toggle appears
    only on real doorbells (Support `doorbellVersion`, not just a `doorbellLightState`
    field — some non-doorbells report one anyway); the IR-brightness slider appears
    on cameras whose LED state carries it (e.g. the Elite). Both live in LIGHTS.
  - **Device extras**: Wi-Fi signal and SD-card usage (free/total, mount state) in
    the DEVICE card.
  All device writes are read-modify-write (the camera's own config rides along
  untouched) and stage in the panel until "Apply to camera". A camera whose HTTP
  port is unreachable is backed off for 60s so panel opens stay fast.
  Every one of these controls is also exposed to Home Assistant over MQTT, per
  camera capability: a Spotlight light (on/off + brightness) for white-LED cameras,
  IR-brightness and picture-setting entities (config category), a doorbell-light
  switch and a "play quick reply" select on doorbells, alongside the volume number,
  auto-tracking switch and PTZ-preset select. Entities a camera doesn't qualify for
  are actively cleared from the broker, so nothing stale lingers in HA.
- **Server settings redesigned**: the sidebar gear now opens the full Server
  settings dialog directly (the old inline mini-form is gone). The dialog keeps a
  fixed height — switching tabs no longer resizes it — and gains two tabs: a
  **Users** tab embedding account management (the separate users dialog is
  retired), and a **Connection** tab holding this browser's client-side settings
  — the server API URL and the legacy control username/password — each with a
  plain-language explanation of when (rarely) they're needed. Non-admins and
  open installs see just the Connection tab.
- **Camera panel reorganised**: the camera dialog was getting crowded, so the
  device identity (model, firmware, hardware, serial, Wi-Fi, SD card) is now a
  compact strip pinned under the title — always visible while the rest scrolls —
  and the content splits into two tabs: **Camera settings** (all device controls,
  groupings kept, with an unsaved-changes dot on the tab) and **Recording** (the
  server-side recording settings). The dialog holds a fixed height too, so tab
  switches don't jump.
- **Suspend a camera in Neolink.NET (beta)**: a per-camera switch that stops Neolink.NET
  from connecting to the camera at all — so it can't be viewed or recorded here —
  without editing the config or restarting. It drops Neolink.NET's connection and holds
  it closed; downstream (live view, event and 24/7 recording, HA entities) goes
  quiet naturally, and the setting persists across restarts. The camera itself is
  untouched: its own SD-card / cloud recording and any other system pulling its
  stream directly keep working, so it is not a privacy guarantee — the UI says so.
  Works on Reolink and generic RTSP cameras alike, appears as a "Suspend" switch in
  Home Assistant (with bridge-only availability so it stays usable while the camera
  is intentionally offline), and suppresses the camera-offline email alert while
  suspended. Writing is admin-only once accounts exist (it stops recording, a
  server-side change).
- **Panel feedback as toasts**: the camera panel's action feedback now shows as a
  toast at the panel's bottom edge — green for applied, red for rejected (with the
  camera's own error text) — and dismisses itself after 5 seconds.
- **Camera management in the web UI (beta)**: Server settings gains a Cameras tab
  where admins add, edit and delete cameras — Reolink (address/username/password,
  optional HTTP address and NVR channel) and generic RTSP (rtsp:// URLs) alike —
  without touching config.json by hand. Every field validates live (host/port
  syntax, rtsp:// URLs, duplicate names, characters that would break recording
  paths), and a **Test connection** button does a real check before saving: a full
  Baichuan connect + login for Reolink cameras (reporting the camera's resolution
  on success and its actual error on failure), an RTSP OPTIONS round-trip for
  generic ones. Passwords are write-only: never sent to the browser (a stored one
  shows as a placeholder; blank keeps it), and RTSP URL passwords are masked in
  transit the same way. Saves go through the same validated, atomic config.json
  write as the rest of the settings (a .bak of the previous version is kept), and
  the panel prompts for the restart that applies them.

### Fixed

- **A source gap now closes the open 24/7 segment (correct timeline placement)**:
  when a camera stopped delivering frames with a segment file open — suspended,
  offline, or asleep — the recorder used to keep the file open and glue the
  resumed footage into it. The writer clamps large timestamp jumps, so the whole
  gap got time-compressed into one segment and everything after it played at the
  wrong position on the timeline. The recorder now finalizes the open segment
  after 15 seconds of silence (bounded and playable at its true end time) and
  starts a fresh segment when frames return, leaving a real gap with no footage —
  exactly like a camera that was off. Event recording's pre-roll buffer is
  cleared across such gaps too, so the first event after a resume can't start
  with stale pre-gap frames.
- **Timeline: gaps play as gaps**: the day view used to guess every segment
  file's length from the typical roll interval, so a segment cut short (the
  camera was suspended or went offline mid-segment) claimed lane minutes it
  doesn't have — the cursor was sent past the end of the real media, the player
  could never catch up, and the whole timeline slowed to a crawl ("catching
  up…") over footage that doesn't exist. The segment listing now reports each
  file's actual length (from its close time), lanes size their coverage with it
  — the gap is genuinely blank, with "no footage" on that camera's tile while
  the others play on at full speed — and the player treats a cursor beyond a
  file's media as exhausted rather than lagging, so one short file can never
  hold the other cameras hostage. While viewing today, the lanes also refresh
  themselves once a minute, so recording that starts after the page loaded —
  a resumed camera, most visibly — shows up without a reload; and the live
  edge is capped at "now" instead of projecting a whole segment length ahead,
  which had made cameras with different roll intervals (a high-bitrate camera
  rolls on the size cap every few minutes, others on the timer) look like they
  held unequal footage when they were equal.
- **Recording settings are admin-gated**: the per-camera recording switches
  (retention, schedules, event types, archive routing) persist server-side, but
  any signed-in user could change them. Once accounts exist, the API now requires
  an admin (reads stay open), and the panel shows the section read-only to
  non-admins. Installs without accounts behave as before. Every other
  server-writing endpoint was audited: `/api/admin/*` and `/api/users` were
  already admin-only, and `/api/me/settings` is per-user by design.
- **Privacy mode no longer flaps the camera offline**: on cameras that go fully dark
  in privacy mode (E1 Pro and similar), the absence of video was mistaken for a
  stalled stream, so Neolink.NET reconnected every ~15s — flapping the status between
  online and offline and, worse, marking the camera (and every entity, including the
  privacy switch itself) Unavailable in Home Assistant, which made turning privacy
  back off unreliable. When privacy mode is on, the missing video is now treated as
  expected: the connection is held open, the camera stays online, and control stays
  available. A genuine disconnect (a socket error, not just silence) still reconnects,
  and because some cameras (the E1 Pro) actively *close* the idle connection every
  ~30s while dark instead of just going quiet, a short grace period on the Home
  Assistant availability keeps every entity Available across those brief reconnects —
  including the moment privacy switches back off — rather than flapping the whole
  camera Unavailable and back. The reconnect churn while dark is now announced once
  as a warning, with the per-reconnect chatter dropped to debug, so the log stays
  readable during a long privacy session.
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
