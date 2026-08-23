---
id: 0006
title: Should ADRs be a type — or are they the wayfinder/ markdown already?
type: grilling
status: open
claimed_by:
blocked_by: [0002]
---

## Question

Should Architecture Decision Records be a work item type on this board?

## Why this exists

Daniel raised it this session. **It is genuinely open and must not be answered by an agent
acting alone.**

🔴 **It is entangled with ticket 0002 and may not be separable from it.** The brief says so
plainly: *"the ADR question is really this mapping question wearing different clothes."*

The entanglement, concretely:

- The board already has a **`Decision`** type, carrying `Custom.DecisionStanding`,
  `Custom.SupersededBy` and `Custom.ClosingStatement` (measured). *Superseded-by* and *standing*
  are the two fields an ADR corpus needs — so something ADR-shaped has already been built here,
  under a different name and with no level.
- `AGENTS.md` §*Where work is tracked* says decisions live in the **repo**, because they are
  reviewed with the code they govern and carry evidence a work item cannot hold. `wayfinder/`,
  `wayfinder-1.0/`, `wayfinder-detail-projection/` are full of exactly that.
- `docs/agents/domain.md` records that **wayfinder rulings carry ADR force**. If that is true,
  the ADR corpus already exists as markdown and the question is not "should we add a type" but
  "what is `Decision` for".

So the three plausible answers are: ADRs are the `wayfinder/` markdown (no new type, possibly
one fewer); ADRs are the existing `Decision` type (rename, document, done); or they are a third
thing neither surface covers — which needs a reason.

## Blocked on 0002 — and possibly resolved by it

If 0002 rules that `Decision` is markdown-authoritative with a board projection (or the reverse),
this ticket may have no residue. **That is a legitimate outcome:** close it as resolved-by-0002
with a one-line note rather than manufacturing a distinct answer. A ticket that survives only to
be answered is worse than one honestly folded in.

## What a good answer settles

- Whether an ADR is a new type, the existing `Decision`, markdown, or a pair linked by
  `tracked_in`.
- If markdown: what `Decision` is for, given it exists and carries superseding fields.
- How supersession works across whichever surfaces are authoritative — an ADR corpus whose
  supersession chain spans two systems with no link is a stale-reference hazard, the same class
  `tools/check-tracking.sh` exists to catch.
- Whether wayfinder rulings *are* ADRs (per `docs/agents/domain.md`) or merely resemble them,
  and whether `docs/specs/` is a third ADR-ish surface that needs the same answer.

## Do not

- Do not add an `ADR` type because the acronym is standard. The board already has a type doing
  most of the job.
- Do not resolve this before 0002. Answering the derived question first is how the two surfaces
  drift apart.
