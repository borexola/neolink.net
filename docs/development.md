# Self-tests & development

```bash
dotnet run --project src/Neolink.Server -- selftest
# with protocol samples from the original Rust repository:
dotnet run --project src/Neolink.Server -- selftest --config /path/to/rust/neolink-repo
```

`tools/fake_camera.py` implements enough of the camera side of the protocol to test the
full pipeline without hardware:

```bash
python3 tools/fake_camera.py /path/to/rust-repo/crates/core/src/bcmedia/samples 9000 &
# point a config at address = "127.0.0.1:9000", run neolink, then:
ffprobe -rtsp_transport tcp rtsp://127.0.0.1:8654/testcam
```

## Testing the ONVIF side without a third-party camera

`tools/fake_onvif.js` is the same idea for the settings surface of a non-Reolink
camera: a Node script that answers enough ONVIF (device information, media
profiles and their encoder options, stream URIs, imaging, PTZ with presets and
an absolute zoom, OSDs, the analytics cell motion grid, events, stills, reboot)
for the whole Camera settings panel to render and every write to land. It keeps the settings you write, prints every operation it is asked for
and the body of every `Set*`, and serves its current state as JSON at
`/__state` so a test can assert on it:

```bash
node tools/fake_onvif.js 8010 &
```

Two optional arguments shape the camera it pretends to be: a clock skew in
seconds (it then refuses any request not stamped in its own, drifted time), and
a comma-separated list of modes — `media2` (it speaks only Media2, and every
ver10 media call faults), `zoomonly` (a motorised lens with no pan or tilt),
`nocells` (no analytics service, so no motion grid of its own):

```bash
node tools/fake_onvif.js 8010 47 media2,zoomonly &
```

`/__state` shows the motion grid as rows of `0`/`1`, so a zone written from the
editor can be checked cell by cell.

Then add a generic camera pointed at any RTSP source, with its ONVIF address
aimed at the fake (the credentials in the URL are what it signs in with — the
fake accepts any):

```json
{ "name": "fakecam",
  "rtsp_main": "rtsp://127.0.0.1:8654/some-stream",
  "onvif_address": "http://admin:secret@127.0.0.1:8010/onvif/device_service" }
```

An easy way to have an RTSP source at hand is a second Neolink in `--demo`
mode: its synthetic cameras serve on `rtsp://127.0.0.1:8654/<name>`, which also
exercises the frame grab that gives a camera with no snapshot command its still.

It is a test double, not a conformant device — it is there to catch envelope,
namespace and field-name mistakes that unit tests on the parsers cannot.

## Testing uncommitted changes on a real server (local Docker image)

To try work-in-progress on a production-like box **without pushing anything to
GitHub**: build an image straight from your working tree, carry it over as a
tar, and load it there. The Dockerfile copies `src/` as-is, so uncommitted
edits are included.

On the dev machine (repo root). The image tag and tar name are FIXED
(`neolink.net:test` → `neolink-test.tar`), so the server-side commands never
change between test builds; only the `VERSION` label varies. Keep that label
`X.Y.Z-something` — the update checker compares by the numeric prefix, and a
label without one would see every release as an update:

```bash
docker build -t neolink.net:test --build-arg VERSION=0.8.8-test .

# sanity checks: the right version AND the right code (the suite runs in-image)
docker run --rm --entrypoint dotnet neolink.net:test neolink.net.dll --version
docker run --rm --entrypoint dotnet neolink.net:test neolink.net.dll selftest

docker save neolink.net:test -o neolink-test.tar
```

On the server (after copying the tar over):

```bash
docker load -i neolink-test.tar

# replace the previous test container if one exists — a plain `docker start`
# later would silently resurrect the OLD image, so remove it outright
docker stop neolink-test && docker rm neolink-test

docker run -d --name neolink-test \
  --restart unless-stopped \
  -p 8654:8654 -p 8655:8655 \
  -e TZ=Europe/London \
  -v /srv/neolink/config:/config \
  -v /srv/neolink/recordings:/recordings \
  neolink.net:test
```

Notes:

- The config mounted at `/config/config.json` must use **container paths**
  (e.g. `"path": "/recordings"`), matching the volume mounts.
- Config and recordings live in the host mounts, so stopping/removing the
  container never touches them.
- Confirm which build is live at the top toolbar of the web UI — it shows the
  exact version string you baked in.
- Old test images pile up; reclaim disk with `docker rmi neolink.net:<old-tag>`.
- The image is built for the dev machine's Docker platform (typically
  linux/amd64). For an ARM server, add `--platform linux/arm64` to the build.

## Project layout

```
src/Neolink.Server/          the service (RTSP + web API + optional web UI host)
  Bc/                        Baichuan wire protocol: header codec, BCEncrypt/AES/FullAes, XML
  Protocol/                  camera connection (message routing), login/stream/ping ops
  Media/                     BcMedia demuxer (I/P-frames, AAC, ADPCM), Annex-B utils, fMP4 muxer
  Streaming/                 per-camera reconnect service and the fan-out StreamHub
  Rtsp/                      RTSP server, sessions, RTP packetization, SDP
  Web/                       HTTP/WebSocket API + Blazor host (camera list, live fMP4)
  Config/                    JSON/TOML config (dependency-free mini parser)
src/Neolink.WebClient/       the web UI (Blazor razor class library, hosted in-process)
tools/fake_camera.py         protocol-level camera simulator for tests
```

The protocol implementation is a faithful port of the Rust `neolink_core` crate,
including its odd corners: 31-character MD5 credential mangling, XOR "encryption" keyed
by channel, nonce-derived AES session keys, binary-mode switching via
`<binaryData>1</binaryData>` extensions, `encryptLen`-padded FullAes media payloads, and
8-byte-padded media packets.
