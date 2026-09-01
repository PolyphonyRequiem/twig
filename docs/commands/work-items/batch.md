---
command: batch
group: work-items
summary: State transition, field updates, and a note in a single atomic call.
stability: stable
mutates: ado
---

# `twig batch`

Combine a state transition, one or more field updates, and an optional
comment into a single ADO PATCH per work item. Reach for this when a Done
transition also needs its gate fields written in the same operation, or when
you want to script a coordinated multi-field edit across one or many items.

## Synopsis

```
twig batch [--state <name>] [--set <key=value>]... [--note <text>]
           [--id <int> | --ids <n,n,...>]
           [--format markdown] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| — | — | `twig batch` has no positional arguments. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--state <name>` | string | none | Target state name (full or partial). |
| `--set <key=value>` | repeatable | none | Field update as `FieldRef=value` (e.g. `Priority=1`). Repeatable. |
| `--note <text>` | string | none | Comment added after the PATCH. |
| `--id <int>` | int | (active item) | Single-item target. Mutually exclusive with `--ids`. |
| `--ids <n,n,...>` | string | none | Comma-separated IDs for a multi-item run (e.g. `1234,5678`). Mutually exclusive with `--id`. |
| `--format <mode>` | `markdown` \| `raw` | auto | Convert `--set` values (and the `--note` body) before sending. `markdown` force-converts; auto converts only HTML-typed fields. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. `json` emits a per-item `BatchResult`. |

## Behavior

At least one of `--state`, `--set`, or `--note` must be given, otherwise
`twig batch` exits with a usage error
(`src/Twig/Commands/BatchCommand.cs:94-99`).

Per item, `ProcessItemAsync`
(`src/Twig/Commands/BatchCommand.cs:359-503`) runs the following sequence,
which is the reason `batch` is the correct verb for a Done-plus-gate-fields
close:

1. Fetch the item from ADO.
2. Conflict-resolve. Single-item runs use the interactive
   `ConflictResolutionFlow`; multi-item runs auto-accept the remote when a
   three-way merge finds conflicts, so ambiguous states never block a batch
   in the middle.
3. If `--state` is set, resolve the state against the type's configured
   states and verify the transition is allowed. `batch` intentionally
   **does not** auto-chain multi-hop transitions — that would break the
   "one atomic PATCH" guarantee. Multi-hop needs `twig state`
   (`src/Twig/Commands/BatchCommand.cs:451-456`).
4. Build the combined `FieldChange[]` from the resolved state (if any) plus
   the `--set` pairs, and send a single PATCH with retry on concurrency
   conflict.
5. If `--note` is set, add the comment (converted per `--format` — default
   Markdown→HTML) via a separate ADO call.
6. Auto-push any residual pending notes for the item, then resync the local
   cache. A resync failure is non-fatal — the command warns and continues.

`--set` values are parsed via the shared `FieldAssignment` parser used by
`twig new --field` and `twig seed new --field`. HTML-typed fields default to
Markdown→HTML conversion; plain-text fields pass through unchanged.
`--format markdown` forces conversion for every value regardless of field
type.

Multi-item runs collect per-item results and emit a summary. In `json` mode
the whole run is a single `BatchResult` object (`totalItems`, `succeeded`,
`failed`, `items[]`) — the wire format is committed
(`src/Twig/Commands/BatchCommand.cs:326-357`).

## Examples

Close a `Task` in one operation, writing `Custom.TerminalOutcome` alongside
`System.State`:

```
$ twig batch --state Done --set Custom.TerminalOutcome=completed \
             --note "All acceptance criteria met."
#1234 Fix login redirect: To do → Done, 1 field(s) updated, note added
```

Multi-item update: bump priority across two items:

```
$ twig batch --set Priority=1 --set Severity=High --ids 1234,5678
#1234 Fix login redirect: 2 field(s) updated
#5678 Login flakiness: 2 field(s) updated
Batch complete: 2/2 succeeded.
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| All items succeeded | Exit `0`. |
| At least one item failed in multi-item mode | Exit `1`, summary counts failures. |
| Single-item failure | Exit `1`, error to stderr. |
| No operation given (no `--state`, `--set`, `--note`) | Exit `2`, usage error. |
| Both `--id` and `--ids` given | Exit `2`, mutually exclusive. |
| Invalid `--format` value | Exit `2`. |
| Malformed `--set` pair | Exit `2`. |
| Malformed `--ids` list | Exit `2`. |
| No process configuration for a target's type | Item marked failed. |
| Requested transition not allowed by process | Item marked failed. |
| Concurrency conflict after retry | Item marked failed with "Run 'twig sync' and retry." |
| Cache resync failed after successful PATCH | Warning to stderr; exit stays `0`. |

## See also

- [`twig state`](state.md) — single-purpose state transition, auto-chains
  multi-hop, refuses when gate fields are unmet.
- [`twig update`](update.md) / [`twig patch`](patch.md) — pure field writes
  without a state transition.
- [`twig note`](note.md) — add a comment without touching fields.
