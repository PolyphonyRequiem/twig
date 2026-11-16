# links commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`link parent`](link-parent.md) | Set the parent of an unparented published item through a verified Core proposal. |
| [`link unparent`](link-unparent.md) | Remove the one observed native parent through a verified Core proposal. |
| [`link reparent`](link-reparent.md) | Parent replacement is unsupported; refuses before HTTP without removing any link. |
| [`link predecessor`](link-predecessor.md) | Mark the active work item as blocked by another item (Predecessor link). |
| [`link successor`](link-successor.md) | Mark the active work item as blocking another item (Successor link). |
| [`link related`](link-related.md) | Relate the active work item to another item (symmetric Related link). |
| [`link unrelate`](link-unrelate.md) | Remove a Related link between the active work item and another item. |
| [`link local related`](link-local-related.md) | Create an explicit LOCAL, non-native Related association through a Verified local CAS proposal. May acquire native metadata/observations; never mutates native links. |
| [`link local unrelate`](link-local-unrelate.md) | Tombstone an existing LOCAL Related association through its exact local CAS. Native reads may occur; no native mutation. |
| [`link local list`](link-local-list.md) | List cached LOCAL associations for an explicit item. Zero HTTP; no schema acquisition or context change. |
| [`link unlink`](link-unlink.md) | Remove a non-hierarchy link (predecessor, successor, or related) from a work item. |
| [`link artifact`](link-artifact.md) | Add an artifact link (URL or vstfs:// URI) to a work item. |
