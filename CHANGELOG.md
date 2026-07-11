# Changelog

Release notes for Neolink.NET. Releasing works by tagging `vX.Y.Z` — the docker
workflow bakes the tag into the app as its version (see "Versioning & releases"
in the README). Paste the matching section below into the GitHub release.

## 0.8.3 — unreleased

### New

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

### Fixed

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
