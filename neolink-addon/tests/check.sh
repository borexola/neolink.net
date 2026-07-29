#!/usr/bin/env bash
# CI smoke test for the add-on launcher (run by docker.yml's addon-check job).
#
# run.sh executes at every add-on boot for every user — a jq typo in its
# options -> config.json mapping crash-loops them all, and nothing else ever
# runs the script before an image ships. So: run the REAL script against
# fixtures and assert the config.json it writes. `exec dotnet` at the end is
# satisfied by a stub on PATH; the NEOLINK_*_PATH overrides exist only for this.
set -euo pipefail
cd "$(dirname "$0")"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Stub the app: run.sh supervises `dotnet ...`, which must exit cleanly.
mkdir -p "$work/bin"
printf '#!/bin/sh\nexit 0\n' > "$work/bin/dotnet"
chmod +x "$work/bin/dotnet"

launch() { # $1 = options fixture
  PATH="$work/bin:$PATH" \
  NEOLINK_CONFIG_PATH="$work/config.json" \
  NEOLINK_OPTIONS_PATH="$1" \
  bash ../run.sh > /dev/null
}

fail() { echo "FAIL: $1" >&2; exit 1; }

assert_cameras() { # $1 = expected cameras JSON, $2 = test name
  diff <(jq -S .cameras "$work/config.json") <(jq -S . <<<"$1") \
    || fail "$2: cameras in config.json differ from expected (above)"
  echo "ok: $2"
}

bash -n ../run.sh || fail "run.sh has a bash syntax error"

# --- 1. First start, NVR channels: channel_id 2, explicit 0, and absent -----
# (A camera with no options password gets NO password key — the app treats
# absent and empty the same, and omitting is what lets a stored one survive.)
cat > "$work/options.json" <<'JSON'
{
  "cameras": [
    {"name": "front", "address": "10.0.0.5", "username": "admin", "password": "x", "channel_id": 2},
    {"name": "back",  "address": "10.0.0.5", "username": "admin", "always_on": true, "channel_id": 0},
    {"name": "solo",  "address": "10.0.0.9", "username": "admin", "password": "y"}
  ],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
rm -f "$work/config.json"
launch "$work/options.json"
assert_cameras '[
  {"name": "front", "address": "10.0.0.5", "username": "admin", "password": "x", "channel_id": 2},
  {"name": "back",  "address": "10.0.0.5", "username": "admin", "always_on": true, "channel_id": 0},
  {"name": "solo",  "address": "10.0.0.9", "username": "admin", "password": "y"}
]' "channel_id carried through (incl. explicit 0), absent stays absent"
jq -e '.bind == "0.0.0.0" and .web_port == 8655' "$work/config.json" > /dev/null \
  || fail "first-start template fields missing"

# --- 2. Legacy options (no channel_id anywhere) map cleanly -----------------
cat > "$work/options.json" <<'JSON'
{
  "cameras": [
    {"name": "gate", "address": "10.0.0.7", "username": "admin", "password": "z", "always_on": true}
  ],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
rm -f "$work/config.json"
launch "$work/options.json"
assert_cameras '[
  {"name": "gate", "address": "10.0.0.7", "username": "admin", "password": "z", "always_on": true}
]' "legacy options produce the plain mapping, no new keys"

# --- 3. Empty options list: hand-managed cameras in config.json untouched ---
cat > "$work/options.json" <<'JSON'
{"cameras": [], "auto_mqtt": false, "log_verbose": false}
JSON
cat > "$work/config.json" <<'JSON'
{"bind": "0.0.0.0", "cameras": [{"name": "handmade", "address": "10.0.0.3", "username": "admin", "password": "p", "channel_id": 5}]}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "handmade", "address": "10.0.0.3", "username": "admin", "password": "p", "channel_id": 5}
]' "empty options list leaves hand-managed cameras alone"

# --- 4. Hand-edited (commented, non-JSON) config.json: left byte-identical --
cat > "$work/config.json" <<'JSON'
{
  // hand-edited: run.sh must not touch this file
  "cameras": []
}
JSON
cp "$work/config.json" "$work/config.json.orig"
launch "$work/options.json"
cmp "$work/config.json" "$work/config.json.orig" \
  || fail "hand-edited config.json was modified"
echo "ok: hand-edited config.json left byte-identical"

# --- 5. Options MERGE per camera: web-UI-managed fields survive a boot ------
# The web UI's Cameras editor writes fields the options schema doesn't know
# (uid, wake_capture, udp, streams, http_address). A boot must keep them,
# keep a stored password when the options one is empty, keep cameras that
# exist only in config.json, and still append options-only cameras.
cat > "$work/options.json" <<'JSON'
{
  "cameras": [
    {"name": "TestCam", "address": "10.1.0.99", "username": "admin", "password": ""},
    {"name": "newcam",  "address": "10.0.0.8",  "username": "admin", "password": "n"}
  ],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
cat > "$work/config.json" <<'JSON'
{
  "bind": "0.0.0.0",
  "cameras": [
    {"name": "TestCam", "address": "10.1.0.13", "username": "admin", "password": "stored",
     "uid": "95270000ABCDEFGH", "wake_capture": true, "udp": true, "streams": "main"},
    {"name": "webonly", "address": "10.0.0.4", "username": "admin", "password": "w"}
  ]
}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "TestCam", "address": "10.1.0.99", "username": "admin", "password": "stored",
   "uid": "95270000ABCDEFGH", "wake_capture": true, "udp": true, "streams": "main"},
  {"name": "webonly", "address": "10.0.0.4", "username": "admin", "password": "w"},
  {"name": "newcam", "address": "10.0.0.8", "username": "admin", "password": "n"}
]' "per-camera merge: web-UI fields, stored password and web-only cameras survive; options address wins; new camera appended"

echo "all launcher checks passed"
