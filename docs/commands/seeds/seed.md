---
command: seed
group: seeds
summary: Hidden backward-compat shortcut for `seed new`.
stability: stable
mutates: local
---

# `twig seed`

Bare `twig seed "<title>"` is a **hidden** backward-compatibility shortcut for
[`twig seed new`](./seed-new.md). It routes into the same `SeedNewCommand` with the
same flags, so it creates a local seed under the active parent without touching
Azure DevOps. It stays in the surface so old scripts and finger memory keep working;
new work should call `seed new` explicitly.

The shortcut is `[Hidden]` in `Program.cs`, which excludes it from
`twig --help` output but still routes at the CLI parser (`src/Twig/Program.cs:807-810`).

## Synopsis

```
twig seed "<title>" [--type <type>] [--editor] [--parent <id> | --no-parent]
                    [--description <string>] [--field <ref>=<value> ...] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`title`|yes|Title for the new seed. Unlike `seed new --title`, this is a positional argument.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--type`|string|`null`|Work item type. Inferred from the parent when omitted; required with `--no-parent`.|
|`--editor`|bool|`false`|Open an external editor pre-filled with the seed template.|
|`--parent`|int|`null`|Parent work item ID. Defaults to the active work item.|
|`--no-parent`|bool|`false`|Create the seed with no parent. Mutually exclusive with `--parent`; requires `--type`.|
|`--description`|string|`null`|Description text. Converted to HTML only for HTML-typed `System.Description`.|
|`--field`|string[]|`null`|Set a field as `Reference.Name=value`. Repeatable.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Wires directly into `SeedNewCommand.ExecuteAsync` with the positional `title` in the same slot as `seed new --title` (`src/Twig/Program.cs:807-810`).
- Every behavior of [`seed new`](./seed-new.md) applies verbatim: field parsing, unknown-reference rejection, parent inference/announcement, orphan defaults, editor round-trip, and the explicit-parent link row (`src/Twig/Commands/SeedNewCommand.cs:53-247`).
- Because the shortcut takes `title` as a required positional, dropping the argument prints the ConsoleAppFramework "missing argument" error rather than `seed new`'s "Usage:" hint. Use `seed new --editor` when you want an editor-only flow.

## Examples

Terse creation under the active item:

```
$ twig seed "Wire audit trail into the batch endpoint"
Created local seed: #-42 Wire audit trail into the batch endpoint (Task)
  Parent: #5678 Batch API rework (from active item)
```

Same, but as a Bug and via editor:

```
$ twig seed "Race in link publisher" --type Bug --editor
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Seed created (or editor cancelled).|`0`|
|Unknown field reference, invalid type, parent not cached, or seed factory error.|`1`|
|Missing positional title, or the same flag validation errors as `seed new`.|`2`|

## See also

- [`seed new`](./seed-new.md) — the non-shortcut, non-hidden equivalent. Prefer it in new work.
- [`seed chain`](./seed-chain.md) — the batch equivalent for multiple linked seeds.
- [Seeds README](./README.md) — group overview.
