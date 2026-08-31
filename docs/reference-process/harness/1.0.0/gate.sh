#!/usr/bin/env bash
# AB#847 / AB#733 §5 — harness gate check.
#
# Reads ONLY the committed evidence bundle (JSON is authoritative; PNGs are
# secondary and are never read here) and asserts the pass criterion for each of
# the ten surfaces in AB#733 §4.3. Exits non-zero on the first failing surface
# so a profile-version bump cannot land on a red harness.
#
# Usage: ./gate.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EV="$HERE/evidence"
FX="$HERE/fixtures.json"

INIT=$(jq -r .items.INIT "$FX"); INV=$(jq -r .items.INV "$FX")
FEAT=$(jq -r .items.FEAT "$FX"); BUG=$(jq -r .items.BUG "$FX")
TA=$(jq -r .items.TA "$FX"); TB=$(jq -r .items.TB "$FX"); TC=$(jq -r .items.TC "$FX")

fail=0
pass() { printf '  PASS  %-42s %s\n' "$1" "${2:-}"; }
bad()  { printf '  FAIL  %-42s %s\n' "$1" "${2:-}"; fail=1; }

check() { # $1=label  $2=jq filter  $3=file  (filter must yield true/false)
  local got
  got=$(jq -r "$2" "$EV/$3" 2>/dev/null)
  if [[ "$got" == "true" ]]; then pass "$1"; else bad "$1" "expected true, got '$got' ($3)"; fi
}

echo "Twig reference-process harness gate — $(jq -r .project "$FX")"
echo

# 00 — baseline present and bound to the right process/project.
check "00 baseline pins process+project" \
  '(.header.processId=="'"$(jq -r .processId "$FX")"'") and (.header.project=="'"$(jq -r .project "$FX")"'")' \
  00-sandbox-baseline.json

# 01 — Initiative sits on the portfolio backlog.
check "01 Initiative on portfolio backlog" \
  '.present and (.backlogOrder|index('"$INIT"')!=null)' 01-initiative-backlog.json

# 02-04 — the three requirement types sit on the Requirements backlog.
check "02 Investigation on requirement backlog" \
  '.present and (.backlogOrder|index('"$INV"')!=null)' 02-investigation-work.json
check "03 Feature on requirement backlog" \
  '.present and (.backlogOrder|index('"$FEAT"')!=null)' 03-feature-work.json
check "04 Bug on requirement backlog" \
  '.present and (.backlogOrder|index('"$BUG"')!=null)' 04-bug-work.json

# 05 — every Task is committed to the sprint iteration.
check "05 Tasks on the sprint board" \
  '.allPresent and ([.items[]|.type=="Task"]|all)' 05-task-sprint.json

# 06 — parent/child renders as decomposition, with the exact expected shape.
check "06 hierarchy is Initiative>Requirements>Tasks" \
  '([.items[]|select(.id=='"$INV"' or .id=='"$FEAT"' or .id=='"$BUG"')
      |[.relations[]|select(.rel=="System.LinkTypes.Hierarchy-Reverse")|.url|split("/")|last|tonumber]
      |index('"$INIT"')!=null] | all)
   and
   ([.items[]|select(.id=='"$TA"' or .id=='"$TB"' or .id=='"$TC"')
      |[.relations[]|select(.rel=="System.LinkTypes.Hierarchy-Reverse")|.url|split("/")|last|tonumber]
      |index('"$FEAT"')!=null] | all)' 06-hierarchy-links.json

# 07 — predecessor/successor renders as a real dependency, in both directions.
check "07 dependency A->B both directions" \
  '([.items[]|select(.id=='"$TA"')|[.relations[]|select(.rel=="System.LinkTypes.Dependency-Forward")|.url|split("/")|last|tonumber]|index('"$TB"')!=null]|all)
   and
   ([.items[]|select(.id=='"$TB"')|[.relations[]|select(.rel=="System.LinkTypes.Dependency-Reverse")|.url|split("/")|last|tonumber]|index('"$TA"')!=null]|all)' \
  07-predecessor-successor.json

# 08 — related renders nondirectionally (present on both endpoints).
check "08 related is nondirectional" \
  '([.items[]|select(.id=='"$INV"')|[.relations[]|select(.rel=="System.LinkTypes.Related")|.url|split("/")|last|tonumber]|index('"$FEAT"')!=null]|all)
   and
   ([.items[]|select(.id=='"$FEAT"')|[.relations[]|select(.rel=="System.LinkTypes.Related")|.url|split("/")|last|tonumber]|index('"$INV"')!=null]|all)' \
  08-related-links.json

# 09 — artifact link present and shaped as a Git branch ref.
check "09 artifact link is a Git branch ref" \
  '([.items[]|[.relations[]|select(.rel=="ArtifactLink")|.url]|map(startswith("vstfs:///Git/Ref/"))|any]|all)' \
  09-artifact-links.json

# 10 — backlog rank preserved across publish + link.
if [[ -f "$EV/10-rank-before.json" && -f "$EV/10-rank-after.json" ]]; then
  diff <(jq -S '{portfolioBacklogOrder,requirementBacklogOrder}' "$EV/10-rank-before.json") \
       <(jq -S '{portfolioBacklogOrder,requirementBacklogOrder}' "$EV/10-rank-after.json") \
       > "$EV/10-rank-diff.txt" 2>&1
  if [[ -s "$EV/10-rank-diff.txt" ]]; then
    bad "10 rank preserved across publish+link" "10-rank-diff.txt is non-empty"
  else
    pass "10 rank preserved across publish+link" "diff empty"
  fi
else
  bad "10 rank preserved across publish+link" "missing before/after snapshot"
fi

echo
if [[ $fail -eq 0 ]]; then
  echo "GATE: PASS — all ten surfaces satisfied."
else
  echo "GATE: FAIL — see the FAIL rows above."
fi
exit $fail
