---
command: pending
group: plans
summary: List raw staged pending changes in exact staging order.
stability: stable
mutates: none
---

# `twig pending`

Dumps every currently-staged pending change in the exact order the store
returned it. Strict read-only projection — no business logic, no ADO calls,
and no journal writes. The same rows that appear here are what
`proposal preview` snapshots to flip `canApply` false: a non-empty `pending`
list blocks proposal apply.

## Synopsis

```
twig pending [-o human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `IPendingChangeReader.GetAllChangesAsync` via
`PendingCommand.ExecuteAsync` (`src/Twig/Commands/PendingCommand.cs:28-33`).

- **Raw values preserved verbatim.** The `OldValue`/`NewValue` strings are
  written to stdout character-for-character. They are command output, not
  telemetry, and are deliberately never routed through the telemetry client
  (`src/Twig/Commands/PendingCommand.cs:14-18`) — this respects the
  telemetry privacy rules for ADO field content.
- **Exact staging order.** Rows are emitted in the same order the pending
  store returned them; no reordering, grouping, or dedup.
- **Always exits 0.** An empty pending queue emits `No pending changes.`
  and still exits 0.

## Examples

Human snapshot:

```console
$ twig pending
2 pending change(s):
  #1234 System.State: Doing → Done
  #1234 Custom.TerminalOutcome: (unset) → completed
```

Machine snapshot for a script deciding whether to prompt for `sync`:

```console
$ twig pending -o json
{
  "count": 2,
  "pendingChanges": [
    { "id": 1234, "field": "System.State", "oldValue": "Doing", "newValue": "Done" },
    { "id": 1234, "field": "Custom.TerminalOutcome", "oldValue": null, "newValue": "completed" }
  ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Reader returned successfully — pending list, empty or not, was rendered.|`0`|

## See also

- [`proposal preview`](proposal-preview.md) — snapshots the same rows and gates `canApply` on emptiness.
- [Plans group overview](README.md) — where pending fits on the change-proposal path.
