#!/usr/bin/env bash
# ADO #216 mutation harness.
#
# Proves the --org/--project override guards are NOT hollow, by patching the implementation
# WRONG in several ways and requiring the suite to go red BY NAME.
#
# Reports four outcomes per mutant, never two:
#   KILLED          — the expected arms failed, and `error CS` count is 0.
#   SURVIVED        — the suite stayed green. The tests are weaker than they look.
#   DID NOT COMPILE — `error CS` > 0. NOT a kill: a non-compiling mutant reds identically to a
#                     caught one, and banking it as a pass is the false green this repo's test
#                     tooling exists to abolish.
#   NO NAMED ARMS   — the run failed but named no test. A harness/toolchain verdict, not a kill.
#
# Copied from tools/ab398-mutants.sh, which carries the three defects that made earlier
# harnesses report a WORKING suite as hollow:
#   1. arm extraction must grep xUnit's `Name [FAIL]`, not a `Failed <name>` summary line, and
#      must NOT require " [FAIL]" directly after the method name (a parameterised theory prints
#      its inline data in between). An EMPTY arm list is ambiguous rather than SURVIVED.
#   2. two concurrent runs on one working tree destroy each other — hence the flock and the
#      INT/TERM trap.
#   3. the BASELINE must be proven green before mutating: one unrelated red arm makes every
#      mutant "red" and masks the real ones.
#
# 🔴 AB#216 addition — M6 and M7 are the guards-masking-each-other pair. Killing a guard
# outright is the easy case; the failure that costs real time is two guards that both catch the
# same input, so a test asserting only "it was refused" passes against a dead one. Both mutants
# leave BOTH guards firing and swap only their WORDING, so only a test asserting each guard's
# DISTINCT message can kill them.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

LOCKFILE="$REPO_ROOT/artifacts/.ab216-mutants.lock"
mkdir -p "$(dirname "$LOCKFILE")"
exec 9>"$LOCKFILE"
if ! flock -n 9; then
  echo "🔴 another ab216-mutants run holds the lock ($LOCKFILE) — refusing to start."
  echo "   Two runs would mutate and restore the same files concurrently."
  echo
  echo "   🔴 BUT CHECK WHETHER A RUN IS ACTUALLY ALIVE BEFORE WAITING. Verified on this"
  echo "   card: a COMPLETED run left the lock held, because child processes inherit the"
  echo "   open fd and outlive the harness — MSBuild's reusable node daemons, and the"
  echo "   'sleep 3600' that BuildFixtureRunProcessTests deliberately spawns as its"
  echo "   orphan-reaping probe. flock is held until the LAST holder closes it, so a"
  echo "   finished run can block the next one indefinitely."
  echo
  echo "   Diagnose, then clear:"
  echo "     ps -eo pid,etime,args --no-headers | grep -E '[a]b216-mutants|[d]otnet test'"
  echo "     fuser -v $LOCKFILE       # holders: sleep/MSBuild = orphans, not a live run"
  echo "     fuser -k $LOCKFILE       # safe ONLY when the ps check above found nothing"
  exit 3
fi

LOGDIR="artifacts/ab216-mutants"
mkdir -p "$LOGDIR"

RESOLVER="src/Twig/ProcessOverrides/ProcessOverrideResolver.cs"
SCOPE="src/Twig/ProcessOverrides/ProcessOverrideScope.cs"
HOST="src/Twig/ProcessOverrides/ProcessOverrideHost.cs"
PROGRAM="src/Twig/Program.cs"

# 🔴 Every file ANY mutant touches must be listed here, or `restore` leaves that mutant
# resident in the working tree — where it reads as a defect in correct code rather than as
# harness residue, and the leave-no-trace check at the bottom cannot see it either.
ALL_FILES=("$RESOLVER" "$SCOPE" "$HOST" "$PROGRAM")

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
  # 🔴 Match `error CS` AND `error CAF` — NOT `error CS` alone.
  #
  # Verified on this card: M1/M8 remove the --org/--project parameters from a command
  # declaration whose XML doc comment still documents them, and ConsoleAppFramework's source
  # generator rejects that with `error CAF015: Document Comment parameter name 'org' does not
  # match method parameter name.` That is a compile failure — ZERO tests ran — but it contains
  # no `error CS`, so the compile check missed it, the arm list came back empty, and both
  # mutants were reported as NO NAMED ARMS. A reader would have gone hunting for a hollow test
  # that does not exist.
  #
  # This is the same class the header records for `error CS`: a mutant that cannot build reds
  # identically to a caught one, and any compile-error dialect the guard does not know reopens
  # the hole. Match the generator's dialects too.
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

echo "ADO #216 mutation harness"
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

# ── M1: the flags never reach the parser — the headline fix reverted ────────────
# Mutates the DECLARATION, so the arity/parse layer is exercised the way a real regression
# would arrive. This is the one the whole card exists to prevent.
#
# 🔴 The doc-comment lines must be removed TOO. ConsoleAppFramework fails the build with
# `error CAF015` when a <param> documents a parameter the method no longer has, which makes
# the mutant non-compiling rather than caught — it proved nothing about the tests on this
# card's first sweep.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
docs = """    /// <param name="org">Azure DevOps organization to describe instead of this workspace's. Requires --project. Reads live from ADO (announced on stderr); writes nothing.</param>
    /// <param name="project">Azure DevOps project to describe instead of this workspace's. Requires --org.</param>
"""
assert s.count(docs) == 1
s = s.replace(docs, "")
old = "public async Task<int> Process([Argument] string? type = null, string output = OutputFormatterFactory.DefaultFormat, string? org = null, string? project = null, CancellationToken ct = default)"
new = "public async Task<int> Process([Argument] string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)"
assert s.count(old) == 1
s = s.replace(old, new)
old2 = """        => await ProcessOverrideHost.RunAsync(
            services, org, project,
            sp => sp.GetRequiredService<ProcessCommand>().ExecuteAsync(type, output, ct),
            output, ct);"""
new2 = "        => await services.GetRequiredService<ProcessCommand>().ExecuteAsync(type, output, ct);"
assert s.count(old2) == 1
open(p, "w").write(s.replace(old2, new2))
PY
run_mutant "M1-process-loses-its-override-flags" "Overrides_AreAcceptedByTheParser_WithNoWorkspace"
restore

# ── M2: the manifest stops being authoritative — a flag silently wins ───────────
# The acceptance-4 departure the card forbids. Nothing is written either way, so this
# mutant produces NO error at all: it just answers about a different project.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideResolver.cs"
s = open(p).read()
old = "        if (workspaceConfig is not null && !string.IsNullOrWhiteSpace(workspaceConfig.Organization))"
new = "        if (false && workspaceConfig is not null && !string.IsNullOrWhiteSpace(workspaceConfig.Organization))"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M2-manifest-stops-being-authoritative" "ConflictingOverride_IsRefused"
restore

# ── M3: half an override is silently accepted ──────────────────────────────────
# --org alone would then fall back to the workspace's project: a DIFFERENT document under
# the same command line, with nothing on stderr to say so.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideResolver.cs"
s = open(p).read()
old = "        if (hasOrg != hasProject)"
new = "        if (false && hasOrg != hasProject)"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M3-half-an-override-is-accepted" "HalfAnOverride_IsRefused"
restore

# ── M4: the conflict comparison turns case-SENSITIVE — a false RED ─────────────
# `--org polyphonyrequiem` against a manifest saying `PolyphonyRequiem` would be refused.
# The false-red direction, which corrodes an exit code exactly as fast as a false green.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideResolver.cs"
s = open(p).read()
old = "            if (!string.Equals(workspaceConfig.Organization, org, StringComparison.OrdinalIgnoreCase))"
new = "            if (!string.Equals(workspaceConfig.Organization, org, StringComparison.Ordinal))"
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M4-conflict-check-becomes-case-sensitive" "FlagsMatchingTheManifest_UseTheWorkspace"
restore

# ── M5: the override scope actually RESOLVES workspace persistence ─────────────
# 🔴 The subtlest and most valuable mutant, and it took two attempts to make it a real one.
#
# The obvious mutation — add AddConnectionServices back — is an EQUIVALENT MUTANT, verified
# by running the mutated binary: it writes nothing and the scratch dir stays empty. Two
# independent reasons, both worth knowing:
#   1. SqliteCacheStore is registered as a LAZY FACTORY ("created lazily on first resolution"),
#      so registering it constructs nothing;
#   2. this file's Ephemeral* registrations come AFTER it, and DI is last-wins, so the command
#      still resolves the in-memory stores and the SQLite ones are never touched.
# Registering persistence is therefore harmless; RESOLVING it is the defect. Mutating the
# registration could never red, no matter how good the tests were.
#
# So the mutation moves UP to the thing that makes the branch reachable: the stores the
# command actually resolves now come from the workspace's SQLite cache. That is exactly the
# "harmonise the override path with the workspace path" edit a future maintainer would make,
# and it is what acceptance 2 forbids.
#
# 🔴 What it actually breaks, measured rather than assumed: the mutated binary fails with
# "No twig workspace found" instead of describing the process — SqliteCacheStore throws
# WorkspaceNotFoundException before it can create anything. So the arm that catches this is
# the ACCEPTANCE-1 one (the override works with no workspace), not the filesystem one. Worth
# stating, because the natural expectation is a stray .twig/ directory, and a harness that
# expected the wrong arm would report WRONG ARMS on a correctly-caught mutant.
#
# The filesystem arm (Override_WritesNothingToTheFilesystem) still earns its place: it is the
# only thing standing between this design and a variant that resolves persistence somewhere
# a workspace DOES exist, where the write would succeed silently.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideScope.cs"
s = open(p).read()
anchor = "using Twig.Infrastructure.Config;"
assert s.count(anchor) == 1
s = s.replace(anchor, anchor + "\nusing Twig.Infrastructure;")
old = "        services.AddSingleton(config);\n        services.AddTwigNetworkServices(config);"
new = ("        services.AddSingleton(config);\n"
       "        services.AddTwigNetworkServices(config);\n"
       "        services.AddConnectionServices(config, Path.Combine(Directory.GetCurrentDirectory(), \".twig\"));")
assert s.count(old) == 1
s = s.replace(old, new)

# Drop the ephemeral store registrations so the SQLite ones above are what the command
# resolves — i.e. the override now reads (and creates) a workspace database.
import re
start = s.index("        services.AddSingleton<IProcessTypeStore>(sp =>")
end = s.index("        services.AddSingleton(sp => new ProcessCommand(")
s = s[:start] + s[end:]
open(p, "w").write(s)
PY
run_mutant "M5-override-scope-gains-persistence" "Overrides_AreAcceptedByTheParser_WithNoWorkspace"
restore

# ── M6: the two refusals swap WORDING while BOTH keep firing ──────────────────
# 🔴 The guards-masking-each-other mutant. The half-override guard now emits the conflict
# guard's message. Both guards still fire, both still refuse, both still exit 1 — so every
# test asserting "it was refused" stays GREEN. Only asserting each guard's DISTINCT message
# kills this, which is why HalfAnOverride_IsRefused carries a ShouldNotContain on the other
# guard's wording.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideResolver.cs"
s = open(p).read()
old = ('                $"{supplied} requires {missing}. Both name one Azure DevOps project; "\n'
       '                + "supplying one alone cannot address a process.");')
new = '                "The manifest is authoritative.");'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M6-refusals-swap-wording-both-still-fire" "HalfAnOverride_IsRefused"
restore

# ── M7: the conflict refusal stops naming the MANIFEST's value ────────────────
# The other half of the pair. The guard fires, refuses, exits 1, and says "The manifest is
# authoritative" — but never tells the user WHAT they conflicted with, so they cannot act.
# Distinguishes "the guard fired" from "the guard fired usefully".
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideResolver.cs"
s = open(p).read()
old = ('                    $"--org \'{org}\' conflicts with existing twig.json value "\n'
       '                    + $"\'{workspaceConfig.Organization}\'. The manifest is authoritative.");')
new = '                    "--org conflicts with this workspace. The manifest is authoritative.");'
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M7-conflict-refusal-hides-the-manifest-value" "ConflictingOverride_IsRefused"
restore

# ── M8: process layout loses its overrides while process keeps them ───────────
# The asymmetric regression: one of the two commands silently stops honouring the flags.
python3 - <<'PY'
p = "src/Twig/Program.cs"
s = open(p).read()
docs = """    /// <param name="org">Azure DevOps organization to read the layout from instead of this workspace's. Requires --project.</param>
    /// <param name="project">Azure DevOps project to read the layout from instead of this workspace's. Requires --org.</param>
"""
assert s.count(docs) == 1
s = s.replace(docs, "")
old = "public async Task<int> ProcessLayout([Argument] string type, string? @out = null, string output = OutputFormatterFactory.DefaultFormat, string? org = null, string? project = null, CancellationToken ct = default)"
new = "public async Task<int> ProcessLayout([Argument] string type, string? @out = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)"
assert s.count(old) == 1
s = s.replace(old, new)
old2 = """        => await ProcessOverrideHost.RunAsync(
            services, org, project,
            sp => sp.GetRequiredService<ProcessLayoutCommand>().ExecuteAsync(type, @out, output, ct),
            output, ct);"""
new2 = "        => await services.GetRequiredService<ProcessLayoutCommand>().ExecuteAsync(type, @out, output, ct);"
assert s.count(old2) == 1
open(p, "w").write(s.replace(old2, new2))
PY
run_mutant "M8-layout-loses-its-override-flags" "Overrides_AreAcceptedByTheParser_WithNoWorkspace"
restore

# ── M9: the live-read notice goes to STDOUT instead of stderr ─────────────────
# 🔴 The breaking-change direction, and the one a reader would call harmless. The notice is
# still printed, still says the right thing, still names the coordinates — every "does it
# announce itself" assertion stays green. But stdout is the DOCUMENT: a caller piping
# `-o json` into jq now receives a line of prose before the JSON and the pipeline dies.
# Killable only by an arm that asserts the notice is ABSENT from stdout, which is why
# TheLiveReadNotice_NeverReachesStdout exists as its own test rather than as a second
# assertion inside the positive one.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideHost.cs"
s = open(p).read()
old = "        Console.Error.WriteLine(infoFormatter.FormatInfo("
new = "        Console.Out.WriteLine(infoFormatter.FormatInfo("
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M9-live-read-notice-goes-to-stdout" "TheLiveReadNotice_NeverReachesStdout"
restore

# ── M10: the notice fires on EVERY invocation, override or not ────────────────
# The noise direction. Every positive arm stays green — the override still announces itself —
# while thousands of ordinary workspace invocations gain a line of stderr that is a lie
# (nothing was read live). Killed by the negative control.
python3 - <<'PY'
p = "src/Twig/ProcessOverrides/ProcessOverrideHost.cs"
s = open(p).read()
old = "        if (!decision.IsOverride)\n            return await run(workspaceServices);"
new = ("        var announce = new OutputFormatterFactory(new HumanOutputFormatter())\n"
       "            .GetFormatter(outputFormat);\n"
       "        Console.Error.WriteLine(announce.FormatInfo(\n"
       "            $\"Reading {decision.Org}/{decision.Project} live from Azure DevOps (no workspace; nothing is written).\"));\n"
       "        if (!decision.IsOverride)\n"
       "            return await run(workspaceServices);")
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
run_mutant "M10-notice-fires-on-every-invocation" "WorkspaceInvocation_PrintsNoLiveReadNotice"
restore

echo
echo "KILLED: $PASS   NOT KILLED: $FAIL"

# Leave-no-trace check — diff against the SNAPSHOT, not against git HEAD. This card's
# implementation is uncommitted, so `git status` would report every touched file as modified
# and a clean run would exit non-zero. A check that cries wolf gets switched off.
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
