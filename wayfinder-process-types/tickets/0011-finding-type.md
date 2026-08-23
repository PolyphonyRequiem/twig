---
id: 0011
title: Is Finding still wanted as a level-less artifact type?
type: grilling
status: open
claimed_by:
blocked_by: [0002, 0003]
tracked_in: [685]
---

## Question

A prior session agreed **Finding** as a level-less artifact type alongside `Decision` and
`Idea`. It was never created. Is it still wanted — and what is it for that `Decision`, `Idea`,
`Research` and a `Bug` do not already cover?

🔴 **Constraint inherited from ticket [0002](0002-board-types-vs-wayfinder-markdown.md)'s partial
answer 2 (2026-08-22): if `Finding` is ruled in, it must DECLARE whether it is a wayfinder ticket
type, and that determines its fields.** The four wayfinder ticket types
(`Wayfinder Task`/`Prototype`/`Research`/`Grilling`) carry `MaturityNote`/`WayfinderAnswer`/
`WayfinderDecisionMaturity` because those fields describe *how well a question was answered* —
that cluster is designed, not incidental. **A `Finding` reads as a standing statement, not a
question in flight, so it should NOT inherit that cluster by proximity to `Research`.** Copying
the nearest neighbour's field set is exactly the mechanism by which the three `Maturity`
near-homonyms spread.

## Why this exists

@session:twig/20260821_175214_910f0c agreed it as the fourth of the four missing types. Unlike
`Change`, nothing is currently blocked on it, which makes it the easiest of the four to add by
reflex and the easiest to drop.

**A findings-shaped gap is plausible.** A research or grilling session frequently turns up a
fact that is not a decision, not schedulable work, and not a defect — the measured
three-`Maturity`-field drift in ticket 0007 is exactly such a thing. Today it has nowhere to
live except prose inside a ticket, where it is unfindable across maps.

**But the counter-argument is real.** `Research` already produces findings and carries
`Custom.WayfinderAnswer` to hold them; `Idea` exists and is level-less; and this repo's
convention is that evidence-carrying artifacts live in markdown reviewed with the code
(`AGENTS.md` §*Where work is tracked*). A `Finding` may be a `Research` item's answer field, a
markdown file, or a line in a map's *Decisions so far* — none of which is a new type.

## Blocked on 0002 and 0003

- **0002** decides whether artifacts are board-shaped or markdown-shaped. `Finding` is squarely
  an artifact, so it inherits that answer wholesale.
- **0003** supplies the demand test: which role produces a finding, and which role consumes one?
  If no role reads findings, the type is a write-only store.

## What a good answer settles

- Keep, drop, or fold into an existing type — and if kept, the sentence that distinguishes a
  `Finding` from a `Decision`, an `Idea`, and a `Research` answer.
- Its lifecycle. An artifact with `To do / Doing / Done` states is odd; a finding is true when
  written. Does it need states at all, and what does 0009 say about that?
- Whether it supersedes — a finding disproved later has the same problem `Decision` solves with
  `Custom.SupersededBy`.
- What fields it carries (feeds 0007) and where it sits, if anywhere (feeds 0010).

## Do not

- Do not accept "a prior session agreed it" as the argument. That agreement is input, not
  authority — and it never executed, which is itself worth weighing.
- Do not add it for symmetry with `Decision` and `Idea`. Symmetry is not a use case.
