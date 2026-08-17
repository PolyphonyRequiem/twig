#!/usr/bin/env bash
# AB#524: prove ParallelizationPolicyTests DISCRIMINATES.
#
# A guard that passes both before and after the fix is worthless. Mutate the
# mitigation away (flip DisableTestParallelization to false) and require the guard
# to go RED, then restore and require GREEN.
#
# Hygiene per test-verdict-integrity:
#  - a mutant that does not COMPILE is not a killed mutant -> count compile errors
#    in every dialect this toolchain emits (CS and the CAF source generator)
#  - restore by snapshot + cmp, not by git status (the tree may hold other work)
#  - touch restored files and drop obj/bin, or MSBuild runs the STALE MUTANT dll
set -u
export HOME=/home/polyphonyrequiem
unset DOTNET_ROOT
export PATH=/home/polyphonyrequiem/.dotnet-p5:$PATH
cd /home/polyphonyrequiem/repos/twig-524

SRC=tests/Twig.Tui.Tests/AssemblyAttributes.cs
SNAP=/tmp/ab524-attr.snapshot
FILTER='FullyQualifiedName~ParallelizationPolicyTests'
cp "$SRC" "$SNAP"

run() {   # $1 = label
  local log="/tmp/ab524-mut-$1.log"
  rm -rf tests/Twig.Tui.Tests/obj tests/Twig.Tui.Tests/bin
  dotnet test tests/Twig.Tui.Tests/Twig.Tui.Tests.csproj \
    --nologo --filter "$FILTER" > "$log" 2>&1
  local rc=$?
  local cs
  cs="$(grep -cE 'error (CS|CAF)[0-9]+' "$log" || true)"
  local arms
  arms="$(grep -oE '[A-Za-z_]+ \[FAIL\]' "$log" | sort -u | tr '\n' ' ')"
  local total
  total="$(grep -oE 'Total: *[0-9]+' "$log" | tail -1 | grep -oE '[0-9]+' || echo 0)"
  echo "  $1: exit=$rc compile_errors=$cs tests_run=$total"
  echo "     failing arms: ${arms:-<none>}"
  [ "$cs" -ne 0 ] && echo "     !! COMPILE ERRORS -> this is NOT a valid result"
  return $rc
}

echo "=== ARM 1: restored tree (must be GREEN) ==="
run baseline && echo "  -> PASSED as required" || echo "  -> UNEXPECTED RED"

echo
echo "=== ARM 2: mutant, DisableTestParallelization = false (must be RED) ==="
sed -i 's/CollectionBehavior(DisableTestParallelization = true)/CollectionBehavior(DisableTestParallelization = false)/' "$SRC"
grep -n "DisableTestParallelization" "$SRC" | head -2
if run mutant; then
  echo "  -> !!! SURVIVED: the guard does NOT discriminate. It is hollow."
else
  echo "  -> KILLED as required (guard discriminates)"
fi

echo
echo "=== RESTORE ==="
cp "$SNAP" "$SRC"
touch "$SRC"                                  # newer than any mutant obj
rm -rf tests/Twig.Tui.Tests/obj tests/Twig.Tui.Tests/bin
if cmp -s "$SNAP" "$SRC"; then
  echo "  byte-identical to snapshot: OK"
else
  echo "  !! RESTORE FAILED"
fi
run restored && echo "  -> restored tree GREEN: OK" || echo "  -> !! restored tree RED"
