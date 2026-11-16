---
command: edit
group: work-items
summary: Use the qualified external editor and save typed changes through Core.
stability: stable
mutates: ado
---

# `twig edit`

## Behavior

Opens an external editor for actual supported writable fields.
`--field` selects an explicit field. Existing draft intent overlays the acquired
observation; changed input is staged against the captured baseline/store and
saved through Core. Cancellation/no changes is a no-op, not an implicit save.
Local seed targets use the local seed editor.

Machine calls require `--id`. GitHub editor convenience is not qualified; use
explicit supported `update`/`patch` instead.

## Effects

Can acquire schema/item evidence, launch an editor, and publish changed
existing-item fields immediately. Seed edits remain local.

## Failure modes

Unknown, duplicate, read-only, malformed or unsupported typed fields refuse.
Changed native evidence or an unresolved save retains intent/journal; no
automatic remote conflict acceptance or retry occurs.
