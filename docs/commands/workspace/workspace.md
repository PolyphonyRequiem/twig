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

In terminals that support OSC 8 hyperlinks, published work-item rows in Tree
and IDs/titles in Table link to the corresponding Azure DevOps page. In Herdr,
Ctrl+click activates the link on the client desktop, including over SSH.
Local seeds have no published URL and are not linked.

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
|`--include-browser`|`bool`|`false`|With `--view tree -o json`, include the versioned semantic Bench hierarchy, native styled labels, explicit pins and inherited subtree provenance.|
|`--expect-binding`|`string`|unset|Refuse semantic browser reads unless the admitted binding matches this captured ID. Never selects an identity.|
|`--expect-identity`|`string`|unset|Refuse semantic browser reads unless the admitted identity matches this captured ID.|

The semantic browser envelope also includes `configuration`: a versioned snapshot
of the captured Bench's explicit pins (including uncached IDs), exact/under area
filters, sprint expressions, ownership summary, and raw saved-query settings digest.
These reads resolve expressions from the cached calendar and never replace the
Bench's settings with workspace-global area/sprint settings. Use
[`bench configuration`](../bench/README.md#configure-the-current-bench) for edits.

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
  otherwise the default self rule uses the bound account's canonical ADO identity,
  not `Config.User.DisplayName`. Published rows need server-provided canonical
  assignee metadata; display names, even ones resembling an email, cannot prove a
  self match. Older rows without that metadata need a refresh. Missing bound-user
  metadata refuses the self view rather than silently widening to the team.
  `--all` explicitly bypasses self lookup. Existing default Bench sprint rules
  are normalized without losing pins; explicit custom Bench rules remain unchanged.

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

## Bench-scoped pull: `twig workspace sync`

`workspace sync` is the browser's **pull-only** operation. Unlike ordinary
`twig sync --pull-only`, it does not invoke the broad iteration/area refresh.
It refreshes the current Bench's cached members and discovers candidates through
that Bench's saved selectors: legacy current-sprint and versioned bench-filter rules
retain their iteration, exact/under area and assignee intersection; item pins name
one item, and subtree pins follow their actual descendants. An empty sprint scope
never becomes an unbounded query. Scoped sync refreshes the cached iteration calendar.

Positive pending-work IDs remain protected, and active-context children/siblings
and ancestors are fetched only within the configured relationship depths. Seeds
stay local. This operation never flushes pending edits, applies a proposal,
changes the active item, or refreshes every unrelated cached item.

```powershell
twig workspace sync -o json
twig workspace sync --expect-bench 7 --expect-binding "<binding-id>" --expect-identity "<identity-id>" -o json
```

`--expect-bench`, `--expect-binding`, and `--expect-identity` are optional
captured-target preconditions. A mismatch refuses before remote work; they are
not authentication selectors. Success returns `kind: benchSync`, `benchId`,
`benchName`, `itemCount`, `memberCount`, `relationshipCount`, and `protectedCount`.
The member count includes mandatory pending-work rows; the relationship count
covers additional context. Protected local values are not overwritten. Runtime
or scope failures return `1`, with a format-aware diagnosis on stderr.

The shared browser uses local selection, folding, and mouse hit-testing over the
semantic snapshot. `p` chooses single-item or subtree pinning; `Shift+P` confirms
removal of both explicit pin kinds on the selected item. Neither operation removes
inherited membership. Seeds and pending rows omitted by a depth/working-level
presentation cutoff are promoted into visible roots rather than concealed.

## See also

- [`ws`](./ws.md) — short alias
- [`workspace track`](./track.md)
- [`workspace exclusions`](./exclusions.md)
- [`workspace area`](./area.md)
