---
id: 0006
title: Team-scale baseline revision
type: grilling
status: open
blocked_by: [0001, 0004]
---

## Question

Should twig persist a baseline revision per work item to enable three-way merge? `ConflictResolver` currently makes a two-way guess and documents its own limitation at `ConflictResolver.cs:117-120`. The audit's assessment: one persisted baseline-revision integer converts it to a three-way merge — the highest leverage-per-unit-of-interface change found in the entire review. This is the smallest concrete step toward team-scale correctness, but it only pays off if 0001 answers "shared substrate."

## Answer

<!-- empty until resolved -->
