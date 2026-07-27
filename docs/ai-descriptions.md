# AI event descriptions (BETA)

> Point Neolink.NET at a vision-capable LLM and every detection event gets a
> short written description ("A person in a red jacket walks up the driveway
> carrying a box.") and a **threat classification** — GREEN, YELLOW or RED —
> in the web UI, the event metadata, and Home Assistant.
>
> **Beta — and your feedback is very welcome.** It works end-to-end and is
> safe to leave on (a slow or dead model can never affect recording or
> streaming). Prompts and the models people run keep improving — if you try
> it, tell us what worked, what didn't, and what your setup was; that is what
> shapes where this goes next. Tested with **llama.cpp**, **Ollama** and
> **LM Studio**; Anthropic-style APIs (Claude, or any proxy speaking the
> Messages shape) are implemented to spec.
>
> The only hard requirement on the model is that it be **vision-capable** (it
> must accept images). Any such model your backend can run will work.

## How it works

1. **A detection fires** (person, vehicle, animal, … — the camera's own
   detection, as always). The event records exactly as before.

2. **Frames are gathered while the event records.** How depends on whether
   the server has an **ffmpeg** (the Docker image ships one; see
   [ffmpeg](#ffmpeg) below):

   - **With ffmpeg — the stream itself is the source.** Neolink passively
     keeps keyframes from the video that is already flowing for the
     recording. This costs the camera *nothing* — no snapshot commands, no
     extra network traffic, no battery drain — and the frames carry exact
     timestamps. Keyframes arrive at the camera's own cadence (typically one
     every 2–4 seconds), which is the natural sampling floor.
   - **With ffmpeg — the moments *before* the trigger join the set.** The
     recorder's pre-roll buffer (the seconds that make it into the clip
     before the detection reached the server) is decoded into up to three
     leading frames. This is often the best look at whatever caused the
     event — a passing car, a courier already turning away — and the model
     is told these frames are from *before* the trigger.
   - **Without ffmpeg** frames come from the camera's own JPEG snapshot
     command (sub stream, one per second), as earlier versions did. Pre-roll
     frames are unavailable, and cameras whose snapshot command ignores the
     size request send full-resolution images (see payload discipline below).

   Two settings shape the sampling either way (Settings → AI → BEHAVIOR):
   **One frame every N seconds** sets the density — honestly labeled "as the
   source allows", since stream sampling cannot outrun the keyframe cadence —
   and **Max frames per event** (default 30) is the ceiling: when a long
   event would exceed it, the kept frames thin out so they always **span the
   whole event**, start to finish. The event's **first five seconds are
   always kept at full source density** and are never thinned — the subject
   that triggered the event is usually in them.

3. **When the event closes**, the frames go to your configured model with the
   instruction prompt — each image introduced by its own inline label ("Frame
   3 of 12 — +4s into the event:", or "3s BEFORE the trigger (pre-roll):") so
   its timing cannot detach from the picture, plus grounding rules that
   forbid narrating anything the frames don't show (a subject that leaves the
   view "left the view" — the model may not invent a return) and tell the
   model to trust the camera's own burned-in timestamp when one is visible.
   Payload discipline: frames larger than a sub-stream thumbnail are
   **downscaled to model size through ffmpeg** before sending, and requests
   are split into ordered parts when they would exceed 100 frames or ~24 MB —
   each part told what the earlier parts saw, the final threat level being
   the most severe any part reported. Jobs are processed **one at a time on a
   bounded background queue**: the event pipeline never waits for the model,
   and if the model can't keep up, extra events are skipped with a log line —
   never delayed.

4. **The answer lands everywhere**: the description and threat level are
   written into the event's metadata (`event.json`, `/api/events`), shown in
   the web UI (a banner in the event players, colored dots on event rows and
   review-strip cards), published to Home Assistant (per-camera **Last AI
   description** and **AI threat level** sensors, retained), and logged.
   While the model is still working, the event player says so
   ("AI is describing this event…") instead of showing nothing.

Battery-friendly by design: the stream tap only reads frames that are already
flowing, and tentative self-wake recordings are never sampled — frames only
flow once a real detection confirms the event.

**How to see which pipeline you're on:** the startup log states it in one
line ("AI describe: ffmpeg at /usr/local/bin/ffmpeg — stream frame sampling,
pre-trigger frames and downscaling active", or the "no ffmpeg found"
variant), the Settings → AI page shows the same status, and each event's
"describing event" log line says `stream-tap` when the stream was the source.

### Threat classification

The model is required to start its answer with one word — **GREEN** (routine:
a delivery, a known pattern, nothing out of place), **YELLOW** (suspicious:
someone lingering, checking car handles, a face deliberately concealed) or
**RED** (danger: a visible weapon, fighting, a break-in attempt, fire). The
classification contract is appended to your prompt automatically — editing
the instructions cannot break it. The level drives the colored dot/banner in
the UI and the `ai_threat` sensor in Home Assistant, which is the natural
automation hook ("notify loudly on RED").

Threat calls are context calls — a person at a window is routine on a street
camera and alarming on a backyard camera. That context is what the per-camera
**scene notes** are for (below).

## Enabling it

Two switches, both required:

1. **Globally** — Settings → **AI** (admin):
   - **Backend**: *OpenAI-style* (LM Studio, llama.cpp server, hosted APIs),
     *Ollama* (native API), or *Anthropic-style* (Claude / Messages-API
     proxies). Each backend keeps its own endpoint, model and API key, so
     switching between them loses nothing.
   - **Endpoint**: e.g. `http://127.0.0.1:1234` for LM Studio,
     `http://127.0.0.1:11434` for Ollama. The API path (`/v1/chat/completions`,
     `/api/chat`, `/v1/messages`) is appended automatically. For the Anthropic
     backend a blank endpoint means `https://api.anthropic.com`.
   - **Model**: the name of a **vision-capable** model your backend can run.
     Required for Ollama (it has no loaded-model default) and Anthropic;
     optional for OpenAI-style servers that answer with their loaded model.
   - **API key**: stored encrypted, write-only — it is never sent back to the
     browser. Local servers usually need none.
   - Use **Test LLM connection** before saving — it round-trips a real request
     **with a small test image attached**, so a model that cannot accept
     images fails right here (and the error says exactly that) instead of on
     your first real event.
2. **Per camera** — camera ⚙ → EVENTS → **AI descriptions**. The toggle only
   exists while the global switch is on, and only cameras you opt in send
   frames anywhere. With the toggle on, a **scene notes** field appears —
   see below; it is the single best lever for accurate threat levels.

Requires event recording (a `recording` section in the config) — descriptions
attach to recorded events.

## Getting good descriptions

**Scene notes — per camera, the biggest threat-level win.** The model sees
frames, but it doesn't know your property. The scene notes field (under the
per-camera AI toggle) is where you tell it, in a sentence or two, what this
camera watches and what is *normal there*:

> Faces the street — passing cars and pedestrians are routine. The white SUV
> in the driveway is ours. Nobody should enter through the side gate.

The notes ride every request as context (framed so they can never be mistaken
for a description of the frames), and they are what turns "a person stands at
the window — GREEN" into the YELLOW it should be on an indoor camera.

**The two sampling knobs.** *One frame every N seconds* is detail: how often
the story is sampled (bounded below by the camera's keyframe cadence when the
stream is the source). *Max frames per event* is cost: the ceiling per event,
with long events spreading their frames end to end rather than cutting off.
More frames = a better story, paid for in tokens and answer latency — with a
local model the price is seconds, with a hosted API it is money. The default
(every 2s, max 30) covers a ~2-minute event at full keyframe density.

**Review what the model saw.** Turn on **Keep sent frames on disk** and the
exact JPEGs sent with each event are stored in the event's folder
(`ai-frames/`, one file per frame with its time offset in the name), right
next to the clip. They are plain, unencrypted files *by design* — they exist
to be opened — even when footage encryption is on, and they are deleted
together with the event. When a description surprises you, this folder is
the answer to "what did it actually see?".

**Fine-tune the instructions — please.** The default prompt is a generic
security narrator; the instruction field in Settings → AI is *yours*, and it
sets the global voice: length, focus, language. (Property-specific facts
belong in each camera's scene notes instead.) Short, concrete instructions
beat long abstract ones. The threat-level contract is appended after your
prompt automatically, so you never need to mention GREEN/YELLOW/RED yourself.

**Model choice and speed.** A small vision model answers in a few seconds on
a modest GPU; reasoning-heavy models can take 10× longer for little gain on
this task. If your model supports it, the **Skip model "thinking"** switch
asks it not to reason step-by-step (`<think>` blocks are stripped from
answers either way). The **timeout** setting caps how long Neolink waits per
event.

**A known-good setup.** A ~12B-parameter vision model gave excellent
descriptions in testing — run through Ollama, LM Studio and llama.cpp alike —
on an **RTX 5080**, with inference landing in **3–5 seconds even at a
20-frame budget**. That is a useful reference point: a mid-sized vision model
on a current desktop GPU comfortably describes an event before the next one is
likely to start, so you can turn the frame cap all the way up without the
queue backing up. (Vision models move fast — pick a current one your backend
runs well rather than any specific name here.)

**Privacy.** Frames go wherever the endpoint points — with LM Studio, Ollama
or llama.cpp on your own hardware nothing leaves your network; with a hosted
API, event frames are sent to that provider. All frame extraction and
downscaling happens locally either way. Choose accordingly.

## ffmpeg

ffmpeg unlocks the three best parts of this feature: **stream sampling**
(frames from the recording itself, zero camera cost), **pre-trigger frames**
(the moments before the detection), and **downscaling** (cameras that answer
snapshots with multi-megabyte images get them shrunk to model size instead of
ballooning the request).

- **Docker / Home Assistant add-on**: nothing to do — the image ships a
  static ffmpeg (single ~40 MB binary), and everything above is active out of
  the box.
- **Bare metal**: install ffmpeg so it's on `PATH`, or point the
  `NEOLINK_FFMPEG` environment variable at a binary. The startup log confirms
  what was found.
- **Without it**, descriptions still work exactly as they did before ffmpeg
  existed here: snapshot-command sampling, no pre-roll, oversized payloads
  split into byte-capped parts. It is an enhancement, never a requirement.

## What it costs

- **With ffmpeg**: effectively nothing per frame — the stream tap reads video
  that is already flowing for the recording, and the decode work (a handful
  of keyframes per event) runs on the describe worker, never on the event
  path.
- **Without ffmpeg**: one snapshot per second (sub-stream JPEG) from the
  camera while an event records — negligible next to the event's video
  itself.
- One model call per event, serialized. The queue is bounded (8): a burst of
  events beyond it logs "queue is full — event skipped" rather than piling up.
- Nothing at all for cameras with the toggle off, and nothing while the
  global switch is off.
