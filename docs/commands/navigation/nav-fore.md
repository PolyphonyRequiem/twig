---
command: nav fore
group: navigation
summary: Move the navigation-history cursor one entry forward.
stability: stable
mutates: local
---

# `twig nav fore`

Walks the chronological navigation history one step forward. Like `nav back`,
it sets the active context directly rather than recording a new visit — the
cursor moves within the existing stack, so a subsequent `nav back` returns to
the same place.

## Synopsis

```
twig nav fore [-o|--output <human|json|minimal>]
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

- Delegates to `INavigationHistoryStore.GoForwardAsync`. When the cursor is
  already at the newest entry, prints an error and returns `1`
  (`src/Twig/Commands/NavigationHistoryCommands.cs:62-67`).
- Resolves seed IDs at read time via `IPublishIdMapRepository` (DD-05,
  `src/Twig/Commands/NavigationHistoryCommands.cs:69-70`).
- Writes the active context directly through `IContextStore`, bypassing
  `SetCommand` so that no new navigation history entry is recorded (DD-04,
  `src/Twig/Commands/NavigationHistoryCommands.cs:72-73`).
- Prints a fixed one-line target descriptor: `#<id> <Type> — <Title> [<State>]`
  when the item is present in the cache, or `#<id>` when it is not
  (`src/Twig/Commands/NavigationHistoryCommands.cs:52-55`).
- Refreshes the prompt state file when a `IPromptStateWriter` is registered.

## Examples

Step forward one entry after having gone back:

```
$ twig nav fore
#4110 Task — Preflight for `batch` op class detection [Doing]
```

Attempt to go forward when already at the head of the stack:

```
$ twig nav fore
error: Already at newest entry in navigation history.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Cursor moved forward and active context updated|`0`|
|Cursor already at newest entry|`1`, `Already at newest entry in navigation history.`|

## See also

- [`nav back`](./nav-back.md)
- [`nav history`](./nav-history.md)
- [`fore`](./fore.md)
