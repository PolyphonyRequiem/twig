---
command: nav back
group: navigation
summary: Move the navigation-history cursor one entry backward.
stability: stable
mutates: local
---

# `twig nav back`

Walks the chronological navigation history one step backward. Unlike the tree
navigation commands, `nav back` sets the active context directly and does
**not** record a new history entry — the cursor moves within the existing
stack rather than truncating it.

## Synopsis

```
twig nav back [-o|--output <human|json|minimal>]
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
|`-o`, `--output`|`string`|`human`|Output format used for error messages. The success line is a fixed one-line target string; see below.|

## Behavior

- Delegates to `INavigationHistoryStore.GoBackAsync`. When the cursor is
  already at the oldest entry, prints an error and returns `1`
  (`src/Twig/Commands/NavigationHistoryCommands.cs:30-35`).
- Resolves seed IDs at read time via `IPublishIdMapRepository`: negative IDs
  in the history store are staged-alias placeholders that map to positive ADO
  IDs once the seed has been published (DD-05,
  `src/Twig/Commands/NavigationHistoryCommands.cs:37-38`,
  `src/Twig/Commands/NavigationHistoryCommands.cs:209-224`).
- Writes the active context directly through `IContextStore`, bypassing
  `SetCommand` so that no new navigation history entry is recorded — this is
  what allows repeated `nav back`/`nav fore` to walk the stack without
  destroying forward history (DD-04,
  `src/Twig/Commands/NavigationHistoryCommands.cs:40-41`).
- Prints a fixed one-line target descriptor: `#<id> <Type> — <Title> [<State>]`
  when the item is present in the cache, or just `#<id>` when it is not
  (`src/Twig/Commands/NavigationHistoryCommands.cs:52-55`).
- Refreshes the prompt state file when a `IPromptStateWriter` is registered.

## Examples

Step backward through history:

```
$ twig nav back
#4102 Task — Wire batch preflight into publish [Doing]
```

Attempt to go back with an empty or exhausted history:

```
$ twig nav back
error: Already at oldest entry in navigation history.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Cursor moved back and active context updated|`0`|
|Cursor already at oldest entry|`1`, `Already at oldest entry in navigation history.`|

## See also

- [`nav fore`](./nav-fore.md)
- [`nav history`](./nav-history.md)
- [`back`](./back.md)
