# Read and find work items

| Command | Purpose |
| --- | --- |
| [`show`](show.md) | Cached detail; explicit `--refresh` for provider acquisition |
| [`show-batch`](show-batch.md) | Multiple cached numeric targets, missing IDs disclosed |
| [`query`](query.md) | Bounded provider query using actual schema |

Item reads need explicit targets. GitHub does not qualify trees or all ADO query predicates. See
[provider differences](../../getting-started.md#provider-differences).

[`set`](set.md), `history`, `tree-set`, and `web` are retired; no legacy cache route is available.
