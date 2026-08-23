---
id: 0005
title: Should Request for Change be a type, and how does it avoid the four dormant Request/Response types?
type: grilling
status: open
claimed_by:
blocked_by: [0002, 0004]
---

## Question

Should `Request for Change` exist as a work item type — and if so, how does it avoid colliding
with the four inherited Request/Response types already on the board?

## Why this exists

Daniel raised it this session. **It is genuinely open and must not be answered by an agent
acting alone.**

⚠️ **The trap, measured.** `Code Review Request`/`Code Review Response` and `Feedback Request`/
`Feedback Response` already exist as inherited-but-unused pairs, and **all four measure zero
items**. They are the back ends of two Microsoft tool features — the *Request Feedback* flow and
the legacy TFVC code review handshake. `docs/agents/issue-tracker.md` warns they are reachable
and *"would accept a write without erroring"*, so work routed into them lands somewhere real and
invisible.

🔴 **A fifth similarly-named type needs a deliberate answer on collision, not an addition by
reflex.** This is a false-green shape in the making: a user or agent reaching for "request" gets
five plausible targets, four of which silently swallow the write. `AGENTS.md`
§*The false-green class* is the house doctrine on exactly this defect — a surface that accepts
input and reports nothing wrong.

## Blocked on 0002 and 0004 — and why

- **0004** may already answer it. If `Change` is ruled in, "request for a change" might be a
  *state* of a `Change` (`To do` already means "proposed"), a field, or a link — not a type.
  Resolving 0005 before 0004 risks inventing a type whose job `Change` already does.
- **0002** decides whether an inbound request is board-shaped at all, or whether it is the
  GitHub public-record surface plus `tracked_in`.

## What a good answer settles

- Type / state / field / link / nothing — and the argument, not just the verdict.
- If it is a type: how a user or agent picking from a type list is prevented from choosing one
  of the four dormant ones. Naming alone is not a mechanism.
- What happens to the four dormant types. They are a live hazard independent of this answer.
  Research ticket 0009 establishes what ADO actually permits — **use its findings; do not guess
  at removability.**
- Who raises a `Request for Change` and to whom (feed from 0003). A request implies a requester
  and an approver; if neither role exists, the type has no lifecycle.
- Whether an inbound external request (a GitHub issue) is the same thing wearing a different
  hat — `AGENTS.md` already routes those: the issue **stays open** on GitHub, tracking moves to
  ADO.

## Do not

- Do not add the type by reflex because the name sounds reasonable. That is the stated trap.
- Do not answer it from the type list alone. This is HITL — Daniel raised it, Daniel settles it.
- Do not mutate or delete the dormant types. Ruling is in scope; mutation is not.
