# Battery cameras — setup, sleep, and recording

> **Status: beta — under active development and testing.** Battery support
> works against real hardware, but it lives on top of hard limitations these
> cameras impose: a sleeping camera is off the network and cannot be woken
> remotely, sleep can only be observed indirectly (and some models mimic the
> sleep pattern while idle-awake), the first seconds of an event happen before
> any connection exists, and the UDP transport some models require is itself
> beta. Behavior is validated on a handful of models and still being tuned —
> expect rough edges on models we haven't seen, and please open an issue with
> the `[wake-diag]` log lines when yours misbehaves; that evidence is what
> drives the tuning.

How Neolink.NET runs battery-powered Reolinks (Argus line, Reolink Go, battery
doorbells): what it does on its own, which knobs exist, what ends up recorded
where, and how to read the logs when something looks off.

The one rule everything below follows: **connection time is battery.** A held
connection keeps the camera's main processor and radio up, and that — not
video bitrate, not polling — is what flattens a battery in a day or two.
Neolink.NET therefore treats every second connected to a battery camera as a
cost, spends it only on purpose, and tells you (tile, badges, logs) whenever
something is spending it.

Everything here applies to a **sleep-friendly** camera: one that reports a
battery and does not have `"always_on": true`. Setting `"always_on": true`
opts the camera out of all of it — it is then treated exactly like a wired
camera (permanent connection, no view limits, no wake logic), which is the
right call on solar or USB power.

## Quick start

A battery camera that is reachable over TCP needs nothing special:

```jsonc
{ "name": "Yard", "username": "admin", "password": "…",
  "address": "192.168.1.50" }          // give it a DHCP reservation
```

Battery power is auto-detected; the camera gets a charge badge and defaults to
sleep-friendly mode. UDP-only models (parts of the Argus line never listen on
TCP) additionally need the UID, the UDP transport, and — in Docker — **host
networking** (see [UDP-only models](#udp-only-models) below):

```jsonc
{ "name": "Solar", "username": "admin", "password": "…",
  "uid": "95270000ABCDEFGH", "udp": true }
```

To also catch motion events while nobody is watching, add
`"wake_capture": true`. A typical full battery entry:

```jsonc
{
  "name": "Solar",
  "username": "admin",
  "password": "…",
  "uid": "95270000ABCDEFGH",
  "udp": true,             // UDP-only model; omit for TCP-reachable cameras
  "wake_capture": true,    // catch events while it sleeps (recommended)
  "record": true
}
```

## Settings reference

All per camera, in the config file. Only `uid`/`udp` are required for anything
(UDP-only models); the rest are choices.

| Setting | Default | What it does |
|---|---|---|
| `always_on` | unset (auto) | `true`: hold the camera awake around the clock and treat it exactly like a wired camera — for solar/USB power. `false`: force sleep-friendly mode even if battery detection fails. Unset: battery cameras sleep, wired cameras stay on. Also editable in the web UI camera editor ("Always on"). |
| `wake_capture` | `false` | Watch a sleeping camera (without waking it — see below) and connect the instant it wakes itself for motion, so the event is recorded. No effect with `always_on` or on non-battery cameras. |
| `keep_alive_hours` | `0` (off) | Hold the camera awake for this many hours after startup (0–24) — for stretches when you want it responsive regardless of cost. The tile shows "held awake — spending battery" while it runs; behavior returns to normal when the window ends. Also a slider in the web UI camera editor. |
| `uid` | — | The camera's UID (Reolink app → device info, or the sticker). Required for UDP. |
| `udp` | `false` | Baichuan-over-UDP transport for models that never listen on TCP (beta). Requires `uid`. |
| `udp_probe` | `false` | Diagnostic only: while the camera is unreachable over TCP, probe UDP discovery and log the exchange (UID masked). Useful to find out whether a stubborn camera is a UDP-only model. |
| `record` | `true` | Seed value for the camera's event-recording switch (the web UI switch wins afterwards). |

## How sleep actually works

These cameras have two sleep stages, and knowing them explains most of the log:

- **Light sleep** — the main processor is off, but a low-power *wake chip*
  still answers discovery (slowly, 2–5 s). Connecting to a camera in this
  stage **boots it**: any connection attempt through the wake chip is a wake.
- **Deep sleep** — the wake chip goes quiet too. Discovery gets silence.
  Curiously, plain ICMP pings are still answered (by the Wi-Fi module,
  autonomously, in a power-save rhythm) — without waking anything.

What wakes a camera: its own PIR, the Reolink app, or **any connection
attempt** while the wake chip listens. What cannot wake it: ICMP pings, and
nothing at all in deep sleep except its own PIR.

This is why Neolink.NET's sleep watching is built on **listening, not
knocking**. With `wake_capture` on, after the last viewer leaves:

1. The stream parks and a settle window (~15 s) lets the camera doze
   undisturbed.
2. A ping scan starts (every 3 s). A dozing camera's Wi-Fi module answers in
   a distinctive power-save sawtooth — hundreds of milliseconds, ramping —
   while a woken processor answers flat and fast (single-digit ms). Models
   that switch the radio off entirely simply stop answering; both patterns
   count as "asleep".
3. After enough sleep-pattern samples the camera is declared asleep — the log
   says `armed to connect on its next self-wake`.
4. A run of consecutive **fast** replies is the wake edge: the camera's own
   PIR woke it. Neolink connects (about 1.5 s to first video) and records.
   On networks where ping is filtered, a slow transport probe takes over as a
   fallback — the log notes `ICMP appears blocked here` when that happens.

No step in this sends anything a sleeping camera would react to, so the
watching itself costs no battery.

The rest of Neolink honors the same radio silence: while every stream of a
sleep-friendly camera is parked, background HTTP feature reads, Wi-Fi signal
warms and ONVIF discovery retries send **no packets at all** — that traffic
would keep the camera's radio out of power-save (mimicking the wake pattern)
and could keep the camera itself from dozing off. The UI simply shows the
last known readings until the next natural connection.

## Watching live

A sleep-friendly camera's tile never opens its stream by itself — opening a
stream is what wakes and holds the camera, so it must be your choice:

- The tile shows the last snapshot with an **asleep** badge and a
  **Wake & watch** button. Clicking it wakes the camera (if the wake chip is
  listening) and opens the feed.
- The view is bounded: the tile counts down ("view ends in 1:47 to spare
  battery") and stops itself after ~2 minutes. The checkbox on the tile keeps
  it going if you're actively watching.
- When the view ends, the camera is released after ~10 s of no demand and
  goes back to sleep on its own (Reolink firmware doses off some seconds
  after the last connection closes).

A camera being held awake for any reason says so: the tile chip reads
"held awake — spending battery", and the server logs one `held awake by …`
line per hour naming the reason (viewer, keep-alive, recording, HA stream).

## Catching events while it sleeps (`wake_capture`)

Without it, a motion event while nobody watches is lost to Neolink (the
camera still records to its own SD card). With it, the wake edge above
triggers a connection and recording starts **immediately from the first
keyframe** — no waiting for the camera's detection push, which arrives late
(25 s measured) or never (subject already left frame).

What becomes a *stored event* is governed by the camera's **event-type
selection** (camera settings → Recording), exactly like any detection:

- The self-wake recording starts **tentatively**. Nothing is announced — no
  event on the list, no Home Assistant trigger, no browser alert.
- If a detection the camera's event types allow arrives within the
  confirmation window (~30 s), the event is announced and kept, labeled by
  the detection ("Human detected"), with footage reaching back to the wake
  itself.
- If nothing allowed arrives, the tentative footage is **deleted** — a squirrel
  tripping the PIR does not become a stored video. The log records the
  decision either way.

"Wake" is therefore never an event type: wake events do not appear on the
events list at all. What self-wakes leave behind lives on the **Timeline**
instead — see next section.

Every wake-capture cycle also writes a `[wake-diag]` line at Info classifying
what happened — a real self-wake, something our side caused, or a false
asleep reading — with the probe evidence, the camera's own sleep status on
arrival, and time to first frame. If wake-capture misbehaves, this line is
the first thing to read (and to paste into an issue).

Limitations: the first second or two of an event happens before the
connection is up; on a very busy camera (a wake every few minutes)
wake-capture trends toward `always_on`-like battery use — the diag lines will
show it.

## What is stored where

- **Events list**: only confirmed detections (person, vehicle, animal, … per
  the camera's event-type selection). Never bare wakes.
- **Timeline**: with **Record wake events** on (camera settings → Recording →
  CONTINUOUS section; on by default), whatever the camera streams during
  self-wakes and viewing sessions is taped into ordinary timeline segments.
  This is a *passive tap*: it only writes frames that are already flowing and
  never demands video itself, so it cannot wake the camera, extend a hold, or
  block sleep — zero battery cost by construction. The result is islands of
  footage around each wake, with honest gaps while the camera sleeps. It
  follows the continuous retention and archive settings.
- **24/7 recording is deliberately unavailable** while the camera is allowed
  to sleep: taping around the clock would hold it awake until the battery
  dies. The toggle is disabled with the reason shown; the API and the Home
  Assistant switch refuse it too, and a stale `continuous: true` left in
  settings.json is ignored. Set *Always on* (constant power) to get real
  24/7 recording.
- **SD card**: the camera itself records PIR events to its card while asleep,
  same as with the official app, independent of all of the above.

## Home Assistant

- A dozing camera **stays available** in HA: retained readings (battery,
  switches, last states) remain visible, and a dedicated **Asleep** binary
  sensor says the camera is napping on purpose. Only a genuinely unreachable
  camera goes Unavailable.
- **Polls never wake a sleeping camera.** Battery/state refreshes wait for
  the next natural connection.
- Detection events arrive only when Neolink is connected — i.e. on confirmed
  wake-captures, during views, or with `always_on`/keep-alive. Automations
  fire on *confirmed* events only; tentative self-wakes are invisible to HA.
- Offline alerts skip cameras that are merely dozing.

## UDP-only models

Parts of the Argus line never listen on TCP — they log `Connection refused`
forever on the normal path. For these, set `uid` and `"udp": true`, and in
Docker/Podman use **host networking** (`network_mode: host` / `--network
host`) — this is required, not optional: the Baichuan UDP handshake carries
the client's port *inside* the packet and discovery relies on LAN broadcast,
neither of which survives a bridge network. The tell in the log is a
discovery sweep listing only container-internal broadcast addresses
(`172.x.255.255`) and ending in `UDP: SILENCE`. Full details, including the
compose/run snippets, are in the README's
[UDP-only battery models](../README.md#udp-only-battery-models-beta) section.

UDP cameras wear a **UDP BETA** badge in the sidebar. Once connected, video,
events, battery, recording and controls all work — only the transport
differs.

## Reading the logs

The battery lifecycle narrates itself at Info. The lines worth knowing:

| Log line | Meaning |
|---|---|
| `parked — the recording stream watches for self-wakes…` | Streams released; the camera may sleep. One stream keeps watch (if `wake_capture`). |
| `camera is asleep (ping settled into the power-save pattern) — armed…` | Sleep confirmed by the ping pattern; the wake scan is now at 1 s. |
| `camera answered the transport probe after an all-silent park (ICMP appears blocked here)` | Ping is filtered on this network; the fallback prober caught the wake instead. |
| `self-wake — recording tentatively…` | Wake caught; footage is being captured, nothing announced yet. |
| `event started (person — confirmed self-wake, footage from the wake onward)` | A detection the event types allow confirmed it; the event is now real and announced. |
| `self-wake ended with no matching detection — footage discarded…` | Nothing allowed arrived; the tentative event was deleted. |
| `taping this wake to the timeline (passive — never holds the camera awake)` | The timeline tap is writing a segment from the frames already flowing. |
| `held awake by …` (hourly) | Something is spending battery: names the viewer/recorder/keep-alive holding the camera up. |
| `that self-wake connect caught nothing (N in a row) — being more skeptical…` | Self-healing: connects that yield no detection raise the next park's arming requirement and settle time, so a camera whose idle radio mimics the sleep pattern is left alone until it truly sleeps. A real catch resets. |
| `[wake-diag] REAL self-wake / LIKELY OUR PROBE / SUSPECT FALSE ASLEEP / INCONCLUSIVE` | Post-mortem of each wake with the evidence. Paste this into issues. |

## Timing reference

| What | Value |
|---|---|
| Settle window after parking (no probes) | ~15 s |
| Wake scan cadence | 3 s, steady (a tighter armed cadence proved able to disturb the radio's power-save) |
| "Fast" reply threshold (wake edge) | < 50 ms, 3 in a row |
| Tile view budget | ~2 min (checkbox extends) |
| Idle release after last demand | ~10 s |
| Wake confirmation window (event kept vs discarded) | ~30 s |
| Keep-alive maximum | 24 h |

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| `Connection refused` forever, no open ports on a port scan | UDP-only model: set `uid` + `"udp": true`. |
| Discovery sweep ends in `UDP: SILENCE`, broadcasts all `172.x.…` | Docker bridge network. Use host networking (required for UDP models). |
| Camera never shows "armed", wake-capture catches nothing | Ping may be filtered (look for the fallback line), or the camera never truly sleeps — read the `held awake by` lines to see what's holding it. |
| Repeated "camera woke itself" with `[wake-diag] INCONCLUSIVE` and "it was already up" | The scan misread an idle-awake camera as asleep (its radio power-save pings like the sleep pattern). This self-heals: each fruitless connect makes the next arming stricter until the camera genuinely sleeps — watch for the "being more skeptical" line. |
| Events missed although wake-capture is armed | Check the camera's event-type selection: a wake with no allowed detection is discarded by design. The raw footage is still on the Timeline if *Record wake events* is on. |
| Stream is choppy on every client, including the Reolink app | The camera's radio ceiling, not Neolink (seen on Argus MagiCam: a single 1080p encoder outrunning a ~250 kb/s link). Neolink logs a saturation self-diagnosis naming it. |
| First 1–2 s of an event missing | Inherent: the camera must wake and accept a connection first. |
| Battery drains fast | Something holds it awake: `always_on`, keep-alive, a forgotten open tile, or very frequent wakes. The hourly `held awake by` line and the tile chip name the culprit. |
| Old "Wake detected" entries disappeared from the events list | By design since 0.9.6: wakes belong to the Timeline. The clips are still on disk. |

---

Battery support is validated against real hardware (Argus Solar, Argus Eco
Pro, Argus MagiCam, battery doorbells). If your model behaves differently,
open an issue with the `[wake-diag]` lines and a debug log — that evidence is
exactly what drove every improvement on this page.
