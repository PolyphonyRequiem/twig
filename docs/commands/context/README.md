# Context commands

Commands for selecting the active work item, displaying items without changing
context, running ad‑hoc searches, opening the web view, and reading revision
history. Reads default to the local SQLite cache — mutation flows through the
plan / proposal path documented in the mutation and plan groups.

| Command | Summary | Mutates |
|---------|---------|---------|
| [`twig set`](./set.md) | Set the active work item by ID or title pattern. | local |
| [`twig show`](./show.md) | Display a work item (cache‑only by default). | none |
| [`twig show-batch`](./show-batch.md) | Display multiple work items by ID (cache‑only). | none |
| [`twig tree-set`](./tree-set.md) | Render an arbitrary working set as a forest of annotated trees. | none |
| [`twig query`](./query.md) | Search and filter work items via ad‑hoc WIQL. | local |
| [`twig web`](./web.md) | Open the active work item in the browser. | none |
| [`twig history`](./history.md) | Show the ADO revision history for a work item. | none |

## Behavior at a glance

- **Cache‑only reads.** `show`, `show-batch`, and `tree-set` read the local
  SQLite cache and never fetch from ADO on their own. Use `twig sync`, or the
  per‑command `--refresh` flag where it exists, to pull fresh data.
- **`set` can fetch.** A numeric ID that is not in the cache is fetched from
  ADO on demand; a title pattern searches the cache only.
- **`query` mutates local state.** The generated WIQL is executed against
  ADO and returned rows are written into the local cache so subsequent
  reads see them.
- **`history` is ADO‑only.** Revision history is never cached; every call
  hits the ADO Work Item Updates API.
- **`web` opens a URL.** It launches the default browser via
  `Process.Start`; nothing is written to ADO.
