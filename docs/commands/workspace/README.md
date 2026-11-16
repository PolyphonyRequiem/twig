# Workspace view and selection

`workspace` (also `ws`) shows a cached projection of the selected user-wide Bench,
not automatically every item assigned to you in a sprint.

| Route | Purpose |
| --- | --- |
| [`workspace`](workspace.md), [`ws`](ws.md) | Static cached membership and protected drafts |
| `workspace sync` | Pull selected Bench scope without flushing edits |
| [`workspace track`](track.md), [`track-tree`](track-tree.md), [`untrack`](untrack.md) | Shared explicit selectors |
| [`workspace exclude`](exclude.md), [`exclusions`](exclusions.md) | Local view exclusions, not shared membership removal |
| [`workspace area`](area.md) and area add/remove/list/sync | Retained ADO planning-rule management/metadata |
| `workspace sprint add/remove/list` | Retained ADO iteration planning rules |

See [Bench planning](../bench/planning.md) for rule conjunction/union and captured
revision requirements. These routes no longer edit a legacy repository config
list or replace rules with today's query results.

Machine views/pulls do not expand active-item context. `--tree`, `--all`,
`--live`, workspace migration and old UI envelopes are retired. GitHub allows the
narrow static view, single-item pins and exclusions, not ADO planning/sync parity.
