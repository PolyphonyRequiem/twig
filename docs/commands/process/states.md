---
command: states
group: process
summary: Hidden alias — list workflow states for the active work item's type.
stability: stable
mutates: none
---

# `twig states`

Hidden compatibility alias for `twig process <type>`, scoped to whichever
type the active work item is. Resolves the active work item via
`ActiveItemResolver`, reads its type, and forwards to
`ProcessCommand.ExecuteTypeDetailAsync` — the same code path
`twig process <type>` uses (`src/Twig/Commands/ProcessCommand.cs:71-95`,
`src/Twig/Program.cs:644-648`). Hidden from `twig --help`; kept because
older muscle memory and older scripts still call it. Prefer
`twig process <type>` in new work.

Type and state data are discovered from the process description — this
command hard-codes nothing about which states any type has.

## Synopsis

```
twig states [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|—|—|—|—|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`string`|`human`|Output format. Accepts `human`, `json`, and `minimal`.|

## Behavior

- Requires a workspace. `ActiveItemResolver` is a workspace-scoped service,
  and this command refuses without one: `'twig states' resolves the active
  work item, which requires a workspace. Run it from a twig workspace, or
  use 'twig process <type>' with --org/--project.`
  (`src/Twig/Commands/ProcessCommand.cs:77-83`).
- Requires an active work item. If the active-item resolver returns nothing,
  exits 1 with `No active work item. Run 'twig set <id>' first.` If it
  returns an id that is not in the local cache, exits 1 with
  `Work item #<id> not found in cache.`
  (`src/Twig/Commands/ProcessCommand.cs:85-92`).
- Forwards to `ExecuteTypeDetailAsync` with the resolved type name, so the
  output is byte-identical to `twig process <type>` for the same type
  (`src/Twig/Commands/ProcessCommand.cs:94`).
- Does **not** accept `--org`/`--project`. Overrides are workspace-less and
  the concept of an "active work item" does not exist in that scope; the
  guard makes that a checked fact rather than a null-reference at runtime
  (`src/Twig/Commands/ProcessCommand.cs:63-70`).
- Hidden from top-level help (`[Hidden]` attribute at
  `src/Twig/Program.cs:646-648`). Still listed in the process-command roster
  at `src/Twig/Program.cs:1538-1543` so it is a real command, just not one
  the help text advertises.

Read-only: no local writes, no ADO mutations.

## Examples

### Show the active item's states

```
$ twig set 1234
Active work item set: 1234 (Task) 'Wire up seed --publish flow'
$ twig states
  New                 Proposed (#B2B2B2)
  Active              InProgress (#007ACC)
  Resolved            Resolved (#FF9D00)
  Closed              Completed (#339933)
  Removed             Removed (#B2B2B2)
```

### Prefer the explicit spelling in scripts

```
$ twig process Task -o json
```

Same tree as `twig states -o json` when the active item is a Task, but does
not depend on which item is currently active — which is what you want in a
script.

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Invoked outside a workspace.|Exit `1`; stderr `'twig states' resolves the active work item, which requires a workspace. …`|
|No active work item is set.|Exit `1`; stderr `No active work item. Run 'twig set <id>' first.`|
|Active work item id not present in cache.|Exit `1`; stderr `Work item #<id> not found in cache.`|
|Active item's type has no states in cache.|Exit `1`; stderr `No states found for type '<name>'. Run 'twig sync' to refresh process data.`|
|Otherwise successful invocation.|Exit `0`; state list printed.|

## See also

- [`twig process`](./process.md)
- [`twig process layout`](./process-layout.md)
- [`twig process description`](./process-description.md)
