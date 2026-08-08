# Running Neolink.NET in Docker

The full Docker reference: image tags, compose, Unraid, upgrading, and building
the image yourself. For the one-command version, see the
[README's quick start](../README.md#quick-start-docker--recommended).

Prebuilt multi-arch images (`linux/amd64` + `linux/arm64`) are published to
GitHub Container Registry on every push to `main` and every `v*` release tag.
Docker selects the right architecture (x86-64 server, Raspberry Pi 4/5, ARM
NAS) automatically.

## 1. Pull the image

```bash
docker pull ghcr.io/borexola/neolink.net:latest
```

Available tags:

| Tag | Meaning |
|---|---|
| `latest` | most recent build of `main` |
| `0.6.0`, `0.6` | a specific release (created from `v0.6.0` git tags) — pin these in production |
| `main` | same as `latest`, explicit branch tag |
| `beta` | rolling pre-release test channel (built from the `beta` branch) — try new features early; not for production |

Verify the pull:

```bash
docker image inspect ghcr.io/borexola/neolink.net:latest --format '{{.Os}}/{{.Architecture}} {{.Created}}'
```

> **`denied` or `unauthorized` when pulling?** The package is public, so no login is
> needed. If you see this on a fresh setup you are likely logged into ghcr.io with an
> expired token — run `docker logout ghcr.io` and pull again.
> **`manifest unknown`?** The tag doesn't exist (typo, or a release tag that hasn't
> been built yet) — check the available tags on the
> [package page](https://github.com/borexola/neolink.net/pkgs/container/neolink.net).

## 2. Create a config

```bash
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
```

Edit it: camera names, IP addresses, and credentials (same login as the Reolink app).

> **New to it?** You can skip this step. If `config.json` doesn't exist on
> first start, Neolink.NET writes a commented starter config and boots straight to
> the web UI (empty, no crash-loop) — then add your cameras under Server settings
> (the gear icon) and restart. Handy for one-click installs (Unraid, Portainer).

## 3. Run

```bash
# /config is a directory mount: config.json lives in it, and runtime settings
# from the web UI (settings.json) are persisted next to it.
# TZ sets the time zone for timestamps and the UI clock (defaults to UTC).
docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=Europe/London \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Then check it came up:

```bash
docker logs -f neolink     # prints the ready-to-use RTSP and web UI URLs
```

- **Web UI**: http://localhost:8655
- **RTSP**: `rtsp://localhost:8654/<camera-name>`

## Or with compose

Save this as `docker-compose.yml` next to your `config/` directory
(or `curl -O https://raw.githubusercontent.com/borexola/neolink.net/main/docker-compose.yml`):

```yaml
services:
  neolink:
    image: ghcr.io/borexola/neolink.net:latest
    container_name: neolink
    restart: unless-stopped
    environment:
      - TZ=Europe/London   # time zone for timestamps + the UI clock (defaults to UTC)
    ports:
      - "8654:8654"   # RTSP (TCP-interleaved works for ffmpeg/Frigate/VLC)
      - "8655:8655"   # web UI + API; remove if webui:false and API unused
    volumes:
      - ./config:/config   # holds config.json + web-UI settings.json
      # Recording storage — uncomment and set "recording": { "path": "/recordings" } in config.json:
      # - ./recordings:/recordings
      # Optional tiered storage (see "Tiered storage" in the README). Map a volume for EVERY tier
      # path you set, or that footage lands inside the container and is lost on recreate:
      # - /mnt/fast-ssd/neolink:/clips     # fast SSD tier  → "clips_path": "/clips"
      # - /mnt/bigdisk/neolink:/archive    # cold archive   → "archive_path": "/archive"
    # Host networking instead of port maps — REQUIRED for UDP-only battery
    # cameras ("udp": true), and needed for RTSP over UDP transport. Delete the
    # `ports:` block above when you enable it. See "UDP-only battery models".
    # network_mode: host
```

Then:

```bash
docker compose up -d
docker compose logs -f    # shows the rtsp:// and web UI URLs
```

## Unraid

An Unraid [Community Applications](https://forums.unraid.net/topic/38582-plug-in-community-applications/)
template ships in [`unraid/`](../unraid/). Add
`https://github.com/borexola/neolink.net` under **Apps → Settings → Template
Repositories**, then search **Neolink.NET** in *Apps* — or paste the raw
[template URL](https://raw.githubusercontent.com/borexola/neolink.net/main/unraid/neolink.net.xml)
into **Docker → Add Container**. First start writes a starter config and opens
the web UI; add cameras under Server settings (the gear icon) and restart. See
[unraid/README.md](../unraid/README.md).

## Upgrading

```bash
docker pull ghcr.io/borexola/neolink.net:latest
docker rm -f neolink
docker run -d --name neolink ...   # same run command as above
# or, with compose:
docker compose pull && docker compose up -d
```

## Building the image from source

```bash
git clone https://github.com/borexola/neolink.net.git && cd neolink.net
docker build -t neolink.net .
docker run -d --name neolink -p 8654:8654 -p 8655:8655 \
    -v "$PWD/config:/config" neolink.net
```

Then:
- **Web UI**: http://localhost:8655
- **RTSP**: `rtsp://localhost:8654/<camera-name>`

> **When you need host networking** (`network_mode: host`, or `--network host`,
> instead of port mapping): for
> [UDP-only battery cameras](../README.md#udp-only-battery-models-beta) — where
> it is required, not optional — and for RTSP over **UDP** transport. Everything
> else, including TCP-interleaved RTSP (the default for ffmpeg/Frigate, and
> `--rtsp-tcp` in VLC), works fine with plain port mapping.
