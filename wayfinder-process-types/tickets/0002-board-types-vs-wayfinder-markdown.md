---
id: 0002
title: How do the board's wayfinder-named types relate to the repo's wayfinder/ markdown?
type: grilling
status: open
claimed_by: daniel
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

## Partial answer 2 — recorded 2026-08-22, ticket STAYS OPEN

Grilled with Daniel. **One of the four open items above is now settled; the headline question is
not, and is explicitly parked.** Recorded here rather than closed, because closing 0002 on the
strength of a field ruling would unblock 0004, 0005, 0006 and 0011 onto an answer that does not
exist — the false-green shape `AGENTS.md` §*The false-green class* documents.

### SETTLED: the four wayfinder ticket types are a designed set, and their field cluster is intentional

The disjoint cluster is **real, not drift**. This closes open item 4 above.

`Wayfinder Task`, `Prototype`, `Research` and `Grilling` are **exactly** the four ticket types
the `wayfinder` skill defines (`research`, `prototype`, `grilling`, `task`). They carry
`Custom.MaturityNote`, `Custom.WayfinderAnswer` and `Custom.WayfinderDecisionMaturity` and
nothing else does. That is not a pattern inferred from a matrix — it is **a spec that was
implemented, and the fields are the implementation**. The three fields all describe *how well a
question was answered*, which is the only thing all four types have in common and is meaningless
on anything that is not a question in flight.

Consequences ruled here:

- **`Wayfinder Task` is settled** — it is one of the four, and it rides with them. One of the
  brief's four "completely unsettled" types, closed without needing its own ruling.
- **`Map` belongs with this set as its CONTAINER**, not with `Decision`/`Spec`/`Idea`. It carries
  `Custom.WayfinderDestination` and `Custom.WayfinderDecisionsSoFar`, which are container fields
  matching the skill's map body. ⚠️ An earlier draft of this ruling grouped `Map` with the
  artifacts on the strength of one shared field (`Custom.ClosingStatement`) and called it a
  possible third kind. **That was thin evidence and is withdrawn** — do not revive it.
- **`Custom.MaturityNote` gating `Grilling`→`Done` and `Research`→`Done` is COHERENT BY DESIGN**,
  not an accident of configuration: a question type cannot close without saying how well it was
  answered. The same gate would be incoherent on a `Decision`, which is not "answered". Ticket
  0007 should treat this gate as intended and ask which *other* types need one — not whether this
  one should exist. (It remains advisory, not inviolable: `bypassRules` walks through it.)
- **A new type must declare whether it is a wayfinder ticket type**, and that determines its
  fields. Copying the nearest neighbour's field set is no longer acceptable — that is the
  mechanism by which the three near-homonyms spread. Binds tickets 0004 and 0011; `Finding`
  (0011) in particular reads as a standing statement, not a question, so it should NOT inherit
  this cluster by proximity to `Research`.

🔴 **What this ruling explicitly does NOT say.** `Decision`, `Spec` and `Idea` are **three
separate open questions**, not a second cluster. An earlier draft grouped them together; they
share a field cluster only in the trivial sense that things which *stand* need to record whether
they still stand. They are not related to each other, and none of them is settled here.

### PARKED: the headline question, pending Daniel's mirroring system

**Daniel is building a new system for handling the mirroring.** Until it exists, these stay open
and this ticket cannot close:

1. **Which written authority yields** — `docs/agents/issue-tracker.md` ("does not model the map
   as a tracker item") versus last session's mirroring.
2. **What the board item is FOR** if the markdown is authoritative — audience, scheduling,
   visibility, or cross-repo reach.
3. **`Decision`, `Spec`, `Idea`** — board-only, markdown-only, or both, ruled individually.

**Measured this session, and it sharpens item 1 — the doc is the MAJORITY practice, not the
outlier.** Four map dirs in this repo, 47 tickets. `tracked_in` appears on 15: all 11 in
`wayfinder-process-types` (last session's mirroring) and 4 in `wayfinder-1.0`. `wayfinder/`
(23 tickets) and `wayfinder-detail-projection/` (6) carry **zero**. So
`docs/agents/issue-tracker.md` describes 29 of 47 tickets accurately, and it is last session's
map that is the exception. ⚠️ **Do not read the mirroring as established convention** — it is
one map old.

**Also measured, and it bears on item 3:** board-only `Decision`s already exist and cite no
markdown at all — #353 (IChangeSink contract, Done), #633 (Execution Plan parenting), #671
(reserve-before-prune, Done), and `Spec` #687 — each carrying its full ruling in the description
while `docs/specs/` exists in the repo. **`AGENTS.md` §*Where work is tracked*'s "decisions live
in the repo" is already false in practice.** That is evidence for whichever way item 1 is ruled;
it is not itself a ruling.

### Customer-zero gate verdict (per ticket 0001)

> *Would this still be right for a customer whose process we have never seen?*

**ACCEPTABLE — no defect line for ADO #615.** Only the *values* are ours: the four type names,
and the three field names. The mechanism the ruling relies on is "a type set defined by a
workflow, carrying a field cluster that workflow needs" — any customer can express that with
their own workflow, their own type names and their own fields. Nothing here requires twig to
know the word *wayfinder*.

⚠️ The **parked** questions are not gated, because they are not yet rulings. Whatever settles
the markdown/board relationship will need its own verdict, and it is the more likely place for a
defect to surface — a mirroring mechanism that only works for this repo's file layout would be
one.
