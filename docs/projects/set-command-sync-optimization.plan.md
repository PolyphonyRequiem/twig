# Plan: `twig set` Syncs Entire Working Set Instead of Just Target Item

> **Date**: 2026-03-31
> **Status**: 🔨 In Progress
> **ADO Issue**: #1352

---

## Executive Summary

`twig set` currently takes ~26 seconds because after setting active context it unconditionally
computes the full working set (7 SQLite queries spanning parents, children, sprint items, seeds,
and dirty IDs) and then calls `SyncCoordinator.SyncWorkingSetAsync` which fetches **every stale
item in the sprint** from ADO. This was designed for the cold-cache "set after init" case, but
it runs identically on warm-cache context switches — the dominant use case.

This plan replaces the full working set sync in `SetCommand` with a **targeted sync** that only
refreshes the target item and its immediate parent chain. Sprint-wide sync remains the
responsibility of `twig refresh`, `twig tree`, and `twig status` — commands that actually
display sprint-scoped data. A new `SyncCoordinator.SyncItemSetAsync` method provides a
lightweight batch sync that skips the working set computation entirely.

Eviction behavior is unchanged: still triggered only on cache miss (`fetchedFromAdo`), still
uses the full working set to determine keep-IDs. The `ComputeAsync` call is retained only for
eviction (cache-miss path); on cache-hit, it is skipped entirely.

Expected performance improvement: **~26s → <1s** for cache-hit `twig set`, with no behavior
regression for cache-miss scenarios or other commands. Agent workflows that bulk-loop over items
drop from minutes to seconds.

Total estimated scope: ~250 LoC across 1 epic (1 PR).

---

## Background

### Current Architecture

The `twig set <id>` command flow (from `SetCommand.ExecuteCoreAsync`):

```
twig set <id>
  ├─ Resolve work item (cache or ADO fetch)        ← fast (1 SQLite query or 1 ADO call)
  ├─ Hydrate parent chain (auto-fetch on miss)     ← fast (1 SQLite query + 0-2 ADO calls)
  ├─ Set active context                            ← fast (1 SQLite write)
  ├─ Record navigation history                     ← fast (1 SQLite write)
  ├─ WorkingSetService.ComputeAsync()              ← 7 SQLite queries
  │   ├─ GetActiveWorkItemIdAsync()
  │   ├─ GetParentChainAsync()
  │   ├─ GetChildrenAsync()
  │   ├─ Resolve iteration (no ADO call when passed)
  │   ├─ GetByIterationAsync() / GetByIterationAndAssigneeAsync()
  │   ├─ GetSeedsAsync()
  │   └─ SyncGuard.GetProtectedItemIdsAsync()
  ├─ EvictExceptAsync(workingSet.AllIds)            ← only on cache miss (FR-012)
  └─ SyncCoordinator.SyncWorkingSetAsync(workingSet)  ← THE BOTTLENECK
      ├─ Filter AllIds (exclude seeds, dirty)
      ├─ Per-item staleness check (N GetByIdAsync calls)
      ├─ Concurrent ADO FetchAsync for each stale item   ← 5-30 HTTP requests
      └─ SaveBatchProtectedAsync                         ← batch SQLite write
```

The bottleneck is `SyncWorkingSetAsync`: for a typical sprint with 30 items, if most are stale
(>5 min since last sync), this fires 20-30 concurrent ADO REST calls. Each call takes ~0.5-1s,
but the concurrency still accumulates to ~10-26s wall-clock time due to HTTP connection limits,
token-refresh overhead, and ADO rate limiting.

### Call-Site Audit

The following table inventories all callers of the two services being modified:

#### `SyncCoordinator.SyncWorkingSetAsync` Callers

| File | Method | Usage | Impact of Change |
|------|--------|-------|------------------|
| `src/Twig/Commands/SetCommand.cs` | `ExecuteCoreAsync` (L159, L168) | Full working set sync on every `twig set` | **MODIFIED** — replaced with targeted sync |
| `src/Twig/Commands/RefreshCommand.cs` | `ExecuteCoreAsync` (L224) | After sprint fetch + ancestor hydration | No change — refresh legitimately syncs full sprint |
| `src/Twig/Commands/TreeCommand.cs` | `ExecuteAsync` (L123, L202) | TTY + non-TTY paths after tree render | No change — tree displays sprint-scoped data |
| `src/Twig/Commands/StatusCommand.cs` | `ExecuteCoreAsync` (L145, L281) | TTY + non-TTY paths after status render | No change — status displays sprint-scoped data |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | `SyncWorkingSetAsync` (L130) | Wrapper method for domain orchestration | No change |
| `src/Twig.Domain/Services/StatusOrchestrator.cs` | `SyncWorkingSetAsync` (L72) | Best-effort sync wrapper | No change |

#### `WorkingSetService.ComputeAsync` Callers

| File | Method | Usage | Impact of Change |
|------|--------|-------|------------------|
| `src/Twig/Commands/SetCommand.cs` | `ExecuteCoreAsync` (L143) | Compute working set for eviction + sync | **MODIFIED** — only called on cache-miss path for eviction |
| `src/Twig/Commands/RefreshCommand.cs` | `ExecuteCoreAsync` (L223) | Compute for post-refresh sync | No change |
| `src/Twig/Commands/TreeCommand.cs` | `ExecuteAsync` (L118, L201) | Compute for tree sync | No change |
| `src/Twig/Commands/StatusCommand.cs` | `ExecuteCoreAsync` (L133, L280) | Compute for status sync | No change |
| `src/Twig/Commands/WorkspaceCommand.cs` | `ExecuteAsync` (L231) | Dirty orphan computation | No change |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | `SyncWorkingSetAsync` (L129) | Wrapper method | No change |
| `src/Twig.Domain/Services/StatusOrchestrator.cs` | `SyncWorkingSetAsync` (L71) | Best-effort wrapper | No change |

#### `SyncCoordinator.SyncItemAsync` — Existing Single-Item Sync

| File | Method | Usage | Impact of Change |
|------|--------|-------|------------------|
| (test code only) | SyncCoordinatorTests | Tests staleness check + fetch | **REUSED** as building block for the new `SyncItemSetAsync` |

**Key insight**: `SyncItemAsync` already exists and handles per-item staleness correctly. The
new `SyncItemSetAsync` will be a batch wrapper around the same staleness logic, avoiding the
full working set computation.

### Why Not Just Lower `CacheStaleMinutes`?

Lowering `CacheStaleMinutes` doesn't help — the items still need to be *checked*, which
requires N `GetByIdAsync` calls just to discover they're fresh. The real win is reducing N
from "all sprint items" to "target + parent chain" (typically 1-4 items).

---

## Design Decisions

### DD-1: Targeted Sync Scope — Target Item + Parent Chain

**Decision**: `twig set` syncs only the target item and its parent chain (IDs already
resolved during the hydration step), not the full working set.

**Rationale**: The `set` command's purpose is context-switching, not data refresh. The user
wants to see the current state of the item they're switching to, plus its ancestry for tree
context. Sprint items, children, and seeds are not displayed by `set` and shouldn't trigger
ADO calls.

**Trade-off**: If a user does `twig set 42` then immediately `twig tree`, the tree command
will still sync sprint items (it already calls `SyncWorkingSetAsync` itself). There is no
double-sync risk because `SyncWorkingSetAsync` checks per-item freshness and the items
synced by `set` will be fresh.

### DD-2: New `SyncItemSetAsync` Method on SyncCoordinator

**Decision**: Add a new `SyncItemSetAsync(IReadOnlyList<int> ids, CancellationToken)` method
to `SyncCoordinator` that syncs a specific set of IDs using the same staleness logic as
`SyncWorkingSetAsync` but without requiring a `WorkingSet` object.

**Rationale**: The existing `SyncItemAsync` handles one item at a time. We need batch
behavior (concurrent fetch, batch save via `ProtectedCacheWriter`) for the parent chain.
Creating a new method keeps `SyncWorkingSetAsync` unchanged (no risk to other callers).

**Alternative considered**: Modify `SyncWorkingSetAsync` to accept an optional "sync scope"
parameter. Rejected because it changes the semantics of an existing method used by 6 callers
— too risky for a performance optimization.

### DD-3: Skip `ComputeAsync` on Cache-Hit Path

**Decision**: On cache hit (`!fetchedFromAdo`), skip `WorkingSetService.ComputeAsync` entirely.
On cache miss, still compute the working set for eviction (FR-012 unchanged).

**Rationale**: `ComputeAsync` runs 7 SQLite queries to build the working set. This is
unnecessary when the only consumer was `SyncWorkingSetAsync` (now removed from the hot path)
and eviction (only runs on cache miss). Removing 7 queries from the cache-hit path saves
~10-50ms.

**Risk**: Eviction on cache miss still needs the full working set. The fix keeps `ComputeAsync`
in the `fetchedFromAdo` branch only.

### DD-4: No `--sync` Flag (Yet)

**Decision**: Do not add a `--sync` flag to `twig set` in this iteration.

**Rationale**: The `twig refresh` command already provides explicit full sync. Adding a flag
to `set` creates a second way to do the same thing, which is confusing. If user feedback
shows demand, a `--sync` flag can be added later as a one-line change (call
`SyncWorkingSetAsync` when the flag is set).

### DD-5: TTY vs Non-TTY Path Unification

**Decision**: Both TTY and non-TTY paths in `SetCommand` will use the same targeted sync
logic. The TTY path currently wraps sync in `RenderWithSyncAsync` (live spinner). Since the
targeted sync is fast (<1s), we simplify by removing the live-render wrapper and just
running the sync inline before output.

**Rationale**: A sub-second sync doesn't benefit from a spinner. Removing the
`RenderWithSyncAsync` wrapper simplifies the code and eliminates the Spectre.Console
dependency in the sync path. The renderer is still used for interactive disambiguation
(pattern matches).

**Trade-off**: If the target item + parent chain are all stale (first sync after cold cache),
the sync might take 2-3s with no spinner. This is acceptable because: (a) it's still 10x
faster than the current 26s, and (b) cold-cache is rare in practice.

---

## Implementation Plan

### Epic 1: Targeted Sync for SetCommand (Deep — few files, complex logic changes)

**Successor links**: None (single epic)
**PR scope**: ~230 LoC, ~7 files (source + tests)

#### Task 1.1: Add `SyncItemSetAsync` to `SyncCoordinator`

**Files**: `src/Twig.Domain/Services/SyncCoordinator.cs`
**Change**: Add new public method:
```csharp
public async Task<SyncResult> SyncItemSetAsync(
    IReadOnlyList<int> ids, CancellationToken ct = default)
```
**Logic**:
- Filter out negative IDs (seeds)
- Check per-item `LastSyncedAt` against `_cacheStaleMinutes` (reuse existing staleness pattern)
- Fetch stale items concurrently via `_adoService.FetchAsync`
- Save via `_protectedCacheWriter.SaveBatchProtectedAsync`
- Return appropriate `SyncResult` (UpToDate, Updated, PartiallyUpdated, Failed)

This is essentially the core of `SyncWorkingSetAsync` but accepting `IReadOnlyList<int>`
instead of `WorkingSet`, and without the `DirtyItemIds` filter (the IDs passed in are
explicit — the caller decides what to sync). Dirty-item protection is still enforced by
`ProtectedCacheWriter.SaveBatchProtectedAsync` at write time.

**Estimated LoC**: ~40

#### Task 1.2: Refactor `SetCommand.ExecuteCoreAsync` Sync Logic

**Files**: `src/Twig/Commands/SetCommand.cs`
**Change**: Replace the working set compute + sync block (lines 139-178) with:

1. Build targeted sync scope: `[item.Id] + parentChainIds` (parent chain IDs already
   available from the hydration step at line 122-131)
2. On **cache hit**: call `syncCoordinator.SyncItemSetAsync(targetIds, ct)` — skip
   `ComputeAsync` entirely
3. On **cache miss**: call `workingSetService.ComputeAsync()` for eviction, then call
   `syncCoordinator.SyncItemSetAsync(targetIds, ct)` — eviction uses full working set,
   sync uses targeted scope
4. Output the work item before sync (print immediately, sync best-effort after)
5. Keep the `try/catch` best-effort pattern (sync failure never fails the command)

**Structural changes**:
- Capture parent chain IDs during the hydration step (currently discarded)
- Remove `RenderWithSyncAsync` wrapper from TTY path (sync is now fast enough to not need
  a spinner)
- Both TTY and non-TTY paths: print item → sync targeted set → done
- Update XML doc comment to reflect targeted sync behavior

**Estimated LoC**: ~65 (net reduction from current code, but significant restructuring)

#### Task 1.3: Add `SyncItemSetAsync` Unit Tests

**Files**: `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs`
**Change**: Add test cases for `SyncItemSetAsync` (renumbered from 1.4 after collapsing doc task):
- Fresh items → returns `UpToDate`, no ADO calls
- Stale items → fetches from ADO, returns `Updated(count)`
- Mixed fresh/stale → only fetches stale items
- Negative IDs (seeds) → filtered out
- Empty ID list → returns `UpToDate`
- ADO failure on one item → returns `PartiallyUpdated`
- Protected items → saved via `SaveBatchProtectedAsync` (dirty protection at write time)

**Estimated LoC**: ~100

#### Task 1.4: Update `SetCommand` Tests for Targeted Sync

**Files**: `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs`
**Change**: Update existing tests to reflect new behavior:
- `Set_CacheHit_StillCallsSyncWorkingSet` → rename to `Set_CacheHit_SyncsTargetAndParentChain`
  and verify only the target item's ID (and parent chain) is synced, NOT the full working set
- `Set_CacheMiss_TriggersEviction` → verify eviction still works (still calls `ComputeAsync`)
- `Set_CacheHit_SkipsEviction` → verify `ComputeAsync` is NOT called on cache hit
- Add new test: `Set_WithParentChain_SyncsParentIds` — verify parent IDs are included in sync

**Estimated LoC**: ~35

#### Acceptance Criteria (Epic 1)

- [ ] `twig set` completes in <2s for cached items (no full working set sync)
- [ ] Target item and parent chain are synced (staleness-checked, fetched if stale)
- [ ] Cache-miss path still computes working set for eviction (FR-012 unchanged)
- [ ] Cache-hit path skips `ComputeAsync` entirely
- [ ] Sprint-wide sync only happens on `twig refresh`, `twig tree`, `twig status`
- [ ] `SyncWorkingSetAsync` callers in RefreshCommand, TreeCommand, StatusCommand unchanged
- [ ] All existing SetCommand tests pass (updated to match new behavior)
- [ ] New `SyncItemSetAsync` tests cover fresh, stale, mixed, seed, empty, failure cases
- [ ] No new warnings (TreatWarningsAsErrors=true)
- [ ] AOT-compatible (no reflection, source-gen JSON context not affected)

---

## Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| User expects `twig set` to refresh sprint data | Low | Low | `twig tree` and `twig status` still sync sprint. `twig refresh` explicitly refreshes. Hint engine could suggest `twig refresh` if sprint data is stale. |
| Eviction on cache miss requires `ComputeAsync` | Medium | N/A (by design) | Cache-miss path retains `ComputeAsync` for eviction. Only cache-hit path skips it. |
| Parent chain hydration misses ancestors >1 level deep | Low | Low | `GetParentChainAsync` already walks the full chain recursively in SQLite. Only missing parents trigger ADO fetch, which is handled during hydration (line 122-131). |
| `SyncItemSetAsync` doesn't filter dirty items | Low | Low | `ProtectedCacheWriter.SaveBatchProtectedAsync` enforces dirty protection at write time. Items with pending changes are never overwritten. |
| Removal of `RenderWithSyncAsync` wrapper changes TTY output | Low | Medium | The wrapper currently shows a spinner during sync. With sub-second sync, the spinner is imperceptible. Visual output (the work item text) is unchanged. |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `SyncItemSetAsync` use `FetchBatchAsync` instead of concurrent `FetchAsync` calls? | Low | `FetchBatchAsync` exists but uses WIQL under the hood, which has a different code path. Concurrent `FetchAsync` matches the existing `SyncWorkingSetAsync` pattern and is proven. Can be optimized later. |

---

## Appendix: Performance Model

### Before (Current)

| Operation | Time | Count |
|-----------|------|-------|
| `ComputeAsync` (7 SQLite queries) | ~20ms | 1 |
| `SyncWorkingSetAsync` staleness check | ~50ms | 1 (N GetByIdAsync) |
| ADO FetchAsync (per stale item) | ~800ms | 20-30 |
| `SaveBatchProtectedAsync` | ~30ms | 1 |
| **Total** | **~16-26s** | |

### After (Targeted)

| Operation | Time | Count |
|-----------|------|-------|
| `SyncItemSetAsync` staleness check | ~5ms | 1 (1-4 GetByIdAsync) |
| ADO FetchAsync (per stale item) | ~800ms | 0-4 |
| `SaveBatchProtectedAsync` | ~5ms | 0-1 |
| **Total (cache hit, all fresh)** | **~5ms** | |
| **Total (cache hit, target stale)** | **~800ms** | |
| **Total (cache miss, target + eviction)** | **~1-3s** | |
