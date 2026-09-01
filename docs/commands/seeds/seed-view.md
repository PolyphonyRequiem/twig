---
command: seed view
group: seeds
summary: Show the seed dashboard grouped by parent.
stability: stable
mutates: none
---

# `twig seed view`

Renders the seed dashboard: every local seed in the workspace, grouped under its
parent work item (or as an "Orphan Seeds" section for unparented drafts). Reach for
this before publishing to eyeball structure, completeness, and staleness.

## Synopsis

```
twig seed view [-o|--output <format>]
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

- Read-only against the workspace cache: no ADO calls, no local writes.
- Counts writable fields from `IFieldDefinitionStore` and computes a per-seed completeness ratio; readonly reference names are excluded so the ratio is meaningful (`src/Twig/Commands/SeedViewCommand.cs:31-38`).
- Stale threshold comes from `TwigConfiguration.Seed.StaleDays` (`src/Twig/Commands/SeedViewCommand.cs:40`). Seeds older than the threshold are flagged in the rendered view.
- Builds a per-seed link map from `ISeedLinkRepository` so parent-child and dependency edges are shown alongside each row (`src/Twig/Commands/SeedViewCommand.cs:42-43`).
- When rendering to an interactive TTY, drops into the Spectre.Console `RenderSeedViewAsync` pipeline with live tables and badges. Non-TTY / machine formats fall back to the `RenderTree` document with a `Seeds (N)` section per parent group plus a `totalSeeds` scalar (`src/Twig/Commands/SeedViewCommand.cs:46-61,64-108`).

## Examples

Interactive dashboard:

```
$ twig seed view
Seeds (5)
  Parent: #5678 Feature — Batch API rework
    #-42  Task  Wire audit trail into the batch endpoint    3/7 fields   fresh
    #-43  Task  Cover audit trail with tests                 2/7 fields   fresh
  Orphan Seeds
    #-99  Bug   Race in link publisher                       5/7 fields   stale
```

Machine form:

```
$ twig seed view -o json
{"groups":[...],"totalSeeds":5}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Dashboard rendered.|`0`|

## See also

- [`seed validate`](./seed-validate.md) — check publish readiness for what the dashboard shows.
- [`seed links`](./seed-links.md) — inspect just the link table.
- [`seed publish`](./seed-publish.md) — push the dashboard's contents to ADO.
