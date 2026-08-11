#!/usr/bin/env bash
# Ticket 0001 probe helper: raw ADO REST GET with auth, saving payloads.
# Usage: probe.sh <out-name> <url-path-after-org>
set -euo pipefail
# az stores its login under the LOGIN user's home, not a tool profile home.
# Override if yours differs.
: "${AZURE_CONFIG_DIR:=$HOME/.azure}"
export AZURE_CONFIG_DIR
ADO_RES=499b84ac-1321-427f-aa17-267ca6975798
ORG=https://dev.azure.com/PolyphonyRequiem
OUT_DIR="$(dirname "$0")/raw"
mkdir -p "$OUT_DIR"
name="$1"; shift
url="$ORG$1"
az rest --method get --resource "$ADO_RES" --url "$url" > "$OUT_DIR/$name.json" 2>"$OUT_DIR/$name.err" \
  && echo "OK $name  $(wc -c <"$OUT_DIR/$name.json") bytes  <- $url" \
  || { echo "FAIL $name <- $url"; cat "$OUT_DIR/$name.err"; }
