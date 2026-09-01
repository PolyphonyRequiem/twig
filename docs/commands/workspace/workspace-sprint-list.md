---
command: workspace sprint list
group: workspace
summary: List configured sprint iteration expressions.
stability: stable
mutates: none
---

# `twig workspace sprint list`

Print every configured sprint expression. Useful for confirming which
iterations the workspace view is currently resolving.

## Synopsis

```
twig workspace sprint list [flags]
```

## Arguments

|Argument|Required|Description|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `SprintCommand.ListAsync`
(`src/Twig/Commands/SprintCommand.cs:86-131`):

- On machine formats (`json`, `json-full`, `json-compact`, `ids`), emit a
  `sprintList` Document with a `count` field and an `entries` table whose
  rows carry the `expression` cell
  (`src/Twig/Commands/SprintCommand.cs:92-114`).
- On human format with no entries, emit
  `No sprint expressions configured. Use 'twig workspace sprint add <expr>' to configure.`
  as an info-severity node
  (`src/Twig/Commands/SprintCommand.cs:116-123`).
- Otherwise stream each expression followed by an
  `N sprint expression(s) configured.` footer
  (`src/Twig/Commands/SprintCommand.cs:125-129`).

Does not read from ADO or mutate configuration.

## Examples

```
$ twig workspace sprint list
@current
@current-1
2 sprint expression(s) configured.
```

```
$ twig workspace sprint list -o json
{"sprintList":{"count":2,"entries":[{"expression":"@current"},{"expression":"@current-1"}]}}
```

## Exit codes and failure modes

|Condition|Result|
|Success|`0`|

## See also

- [`workspace sprint add`](./workspace-sprint-add.md)
- [`workspace sprint remove`](./workspace-sprint-remove.md)
