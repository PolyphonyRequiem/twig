#!/usr/bin/env bash
# ADO #154 mutation harness.
#
# Proves the guards added for "return work item links for a SET of items" are NOT hollow, by
# patching the implementation WRONG in several ways and requiring the suite to go red BY NAME.
#
# Reports three outcomes per mutant, never two:
#   KILLED       — the expected arms failed, and `error CS` count is 0.
#   SURVIVED     — the suite stayed green. The tests are weaker than they look.
#   DID NOT COMPILE — `error CS` > 0. This is NOT a kill: a non-compiling mutant reds
#                     identically to a caught one, and banking it as a pass is the same
#                     false green this repo's test tooling exists to abolish.
#
# Leaves no trace: every mutated file is snapshotted and restored, and the script asserts a
# clean `git status --porcelain` before reporting.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

LOGDIR="artifacts/ab154-mutants"
mkdir -p "$LOGDIR"

MAPPER="src/Twig.Infrastructure/Ado/AdoRestClient.cs"
GRAPH="src/Twig.Domain/ReadModels/WorkItemGraph.cs"
REPO_FILE="src/Twig.Infrastructure/Persistence/SqliteWorkItemLinkRepository.cs"
SYNC="src/Twig.Domain/Services/Sync/SyncCoordinator.cs"
SHOW="src/Twig/Commands/ShowCommand.cs"

ALL_FILES=("$MAPPER" "$GRAPH" "$REPO_FILE" "$SYNC" "$SHOW")

snapshot() {
  SNAPDIR="$(mktemp -d)"
  for f in "${ALL_FILES[@]}"; do
    mkdir -p "$SNAPDIR/$(dirname "$f")"
    cp "$f" "$SNAPDIR/$f"
  done
}

restore() {
  for f in "${ALL_FILES[@]}"; do
    cp "$SNAPDIR/$f" "$f"
  done
}

cleanup() {
  [[ -n "${SNAPDIR:-}" ]] && restore && rm -rf "$SNAPDIR"
}
trap cleanup EXIT

PASS=0
FAIL=0

# run_mutant <name> <suite> <expected-arm-substring> ; mutation already applied
run_mutant() {
  local name="$1" suite="$2" expect="$3"
  local log="$LOGDIR/$name.log"

  tools/run-tests.sh "$suite" > "$log" 2>&1
  local cs
  cs="$(grep -c 'error CS' "$log")"

  if [[ "$cs" -gt 0 ]]; then
    echo "  DID NOT COMPILE  $name  ($cs compile errors — NOT a kill) [log: $log]"
    FAIL=$((FAIL + 1))
    return
  fi

  # 🔴 Extract failing arms from xUnit's "[FAIL]" lines, NOT from a "Failed <name>" summary.
  # run-tests.sh surfaces the former; grepping for the latter returned EMPTY on a genuinely
  # killed mutant and reported SURVIVED for all 8 on this harness's first run. An empty arm
  # list is ambiguous between "stayed green" and "I cannot parse this", so the OVERALL verdict
  # below is checked as an independent second signal rather than trusting the arm list alone.
  local arms
  arms="$(grep -oE '[A-Za-z_.]+Tests\.[A-Za-z_]+ \[FAIL\]' "$log" | sed 's/ \[FAIL\]//' | sort -u)"

  local overall
  overall="$(grep -oE 'TWIG-VERDICT OVERALL: [A-Z]+' "$log" | tail -1)"

  if [[ "$overall" != "TWIG-VERDICT OVERALL: FAILED" ]]; then
    echo "  SURVIVED         $name  (suite stayed green — the guard is hollow) [log: $log]"
    FAIL=$((FAIL + 1))
    return
  fi

  if [[ -z "$arms" ]]; then
    # The run failed but named no test. That is a harness/toolchain verdict, not a kill —
    # banking it as one would be exactly the false green this script exists to measure.
    echo "  NO NAMED ARMS    $name  (run failed but named no test — NOT a kill) [log: $log]"
    FAIL=$((FAIL + 1))
    return
  fi

  if echo "$arms" | grep -q "$expect"; then
    echo "  KILLED           $name"
    echo "$arms" | sed 's/^/                     red: /'
    PASS=$((PASS + 1))
  else
    echo "  WRONG ARMS       $name  (expected an arm matching '$expect') [log: $log]"
    echo "$arms" | sed 's/^/                     red: /'
    FAIL=$((FAIL + 1))
  fi
}

echo "ADO #154 mutation harness"
echo "========================="

# ── M1: the headline fix reverted — batch path discards relations again ─────────
snapshot
python3 - <<'PY'
import re
p = "src/Twig.Infrastructure/Ado/AdoRestClient.cs"
s = open(p).read()
old = "var (snapshot, itemLinks) = AdoResponseMapper.MapToSnapshotWithLinks(dto, lookup);"
new = "var snapshot = AdoResponseMapper.MapToSnapshot(dto, lookup); var itemLinks = System.Array.Empty<Twig.Domain.ValueObjects.WorkItemLink>();"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M1-batch-discards-relations" Infrastructure "AdoRestClientBatchLinksTests"
restore

# ── M2: graph hands every item the WHOLE edge set ───────────────────────────────
python3 - <<'PY'
p = "src/Twig.Domain/ReadModels/WorkItemGraph.cs"
s = open(p).read()
old = """        return _linksBySource.TryGetValue(workItemId, out var links)
            ? links
            : Array.Empty<WorkItemLink>();"""
new = "        return Links;"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M2-graph-returns-all-edges-per-item" Domain "WorkItemGraphTests"
restore

# ── M3: graph FILTERS OUT edges leaving the set (the documented contract) ───────
python3 - <<'PY'
p = "src/Twig.Domain/ReadModels/WorkItemGraph.cs"
s = open(p).read()
old = "        var edges = links ?? Array.Empty<WorkItemLink>();"
new = """        var edges = links ?? Array.Empty<WorkItemLink>();
        {
            var ids = new HashSet<int>();
            foreach (var i in items) ids.Add(i.Id);
            var kept = new List<WorkItemLink>();
            foreach (var l in edges) if (ids.Contains(l.TargetId)) kept.Add(l);
            edges = kept;
        }"""
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M3-graph-drops-outward-edges" Domain "WorkItemGraphTests"
restore

# ── M4: repository ignores the id filter and returns the whole table ────────────
python3 - <<'PY'
p = "src/Twig.Infrastructure/Persistence/SqliteWorkItemLinkRepository.cs"
s = open(p).read()
old = '            $"SELECT source_id, target_id, link_type FROM work_item_links WHERE source_id IN ({string.Join(", ", placeholders)});";'
new = '            $"SELECT source_id, target_id, link_type FROM work_item_links WHERE @id0 IS NOT NULL;";'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M4-repo-ignores-id-filter" Infrastructure "SqliteWorkItemLinkSetReadTests"
restore

# ── M5: sync skips writing ids that came back with no edges (stale edges survive) ──
python3 - <<'PY'
p = "src/Twig.Domain/Services/Sync/SyncCoordinator.cs"
s = open(p).read()
old = """                var itemLinks = bySource.TryGetValue(item.Id, out var found)
                    ? (IReadOnlyList<WorkItemLink>)found
                    : Array.Empty<WorkItemLink>();
                await _linkRepo.SaveLinksAsync(item.Id, itemLinks, ct);"""
new = """                if (!bySource.TryGetValue(item.Id, out var found)) continue;
                await _linkRepo.SaveLinksAsync(item.Id, (IReadOnlyList<WorkItemLink>)found, ct);"""
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M5-sync-skips-edgeless-ids" Domain "SyncCoordinatorSetLinksTests"
restore

# ── M6: CLI emits links only when non-empty (missing-vs-empty ambiguity) ────────
python3 - <<'PY'
p = "src/Twig/Commands/ShowCommand.cs"
s = open(p).read()
old = """            cells["links"] = new RenderCell(string.Empty, new RenderValue.Array(linkCells));
            cells["relations"] = new RenderCell(string.Empty, new RenderValue.Array(relationCells));"""
new = """            if (linkCells.Count > 0)
            {
                cells["links"] = new RenderCell(string.Empty, new RenderValue.Array(linkCells));
                cells["relations"] = new RenderCell(string.Empty, new RenderValue.Array(relationCells));
            }"""
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M6-cli-omits-empty-link-arrays" Cli "ShowBatchLinksTests"
restore

# ── M7: CLI attributes the whole set's edges to every item ──────────────────────
python3 - <<'PY'
p = "src/Twig/Commands/ShowCommand.cs"
s = open(p).read()
old = "            var itemLinks = graph.GetLinks(item.Id);"
new = "            var itemLinks = graph.Links;"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M7-cli-misattributes-edges" Cli "ShowBatchLinksTests"
restore

# ── M8: CLI reverts to one link call per id ─────────────────────────────────────
python3 - <<'PY'
p = "src/Twig/Commands/ShowCommand.cs"
s = open(p).read()
old = "            try { links = await linkRepo.GetLinksForSetAsync(foundIds, ct); }"
new = """            try
            {
                var acc = new List<WorkItemLink>();
                foreach (var fid in foundIds) acc.AddRange(await linkRepo.GetLinksAsync(fid, ct));
                links = acc;
            }"""
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M8-cli-one-call-per-id" Cli "ShowBatchLinksTests"
restore

echo
echo "KILLED: $PASS   NOT KILLED: $FAIL"

# Leave-no-trace check — a mutation harness that dirties the tree is its own defect.
#
# 🔴 Diff against the SNAPSHOT, not against git HEAD. This card's implementation is still
# uncommitted, so `git status --porcelain` reports every implementation file as modified and a
# brand-new file as untracked — none of which is mutation residue. The first version of this
# check did exactly that and exited 2 on a clean run, which is a false RED: it accuses the
# harness of damage it did not do, and a check that cries wolf gets switched off.
DIRTY=""
for f in "${ALL_FILES[@]}"; do
  if ! cmp -s "$SNAPDIR/$f" "$f"; then
    DIRTY="$DIRTY$f"$'\n'
  fi
done

if [[ -n "$DIRTY" ]]; then
  echo "🔴 MUTATION NOT REVERTED (differs from pre-run snapshot):"
  echo "$DIRTY"
  exit 2
fi
echo "all mutations reverted; files byte-identical to the pre-run snapshot."

[[ "$FAIL" -eq 0 ]] || exit 1
