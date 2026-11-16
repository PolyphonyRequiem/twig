# workspace commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`workspace`](workspace.md) | Read cached Core Bench membership and all protected drafts as a static table. Acquires provider schema metadata. |
| [`ws`](ws.md) | Read the static cached Core Bench workspace (short alias); acquires provider schema metadata. |
| [`workspace track`](track.md) | Track a single work item by ID (pinned to workspace). |
| [`workspace track-tree`](track-tree.md) | Track a work item and its subtree. |
| [`workspace untrack`](untrack.md) | Remove explicit pins, optionally only single or tree; inherited membership remains. |
| [`workspace sync`](workspace-sync.md) | Pull only the current Bench's query, pin, and bounded relationship scope; never flush pending writes. |
| [`workspace exclude`](exclude.md) | Exclude a work item from workspace view. |
| [`workspace exclusions`](exclusions.md) | List all excluded work items. |
| [`workspace area`](area.md) | Show the current Bench's cached view, including its shared planning rules and protected drafts. |
| [`workspace area add`](area-add.md) | Add an area to the current Bench's planning rule; areas are OR, area/iteration/ownership categories are AND. |
| [`workspace area remove`](area-remove.md) | Remove one area from the current Bench's planning rule; other pins, rules and workspace exclusions are preserved. |
| [`workspace area list`](area-list.md) | List saved shared area paths and exact/under semantics without fetching items. |
| [`workspace area sync`](area-sync.md) | Refresh native planning metadata only; does not replace or populate shared Bench rules. |
| [`workspace sprint add`](sprint-add.md) | Add an iteration expression to the current Bench's planning rule; union iterations and preserve area/ownership constraints. |
| [`workspace sprint remove`](sprint-remove.md) | Remove one saved iteration expression; never replace the last rule with an all-items query. |
| [`workspace sprint list`](sprint-list.md) | List saved absolute/relative iteration expressions and canonical teams without remote item acquisition. |
