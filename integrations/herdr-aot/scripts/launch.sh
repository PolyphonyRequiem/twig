#!/bin/sh
set -eu
: "${HERDR_PLUGIN_ROOT:?HERDR_PLUGIN_ROOT is required; no stable executable fallback}"
intent=${1:?Expected open/table/tree/review/exit-review/reconnect/status/close}
shift
binary="$HERDR_PLUGIN_ROOT/bin/twig"
if [ ! -x "$binary" ]; then
    printf '%s\n' "Isolated native candidate payload is absent or not executable; delivery/signing qualification must supply it." >&2
    exit 1
fi
case "$intent" in
    open|table|tree|review|exit-review|reconnect|status|close)
        exec "$binary" bench browser --herdr --intent "$intent" "$@" ;;
    *) printf '%s\n' 'Unsupported native candidate control intent.' >&2; exit 2 ;;
esac
