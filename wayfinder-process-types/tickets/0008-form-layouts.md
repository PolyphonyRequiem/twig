---
id: 0008
title: What does each type's form layout look like, and what is the convention governing layouts?
type: grilling
status: open
claimed_by:
blocked_by: []
tracked_in: [682]
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

## Answer — recorded 2026-10-27

This is a bounded ruling on the live Hyperbright process, not a normative Twig template or a
change to ADO. It records the measured field and gate disposition from
[ticket 0007 (ADO #681)](0007-fields-and-gates.md) without extending it. Layouts are supplied per
process and per type; Twig's generic detail projection preserves the server-authored
**page → section → group → control** structure and reports each field as having a value, empty
on the server, or not carried by Twig. `ReadOnly` is reported, not enforced.

### 1. Measured convention and current inventory

All 12 visible current types have four pages: a custom `Details` page, followed by the built-in
`History`, `Links`, and `Attachments` pages. On `Details`, the measured layouts place narrative
and answer fields in an `Outcome` group and categorical/status fields in a `Status` group.
`Task` additionally has a `Planning` group; it has no custom Outcome field. This describes the
current process only and does not prescribe where a future type must place a field.

The table records the custom controls in their measured groups. All entries are on the `Details`
page; “—” means no custom field was measured in that group.

| Type | Section 1 — Outcome | Section 2 — Status |
|---|---|---|
| Bug | `FalsificationCriteria`, `ChangelogSummary` | `Maturity`, `TerminalOutcome`, `VerificationMode`, `WayfinderExecutionMode`, `PriorityBand` |
| Decision | `ClosingStatement` | `DecisionStanding`, `SupersededBy` |
| Epic | `ClosingStatement` | `Maturity`, `PriorityBand` |
| Feature | `FalsificationCriteria`, `ChangelogSummary` | `Maturity`, `TerminalOutcome`, `VerificationMode`, `WayfinderExecutionMode`, `PriorityBand` |
| Grilling | `WayfinderAnswer`, `MaturityNote` | `WayfinderDecisionMaturity` |
| Idea | `ClosingStatement` | `Maturity`, `IdeaOutcome`, `PriorityBand` |
| Map | `ClosingStatement`, `WayfinderDecisionsSoFar` | `WayfinderDestination` |
| Prototype | `WayfinderAnswer`, `MaturityNote` | `WayfinderDecisionMaturity`, `WayfinderExecutionMode` |
| Research | `WayfinderAnswer`, `MaturityNote` | `WayfinderDecisionMaturity`, `WayfinderExecutionMode` |
| Spec | `FalsificationCriteria` | `Maturity`, `VerificationMode`, `PriorityBand` |
| Task | — | `TerminalOutcome`, `WayfinderExecutionMode` |
| Wayfinder Task | `WayfinderAnswer`, `MaturityNote` | `WayfinderDecisionMaturity`, `TerminalOutcome`, `WayfinderExecutionMode` |

The gate-related placements are mixed and are recorded exactly as measured:

- `FalsificationCriteria` on Bug and Feature is in Outcome.
- `WayfinderAnswer` and `MaturityNote` on Wayfinder Task, Prototype, Research, and Grilling are
  in Outcome.
- `VerificationMode` on Bug, Feature, and Spec is in Status.

These are observed placements, not a universal gate-field placement rule or a reason to move
any field. No layout page, group, or control is marked as a contribution in any of the 12
measured layouts.

### 2. Pages, contributions, and projection

The custom `Details` page and all other server-authored pages and controls remain part of the
generic detail document. Contribution flags are preserved rather than used as a filter, as
settled in the [detail-document contract](../../wayfinder-detail-projection/tickets/0002-detail-document-contract.md).
The live inventory contains zero contribution rows, so it establishes only their current absence,
not a policy against contributions.

### 3. Generic mechanism versus ADO-specific layouts

The generic mechanism is Twig's projection of layout data supplied by the process/type. The
measured page, section, group, and control choices are Hyperbright ADO configuration, not
Twig-owned layout templates. This ruling assigns no fields or gates to other types and does not
infer template identifiers from repeated layout shapes.

### Evidence

The inventory is from the live Hyperbright process description and serial
`twig process layout <type> -o json` reads for Bug, Decision, Epic, Feature, Grilling, Idea, Map,
Prototype, Research, Spec, Task, and Wayfinder Task on 2026-10-27. The field/gate disposition is
the bounded ruling in ticket 0007 (ADO #681).

### Explicitly unresolved

The evidence does not settle the original future-type requirement: **which page/section/group a
field kind must land in so a new type's form is derivable rather than invented**, including
whether the field clusters imply two reusable layout templates. That is an ADO process-policy
choice outside this bounded Twig ruling. Ticket 0008 remains open for that specific decision; no
future-type template or placement rule is prescribed here.

## What a good answer settles

- The current Hyperbright layout inventory and its observed grouping convention are recorded;
  they do not constitute a future-type template.
- Custom pages and contribution flags are carried in the generic projection; the measured
  current layouts contain no contribution rows.
- Layouts are process/type input to the generic projection, not Twig-owned templates.
- The normative future-type placement/template clause remains unresolved as stated above.

## Do not

- Do not edit layouts on the board. Ruling only.
- Do not assume a field on a form is visible to twig. Check against `FieldImportFilter` and the
  three field states.
- Do not use `ReadOnly` as an enforcement mechanism; it is reported and never enforced.
- Do not turn the measured Hyperbright grouping into a future-type template or gate-placement
  requirement without a separate process-policy decision.
