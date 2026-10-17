# Neolink.NET (host network)

The same app and image as the **Neolink.NET** add-on, run on the host's network
instead of Home Assistant's add-on network. UDP-only battery cameras need that:
their handshake carries the client's port inside the packet, which no port
mapping can translate. Setup, options, MQTT, the sidebar panel, recording and
wake hints all work as described in the stable add-on's documentation:
https://github.com/borexola/neolink.net/blob/main/neolink-addon/DOCS.md

## Who should use it

- Cameras with `udp` on (they also need their `uid`). Wired and Wi-Fi cameras
  that connect over TCP work here too, so one add-on can run the whole set.
- Everyone else: stay on the stable add-on, which keeps the Network section
  for remapping ports.

## What is different

- 8654 (RTSP) and 8655 (web UI and API) bind directly on the Home Assistant
  host; there is no Network section. To move them, set `bind_port` and
  `web_port` in `config.json`. The sidebar panel follows the web UI only on
  8655.
- Wake-hint listeners (syslog 5140/udp, the push decoy) and the ONVIF PTZ port
  are live on the host as soon as you turn them on in the web UI; there is no
  host port to map.
- Automatic MQTT setup is verified on Home Assistant OS. On a Supervised
  install, if the log says the broker cannot be resolved, turn **Automatic
  MQTT setup** off and enter the broker's IP in the web UI.
- The add-on's security rating is one step below the stable add-on's, which
  is how Home Assistant scores host networking.

## Moving from the stable add-on

Both bind the same ports, so they cannot run at the same time.

1. Stop the stable add-on and turn off its **Start on boot** (or uninstall it).
2. Copy its settings: with the file editor or Samba add-on, copy everything
   under `addon_configs/…_neolink/` to `addon_configs/…_neolink_host/`
   (`config.json`, `users.json`, settings and the rest). Skip this to start
   fresh; cameras can also be entered in this add-on's Configuration tab.
3. Start this add-on. Recordings and events in `/media/neolink` are shared,
   so they show up as before.
4. If the log says a port is **already in use**, the stable add-on is still
   running: stop it and start this one again.

Project docs, issues and source: https://github.com/borexola/neolink.net
