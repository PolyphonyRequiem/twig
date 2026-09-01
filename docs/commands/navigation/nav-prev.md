---
command: nav prev
group: navigation
summary: Set the active work item to the previous sibling.
stability: stable
mutates: local
---

# `twig nav prev`

Moves the active pointer to the previous sibling in the tree — following an
explicit predecessor link when one exists, or falling back to the parent's
children in display order.

## Synopsis

```
twig nav prev [-o|--output <human|json|minimal>]
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

- Requires an active work item with a parent
  (`src/Twig/Commands/NavigationCommands.cs:241-260`).
- **Link-based first.** For `nav prev`, the seed-link store is queried for a
  `Successor` link whose target is the active item (i.e., the active item is
  the *successor* of some earlier sibling), and the work-item link store is
  queried for a `Predecessor` link emanating from the active item
  (`src/Twig/Commands/NavigationCommands.cs:300-343`).
- **Fallback to display order.** When no explicit link exists, fetches all
  siblings from the workspace repository in display order and moves one slot
  backward (`src/Twig/Commands/NavigationCommands.cs:267-289`).
- On success, delegates the context change to `SetCommand.ExecuteAsync`, so a
  navigation history entry is recorded and the prompt state is refreshed.

## Examples

Step to the previous sibling under the current parent:

```
$ twig nav prev
● #4110  Task — Preflight for `batch` op class detection [Doing]
```

Step to the previous sibling as JSON:

```
$ twig nav prev --output json
{
  "id": 4110,
  "type": "Task",
  "title": "Preflight for `batch` op class detection",
  "state": "Doing"
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Sibling resolved and active context updated|`0`|
|No active work item set|`1`, `No active work item. Run 'twig set <id>' first.`|
|Active work item missing from cache after auto-fetch|`1`, `Work item #<id> not found in cache.`|
|Active item has no parent (top-level)|`1`, `Cannot navigate siblings — item has no parent.`|
|Active item is the first sibling and no predecessor link exists|`1`, `Already at first sibling under #<parent-id>.`|

## See also

- [`nav next`](./nav-next.md)
- [`nav up`](./nav-up.md)
- [`nav down`](./nav-down.md)
- [`prev`](./prev.md)
