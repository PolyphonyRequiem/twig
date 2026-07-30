#!/bin/bash
# Faithful stressor for twig#311 — competing MSBuild load.
#
# CPU spinners alone did NOT reproduce the abort in 10 attempts. Adding competing
# `dotnet build` loops did, on the next attempt. MSBuild contention models the real
# condition that CPU load does not: node reuse, the shared obj/bin output, NuGet
# locks, and file I/O — plus the Cli suite itself shells out to a nested build
# inside BuildFixture's constructor.
#
# Run two or more of these alongside tools/find-hung-test.sh. Self-terminating so
# it cannot outlive the hunt.
#
#   tools/repro-311/build-load.sh 1800 &
#   tools/repro-311/build-load.sh 1800 &
#   tools/find-hung-test.sh 25
#
# Assumes the pinned SDK is already on PATH (see "Build & test" in AGENTS.md).
set -u
cd "$(git rev-parse --show-toplevel)" || exit 1
END=$(( $(date +%s) + ${1:-1800} ))
while [ "$(date +%s)" -lt "$END" ]; do
  dotnet build src/Twig/Twig.csproj > /dev/null 2>&1
done
