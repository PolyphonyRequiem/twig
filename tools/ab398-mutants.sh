#!/usr/bin/env bash
# ADO #398 mutation harness.
#
# Proves the stray-positional guard and the restored [Argument] slots are NOT hollow, by
# patching the implementation WRONG in several ways and requiring the suite to go red BY NAME.
#
# Reports four outcomes per mutant, never two:
#   KILLED          — the expected arms failed, and `error CS` count is 0.
#   SURVIVED        — the suite stayed green. The tests are weaker than they look.
#   DID NOT COMPILE — `error CS` > 0. NOT a kill: a non-compiling mutant reds identically to a
#                     caught one, and banking it as a pass is the false green this repo's test
#                     tooling exists to abolish.
#   NO NAMED ARMS   — the run failed but named no test. A harness/toolchain verdict, not a kill.
#
# Copied from tools/ab154-mutants.sh, which carries the two defects that made an earlier
# harness report KILLED: 0/8 against eight loudly-killed mutants:
#   1. arm extraction must grep xUnit's `Name [FAIL]`, not a `Failed <name>` summary line, and
#      an EMPTY arm list is ambiguous rather than SURVIVED;
#   2. the leave-no-trace check must cmp against a pre-run SNAPSHOT, not `git status`, because
#      this card's implementation is uncommitted and would read as mutation residue.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# 🔴 SINGLE-INSTANCE LOCK. This harness mutates the shared working tree, so two concurrent
# runs interleave their mutations AND their restores: one run's `restore` reverts the other's
# mutant mid-test, and both append to the same report. That happened on this card — a run
# believed abandoned was still alive, a second was launched over it, and the merged report
# read `KILLED: 1 NOT KILLED: 11` with verdict lines spliced mid-word and 12 outcomes for 10
# mutants. Every "NOT KILLED" was an artifact of the collision, not a hollow guard.
#
# Same hazard AGENTS.md records for two concurrent `dotnet test` invocations sharing build
# output; here the shared resource is the source tree itself.
LOCKFILE="$REPO_ROOT/artifacts/.ab398-mutants.lock"
mkdir -p "$(dirname "$LOCKFILE")"
exec 9>"$LOCKFILE"
if ! flock -n 9; then
  echo "🔴 another ab398-mutants run holds the lock ($LOCKFILE) — refusing to start."
  echo "   Two runs would mutate and restore the same files concurrently and the report"
  echo "   would be meaningless. Wait for it, or kill it and re-run."
  exit 3
fi

LOGDIR="artifacts/ab398-mutants"
mkdir -p "$LOGDIR"

GUARD="src/Twig/Commands/StrayPositionalGuard.cs"
PROGRAM="src/Twig/Program.cs"

ALL_FILES=("$GUARD" "$PROGRAM")

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

# 🔴 Trap INT/TERM as well as EXIT. A harness killed mid-run (Ctrl-C, a session timeout, a
# reaped background job) otherwise leaves the CURRENT mutant resident in the working tree,
# where it reads as a defect in the implementation rather than as harness residue. That
# happened during this card: an interrupted run left M3 and M4 applied, and the next targeted
# test run failed on `--help` and on every named spelling — two convincing false REDs pointing
# at code that was correct on disk five minutes earlier.
cleanup() {
  [[ -n "${SNAPDIR:-}" ]] && restore && rm -rf "$SNAPDIR"
}
trap cleanup EXIT INT TERM

PASS=0
FAIL=0

# run_mutant <name> <expected-arm-substring> ; mutation already applied
run_mutant() {
  local name="$1" expect="$2"
  local log="$LOGDIR/$name.log"

  tools/run-tests.sh Cli > "$log" 2>&1

  local cs
  cs="$(grep -c 'error CS' "$log")"

  if [[ "$cs" -gt 0 ]]; then
    echo "  DID NOT COMPILE  $name  ($cs compile errors — NOT a kill) [log: $log]"
    FAIL=$((FAIL + 1))
    return
  fi

  # 🔴 Extract failing arms from xUnit's "[FAIL]" lines, NOT from a "Failed <name>" summary.
  # run-tests.sh surfaces the former; grepping for the latter returned EMPTY on a genuinely
  # killed mutant on AB#154 and reported SURVIVED for all 8.
  #
  # 🔴 The arm name must be matched WITHOUT requiring " [FAIL]" to follow it directly. A
  # parameterised xUnit theory prints its inline data between the two:
  #     ...StrayPositionalGuardTests.PositionalsWithinArity_AreRoutedNormally(argv: "…") [FAIL]
  # A pattern anchored on `\.[A-Za-z_]+ \[FAIL\]` matches only FACTS, so a mutant killed
  # exclusively by theory arms produced an empty list and read as NO NAMED ARMS. Verified on
  # this card: M2/M3/M4 were each killed by the correct named theory and reported as unkilled.
  # Anchor on the [FAIL] LINE, then pull the name out of it.
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

echo "ADO #398 mutation harness"
echo "========================="

# 🔴 BASELINE FIRST. A mutation verdict is only meaningful against a green tree: if some
# unrelated arm is already red, EVERY mutant "reds" and the arm extraction reports that
# pre-existing failure instead of the mutation's. On this card an `init` regression was red
# while the first sweep ran, so `InitCommandProductionCliTests` appeared in nearly every
# mutant's arm list and masked the real ones. Establish the control before trusting anything.
echo
echo "── baseline (unmutated tree) ──"
tools/run-tests.sh Cli > "$LOGDIR/M0-baseline.log" 2>&1
BASE_OVERALL="$(grep -oE 'TWIG-VERDICT OVERALL: [A-Z]+' "$LOGDIR/M0-baseline.log" | tail -1)"
if [[ "$BASE_OVERALL" != "TWIG-VERDICT OVERALL: PASSED" ]]; then
  echo "🔴 BASELINE IS NOT GREEN ($BASE_OVERALL) — refusing to mutate."
  grep -oE '[A-Za-z_.]+Tests\.[A-Za-z_]+ \[FAIL\]' "$LOGDIR/M0-baseline.log" \
    | sed 's/ \[FAIL\]//' | sort -u | sed 's/^/   red at baseline: /'
  echo "   Every mutant would 'red' for this reason and the sweep would prove nothing."
  echo "   [log: $LOGDIR/M0-baseline.log]"
  exit 4
fi
echo "  baseline PASSED — mutation verdicts are attributable."
echo

snapshot

# ── M1: the guard never fires — the headline fix reverted ───────────────────────
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = "        if (positionals.Count <= allowed)\n            return null;"
new = "        if (positionals.Count >= 0)\n            return null;"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M1-guard-never-fires" "StrayPositionalGuardTests"
restore

# ── M2: the guard fires one word too early — a false RED on a WORKING spelling ──
# The dangerous direction: `twig note "hello world"` would start failing.
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = "        if (positionals.Count <= allowed)\n            return null;"
new = "        if (positionals.Count < allowed)\n            return null;"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M2-off-by-one-false-red" "PositionalsWithinArity_AreRoutedNormally"
restore

# ── M3: help requests become usage errors (AB#352's lesson reversed) ────────────
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = '        if (args.Any(a => a is "-h" or "--help"))\n            return null;'
new = '        if (args.Any(a => a is "-h" or "--nonexistent-flag"))\n            return null;'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M3-help-becomes-a-usage-error" "HelpRequests_AreNeverAUsageError"
restore

# ── M4: options are treated as positionals — named spellings start failing ──────
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = "            if (token.StartsWith('-'))"
new = "            if (false && token.StartsWith('-'))"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M4-options-counted-as-positionals" "NamedSpellings_AreRoutedNormally"
restore

# ── M5: the hint names the WRONG token ──────────────────────────────────────────
# Distinguishes "the guard fired" from "the guard fired with the right message".
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = "        var stray = positionals[allowed];"
new = "        var stray = positionals[0];"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M5-hint-names-the-wrong-token" "StrayPositionalGuardTests"
restore

# ── M6: the suggestion drops its quotes — the hint stops being a fix ────────────
# 🔴 The subtlest mutant, and the reason M1 alone is not enough: the guard still fires,
# still names the right token, and suggests a spelling that fails identically.
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = '            + $"       Did you mean: twig {prefix} \\"{quoted}\\"";'
new = '            + $"       Did you mean: twig {prefix} {quoted}";'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M6-suggestion-loses-its-quotes" "TheSuggestedSpelling_IsWithinTheCommandsAcceptedArity"
restore

# ── M7: `note` loses its [Argument] slot — the quoted form is rejected again ────
# The other half of the card. Mutating the DECLARATION, not the guard, so the arity
# drift guard is exercised through the path a real regression would take.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = "public async Task<int> Note([Argument] string? textArg = null,"
new = "public async Task<int> Note(string? textArg = null,"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M7-note-loses-its-argument-slot" "CommandsFixedByThisCard_AcceptTheirPositionals"
restore

# ── M8: the Arity registry drifts from the declarations ────────────────────────
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = '        ["new"] = 2,'
new = '        ["new"] = 1,'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M8-arity-drifts-from-declarations" "EveryRegisteredArity_MatchesTheCommandsArgumentCount"
restore

# ── M9: seed chain stops splitting — a chain of one ────────────────────────────
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
old = "            : titles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);"
new = "            : [titles];"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M9-seed-titles-never-split" "SplitSeedTitles_SplitsOnCommasAndTrims"
restore

# ── M10: init gains a quoting hint it must not have ────────────────────────────
# The false-RED direction on a command whose surplus words are two unrelated identifiers.
python3 - <<'PY'
p = "src/Twig/Commands/StrayPositionalGuard.cs"
s = open(p).read()
old = '        ["new"] = 2,'
new = '        ["new"] = 2,\n        ["init"] = 2,'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M10-init-gains-a-wrong-hint" "Init_IsDeliberatelyAbsent_BecauseQuotingIsNotItsRemedy"
restore

echo
echo "KILLED: $PASS   NOT KILLED: $FAIL"

# Leave-no-trace check — diff against the SNAPSHOT, not against git HEAD.
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
