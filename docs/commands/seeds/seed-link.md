---
command: seed link
group: seeds
summary: Create a virtual link between two items, at least one of which must be a seed.
stability: stable
mutates: local
---

# `twig seed link`

Creates a **virtual link** — a row in the workspace `seed_links` table — between two
items. At least one endpoint must be a seed (negative ID); links between two
published items belong in ADO proper and are rejected here. Links carry a type
(`related` by default) and, for `parent-child`, also reconcile the child seed's
`ParentId`.

## Synopsis

```
twig seed link <sourceId> <targetId> [--type <type>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`sourceId`|yes|Source item ID for the link. For `parent-child`, this is the **child**.|
|`targetId`|yes|Target item ID for the link. For `parent-child`, this is the **parent**.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--type`|string|`related`|One of: `parent-child`, `blocks`, `blocked-by`, `depends-on`, `depended-on-by`, `related`, `successor`, `predecessor`.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Rejects links where both IDs are positive: two published items cannot be joined through the local link table (`src/Twig/Commands/SeedLinkCommand.cs:37-42`).
- Normalizes the link type against `SeedLinkTypes.All`; unknown types return exit `1` with the full valid-type list (`src/Twig/Commands/SeedLinkCommand.cs:44-51`).
- **Parent-child reparenting.** When `--type parent-child` and the source is a seed, the command tolerates an existing parent link: it writes the new parent row first, then removes stale rows for the source, then updates `WorkItem.ParentId`. The write-before-teardown order is deliberate — a failure mid-way leaves the seed with its old, correct parent instead of a `ParentId` with no matching row (`src/Twig/Commands/SeedLinkCommand.cs:57-109`).
- Dependency link types other than `related` and `parent-child` run through `SeedDependencyGraph.WouldCreateCycle`. Any cycle is rejected with exit `1` and the cyclic ID list is enumerated in the error (`src/Twig/Commands/SeedLinkCommand.cs:123-145`).
- Positive-ID endpoints that are not in the local cache produce an info warning but the link is still written; the row simply references an out-of-cache published item (`src/Twig/Commands/SeedLinkCommand.cs:112-121`).
- Duplicate links are refused with a user-facing error via `TryAddLinkAsync` (`src/Twig/Commands/SeedLinkCommand.cs:158-176`).

## Examples

Attach seed `#-42` under published item `#5678`:

```
$ twig seed link -42 5678 --type parent-child
Linked #-42 ──parent-child──▶ #5678
```

Wire a dependency between two seeds:

```
$ twig seed link -42 -43 --type blocked-by
Linked #-42 ──blocked-by──▶ #-43
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Link (or reparent) succeeded.|`0`|
|Both IDs positive, invalid type, endpoint seed not found, would create a cycle, or duplicate link.|`1`|
|Seed cannot be its own parent (`--type parent-child` self-loop).|`2`|

## See also

- [`seed unlink`](./seed-unlink.md) — remove an existing link.
- [`seed links`](./seed-links.md) — list current links.
- [`seed chain`](./seed-chain.md) — the batch equivalent for `successor` chains.
- [`seed reconcile`](./seed-reconcile.md) — repair links after a partial publish.
