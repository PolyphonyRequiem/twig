---
id: 0007
title: Which types carry which fields and which close gates?
type: grilling
status: open
claimed_by:
blocked_by: [0004, 0009]
tracked_in: [681]
---

## Question

Which fields does each type carry, and which types have close gates? `Custom.FalsificationCriteria`
+ `Custom.VerificationMode` gate `Bug` → `Done` today. What gates the others — and are the
sixteen existing custom fields the right sixteen?

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

Two things fall out of that matrix, and neither should be assumed to be design:

1. **A clean two-cluster split.** `Maturity`/`PriorityBand`/`FalsificationCriteria`/
   `VerificationMode` sit on the schedulable types. `MaturityNote`/`WayfinderAnswer`/
   `WayfinderDecisionMaturity` sit on **exactly** the four wayfinder types
   (`Wayfinder Task`, `Prototype`, `Research`, `Grilling`) and nothing else. The clusters are
   nearly disjoint — `WayfinderExecutionMode` and `TerminalOutcome` are the only crossers. This
   is either the taxonomy asserting itself in the fields, or accretion. **It is direct evidence
   for ticket 0002** and should be read there too.

2. 🔴 **Three near-homonyms: `Maturity`, `MaturityNote`, `WayfinderDecisionMaturity` — and no
   type carries more than one of them.** Three names for what may be one concept, split across
   two clusters. That is the shape of drift, and it is what a new type inherits when it is
   created by copying its nearest neighbour. Settle whether they are one concept, two, or three.

**The Bug→Done gate**, measured: `Custom.FalsificationCriteria` (html, free text) **and**
`Custom.VerificationMode` (string) must both be set before the state will move.

🔴 **`VerificationMode` has no `allowedValues` in the API.** The board's convention is
*"Validation proven to catch failure"* (regression tests confirmed red pre-fix) or
*"Developer attested"* — but nothing on the server enforces that, so any string satisfies the
gate. **A gate satisfiable by typing anything is a false green**, the exact defect class
`AGENTS.md` §*The false-green class* catalogues. Whether that is a defect to fix or a deliberate
free-text choice is part of this ticket.

## Blocked on 0004 and 0009

- **0004** decides which types exist. Assigning fields to a type set still in flux is wasted.
- **0009** establishes what ADO actually permits — picklists, required-on-transition, per-type
  field removal. **Do not design a gate ADO cannot express**, and do not guess at what it can.

## What a good answer settles

- The type × field matrix as it *should* be, including any field to retire.
- The three-Maturity question.
- Which types gate on what, and at which transition. `Bug` → `Done` is the only measured gate;
  a `Change`, a `Validation` or a `Decision` may want one.
- Whether `VerificationMode` gets an enforced picklist or stays free text — ⚠️ **the premise was
  wrong and this sub-question is largely answered.** It already **has** a five-item enforced
  picklist (`Not verified yet`, `Developer attested`, `Owner attested`, `Validation accepted`,
  `Validation proven to catch failure`). The *process* API returns a stub; the **project WIT**
  endpoint with `$expand=all` shows the values. What remains open is whether those five are the
  right five, and whether other gate fields need the same treatment.
- 🔴 **What a gate is worth, given it is bypassable.** 0009 measured `bypassRules=true` closing a
  Bug with **both gate fields empty** (HTTP 200, verified by GET). A `makeRequired` rule is a
  *state* rule, not a transition restriction, and it does not survive a privileged automation
  identity — which twig may be. **Do not design a gate whose value depends on being
  unbypassable.** Type-disabling was the only mechanism found that `bypassRules` cannot walk
  through. Decide whether gates here are guardrails (fine) or guarantees (not available).
- 🔴 **Generic-layer check (per the map's governing rule):** would this field set be right for a
  customer whose process we have never seen, or is it Hyperbright-specific? Ticket 0001 decides
  how much that binds; this ticket must not dodge the question either way.
- What the new types from 0004 carry — they are 0007's customers.

## Do not

- Do not create or modify fields on the board. Ruling only.
- Do not design a gate before 0009 says ADO can enforce it.
- Do not copy a new type's field set from its nearest neighbour. That is how the three
  `Maturity` fields most plausibly happened.
