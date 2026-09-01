---
command: sprint
group: views
summary: Show sprint items grouped by assignee.
stability: stable
mutates: local
---

# `twig sprint`

`twig sprint` is the top-level "what am I working on right now?" view. It reads the cached workspace, filters to items whose iteration matches the workspace's subscribed sprint expressions, and renders them grouped by assignee. By default you see only your own items; pass `--all` when you need the full team's slice of the sprint.

Internally this is the sprint-layout mode of the workspace command — the CLI entry point calls `WorkspaceCommand.ExecuteAsync(..., sprintLayout: true, ...)` (`src/Twig/Program.cs:1270-1271`), and the same `HumanOutputFormatter.FormatSprintView` path drives the rendering (`src/Twig/Commands/WorkspaceCommand.cs:432-434`). Use it when you want the sprint-grouped presentation without also opting into the workspace dashboard's tracking, seed, and dirty-orphan sections.

## Synopsis

```
twig sprint [-o|--output <format>] [--all] [--refresh] [--flat] [--tree]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| — | — | — |

## Flags

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output <format>` | string | `human` | Output format: `human`, `json`, or `minimal`. |
| `--all` | bool | `false` | Show all team members' sprint items, not just yours. Forces the sprint-grouped layout even in non-sprint fallback paths (`src/Twig/Commands/WorkspaceCommand.cs:322-323,432-434`). |
| `--refresh` | bool | `false` | Sync from ADO before displaying, instead of reading cache only. Triggers the same sync coordinator the workspace command uses. |
| `--flat` | bool | `false` | Use flat (non-tree) output instead of the hierarchical rendering. Mutually exclusive with `--tree`. |
| `--tree` | bool | `false` | Render the full backlog hierarchy tree instead of the sprint table. Mutually exclusive with `--flat`; combining them fails fast (`src/Twig/Commands/WorkspaceCommand.cs:54`). |

## Behavior

- Cache-first. `twig sprint` reads the local workspace cache (`.twig/{org}/{project}/twig.db`) and renders immediately. Without `--refresh` it never touches ADO.
- The sprint scope is defined by the workspace's `sprints` expressions (managed via `twig workspace sprint add|remove|list`). Items whose iteration matches those expressions land in the view; items outside them do not.
- Grouping is by assignee. Your own items always render; other assignees appear only under `--all`.
- `sprintLayout` forces every sprint invocation through the synchronous rendering path; it never uses the live Spectre renderer (`src/Twig/Commands/WorkspaceCommand.cs:67-72,231-232`).
- For machine formats, `--refresh` runs `ReadOnly.SyncWorkingSetAsync` before rendering. A refresh failure is non-fatal and falls back to cached data (`src/Twig/Commands/WorkspaceCommand.cs:287-303`). The human sprint path remains cache-based even when `--refresh` is supplied.
- The refresh path is read-only: it does not flush pending changes to ADO.
- `--tree` swaps the sprint table for the full backlog hierarchy tree. When combined with `--flat` the command exits with an error before touching the store (`src/Twig/Commands/WorkspaceCommand.cs:54`).

## Examples

### Look at your own sprint items

```
$ twig sprint
Sprint: Contoso\Release 1\Sprint 42

Daniel Green
  1234  Task   Doing   Wire status-field renderer
  1240  Bug    To do   Live layout jitters on resize
```

Reads the cache, filters to your assignee, groups the results, and prints. No ADO calls are made.

### Team snapshot as JSON after a refresh

```
$ twig sprint --all --refresh --output json
{
  "kind": "sprintView",
  "iteration": "Contoso\\Release 1\\Sprint 42",
  "groups": [
    { "assignee": "Daniel Green", "items": [ { "id": 1234, "type": "Task", "state": "Doing", "title": "Wire status-field renderer" } ] },
    { "assignee": "Sam Rivers",   "items": [ { "id": 1301, "type": "Bug",  "state": "Doing", "title": "Retry on 429" } ] }
  ]
}
```

`--refresh` runs a sync-first pass so the JSON snapshot reflects the latest ADO state; `--all` widens the grouping to every teammate with items in the sprint.

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Successful render | `0` |
| No workspace found | `1`, with a "workspace not found" hint on stderr. |
| `--flat` combined with `--tree` | `1`, before any store access (`src/Twig/Commands/WorkspaceCommand.cs:54`). |
| Refresh sync fails (`--refresh`) | Non-zero exit propagated from the sync coordinator; render is skipped. |
| Cache read or ADO fetch error | `1`, with the error surfaced through the formatter's error channel. |

## See also

- [`twig workspace`](../workspace/README.md) — full workspace dashboard the sprint view is layered on.
- [`twig workspace sprint add|remove|list`](../workspace/README.md) — manage the sprint expressions this view honors.
- [`twig tree`](./tree.md) — hidden alias for the hierarchy tree; note that `twig sprint --tree` is the supported spelling.
- [`twig show`](../context/show.md) — drill into a specific item from the sprint list.
