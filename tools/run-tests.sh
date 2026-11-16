#!/usr/bin/env bash
# USAGE
#   tools/run-tests.sh                 # 15-second offline smoke; no build or test host
#   tools/run-tests.sh --pre-push       # the same smoke
#   tools/run-tests.sh Cli Core         # explicit already-built suites, serial and bounded
#   tools/run-tests.sh --list           # current solution-validated suite inventory
#   tools/run-tests.sh --selftest       # runner guards; no compilation or test hosts
# EXIT CODE: zero only for an unaborted, reconciled pass.
set -uo pipefail

fatal() {
  echo "run-tests: $1" >&2
  echo "TWIG-VERDICT OVERALL: FAILED ($1; nothing ran)"
  exit 2
}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || fatal "cannot cd to repo root"

mode=""
suites=()
for arg in "$@"; do
  case "$arg" in
    -h|--help)
      sed -n '/^# USAGE$/,/^# EXIT CODE/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    --pre-push|--selftest|--list)
      [ -z "$mode" ] || fatal "conflicting modes"
      mode="$arg"
      ;;
    -*) fatal "unknown option '$arg'" ;;
    *) suites+=("$arg") ;;
  esac
done

if [ -n "$mode" ] && [ ${#suites[@]} -ne 0 ]; then
  fatal "a mode cannot be combined with suite names"
fi

if [ "$mode" = "--selftest" ] || [ "$mode" = "--list" ]; then
  python tools/run-ci-tests.py "$mode"
  exit $?
fi

if [ ${#suites[@]} -eq 0 ]; then
  python tools/verify-build.py --timeout 15
  code=$?
  if [ "$code" -eq 0 ]; then
    echo "TWIG-VERDICT OVERALL: PASSED (15-second fundamental smoke; full validation belongs to release candidates)"
  else
    echo "TWIG-VERDICT OVERALL: FAILED (fundamental smoke rejected the build; exit $code)"
  fi
  exit "$code"
fi

# No local-only exclusions, implicit builds, or summary-only verdicts.
python tools/run-ci-tests.py "${suites[@]}"
exit $?
