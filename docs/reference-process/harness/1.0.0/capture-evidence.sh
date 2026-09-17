#!/usr/bin/env bash
# AB#847 / AB#733 §4.3 — capture the reference-process harness evidence surfaces.
#
# Usage:
#   ./capture-evidence.sh rank <before|after>   # surface 10 only
#   ./capture-evidence.sh surfaces              # surfaces 01-09
#
# Every artifact is written to ./evidence/. JSON is authoritative; the optional
# PNGs alongside are human-consumable secondary evidence and are not produced
# here (see README "Screenshots").
#
# Requires: az (logged in), jq, twig, and a Twig workspace bound to the Sandbox
# project exported as SANDBOX_WS.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EV="$HERE/evidence"
FX="$HERE/fixtures.json"
mkdir -p "$EV"

ADO_RESOURCE=499b84ac-1321-427f-aa17-267ca6975798
SANDBOX_WS="${SANDBOX_WS:-$HOME/.twig/harness/twig-reference/sandbox-ws}"

ORG=$(jq -r .organization "$FX")
PROJECT=$(jq -r .project "$FX")
TEAM=$(jq -r .team "$FX")
ITER_ID=$(jq -r .iteration.id "$FX")
BL_PORTFOLIO=$(jq -r .backlogs.portfolio "$FX")
BL_REQUIREMENT=$(jq -r .backlogs.requirement "$FX")

BASE="https://dev.azure.com/$ORG"
TEAM_ENC=${TEAM// /%20}

id_of() { jq -r ".items.$1" "$FX"; }

rest() { az rest --method get --resource "$ADO_RESOURCE" --url "$1" 2>/dev/null; }

# Ordered backlog membership for a backlog id, as {id, order, stackRank, type}.
backlog_rows() {
  local backlog_id="$1"
  rest "$BASE/$PROJECT/$TEAM_ENC/_apis/work/backlogs/$backlog_id/workItems?api-version=7.1" \
    | jq -c '[.workItems[]?.target.id]'
}

# Full field detail for a set of ids, order preserved as given.
items_detail() {
  local ids="$1"
  rest "$BASE/$PROJECT/_apis/wit/workitems?ids=$ids&\$expand=relations&api-version=7.1" \
    | jq '[.value[] | {
        id,
        type: .fields."System.WorkItemType",
        state: .fields."System.State",
        title: .fields."System.Title",
        iterationPath: .fields."System.IterationPath",
        stackRank: .fields."Microsoft.VSTS.Common.StackRank",
        backlogPriority: .fields."Microsoft.VSTS.Common.BacklogPriority",
        relations: [ .relations[]? | {rel, url, name: .attributes.name} ]
      }] | sort_by(.id)'
}

surface() { # $1=file  $2=jq-built payload on stdin
  cat > "$EV/$1"
  echo "  wrote evidence/$1"
}

cmd_rank() {
  local phase="$1"
  case "$phase" in before|after) ;; *) echo "phase must be before|after" >&2; exit 2;; esac
  local all
  all=$(jq -r '[.items[]] | join(",")' "$FX")
  jq -n \
    --arg phase "$phase" \
    --argjson portfolio "$(backlog_rows "$BL_PORTFOLIO")" \
    --argjson requirement "$(backlog_rows "$BL_REQUIREMENT")" \
    --argjson items "$(items_detail "$all" | jq '[.[] | {id, type, stackRank, backlogPriority}]')" \
    '{
       kind: "rankSnapshot",
       phase: $phase,
       portfolioBacklogOrder: $portfolio,
       requirementBacklogOrder: $requirement,
       items: $items
     }' | surface "10-rank-$phase.json"
}

cmd_surfaces() {
  local init inv feat bug ta tb tc
  init=$(id_of INIT); inv=$(id_of INV); feat=$(id_of FEAT); bug=$(id_of BUG)
  ta=$(id_of TA); tb=$(id_of TB); tc=$(id_of TC)

  local portfolio requirement
  portfolio=$(backlog_rows "$BL_PORTFOLIO")
  requirement=$(backlog_rows "$BL_REQUIREMENT")

  # 01 — Initiative on the portfolio (Initiatives/Epics) backlog
  jq -n --argjson order "$portfolio" --arg id "$init" \
     --argjson detail "$(items_detail "$init")" \
     '{kind:"backlogMembership", surface:"01-initiative-backlog",
       backlog:"portfolio", expectedId:($id|tonumber),
       backlogOrder:$order, present: ($order | index($id|tonumber) != null),
       items:$detail}' | surface "01-initiative-backlog.json"

  # 02/03/04 — the three requirement types on the Requirements backlog
  local n f
  for pair in "02-investigation-work:$inv" "03-feature-work:$feat" "04-bug-work:$bug"; do
    n=${pair%%:*}; f=${pair##*:}
    jq -n --argjson order "$requirement" --arg id "$f" \
       --argjson detail "$(items_detail "$f")" \
       '{kind:"backlogMembership", surface:$ENV.SURFACE,
         backlog:"requirement", expectedId:($id|tonumber),
         backlogOrder:$order, present: ($order | index($id|tonumber) != null),
         items:$detail}' SURFACE="$n" | surface "$n.json"
  done

  # 05 — Tasks on the sprint board
  jq -n \
    --argjson iter "$(rest "$BASE/$PROJECT/$TEAM_ENC/_apis/work/teamsettings/iterations/$ITER_ID/workitems?api-version=7.1" | jq -c '[.workItemRelations[]?.target.id]')" \
    --argjson expected "[$ta,$tb,$tc]" \
    --argjson detail "$(items_detail "$ta,$tb,$tc")" \
    '{kind:"sprintMembership", surface:"05-task-sprint",
      iterationWorkItems:$iter, expectedIds:$expected,
      allPresent: ([$expected[] | . as $e | $iter | index($e) != null] | all),
      items:$detail}' | surface "05-task-sprint.json"

  # 06 — native parent/child hierarchy
  jq -n --argjson detail "$(items_detail "$init,$inv,$feat,$bug,$ta,$tb,$tc")" \
    '{kind:"hierarchy", surface:"06-hierarchy-links", items:$detail}' \
    | surface "06-hierarchy-links.json"

  # 07 — predecessor/successor
  jq -n --argjson detail "$(items_detail "$ta,$tb")" \
    '{kind:"dependency", surface:"07-predecessor-successor", items:$detail}' \
    | surface "07-predecessor-successor.json"

  # 08 — related
  jq -n --argjson detail "$(items_detail "$inv,$feat")" \
    '{kind:"related", surface:"08-related-links", items:$detail}' \
    | surface "08-related-links.json"

  # 09 — artifact link
  jq -n --argjson detail "$(items_detail "$feat")" \
    '{kind:"artifact", surface:"09-artifact-links", items:$detail}' \
    | surface "09-artifact-links.json"
}

case "${1:-}" in
  rank)     cmd_rank "${2:-}" ;;
  surfaces) cmd_surfaces ;;
  *) echo "usage: $0 {rank <before|after>|surfaces}" >&2; exit 2 ;;
esac
