---
command: nav history
group: navigation
summary: Display or pick from the navigation history stack.
stability: stable
mutates: local
---

# `twig nav history`

Shows every entry recorded in the local navigation history, marking the
current cursor position. When a TTY is available and `--non-interactive` is
not passed, opens a picker that lets you jump to any past entry — the
selection is treated as a new visit, so it records a fresh history entry and
prunes anything that was previously *forward* of the cursor.

## Synopsis

```
twig nav history [--non-interactive] [-o|--output <human|json|minimal>]
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
|`--non-interactive`|`bool`|`false`|Skip the interactive picker and print the flat list, even on a TTY.|
|`-o`, `--output`|`string`|`human`|Output format. `human` renders a padded list or interactive picker; `json` emits `{entries, currentEntryId}`; `minimal` emits one resolved work-item ID per line.|

## Behavior

- Loads every entry from `INavigationHistoryStore.GetHistoryAsync` together
  with the current cursor entry ID
  (`src/Twig/Commands/NavigationHistoryCommands.cs:91`).
- An empty history prints an info line and returns `0`
  (`src/Twig/Commands/NavigationHistoryCommands.cs:93-97`).
- Resolves seed IDs at read time (DD-05), then batch-fetches the work items
  they reference to avoid N+1 lookups — items that are no longer in the cache
  are rendered with just their resolved ID
  (`src/Twig/Commands/NavigationHistoryCommands.cs:99-117`).
- **`json` output.** Emits `{ "entries": [ { "id", "workItemId", "visitedAt" }
  ... ], "currentEntryId": <int|null> }` (pretty-printed, ISO-8601
  timestamps). Non-interactive by construction
  (`src/Twig/Commands/NavigationHistoryCommands.cs:120-145`).
- **`minimal` output.** Emits one resolved work-item ID per line, in history
  order. Non-interactive by construction
  (`src/Twig/Commands/NavigationHistoryCommands.cs:148-153`).
- **`human` output.** Uses the interactive picker when a renderer is
  available and `--non-interactive` was not passed; otherwise prints a padded
  flat list titled `Navigation History (<n> entries):`, marking the current
  entry with `→`
  (`src/Twig/Commands/NavigationHistoryCommands.cs:156-201`).
- **Selection.** Committing a pick in the interactive picker updates the
  active context via `IContextStore` **and** records a new visit via
  `INavigationHistoryStore.RecordVisitAsync` — per FR-08, this is a new
  history entry that prunes anything forward of the previous cursor
  (`src/Twig/Commands/NavigationHistoryCommands.cs:171-184`). Quitting the
  picker without selecting leaves state untouched.

## Examples

Print the flat history list explicitly (safe in scripts):

```
$ twig nav history --non-interactive
Navigation History (3 entries):
    #4090  ● Epic — Preflight infrastructure [Doing]        2026-08-31 14:02
    #4102  ● Task — Wire batch preflight into publish [Doing]  2026-08-31 14:07
  → #4110  ● Task — Preflight for `batch` op class detection [Doing]  2026-09-01 09:41
```

Emit the same data as JSON:

```
$ twig nav history --output json
{
  "entries": [
    { "id": 17, "workItemId": 4090, "visitedAt": "2026-08-31T14:02:11.000Z" },
    { "id": 18, "workItemId": 4102, "visitedAt": "2026-08-31T14:07:29.000Z" },
    { "id": 19, "workItemId": 4110, "visitedAt": "2026-09-01T09:41:04.000Z" }
  ],
  "currentEntryId": 19
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|List rendered or picker completed (with or without a selection)|`0`|
|History is empty|`0`, `Navigation history is empty.` on stderr|

## See also

- [`nav back`](./nav-back.md)
- [`nav fore`](./nav-fore.md)
- [`nav`](./nav.md)
