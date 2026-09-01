---
command: state
group: work-items
summary: Change the state of the active work item by name.
stability: stable
mutates: ado
---

# `twig state`

Move a work item to a new state by full or partial name — for example
`twig state Doing` or `twig state Done`. The transition PATCHes ADO
directly (single-hop or auto-chained multi-hop) and refreshes the local
cache; no proposal is staged. For seeds, the change is applied to the
local seed record instead.

## Synopsis

```
twig state <name> [--id <int>] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `<name>` | yes | Target state name. Full or partial match against the type's configured states (e.g. `Active`, `Done`, `Res` → `Resolved`). Category names (e.g. `Completed`) are also resolved. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--id <int>` | int | (active item) | Work item ID to target; omit to use the active work item. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format for success/error rendering. |

## Behavior

`twig state` writes `System.State` — and *only* `System.State`. Sequence, per
`src/Twig/Commands/StateCommand.cs:45-85`:

1. Resolve the target work item from cache (either by `--id` or the active
   item). Seeds route through `SeedMutationProvider` and mutate locally
   without any ADO round-trip (`src/Twig/Commands/StateCommand.cs:66-67`,
   `87-116`).
2. Preflight-validate the transition: name resolution, allowed transition,
   and gate-field satisfaction
   (`src/Twig/Commands/StateCommand.cs:69-71`).
3. Fetch the item from ADO and run the standard three-way conflict-resolution
   flow (`src/Twig/Commands/StateCommand.cs:73-81`).
4. Execute the transition via `StateTransitionWorkflow`, which auto-chains
   multi-hop transitions (e.g. `To do` → `Doing` → `Done`) when the process
   requires it, auto-pushes any pending notes, then resyncs the cache
   (`src/Twig/Commands/StateCommand.cs:83-84`).

Gate-field enforcement is load-bearing. If the target state has close-gate
fields that the process makes required and no rule supplies, twig refuses
the transition rather than emitting a PATCH that ADO would reject
(`src/Twig/Commands/StateCommand.cs:147-157`). The error message explicitly
directs you to stage the transition as a change-proposal `batch` op
carrying `System.State` plus the missing fields, then apply it with
`twig proposal apply --confirm <digest> --authorize <identity>`. `twig state`
is not the mechanism for closing a gated type.

## Examples

Transition the active item to `Doing`:

```
$ twig state Doing
#1234 Fix login redirect: To do → Doing
```

Multi-hop transitions auto-chain and the path is shown:

```
$ twig state Done
#1234 Fix login redirect: To do → Doing → Done (2 transitions)
```

Category resolution (the Basic process resolves `Completed` to `Done`):

```
$ twig state Completed
#1234 Fix login redirect → Done (resolved category 'Completed' → 'Done')
```

Refused because the target state has unmet gate fields:

```
$ twig state Done
#1234 cannot move to 'Done': the process requires Custom.FalsificationCriteria,
Custom.VerificationMode in that state, and 'Bug' supplies no value for them.
'twig state' writes System.State alone, so it can never carry these fields.
Stage them in the same operation as the transition — a change-proposal 'batch'
op setting System.State plus the fields above — then apply it with
'twig proposal apply --confirm <digest> --authorize <identity>'.
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Transition succeeded (single- or multi-hop) | Exit `0`. |
| Already in the requested state | Exit `0`, prints "Already in state '<name>'." |
| Missing/empty `<name>` argument | Exit `2`, usage error to stderr. |
| Active item not set and no `--id` given | Exit `1`, "No active work item." |
| `--id` refers to an item not in the cache | Exit `1`, "Work item #\<id\> not found in cache." |
| No process configuration for the item's type | Exit `1`. |
| State name doesn't resolve for the type | Exit `1`, invalid state error. |
| Transition not allowed by the process | Exit `1`, "Transition from 'X' to 'Y' is not allowed." |
| Required gate fields missing for target state | Exit `1`, message directs caller to change-proposal path. |
| ADO PATCH rejected mid-chain | Exit `1`, shows the reached path plus ADO error. |
| Conflict-resolution flow aborted or accepted remote | Exit `0`. |
| Conflict emitted as JSON (`--output json`) | Exit `1`. |

## See also

- [`twig batch`](batch.md) — combine `System.State` with the gate fields it
  requires in a single atomic call.
- [`twig update`](update.md) / [`twig patch`](patch.md) — pure field writes
  without state transitions.
- [`twig discard`](discard.md) — drop staged changes without pushing.
