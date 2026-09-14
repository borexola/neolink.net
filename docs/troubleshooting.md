# Troubleshooting

The common failure signatures and what each one means. First move for
anything not listed: run with `--verbose` (or `NEOLINK_LOG=debug`) for
protocol-level logging and read the service log — almost every symptom has a
matching line.

- **`503 Service Unavailable` on DESCRIBE / web tiles stuck on "connecting…"**: the
  camera is not connected yet (wrong address/credentials, camera booting) — check the
  service logs.
- **"authentication failed … retrying in 30s"**: the camera rejected the configured
  username/password. Cameras also reject transiently while rebooting or when their user
  table is full, so the bridge keeps retrying at a slow pace. Five wrong attempts can
  lock the account for a few minutes.
- **"Connection closed while waiting for message ID 1"**: the camera accepted the
  TCP connection and then dropped it without answering the login. First make sure
  you're on the latest build (FullAes support). If it still happens, see
  [Cameras that reset the connection at login](#cameras-that-reset-the-connection-at-login).
- **A camera is missing from the wall and the log says "skipping camera …"**: that
  entry could not be loaded (the message names what is wrong), so it was dropped and
  every other camera started. A skipped camera is not shown in the web UI's camera
  list, so fix or remove it in the config file itself. Under the Home Assistant
  add-on, check the add-on Options too: it merges its cameras into `config.json` and
  only ever adds, so an entry it wrote once stays until it is deleted.
- **Video works but picture settings / volume / scaled snapshots are missing,
  and the log says the HTTP API "REJECTED the login"**: the camera's HTTP API
  is refusing a password the video protocol accepts. Almost always a special
  character (or a password over 31 chars) — set the camera's password to
  letters and digits only in the Reolink app. See the README's
  [Per camera](../README.md#per-camera) section.
- **"the camera's HTTP API is not answering … responds too slowly"**: the HTTP
  port is open but the camera stalled — common for a Wi-Fi camera busy pushing
  video. Reads resume automatically when it recovers; nothing to do unless it
  never recovers (then check the camera's Wi-Fi signal, or set a wired
  `http_address`).
- **A UDP camera (`"udp": true`) times out while every other camera works, and
  the discovery sweep ends in `UDP: SILENCE`**: in Docker, the container is on a
  bridge network — UDP-only cameras need `network_mode: host` / `--network host`
  (see [UDP-only battery models](../README.md#udp-only-battery-models-beta)).
  Read the sweep's `targets:` line to confirm: if the only broadcast address is
  container-internal (`172.x.255.255`) and none is on the camera's subnet, that
  is the cause.
- Cameras limit concurrent Baichuan clients; if the Reolink app is streaming
  `mainStream`, use `stream: "subStream"` or close the app.
- **Choppy browser video on Firefox for main streams**: that's H.265 — use the sub
  stream or a Chromium/Safari browser with hardware HEVC.
- **Configured `clips_path`/`archive_path` but the folders look empty on the
  host (Docker)**: recording never blocks on a missing directory — Neolink.NET
  creates it at startup. If no volume is mapped at that container path, the
  directory is created **inside the container's writable layer**: footage
  records fine but lives in the container (surviving restarts, destroyed by
  `docker rm`) and eats the Docker host's disk. Map a volume for every
  configured tier. The Monitor page's STORAGE section is the tell: tiers on
  the container layer report the same total/free bytes as the root disk.
- **Perimeter events (line crossing / intrusion / loitering) don't appear**:
  they are opt-in — tick them under the camera's ⚙ → *Event types* first
  (they need perimeter protection configured in the Reolink app). If the
  camera still produces none, set `NEOLINK_DEBUG_ALARMS=1`, trip the line
  once, and open an issue with the logged `alarm push <AlarmEventList …>`
  XML lines so the mapping can be extended.

## Cameras that reset the connection at login

A few models — so far all of them cameras sold bundled with an NVR, with port
9000 as their only open port — accept the TCP connection and then reset it a
couple of milliseconds after Neolink sends the first login message, without
ever answering. The log shows the send followed immediately by
`Connection closed while waiting for message ID 1`. Credentials are never
reached, so they are not the cause.

That opening message varies along two axes, and both forms are in use by other
Baichuan clients today. Which one a given firmware accepts is not discoverable
except by trying, so two per-camera options exist to try them:

- `max_encryption` — the encryption tier the message advertises:
  `none`, `bcencrypt`, `aes`, or `fullaes` (the default).
- `legacy_login` — `true` sends the older framing, which carries the MD5
  credential fields in the body instead of leaving it empty.

Work down this list, restarting after each change, until one gets a reply. Each
row is a real combination some Reolink client ships with today:

| `max_encryption` | `legacy_login` | Also used by |
|---|---|---|
| *(unset)* | *(unset)* | the default — what fails if you are reading this |
| `aes` | `true` | the original Rust neolink |
| `bcencrypt` | `true` | older firmware, and Neolink's own protocol test sample |
| `bcencrypt` | `false` | newer clients with encryption capped |
| `none` | `false` | pre-encryption firmware |

```json
{ "name": "front", "address": "192.168.1.201", "username": "admin", "password": "…",
  "max_encryption": "aes", "legacy_login": true }
```

Both options live in the config file — the web UI's camera editor does not offer
them. The camera's ⚙ → **Test** button re-reads the file on every click, so you
can try a row by saving the config and hitting Test, and only restart once a row
answers. When the log stops saying "Connection closed" and starts saying
`encryption negotiated:`, that row is the answer — please report it along with
the camera's model and firmware, so the working combination can become automatic
instead of something you had to find by hand.
