# Unraid template

`neolink.net.xml` is a [Community Applications](https://forums.unraid.net/topic/38582-plug-in-community-applications/)
(CA) template for running Neolink.NET on Unraid. `neolink.net.png` is the store
icon (source: `logo.svg`).

## Getting it into the CA "app store"

1. This template and its icon already live in the public repo.
2. Request inclusion: post the URL of this repository in the **Community
   Applications** support thread on the Unraid forums (linked above) and ask
   for the template to be added to the feed. A moderator adds the repo; after
   that, searching **Neolink.NET** in *Apps* installs it for everyone.

## Installing it today (before CA inclusion)

Anyone can run it now without waiting for the store:

- **Apps → Settings → Template Repositories** → add
  `https://github.com/borexola/neolink.net` → **Save**. Neolink.NET then shows
  up in the *Apps* search on that server.
- Or **Docker → Add Container** and paste the raw template URL
  (`https://raw.githubusercontent.com/borexola/neolink.net/main/unraid/neolink.net.xml`)
  into the *Template* field.

## First-run setup

Install it, set the ports and the **Config** path (default
`/mnt/user/appdata/neolink`), and start it. On first start Neolink writes a
commented starter `config.json` into that folder and the web UI comes straight
up (no crash-loop) — open the **WebUI** (port 8655) and create your admin
account.

To add cameras, edit `config.json` in the Config folder: uncomment the example
camera block and set each camera's `address`, `username` and `password`, then
restart the container. To record, add `"recording": { "path": "/recordings" }`
and map the **Recordings** volume. Full reference:
[`config.example.json`](../src/Neolink.Server/config.example.json).

Tiered storage: map the optional **Clips** / **Archive** volumes and set
`clips_path` / `archive_path` in `config.json` — see the project README's
*Tiered storage* section. In Unraid, put `/clips` on the cache/SSD and
`/archive` on the array.

## Beta channel

To test pre-release builds, change the container's **Repository** from
`ghcr.io/borexola/neolink.net:latest` to `ghcr.io/borexola/neolink.net:beta`.
The `:beta` tag is published from the repo's `beta` branch and is separate from
stable releases; switch back to `:latest` at any time.

## Updating the icon

Edit `logo.svg`, then re-rasterize to a 256×256 transparent PNG (any tool;
headless Chrome works well):

```bash
chrome --headless=new --disable-gpu --default-background-color=00000000 \
       --window-size=256,256 --screenshot=neolink.net.png logo.svg
```
