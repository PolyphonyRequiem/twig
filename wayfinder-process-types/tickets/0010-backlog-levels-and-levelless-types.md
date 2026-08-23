---
id: 0010
title: What are the backlog levels for, and does every type need one?
type: grilling
status: open
claimed_by:
blocked_by: [0004, 0009]
---

## Question

What does sitting at a backlog level *mean* here, and does every type need one? `Decision` and
`Idea` are deliberately level-less. Is level-lessness a real category — "artifact, not
schedulable work" — and which types belong in it?

## The measured starting point

```
rank 30  Initiatives  Microsoft.VSTS.Basic.EpicBacklogBehavior  inherited
rank 20  Work         System.RequirementBacklogBehavior         inherited
rank 10  Tasks        System.TaskBacklogBehavior                system
```

```
Initiatives : Epic, Map
Work        : Bug, Feature, Grilling, Prototype, Research, Spec, Wayfinder Task
Tasks       : Task
(no level)  : Decision, Idea            <- deliberate
(no level)  : Issue, Test Case/Plan/Suite, the four dormant Request/Response types
```

🔴 **The behaviour reference names never change.** The Initiatives level is
`Microsoft.VSTS.Basic.EpicBacklogBehavior` in the API forever, and `Work` is
`System.RequirementBacklogBehavior`. Anyone reading the API and expecting "Initiatives" or
"Work" gets a false green. **This is recorded, not a defect** — but note it lands squarely in
this repo's documented false-green class, so the ruling should say how a reader is protected
from it.

**From the closed research (do not re-derive): backlog level governs *display*, not link
legality.** ADO cannot enforce type-level parent/child policy at all — six avenues closed —
which is why ADO #615 ("twig needs a declared policy engine, not inferred hierarchy rules")
exists. So a level assignment is a statement about which backlog a type appears on, and
**nothing at all** about what may parent what.

That has a sharp consequence this ticket must confront: **if level is display-only, "level-less"
means "does not appear on a backlog" — which is a weaker claim than "is not schedulable".** The
`Decision`/`Idea` choice may be doing less work than it appears to.

## Blocked on 0004 and 0009

0004 decides which types exist; 0009 establishes whether a type can be created with no level and
whether a level can be removed later.

## What a good answer settles

- What level-lessness asserts, precisely, given level is display-only.
- Which types are artifacts and which are schedulable work, and whether that line is the same
  line as the level/no-level line. `Map` is at Initiatives while `Decision` has no level — both
  are wayfinder artifacts, so something distinguishes them and it should be written down.
- Where each type from 0004 sits.
- 🔴 **`Change` specifically.** "A pull-requestable unit of work" is a *size* claim. Work or
  Tasks? And since level cannot enforce parenting, what actually stops a `Change` being
  parented to a `Decision` — a twig-side declared policy (#615), or nothing?
- Whether the three level names survive, and how the reference-name mismatch is documented so
  the false green cannot bite an API reader.
- What twig must express generically about levels for a customer whose levels are named
  differently (feeds 0001).

## Do not

- Do not re-derive the parent/child enforcement research. It is closed with primary sources.
- Do not treat level as a hierarchy rule. It is display.
- Do not PUT behaviour changes to the board. Ruling only. (And when the build comes: **PUT, not
  PATCH** — PATCH returns 405.)
