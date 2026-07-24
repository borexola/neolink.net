# AI event descriptions (EXPERIMENTAL)

> Point Neolink.NET at a vision-capable LLM and every detection event gets a
> short written description ("A person in a red jacket walks up the driveway
> carrying a box.") and a **threat classification** — GREEN, YELLOW or RED —
> in the web UI, the event metadata, and Home Assistant.
>
> **Experimental — and your feedback is very welcome.** It works end-to-end
> and is safe to leave on (a slow or dead model can never affect recording or
> streaming), but the prompts, the models people run, and the UI around it are
> all still evolving. If you try it, tell us what worked, what didn't, and what
> your setup was — that is what shapes where this goes next. Tested so far with
> **llama.cpp**, **Ollama** and **LM Studio**; Anthropic-style APIs (Claude, or
> any proxy speaking the Messages shape) are implemented to spec.
>
> The only hard requirement on the model is that it be **vision-capable** (it
> must accept images). Any such model your backend can run will work.

## How it works

1. **A detection fires** (person, vehicle, animal, … — the camera's own
   detection, as always). The event records exactly as before.
2. **While the event records**, Neolink samples low-resolution frames using
   the camera's *own JPEG snapshot command* on the sub stream — no video
   decoding, no ffmpeg, no GPU use on the server. Sampling starts at one
   frame per second; when the frame budget fills, the set is thinned and the
   interval doubles, so the kept frames always **span the whole event** — a
   subject that appears 30 seconds in is part of the story, not cropped out.
3. **When the event closes**, the frames (each stamped with its real time
   offset) go to your configured model with the instruction prompt. Jobs are
   processed **one at a time on a bounded background queue**: the event
   pipeline never waits for the model, and if the model can't keep up, extra
   events are skipped with a log line — never delayed.
4. **The answer lands everywhere**: the description and threat level are
   written into the event's metadata (`event.json`, `/api/events`), shown in
   the web UI (a banner in the event players, colored dots on event rows and
   review-strip cards), published to Home Assistant (per-camera **Last AI
   description** and **AI threat level** sensors, retained), and logged.
   While the model is still working, the event player says so
   ("AI is describing this event…") instead of showing nothing.

Battery-friendly by design: tentative self-wake recordings are never sampled —
frames only flow once a real detection confirms the event.

### Threat classification

The model is required to start its answer with one word — **GREEN** (routine:
a delivery, a known pattern, nothing out of place), **YELLOW** (suspicious:
someone lingering, checking car handles, a face deliberately concealed) or
**RED** (danger: a visible weapon, fighting, a break-in attempt, fire). The
classification contract is appended to your prompt automatically — editing
the instructions cannot break it. The level drives the colored dot/banner in
the UI and the `ai_threat` sensor in Home Assistant, which is the natural
automation hook ("notify loudly on RED").

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
     and tells you exactly what answered.
2. **Per camera** — camera ⚙ → EVENTS → **AI descriptions**. The toggle only
   exists while the global switch is on, and only cameras you opt in send
   frames anywhere.

Requires event recording (a `recording` section in the config) — descriptions
attach to recorded events.

## Getting good descriptions

**More frames = a better story.** The model can only describe what it sees:
with 3 frames it sees three disconnected moments, with 10–15 it sees a
sequence — direction of movement, what changed, what the subject did. The
**Frames per event** setting (Settings → AI, capped at 20) is the budget that
gets spread across the event. Raise it if descriptions feel like guesses;
the cost is a bigger payload and a slower answer per event (each frame is
extra tokens through the model), so find the balance your hardware sustains.

**Fine-tune the instructions — please.** The default prompt is a generic
security narrator; the field in Settings → AI is *yours*, and tailoring it to
your property is the single biggest quality lever. Tell the model what the
scene is, what is normal, and what you care about:

> You are watching the front driveway of a family home. A silver station
> wagon parked on the left is ours. Deliveries are normal before 18:00.
> Describe people by clothing and describe what they actually do. Anyone
> touching car doors or windows is suspicious.

Short, concrete instructions beat long abstract ones. The threat-level
contract is appended after your prompt automatically, so you never need to
mention GREEN/YELLOW/RED yourself (but you *can* sharpen what counts as
suspicious for your scene, as above).

**Model choice and speed.** A small vision model answers in a few seconds on
a modest GPU; reasoning-heavy models can take 10× longer for little gain on
this task. If your model supports it, the **Skip model "thinking"** switch
asks it not to reason step-by-step (`<think>` blocks are stripped from
answers either way). The **timeout** setting caps how long Neolink waits per
event.

**A known-good setup.** A ~12B-parameter vision model gave excellent
descriptions in testing — run through Ollama, LM Studio and llama.cpp alike —
on an **RTX 5080**, with inference landing in **3–5 seconds even at the full
20-frame budget**. That is a useful reference point: a mid-sized vision model
on a current desktop GPU comfortably describes an event before the next one is
likely to start, so you can turn the frame budget all the way up without the
queue backing up. (Vision models move fast — pick a current one your backend
runs well rather than any specific name here.)

**Privacy.** Frames go wherever the endpoint points — with LM Studio, Ollama
or llama.cpp on your own hardware nothing leaves your network; with a hosted
API, event frames are sent to that provider. Choose accordingly.

## What it costs

- One snapshot per second (sub-stream JPEG, tens of KB) from the camera while
  an event records — negligible next to the event's video itself.
- One model call per event, serialized. The queue is bounded (8): a burst of
  events beyond it logs "queue is full — event skipped" rather than piling up.
- Nothing at all for cameras with the toggle off, and nothing while the
  global switch is off.
