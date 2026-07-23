#!/usr/bin/env bash
# Neolink.NET add-on launcher.
#
# Builds /config/config.json from the add-on options and the Supervisor's MQTT
# service, then hands over to the app. One design rule above all: NEVER lose a
# user's edits. The file is only ever merged key-by-key, and a file jq cannot
# parse (the app itself accepts // comments) is left completely untouched.
set -uo pipefail

# Overridable ONLY so CI can exercise this script against fixtures
# (tests/check.sh); inside the add-on container neither variable exists and
# the paths are exactly what they always were.
CONFIG=${NEOLINK_CONFIG_PATH:-/config/config.json}
OPTIONS=${NEOLINK_OPTIONS_PATH:-/data/options.json}
APP=(dotnet /app/neolink.net.dll --config "$CONFIG")

log() { echo "[addon] $*"; }

finish() {
  if [ "$(jq -r '.log_verbose' "$OPTIONS")" = "true" ]; then APP+=(--verbose); fi
  exec "${APP[@]}"
}

# First start: HA-friendly defaults. RTSP on 8654 and the web UI on 8655
# (matching the manifest's port map), recordings on the media share so clips
# appear in Home Assistant's media browser, event recording on by default.
template() {
  cat <<'JSON'
{
  "bind": "0.0.0.0",
  "bind_port": 8654,
  "web_port": 8655,
  "webui": true,
  "recording": {
    "path": "/media/neolink"
  },
  "cameras": []
}
JSON
}

mkdir -p /media/neolink

base=""
if [ -f "$CONFIG" ]; then
  if jq -e . "$CONFIG" >/dev/null 2>&1; then
    base=$(cat "$CONFIG")
  else
    log "config.json is hand-edited (comments or non-JSON syntax) — leaving it untouched"
    finish
  fi
else
  log "first start — creating $CONFIG"
  base=$(template)
fi

# Cameras: the add-on options own the list whenever it is non-empty, so adding
# or removing a camera in the add-on UI just works. An EMPTY options list keeps
# whatever config.json already has — cameras managed by hand stay untouched.
cams=$(jq '[.cameras[]? | {name, address, username, password: (.password // "")}
            + (if .always_on == true then {always_on: true} else {} end)
            + (if .channel_id != null then {channel_id: .channel_id} else {} end)]' "$OPTIONS")
count=$(jq 'length' <<<"$cams")
if [ "$count" -gt 0 ]; then
  base=$(jq --argjson cams "$cams" '.cameras = $cams' <<<"$base")
  log "applied $count camera(s) from the add-on options"
fi

# MQTT: fetch the broker the Mosquitto add-on provides and merge ONLY the
# connection fields — base_topic, stats_interval and anything else set in the
# web UI survive. auto_mqtt: false leaves the whole block alone (own broker).
if [ "$(jq -r '.auto_mqtt' "$OPTIONS")" = "true" ] && [ -n "${SUPERVISOR_TOKEN:-}" ]; then
  svc=$(curl -fsS -m 10 -H "Authorization: Bearer ${SUPERVISOR_TOKEN}" \
        http://supervisor/services/mqtt 2>/dev/null) || svc=""
  if [ -n "$svc" ] && [ "$(jq -r '.result // empty' <<<"$svc")" = "ok" ]; then
    base=$(jq --argjson m "$(jq '.data' <<<"$svc")" \
      '.mqtt = ((.mqtt // {})
                + {broker: $m.host, port: ($m.port // 1883),
                   username: ($m.username // ""), password: ($m.password // "")}
                + (if $m.ssl == true then {tls: true} else {} end))' <<<"$base")
    log "MQTT wired to the Home Assistant broker at $(jq -r '.data.host' <<<"$svc") — entities appear automatically"
  else
    log "no MQTT broker service found — install the 'Mosquitto broker' add-on to get Home Assistant entities"
  fi
fi

printf '%s\n' "$base" | jq . > "$CONFIG.tmp" && mv "$CONFIG.tmp" "$CONFIG"

finish
