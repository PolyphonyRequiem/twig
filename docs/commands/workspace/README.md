# Workspace commands

The `workspace` group shows the local working set, pins items through the current
Bench, and manages sprint and area configuration. `workspace` on its own renders
the current view. It does not hide items matched by Bench selectors.

Workspace reads use the local cache; pin verbs update Bench selectors and area/sprint
verbs update workspace config. `workspace area sync` reads team area paths from
Azure DevOps to rebuild local configuration. No sub-command here pushes work-item
mutations to ADO.

## Commands

|Command|Summary|
|---|---|
|[`workspace`](./workspace.md)|Show the current workspace.|
|[`ws`](./ws.md)|Short alias for `workspace`.|
|[`workspace track`](./track.md)|Track a single work item by ID (pinned to workspace).|
|[`workspace track-tree`](./track-tree.md)|Track a work item and its subtree.|
|[`workspace untrack`](./untrack.md)|Remove a work item from tracking.|
|[`workspace area`](./area.md)|Show the area-filtered workspace view.|
|[`workspace area add`](./area-add.md)|Add an area path to workspace configuration.|
|[`workspace area remove`](./area-remove.md)|Remove an area path from workspace configuration.|
|[`workspace area list`](./area-list.md)|List configured area paths with match semantics.|
|[`workspace area sync`](./area-sync.md)|Fetch team area paths from ADO and replace configuration.|
|[`workspace sprint add`](./sprint-add.md)|Add a sprint iteration expression to workspace configuration.|
|[`workspace sprint remove`](./sprint-remove.md)|Remove a sprint iteration expression from workspace configuration.|
|[`workspace sprint list`](./sprint-list.md)|List configured sprint iteration expressions.|

## Deprecated aliases

The top-level `area` verbs are retained as hidden deprecated aliases that emit
a `hint:` line on stderr and then delegate to the canonical `workspace area`
implementation (`src/Twig/Program.cs:1214-1262`). New scripts and skills SHOULD
use `workspace area …` instead.

|Deprecated alias|Canonical form|
|---|---|
|[`area`](./area-deprecated.md)|[`workspace area`](./area.md)|
|[`area add`](./area-add-deprecated.md)|[`workspace area add`](./area-add.md)|
|[`area remove`](./area-remove-deprecated.md)|[`workspace area remove`](./area-remove.md)|
|[`area list`](./area-list-deprecated.md)|[`workspace area list`](./area-list.md)|
|[`area sync`](./area-sync-deprecated.md)|[`workspace area sync`](./area-sync.md)|
