---
id: 0002
title: How do the board's wayfinder-named types relate to the repo's wayfinder/ markdown?
type: grilling
status: open
claimed_by:
blocked_by: []
---

## Question

The board has `Map`, `Wayfinder Task`, `Research`, `Prototype`, `Grilling`, `Decision`, `Spec`,
`Idea`. The repo has `wayfinder/` markdown holding maps, tickets and rulings. **What is the
relationship?** Board item, markdown file, or both linked via `tracked_in`?

## Why this is the highest-leverage ticket on the map

`docs/agents/issue-tracker.md` (commit `054e780b`) states it outright:

> 🔴 **How these relate to the repo's `wayfinder/` markdown is UNDECIDED.** The names line up
> suggestively, but a suggestive name is not a mapping. **Ask; do not infer it from the type
> list.**

The names collide almost perfectly, which is exactly what makes inferring the mapping dangerous
— a suggestive name produces a confident wrong answer, and this repo has a documented history of
that failure class (`AGENTS.md` §*The false-green class*).

`AGENTS.md` §*Where work is tracked* is the countervailing authority and it says the opposite of
what the type list suggests: **decisions live in the repo**, not the board, because they are
reviewed with the code they govern and carry evidence a work item cannot hold; **work lives on
ADO**. If that rule is complete, several board types are either redundant with markdown or serve
a different purpose that has never been written down.

**Nearly every other ticket depends on the answer.** The ADR question (0006) is this question
wearing different clothes. Whether `Decision`, `Spec` and `Finding` (0011) are types at all
turns on it. The field clusters (0007) split along exactly this seam — the measured matrix shows
`MaturityNote`/`WayfinderAnswer`/`WayfinderDecisionMaturity` on precisely the four wayfinder
types and nothing else. And "how each team member uses twig" (0003) cannot be described if it is
unknown whether a decision is a twig verb or a text editor.

🔴 **This ticket is HITL and must be answered by Daniel.** The doc says *ask*. An agent
resolving it from the type list has done the one thing the red flag forbids.

## What a good answer settles

- For each of `Map`, `Wayfinder Task`, `Research`, `Prototype`, `Grilling`, `Decision`, `Spec`,
  `Idea`: board-only, markdown-only, or both — and if both, which is authoritative and which is
  the projection.
- Whether `tracked_in` is the general mechanism or a special case for scheduled rulings only.
- What the map/ticket markdown files are *for* if the board also models them — and note this
  map itself is markdown per that convention, so the answer is self-referential and should say
  what happens to it.
- Whether `AGENTS.md` §*Where work is tracked* needs amending, or whether the board types
  predate it and are the drift.
- The measured evidence: the four wayfinder types carry a disjoint field cluster
  (`MaturityNote`, `WayfinderAnswer`, `WayfinderDecisionMaturity`) — does that support a real
  distinction, or is it drift the answer should sweep away?

## Do not

- **Do not infer the mapping from the names.** That is the specific move the red flag forbids.
- Do not answer 0006 (ADRs) here beyond noting the entanglement — but if the answer genuinely
  subsumes 0006, say so and rule 0006 resolved-by-this rather than leaving a zombie ticket.
