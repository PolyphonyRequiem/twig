---
id: 0005
title: Should Request for Change be a type?
type: grilling
status: open
claimed_by:
blocked_by: [0002, 0004]
---

## Question

Should `Request for Change` exist as a work item type? **Judge it on its own merits** — the
naming-collision framing this ticket was originally charted with was wrong (see below).

## Why this exists

Daniel raised it this session. **It is genuinely open and must not be answered by an agent
acting alone.**

## ⚠️ CORRECTION 2026-08-22 — the collision premise was FALSE, and it was the whole ticket

This ticket was charted claiming `Code Review Request`/`Response` and `Feedback Request`/
`Response` are *"inherited-but-unused"* types a fifth similar name would collide with. **Daniel
corrected that as a factual error in the brief, and it is confirmed measured.**

All four sit in **`Microsoft.HiddenCategory`**, which Microsoft defines as *"the set of WITs that
you do not want users to create manually"*. They are **tooling-created back ends** — the legacy
TFVC code review handshake, and the *Request Feedback* flow — **not part of the namable
vocabulary**. A user or agent choosing a type never sees them.

🔴 **So there is no collision to design around, and the "how does a fifth name avoid the four"
question is void.** Ticket 0009's finding that they cannot be disabled or removed still stands
and is still true — it is simply no longer *relevant here*, because nothing was ever going to
route work into a type the picker does not offer. Do not resurrect the collision argument.

Three measured facts that survive, and that a future session will otherwise re-derive:

- **Category membership does not follow the type name.** `Microsoft.BugCategory` contains
  **`Issue`**, not `Bug`; `Bug` sits in `Microsoft.RequirementCategory`; `Issue` is itself
  hidden. Verified live 2026-08-22. Never infer a category from a name.
- **Three routes return three different type lists.** The process roster
  (`_apis/work/processes/{id}/workItemTypes`) returned **16**; the project-scoped
  `_apis/wit/workitemtypes` returned **22**, because it carries the hidden helpers; and
  `_apis/wit/workitemtypecategories` is a third view again. **Say which roster you mean.**
- `Microsoft.HiddenCategory` holds 10 types here: `Issue`, `Code Review Request`,
  `Code Review Response`, `Shared Steps`, `Shared Parameter`, `Test Suite`, `Test Plan`,
  `Test Case`, `Feedback Response`, `Feedback Request`.

**Already filed, do not duplicate:** **ADO #656** (no twig surface reports category membership at
all) and **ADO #657** (`twig process` lists 21 types, 10 of them hidden, unmarked). 🔴 **An omp
session is already working #657 in `/home/polyphonyrequiem/repos/twig-657` — do not touch that
card or that worktree.** The corrected guidance is commit `8054d550`.

## Blocked on 0002 and 0004 — and why

- **0004** may already answer it. If `Change` is ruled in, "request for a change" might be a
  *state* of a `Change` (`To do` already means "proposed"), a field, or a link — not a type.
  Resolving 0005 before 0004 risks inventing a type whose job `Change` already does.
- **0002** decides whether an inbound request is board-shaped at all, or whether it is the
  GitHub public-record surface plus `tracked_in`.

## What a good answer settles

- Type / state / field / link / nothing — and the argument, not just the verdict. **Judge it on
  its own merits**; there is no collision to design around.
- Who raises a `Request for Change` and to whom (feed from 0003). A request implies a requester
  and an approver; if neither role exists, the type has no lifecycle.
- Whether an inbound external request (a GitHub issue) is the same thing wearing a different
  hat — `AGENTS.md` already routes those: the issue **stays open** on GitHub, tracking moves to
  ADO.

## Do not

- Do not add the type by reflex because the name sounds reasonable.
- 🔴 **Do not revive the collision argument against the four hidden types.** It was a factual
  error, corrected by the author 2026-08-22 and confirmed measured. They are tooling back ends
  in `Microsoft.HiddenCategory` and are never offered to a chooser.
- Do not answer it from the type list alone. This is HITL — Daniel raised it, Daniel settles it.
- Do not mutate or delete the four hidden types. Ruling is in scope; mutation is not — and ADO
  offers no lever anyway (ticket 0009 §3.5).
