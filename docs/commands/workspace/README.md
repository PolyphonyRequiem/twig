# Workspace views

`workspace --bench <name-or-id>` (also `ws --bench <name-or-id>`) shows a cached projection of that user-wide Bench,
not automatically every item assigned to you in a sprint.

| Route | Purpose |
| --- | --- |
| [`workspace`](workspace.md), [`ws`](ws.md) | Static cached membership and protected drafts |
| `workspace --bench MyWork --refresh` | Pull permitted members of that Bench without flushing edits |
| [`workspace exclude`](exclude.md), [`exclusions`](exclusions.md) | Local view exclusions, not shared membership removal |
| [`workspace area sync`](area-sync.md) | Refresh ADO planning metadata without editing Bench rules |

See [Bench planning](../bench/planning.md) for rule conjunction/union and captured
revision requirements. These routes no longer edit a legacy repository config
list or replace rules with today's query results.

CLI views and pulls never expand an active item. `track`, `track-tree`, `untrack`,
contextual area/sprint edits, `--tree`, `--all`, `--live`, workspace migration
and old UI envelopes are retired. GitHub supports a narrow static view and local
exclusions, not ADO planning or Bench pulls.
