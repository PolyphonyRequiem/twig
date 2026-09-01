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

By default the command reads from the local cache and renders a live Spectre
region. `--refresh` re-syncs from ADO before rendering, `--tree` swaps the
default table for a full-backlog hierarchy, and `--all` widens the view to
every team member's items instead of just yours.

## Synopsis

```
twig workspace [flags]
```

## Arguments

|Argument|Required|Description|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|
|`--all`|`bool`|`false`|Show all team members' items, not just yours.|
|`--no-live`|`bool`|`false`|Disable live-refresh and render a static snapshot.|
|`--refresh`|`bool`|`false`|Sync from ADO before displaying, instead of reading cache only.|
|`--flat`|`bool`|`false`|Use flat (non-tree) output instead of hierarchical rendering.|
|`--tree`|`bool`|`false`|Render full backlog hierarchy tree instead of workspace table.|

## Behavior

The command delegates to `WorkspaceCommand.ExecuteAsync`
(`src/Twig/Commands/WorkspaceCommand.cs:52`). Key details:

- `--tree` and `--flat` are mutually exclusive; combining them prints
  `error: --tree and --flat are mutually exclusive.` and exits `1`
  (`src/Twig/Commands/WorkspaceCommand.cs:54-58`).
- `--tree` routes to `ExecuteTreeModeAsync`, which renders one tree per sprint
  root through the shared `TreeRenderingService`. Only the first tree in the
  list gets the refresh pass, to avoid redundant ADO round-trips
  (`src/Twig/Commands/WorkspaceCommand.cs:240-283`).
- Without `--tree`, the human-format path streams staged
  `SprintItemsLoaded` / `SeedsLoaded` / `RefreshStarted` / `RefreshCompleted`
  events into the Spectre live region; machine formats (`json`, `minimal`)
  fall through to `ExecuteSyncAsync`
  (`src/Twig/Commands/WorkspaceCommand.cs:60-232`).
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
$ twig workspace --tree --refresh -o json
{"workspace":{"iterations":["…\\Sprint 42"],"tree":[{"id":4211,"title":"Wire retry telemetry","children":[…]}], …}}
```

## Exit codes and failure modes

|Condition|Result|
|Success|`0`|
|`--tree` combined with `--flat`|`1` with `error: --tree and --flat are mutually exclusive.` on stderr|
|Tree rendering service unavailable when `--tree` is set|`1` with `error: Tree rendering is not available.` on stderr (`src/Twig/Commands/WorkspaceCommand.cs:242-247`)|
|Refresh path fails|`0`; the cached rows are shown instead of aborting (`src/Twig/Commands/WorkspaceCommand.cs:180-197`)|

## See also

- [`ws`](./ws.md) — short alias
- [`workspace track`](./workspace-track.md)
- [`workspace exclusions`](./workspace-exclusions.md)
- [`workspace area`](./workspace-area.md)
