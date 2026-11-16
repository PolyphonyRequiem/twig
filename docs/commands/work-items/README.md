# work-items commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`state`](state.md) | Change the state of the active work item by name. |
| [`batch`](batch.md) | Batch state transitions, field updates, and notes in a single call. |
| [`note`](note.md) | Add a note to the active work item. |
| [`update`](update.md) | Update a field on the active work item. |
| [`patch`](patch.md) | Atomically patch multiple fields on a work item via JSON input. |
| [`edit`](edit.md) | Edit work item fields in an external editor. |
| [`discard`](discard.md) | Discard pending changes for a single item or all dirty items. |
| [`save`](save.md) | Save selected staged Core intent through its proposal journal; does not perform a sync pull. |
