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
trap 'kill "${holder:-}" 2>/dev/null || true; rm -rf "$work"' EXIT

# Stub the app: run.sh supervises `dotnet ...`, which must exit cleanly.
mkdir -p "$work/bin"
printf '#!/bin/sh\ntouch "%s/dotnet.ran"\nexit 0\n' "$work" > "$work/bin/dotnet"
chmod +x "$work/bin/dotnet"

launch() { # $1 = options fixture
  PATH="$work/bin:$PATH" \
  NEOLINK_CONFIG_PATH="$work/config.json" \
  NEOLINK_OPTIONS_PATH="$1" \
  bash ../run.sh > "$work/launch.log"
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
     "uid": "95270000ABCDEFGH", "wake_capture": true, "udp": true, "streams": "main",
     "max_encryption": "bcencrypt", "legacy_login": true},
    {"name": "webonly", "address": "10.0.0.4", "username": "admin", "password": "w"}
  ]
}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "TestCam", "address": "10.1.0.99", "username": "admin", "password": "stored",
   "uid": "95270000ABCDEFGH", "wake_capture": true, "udp": true, "streams": "main",
   "max_encryption": "bcencrypt", "legacy_login": true},
  {"name": "webonly", "address": "10.0.0.4", "username": "admin", "password": "w"},
  {"name": "newcam", "address": "10.0.0.8", "username": "admin", "password": "n"}
]' "per-camera merge: web-UI fields, hand-edited login overrides, stored password and web-only cameras survive; options address wins; new camera appended"

# --- 6. Camera renamed by case in the web UI: updated, never duplicated ------
# The app compares camera names case-insensitively, so appending a second
# entry under the options' spelling would give it two cameras it calls one.
cat > "$work/options.json" <<'JSON'
{
  "cameras": [{"name": "TestCam", "address": "10.0.0.2", "username": "admin", "password": "p"}],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
cat > "$work/config.json" <<'JSON'
{"bind": "0.0.0.0", "cameras": [{"name": "testcam", "address": "10.0.0.1", "username": "admin", "password": "p", "uid": "95270000ABCDEFGH"}]}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "testcam", "address": "10.0.0.2", "username": "admin", "password": "p", "uid": "95270000ABCDEFGH"}
]' "case-renamed camera is updated in place (web UI spelling kept), not duplicated"

# --- 7. Extended fields: set ones apply, blanks and false toggles do not -----
# Home Assistant stores an untouched toggle as false, so a false udp/wake_capture
# must NOT overwrite a web-UI-set true; blank text fields must not blank stored
# values either. Fields the user really did set do win.
cat > "$work/options.json" <<'JSON'
{
  "cameras": [
    {"name": "batt", "address": "10.0.0.6", "username": "admin", "password": "p",
     "uid": "95270000ABCDEFGH", "udp": true, "wake_capture": true,
     "stream": "subStream", "keep_alive_hours": 6, "http_address": "10.0.0.6:80"},
    {"name": "kept", "address": "10.0.0.7", "username": "admin", "password": "",
     "uid": "", "udp": false, "wake_capture": false, "http_address": ""}
  ],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
cat > "$work/config.json" <<'JSON'
{
  "bind": "0.0.0.0",
  "cameras": [
    {"name": "kept", "address": "10.0.0.7", "username": "admin", "password": "stored",
     "uid": "95270000ZZZZZZZZ", "udp": true, "wake_capture": true, "http_address": "10.0.0.7:80"}
  ]
}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "kept", "address": "10.0.0.7", "username": "admin", "password": "stored",
   "uid": "95270000ZZZZZZZZ", "udp": true, "wake_capture": true, "http_address": "10.0.0.7:80"},
  {"name": "batt", "address": "10.0.0.6", "username": "admin", "password": "p",
   "uid": "95270000ABCDEFGH", "udp": true, "wake_capture": true,
   "stream": "subStream", "keep_alive_hours": 6, "http_address": "10.0.0.6:80"}
]' "extended fields apply when set; blank text and false toggles never clobber the web UI"

# --- 8. A name that is a SUBSTRING of an existing one is still a new camera --
# jq's inside() compares strings by substring, so "Drive" once counted as
# already present because "Driveway" contains it, and was silently dropped.
cat > "$work/options.json" <<'JSON'
{
  "cameras": [
    {"name": "Drive", "address": "10.0.0.9", "username": "admin", "password": "b"}
  ],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
cat > "$work/config.json" <<'JSON'
{
  "bind": "0.0.0.0",
  "cameras": [
    {"name": "Driveway", "address": "10.0.0.1", "username": "admin", "password": "a", "uid": "95270000KEEPMEXX"}
  ]
}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "Driveway", "address": "10.0.0.1", "username": "admin", "password": "a", "uid": "95270000KEEPMEXX"},
  {"name": "Drive", "address": "10.0.0.9", "username": "admin", "password": "b"}
]' "a name that is a substring of an existing camera is appended, not swallowed"

# --- 9. "udp": true with no uid is repaired, not handed to the app ----------
# The app refuses to load such an entry and used to fail the WHOLE config with
# it, taking every other camera down. The merge cannot unset a boolean, so an
# Options toggle could write a state only a hand edit could undo.
cat > "$work/options.json" <<'JSON'
{
  "cameras": [
    {"name": "nouid", "address": "10.0.0.11", "username": "admin", "password": "p", "udp": true}
  ],
  "auto_mqtt": false,
  "log_verbose": false
}
JSON
cat > "$work/config.json" <<'JSON'
{
  "bind": "0.0.0.0",
  "cameras": [
    {"name": "stale", "address": "10.0.0.12", "username": "admin", "password": "p", "udp": true},
    {"name": "real",  "address": "10.0.0.13", "username": "admin", "password": "p",
     "udp": true, "uid": "95270000ABCDEFGH"}
  ]
}
JSON
launch "$work/options.json"
assert_cameras '[
  {"name": "stale", "address": "10.0.0.12", "username": "admin", "password": "p"},
  {"name": "real",  "address": "10.0.0.13", "username": "admin", "password": "p",
   "udp": true, "uid": "95270000ABCDEFGH"},
  {"name": "nouid", "address": "10.0.0.11", "username": "admin", "password": "p"}
]' "udp without a uid is stripped (new and already-stored); udp with a uid is untouched"

# --- 10. A port already in use stops the launcher with a clear line ---------
# Under host networking the app's ports are the host's; the alternative is a
# bind-exception crash loop that never says which add-on holds the port.
if command -v python3 >/dev/null; then
  python3 -c 'import socket,time; s=socket.socket(); s.bind(("127.0.0.1",0)); s.listen(1); print(s.getsockname()[1], flush=True); time.sleep(120)' > "$work/port" &
  holder=$!
  for _ in $(seq 50); do [ -s "$work/port" ] && break; sleep 0.1; done
  busy=$(cat "$work/port")
  [ -n "$busy" ] || fail "could not start a listener for the busy-port test"
  cat > "$work/options.json" <<'JSON'
{"cameras": [], "auto_mqtt": false, "log_verbose": false}
JSON
  printf '{"bind": "0.0.0.0", "web_port": %s, "cameras": []}\n' "$busy" > "$work/config.json"
  rm -f "$work/dotnet.ran"
  if NEOLINK_BUSY_RETRY_SECONDS=0 launch "$work/options.json"; then
    fail "launcher started the app on a port already in use"
  fi
  grep -q "tcp:$busy is already in use" "$work/launch.log" \
    || fail "busy port was not named in the launcher log"
  [ ! -e "$work/dotnet.ran" ] || fail "app was started despite the busy port"
  kill "$holder" 2>/dev/null; wait "$holder" 2>/dev/null || true
  echo "ok: a port already in use stops the launcher and names the port"
else
  echo "skip: busy-port test (no python3)"
fi

# --- Changelog: Home Assistant reads CHANGELOG.md from THIS folder (not the
# repo root) and slices the notes it shows by an exact "## <version>" heading.
# A version bump without a matching section ships an empty update dialog, and
# nothing else would catch it: once the file exists the Supervisor stops
# complaining whatever is in it.
[ -f ../CHANGELOG.md ] || fail "neolink-addon/CHANGELOG.md is missing (HA shows 'No changelog found')"
ver=$(sed -n 's/^version: *"\?\([^"]*\)"\? *$/\1/p' ../config.yaml | head -1)
[ -n "$ver" ] || fail "could not read version from neolink-addon/config.yaml"
grep -qE "^#* ${ver//./\\.}$" ../CHANGELOG.md \
  || fail "neolink-addon/CHANGELOG.md has no '## $ver' section (HA slices release notes by that heading)"
echo "ok: changelog has a section for $ver"

# --- Host-network add-on: same image, so the same version and release notes --
host=../../neolink-host
hver=$(sed -n 's/^version: *"\?\([^"]*\)"\? *$/\1/p' "$host/config.yaml" | head -1)
[ "$hver" = "$ver" ] || fail "neolink-host/config.yaml is at '$hver' but neolink-addon is at $ver (both run the same image)"
grep -qE "^#* ${ver//./\\.}$" "$host/CHANGELOG.md" \
  || fail "neolink-host/CHANGELOG.md has no '## $ver' section"
echo "ok: host-network add-on matches $ver"

echo "all launcher checks passed"
