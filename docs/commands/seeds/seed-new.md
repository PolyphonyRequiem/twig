---
command: seed new
group: seeds
summary: Create a new local seed work item.
stability: stable
mutates: local
---

# `twig seed new`

Creates a new **local seed** — a draft work item with a negative ID that lives only in
the workspace SQLite cache. Nothing is sent to Azure DevOps. Reach for this when you
want to compose one or many work items, wire them together, and publish the batch
later with `seed publish`.

## Synopsis

```
twig seed new [--title <string>] [--type <type>] [--editor] [--parent <id> | --no-parent]
              [--description <string>] [--field <ref>=<value> ...] [-o|--output <format>]
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
|`--title`|string|`null`|Title for the new seed. Required unless `--editor` supplies it via the editor buffer.|
|`--type`|string|`null`|Work item type (e.g. `Task`, `Bug`, `Feature`). Inferred from the parent when omitted; **required** with `--no-parent`.|
|`--editor`|bool|`false`|Open an external editor pre-filled with the seed template; parsed fields overwrite CLI values.|
|`--parent`|int|`null`|Parent work item ID. Defaults to the active work item when omitted.|
|`--no-parent`|bool|`false`|Create the seed with no parent. Mutually exclusive with `--parent`; requires `--type`.|
|`--description`|string|`null`|Description text. Converted to HTML only when `System.Description` is HTML-typed.|
|`--field`|string[]|`null`|Set a field as `Reference.Name=value`. Repeatable. HTML-typed fields convert Markdown; other types pass through. An explicit `--field System.Description` overrides `--description`.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Parses every `--field` up-front via `FieldAssignment.ParseAll` (`src/Twig/Commands/SeedNewCommand.cs:59`). A malformed pair returns exit `2` before any persistence.
- Rejects unknown field reference names against the local `IFieldDefinitionStore` because ADO silently drops unknown fields at publish, hiding the loss (`src/Twig/Commands/SeedNewCommand.cs:66-88`).
- Parent resolution: `--no-parent` → orphan seed; `--parent <id>` → hard failure if the ID is not in the local cache; otherwise the active item is used and the fact that the parent was **inferred** is announced in the output as a warning (`src/Twig/Commands/SeedNewCommand.cs:114-142,297-303`).
- Orphan seeds fall back to `TwigConfiguration.Defaults.AreaPath` / `IterationPath`, then the project name, then default, mirroring the MCP `SeedTools.ResolveDefaultPath` path (`src/Twig/Commands/SeedNewCommand.cs:169-176,372-389`).
- With `--editor`, the seed template is generated from field definitions, launched via `IEditorLauncher`, and reparsed. A cancelled editor yields exit `0` with a `Seed creation cancelled` message (`src/Twig/Commands/SeedNewCommand.cs:210-235`).
- Persistence: the seed row is saved through `IWorkItemRepository.SaveAsync`. When the parent was **explicit** (either `--parent` or an editor override), an additional parent-child row is written to `ISeedLinkRepository` so `seed validate` can tell "inferred" from "chosen" — the presence of both stores means chosen, `ParentId` alone means inferred (`src/Twig/Commands/SeedNewCommand.cs:238-247`).
- No ADO calls. No `twig save` or `twig sync` is needed; the seed is immediately visible to `seed view`, `seed link`, and `seed publish`.

## Examples

Create a Task under the active item:

```
$ twig seed new --title "Wire audit trail into the batch endpoint" --type Task
Created local seed: #-42 Wire audit trail into the batch endpoint (Task)
  Parent: #5678 Batch API rework (from active item)
```

Create an orphan Bug with a description and a custom field:

```
$ twig seed new --title "Race in link publisher" --type Bug --no-parent \
    --description "Two publishes running back-to-back can double-link." \
    --field Custom.FalsificationCriteria="Repro from log shows single link only." \
    -o json
{"kind":"seedCreated","id":-43,"title":"Race in link publisher","type":"Bug","isSeed":true, ...}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Seed created (or editor cancelled).|`0`|
|Unknown field reference, invalid type, parent not cached, or seed factory error.|`1`|
|Malformed `--field`, missing title without `--editor`, `--no-parent` with `--parent`, or `--no-parent` without `--type`.|`2`|

## See also

- [`seed edit`](./seed-edit.md) — modify the seed you just created.
- [`seed link`](./seed-link.md) — attach dependencies before publishing.
- [`seed publish`](./seed-publish.md) — push the seed to Azure DevOps.
- [`seed`](./seed.md) — hidden shortcut equivalent to `seed new`.
