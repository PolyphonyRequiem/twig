# Benches

A Bench is a durable, user-wide collection of connection-qualified selection
rules. The CLI and daemon share its identity and definition. It is not owned by
a repository or automatically created from a sprint. Creation does not select it.

| Command | Purpose |
| --- | --- |
| [`bench create`](create.md) | Create shared metadata |
| [`bench list`](list.md) / `bench` | List stored definitions |
| [`bench rename`](rename.md) | Preserve identity/rules while changing the name |
| [`bench switch`](switch.md) | Select in this initialized worktree |
| [`bench delete`](delete.md) | Delete metadata under content/confirmation checks, not upstream items |
| [`bench rules`, `bench rule add/remove`](planning.md) | Inspect/edit shared selectors with revision checks |
| [`bench planning [show/refresh]`](planning.md) | Cached or explicitly refreshed ADO planning metadata |
| [`bench sources`, `bench browse`](planning.md) | Discover and explicitly read native saved queries/backlogs |

Metadata management does not discover identity profiles, execute queries, or
initialize a default. Worktree selection is independent; changes to a shared
definition are visible wherever it is used.

The old `bench browser/detail/discussion/configuration` commands are retired.
`bench browse` is a distinct explicit native source read, not that browser.
For graphical hosting and human Save/Apply, see the [daemon guide](../../daemon.md).
For model and provider limits, see [Benches and context](../../features/workspace-bench-context.md).
