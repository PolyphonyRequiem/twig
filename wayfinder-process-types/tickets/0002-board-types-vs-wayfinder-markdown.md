---
id: 0002
title: How do the board's wayfinder-named types relate to the repo's wayfinder/ markdown?
type: grilling
status: open
claimed_by:
blocked_by: []
tracked_in: [675]
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

## Partial answer — recorded 2026-08-22, ticket stays OPEN

⚠️ **One part of this question was settled as a side effect of mirroring the map, and is
recorded here so it does not become undocumented precedent.**

While resolving ticket 0001, Daniel asked for the full map to be mirrored onto ADO. That is a
partial answer to this ticket, because deciding that wayfinder tickets get board items *is* the
board-or-markdown question. What it settles:

- **Wayfinder maps and tickets exist as BOTH** a markdown file and a board item. The board
  carries `Map` #674 with a `Grilling`/`Research` child per ticket, matching the sibling map's
  shape (#621/#637 and children #622, #623, #625, #638, #639).
- **The markdown is AUTHORITATIVE; the board item is the projection.** Every mirrored item's
  description ends by naming its source file and branch, and each ticket carries `tracked_in`.
- **`tracked_in` is therefore NOT only for scheduled rulings** — it is the general
  markdown→board link. This *contradicts* `docs/agents/issue-tracker.md`, which says the repo
  "does not model the map as a tracker item" and that a ticket with no `tracked_in` is normal
  because "most rulings were never scheduled". That doc predates the sibling map's practice.

🔴 **What remains OPEN, and why this ticket is not closed:**

- Whether `docs/agents/issue-tracker.md` should be amended to match practice, or whether the
  mirroring is the drift. Two authorities now disagree in writing and one must yield.
- The per-type rulings this ticket actually asks for: `Decision`, `Spec`, `Idea` and
  `Wayfinder Task` were **not** settled by the mirroring — only `Map`, `Grilling` and
  `Research` were exercised.
- What the board item is *for* if the markdown is authoritative — audience, or scheduling, or
  visibility.
- Whether the disjoint field cluster (`MaturityNote`, `WayfinderAnswer`,
  `WayfinderDecisionMaturity`) supports a real distinction or is drift.

**Measured while mirroring, and relevant to the field question above:**
`Custom.MaturityNote` is a **required close gate** on `Grilling`→`Done` and `Research`→`Done` —
a `TF401320 Required, InvalidEmpty` rule error, hit live on #676 and #683. The PATCH failed
**atomically**: state stayed `To do` and the answer field stayed empty, so there was no
half-written item. That is a real gate on the wayfinder types, which 0007 should account for.
