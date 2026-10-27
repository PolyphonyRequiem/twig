---
id: 0007
title: Which types carry which fields and which close gates?
type: grilling
status: open
claimed_by:
blocked_by: []
tracked_in: [681]
---

## Question

Which fields does each type carry, and which types have close gates? `Custom.FalsificationCriteria`
+ `Custom.VerificationMode` gate `Bug` → `Done` today. What gates the others — and are the
sixteen existing custom fields the right sixteen?

## Answer — recorded 2026-10-27

The field/type matrix measured above is retained unchanged: no field is added, removed, or
consolidated, and no new close gate is introduced. Keep the measured `Bug`→`Done` gate on
`Custom.FalsificationCriteria` and `Custom.VerificationMode`, and the already-ratified
`Custom.MaturityNote` gate on `Grilling`→`Done` and `Research`→`Done` from ticket
[0002](0002-board-types-vs-wayfinder-markdown.md). The wayfinder field cluster remains assigned
to its four ratified types. `Custom.VerificationMode` keeps its existing five-item picklist.
`makeRequired` rules are advisory guardrails, not guarantees: ticket
[0009](0009-ado-process-capabilities.md) measured that `bypassRules=true` can bypass them. The
customer-zero verdict is **Acceptable**: the field values are Hyperbright-specific; the
mechanism of assigning fields and rules to a process's own types is not.

The three maturity-named fields remain separate ADO fields in their measured placements.
Ticket 0002 ratifies the wayfinder cluster's placement and intentionality, and describes its
fields collectively as recording how well a question was answered. It does not define whether
`Custom.Maturity`, `Custom.MaturityNote`, and `Custom.WayfinderDecisionMaturity` represent one,
two, or three concepts. That semantic question concerns the ADO workflow, not Twig's product
contract, and is out of scope for this ruling. Do not infer conceptual equivalence or
consolidate fields.

This answer covers the measured existing types only. It allocates no fields or gates to
hypothetical types from ticket 0004; any later type decision must deliberately specify its own
field assignments rather than copying a neighbor.

## The measured starting point

Read live 2026-08-22. **Every non-stock type carries custom fields; none has zero.**

```
Custom.ChangelogSummary            Feature, Bug
Custom.ClosingStatement            Map, Decision, Idea, Epic
Custom.DecisionStanding            Decision
Custom.FalsificationCriteria       Issue, Feature, Spec, Bug
Custom.IdeaOutcome                 Idea
Custom.Maturity                    Issue, Idea, Epic, Feature, Spec, Bug
Custom.MaturityNote                Wayfinder Task, Prototype, Research, Grilling
Custom.PriorityBand                Issue, Idea, Epic, Feature, Spec, Bug
Custom.SupersededBy                Decision
Custom.TerminalOutcome             Wayfinder Task, Task, Feature, Bug
Custom.VerificationMode            Issue, Feature, Spec, Bug
Custom.WayfinderAnswer             Wayfinder Task, Prototype, Research, Grilling
Custom.WayfinderDecisionMaturity   Wayfinder Task, Prototype, Research, Grilling
Custom.WayfinderDecisionsSoFar     Map
Custom.WayfinderDestination        Map
Custom.WayfinderExecutionMode      Wayfinder Task, Prototype, Task, Feature, Research, Bug
```

🔴 **PARTIALLY PRE-EMPTED by ticket [0002](0002-board-types-vs-wayfinder-markdown.md)'s partial
answer 2 (2026-08-22) — read it before working this ticket.** One of the two things below is
already ruled, and this ticket inherits the frame rather than a blank sheet:

- **The wayfinder cluster is REAL, not drift.** `MaturityNote`/`WayfinderAnswer`/
  `WayfinderDecisionMaturity` sit on `Wayfinder Task`/`Prototype`/`Research`/`Grilling` because
  those are **exactly** the `wayfinder` skill's four ticket types, and the three fields describe
  *how well a question was answered*. That is a spec that was implemented. `Map` joins them as
  their **container** (`WayfinderDestination`, `WayfinderDecisionsSoFar`). **Do not re-open
  whether this cluster belongs.**
- **`Custom.MaturityNote` gating `Grilling`→`Done` and `Research`→`Done` is COHERENT BY DESIGN**,
  not accreted configuration: a question type cannot close without saying how well it was
  answered. The same gate would be incoherent on a `Decision`, which is not *answered*.
- **So this ticket's gate question narrows to: which OTHER types need a close gate, and of what
  kind** — not whether the existing wayfinder gate should exist. ⚠️ Note the gate is advisory:
  `bypassRules=true` walks through a `makeRequired` rule (ticket 0009).
- **A new type must DECLARE whether it is a wayfinder ticket type**, and that determines its
  fields. Copying the nearest neighbour's field set is no longer acceptable.

**The current disposition and close-gate questions are partially answered above.** The semantic
relationship among the three near-homonyms remains open; the fields stay separate regardless
of that unresolved concept count.

Two things fall out of that matrix, and neither should be assumed to be design:

1. **A clean two-cluster split.** `Maturity`/`PriorityBand`/`FalsificationCriteria`/
   `VerificationMode` sit on the schedulable types. `MaturityNote`/`WayfinderAnswer`/
   `WayfinderDecisionMaturity` sit on **exactly** the four wayfinder types
   (`Wayfinder Task`, `Prototype`, `Research`, `Grilling`) and nothing else. The clusters are
   nearly disjoint — `WayfinderExecutionMode` and `TerminalOutcome` are the only crossers. This
   is either the taxonomy asserting itself in the fields, or accretion. **It is direct evidence
   for ticket 0002** and should be read there too.

2. **Three near-homonyms: `Maturity`, `MaturityNote`, `WayfinderDecisionMaturity` — and no type
   carries more than one of them.** Their field identities and measured placements remain
   unchanged. The sources do not settle whether they represent one concept, two, or three; do
   not infer that count from the shared cluster or consolidate them without Daniel's decision.

**The Bug→Done gate**, measured: `Custom.FalsificationCriteria` (html, free text) **and**
`Custom.VerificationMode` (string) must both be set before the state will move.

The early process-API read suggested that `VerificationMode` had no `allowedValues`; ticket
0009 corrected this: the project WIT endpoint exposes its existing five-item picklist. The
partial answer above retains that picklist unchanged.

## What a good answer settles

- The field/type matrix and gate disposition for the measured existing types: retained unchanged,
  with no field additions, removals, consolidations, or new close gates.
- The semantic relationship among the maturity-named fields is explicitly outside Twig's product
  contract; this answer preserves their separate ADO identities without assigning a concept count.
- Existing `Bug` and wayfinder close gates are retained; `makeRequired` is recorded as advisory,
  and the existing five-value `VerificationMode` picklist is unchanged.
- The customer-zero verdict is **Acceptable** for this process ruling: its field values are
  instance-specific, while the mechanism is process-driven.
- Proposed types not yet accepted by ticket 0004 are outside this answer and receive no field or
  gate assignments here.

## Do not

- Do not create or modify fields on the board. Ruling only.
- Do not design a gate before 0009 says ADO can enforce it.
- Do not copy a new type's field set from its nearest neighbour. That is how the three
  `Maturity` fields most plausibly happened.
