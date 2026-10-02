# Seeds

Seeds are **local-only work items** with negative IDs (e.g. `#-42`) that live in the
per-worktree SQLite cache at `.twig/cache/twig.db`. They exist so you can
compose a batch of draft work — titles, fields, parent/child structure, dependency
graph — without publishing work items, and then publish the whole set in dependency
order when it is ready.

## Local seed work vs ADO publishing

Local authoring and remote publication remain distinct; resolving a default assignee can require an ADO read.

|Group|Commands|What they touch|
|---|---|---|
|Local seed authoring|[`seed new`](./seed-new.md), [`seed edit`](./seed-edit.md), [`seed discard`](./seed-discard.md), [`seed view`](./seed-view.md), [`seed link`](./seed-link.md), [`seed unlink`](./seed-unlink.md), [`seed links`](./seed-links.md), [`seed chain`](./seed-chain.md), [`seed validate`](./seed-validate.md)|Draft mutations stay local. Creation resolves the bound account's canonical default assignee before minting; that lookup can contact ADO and refuses when identity metadata is unavailable. Other local operations retain their existing offline behavior. `mutates: local` or `none`.|
|ADO publishing|[`seed publish`](./seed-publish.md), [`seed reconcile`](./seed-reconcile.md)|Push seeds to ADO (`seed publish`) or repair local link tables against the `publish_id_map` after a partial push (`seed reconcile`). `mutates: ado` and `mutates: local` respectively.|
|Backward-compat shortcut|[`seed`](./seed.md)|Hidden alias for `seed new`. Local only.|

Seeds carry negative IDs while local. When `seed publish` creates the real ADO item,
the mapping between the old negative ID and the new positive ADO ID is written to
the local `publish_id_map`, and inbound references (parent pointers, virtual links)
are rewritten to the new positive ID. If a publish batch is interrupted partway,
`seed reconcile` walks the map and repairs stale rows.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`twig seed new`](./seed-new.md)|Create a new local seed work item.|local|
|[`twig seed edit`](./seed-edit.md)|Edit a seed's fields in an external editor.|local|
|[`twig seed discard`](./seed-discard.md)|Delete a local seed and its descendants.|local|
|[`twig seed view`](./seed-view.md)|Show the seed dashboard grouped by parent.|none|
|[`twig seed link`](./seed-link.md)|Create a virtual link between two items.|local|
|[`twig seed unlink`](./seed-unlink.md)|Remove a virtual link between two items.|local|
|[`twig seed links`](./seed-links.md)|List virtual links, optionally filtered by item.|none|
|[`twig seed chain`](./seed-chain.md)|Create a chain of successor-linked seeds.|local|
|[`twig seed validate`](./seed-validate.md)|Validate seeds against publish rules.|none|
|[`twig seed publish`](./seed-publish.md)|Publish seeds to Azure DevOps.|ado|
|[`twig seed reconcile`](./seed-reconcile.md)|Repair stale seed links and parent references after partial publishes.|local|
|[`twig seed`](./seed.md)|Hidden backward-compat shortcut for `seed new`.|local|
