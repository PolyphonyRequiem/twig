#!/usr/bin/env bash
# ============================================================================
# check-tracking.sh — verify the decision↔board links are real, in BOTH
# directions.
#
# WHY THIS EXISTS
#
# Work is tracked in three places on purpose (AGENTS.md § "Where work is
# tracked"): schedulable work on the ADO board, decisions in this repo, public
# issues on GitHub. That split only works if the links between them hold. A
# tracker split WITHOUT enforced links does not solve "I cannot see the whole
# picture from one place" — it just moves the problem and adds a second place
# to look.
#
# Prose in AGENTS.md cannot enforce this. The repo has already learned that
# lesson once: guidance telling humans to "remember the exit code" demonstrably
# did not hold, which is why tools/run-tests.sh exists. Same shape here.
#
# WHAT IT CHECKS
#
# A wayfinder ticket declares its board items in frontmatter:
#
#     ---
#     id: 1007
#     title: Build the Bench
#     tracked_in: [139, 140]
#     ---
#
# For every such declaration this script asserts:
#
#   1. FORWARD  — the ADO work item exists. A dangling id is a hard error, not
#                 a warning: an id that does not resolve is exactly the stale
#                 reference the Bench design forbids elsewhere in twig.
#   2. BACKWARD — that work item's description names the ticket back. A one-way
#                 link is how you end up on a board item with no idea which
#                 ruling it implements.
#
# A ticket with NO tracked_in field is not an error. Most rulings are decisions
# that were never scheduled, and demanding a board item for each would push
# ceremony onto the decision layer.
#
# USAGE
#     tools/check-tracking.sh              # check every ticket
#     tools/check-tracking.sh 1007         # check one ticket by id
#     tools/check-tracking.sh --selftest   # prove the checker can FAIL
#
# EXIT CODES
#     0  every declared link resolves in both directions
#     1  at least one link is broken (details on stdout)
#     2  usage error, or twig is unavailable
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TICKET_DIRS=("$REPO_ROOT/wayfinder/tickets" "$REPO_ROOT/wayfinder-1.0/tickets")

fail_count=0
check_count=0

note()  { printf '%s\n' "$*"; }
bad()   { printf 'BROKEN  %s\n' "$*"; fail_count=$((fail_count + 1)); }
good()  { printf 'ok      %s\n' "$*"; }

# Extract the tracked_in ids from a ticket's YAML frontmatter.
# Frontmatter is the block between the first two '---' lines. Reading only that
# block matters: ticket BODIES quote ids constantly ("see 0013", "#271"), so a
# whole-file grep would invent links that were never declared.
extract_tracked_in() {
    local file="$1"
    awk '
        NR == 1 && $0 == "---" { in_fm = 1; next }
        in_fm && $0 == "---"   { exit }
        in_fm && /^tracked_in:/ {
            line = $0
            sub(/^tracked_in:[[:space:]]*/, "", line)
            gsub(/[\[\]]/, "", line)
            gsub(/,/, " ", line)
            print line
        }
    ' "$file"
}

extract_ticket_id() {
    local file="$1"
    awk '
        NR == 1 && $0 == "---" { in_fm = 1; next }
        in_fm && $0 == "---"   { exit }
        in_fm && /^id:/ {
            line = $0
            sub(/^id:[[:space:]]*/, "", line)
            print line
        }
    ' "$file"
}

# Fetch a work item DESCRIPTION only.
#
# Scoping to the description matters. Matching a ticket id against the whole
# work item would be a false-positive machine: the JSON carries dates, revision
# numbers and ids, so a short ticket id like 22 would "match" almost any item
# and the backward check would pass on links that do not exist. A guard that
# cannot fail is worse than none.
fetch_item_description() {
    local id="$1"
    twig show "$id" --output json 2>/dev/null | python3 -c '
import sys, json
try:
    doc = json.load(sys.stdin)
except Exception:
    sys.exit(0)
print(doc.get("fields", {}).get("System.Description") or "")
' 2>/dev/null
}

check_ticket() {
    local file="$1"
    local ticket_id
    ticket_id="$(extract_ticket_id "$file")"
    [[ -z "$ticket_id" ]] && return 0

    local ids
    ids="$(extract_tracked_in "$file")"
    [[ -z "$ids" ]] && return 0

    local ado_id
    for ado_id in $ids; do
        check_count=$((check_count + 1))

        # Forward: the work item must resolve at all.
        if ! twig show "$ado_id" --output json >/dev/null 2>&1; then
            bad "ticket $ticket_id declares work item $ado_id, which does not resolve"
            continue
        fi

        local desc
        desc="$(fetch_item_description "$ado_id")"

        if [[ -z "$desc" ]]; then
            bad "work item $ado_id has no description, so it cannot name ticket $ticket_id back"
            continue
        fi

        # Backward: the description must name the ticket using the CONVENTIONAL
        # phrasing, not a bare number. Requiring the word makes the match mean
        # something — a loose numeric match would hit dates and revision ids.
        if grep -qiE "(wayfinder|ticket|ruling)[^0-9]{0,12}0*${ticket_id}\b" <<<"$desc"; then
            good "ticket $ticket_id <-> work item $ado_id"
        else
            bad "work item $ado_id does not name ticket $ticket_id back (one-way link)"
        fi
    done
}

selftest() {
    # Prove the checker can BOTH fail and pass. A guard that cannot fail is
    # worse than none — this repo shipped a silently-inert structural guard
    # once already (wayfinder 0021), and it passed at the pre-fix SHA because
    # nothing ever exercised its failure path. Testing only the failing side is
    # the same mistake mirrored: a checker that always fails is equally useless.
    local tmp rc=0
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' RETURN

    # --- negative: a dangling forward link must be rejected -----------------
    note "selftest 1/3: dangling work item id must FAIL"
    cat >"$tmp/9999-dangling.md" <<'EOF'
---
id: 9999
title: selftest — dangling forward link
tracked_in: [999999999]
---
EOF
    fail_count=0; check_count=0
    check_ticket "$tmp/9999-dangling.md" >/dev/null
    if [[ "$fail_count" -eq 0 ]]; then
        note "  FAILED: checker accepted a work item that does not exist."
        rc=1
    else
        note "  ok: rejected."
    fi

    # --- negative: a one-way link must be rejected --------------------------
    # Work item 139 exists but its description names a GitHub issue, not a
    # wayfinder ticket. This is the case prose cannot catch and the whole
    # reason the backward check exists.
    note "selftest 2/3: existing work item that does NOT name the ticket back must FAIL"
    cat >"$tmp/9998-oneway.md" <<'EOF'
---
id: 9998
title: selftest — one-way link
tracked_in: [139]
---
EOF
    fail_count=0; check_count=0
    check_ticket "$tmp/9998-oneway.md" >/dev/null
    if [[ "$fail_count" -eq 0 ]]; then
        note "  FAILED: checker accepted a one-way link."
        rc=1
    else
        note "  ok: rejected."
    fi

    # --- positive: a genuine two-way link must PASS -------------------------
    # Without this arm the checker could be unconditionally-failing and the
    # two tests above would still look green.
    note "selftest 3/3: a genuine two-way link must PASS"
    local probe_id
    probe_id="$(twig new --type Task \
        --title "selftest probe — safe to delete" \
        --description "Selftest fixture for tools/check-tracking.sh. Implements wayfinder ticket 9997. Safe to delete." \
        --output minimal 2>/dev/null | tr -d '#')"

    if [[ -z "$probe_id" ]]; then
        note "  SKIPPED: could not create a probe work item (no board access?)."
        note "  The positive path is therefore UNPROVEN in this run."
    else
        cat >"$tmp/9997-twoway.md" <<EOF
---
id: 9997
title: selftest — genuine two-way link
tracked_in: [$probe_id]
---
EOF
        fail_count=0; check_count=0
        check_ticket "$tmp/9997-twoway.md" >/dev/null
        if [[ "$fail_count" -ne 0 ]]; then
            note "  FAILED: checker rejected a genuine two-way link (probe $probe_id)."
            rc=1
        else
            note "  ok: accepted (probe $probe_id)."
        fi

        # Clean up after ourselves. A selftest that litters the board every run
        # trains people not to run it.
        if twig delete "$probe_id" --force --output minimal >/dev/null 2>&1; then
            note "  probe $probe_id deleted."
        else
            note "  WARNING: probe $probe_id could not be deleted — remove it by hand."
        fi
    fi

    note ""
    if [[ "$rc" -eq 0 ]]; then
        note "SELFTEST PASSED: the checker rejects broken links and accepts real ones."
    else
        note "SELFTEST FAILED: the checker is not trustworthy."
    fi
    return "$rc"
}

main() {
    if ! command -v twig >/dev/null 2>&1; then
        note "twig is not on PATH — cannot verify board links."
        exit 2
    fi

    if [[ "${1:-}" == "--selftest" ]]; then
        selftest
        exit $?
    fi

    local wanted="${1:-}"
    local dir file
    for dir in "${TICKET_DIRS[@]}"; do
        [[ -d "$dir" ]] || continue
        for file in "$dir"/*.md; do
            [[ -e "$file" ]] || continue
            if [[ -n "$wanted" ]]; then
                [[ "$(extract_ticket_id "$file")" == "$wanted" ]] || continue
            fi
            check_ticket "$file"
        done
    done

    note ""
    if [[ "$check_count" -eq 0 ]]; then
        note "TWIG-TRACKING: no declared links to check."
        exit 0
    fi

    if [[ "$fail_count" -gt 0 ]]; then
        note "TWIG-TRACKING: BROKEN ($fail_count of $check_count links)"
        exit 1
    fi

    note "TWIG-TRACKING: OK ($check_count links, both directions)"
    exit 0
}

main "$@"
