# context commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`set`](set.md) | Set the active work item by ID or title pattern. |
| [`show`](show.md) | Display an explicitly cached Core item without changing context; --local-id retains original draft UUIDs and permanent mappings. |
| [`show-batch`](show-batch.md) | Display multiple work items by ID (cache-only). Missing IDs are disclosed and exit nonzero. |
| [`query`](query.md) | Search using bounded provider-qualified Core queries. GitHub supports title/body/state only, with at most 100 total candidates. |
