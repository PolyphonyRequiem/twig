---
command: bench list
group: bench
summary: List user-wide Benches, marking the current worktree's selection.
stability: stable
mutates: none
---

# `twig bench list`

List the native user-wide Bench definitions shared by the CLI and panel. Listing
does not create or select a default Bench, evaluate queries, refresh work items
or acquire provider credentials.

```powershell
twig bench list
twig bench list --include-management -o json
```

`-o|--output` supports `human`, JSON variants, `minimal` and `ids`. Normal rows
carry `id`, `name`, `revision`, `selectorCount` and `isCurrent`; `ids` emits only
canonical Bench GUIDs, one per line. An empty store produces an empty listing.

Without an initialized workspace, metadata listing remains available and no
Bench is marked current. In an initialized workspace, native local admission
checks the worktree's selected Bench. Other worktrees' selections are unchanged.
Previously saved native definitions are preserved once per native store instance;
conflicting names or intent refuse rather than overwrite existing definitions.

`--include-management` requires JSON output and returns a version-3 object:
`storeId`, store `revision`, `currentBenchId` (or null), and `benches`. Each entry
contains `id`, `name`, Bench `revision`, `contentsDigest` and the complete typed
`definition`, including connection-qualified selectors. This is native metadata,
not work-item contents or credentials.

The native store is `benches.db` beside the user registry under `TWIG_USER_HOME`
or the existing per-user Twig home. The panel does not maintain an independent
authoritative Bench copy.

Exit codes: `0` success (including empty); `1` unavailable/conflicting state;
`2` invalid input. Errors are format-aware on stderr.

See [`bench create`](./create.md), [`bench rename`](./rename.md),
[`bench switch`](./switch.md) and [`bench delete`](./delete.md).
