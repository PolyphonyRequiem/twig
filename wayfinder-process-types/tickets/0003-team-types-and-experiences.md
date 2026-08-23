---
id: 0003
title: What kinds of team member exist, and how does each use twig to do their work?
type: grilling
status: open
blocked_by: []
tracked_in: [677]
claimed_by:
---

## Question

Who are the kinds of person working this board — and for each, what is their working day in
twig: which verbs, which views, which types do they touch, and what does twig fail to serve them
today?

## Why this exists

Daniel named this part explicitly when he asked for the map:

> *"...including the **team types and experiences and how they will use the twig to accomplish
> their goals**."*

🔴 **The brief flags this as the part most likely to be under-served if the map is treated as a
pure type-taxonomy exercise.** That is the failure mode to guard against: a beautifully settled
type list that nobody can be shown how to use. A taxonomy justified by "who needs this and when"
is a different, better taxonomy than one justified by symmetry.

This ticket is deliberately **unblocked and on the frontier**, even though it reads like it
should follow the type set. Taking it early is a feature: an experience-first pass gives 0004,
0005 and 0011 a demand-side test — *which role is worse off if this type does not exist?* — that
a purely structural argument cannot supply.

## What a good answer settles

- The roster of team types. `docs/agents/triage-labels.md` already names **five canonical
  roles** applied as `System.Tags` (ADO has no labels) — are those the same roster, a subset, or
  a different axis entirely?
- Agents are one of the team types. An agent session driving twig has different needs from a
  human (`--output json`, seeds, `twig process`), and the docs under `docs/agents/` exist for
  it. Say whether agents are a first-class role here.
- Per role: the entry point, the verbs used, the types touched, what "done" looks like.
- What twig does **not** serve today. This is the part that turns into work items, so name gaps
  concretely rather than as sentiment.
- Whether roles belong in the process at all (fields? tags? area paths?) or are purely a
  documentation and CLI-affordance concern.

## Do not

- Do not invent roles for symmetry. A role nobody occupies is worse than an unnamed one.
- Do not let this collapse into a `twig --help` transcript. The question is about goals and
  friction, not the command list — `docs/agents/issue-tracker.md` already has the verbs.
- Do not settle the type set here. Feed the demand-side evidence to 0004/0005/0011 and stop.
