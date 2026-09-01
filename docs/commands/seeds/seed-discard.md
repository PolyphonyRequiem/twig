---
command: seed discard
group: seeds
summary: Delete a local seed and its descendants.
stability: stable
mutates: local
---

# `twig seed discard`

Deletes a local seed from the workspace cache. If the seed has descendant seeds — the
common case with `seed chain` or hand-built parent trees — they are discarded too.
No ADO calls; a published item cannot be discarded through this command.

## Synopsis

```
twig seed discard <id> [--yes] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`id`|yes|Seed ID to discard. Must be a negative ID belonging to a local seed.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--yes`|bool|`false`|Skip the interactive confirmation prompt. Required for non-interactive callers.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Loads the seed via `IWorkItemRepository.GetByIdAsync`. Missing IDs and non-seed IDs return exit `1`; published items must go through the ADO delete path (`src/Twig/Commands/SeedDiscardCommand.cs:37-48`).
- Builds a discard plan via `SeedDiscardOrchestrator.BuildDiscardPlanAsync`. The plan enumerates the target seed and every descendant seed reachable through the parent-child link table so the confirmation prompt can state the blast radius before anything is deleted (`src/Twig/Commands/SeedDiscardCommand.cs:50-55`).
- Without `--yes`, prompts on stdin. Any response other than `y` (case-insensitive) prints `Discard cancelled.` and exits `0` without touching the cache (`src/Twig/Commands/SeedDiscardCommand.cs:57-69`).
- On confirmation, `SeedDiscardOrchestrator.ExecuteDiscardAsync` removes the seeds and any parent-child/dependency link rows anchored on them in a single unit of work.
- The success line differs based on descendant count: singular seed vs. seed plus N descendants (`src/Twig/Commands/SeedDiscardCommand.cs:73-77`).

## Examples

Discard a leaf seed:

```
$ twig seed discard -42
Discard seed #-42 'Wire audit trail into the batch endpoint'? (y/N) y
Discarded seed #-42 Wire audit trail into the batch endpoint
```

Discard a subtree non-interactively:

```
$ twig seed discard -100 --yes -o minimal
Discarded seed #-100 API design and 4 descendants
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Seeds discarded, or user cancelled at the prompt.|`0`|
|Seed not found, or target is not a seed.|`1`|

## See also

- [`seed new`](./seed-new.md) — the operation this reverses.
- [`seed view`](./seed-view.md) — review pending seeds before discarding.
- [`seed chain`](./seed-chain.md) — build seed subtrees; `seed discard` collapses them.
