---
command: workspace sprint add
group: workspace
summary: Add a sprint iteration expression to workspace configuration.
stability: stable
mutates: local
---

# `twig workspace sprint add`

Subscribe the workspace to a sprint iteration. The expression can be relative
(`@current`, `@current-1`, `@current+2`) or an absolute iteration path
(`Contoso\Sprint 42`). Relative expressions are resolved at every workspace
evaluation, so they automatically follow the calendar as sprints roll.

## Synopsis

```
twig workspace sprint add <expression> [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
|`<expression>`|yes|Sprint expression to subscribe to — e.g. `@current`, `@current-1`, or an absolute iteration path like `Contoso\Sprint 42`.|

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `SprintCommand.AddAsync`
(`src/Twig/Commands/SprintCommand.cs:28-56`):

1. Empty/whitespace expressions are rejected with a `sprintExpressionInvalid`
   error on stderr and exit `2`
   (`src/Twig/Commands/SprintCommand.cs:33-37`).
2. `config.Workspace.Sprints` is initialized on first use.
3. Case-insensitive duplicate check on `SprintEntry.Expression`. Duplicates
   emit `sprintAddDuplicate` at severity `info` (not an error) and exit `0`
   (`src/Twig/Commands/SprintCommand.cs:41-48`).
4. New expressions are appended and persisted via
   `config.SaveSplitAsync(paths, ct)`; the command emits a `sprintAdded`
   success record.

The command does not validate the expression against ADO — an invalid absolute
path is only surfaced later when the workspace tries to resolve it.

## Examples

```
$ twig workspace sprint add @current
Added sprint expression '@current'.
```

```
$ twig workspace sprint add "Contoso\Sprint 42" -o json
{"kind":"sprintAdded","expression":"Contoso\\Sprint 42","message":"Added sprint expression 'Contoso\\Sprint 42'."}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|Empty or whitespace expression|`2` with error on stderr|
|Duplicate expression (case-insensitive)|`0` with `sprintAddDuplicate` info outcome|

## See also

- [`workspace sprint remove`](./sprint-remove.md)
- [`workspace sprint list`](./sprint-list.md)
- [`workspace`](./workspace.md)
