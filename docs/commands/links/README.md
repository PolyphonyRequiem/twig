# Links commands

Manage relationships and artifact links. The published-item verbs operate on
Azure DevOps work items — hierarchy (parent/child), non-hierarchy relations
(`predecessor`, `successor`, `related`), and artifact links (URLs and
`vstfs://` URIs). The `link local` verbs use provider-neutral local GUIDv7
identities for explicit Twig-local Related links. Virtual links between local
seeds live under `seed link` / `seed unlink` instead.

Published-item commands mutate ADO immediately, then resync both endpoints.
`link local related` and `link local unrelate` mutate only Twig's durable
local relationship store; they never write or translate the edge to a provider.
`link local list` reads that store without provider access.

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
|[`link local related`](./link-local-related.md)|Add an explicit local-only Related edge between local work items.|local|
|[`link local unrelate`](./link-local-unrelate.md)|Remove a local-only Related edge without provider access.|local|
|[`link local list`](./link-local-list.md)|Read local-only Related edges without provider access.|none|
|[`link unlink`](./link-unlink.md)|Remove any non-hierarchy link (`predecessor`, `successor`, `related`).|ado|
|[`link artifact`](./link-artifact.md)|Attach a hyperlink or `vstfs://` artifact link to an item.|ado|

## Shared behavior

- **Published-item targeting.** These verbs operate on the active work item by default. Pass
  `--id <n>` — or, for the hierarchy verbs, the optional second positional
  argument — to target a specific item without changing active context.
- **Local targeting.** `link local` requires explicit canonical local GUIDv7
  identities. Creating a link requires both local items to exist and be live;
  reading or removing an existing edge does not resolve a provider identity.
- **Published-item guards.** Self-links are rejected. Adding a link that
  already exists is a no-op reported as `linkUnchanged`, and the command exits
  `0`. Removing a link that does not exist is a failure.
- **Published-item resync.** After a successful mutation, both endpoints are
  resynced into the local cache so subsequent reads see the new edge set. If
  resync fails, the command still exits `0` and emits a warning to stderr —
  the ADO write already succeeded.
- **Published-item output.** `-o human` (default) prints a status line followed
  by the current link set. `-o minimal` prints the status line only. `-o json`
  (and its `json-full`/`json-compact`/`ids` variants) emits a structured
  document containing the message, count, and per-link records.
- **Local output.** `link local` supports `human`, `json`, and `minimal`.
  Its `ids` format is intentionally unsupported because local GUIDv7 identities
  are not numeric work-item IDs.
