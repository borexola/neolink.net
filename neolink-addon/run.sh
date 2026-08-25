#!/usr/bin/env bash
# Neolink.NET add-on launcher.
#
# Builds /config/config.json from the add-on options and the Supervisor's MQTT
# service, then runs the app. One design rule above all: NEVER lose a user's
# edits. Options are merged key-by-key and PER CAMERA (the web UI's Cameras
# editor owns every field the options don't), and a file jq cannot parse (the
# app itself accepts // comments) is left completely untouched.
set -uo pipefail

# Overridable ONLY so CI can exercise this script against fixtures
# (tests/check.sh); inside the add-on container neither variable exists and
# the paths are exactly what they always were.
CONFIG=${NEOLINK_CONFIG_PATH:-/config/config.json}
OPTIONS=${NEOLINK_OPTIONS_PATH:-/data/options.json}
APP=(dotnet /app/neolink.net.dll --config "$CONFIG")

log() { echo "[addon] $*"; }

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

# Rebuilds config.json from itself + the options. Runs at boot AND between
# web-UI restarts, so a restart behaves exactly like a fresh start.
build_config() {
  local base cams count
  if [ -f "$CONFIG" ]; then
    if jq -e . "$CONFIG" >/dev/null 2>&1; then
      base=$(cat "$CONFIG")
    else
      log "config.json is hand-edited (comments or non-JSON syntax) — leaving it untouched"
      return 0
    fi
  else
    log "first start — creating $CONFIG"
    base=$(template)
  fi

  # Cameras: the options BOOTSTRAP the list, they do not own it. Each options
  # camera is merged onto the config.json camera of the same name — the
  # options' own fields win, and everything the web UI's Cameras editor set
  # (uid, wake_capture, udp, http_address, streams, ...) survives. Cameras
  # that exist only in config.json are KEPT: deleting happens in the web UI.
  # An empty options password keeps the stored one.
  # Only fields the user actually SET are carried over: a blank text field or an
  # untouched toggle must not overwrite what the web UI holds. Booleans are
  # therefore applied on true only — turning one back off is the web UI's job,
  # because Home Assistant stores an untouched toggle as false and that would
  # silently revert the web UI's setting at every boot.
  cams=$(jq '[.cameras[]? | . as $c
              | {name, address, username}
              + (if ($c.password // "") != "" then {password: $c.password} else {} end)
              + (if $c.always_on == true then {always_on: true} else {} end)
              + (if $c.udp == true then {udp: true} else {} end)
              + (if $c.wake_capture == true then {wake_capture: true} else {} end)
              + (if $c.hint_events == true then {hint_events: true} else {} end)
              + (if $c.channel_id != null then {channel_id: $c.channel_id} else {} end)
              + (if $c.keep_alive_hours != null then {keep_alive_hours: $c.keep_alive_hours} else {} end)
              + (if ($c.stream // "") != "" then {stream: $c.stream} else {} end)
              + (if ($c.uid // "") != "" then {uid: $c.uid} else {} end)
              + (if ($c.http_address // "") != "" then {http_address: $c.http_address} else {} end)]' "$OPTIONS")
  count=$(jq 'length' <<<"$cams")
  # Names match case-INSENSITIVELY, as the app compares them: renaming a camera's
  # case in the web UI must update that camera, not append a second one under a
  # name the app then treats as the same camera. The "is it already there?" test
  # is index(), never inside(): inside() compares strings by SUBSTRING, so a new
  # "Drive" would count as present because "Driveway" contains it, and would be
  # silently dropped instead of added.
  if [ "$count" -gt 0 ]; then
    base=$(jq --argjson opts "$cams" '
      (.cameras // []) as $existing
      | ($existing | map(.name | ascii_downcase)) as $have
      | .cameras =
          [ $existing[] as $c
            | (($opts | map(select((.name | ascii_downcase) == ($c.name | ascii_downcase)))
                | first) // null) as $o
            | if $o == null then $c else $c + $o | .name = $c.name end ]
          + [ $opts[] | . as $o | select(($have | index($o.name | ascii_downcase)) == null) ]' <<<"$base")
    log "merged $count camera(s) from the add-on options (web-UI-managed fields preserved)"
  else
    # The options list being empty while the web UI shows cameras is normal and
    # confusing in equal measure: say which list is in play, so nobody reads the
    # empty Options page as "my cameras are gone".
    have=$(jq '(.cameras // []) | length' <<<"$base")
    if [ "$have" -gt 0 ]; then
      log "no cameras in the add-on options — running the $have camera(s) from config.json (add and edit them in Neolink's web UI, camera ⚙)"
    fi
  fi

  # MQTT: fetch the broker the Mosquitto add-on provides and merge ONLY the
  # connection fields — base_topic, stats_interval and anything else set in the
  # web UI survive. auto_mqtt: false leaves the whole block alone (own broker).
  if [ "$(jq -r '.auto_mqtt' "$OPTIONS")" = "true" ] && [ -n "${SUPERVISOR_TOKEN:-}" ]; then
    local svc
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
}

mkdir -p /media/neolink 2>/dev/null || true

build_config
if [ "$(jq -r '.log_verbose' "$OPTIONS")" = "true" ]; then APP+=(--verbose); fi
# The web UI's Restart button exits the process; the add-on's Watchdog toggle
# (or a docker/systemd restart policy outside HA) starts it again.
exec "${APP[@]}"
