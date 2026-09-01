# Workspace commands

The `workspace` group is Twig's canonical entry point for viewing and shaping
the local working set: what appears in the workspace view, which items are
pinned or hidden, and which sprints and area paths the workspace subscribes to.
`workspace` on its own renders the current view; the sub-commands mutate the
workspace configuration or the local tracking tables.

Everything under `workspace` operates on cache and workspace config. The one
exception is `workspace area sync`, which reads team area paths from Azure
DevOps to rebuild the local configuration. No sub-command in this group pushes
work-item mutations to ADO.

## Commands

|Command|Summary|
|---|---|
|[`workspace`](./workspace.md)|Show the current workspace.|
|[`ws`](./ws.md)|Short alias for `workspace`.|
|[`workspace track`](./workspace-track.md)|Track a single work item by ID (pinned to workspace).|
|[`workspace track-tree`](./workspace-track-tree.md)|Track a work item and its subtree.|
|[`workspace untrack`](./workspace-untrack.md)|Remove a work item from tracking.|
|[`workspace exclude`](./workspace-exclude.md)|Exclude a work item from workspace view.|
|[`workspace exclusions`](./workspace-exclusions.md)|List, clear, or remove exclusions.|
|[`workspace area`](./workspace-area.md)|Show the area-filtered workspace view.|
|[`workspace area add`](./workspace-area-add.md)|Add an area path to workspace configuration.|
|[`workspace area remove`](./workspace-area-remove.md)|Remove an area path from workspace configuration.|
|[`workspace area list`](./workspace-area-list.md)|List configured area paths with match semantics.|
|[`workspace area sync`](./workspace-area-sync.md)|Fetch team area paths from ADO and replace configuration.|
|[`workspace sprint add`](./workspace-sprint-add.md)|Add a sprint iteration expression to workspace configuration.|
|[`workspace sprint remove`](./workspace-sprint-remove.md)|Remove a sprint iteration expression from workspace configuration.|
|[`workspace sprint list`](./workspace-sprint-list.md)|List configured sprint iteration expressions.|

## Deprecated aliases

The top-level `area` verbs are retained as hidden deprecated aliases that emit
a `hint:` line on stderr and then delegate to the canonical `workspace area`
implementation (`src/Twig/Program.cs:1214-1262`). New scripts and skills SHOULD
use `workspace area …` instead.

|Deprecated alias|Canonical form|
|---|---|
|[`area`](./area-deprecated.md)|[`workspace area`](./workspace-area.md)|
|[`area add`](./area-add-deprecated.md)|[`workspace area add`](./workspace-area-add.md)|
|[`area remove`](./area-remove-deprecated.md)|[`workspace area remove`](./workspace-area-remove.md)|
|[`area list`](./area-list-deprecated.md)|[`workspace area list`](./workspace-area-list.md)|
|[`area sync`](./area-sync-deprecated.md)|[`workspace area sync`](./workspace-area-sync.md)|
