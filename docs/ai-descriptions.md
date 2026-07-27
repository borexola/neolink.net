# AI event descriptions (BETA)

> Point Neolink.NET at a vision-capable LLM and every detection event gets a
> short written description ("A person in a red jacket walks up the driveway
> carrying a box.") and a **threat classification** — GREEN, YELLOW or RED —
> in the web UI, the event metadata, and Home Assistant.
>
> **Beta — feedback is very welcome.** Safe to leave on: a slow or dead model
> can never affect recording or streaming. Tested with **llama.cpp**,
> **Ollama** and **LM Studio**; Anthropic-style APIs (Claude, or any proxy
> speaking the Messages shape) are implemented to spec. The only hard
> requirement on the model: it must be **vision-capable** (accept images).

## How it works

1. **A detection fires** — the camera's own detection, and the event records
   exactly as before.
2. **Frames are gathered while the event records.** With **ffmpeg** present
   (the Docker image ships one), frames are sampled from the stream that is
   already flowing for the recording — a passive keyframe tap that costs the
   camera *nothing* — and up to three **pre-roll frames from the moments
   before the trigger** join the set, usually the best look at whatever
   caused the event. Without ffmpeg, the camera's own JPEG snapshot command
   carries the event, as earlier versions did.
   Two settings shape the sampling (Settings → AI): **one frame every N
   seconds** (density — bounded by the camera's keyframe cadence, typically
   one per 2–4s, when the stream is the source) and **max frames per event**
   (default 30 — long events spread their kept frames end to end rather than
   cutting off). The first five seconds are always kept at full density.
3. **When the event closes**, the frames go to the model, each introduced by
   its own time label ("Frame 3 of 12 — +4s into the event:", or "3s BEFORE
   the trigger (pre-roll):"), with grounding rules that forbid narrating
   anything the frames don't show. Oversized frames are downscaled through
   ffmpeg; very large sets split into ordered parts. Jobs run one at a time
   on a bounded background queue — the event pipeline never waits, and if
   the model can't keep up, extra events are skipped with a log line.
4. **The answer lands everywhere**: event metadata (`/api/events`), the web
   UI (banner in the players, colored dots on rows and strip cards), Home
   Assistant (per-camera **Last AI description** and **AI threat level**
   sensors), and the log.

Battery-friendly: the stream tap only reads frames already flowing, and
tentative self-wake recordings are never sampled.

**Which pipeline am I on?** The startup log says it in one line ("AI
describe: ffmpeg at … — stream frame sampling, pre-trigger frames and
downscaling active", or the "no ffmpeg found" variant); each event's
"describing event" line says `stream-tap` when the stream was the source.

## Threat classification

The model must start its answer with **GREEN** (routine), **YELLOW**
(suspicious — lingering, checking car handles, a concealed face) or **RED**
(danger — a weapon, fighting, a break-in, fire). The contract is appended to
your prompt automatically, so editing the instructions can't break it. The
level drives the colored dot/banner and the `ai_threat` HA sensor — the
natural automation hook ("notify loudly on RED").

## Enabling it

Two switches, both required:

1. **Globally** — Settings → **AI** (admin): pick the backend (OpenAI-style,
   Ollama, or Anthropic-style — each keeps its own endpoint/model/key),
   set the endpoint (API paths are appended automatically; blank Anthropic
   endpoint = `https://api.anthropic.com`) and a vision-capable model name.
   API keys are stored encrypted, write-only. Use **Test LLM connection** —
   it sends a real request *with a test image*, so a text-only model fails
   here instead of on your first event.
2. **Per camera** — camera ⚙ → EVENTS → **AI descriptions**. Only opted-in
   cameras send frames anywhere. The toggle reveals the **scene notes**
   field — see below.

Requires event recording (a `recording` section in the config).

## Getting good descriptions

- **Scene notes (per camera) — the biggest threat-level win.** Tell the
  model what this camera watches and what is normal there: *"Faces the
  street — passing cars are routine. The white SUV is ours. Nobody should
  enter through the side gate."* Threat calls are context calls; this is the
  context.
- **The two sampling knobs.** Density is detail; the cap is cost. More
  frames = a better story, paid in tokens and latency — seconds on a local
  model, money on a hosted API. The defaults (every 2s, max 30) cover a
  ~2-minute event at full keyframe density.
- **Keep sent frames on disk** stores the exact JPEGs sent with each event
  (`ai-frames/` in the event folder — plain files by design, deleted with
  the event). When a description surprises you, this answers "what did it
  actually see?".
- **The instruction prompt** sets the global voice — length, focus,
  language. Property-specific facts belong in scene notes instead.
- **Model choice.** A small vision model answers in seconds; reasoning-heavy
  models take 10× longer for little gain — **Skip model "thinking"** asks
  them not to. Reference point: a ~12B vision model on an RTX 5080 answers
  in 3–5s at a 20-frame budget, comfortably faster than events arrive.
- **Privacy.** Frames go wherever the endpoint points — local backends keep
  everything on your network; hosted APIs receive event frames. All frame
  extraction and downscaling happens locally either way.

## ffmpeg

ffmpeg unlocks stream sampling, pre-trigger frames and downscaling.
**Docker / HA add-on**: built in, nothing to do. **Bare metal**: install it
on `PATH` or point `NEOLINK_FFMPEG` at a binary; the startup log confirms
what was found. **Without it** everything still works the old way — snapshot
sampling, no pre-roll, oversized payloads split into parts. An enhancement,
never a requirement.

## What it costs

With ffmpeg: effectively nothing per frame — the tap reads video already
flowing, and decoding runs on the describe worker, never the event path.
Without: one sub-stream snapshot per second during events. Either way, one
model call per event on a bounded queue (8) — a burst beyond it skips events
with a log line rather than piling up. Cameras with the toggle off cost
nothing at all.
