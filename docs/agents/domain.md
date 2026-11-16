# Domain documentation

Read [`CONTEXT.md`](../../CONTEXT.md) for shared vocabulary and the
[architecture overview](../architecture/overview.md) for current ownership.
Relevant [ADRs](../adr) and [specifications](../specs) provide decision/design
context; check their scope and implementation status before treating them as
current behavior.

Use names from current source rather than inventing synonyms. A Bench is shared
saved membership intent; a workspace is an initialized client context; the active
item is local focus, not a work claim. Seeds are UUID-backed unpublished drafts,
not predicted native IDs.

When a proposed change contradicts a relevant recorded decision, cite it and
explain the reason to revisit it. Do not silently restore removed architecture
because an older plan describes it.

Keep domain vocabulary separate from implementation structure. Types, adapters,
ports and modules describe code organization; they do not create new work-item
concepts or process assumptions.
