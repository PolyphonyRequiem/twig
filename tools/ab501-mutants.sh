#!/usr/bin/env bash
# ADO #501 mutation harness.
#
# Proves the show-batch positional guards are NOT hollow, by patching the implementation
# WRONG in several ways and requiring the suite to go red BY NAME.
#
# Reports four outcomes per mutant, never two:
#   KILLED          — the expected arms failed, and the compile-error count is 0.
#   SURVIVED        — the suite stayed green. The tests are weaker than they look.
#   DID NOT COMPILE — a compile error. NOT a kill: a non-compiling mutant reds identically to a
#                     caught one, and banking it as a pass is the false green this repo's test
#                     tooling exists to abolish.
#   NO NAMED ARMS   — the run failed but named no test. A harness/toolchain verdict, not a kill.
#
# Copied from tools/ab216-mutants.sh, which is the current best version and carries every fix
# earlier harnesses needed:
#   1. arm extraction must grep xUnit's `Name [FAIL]`, not a `Failed <name>` summary line, and
#      must NOT require " [FAIL]" directly after the method name (a parameterised theory prints
#      its inline data in between). An EMPTY arm list is ambiguous rather than SURVIVED.
#   2. two concurrent runs on one working tree destroy each other — hence the flock and trap.
#   3. the BASELINE must be proven green before mutating: one unrelated red arm makes every
#      mutant "red" and masks the real ones.
#   4. 🔴 match `error CAF` as well as `error CS`. ConsoleAppFramework's generator emits
#      `CAF015` when a <param> documents a parameter the method no longer has — which is
#      exactly what M1 below would trip if it removed the parameter and left the doc comment.
#      That cost AB#216 a whole sweep.
#
# 🔴 AB#501 additions:
#   M5/M6 are the guards-masking-each-other pair, mandated by AGENTS.md. Two refusals live on
#   this command's no-argument path family — the ShowBatch usage refusal and the workspace
#   refusal — and M6 leaves BOTH firing while swapping only the WORDING, so only a test
#   asserting each guard's DISTINCT message can kill it.
#   M7 is the EQUIVALENT-MUTANT row, kept deliberately rather than deleted: see its comment.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

LOCKFILE="$REPO_ROOT/artifacts/.ab501-mutants.lock"
mkdir -p "$(dirname "$LOCKFILE")"
exec 9>"$LOCKFILE"
if ! flock -n 9; then
  echo "🔴 another ab501-mutants run holds the lock ($LOCKFILE) — refusing to start."
  echo "   Two runs would mutate and restore the same files concurrently."
  echo
  echo "   🔴 BUT CHECK WHETHER A RUN IS ACTUALLY ALIVE BEFORE WAITING. A COMPLETED run can"
  echo "   leave the lock held, because child processes inherit the open fd and outlive the"
  echo "   harness — MSBuild's reusable node daemons, and the 'sleep 3600' that"
  echo "   BuildFixtureRunProcessTests deliberately spawns as its orphan-reaping probe."
  echo "   flock is held until the LAST holder closes it."
  echo
  echo "   Diagnose, then clear:"
  echo "     ps -eo pid,etime,args --no-headers | grep -E '[a]b501-mutants|[d]otnet test'"
  echo "     fuser -v $LOCKFILE       # holders: sleep/MSBuild = orphans, not a live run"
  echo "     fuser -k $LOCKFILE       # safe ONLY when the ps check above found nothing"
  exit 3
fi

LOGDIR="artifacts/ab501-mutants"
mkdir -p "$LOGDIR"

PROGRAM="src/Twig/Program.cs"
GUARD="src/Twig/Commands/StrayPositionalGuard.cs"
EXAMPLES="src/Twig/CommandExamples.cs"

# 🔴 Every file ANY mutant touches must be listed here, or `restore` leaves that mutant
# resident in the working tree — where it reads as a defect in correct code rather than as
# harness residue, and the leave-no-trace check at the bottom cannot see it either.
ALL_FILES=("$PROGRAM" "$GUARD" "$EXAMPLES")

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
trap cleanup EXIT INT TERM

PASS=0
FAIL=0

run_mutant() {
  local name="$1" expect="$2"
  local log="$LOGDIR/$name.log"

  tools/run-tests.sh Cli > "$log" 2>&1

  local cs
  cs="$(grep -cE 'error (CS|CAF)[0-9]+' "$log")"

  if [[ "$cs" -gt 0 ]]; then
    echo "  DID NOT COMPILE  $name  ($cs compile errors — NOT a kill) [log: $log]"
    FAIL=$((FAIL + 1))
    return
  fi

  local arms
  arms="$(grep -E '\[FAIL\]' "$log" \
    | grep -oE '[A-Za-z_]+Tests\.[A-Za-z_]+' \
    | sort -u)"

  local overall
  overall="$(grep -oE 'TWIG-VERDICT OVERALL: [A-Z]+' "$log" | tail -1)"

  if [[ "$overall" != "TWIG-VERDICT OVERALL: FAILED" ]]; then
    echo "  SURVIVED         $name  (suite stayed green — the guard is hollow) [log: $log]"
    FAIL=$((FAIL + 1))
    return
  fi

  if [[ -z "$arms" ]]; then
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

echo "ADO #501 mutation harness"
echo "========================="

echo
echo "── baseline (unmutated tree) ──"
tools/run-tests.sh Cli > "$LOGDIR/M0-baseline.log" 2>&1
BASE_OVERALL="$(grep -oE 'TWIG-VERDICT OVERALL: [A-Z]+' "$LOGDIR/M0-baseline.log" | tail -1)"
if [[ "$BASE_OVERALL" != "TWIG-VERDICT OVERALL: PASSED" ]]; then
  echo "🔴 BASELINE IS NOT GREEN ($BASE_OVERALL) — refusing to mutate."
  grep -E '\[FAIL\]' "$LOGDIR/M0-baseline.log" \
    | grep -oE '[A-Za-z_]+Tests\.[A-Za-z_]+' | sort -u | sed 's/^/   red at baseline: /'
  echo "   [log: $LOGDIR/M0-baseline.log]"
  exit 4
fi
echo "  baseline PASSED — mutation verdicts are attributable."
echo

snapshot

# ── M1: the positional slot never reaches the parser — the headline fix reverted ─
# Mutates the DECLARATION, so the parse layer is exercised the way a real regression would
# arrive. This is the one the whole card exists to prevent.
#
# 🔴 The doc-comment line must be removed TOO, or ConsoleAppFramework fails the build with
# `error CAF015` and the mutant proves nothing about the tests.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
doc = '    /// <param name="batchArg">Comma-separated work item IDs, positionally: twig show-batch 1234,5678,9012.</param>\n'
assert s.count(doc) == 1
s = s.replace(doc, "")
old = 'public async Task<int> ShowBatch([Argument] string? batchArg = null, string? batch = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)'
new = 'public async Task<int> ShowBatch(string? batch = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)'
assert s.count(old) == 1
s = s.replace(old, new)
old2 = "        var resolved = ResolveBatch(batch, batchArg);"
new2 = "        var resolved = batch;"
assert s.count(old2) == 1
open(p, "w").write(s.replace(old2, new2))
PY
run_mutant "M1-showbatch-loses-its-positional-slot" "BareIdList_IsAcceptedByTheParser"
restore

# ── M2: the positional binds but is DROPPED — accepted, then ignored ────────────
# The subtle direction. Every "was it rejected" arm stays green, because the parser really does
# accept the token; the value simply never reaches the command.
#
# 🔴 This mutant SURVIVED the first sweep, and that survival is why TwigCommands.ResolveBatch
# exists as a pure function. Every production-CLI arm runs with no populated cache, so the
# workspace refusal fires BEFORE the ids are read and two different resolutions emit identical
# bytes — no assertion at that layer can discriminate. The untestability WAS the untestedness,
# so the fix was to extract the rule, not to weaken the mutant.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = "    internal static string? ResolveBatch(string? batch, string? batchArg) => batch ?? batchArg;"
new = "    internal static string? ResolveBatch(string? batch, string? batchArg) => batch ?? (batchArg is null ? null : string.Empty);"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M2-positional-binds-but-is-dropped" "PositionalValue_IsResolvedVerbatim"
restore

# ── M3: resolution order inverted — the POSITIONAL wins over --batch ────────────
# Substitution rather than addition, wearing the shape of a fix. AB#398 regressed
# `edit --field` and `init --org/--project` in exactly this direction.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = "    internal static string? ResolveBatch(string? batch, string? batchArg) => batch ?? batchArg;"
new = "    internal static string? ResolveBatch(string? batch, string? batchArg) => batchArg ?? batch;"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M3-positional-wins-over-named-option" "NamedOption_WinsOverThePositional"
restore

# ── M4: the no-argument refusal reports SUCCESS ────────────────────────────────
# A command that displayed nothing exiting 0 is the false green this card's family is named
# for, and it is what the parser's retired [Required] check used to prevent.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = '            Console.Error.WriteLine("error: Usage: twig show-batch <ids>, or twig show-batch --batch <ids>");\n            return 1;'
new = '            Console.Error.WriteLine("error: Usage: twig show-batch <ids>, or twig show-batch --batch <ids>");\n            return 0;'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M4-no-argument-refusal-exits-zero" "NoIdsAtAll_IsAUsageError_NamingBothSpellings"
restore

# ── M5: the refusal stops naming the POSITIONAL spelling ───────────────────────
# The refusal still fires and still exits 1, so a bare "was it refused" check passes. Only an
# arm asserting the message names both working spellings can kill it.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = '"error: Usage: twig show-batch <ids>, or twig show-batch --batch <ids>"'
new = '"error: Usage: twig show-batch --batch <ids>"'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M5-refusal-omits-the-positional-spelling" "NoIdsAtAll_IsAUsageError_NamingBothSpellings"
restore

# ── M6: WORDING SWAP — both guards still fire, each wearing the other's message ─
# 🔴 AGENTS.md mandates this shape. Killing a guard outright is the easy case; two guards
# masking each other is the failure that costs real time. Here the ShowBatch usage refusal
# adopts the WORKSPACE refusal's wording. Both guards still fire, both still exit 1, and any
# test asserting only "it was refused" stays green against a refusal that now tells the user
# to run `twig init` when their real mistake was omitting the ids.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = '"error: Usage: twig show-batch <ids>, or twig show-batch --batch <ids>"'
new = '"error: No twig workspace found. Run \'twig init --org <org> --project <project>\' to create one."'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M6-refusal-wears-the-workspace-guards-wording" "NoIdsAtAll_IsAUsageError_NamingBothSpellings"
restore

# ── M7: show-batch GAINS a StrayPositionalGuard arity entry ────────────────────
# AB#501's ruling asked for this entry; measurement rejected it. The entry makes the guard
# suggest `twig show-batch "154 140"`, which PARSES, exits 0, and returns nothing, because the
# value splits on COMMAS. That is a hint pointing at a silent false green — the exact defect
# StrayPositionalGuard's own summary forbids porting it into. Killed by the exclusion pin.
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = '        ["note"] = 1,'
new = '        ["show-batch"] = 1,\n        ["note"] = 1,'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M7-showbatch-gains-a-misleading-arity-entry" "ShowBatch_IsDeliberatelyAbsent_BecauseItsPositionalIsACommaList"
restore

# ── M8: the documented positional example is deleted ───────────────────────────
# Help and parser drift apart in the direction tools/positional-drift.py exists to catch: the
# slot exists but nothing tells a user it does.
#
# 🔴 This mutant SURVIVED TWICE, and the second survival found a defect in the TEST rather than
# a missing one. The generated parser emits its own `Arguments:` block from the SLOT, so a help
# arm asserting that block stays green with no example present. The first replacement arm was a
# TAUTOLOGY: it grepped the whole output for `twig show-batch 1234,5678,9012`, which also
# appears in the positional's XML doc summary that the parser prints in `Arguments:`. Killed now
# by scoping the assertion to the text AFTER `Examples:`, with a trailing space so the
# `--batch 1234,5678,9012` examples cannot satisfy it.
python3 - <<'PY'
p = "src/Twig/CommandExamples.cs"
s = open(p).read()
old = '            "twig show-batch 1234,5678,9012         Batch lookup work items",\n'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, ""))
PY
run_mutant "M8-positional-example-deleted" "Help_DocumentsTheBareIdListSpelling_InTheExamplesBlock"
restore

echo
echo "KILLED: $PASS   NOT KILLED: $FAIL"

# Leave-no-trace check — diff against the SNAPSHOT, not against git HEAD. This card's
# implementation may be uncommitted, so `git status` would report every touched file as
# modified and a clean run would exit non-zero. A check that cries wolf gets switched off.
DIRTY=""
for f in "${ALL_FILES[@]}"; do
  if ! cmp -s "$SNAPDIR/$f" "$f"; then
    DIRTY="$DIRTY$f"$'\n'
  fi
done

if [[ -n "$DIRTY" ]]; then
  echo "🔴 working tree NOT restored — mutation residue in:"
  echo "$DIRTY" | sed '/^$/d' | sed 's/^/   /'
  exit 2
fi
echo "working tree restored (cmp against pre-run snapshot)."

[[ "$FAIL" -eq 0 ]] || exit 1
