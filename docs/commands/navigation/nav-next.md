---
command: nav next
group: navigation
summary: Set the active work item to the next sibling.
stability: stable
mutates: local
---

# `twig nav next`

Moves the active pointer to the next sibling in the tree — following an
explicit successor link when one exists, or falling back to the parent's
children in display order.

## Synopsis

```
twig nav next [-o|--output <human|json|minimal>]
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

- Requires an active work item that has a parent — siblings are children of
  a parent, so a top-level item cannot navigate siblings
  (`src/Twig/Commands/NavigationCommands.cs:241-260`).
- **Link-based first.** Looks for a successor sibling by consulting first the
  seed-link store (covering unpublished seeds) and then the work-item link
  store (covering published items). For `nav next`, this is a `Successor`
  seed-link whose source is the active item, or a `Successor` work-item link
  emanating from the active item
  (`src/Twig/Commands/NavigationCommands.cs:300-343`).
- **Fallback to display order.** When no explicit successor link exists,
  fetches all siblings from the workspace repository (already returned in
  display order), locates the active item's index, and moves one slot
  forward (`src/Twig/Commands/NavigationCommands.cs:267-289`).
- On success, delegates the context change to `SetCommand.ExecuteAsync`, so a
  navigation history entry is recorded and the prompt state is refreshed.

## Examples

Advance to the next sibling by display order:

```
$ twig nav next
● #4111  Task — Preflight retry telemetry [To Do]
```

Advance to the next sibling and print the identifier only, for scripting:

```
$ twig nav next --output minimal
4111
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Sibling resolved and active context updated|`0`|
|No active work item set|`1`, `No active work item. Run 'twig set <id>' first.`|
|Active work item missing from cache after auto-fetch|`1`, `Work item #<id> not found in cache.`|
|Active item has no parent (top-level)|`1`, `Cannot navigate siblings — item has no parent.`|
|Active item is the last sibling and no successor link exists|`1`, `Already at last sibling under #<parent-id>.`|

## See also

- [`nav prev`](./nav-prev.md)
- [`nav up`](./nav-up.md)
- [`nav down`](./nav-down.md)
- [`next`](./next.md)
