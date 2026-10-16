#!/bin/sh
set -eu
: "${HERDR_PLUGIN_ROOT:?HERDR_PLUGIN_ROOT is required; no executable fallback}"
intent=${1:?Expected open/table/tree/review/exit-review/reconnect/status/close}
shift
binary="$HERDR_PLUGIN_ROOT/bin/twig"
if [ ! -x "$binary" ]; then
    printf '%s\n' "Native Twig payload is absent or not executable." >&2
    exit 1
fi
case "$intent" in
    open|table|tree|review|exit-review|reconnect|status|close)
        exec "$binary" tui --herdr --intent "$intent" "$@" ;;
    *) printf '%s\n' 'Unsupported native control intent.' >&2; exit 2 ;;
esac
