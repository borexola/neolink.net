# Neolink.NET add-on

Bridge for Reolink cameras that speak the Baichuan protocol (port 9000, the
"phone app" protocol) — including models with no RTSP/ONVIF of their own.

- **Live view + RTSP restreaming** for VLC, Frigate and other NVRs
- **Web UI**: camera wall, event review strip, 24-hour timeline, two-way talk
- **Recording**: detection events with pre-roll, thumbnails and clip retention —
  clips land in Home Assistant's media browser
- **Native HA entities over MQTT**: motion/person/vehicle/animal and smart
  detections, doorbell press events, battery, Wi-Fi, siren, floodlight, PTZ,
  on-demand recording switch, per-camera recording status — all via MQTT
  discovery, configured automatically against the Mosquitto broker add-on

Add your cameras in the Configuration tab, start the add-on, open the Web UI.
Full walkthrough in the Documentation tab.
