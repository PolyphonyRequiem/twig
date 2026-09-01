---
command: seed edit
group: seeds
summary: Edit a seed's fields in an external editor.
stability: stable
mutates: local
---

# `twig seed edit`

Opens a local seed in your configured external editor, parses the edited buffer, and
saves the changes back to the workspace cache. No ADO interaction.

## Synopsis

```
twig seed edit <id> [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`id`|yes|Seed ID to edit. Must be a negative ID belonging to a local seed.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Loads the seed via `IWorkItemRepository.GetByIdAsync`. Fails with exit `1` if the ID is not in the cache or the target is not a seed — published items must be edited through `twig update` (`src/Twig/Commands/SeedEditCommand.cs:37-48`).
- Generates the editor buffer via `SeedEditorFormat.Generate` using the current field definitions, launches `IEditorLauncher`, and diffs the parsed result against the on-disk seed by field reference name (`src/Twig/Commands/SeedEditCommand.cs:50-78`).
- A cancelled editor exits `0` with a `Seed edit cancelled` info line; a no-op edit exits `0` with `No changes detected`. Only when at least one field differs does the seed get re-saved (`src/Twig/Commands/SeedEditCommand.cs:54-84`).
- Title is treated as a first-class field. If `System.Title` is present in the parsed buffer and non-blank, it replaces the old title; the title change counts toward the changed-fields tally.
- The write goes through `WorkItem.TryWithSeedFields`, which enforces seed field invariants (readonly reference names, seed-permitted fields). A validator failure aborts with exit `1` and the domain error message.

## Examples

Edit seed `#-42`:

```
$ twig seed edit -42
Updated seed #-42 Wire audit trail into the batch endpoint (3 field(s) changed)
```

Machine-readable form:

```
$ twig seed edit -42 -o json
{"kind":"seedUpdated","id":-42,"title":"Wire audit trail into the batch endpoint","changedCount":3, ...}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Seed updated, editor cancelled, or no-op edit.|`0`|
|Seed not found, target is not a seed, or domain validation of the parsed fields fails.|`1`|

## See also

- [`seed new`](./seed-new.md) — the counterpart that creates a seed and can also open the editor via `--editor`.
- [`seed view`](./seed-view.md) — inspect the seed dashboard before editing.
- [`seed discard`](./seed-discard.md) — delete a seed instead of editing it.
