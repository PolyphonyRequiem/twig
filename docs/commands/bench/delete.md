---
command: bench delete
group: bench
summary: Delete a Bench — one holding pins refuses without --confirm.
stability: stable
mutates: local
---

# `twig bench delete`

Delete a Bench you no longer need. A Bench that is empty is deleted outright; a
Bench that holds pins, subtree pins, or query rules is deliberately harder to
lose — the command reports what it holds and deletes nothing unless you
re-type the Bench's name into `--confirm`.

There is deliberately no `--force`. Re-typing the name is a different string
every time, so it cannot decay into a reflex the way `-f` does
(`src/Twig/Commands/BenchCommand.cs:120-130`).

## Synopsis

```
twig bench delete <name> [--confirm <name>] [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`name`|yes|Name of the Bench to delete. Compared case-insensitively against the stored name.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--confirm`|string?|`null`|Re-type the Bench's name to authorise deleting one that holds pins. There is deliberately no `--force`.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, or `minimal`. Declared by the caller — never sniffed from the terminal.|

## Behavior

Routes through `BenchWorkflow.DeleteAsync`, passing both the target name and the
`--confirm` value (`src/Twig/Commands/BenchCommand.cs:138`). The workflow
decides which of the outcomes below applies; the command only renders it.

Staged edits and pending changes on items that were on the Bench are **not**
touched by deletion — the success message says as much
(`src/Twig/Commands/BenchCommand.cs:145`). Deleting the current Bench does
not itself un-stage or discard work.

Named outcomes:

- **Deleted.** The Bench is removed. Exit `0`. `human`/`minimal` print
  `Deleted Bench '<name>'. Staged edits are untouched.`; `json` emits a
  `benchDeleted` record with `name` and `message`
  (`src/Twig/Commands/BenchCommand.cs:142-149`).
- **Holds work.** The Bench holds one or more pins, subtree pins, or query
  rules and `--confirm` did not match. The report of what it holds goes to
  **stdout** (because you asked for it) and the one actionable line goes to
  **stderr** beside a non-zero exit, so a script's pipeline stops rather than
  assuming the Bench is gone (`src/Twig/Commands/BenchCommand.cs:151-156`).
  In machine mode a `benchHoldsWork` record is emitted with `name`, a literal
  `deleted: "false"`, comma-joined `pinned`, `pinnedSubtrees`, `queries`, and
  a `message` (`src/Twig/Commands/BenchCommand.cs:189-207`). In human mode
  the pinned items are listed by work item number (e.g. `#123`), not selector
  row IDs, because the person asked for pins
  (`src/Twig/Commands/BenchCommand.cs:214-222`).
- **Default Bench cannot be deleted.** The default Bench is the one Bench
  that always exists and refuses deletion at any confirmation. Exit `1`
  (`src/Twig/Commands/BenchCommand.cs:158-162`).
- **Unknown Bench.** No Bench by that name exists. Exit `1` with the list of
  Benches that do (`src/Twig/Commands/BenchCommand.cs:164-168`).
- **Name rejected.** The workflow refused the name (empty, whitespace, or
  otherwise invalid). Exit `2` with the rejection reason
  (`src/Twig/Commands/BenchCommand.cs:170-172`).

## Examples

Delete an empty Bench:

```
$ twig bench delete "release blockers"
Deleted Bench 'release blockers'. Staged edits are untouched.
```

Attempt to delete a Bench that holds work:

```
$ twig bench delete "release blockers"
Bench 'release blockers' holds:
  pinned: #4821, #4903
  pinned with subtree: #4700
  queries: my-active-bugs
error: Bench 'release blockers' holds 2 pin(s), 1 subtree pin(s), 1 query rule(s), so nothing was deleted. Delete it anyway with: twig bench delete "release blockers" --confirm "release blockers"
```

Delete it anyway, re-typing the name:

```
$ twig bench delete "release blockers" --confirm "release blockers"
Deleted Bench 'release blockers'. Staged edits are untouched.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Bench deleted|`0`|
|Bench holds pins/subtree pins/query rules and `--confirm` did not match|`1`, report on stdout, action line on stderr|
|Named Bench is the default Bench|`1` — the default Bench always exists|
|No Bench with that name exists|`1` with the list of Benches that do|
|Name rejected by the workflow (empty, whitespace, or otherwise invalid)|`2` with the rejection reason|
|Unrecognised workflow outcome|`1`|

## See also

- [`bench create`](./create.md)
- [`bench list`](./list.md)
- [`bench switch`](./switch.md)
