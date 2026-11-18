#!/usr/bin/env bash
# Qualify the archive in an isolated owner profile, from outside the checkout.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
cli="${TWIG_TEST_CLI:-$repo/src/Twig/bin/Debug/net11.0/twig}"
fixture="$(mktemp -d)"
export HOME="$fixture/home" TWIG_DAEMON_INSTALL_ROOT="$fixture/selection" TWIG_USER_HOME="$fixture/metadata"
unset TWIG_TELEMETRY_ENDPOINT
mkdir -m 700 "$HOME"
cleanup() {
    if [[ -f "$TWIG_DAEMON_INSTALL_ROOT/current.json" ]]; then "$cli" daemon stop >/dev/null || return 1; fi
    rm -rf -- "$fixture"
}
trap cleanup EXIT
cd "$repo/artifacts"
sha256sum -c twig-daemon-linux-x64.tar.gz.sha256
tar -xzf twig-daemon-linux-x64.tar.gz -C "$fixture"
cd "$fixture"
python3 -I daemon-linux.py install daemon-linux-x64 "$fixture/private"
[[ "$("$cli" daemon status -o json)" == '{"running":false}' ]]
"$cli" daemon start -o json > first & first=$!
"$cli" daemon start -o json > second & second=$!
wait "$first"; wait "$second"
for result in first second; do grep -Fx '{"running":true}' "$result"; done
[[ "$("$cli" daemon status -o json)" == '{"running":true}' ]]
timeout 10 "$cli" bench create HeadlessSmoke -o json > created
id="$(python3 -c 'import json; print(json.load(open("created"))["id"])')"
timeout 10 "$cli" bench list -o ids | grep -F "$id"
[[ "$("$cli" daemon stop -o json)" == '{"running":false}' ]]
[[ "$("$cli" daemon status -o json)" == '{"running":false}' ]]
# An unverified process holding the data lease is not a stopped instance.
if flock "$fixture/private/daemon.lock" "$cli" daemon start; then exit 1; fi
echo 'PASS: Linux package selection, serialized start/reuse, authenticated status, command, stop and busy-lease refusal.'
