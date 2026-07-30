#!/bin/bash
# Faithful stressor for twig#311 — competing MSBuild load.
#
# CPU spinners alone did NOT reproduce the abort in 10 attempts. Adding competing
# `dotnet build` loops did, on the next attempt. MSBuild contention models the real
# condition that CPU load does not: node reuse, NuGet locks, compiler processes,
# and file I/O -- plus the Cli suite itself shells out to a nested build inside
# BuildFixture's constructor.
#
# 🔴 TWO traps, both hit for real on 2026-07-30. Read before editing this file.
#
# 1. The build must be a REAL rebuild, not a no-op. An up-to-date incremental
#    build finishes in ~3.4s doing almost nothing, spawns no compiler processes,
#    and a 30-attempt hunt under that "load" reproduced NOTHING. Verify with
#    `pgrep -cf csc.dll` while this runs -- zero means you are not applying the
#    load you think you are.
#
# 2. The build must target a DIFFERENT project than the suite under test. An
#    earlier version rebuilt src/Twig while the Cli suite's BuildFixture was also
#    building src/Twig; they collided over the same output and five
#    OutputFormatEntrypointTests failed with "Build failed or timed out". That is
#    a self-inflicted failure, NOT the #311 hang -- it aborts on a real [FAIL]
#    rather than the timeout, and it wastes a hunt. Build a project the suite does
#    not touch, into a private output directory.
#
# Run two or more of these alongside tools/find-hung-test.sh or tools/diag-hunt.sh.
# Self-terminating so it cannot outlive the hunt.
#
#   tools/repro-311/build-load.sh 1800 &
#   tools/repro-311/build-load.sh 1800 &
#   tools/diag-hunt.sh 40
#
# Assumes the pinned SDK is already on PATH (see "Build & test" in AGENTS.md).
set -u
ROOT="$(git rev-parse --show-toplevel)" || exit 1
cd "$ROOT" || exit 1

# Twig.Domain is NOT built by the Cli suite's BuildFixture (which builds src/Twig),
# so hammering it generates compiler/MSBuild contention without corrupting the
# binary the entrypoint tests execute.
PROJECT="src/Twig.Domain/Twig.Domain.csproj"
[ -f "$PROJECT" ] || { echo "build-load: $PROJECT missing" >&2; exit 1; }

# Private output dir per PID so concurrent copies of this script don't collide
# with each other or with the repo's normal build output.
OUT="$ROOT/artifacts/build-load/$$"
mkdir -p "$OUT"
trap 'rm -rf "$OUT"' EXIT

END=$(( $(date +%s) + ${1:-1800} ))
while [ "$(date +%s)" -lt "$END" ]; do
  # Force a genuine recompile every iteration. --no-incremental rebuilds without
  # touching any source file, so the working tree is never modified at all.
  dotnet build "$PROJECT" --no-incremental \
    -p:BaseOutputPath="$OUT/bin/" -p:BaseIntermediateOutputPath="$OUT/obj/" \
    > /dev/null 2>&1
done
