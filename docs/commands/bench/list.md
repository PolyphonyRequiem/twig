---
command: bench list
group: bench
summary: List user-wide Bench definitions without selecting a Bench.
stability: stable
mutates: none
---

# `twig bench list`

## Behavior

List the native user-wide Bench definitions shared by the CLI and panel. Listing
does not create or select a default Bench, evaluate queries, refresh work items
or acquire provider credentials.

```powershell
twig bench list
twig bench list --include-management -o json
```

`-o|--output` supports `human`, JSON variants, `minimal` and `ids`. Normal rows
identify each Bench and its revision and selector count; `ids` emits only
canonical Bench GUIDs, one per line. An empty store produces an empty listing.

Metadata listing does not require an initialized workspace. No Bench is
designated current, even when a daemon view has selected one.
Previously saved native definitions are preserved once per native store instance;
conflicting names or intent refuse rather than overwrite existing definitions.

`--include-management` requires JSON output and includes the
`storeId`, store `revision`, and `benches`. Each entry
contains `id`, `name`, Bench `revision`, `contentsDigest` and the complete typed
`definition`, including connection-qualified selectors. This is native metadata,
not work-item contents or credentials.

The native store is `benches.db` beside the user registry under `TWIG_USER_HOME`
or the existing per-user Twig home. The panel does not maintain an independent
authoritative Bench copy.

Exit codes: `0` success (including empty); `1` unavailable/conflicting state;
`2` invalid input. Errors are format-aware on stderr.

See [`bench create`](./create.md), [`bench rename`](./rename.md)
and [`bench delete`](./delete.md).
