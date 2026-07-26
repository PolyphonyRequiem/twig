---
id: 0004
title: Does reconciliation exist?
type: grilling
status: open
blocked_by: [0001]
---

## Question

Should local/remote reconciliation become a named module owning the staged → published → reconciled → invalidated lifecycle? Today it is not a named concept: 11 scattered sites across 4 assemblies, and `SeedReconcileOrchestrator` is misleadingly named — it is a seed-ID garbage collector, not local/remote reconciliation. The FK ordering rule that caused #268/#269/#270 lives in FOUR XML doc comments rather than in code, and both seed orchestrators accept `IPendingChangeStore?` as a NULLABLE parameter with legacy overloads, so choosing the wrong constructor silently reintroduces the bugs. Relatedly: `CONTEXT.md` §4 records that `Workspace` names three unrelated things — an overloaded core noun often hides a missing concept, and the missing one may be this.

## Scenario — the named working set (owner, 2026-07-26)

Owner's framing: *"we have inconsistent ideas on when we should sync and when we should
leave things in a named working set of some point that we interact with locally, and then
batch update with intelligent conflict resolution."*

This is a candidate answer shape, not a decision. Evidence for and against, from the
ledgers:

**The "when to sync" decision is currently nobody's.** Staleness
(`LastSyncedAt` vs `cacheStaleMinutes`) is evaluated inside
`SyncCoordinator.SyncItemAsync` (`src/Twig.Domain/Services/Sync/SyncCoordinator.cs:51`) —
so a *read* silently becomes a network fetch. `RefreshOrchestrator` holds a **second,
independent** copy of the protect/overwrite branch (`RefreshOrchestrator.cs:74-91`), plus
a `force` escape hatch that bypasses `SyncGuard` and `ConflictResolver` entirely
(`:74-82`) — a data-loss path with no seam. `HydrateAncestorsAsync` (`:104`) writes via an
unguarded `SaveBatchAsync`. `PendingChangeFlusher.cs:142-145` resyncs through a direct
`workItemRepo.SaveAsync`, bypassing `ProtectedCacheWriter`. Five different opinions about
when local and remote meet.

**The conflict-resolution half already exists and is good.** `ConflictResolver.Resolve`
(`src/Twig.Domain/Services/Sync/ConflictResolver.cs:36`) is the one genuinely deep module
in this cluster — field-level, `Revision`-keyed. It is simply not reachable from the paths
that matter. A working-set model would make it the default rather than the exception.

**What a working set would need that does not exist:** a persisted baseline revision per
item — see 0006. Today `SaveBatchProtectedAsync` reduces skipped IDs to a count
(`SyncCoordinator.cs:168`), discarding exactly the remote-side input `ConflictResolver`
requires. A batch reconcile cannot be built on a cache that throws away what it saw.

**Open questions for the session:**
1. Is the working set a new named noun, or is it `Workspace` finally meaning one thing?
   (`CONTEXT.md` §4: `Workspace` currently names three unrelated things.)
2. Does an explicit sync boundary mean commands stop fetching implicitly — i.e. is
   staleness-triggered fetch removed rather than relocated?
3. What is the story for a read that genuinely wants fresh data (`--no-refresh` exists
   today as the inverse default)?

**Measurement note:** the perceived-slowness complaint that produced this scenario has
been measured separately in 0011 and the headline symptom (a ~5s spike on `twig --help`)
looks like eager service construction, *not* sync policy. Do not let this scenario
inherit a performance justification it has not earned; argue it on correctness and
predictability.

## Answer

<!-- empty until resolved -->
