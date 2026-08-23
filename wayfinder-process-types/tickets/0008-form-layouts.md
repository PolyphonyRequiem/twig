---
id: 0008
title: What does each type's form layout look like, and what is the convention governing layouts?
type: grilling
status: open
claimed_by:
blocked_by: [0007]
---

## Question

What form layout does each type carry, and what convention governs layouts so a new type's form
is derivable rather than invented?

## Why this exists

A field a type carries but never shows on its form is invisible in the ADO web UI and in twig's
detail view. **Carrying a field and surfacing it are two different acts**, and the map's
destination names layouts explicitly, so this cannot be folded into 0007.

`twig process layout <type>` renders a layout. The projection work in
`wayfinder-detail-projection/` already established the structure this rests on: ADO serves
**page → section → group → control**, all four levels preserved, and
`WorkItemDetailProjector.Project` resolves each field control to one of **three** states — has a
value / empty on the server / **not carried by Twig**. That third state is the one that matters
here: `FieldImportFilter` excludes all eight core fields, every boolean, and unlisted read-only
fields, so a field can be on the form and still not reach a twig host.

🔴 **So a layout ruling has a downstream consumer that will report it honestly.** Anything
placed on a form that twig does not carry shows as *not carried by Twig* rather than blank —
that is by design (`wayfinder-detail-projection` ticket 0006: absent metadata degrades to "I
don't know", never to a false blank). Check the ruling against that, and note that
`DetailControl.ReadOnly` is **reported, never enforced**, so marking a control read-only on a
layout does not make it so.

## Blocked on 0007

The field set must be settled before it can be arranged. Laying out fields that 0007 may retire
is wasted work.

## What a good answer settles

- The convention: which page/section/group a field kind lands in, so a new type's form follows
  rather than being invented per type.
- Whether the two clusters from 0007's matrix (schedulable vs wayfinder) imply two layout
  templates.
- What a gate field's placement must be. A close gate the user cannot find is a usability
  defect that presents as "the state won't move".
- Whether custom pages or contribution slots are used, and what happens to them in twig's
  projection — the projection **carries them flagged rather than filtering them**, so this is a
  live choice, not a hypothetical.
- Whether layouts are part of the generic layer (0001) or per-instance.

## Do not

- Do not edit layouts on the board. Ruling only.
- Do not assume a field on a form is visible to twig. Check against `FieldImportFilter` and the
  three field states.
- Do not use `ReadOnly` as an enforcement mechanism; it is reported and never enforced.
