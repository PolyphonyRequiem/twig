# Work-item commands

Verbs that write to a work item: transitions, field edits, notes, creates, deletes,
and the local-only pending-change eraser. These commands are how twig turns
your intent into a change on the Azure DevOps board (or, for `discard`, throws
away a change you never pushed).

Each page states whether the command mutates ADO immediately, stages a change
in the local pending-change store, or both. Pay attention to that field before
running a command against a real work item — several of these commands PATCH
ADO on the fly.

| Command | Summary | Mutates |
|---------|---------|---------|
| [`twig state`](state.md) | Change the state of the active work item by name. | ADO |
| [`twig batch`](batch.md) | State, fields, and a note in a single atomic call. | ADO |
| [`twig note`](note.md) | Add a comment/note to the active work item. | ADO (falls back to local staging) |
| [`twig update`](update.md) | Update a single field on the active work item. | ADO |
| [`twig patch`](patch.md) | Atomically patch multiple fields via JSON. | ADO |
| [`twig edit`](edit.md) | Edit fields interactively in `$EDITOR`. | ADO (falls back to local staging) |
| [`twig new`](new.md) | Create a new work item in ADO. | ADO |
| [`twig discard`](discard.md) | Drop pending changes for one item or all dirty items. | Local |
| [`twig delete`](delete.md) | Permanently delete a work item from ADO. | ADO (irreversible) |

## Choosing between `state`, `batch`, and `patch`

- `twig state <name>` is single-purpose: it moves `System.State` only. If the
  process makes a Done-state gate field required, `twig state` refuses the
  transition — use `twig batch` (or a change proposal) to write the transition
  and its gate fields in the same operation.
- `twig batch --state --set` combines state and field edits into a single ADO
  PATCH. It never multi-hop chains state transitions — use `twig state` for
  those.
- `twig patch --json` is the batch equivalent for fields only. It accepts a
  JSON payload from inline text or stdin and is the right verb when you want
  scripted, atomic multi-field writes without any state transition.

## Notes on staging vs. immediate mutation

Most of these commands PATCH ADO immediately. The exceptions are:

- **`discard`** never touches ADO — it clears the local pending-change store.
- **`edit`** stages locally when the PATCH fails (or when the target is an
  unpublished seed).
- **`note`** stages locally when ADO is unreachable, marked as pending.
