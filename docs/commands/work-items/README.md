# Change existing items

| Command | Effect |
| --- | --- |
| [`update`](update.md) | One typed field, immediate Core save for published items |
| [`state`](state.md) | Discovered transition; cannot bypass coupled required fields |
| [`batch`](batch.md) | Explicit combined field/state effects; ADO atomic coupling where qualified |
| [`patch`](patch.md) | Typed fields from JSON through the Core path |
| [`note`](note.md) | Provider comment, not a queued local note |
| [`edit`](edit.md) | Retained ADO external editor with Core preview/save |
| [`discard`](discard.md) | Remove eligible local existing-item drafts, no remote mutation |

Name the target on each command: `update`, `state`, `note` and `edit` take `--id`;
`discard` takes a positional ID or `--all`. Use discovered field/type/state
references, not an assumed process template. Work-item edits are not all
local-only; use [proposal files](../plans/README.md) for explicitly reviewed
file-scoped execution.

Quick `new` and `delete` are retired. New work starts with
[`seed new`](../seeds/seed-new.md); there is no qualified deletion fallback.
The [hidden `save` declaration](save.md) is currently unreachable through CLI
discovery; it is not a standalone command to recommend.
