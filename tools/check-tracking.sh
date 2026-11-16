#!/usr/bin/env bash
# Check declared Wayfinder <-> ADO links using the current workspace's cache.
# A ticket's YAML frontmatter may contain: tracked_in: [139, 140]
# Only System.Description can establish the backward link. Cached evidence is
# not a fresh provider read or proof of the current upstream state.
#
# Usage: check-tracking.sh [ticket-id | --selftest]
# --selftest is entirely offline and does not require an installed Twig.
# Exit: 0 checked/no declared links; 1 broken/unverifiable links; 2 usage/setup.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TICKET_DIRS=("$REPO_ROOT/wayfinder/tickets" "$REPO_ROOT/wayfinder-1.0/tickets")
PYTHON="${PYTHON:-python}"
fail_count=0
check_count=0

note() { printf '%s\n' "$*"; }
bad() { printf 'BROKEN  %s\n' "$*"; fail_count=$((fail_count + 1)); }
good() { printf 'ok      %s\n' "$*"; }
usage_error() { note "TWIG-TRACKING: BROKEN ($* - nothing was checked)"; return 2; }

extract_tracked_in() {
    awk '
        { sub(/\r$/, "") }
        NR == 1 && $0 == "---" { in_fm = 1; next }
        in_fm && $0 == "---" { exit }
        in_fm && /^tracked_in:/ {
            sub(/^tracked_in:[[:space:]]*/, "")
            gsub(/[\[\],]/, " ")
            print
        }
    ' "$1"
}

extract_ticket_id() {
    awk '
        { sub(/\r$/, "") }
        NR == 1 && $0 == "---" { in_fm = 1; next }
        in_fm && $0 == "---" { exit }
        in_fm && /^id:/ {
            sub(/^id:[[:space:]]*/, "")
            sub(/[[:space:]]*$/, "")
            print
        }
    ' "$1"
}

fetch_item_description() {
    local id="$1" payload code
    payload="$(twig show "$id" --fields System.Description --output json)"
    code=$?
    if [[ "$code" -ne 0 ]]; then
        printf 'Cached Twig read for item %s failed (exit %s).\n' "$id" "$code" >&2
        return "$code"
    fi
    # ShowAsync emits one Record, projected directly by JsonRenderer. Do not
    # accept old fields-only objects, a different item, or a seed as that read.
    printf '%s' "$payload" | PYTHONIOENCODING=utf-8 "$PYTHON" -c '
import json
import sys

try:
    item = json.load(sys.stdin)
except json.JSONDecodeError as error:
    sys.exit(f"Invalid cached Twig JSON: {error}")
if (not isinstance(item, dict)
        or type(item.get("id")) is not int or item["id"] != int(sys.argv[1])
        or item.get("isSeed") is not False or item.get("cached") is not True
        or not isinstance(item.get("revision"), str) or not item["revision"]
        or type(item.get("isDirty")) is not bool):
    sys.exit("Expected the current cached published-item projection for the exact ID.")
fields = item.get("fields")
description = fields.get("System.Description") if isinstance(fields, dict) else None
if not isinstance(description, str) or not description.strip():
    sys.exit("The cached item has no readable System.Description.")
sys.stdout.write(description)
' "$id"
}

check_ticket() {
    local file="$1" ticket_id ids ado_id desc
    ids="$(extract_tracked_in "$file")"
    [[ -z "$ids" ]] && return 0
    ticket_id="$(extract_ticket_id "$file")"
    if [[ ! "$ticket_id" =~ ^[0-9]+$ ]]; then
        check_count=$((check_count + 1))
        bad "declared links in $file have no valid ticket id"
        return
    fi
    for ado_id in $ids; do
        check_count=$((check_count + 1))
        if [[ ! "$ado_id" =~ ^[1-9][0-9]*$ ]]; then
            bad "ticket $ticket_id has an invalid declared work-item id"
            continue
        fi
        if ! desc="$(fetch_item_description "$ado_id")"; then
            bad "ticket $ticket_id -> work item $ado_id cannot be verified from the current cache"
            continue
        fi
        if grep -qiE "(wayfinder|ticket|ruling)[^0-9]{0,12}0*${ticket_id}\b" <<<"$desc"; then
            good "ticket $ticket_id <-> cached work item $ado_id"
        else
            bad "cached work item $ado_id does not name ticket $ticket_id back (one-way link)"
        fi
    done
}

main() {
    if [[ "$#" -gt 1 ]]; then
        usage_error "expected at most one ticket id or --selftest"
        return $?
    fi
    local wanted="${1:-}"
    if [[ "$wanted" == --selftest ]]; then
        if ! command -v "$PYTHON" >/dev/null 2>&1; then
            usage_error "Python is unavailable"
            return $?
        fi
        "$PYTHON" "$REPO_ROOT/tools/test-check-tracking.py"
        return $?
    fi
    if [[ "$wanted" == -* ]]; then
        usage_error "unknown option '$wanted'"
        return $?
    fi
    if [[ -n "$wanted" && ! "$wanted" =~ ^[0-9]+$ ]]; then
        usage_error "ticket id must be numeric"
        return $?
    fi
    if ! command -v twig >/dev/null 2>&1 || ! command -v "$PYTHON" >/dev/null 2>&1; then
        usage_error "Twig and Python must be available"
        return $?
    fi
    note "Checking cached descriptions in the current bound workspace; no refresh or publication."
    local dir file
    for dir in "${TICKET_DIRS[@]}"; do
        [[ -d "$dir" ]] || continue
        for file in "$dir"/*.md; do
            [[ -f "$file" ]] || continue
            if [[ -n "$wanted" ]]; then
                [[ "$(extract_ticket_id "$file")" == "$wanted" ]] || continue
            fi
            check_ticket "$file"
        done
    done
    if [[ "$check_count" -eq 0 ]]; then
        if [[ -n "$wanted" ]]; then
            usage_error "no ticket '$wanted' with declared links"
            return $?
        fi
        note "TWIG-TRACKING: no declared links to check."
        return 0
    fi
    if [[ "$fail_count" -gt 0 ]]; then
        note "TWIG-TRACKING: BROKEN ($fail_count of $check_count links)"
        return 1
    fi
    note "TWIG-TRACKING: OK ($check_count links, both directions); cached observations only."
}

main "$@"
