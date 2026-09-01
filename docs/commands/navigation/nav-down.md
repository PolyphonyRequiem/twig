---
command: nav down
group: navigation
summary: Set the active work item to one of the current item's children.
stability: stable
mutates: local
---

# `twig nav down`

Descends into the children of the active work item. Without an argument it
picks the sole child automatically or prompts an interactive picker when
there are several; with an argument it accepts either a child ID or a title
substring pattern.

## Synopsis

```
twig nav down [<idOrPattern>] [-o|--output <human|json|minimal>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`<idOrPattern>`|no|Child work item ID or title substring. When omitted, the command picks the only child, prompts for one interactively when multiple exist, or errors when there are no children.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`string`|`human`|Output format for the resulting active-item render. One of `human`, `json`, `minimal`.|

## Behavior

- Requires an active work item; auto-fetches it on cache miss via
  `ActiveItemResolver`
  (`src/Twig/Commands/NavigationCommands.cs:147-160`).
- Builds a `WorkTree` from the active item's parent chain and children so that
  disambiguation and pattern matching operate against exactly the same
  candidate set the tree renderer would show
  (`src/Twig/Commands/NavigationCommands.cs:162-167`).
- **No argument path.** If the active item has no children, prints an error
  and returns `1`. With exactly one child, delegates immediately to
  `SetCommand`. With multiple children, either drives the interactive
  disambiguation prompt (when a renderer is available) or writes the
  formatter's disambiguation list to stderr and returns `1`
  (`src/Twig/Commands/NavigationCommands.cs:170-190`).
- **Pattern path.** Passes the raw pattern to `WorkTree.FindByPattern`, then
  branches on the returned `SingleMatch` / `MultipleMatches` / `NoMatch`
  result. Multi-match displays are enriched with per-child state before being
  shown (`src/Twig/Commands/NavigationCommands.cs:193-226`).
- On success, delegates the context change to `SetCommand.ExecuteAsync`, so a
  navigation history entry is recorded and the prompt state is refreshed.

## Examples

Descend into the only child of the active item:

```
$ twig nav down
● #4110  Task — Preflight for `batch` op class detection [Doing]
```

Pick a specific child by title substring — matches multiple, so the picker
opens:

```
$ twig nav down preflight
Multiple children match 'preflight':
  #4110  Preflight for `batch` op class detection [Doing]
  #4111  Preflight retry telemetry [To Do]
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Child resolved and active context updated|`0`|
|No active work item set|`1`, `No active work item. Run 'twig set <id>' first.`|
|Active work item missing from cache after auto-fetch|`1`, `Work item #<id> not found in cache.`|
|No children to descend into|`1`, `No children to navigate to.`|
|Pattern matched nothing|`1`, `No child matches '<pattern>'.`|
|Multiple matches without interactive renderer|`1`, disambiguation list printed to stderr|
|User declined the interactive disambiguation prompt|`1`|

## See also

- [`nav up`](./nav-up.md)
- [`nav next`](./nav-next.md)
- [`nav prev`](./nav-prev.md)
- [`down`](./down.md)
