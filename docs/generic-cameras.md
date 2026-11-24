# Non-Reolink cameras (generic RTSP)

Any camera that serves RTSP can be added alongside the Reolink ones. Instead of
`address`/`username`, give it the stream URLs, with credentials inside the URL:

```json
{
  "name": "front_gate",
  "rtsp_main": "rtsp://admin:pass@192.168.1.50:554/Streaming/Channels/101",
  "rtsp_sub":  "rtsp://admin:pass@192.168.1.50:554/Streaming/Channels/102"
}
```

Neolink pulls those streams and re-serves them on its own RTSP port, records
them, and shows them in the web UI.

**Detections come over ONVIF's event service.** Neolink subscribes to the
camera's own analytics (motion, and person/vehicle/animal where the camera
classifies what it saw), so a non-Reolink camera gets the same treatment as a
Reolink one: event clips with thumbnails, the events page and timeline,
e-mail/webhook notifications, AI descriptions, and motion sensors in Home
Assistant. A camera whose ONVIF has no event service (or none reachable) still
streams and records around the clock; it simply produces no events, and the
switches that depend on them stay out of the way.

**Settings come over ONVIF**, the standard nearly every IP camera speaks. Turn
it on in the camera's own web page and the ⚙ panel fills in with whatever its
ONVIF services actually answer for:

| Section | ONVIF service |
|---|---|
| Model / firmware / serial on the identity strip | Device |
| **Streams**: resolution, framerate, bitrate per profile, editable | Media or Media2 (video encoder configuration) |
| **Picture**: brightness, contrast, saturation, sharpness, day/night | Imaging |
| **Pan / tilt** and saved **presets** | PTZ (only when the head actually pans and tilts) |
| **Zoom**: the lens's optical zoom, as a slider | PTZ (`GetStatus` / `AbsoluteMove`) |
| **On-screen display**: where the name and timestamp sit | Media or Media2 (OSD) |
| **Detections**: motion and, where the camera classifies, person/vehicle/animal | Events (pull-point) |
| **Reboot** | Device |
| Stills, for thumbnails and posters | Media (`GetSnapshotUri`) |
| **Detection zone**: the camera's own motion grid | Analytics (cell motion detector); Neolink keeps it otherwise, see [Detection zones](configuration.md#detection-zones) |

A camera that answers none of this still streams and records exactly as
before; the panel simply shows less. What ONVIF cannot do is left out rather
than offered and refused: there is no way to switch an overlay off (only to
move it), and hue, anti-flicker, flip/mirror and HDR have no ONVIF equivalent.
Focus is not offered either: ONVIF moves it through the imaging service in ways
too different from one camera to the next to present as a single slider.

Newer cameras speak **Media2**, the successor to ONVIF's media service, and some
speak only that. Neolink asks the older one first (every Reolink answers it)
and moves to Media2 only when it gets nothing, so the streams, stills and
overlays come through either way.

Two interoperability details are handled for you, because between them they
account for most "ONVIF is enabled but Neolink shows nothing" reports: requests
are stamped in **the camera's own clock** (read from it first, unauthenticated;
a camera drifting from your server by more than a few seconds rejects every
request otherwise, which looks exactly like a wrong password), and the camera's
login is offered over **HTTP authentication as well as WS-Security**, since
which of the two a given firmware insists on is not something you can tell from
the outside.

ONVIF is looked for on the stream URL's own host, port 80 first, then 8000,
2020 (TP-Link Tapo) and 8899, and signs in with the login that URL carries.
`onvif_address` overrides both: give it `host`, `host:port`, or a full URL, and a
full URL may carry its own `user:pass@` when the camera keeps separate accounts
for streaming and management (an `@` or `/` inside the password may be written
as-is there; in a stream URL they must still be percent-encoded, `%40` and
`%2F`). **Test connection** in the Cameras editor says whether ONVIF answered,
and whether it rejected the login, before you save the entry.

A device that carries several video channels (an NVR, a multi-sensor camera) can
be added once per channel, each with that channel's stream URLs: the settings,
stills, pan/tilt, zone and detections of each entry follow the channel its stream
URLs name, not the device's first.

| Option | Default | Description |
|---|---|---|
| `rtsp_main` | *one of these is required* | Main-stream RTSP URL (`rtsp://user:pass@host:554/path`) |
| `rtsp_sub` | *optional* | Sub-stream RTSP URL |
| `onvif_address` | *the stream URL's host* | Where the camera's ONVIF device service lives; may be a full URL with its own login |
| `name`, `permitted_users`, `audio_transcode` | | As for a Reolink camera |
| `record` | `true` | Seeds this camera's "Detection events" switch, as for a Reolink camera. Only meaningful when its ONVIF event service answers |
