---
command: nav
group: navigation
summary: Launch the interactive tree navigator.
stability: stable
mutates: local
---

# `twig nav`

Opens an interactive tree navigator anchored on the active work item. Use it
when you want to browse siblings, children, and ancestors visually rather than
issuing individual `nav up`/`nav down`/`nav next`/`nav prev` commands.

## Synopsis

```
twig nav
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
| — | — | — | — |

## Behavior

- Resolves an interactive renderer through
  `RenderingPipelineFactory`. When stdout is not a TTY, or the framework decides
  interactive rendering is unavailable, the command prints a hint pointing at
  the non-interactive `nav up`/`nav down`/`nav next`/`nav prev` verbs and
  returns exit code `0` without touching state
  (`src/Twig/Commands/NavigationCommands.cs:38-42`).
- Requires an active work item. If none is set, prints
  `No active context. Use 'twig set <id>' to select a work item first.` and
  returns `0` (`src/Twig/Commands/NavigationCommands.cs:45-50`).
- Loads the active item, its parent chain, its children, its siblings (either
  the parent's children, or the workspace root items when the active item has
  no parent), and both work-item links and seed links, then hands that
  `TreeNavigatorState` to the renderer
  (`src/Twig/Commands/NavigationCommands.cs:74-100`).
- If the user commits a selection, the command sets the active context to the
  selected ID, records a navigation history entry, and refreshes the prompt
  state file when a `IPromptStateWriter` is registered
  (`src/Twig/Commands/NavigationCommands.cs:62-69`).
- If the user quits without selecting, the active context is left untouched.

## Examples

Open the navigator anchored on the current active item:

```
$ twig nav
```

Non-interactive fallback (e.g. output is being piped to another tool):

```
$ twig nav | cat
Interactive navigation requires a terminal. Use: twig nav up, twig nav down, twig nav next, twig nav prev
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Selection committed or navigator quit without selecting|`0`|
|No TTY / no interactive renderer available|`0`, hint printed to stderr|
|No active work item set|`0`, hint printed to stderr|
|Active work item ID present but not found in the local cache|`1`, `Work item #<id> not found in cache. Run 'twig sync' to fetch.` on stderr|

## See also

- [`nav up`](./nav-up.md)
- [`nav down`](./nav-down.md)
- [`nav next`](./nav-next.md)
- [`nav prev`](./nav-prev.md)
- [`nav history`](./nav-history.md)
