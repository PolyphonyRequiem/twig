---
id: 0004
title: Does reconciliation exist?
type: grilling
status: closed
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

**Yes. Reconciliation becomes a named module owning the staged -> published -> reconciled ->
invalidated lifecycle.** It is the missing concept the overloaded `Workspace` noun was hiding.

### 1. The evidence: five opinions, no owner

Every citation in this ticket was re-verified against live code on 2026-07-27 and holds:

- `SyncCoordinator.SyncItemAsync` (`src/Twig.Domain/Services/Sync/SyncCoordinator.cs:51`)
  evaluates `LastSyncedAt` vs `cacheStaleMinutes` inline -- a *read* silently becomes a
  network fetch.
- `RefreshOrchestrator` (`src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs:74-91`) holds a
  second, independent protect/overwrite branch, plus a `force` escape hatch that calls
  `workItemRepo.SaveBatchAsync` / `SaveAsync` directly, bypassing `ProtectedCacheWriter`,
  `SyncGuard` and `ConflictResolver` entirely. A data-loss path with no seam.
- `HydrateAncestorsAsync` (`:104`) writes through an unguarded `SaveBatchAsync`.
- `PendingChangeFlusher` (`src/Twig/Commands/PendingChangeFlusher.cs:142-145`) does its
  post-push resync via a direct `workItemRepo.SaveAsync`, also bypassing the protected writer.
- `SeedReconcileOrchestrator` documents itself as repairing "orphaned and stale seed links";
  it is a seed-ID garbage collector and the name is a squatter on the concept this ticket
  creates.

Two structural symptoms confirm the shape is wrong rather than merely untidy:

- **Nullable dependency, legacy overload.** `SeedPublishOrchestrator:97`,
  `SeedDiscardOrchestrator:43` and `ShowCommand:36` all take `IPendingChangeStore?` as a
  nullable parameter with a legacy constructor overload
  (`SyncCoordinator.cs:44` forwards `null`). Correctness depends on every construction site
  picking the non-legacy overload. **This is 0003's five-call-site argument at larger scale**
  -- there it was accepted as evidence of a wrong shape, and it is precedent here.
- **The FK ordering rule lives in four XML doc comments, not in code.** A rule that can only
  be obeyed by reading prose is not enforced. `#268/#269/#270/#271` are its recurrence, and
  **0004 owns their root cause**: #270 is a *reconciliation* failure -- the ADO create at
  step 7 is outside the transaction that rolls back at step 10d, so retry duplicates the
  remote item with no local record. Nothing in twig today is responsible for "the remote
  moved, reconcile it."

**The conflict-resolution half already exists and is good.** `ConflictResolver.Resolve`
(`src/Twig.Domain/Services/Sync/ConflictResolver.cs:36`) is the one genuinely deep module in
the cluster -- field-level, `Revision`-keyed, static, no dependencies. The problem is not that
twig lacks merge logic; it is that the paths that matter cannot reach it. The module this
ticket names exists **to make `ConflictResolver` the default rather than the exception.**

Per the ticket's own measurement note, this is argued on correctness and predictability, not
performance: 0011 attributes the `twig --help` spike to eager service construction, not sync
policy, and this decision claims no speed benefit.

### 2. The unit is the pending set, per Connection -- not a Bench

The ticket asks whether the working set is "`Workspace` finally meaning one thing." It is not:
`CONTEXT.md` §4 records that 0001 **retired** `Workspace` rather than disambiguating it, in
favour of **Connection** (`{org}/{project}`) and **Bench** (a named, persistent, switchable
set of items). So the live question is which of those owns the batch.

**Reconciliation scopes to the pending set, per Connection. A Bench is a view and is never a
sync unit** (owner, 2026-07-27).

Rationale: 0001 ruled that twig is a single-user local tool whose cache is disposable and
whose **pending set is the only durable state it owns**. A Bench is a *selection* -- what you
are looking at now -- and pending edits routinely exist on items outside the current bench.
Reconciling per-Bench would silently skip them, which is the same class of defect as
`SaveBatchProtectedAsync` reducing skipped IDs to a count. The reconciliation unit must be
everything twig owes the remote, not everything the user currently has in view.

This also answers `CONTEXT.md` §4's open question "whether a Bench scopes the sync boundary as
well as reads" -- **it does not.** It leaves open whether the pending set is per-Bench or
per-Connection only in the storage sense; the *reconciliation* boundary is per-Connection.

`WorkingSet` / `WorkingSetService` (`src/Twig.Domain/Services/Workspace/WorkingSetService.cs`,
100 lines, derived and recomputed per access) is unaffected by this ruling. It remains a
derived read projection and is not promoted.

### 3. Reads stop fetching. Staleness becomes an outcome

**Staleness-triggered fetch is removed, not relocated** (owner, 2026-07-27).

0002 settled the shape this rides on: three surfaces at one capability seam, and **REACH is an
outcome (`NotCached(id)`), not a policy**. The same applies to freshness. A read returns the
cache plus a `Stale(lastSyncedAt)` outcome, and each of the four experiences decides what that
means:

- **Rich CLI** renders a hint ("cached 3h ago -- `twig sync` to refresh").
- **Script CLI** gets a stable, network-free contract -- a read never costs a round-trip.
- **MCP** may treat `Stale` exactly as it treats `NotCached`: reach, on its own judgement.
  (This adds no MCP tool and does not violate the 0012 freeze.)
- **TUI** renders it as state.

Relocating the staleness check into the new module -- keeping the fetch, just centralising the
decision -- was rejected. It would preserve the actual defect: a read that silently costs a
network round-trip, which is the unpredictability that produced this ticket.

This is also the third application of 0003 §4's **silent-coercion rule**: twig does not coerce
an unknown or absent value into a plausible known one. "I have no fresh data" is not to be
papered over with an implicit fetch, exactly as an unrecognised state is not to be coerced to
`Proposed` (#286).

**Consequence for `--no-refresh`.** Its polarity inverts: refreshing becomes opt-in via
`--refresh`, and **`--no-refresh` is deleted outright, not aliased** (owner, 2026-07-27 --
twig is pre-1.0; take the break). Live sites to update:
`src/Twig/Commands/ShowCommand.cs:24`, `src/Twig/Commands/TreeRenderingService.cs:59`,
`src/Twig/Commands/WorkspaceCommand.cs:144`, `src/Twig/Program.cs:437` and `:1344`.

### 4. What the module owns, and what it must not become

Owns the four lifecycle transitions and nothing else:

- **staged -> published** -- the durable intent record written BEFORE the ADO call (0001 §4;
  the 7->10 window in `SeedPublishOrchestrator` is the open wound, and #270 is what falls
  through it). Per 0003, that record is keyed by `StagedIdentity`. **It is the same record
  0003 required -- do not design a second one.**
- **published -> reconciled** -- remote revision observed, `ConflictResolver` applied.
- **reconciled -> invalidated** -- remote moved under us; the item re-enters the pending set.
- The FK ordering rule, **as code** rather than as four XML doc comments.

Interface shape follows 0002's mutation-workflow seam: `Validate` -> *(surface interjects)* ->
`Execute` -> match an outcome union. Reads get the single-method read shape 0002 specified.

Guardrails, in the language of `codebase-design`:

- **Deep, not a facade.** The deletion test must pass: deleting this module must make
  complexity *reappear* across the five sites above, not vanish. If it ends up forwarding to
  `SyncCoordinator` and `RefreshOrchestrator` unchanged, it has not earned its keep and this
  decision was wrong.
- **`SyncCoordinator`, `RefreshOrchestrator` and `PendingChangeFlusher`'s resync path are
  absorbed, not wrapped.** The `force` branch is not preserved as a bypass; overwrite becomes
  an explicit resolution outcome that goes *through* `ConflictResolver`, not around it.
- **The nullable `IPendingChangeStore?` legacy overloads are deleted**, not defaulted. A
  dependency correctness depends on is not optional.
- **`SeedReconcileOrchestrator` is renamed** (it is a seed-link GC -- `SeedLinkRepair` or
  similar) so the name is free for the real concept.
- **`SaveBatchProtectedAsync` must stop reducing skipped IDs to a count**
  (`SyncCoordinator.cs:168`). It discards precisely the remote-side input `ConflictResolver`
  needs. A batch reconcile cannot be built on a cache that throws away what it saw.

### 5. Scope: decision only

No code moves in this ticket. Implementation is owned downstream:

- **0005 (persistence model)** -- the durable intent record and the pending-set schema,
  including 0003's `StagedIdentity` work, which 0003 already assigned there.
- **0006 (baseline revision / three-way merge)** -- the persisted per-item baseline revision
  this module requires and `SaveBatchProtectedAsync` currently destroys.

Both are unblocked by this answer. #277 (annotated tree for an arbitrary working set) is
plausibly unblocked too, since the reconciliation unit is now named. #268/#269/#270/#271 keep
their individual fixes; this ticket records that their shared root cause has an owner.

### 6. Open, deliberately not decided here

- Whether the pending set is *stored* per-Bench or per-Connection (only the reconciliation
  boundary is settled: per-Connection).
- Whether `WorkingSet` survives as a Bench's derived projection (`CONTEXT.md` §4, still open).
- The module's final name. `Sprig` remains reserved for planning-over-seeds and is **not** to
  be spent here.
