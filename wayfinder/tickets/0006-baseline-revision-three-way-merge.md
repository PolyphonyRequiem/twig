---
id: 0006
title: Baseline revision for three-way merge
type: grilling
status: open
blocked_by: [0001, 0004]
---

## Question

Should twig persist a baseline revision per work item to enable three-way merge?
`ConflictResolver` currently makes a two-way guess and documents its own limitation at
`ConflictResolver.cs:117-120`. The audit's assessment: one persisted baseline-revision
integer converts it to a three-way merge — the highest leverage-per-unit-of-interface
change found in the entire review.

## Retitled and re-scoped (0001, 2026-07-26)

**Was: "Team-scale baseline revision."** The old framing said this "only pays off if 0001
answers *shared substrate*." **0001 answered the opposite** — twig is a single-user local
tool, and the shared substrate is ADO, never twig.

That does **not** kill this ticket; it corrects why it matters. Twig already has a second
writer at N=1 users, and it is **ADO**. A baseline revision enables three-way merge between
*local edits* and *remote changes*, which is a single-user concern. Nothing about it was
ever team-scale — the title was wrong, not the idea.

Supporting evidence from the ADO research (0001 §7): updates fenced by a JSON-Patch `test`
op on `/rev` are replay-safe, which means the remote side of the three-way merge has a
usable revision fence. `System.Rev` is reliably returned on reads. So the mechanism this
ticket needs is available — full findings in
`%TEMP%\twig-review\research-ado-batch-push.md`.

Still blocked by 0004: a baseline only means something once reconciliation is a named
module that owns when comparison happens.

## Answer

<!-- empty until resolved -->
