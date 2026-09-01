---
command: config
group: configuration
summary: Read or set a configuration value.
stability: stable
mutates: local
---

# `twig config`

Read or write a single key on the split Twig configuration. In read mode the current value
is emitted; in write mode the value is persisted to the appropriate side of the split —
repo coordinates land in `twig.json`, per-user preferences land in `.twig/config` — and
`display.*` writes also refresh the Oh My Posh prompt-state cache so the shell segment
updates without a manual `twig sync`.

## Synopsis

```
twig config <key> [<value>] [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`<key>`|yes|Dot-path configuration key (e.g. `organization`, `git.project`, `display.icons`). The full accepted set is enumerated in `src/Twig/Commands/ConfigCommand.cs:115-147`.|
|`<value>`|no|Value to set. Omit for read mode.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`human` \| `json` \| `minimal`|`human`|Output format.|

## Behavior

- Read mode (`value` omitted) resolves the key against the split configuration and prints the
  current value. Unknown keys are rejected with exit code `1` and a stderr error
  (`src/Twig/Commands/ConfigCommand.cs:40-44`).
- Write mode calls `TwigConfiguration.SetValue`; on success the whole configuration is saved
  through `SaveSplitAsync` which writes both `twig.json` and `.twig/config` atomically
  (`src/Twig/Commands/ConfigCommand.cs:50-57`).
- A key prefixed with `display.` additionally invokes `IPromptStateWriter.WritePromptStateAsync`
  so the Oh My Posh segment is refreshed without a separate command
  (`src/Twig/Commands/ConfigCommand.cs:59-60`).
- Empty or whitespace-only keys exit `2` with a usage error
  (`src/Twig/Commands/ConfigCommand.cs:30-34`).
- Machine formats emit records under the tags `configValue` (read) and `configSet` (write)
  keyed by `key`, `value`, and (write only) `message` — see
  `src/Twig/Commands/ConfigCommand.cs:83-107`.

## Examples

Read the configured organization:

```
$ twig config organization
contoso
```

Set the default area path and see the confirmation payload as JSON:

```
$ twig config defaults.areapath "Contoso\\Team Alpha" -o json
{"kind":"configSet","key":"defaults.areapath","value":"Contoso\\Team Alpha","message":"Set defaults.areapath = Contoso\\Team Alpha"}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Successful read or write|`0`|
|Unknown key in read mode|`1` with stderr error|
|Unknown key or invalid value in write mode|`1` with stderr error|
|Missing key argument|`2` with usage error|

## See also

- [`config status-fields`](./config-status-fields.md)
- [`migrate-config`](./migrate-config.md)
- [`help`](./help.md)
