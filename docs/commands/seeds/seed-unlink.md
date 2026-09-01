---
command: seed unlink
group: seeds
summary: Remove a virtual link between two items.
stability: stable
mutates: local
---

# `twig seed unlink`

Removes a row from the workspace `seed_links` table. Type-scoped: you specify which
link between the two IDs to drop, so multiple typed links between the same two items
can coexist and be pruned one at a time. When the removed link is the seed's
parent-child edge, the seed's `ParentId` is also cleared so the two stores stay in
agreement.

## Synopsis

```
twig seed unlink <sourceId> <targetId> [--type <type>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`sourceId`|yes|Source item ID of the link to remove. For `parent-child`, the child.|
|`targetId`|yes|Target item ID of the link to remove. For `parent-child`, the parent.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--type`|string|`related`|One of: `parent-child`, `blocks`, `blocked-by`, `depends-on`, `depended-on-by`, `related`, `successor`, `predecessor`.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Normalizes the link type against `SeedLinkTypes.All`. Unknown types return exit `1` with the valid-type list (`src/Twig/Commands/SeedLinkCommand.cs:187-194`).
- Removes the row through `ISeedLinkRepository.RemoveLinkAsync`. A missing row is not an error — the operation is idempotent (`src/Twig/Commands/SeedLinkCommand.cs:196`).
- **Parent-child cleanup.** If the removed link is `parent-child` and the source is a seed **and** that seed's current `ParentId` still points at the target, `ParentId` is cleared with `WorkItem.WithParentId(null)` so the seed is not left holding a phantom parent reference (`src/Twig/Commands/SeedLinkCommand.cs:198-203`).
- No cycle checks are needed on removal, and no ADO calls are made.

## Examples

Detach a seed from its published parent:

```
$ twig seed unlink -42 5678 --type parent-child
Unlinked #-42 ──parent-child──▶ #5678
```

Drop a dependency between two seeds:

```
$ twig seed unlink -42 -43 --type blocked-by
Unlinked #-42 ──blocked-by──▶ #-43
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Link removed, or link already absent.|`0`|
|Invalid link type.|`1`|

## See also

- [`seed link`](./seed-link.md) — the inverse operation.
- [`seed links`](./seed-links.md) — list current links before removing one.
- [`seed reconcile`](./seed-reconcile.md) — repair stale links after a partial publish rather than removing them by hand.
