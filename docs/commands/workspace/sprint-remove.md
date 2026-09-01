---
command: workspace sprint remove
group: workspace
summary: Remove a sprint iteration expression from workspace configuration.
stability: stable
mutates: local
---

# `twig workspace sprint remove`

Unsubscribe the workspace from a sprint iteration. The expression is matched
case-insensitively against the stored `Expression` — pass it exactly as
`workspace sprint list` prints it.

## Synopsis

```
twig workspace sprint remove <expression> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<expression>`|yes|Sprint expression to remove. Matched case-insensitively against the configured entries.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `SprintCommand.RemoveAsync`
(`src/Twig/Commands/SprintCommand.cs:59-83`):

1. If no sprint expressions are configured, emit
   `No sprint expressions configured.` on stderr and exit `1`
   (`src/Twig/Commands/SprintCommand.cs:63-67`).
2. Case-insensitive `FindIndex` on `Workspace.Sprints[].Expression`. Not found
   fails with `Sprint expression '<expr>' is not configured.` on stderr and
   exit `1` (`src/Twig/Commands/SprintCommand.cs:69-76`).
3. Remove at the found index and persist via
   `config.SaveSplitAsync(paths, ct)`.
4. Emit a `sprintRemoved` success record echoing the removed expression.

No ADO calls are made.

## Examples

```
$ twig workspace sprint remove @current
Removed sprint expression '@current'.
```

```
$ twig workspace sprint remove "Contoso\Sprint 42" -o json
{"kind":"sprintRemoved","expression":"Contoso\\Sprint 42","message":"Removed sprint expression 'Contoso\\Sprint 42'."}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|No sprint expressions configured|`1` with `No sprint expressions configured.` on stderr|
|Expression not found (case-insensitive)|`1` with error on stderr|

## See also

- [`workspace sprint add`](./sprint-add.md)
- [`workspace sprint list`](./sprint-list.md)
