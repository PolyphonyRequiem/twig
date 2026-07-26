---
id: 0004
title: Does reconciliation exist?
type: grilling
status: open
blocked_by: [0001]
---

## Question

Should local/remote reconciliation become a named module owning the staged → published → reconciled → invalidated lifecycle? Today it is not a named concept: 11 scattered sites across 4 assemblies, and `SeedReconcileOrchestrator` is misleadingly named — it is a seed-ID garbage collector, not local/remote reconciliation. The FK ordering rule that caused #268/#269/#270 lives in FOUR XML doc comments rather than in code, and both seed orchestrators accept `IPendingChangeStore?` as a NULLABLE parameter with legacy overloads, so choosing the wrong constructor silently reintroduces the bugs. Relatedly: `CONTEXT.md` §4 records that `Workspace` names three unrelated things — an overloaded core noun often hides a missing concept, and the missing one may be this.

## Answer

<!-- empty until resolved -->
