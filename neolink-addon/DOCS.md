# Neolink.NET

Bridges Reolink cameras that speak the Baichuan protocol (port 9000) into
RTSP streams, a full web UI with recording and review, and native Home
Assistant entities over MQTT.

## Quick start

1. **Add your camera(s)** in the *Configuration* tab:

   ```yaml
   cameras:
     - name: Driveway
       address: 192.168.1.100
       username: admin
       password: my-camera-password
   ```

   Give each camera a DHCP reservation in your router — the add-on connects
   by IP. `always_on: true` keeps a battery camera streaming around the clock
   (drains the battery; pair with solar/USB power).

2. **Start the add-on**, then click **OPEN WEB UI**. The first visit asks you
   to create the admin account; after that you have the camera wall, event
   review strip and timeline.

3. **Home Assistant entities** (optional but recommended): install the
   **Mosquitto broker** add-on and the **MQTT** integration if you don't have
   them yet. Neolink wires itself to the broker automatically at every start —
   no addresses, no credentials to copy. Each camera appears as a device with
   motion/person/vehicle/animal (and smart detection) sensors, doorbell press
   events, battery, an on-demand **Record** switch, a **Recording** status
   sensor, and more, depending on what the camera supports.

That's the whole setup.

## Recordings

Detection events (with pre-roll) are recorded to `/media/neolink`, so clips
show up in Home Assistant's **media browser** (Media → My media → neolink) as
well as in Neolink's own review strip and timeline. Retention, per-camera
switches, event-type filters and capture schedules are managed in the web UI
(camera ⚙ → RECORDING).

### Tiered storage & archiving (BETA)

Two optional extra locations can be added to the `recording` section of
`config.json` (reachable via the Samba/SSH add-ons — plain JSON edits keep
the add-on's config merging working):

- `"clips_path"` — a fast tier where new event clips are written (24/7
  footage stays on the main path).
- `"archive_path"` — a cold tier that unlocks per-camera **Archive** switches
  in the web UI (camera ⚙ → RECORDING): footage whose retention expires is
  *moved* there instead of deleted, and stays playable in Events and the
  timeline.

Point either at a NAS share added under **Settings → System → Storage**
(`/media/<name>` or `/share/<name>`) and restart the add-on. The Monitor
page shows every configured location's free space, and the web UI warns at
90% used / halts recording cleanly when a disk is actually full.

### Recording to a NAS

Add the share in Home Assistant first: **Settings → System → Storage → Add
network storage** (SMB or NFS). A share added with usage **Media** appears at
`/media/<share-name>`, one added as **Share** at `/share/<share-name>` — both
are visible to this add-on. Then point the recording path at it in Neolink's
web UI (⚙ server settings → RECORDING → storage path), e.g.
`/media/nvr/neolink`, and restart the add-on. Retention management follows
the path — only Neolink's own folders there are ever cleaned up. The path is
written to `config.json` on first start only, so your choice is never
overwritten by add-on restarts or updates.

## RTSP for Frigate, VLC & friends

Every camera is restreamed at:

```
rtsp://<home-assistant-ip>:8654/<camera-name>            (main stream)
rtsp://<home-assistant-ip>:8654/<camera-name>/subStream  (sub stream)
```

## How configuration is layered

The add-on generates and maintains `config.json` in the add-on's config
folder (`/addon_configs/…_neolink/`, reachable via the Samba/SSH add-ons).
The rules, designed so nothing you set ever gets lost:

- The **camera list in the add-on options owns `cameras`** whenever it is
  non-empty. Leave it empty to manage cameras by hand in `config.json`.
- With **Automatic MQTT setup** on, only the broker *connection* fields are
  refreshed at start — `base_topic`, `stats_interval` and everything else you
  set in the web UI survive. Turn it off to point at your own broker.
- Every other setting (ports, recording, users, UI) belongs to the web UI's
  server settings and `config.json` — the add-on never touches them.
- If you hand-edit `config.json` with `//` comments, the add-on stops merging
  entirely and runs the file exactly as you wrote it.

## Troubleshooting

- **"no MQTT broker service found" in the log** — install the Mosquitto
  broker add-on (Settings → Add-ons), then restart this add-on.
- **Camera shows offline** — verify the IP, and that the camera works in the
  Reolink app from the same network. Battery cameras sleep: they are
  unreachable until PIR motion or the app wakes them (see the project README's
  battery section).
- **Port conflict on 8654/8655** — change the host ports in the add-on's
  Network section; the web UI keeps working through **OPEN WEB UI**.
- **A UDP-only battery camera (`"udp": true`) never connects** — these need
  host networking, which the add-on does not use, so they are not supported
  under the add-on today. Run the Docker image with `network_mode: host`
  instead. See the project README's *UDP-only battery models*.

Project docs, issues and source: https://github.com/borexola/neolink.net
