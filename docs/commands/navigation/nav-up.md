---
command: nav up
group: navigation
summary: Set the active work item to the parent of the current one.
stability: stable
mutates: local
---

# `twig nav up`

Moves the active work item pointer one hop up the parent chain. This is a
non-interactive shortcut for `twig set <parent-id>` when you already know the
active item has a parent.

## Synopsis

```
twig nav up [-o|--output <human|json|minimal>]
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
|`-o`, `--output`|`string`|`human`|Output format for the resulting active-item render. One of `human`, `json`, `minimal`.|

## Behavior

- Reads the active work item ID from the context store; errors when none is
  set (`src/Twig/Commands/NavigationCommands.cs:108-113`).
- Resolves the active item via `ActiveItemResolver`, which auto-fetches from
  ADO on cache miss (`src/Twig/Commands/NavigationCommands.cs:115-121`).
- Loads the parent chain and children so that `WorkTree.Build` can compute the
  correct navigation target
  (`src/Twig/Commands/NavigationCommands.cs:123-130`).
- Delegates the actual context change to `SetCommand.ExecuteAsync`, so the
  transition is treated exactly like a manual `twig set <parent-id>` — it
  records a navigation history entry and refreshes the prompt state
  (`src/Twig/Commands/NavigationCommands.cs:137`).

## Examples

Move up one level and render the new active item in the human format:

```
$ twig nav up
● #4102  Task — Wire batch preflight into publish [Doing]
   Parent: #4090  Epic — Preflight infrastructure
```

Move up and emit JSON, e.g. for consumption by a shell prompt:

```
$ twig nav up --output json
{
  "id": 4090,
  "type": "Epic",
  "title": "Preflight infrastructure",
  "state": "Doing"
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Parent found and active context updated|`0`|
|No active work item set|`1`, `No active work item. Run 'twig set <id>' first.`|
|Active work item missing from cache after auto-fetch|`1`, `Work item #<id> not found in cache.`|
|Active work item has no parent|`1`, `Already at root — no parent to navigate to.`|

## See also

- [`nav down`](./nav-down.md)
- [`nav next`](./nav-next.md)
- [`nav prev`](./nav-prev.md)
- [`up`](./up.md)
