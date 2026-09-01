---
command: seed reconcile
group: seeds
summary: Repair stale seed links and parent references after a partial publish.
stability: stable
mutates: local
---

# `twig seed reconcile`

Repairs stale rows in the workspace `seed_links` table and stale `ParentId` values on
seeds whose referenced peers have been published since the row was written. Consults
the local `publish_id_map` — the record of "seed `#-42` became ADO `#7842`" — and
rewrites stale references to the new positive IDs, or removes them entirely when the
peer is no longer traceable.

Reach for this after a `seed publish` batch that halted partway, or whenever
`seed view` shows dangling parent chains.

## Synopsis

```
twig seed reconcile [-o|--output <format>]
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
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- The command class is `SeedLinkRepairCommand` — the CLI verb was renamed to `seed reconcile` in Program.cs while the underlying repair orchestrator kept its name (`src/Twig/Commands/SeedLinkRepairCommand.cs:9-24`; `src/Twig/Program.cs:901-903`).
- Delegates the work to `SeedLinkRepair.RepairAsync`. That service walks the `seed_links` table and the seed `ParentId` field, cross-references each row against the `publish_id_map`, and rewrites or removes stale entries in a single unit of work (`src/Twig/Commands/SeedLinkRepairCommand.cs:33`).
- Emits counts of what was repaired and a warnings table for anything the service could not fix (e.g. a link whose target was published but whose new ID is missing from the map). Machine formats emit a document of counts plus a warnings Table; human output is a labelled summary; minimal emits a single key line (`src/Twig/Commands/SeedLinkRepairCommand.cs:40-109`).
- Always exits `0`; a partial repair with warnings is not treated as an error (`src/Twig/Commands/SeedLinkRepairCommand.cs:37`).
- No network calls. All work is against local SQLite: `seed_links`, work items, and `publish_id_map`.

## Examples

Run reconcile after a partial publish:

```
$ twig seed reconcile
Reconciled seed state:
  Links rewritten: 4
  Links removed:   1
  Parents fixed:   2
  Warnings:        0
```

Machine form for scripting:

```
$ twig seed reconcile -o json
{"kind":"seedReconcile","linksRewritten":4,"linksRemoved":1,"parentsFixed":2,"warnings":[]}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Reconcile completed (with or without warnings).|`0`|

## See also

- [`seed publish`](./seed-publish.md) — the operation whose partial failures this repairs.
- [`seed links`](./seed-links.md) — inspect the link table before and after.
- [`seed view`](./seed-view.md) — see the reconciled state alongside the whole dashboard.
