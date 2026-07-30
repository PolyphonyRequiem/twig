#!/bin/bash
# Load generator for twig#311 repro hunting.
# Saturates CPU so the nested `dotnet build` inside BuildFixture's constructor
# contends with the outer build/test host — the condition both observed failure
# clusters are consistent with.
# Self-terminating so it cannot outlive the hunt.
DURATION="${1:-3600}"
WORKERS="${2:-16}"
for i in $(seq 1 "$WORKERS"); do
  timeout "$DURATION" bash -c 'while :; do :; done' &
done
echo "load: $WORKERS spinners for ${DURATION}s (pgid $$)"
wait
