# Recording

The full recording reference: every `"recording": { ... }` option, tiered
storage, capacity monitoring, and footage encryption. For the short version,
see the [README's Recording section](../README.md#recording-recording---).

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
  📦 package, 😢 crying, 👁 motion) — detections of disabled types are discarded
  entirely. Crying is the audio detection indoor cams offer (E1 series and
  friends): the camera hears crying through its mic and pushes it like any
  other smart detection.
  ⚠ The camera does the detecting: person/vehicle/animal labels only arrive when
  the matching Smart Detection is enabled **in the Reolink app** (camera →
  Settings → Detection). The chips are a Neolink.NET-side filter on what arrives;
  the camera's own settings are never changed.
- **Continuous (24/7)**: classic NVR-style recording into rolling
  `segment_minutes`-long MP4 files, browsable under 🕘 → Recordings (grouped by
  day, click to play). Off by default; enable per camera in the UI.

## Options

| Option | Default | Description |
|---|---|---|
| `path` | *required* | Storage directory. In Docker, mount a volume here (e.g. `./recordings:/recordings`) |
| `clips_path` | = `path` | Optional fast tier: new event clips are written here (point it at an SSD for snappy event playback); continuous footage stays on `path` |
| `archive_path` | *unset* | Optional cold tier: enables per-camera archiving — aged footage is **moved** here instead of deleted. Use a different (bigger/slower) drive; in Docker, map a second volume (e.g. `-v /mnt/bigdisk:/archive`) |
| `retention_days` | `7` | Events older than this are deleted (`0` = keep forever) |
| `pre_seconds` | `5` | Video included from before the detection (pre-roll) |
| `post_seconds` | `8` | Quiet time after the last detection before the event closes |
| `max_clip_seconds` | `120` | Hard cap per event; continued activity starts a new event |
| `stream` | `auto` | Stream to record: `auto` (main if served), `mainStream`, `subStream` |
| `segment_minutes` | `10` | Continuous recording: time limit for one segment file |
| `max_segment_size_mb` | `256` | Continuous recording: size limit for one segment file — a new file starts at the next keyframe once the segment reaches this size *or* `segment_minutes`, whichever comes first (keeps high-bitrate streams from producing huge files) |
| `continuous_retention_days` | = `retention_days` | Days to keep continuous footage (`0` = forever) |
| `encrypt` | `false` | Encrypt new footage at rest (AES-256-GCM) — see [Encrypting footage](#encrypting-footage) |

Everything is fragmented MP4 (H.264/H.265 passthrough, video-only) playable in
the browser and by ffmpeg/VLC. Storage layout is plain files, with everything
for one camera-day under a single date folder —
`recordings/<camera>/<date>/detections/<time>-<id>/{event.json, clip.mp4, thumb.jpg, preview.mp4}`
for events and `recordings/<camera>/<date>/continuous/<HH-mm-ss>.mp4` for 24/7
footage — so backups and external tooling are trivial. Recordings from older
versions (events directly under the date folder, continuous under
`<camera>/continuous/<date>`) are migrated to this layout automatically on
startup — directory renames, instant regardless of footage size. Set
`"record": false` on a camera to start with events off (the UI switch can
re-enable it).

## Tiered storage (optional)

Everything works with the single `path` folder — the tiers below are strictly
opt-in and existing setups keep behaving exactly as before:

- **Fast clips tier** (`clips_path`): point it at an SSD and new event clips
  land there for instant review scrubbing, while bulky 24/7 footage stays on
  the big disk.
- **Archive tier** (`archive_path`): once set, each camera's ⚙ → RECORDING
  section gains **Archive event clips** and **Archive continuous footage**
  switches. Retention stays the single clock: when footage reaches the end of
  its retention window, an enabled type is **moved** to the archive instead of
  deleted (e.g. "Keep event clips: 30" moves clips to the archive on day 30).
  One extra knob sets how long the archive keeps footage (blank = forever).
  The events list and the timeline read archived footage transparently. Use a
  different drive for the archive — in Docker, map a second volume
  (e.g. `-v /mnt/bigdisk:/archive` with `"archive_path": "/archive"`;
  `docker-compose.yml` ships commented examples for both tiers). On the Home
  Assistant add-on no extra mapping is needed: point `archive_path` at a
  folder under `/share` or `/media` — NAS shares added in HA under
  Settings → System → Storage appear there automatically.
  While an archive pass moves footage, admins see it live: a
  **background-process strip** in the live view's sidebar (under the storage
  banners) shows what is being archived — camera, day, bytes moved — with a
  progress bar and percentage, and disappears when the pass finishes. The same
  strip is the home for any future long-running server work admins should know
  about (`GET /api/background` serves it; admin-only once accounts exist).

Capacity is watched for you: when any configured location climbs past **90%
used**, the web UI shows an amber warning banner; if one actually runs out of
space, recording to it halts cleanly (no partial files) with a **red** banner
until space is freed — recording resumes automatically. When split storage is
configured, the 📈 Monitor page grows a STORAGE section showing every
location's free space live.

The Monitor also **forecasts when each disk fills**: free space is sampled
every 15 minutes (persisted across restarts, up to a week of trend), and the
DISK FREE card and each storage card show *"fills in ~23 days at the current
rate"*. The projection is the **net** trend, not the raw write rate — so once
retention starts deleting as fast as the cameras record, it honestly says
*"not filling at the current rate"* instead of inventing a fill date. A fresh
install says nothing for the first ~6 hours while it gathers data
(`GET /api/storage` carries the same numbers: `forecastState`/`forecastDays`).

## Encrypting footage

Opt-in encryption at rest for everything the server records: turn on
**Server settings → Recording → Encrypt footage** (or set
`"recording": { "encrypt": true }`) and restart. From then on new event clips,
ambient previews, thumbnails and 24/7 segments are written as chunked
**AES-256-GCM** — a stolen disk, a NAS share mounted elsewhere, or a copied
backup exposes nothing, and any in-place tampering is detected on read.

- **Playback is unaffected.** Files decrypt transparently when served: live
  timeline scrubbing, seeking (HTTP range requests decrypt only the chunks a
  seek touches), event previews and downloads/exports all behave exactly as
  before. AES-GCM is hardware-accelerated, so the cost is a fraction of a
  percent of one core even at main-stream bitrates.
- **Old footage keeps playing.** The format is sniffed per file: plaintext
  recordings from before the switch (or after turning it back off) are served
  raw forever, side by side with encrypted ones. Nothing is re-encrypted or
  migrated. Footage recorded while encryption was ON also keeps playing after
  turning it OFF — the key stays available either way.
- **The key** is derived from the server secret: the `NEOLINK_SECRET_KEY`
  environment variable (32 bytes, base64/hex — the recommended way: the key
  then never touches the footage disk) or the auto-generated `secret.key` in
  the state dir. **Back it up** — without it, encrypted footage is
  unrecoverable, by design. If `secret.key` lives on the same disk as the
  recordings, a thief gets both: prefer the env var, or keep the state dir on
  the OS disk.
- **What it does not do:** event metadata (`event.json` — labels and
  timestamps, no imagery) stays plaintext so the index stays cheap; exports
  are decrypted downloads by definition; and like any at-rest encryption it
  cannot protect against an attacker with full access to the *running* host.
  If you control the machine, full-disk encryption (LUKS, BitLocker, ZFS)
  gives the same guarantee for the whole system — this feature is for setups
  where you can't control the volume (e.g. the HA add-on on an existing disk).

> ⚠ **Docker: map a volume for every configured tier.** Missing directories
> are created at startup so recording never blocks — but if a configured
> container path has no volume behind it, that directory lands in the
> container's writable layer: footage records fine yet lives inside the
> container (gone on `docker rm`) and fills the Docker host's disk. If the
> Monitor's STORAGE section shows a tier with the same capacity as the root
> disk, that's the sign.
