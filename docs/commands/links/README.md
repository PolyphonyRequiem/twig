# Links commands

Manage the edges that connect published Azure DevOps work items — hierarchy
(parent/child), non-hierarchy relations (`predecessor`, `successor`, `related`),
and artifact links (URLs and `vstfs://` URIs). All verbs in this group operate
on already-published items; virtual links between local seeds live under
`seed link` / `seed unlink` instead.

Every command in this group mutates ADO immediately: the edge is added or
removed on the remote item, both endpoints are resynced into the local cache,
and the resulting link set is rendered. There is no staging step.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`link parent`](./link-parent.md)|Set the parent of the active (or targeted) work item.|ado|
|[`link unparent`](./link-unparent.md)|Remove the parent link from the active (or targeted) work item.|ado|
|[`link reparent`](./link-reparent.md)|Remove the current parent and set a new one.|ado|
|[`link predecessor`](./link-predecessor.md)|Mark the active (or targeted) item as blocked by another.|ado|
|[`link successor`](./link-successor.md)|Mark the active (or targeted) item as blocking another.|ado|
|[`link related`](./link-related.md)|Add a symmetric Related edge between two items.|ado|
|[`link unrelate`](./link-unrelate.md)|Remove a Related edge between two items.|ado|
|[`link unlink`](./link-unlink.md)|Remove any non-hierarchy link (`predecessor`, `successor`, `related`).|ado|
|[`link artifact`](./link-artifact.md)|Attach a hyperlink or `vstfs://` artifact link to an item.|ado|

## Shared behavior

- **Targeting.** Every verb operates on the active work item by default. Pass
  `--id <n>` — or, for the hierarchy verbs, the optional second positional
  argument — to target a specific item without changing active context.
- **Guards.** Self-links are rejected. Adding a link that already exists is a
  no-op reported as `linkUnchanged`, and the command exits `0`. Removing a
  link that does not exist is a failure.
- **Resync.** After a successful mutation, both endpoints are resynced into the
  local cache so subsequent reads see the new edge set. If resync fails, the
  command still exits `0` and emits a warning to stderr — the ADO write already
  succeeded.
- **Output.** `-o human` (default) prints a status line followed by the current
  link set. `-o minimal` prints the status line only. `-o json` (and its
  `json-full`/`json-compact`/`ids` variants) emits a structured document
  containing the message, count, and per-link records.
