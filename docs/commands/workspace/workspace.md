---
command: workspace
group: workspace
summary: Show the current workspace.
stability: stable
mutates: local
---

# `twig workspace`

`workspace` is Twig's default view of your current working set: the sprint
items in the subscribed iterations, manually-tracked pins, seeds, and any
outstanding dirty rows. Reach for it when you want to see everything the
workspace considers "yours" without navigating into a specific work item.

By default the command reads from the local cache and renders the current Bench
as a live hierarchical tree. `--refresh` re-syncs from ADO before rendering.
`--view table` explicitly selects a table for the same current Bench, while
`--view tree` makes the hierarchy choice explicit. Both presentations include
manual pins, seeds, and the active item. `--tree` remains the legacy full-backlog
hierarchy mode, while `--all` remains the team/sprint layout.

The Tree view keeps parent/child connectors and indentation with the complete
work-item identity, including type and ID. The explicit table keeps State and
Age in separate columns, reserving their width before allocating the remaining
space to Title. Age displays elapsed time such as `15m ago`; fresh or unknown
ages remain blank. Titles wrap or truncate as the terminal narrows.
Use `--flat` to explicitly select the legacy non-tree table when no `--view`
flag is supplied.

Type cells include both the configured icon and the type name. The active item
has an arrow, bold text, and a contrasting blue cell background; selection does
not rely on color alone. Its metadata colors are adjusted for readability on
that background. Non-active rows keep the terminal's normal background.

## Synopsis

```
twig workspace [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|
|`--view`|`string`|unset (tree)|Explicit current-Bench presentation: `table` or `tree`; unset defaults to `tree`. Cannot be combined with `--all`, `--flat`, or `--tree`.|
|`--all`|`bool`|`false`|Show all team members' items, not just yours.|
|`--no-live`|`bool`|`false`|Disable live-refresh and render a static snapshot.|
|`--refresh`|`bool`|`false`|Sync from ADO before displaying, instead of reading cache only.|
|`--flat`|`bool`|`false`|Use flat (non-tree) output instead of hierarchical rendering.|
|`--tree`|`bool`|`false`|Render the full backlog hierarchy tree instead of the current-Bench view.|

## Behavior

The command delegates to `WorkspaceCommand.ExecuteAsync`
(`src/Twig/Commands/WorkspaceCommand.cs:100`). Key details:

- `--tree` and `--flat` are mutually exclusive; combining them prints
  `error: --tree and --flat are mutually exclusive.` and exits `1`
  (`src/Twig/Commands/WorkspaceCommand.cs:71-75`).
- `--view table` and `--view tree` keep the current Bench membership unchanged:
  pins, seeds, and the active item remain in scope. The default presentation is
  the same hierarchy as `--view tree`; parent IDs are used even when process
  metadata is absent, while metadata only controls working-level enrichment and
  virtual-group labels.
- `--view` is presentation-only and cannot be combined with `--tree`, `--flat`,
  `--all`, or sprint layout; those combinations exit `1` rather than changing
  the working set.
- `--tree` routes to `ExecuteTreeModeAsync`, which renders one tree per sprint
  root through the shared `TreeRenderingService`. Only the first tree in the
  list gets the refresh pass, to avoid redundant ADO round-trips
  (`src/Twig/Commands/WorkspaceCommand.cs:296-341`).
- Without `--tree`, the human-format path streams staged
  `SprintItemsLoaded` / `SeedsLoaded` / `RefreshStarted` / `RefreshCompleted`
  events into the Spectre live region; machine formats (`json`, `minimal`)
  fall through to `ExecuteSyncAsync`
  (`src/Twig/Commands/WorkspaceCommand.cs:100-292`).
- `--refresh` on the live path only triggers a fetch when the cache is
  considered stale (`Display.CacheStaleMinutes`, default derived from config)
  and updates `context.last_refreshed_at` on success; a refresh failure
  falls back to the cached rows rather than blanking the view
  (`src/Twig/Commands/WorkspaceCommand.cs:149-207`).
- The `--all` / sprint-layout branch forces the "team by assignee" grouping;
  otherwise the view is filtered to `Config.User.DisplayName`.

Side effects are limited to the local workspace: the SQLite cache under
`.twig/{org}/{project}/twig.db` and the `context.last_refreshed_at` key. No
work-item mutation is pushed to ADO.

## Examples

```
$ twig workspace
Sprint (Iteration \Sprint 42):
  ● #4211  Wire retry telemetry             Doing    You
    #4212  Retry policy unit tests          To do    You
Seeds:
  ~ seed:auth-refresh (parent #4200)
Tracked:
  ★ #3980  Auth loop repro                  Done     You
```

```
$ twig workspace --view table
```

```
$ twig workspace --view tree
```
The explicit tree keeps the current Bench (including pins and seeds) while
placing connectors and indentation before each work item's full identity.

```
$ twig workspace --tree --refresh -o json
{"workspace":{"iterations":["…\\Sprint 42"],"tree":[{"id":4211,"title":"Wire retry telemetry","children":[…]}], …}}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|`--view` combined with `--tree`, `--flat`, `--all`, or sprint layout|`1` with a usage error on stderr|
|Invalid `--view` value|`1` with `error: --view must be 'table' or 'tree'.` on stderr|
|`--tree` combined with `--flat`|`1` with `error: --tree and --flat are mutually exclusive.` on stderr|
|Tree rendering service unavailable when `--tree` is set|`1` with `error: Tree rendering is not available.` on stderr (`src/Twig/Commands/WorkspaceCommand.cs:296-302`)|
|Refresh path fails|`0`; the cached rows are shown instead of aborting|

## See also

- [`ws`](./ws.md) — short alias
- [`workspace track`](./track.md)
- [`workspace exclusions`](./exclusions.md)
- [`workspace area`](./area.md)
