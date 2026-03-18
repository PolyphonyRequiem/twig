# Working Set — Solution Design & Implementation Plan

> **Status**: Draft v1.2  
> **Date**: 2026-03-18  
> **Author**: Daniel Green  
> **PRD**: [`docs/projects/twig-working-set.prd.md`](twig-working-set.prd.md)  
> **Revision Notes**: v1.2 — Gap review. Added: sprint iteration drift deferral (DD-09), SetCommand sync indicator fix (WS-010a), RefreshCommand working set sync (FR-014, WS-021a), NavigationCommands delegation clarification, EPIC-005 sequencing correction, FR-013 test coverage. Previous: v1.1 fixed NFR-001 contradiction, cache-hit ComputeAsync gap, WorkspaceCommand IPendingChangeStore issue, N+1 SyncGuard query pattern.

---

## Executive Summary

This document details the solution design and implementation plan for the **Working Set** feature in Twig CLI. The Working Set introduces a context-aware cache lifecycle: read commands (`status`, `tree`, `ws`, `up`, `down`) automatically sync stale items after rendering cached data; context switches (`twig set` on cache miss) evict non-dirty items outside the new working set; and `twig ws` displays dirty orphans from prior contexts. Additionally, all commands that resolve work item context adopt `ActiveItemResolver` as a non-nullable dependency, eliminating backward-compatibility fallback paths. This builds directly on the v0.4.0 cache-first architecture (already shipped) and requires no schema changes, no new external dependencies, and no new SQLite tables.

---

## Background

### Current State (v0.4.0)

Twig's cache-first architecture introduced several key services:

- **`ActiveItemResolver`** — resolves work items from cache with auto-fetch on miss, returning a discriminated union (`Found`, `FetchedFromAdo`, `NoContext`, `Unreachable`)
- **`SyncCoordinator`** — coordinates staleness-based sync with `SyncItemAsync` and `SyncChildrenAsync`, using `ProtectedCacheWriter` to guard dirty items
- **`ProtectedCacheWriter`** — saves work items while protecting dirty/pending items from overwrite, delegating to `SyncGuard`
- **`SyncGuard`** — static helper that computes the union of dirty work items and pending change item IDs
- **`RenderingPipelineFactory` + `IAsyncRenderer`** — progressive rendering with `RenderWithSyncAsync` for cache-then-revise UX

Commands like `SetCommand` already use `ActiveItemResolver` and `SyncCoordinator.SyncChildrenAsync`. However, several gaps remain:

1. **No working set sync on read**: `StatusCommand`, `TreeCommand`, `WorkspaceCommand`, and `NavigationCommands` render from cache but do not sync stale items afterward
2. **Unbounded cache growth**: The cache accumulates items indefinitely across context switches — no eviction mechanism exists
3. **No dirty orphan visibility**: Items with pending changes from prior contexts are invisible in `twig ws`
4. **Inconsistent `ActiveItemResolver` adoption**: 6 commands (`SeedCommand`, `BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `GitContextCommand`) still resolve items via raw `contextStore.GetActiveWorkItemIdAsync()` + `workItemRepo.GetByIdAsync()` without auto-fetch
5. **Nullable `ActiveItemResolver?` patterns**: `StatusCommand`, `TreeCommand`, `NavigationCommands`, and `WorkspaceCommand` accept `ActiveItemResolver?` with fallback paths — dead code from the v0.4.0 migration

### Why Now

The v0.4.0 cache-first infrastructure provides all the primitives needed (staleness tracking, protected writes, progressive rendering). The working set is the natural next step to complete the cache lifecycle.

---

## Problem Statement

1. **Stale reads without notification**: Commands like `twig status` and `twig tree` show cached data that may be minutes or hours old, with no automatic sync to detect remote changes.

2. **Unbounded cache size**: Every `twig set`, `twig refresh`, and `twig seed` adds items to the SQLite cache. Over weeks of use across multiple features/sprints, the cache grows unboundedly, making iteration queries slower and workspace views cluttered.

3. **Invisible dirty orphans**: When a user switches context (e.g., from Feature A to Feature B), any unsaved edits to Feature A's items remain in the cache but are not surfaced anywhere — the user may forget to run `twig save`.

4. **Inconsistent auto-fetch behavior**: 6 commands fail silently when the active item is not in cache, instead of auto-fetching from ADO. This creates a confusing UX where some commands handle cache misses gracefully and others don't.

5. **Dead code paths**: Nullable `ActiveItemResolver?` fallback branches in 4 commands are never exercised in production (DI always injects a non-null instance), adding complexity without value.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|----|------|---------|
| G-1 | Read commands sync stale working set items after cached render | `SyncWorkingSetAsync` called after render; display revised in-place when items change |
| G-2 | Context switch evicts non-dirty items outside working set | `work_items` row count bounded to working set size + dirty orphans after `twig set` cache miss |
| G-3 | `twig ws` shows dirty orphans | "Unsaved changes" section visible when dirty items exist outside sprint/seed scope |
| G-4 | All context-resolving commands use `ActiveItemResolver` | Auto-fetch on cache miss in `seed`, `branch`, `commit`, `pr`, `stash`, `context` |
| G-5 | `ActiveItemResolver` non-nullable everywhere | No `ActiveItemResolver?` parameters in any command; no fallback paths |

### Non-Goals

- **Offline queue visibility** (`twig pending`) — separate feature
- **Background daemon sync** — separate feature
- **Multi-project working sets** — requires multi-project support
- **Separate `working_set` SQLite table** — the cache IS the working set after eviction (RD-001)
- **Schema migrations** — eviction uses existing `work_items` table
- **Sprint lifecycle redesign** — sprint iteration drift (iteration rolls over between commands), automatic sprint item population on context switch, and sprint-aware cache warming are deferred to a dedicated sprint feature redesign. The working set uses whatever sprint items are currently in cache (populated by `twig refresh`). See DD-09.

---

## Requirements

### Functional Requirements

| ID | Requirement | Traces To |
|----|-------------|-----------|
| FR-001 | `WorkingSetService.ComputeAsync()` returns a `WorkingSet` containing: active item, parent chain, children, sprint items (filtered by assignee when configured), seeds, and dirty items | PRD FR-001 |
| FR-002 | `twig set` on cache miss (`FetchedFromAdo`) triggers eviction of non-dirty items outside the new working set | PRD FR-002 |
| FR-003 | Eviction MUST NOT delete items with pending local changes | PRD FR-003 |
| FR-004 | `StatusCommand` renders from cache, then calls `SyncWorkingSetAsync`, then revises display in-place | PRD FR-004 |
| FR-005 | `TreeCommand` renders cached tree, then syncs working set, then revises if changed | PRD FR-005 |
| FR-006 | `WorkspaceCommand` displays dirty orphans in a dedicated "Unsaved changes" section | PRD FR-006 |
| FR-007 | `NavigationCommands` use non-nullable `ActiveItemResolver` | PRD FR-007 |
| FR-008 | `SeedCommand`, `BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `GitContextCommand` use `ActiveItemResolver` for auto-fetch on cache miss | PRD FR-008 |
| FR-009 | `SyncWorkingSetAsync` skips fresh items (within `cacheStaleMinutes`) | PRD FR-009 |
| FR-010 | `SyncWorkingSetAsync` uses `ProtectedCacheWriter` for all saves | PRD FR-010 |
| FR-011 | `ActiveItemResolver` is non-nullable in all consuming commands | PRD FR-011 |
| FR-012 | `twig set` cache HIT does NOT trigger eviction | PRD FR-012 |
| FR-013 | `twig refresh` does NOT trigger eviction | PRD FR-013 |
| FR-014 | `twig refresh` MUST call `SyncWorkingSetAsync` instead of its current direct `SaveBatchAsync`/`SaveBatchProtectedAsync` path, so that the full working set (parents, children, sprint items) is synced consistently with read commands. Eviction MUST NOT occur. | New (gap review) |
| FR-015 | `SetCommand` MUST render the work item **inside** the `RenderWithSyncAsync` cached view so the sync indicator is visible adjacent to the rendered item, not on a blank line | New (gap review) |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-001 | Working set computation completes within 10ms for the local SQLite portion. Note: the first invocation per process includes one ADO REST round-trip (`IIterationService.GetCurrentIterationAsync()`) to resolve the current iteration path. Callers that already hold the iteration path (e.g., `WorkspaceCommand`) SHOULD pass it via the optional `IterationPath?` parameter to avoid this call. |
| NFR-002 | Eviction is a single SQL DELETE statement |
| NFR-003 | `SyncWorkingSetAsync` fetches stale items in parallel where possible |

### Constraints

| ID | Constraint |
|----|------------|
| CON-001 | All changes AOT-compatible (`PublishAot=true`, `PublishTrimmed=true`) |
| CON-002 | No new SQLite schema migrations |
| CON-003 | Domain layer (`Twig.Domain`) MUST NOT reference Infrastructure layer |

---

## Proposed Design

### Architecture Overview

The working set is a **domain concept** computed from existing cache state. It introduces two new types in `Twig.Domain.Services` and extends two existing services:

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Command Layer (Twig)                        │
│                                                                     │
│  SetCommand ──── WorkingSetService.ComputeAsync()                  │
│                  ├── EvictExceptAsync(workingSet.AllIds)            │
│                  └── SyncCoordinator.SyncWorkingSetAsync(ws)       │
│                                                                     │
│  StatusCommand ─┐                                                  │
│  TreeCommand   ─┤── Render from cache                              │
│  WorkspaceCmd  ─┤── WorkingSetService.ComputeAsync()               │
│  Nav Commands  ─┘── SyncCoordinator.SyncWorkingSetAsync(ws)        │
│                     └── Revise display in-place                    │
│                                                                     │
│  WorkspaceCmd ──── Shows dirty orphans section                     │
│                                                                     │
│  SeedCmd, BranchCmd, CommitCmd, PrCmd, StashCmd, GitContextCmd     │
│  └── ActiveItemResolver.GetActiveItemAsync() (non-nullable)        │
└─────────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────────┐
│                    Domain Services (Twig.Domain)                    │
│                                                                     │
│  NEW: WorkingSet (value object)                                    │
│  ├── ActiveItemId: int?                                            │
│  ├── ParentChainIds: IReadOnlyList<int>                            │
│  ├── ChildrenIds: IReadOnlyList<int>                               │
│  ├── SprintItemIds: IReadOnlyList<int>                             │
│  ├── SeedIds: IReadOnlyList<int>                                   │
│  ├── DirtyItemIds: IReadOnlySet<int>                               │
│  ├── IterationPath: IterationPath                                  │
│  └── AllIds: IReadOnlySet<int> (computed union)                    │
│                                                                     │
│  NEW: WorkingSetService                                            │
│  └── ComputeAsync(ct) → WorkingSet                                │
│      reads: IContextStore, IWorkItemRepository,                    │
│             IPendingChangeStore, IIterationService                  │
│                                                                     │
│  EXTENDED: SyncCoordinator                                         │
│  └── + SyncWorkingSetAsync(WorkingSet, ct) → SyncResult            │
│                                                                     │
│  EXTENDED: IWorkItemRepository                                     │
│  └── + EvictExceptAsync(IReadOnlySet<int> keepIds, ct)             │
└─────────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────────┐
│              Infrastructure (Twig.Infrastructure)                    │
│                                                                     │
│  EXTENDED: SqliteWorkItemRepository                                │
│  └── + EvictExceptAsync: DELETE FROM work_items WHERE id NOT IN    │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkingSet` (Value Object)

**File**: `src/Twig.Domain/Services/WorkingSet.cs`

A pure data container with no behavior beyond computing `AllIds`.

```csharp
public sealed record WorkingSet
{
    public int? ActiveItemId { get; init; }
    public IReadOnlyList<int> ParentChainIds { get; init; }
    public IReadOnlyList<int> ChildrenIds { get; init; }
    public IReadOnlyList<int> SprintItemIds { get; init; }
    public IReadOnlyList<int> SeedIds { get; init; }
    public IReadOnlySet<int> DirtyItemIds { get; init; }
    public IterationPath IterationPath { get; init; }

    // Computed: union of all ID sets
    public IReadOnlySet<int> AllIds { get; }
}
```

**Design rationale**: Using `record` for value semantics and equality. `AllIds` is computed once in the constructor (or via a lazy property) from the union of all ID collections. The `ActiveItemId` is included in `AllIds` when non-null.

#### 2. `WorkingSetService` (Domain Service)

**File**: `src/Twig.Domain/Services/WorkingSetService.cs`

Computes the working set from cache state. Most queries are local SQLite. The one exception is `IIterationService.GetCurrentIterationAsync()` which makes an ADO REST call to resolve the current sprint — callers that already hold the iteration path (e.g., `WorkspaceCommand`, `SetCommand` after context switch) pass it via the optional `iterationPath` parameter to avoid this network call.

```csharp
public sealed class WorkingSetService
{
    // Constructor: IContextStore, IWorkItemRepository, IPendingChangeStore,
    //              IIterationService, string? userDisplayName
    
    public async Task<WorkingSet> ComputeAsync(
        IterationPath? iterationPath = null, CancellationToken ct = default)
    {
        // 1. Read active ID from IContextStore
        // 2. Query parent chain from IWorkItemRepository
        // 3. Query children from IWorkItemRepository
        // 4. Resolve iteration: use iterationPath if provided,
        //    otherwise call IIterationService.GetCurrentIterationAsync() (ADO REST)
        // 5. Query sprint items via IWorkItemRepository.GetByIterationAsync
        //    (filtered by userDisplayName when configured)
        // 6. Query seeds from IWorkItemRepository
        // 7. Query dirty IDs via SyncGuard
        // 8. Return WorkingSet with all collections populated
    }
}
```

**Design rationale**: Follows the same primitive-injection pattern as `SyncCoordinator` (accepts `string? userDisplayName` instead of `TwigConfiguration` to avoid Domain → Infrastructure dependency — per RD-004). The optional `IterationPath?` parameter allows callers that already hold the iteration path to skip the ADO REST call (`IIterationService.GetCurrentIterationAsync()`). `AdoIterationService` does NOT cache the iteration response internally (verified — no caching field exists), so without this parameter, every `ComputeAsync()` call in a fresh process invocation makes one network round-trip. `WorkspaceCommand` already calls `iterationService.GetCurrentIterationAsync()` at line 71, and `SetCommand` can derive it from the fetched item's `IterationPath` — both should pass the value through.

#### 3. `SyncCoordinator.SyncWorkingSetAsync` (Extension)

**File**: `src/Twig.Domain/Services/SyncCoordinator.cs` (modified)

New method added to the existing `SyncCoordinator`:

```csharp
public async Task<SyncResult> SyncWorkingSetAsync(
    WorkingSet workingSet, CancellationToken ct = default)
{
    // 1. Filter AllIds to exclude seeds (negative IDs)
    // 2. For each ID, check LastSyncedAt against cacheStaleMinutes
    // 3. Fetch all stale items concurrently via Task.WhenAll + _adoService.FetchAsync
    // 4. Save the batch through _protectedCacheWriter.SaveBatchProtectedAsync(fetchedItems, ct)
    //    This computes protected IDs once internally — avoids N+1 SyncGuard queries
    // 5. Return Updated(count) or UpToDate or Failed
}
```

**Design rationale**: Individual `FetchAsync` calls (not batch WIQL) per RD-006 — items already identified by ID. Concurrent execution via `Task.WhenAll` for stale items. After all fetches complete, the batch is saved through `ProtectedCacheWriter.SaveBatchProtectedAsync(items, ct)` which computes protected IDs once via `SyncGuard.GetProtectedItemIdsAsync` and uses `SaveBatchAsync` for efficient persistence. **Critical**: Do NOT use the per-item `SaveProtectedAsync(WorkItem, CancellationToken)` overload inside `Task.WhenAll` — it calls `SyncGuard.GetProtectedItemIdsAsync()` on each invocation (2 SQLite queries per call), producing 40-100 redundant queries for 20-50 concurrent stale items. Working set typically contains 20-50 items; most will be fresh after initial sync.

#### 4. `IWorkItemRepository.EvictExceptAsync` (Extension)

**Interface**: `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` (modified)  
**Implementation**: `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` (modified)

```csharp
// Interface
Task EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken ct = default);

// Implementation — single SQL DELETE
// DELETE FROM work_items WHERE id NOT IN (@id0, @id1, @id2, ...)
```

**Design rationale**: Single DELETE per NFR-002. The keep set includes `WorkingSet.AllIds` which already contains dirty IDs (computed by `SyncGuard` during `WorkingSetService.ComputeAsync`). SQLite parameter limit is 999 per statement; working sets are typically <100 items, well within this limit. For safety, if the keep set exceeds 900 IDs, the implementation should use a temporary table approach.

### Data Flow

#### `twig set 42` (Cache Miss — Context Switch)

```
1. ActiveItemResolver.ResolveByIdAsync(42)
   └── cache miss → FetchAsync(42) → SaveAsync → FetchedFromAdo(item)

2. Set active context, render item

3. WorkingSetService.ComputeAsync()
   ├── active item: 42
   ├── parent chain: GetParentChainAsync(42) → [10, 5]
   ├── children: GetChildrenAsync(42) → [43, 44, 45]
   ├── sprint items: GetByIterationAsync(item.IterationPath) → [42, 43, 60, 61]
   ├── seeds: GetSeedsAsync() → [-1, -2]
   └── dirty: SyncGuard.GetProtectedItemIdsAsync() → {78, 90}
   → WorkingSet.AllIds = {5, 10, 42, 43, 44, 45, 60, 61, -1, -2, 78, 90}

4. workItemRepo.EvictExceptAsync({5, 10, 42, 43, 44, 45, 60, 61, -1, -2, 78, 90})
   └── DELETE old sprint items, old children, stale context

5. SyncCoordinator.SyncWorkingSetAsync(workingSet)
   └── Fetch stale items → ProtectedCacheWriter

6. Render sync indicator
```

#### `twig set 42` (Cache Hit — No Eviction)

```
1. ActiveItemResolver.ResolveByIdAsync(42)
   └── cache hit → Found(item)

2. Set active context, render item

3. WorkingSetService.ComputeAsync(item.IterationPath)
   → workingSet (same computation as cache-miss path)

4. SyncCoordinator.SyncWorkingSetAsync(workingSet)
   └── [No eviction — FR-012. Sync proceeds normally to refresh stale items.]

5. Render sync indicator
```

> **Note on pattern-based `twig set` (non-numeric)**: Pattern resolution always uses `FindByPatternAsync` which only searches the local cache. This path can never return `FetchedFromAdo`, so eviction is impossible for pattern-based `twig set` calls. This is the correct behavior per FR-012 — the item is already in cache (hit), so no context switch eviction occurs.

#### `twig status` (Steady-State Read)

```
1. ActiveItemResolver.GetActiveItemAsync() → Found(item)
2. Render item from cache immediately (via IAsyncRenderer)
3. WorkingSetService.ComputeAsync() → workingSet
4. SyncCoordinator.SyncWorkingSetAsync(workingSet)
5. If Updated → revise display in-place via RenderWithSyncAsync
```

#### `twig ws` (Workspace View with Dirty Orphans)

```
1. Render sprint items + seeds + context from cache
2. WorkingSetService.ComputeAsync() → workingSet
3. SyncCoordinator.SyncWorkingSetAsync(workingSet)
4. If Updated → revise table in-place
5. Compute dirty orphans:
   dirtyOrphans = workingSet.DirtyItemIds - sprintItemIds - seedIds
6. Render "Unsaved changes" section with dirty orphan items
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | `WorkingSet` is a `record` not a `class` | Value semantics; immutable after construction; structural equality for testing |
| DD-02 | `WorkingSetService` accepts `string? userDisplayName` primitive | Same pattern as `SyncCoordinator` (DD-13 from cache-first plan). Avoids Domain → Infrastructure dependency per CON-003 |
| DD-03 | `SyncWorkingSetAsync` uses individual `FetchAsync` not batch WIQL | Items already identified by ID. Individual fetches use `ProtectedCacheWriter` per-item. WIQL would require re-implementing protected save logic (per RD-006) |
| DD-04 | `EvictExceptAsync` uses parameterized `NOT IN` clause | Single SQL statement per NFR-002. Keep set typically <100 items. Falls back to temp table if >900 IDs (SQLite parameter limit safety) |
| DD-05 | Eviction is gated on `ActiveItemResult.FetchedFromAdo` | Cache hit means item is already in working set. Frequent switches between cached items should not trigger eviction (per RD-002) |
| DD-06 | `WorkingSetService.ComputeAsync` accepts optional `IterationPath?` parameter; falls back to `IIterationService.GetCurrentIterationAsync()` when null | `GetCurrentIterationAsync()` is an ADO REST call (verified — `AdoIterationService` has no internal cache for iteration path). Callers that already hold the iteration path (e.g., `WorkspaceCommand` at line 71, `SetCommand` via `item.IterationPath`) pass it to avoid the network round-trip. This eliminates the contradiction with NFR-001 for the common case. |
| DD-07 | `SetCommand` replaces `SyncChildrenAsync` with `SyncWorkingSetAsync` | Working set sync is a superset — it syncs parents, children, and sprint items. No need for a separate children-only sync |
| DD-08 | Dirty orphans in `twig ws` are computed as `DirtyItemIds - SprintItemIds - SeedIds` | Dirty items in the sprint or seed set are already visible. Only items outside these sets are "orphans" needing special attention |
| DD-09 | Sprint items in the working set come from cache only — no ADO fetch during `ComputeAsync` | Sprint items are populated by `twig refresh` (WIQL query → cache). `ComputeAsync` reads whatever is currently cached via `GetByIterationAsync`. If the iteration has rolled over since the last refresh, the working set will contain stale-sprint items (or none). This is a known limitation — comprehensive sprint lifecycle management (auto-population on context switch, iteration drift detection, sprint-aware cache warming) is deferred to a dedicated sprint feature redesign. The working set scope is deliberately limited to "what's in cache" to avoid pulling ADO fetches into a method that must stay fast (NFR-001). |
| DD-10 | `SetCommand` renders the work item inside `buildCachedView` (not via `Console.WriteLine` before `Live()`) | The v0.4.0 implementation prints the item via `Console.WriteLine` before calling `RenderWithSyncAsync` with an empty `Text("")` cached view. This causes the sync indicator to appear on a blank line, making it invisible. The fix renders the formatted item as the cached view so the indicator appears directly below it within the `Live()` context. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Persist working set in a `working_set_items` table | Avoids recomputation on every read command | Two sources of truth; sync complexity; cache IS the working set after eviction | Rejected — RD-001 |
| Evict on every `twig set` (not just cache miss) | Simpler logic | Disruptive when switching between cached items; unnecessary DELETE + re-fetch | Rejected — RD-002 |
| Batch WIQL query for `SyncWorkingSetAsync` | Single ADO call | Can't use `ProtectedCacheWriter` per-item; WIQL complexity limits | Rejected — RD-006 |
| Keep `ActiveItemResolver` nullable with fallback | No test changes | Dead code paths; confusing conditionals; inconsistent behavior | Rejected — RD-007 |
| TTL-based eviction (evict items older than N days) | No context-switch dependency | Doesn't align with user intent; keeps irrelevant items | Rejected — context-aware eviction is more precise |
| Fetch sprint items during `ComputeAsync` on cache miss | Working set always has current sprint | Adds ADO network call to `ComputeAsync`, violating NFR-001; duplicates `twig refresh` logic; iteration drift is a broader problem | Deferred — sprint lifecycle redesign (DD-09) |

---

## Dependencies

### Internal Dependencies

| ID | Dependency | Status | Impact |
|----|-----------|--------|--------|
| DEP-001 | `SyncGuard` (static class) | Shipped (v0.4.0) | Used for protected ID computation during eviction |
| DEP-002 | `ProtectedCacheWriter` | Shipped (v0.4.0) | Used by `SyncWorkingSetAsync` for safe batch saves |
| DEP-003 | `ActiveItemResolver` | Shipped (v0.4.0) | Made non-nullable in all consuming commands |
| DEP-004 | `SyncCoordinator` | Shipped (v0.4.0) | Extended with `SyncWorkingSetAsync` |
| DEP-005 | `IWorkItemRepository` | Shipped (v0.4.0) | Extended with `EvictExceptAsync` |
| DEP-006 | `IAsyncRenderer.RenderWithSyncAsync` | Shipped (v0.4.0) | Adopted by `StatusCommand`, `TreeCommand` for sync-after-render |

### External Dependencies

None. No new NuGet packages required.

### Sequencing Constraints

- EPIC-001 (domain model) must complete before EPIC-002 (sync coordinator extension)
- EPIC-001 + EPIC-002 must complete before EPIC-003 (SetCommand eviction)
- EPIC-001 + EPIC-002 must complete before EPIC-004 (read command sync)
- EPIC-005 (ActiveItemResolver adoption) depends on EPIC-003 and EPIC-004 completing first — all three modify `CommandRegistrationModule.cs` and running them concurrently would produce merge conflicts

---

## Impact Analysis

### Components Affected

| Component | Type of Change | Risk |
|-----------|---------------|------|
| `Twig.Domain/Services/` | New files (WorkingSet, WorkingSetService) | Low — additive |
| `Twig.Domain/Interfaces/IWorkItemRepository.cs` | Interface extension (new method) | Medium — all implementors must be updated |
| `Twig.Domain/Services/SyncCoordinator.cs` | New method | Low — additive |
| `Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | New method implementation | Low — additive |
| `Twig/Commands/SetCommand.cs` | Behavior change (eviction + working set sync) | Medium — core command |
| `Twig/Commands/StatusCommand.cs` | Behavior change (working set sync) + constructor change | Medium |
| `Twig/Commands/TreeCommand.cs` | Behavior change (working set sync) + constructor change | Medium |
| `Twig/Commands/NavigationCommands.cs` | Constructor change (non-nullable resolver) | Low |
| `Twig/Commands/WorkspaceCommand.cs` | Behavior change (dirty orphans) + constructor change | Medium |
| `Twig/Commands/RefreshCommand.cs` | Behavior change (working set sync after WIQL fetch) | Low — additive |
| `Twig/Commands/{Seed,Branch,Commit,Pr,Stash,GitContext}Command.cs` | Constructor change (add resolver) | Low — mechanical |
| `Twig/DependencyInjection/CommandServiceModule.cs` | New registration (WorkingSetService) | Low |
| `Twig/DependencyInjection/CommandRegistrationModule.cs` | Updated registrations | Low |
| Test files (multiple) | Constructor updates, new test cases | Low — mechanical |

### Backward Compatibility

- **Cache forward-compatible**: Eviction removes rows but doesn't change schema. An older binary reading a post-eviction cache simply has fewer cached items and re-fetches as needed.
- **No config changes**: No new configuration keys required.
- **JSON output unchanged**: No sync indicators leak into structured output (TEST-011).

### Performance Implications

- **Working set computation**: ~10ms for local SQLite queries when `IterationPath` is provided by caller. First call without cached iteration adds one ADO REST round-trip (~100-300ms). Subsequent calls in the same command context reuse the caller-provided path (per revised NFR-001, DD-06).
- **Eviction**: Single DELETE statement (~1ms for typical working sets)
- **Working set sync**: 0–2s depending on stale item count and network latency. Most items will be fresh after initial sync. Non-blocking in TTY mode (progressive rendering).

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Eviction deletes a dirty item | Low | High | Dirty items are always in the keep set — `WorkingSet.DirtyItemIds` is computed from `SyncGuard` and included in `AllIds`. Unit tests verify preservation. |
| Eviction fires on cache hit | Low | Medium | Gated on `ActiveItemResult.FetchedFromAdo` only. Explicit unit test for `Found` → no eviction. |
| `SyncWorkingSetAsync` overwhelms ADO with parallel fetches | Low | Medium | Working set typically 20-50 items. Most will be fresh (skip). Batch via `Task.WhenAll` with natural concurrency limits. |
| `IIterationService.GetCurrentIterationAsync()` adds latency to `ComputeAsync` | Medium | Low | Mitigated by accepting optional `IterationPath?` parameter. `WorkspaceCommand` (line 71), `SetCommand` (via `item.IterationPath`), and read commands (via `WorkspaceCommand` pipeline) already hold the iteration path. Only standalone calls without context trigger the ADO round-trip. |
| SQLite parameter limit exceeded in `EvictExceptAsync` | Very Low | Medium | Working sets typically <100 items. Implementation falls back to temp table approach if >900 IDs. |
| Sprint iteration rolls over between commands | Medium | Low | After an iteration boundary, `ComputeAsync` may return stale sprint items (from the old iteration still in cache) or no sprint items (if cache was evicted). The user must run `twig refresh` to populate the new sprint. This is a known limitation — deferred to sprint lifecycle redesign (DD-09). |
| Sync indicator invisible in `SetCommand` | High | Low | v0.4.0 bug: `Console.WriteLine` prints item outside `Live()` context, then `RenderWithSyncAsync` gets empty cached view. Fixed in this plan by rendering the item inside `buildCachedView` (DD-10, FR-015). |
| Test update burden for non-nullable `ActiveItemResolver` | Medium | Low | Mechanical change — add mock to constructor. Can be batched. |

---

## Open Questions

1. **[Low]** Should `SyncWorkingSetAsync` use a configurable concurrency limit (e.g., `SemaphoreSlim(5)`) for parallel fetches, or rely on the natural `Task.WhenAll` concurrency? The working set is typically 20-50 items, and ADO handles concurrent requests well. A limit could be added later if needed.

2. **[Low]** Should the "Unsaved changes" section in `twig ws` show a summary count or full item details? The PRD specifies a "dedicated section" — the implementation will show item IDs, titles, and a hint to run `twig save`. Full field-level diff display can be deferred.

3. ~~**[Low]** Should `WorkingSetService.ComputeAsync` accept an optional `IterationPath` parameter?~~ **RESOLVED in v1.1**: Yes — `ComputeAsync` now accepts `IterationPath? iterationPath = null`. This was elevated from optional optimization to required design change after technical review confirmed `AdoIterationService.GetCurrentIterationAsync()` has no internal caching, making NFR-001's "all local SQLite" claim incorrect without it.

4. **[Low]** Should EPIC-005 commands (`BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `GitContextCommand`) include explicit test cases for the `Unreachable` result path from `ActiveItemResolver`? Currently these commands fail with a generic "not found in cache" error; after adopting `ActiveItemResolver`, the `Unreachable` variant provides a specific error reason. Test coverage for this path prevents regression if error messaging is refined later.

5. ~~**[Medium]** Should `ComputeAsync` fetch sprint items from ADO when the cache has none for the current iteration?~~ **RESOLVED in v1.2**: No — deferred to sprint lifecycle redesign. `ComputeAsync` reads only from cache (DD-09). Users run `twig refresh` to populate sprint items.

---

## Implementation Phases

### Phase 1: Domain Model & Infrastructure (EPIC-001)
**Exit Criteria**: `WorkingSet` value object and `WorkingSetService` exist with unit tests. `EvictExceptAsync` is implemented and tested. No command changes yet.
**Status**: DONE

### Phase 2: Sync Coordinator Extension (EPIC-002)
**Exit Criteria**: `SyncWorkingSetAsync` exists on `SyncCoordinator` with unit tests covering fresh/stale/mixed/failure scenarios.

### Phase 3: SetCommand Eviction (EPIC-003)
**Exit Criteria**: `twig set` on cache miss triggers eviction + working set sync. Cache hit does not evict. Integration tests pass.

### Phase 4: Read Command Sync & Non-Nullable Resolver (EPIC-004)
**Exit Criteria**: `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand` sync working set after render. `ActiveItemResolver` is non-nullable in all 4 commands. Dirty orphan section visible in `twig ws`. All existing tests updated.

### Phase 5: ActiveItemResolver Adoption (EPIC-005)
**Exit Criteria**: `SeedCommand`, `BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `GitContextCommand` use `ActiveItemResolver` for auto-fetch. DI registrations updated. All tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/WorkingSet.cs` | Value object — computed working set with `AllIds` |
| `src/Twig.Domain/Services/WorkingSetService.cs` | Domain service — computes working set from cache state |
| `tests/Twig.Domain.Tests/Services/WorkingSetServiceTests.cs` | Unit tests for working set computation |
| `tests/Twig.Domain.Tests/Services/WorkingSetAllIdsTests.cs` | Unit tests for `WorkingSet.AllIds` computation |
| `tests/Twig.Cli.Tests/Commands/WorkingSetCommandTests.cs` | Integration tests for SetCommand eviction behavior |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Add `EvictExceptAsync` method |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Implement `EvictExceptAsync` |
| `src/Twig.Domain/Services/SyncCoordinator.cs` | Add `SyncWorkingSetAsync` method |
| `src/Twig/Commands/SetCommand.cs` | Add eviction on cache miss; replace `SyncChildrenAsync` with `SyncWorkingSetAsync`; inject `WorkingSetService` |
| `src/Twig/Commands/StatusCommand.cs` | Make `ActiveItemResolver` required; remove fallback path; add working set sync |
| `src/Twig/Commands/TreeCommand.cs` | Make `ActiveItemResolver` required; remove fallback path; add working set sync |
| `src/Twig/Commands/NavigationCommands.cs` | Make `ActiveItemResolver` required; remove fallback path |
| `src/Twig/Commands/WorkspaceCommand.cs` | Make `ActiveItemResolver` required; remove fallback path; add dirty orphan section |
| `src/Twig/Commands/RefreshCommand.cs` | Add `WorkingSetService` + `SyncCoordinator`; call `SyncWorkingSetAsync` after WIQL fetch |
| `src/Twig/Commands/SeedCommand.cs` | Add `ActiveItemResolver` for auto-fetch on cache miss |
| `src/Twig/Commands/BranchCommand.cs` | Replace raw cache lookup with `ActiveItemResolver` |
| `src/Twig/Commands/CommitCommand.cs` | Replace raw cache lookup with `ActiveItemResolver` |
| `src/Twig/Commands/PrCommand.cs` | Replace raw cache lookup with `ActiveItemResolver` |
| `src/Twig/Commands/StashCommand.cs` | Replace raw cache lookup with `ActiveItemResolver` |
| `src/Twig/Commands/GitContextCommand.cs` | Replace raw cache lookup with `ActiveItemResolver` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `WorkingSetService` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Update DI wiring for all modified commands |
| `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | Add tests for `SyncWorkingSetAsync` |
| `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Add eviction tests |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Update for non-nullable resolver |
| `tests/Twig.Cli.Tests/Commands/StatusCommandAsyncTests.cs` | Update for non-nullable resolver |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Update for non-nullable resolver |
| `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | Update for non-nullable resolver |
| `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | Update for non-nullable resolver |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | Update for non-nullable resolver; add dirty orphan tests |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommandAsyncTests.cs` | Update for non-nullable resolver |
| `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | Add resolver mock |
| `tests/Twig.Cli.Tests/Commands/BranchCommandTests.cs` | Add resolver mock |
| `tests/Twig.Cli.Tests/Commands/CommitCommandTests.cs` | Add resolver mock |
| `tests/Twig.Cli.Tests/Commands/PrCommandTests.cs` | Add resolver mock |
| `tests/Twig.Cli.Tests/Commands/StashCommandTests.cs` | Add resolver mock |
| `tests/Twig.Cli.Tests/Commands/GitContextCommandTests.cs` | Add resolver mock |
| `tests/Twig.Cli.Tests/Commands/CacheFirstReadCommandTests.cs` | Update for non-nullable resolver |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | |

---

## Implementation Plan

### EPIC-001: Working Set Domain Model and Eviction

**Goal**: Introduce `WorkingSet` value object, `WorkingSetService`, and `IWorkItemRepository.EvictExceptAsync`. Pure domain/infrastructure additions — no command changes.

**Prerequisites**: None (builds on shipped v0.4.0 infrastructure)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| WS-001 | IMPL | Create `WorkingSet` record with `ActiveItemId`, `ParentChainIds`, `ChildrenIds`, `SprintItemIds`, `SeedIds`, `DirtyItemIds`, `IterationPath`, and computed `AllIds` (union of all ID sets). Computed `AllIds` property iterates all ID collections fresh on each access to avoid stale results after `with` expressions. | `src/Twig.Domain/Services/WorkingSet.cs` | DONE |
| WS-002 | IMPL | Create `WorkingSetService` with constructor: `IContextStore`, `IWorkItemRepository`, `IPendingChangeStore`, `IIterationService`, `string? userDisplayName`. Method `ComputeAsync(IterationPath? iterationPath = null, CancellationToken ct = default)` → `WorkingSet`: if `iterationPath` is provided, uses it directly (no network call); otherwise calls `IIterationService.GetCurrentIterationAsync()` (ADO REST). Reads active ID, queries parent chain/children/sprint items/seeds/dirty IDs from cache. Sprint items filtered by `userDisplayName` when non-null. | `src/Twig.Domain/Services/WorkingSetService.cs` | DONE |
| WS-003 | IMPL | Add `EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken)` to `IWorkItemRepository` interface. | `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | DONE |
| WS-004 | IMPL | Implement `EvictExceptAsync` in `SqliteWorkItemRepository` — single parameterized `DELETE FROM work_items WHERE id NOT IN (...)`. Handle empty keep set (delete all) and large keep sets (>900 → temp table). | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | DONE |
| WS-005 | TEST | Unit tests for `WorkingSetService.ComputeAsync`: correct membership for each category, empty cache, no active item, missing parent chain, no sprint items, assignee filtering, IterationPath passthrough (verify `GetCurrentIterationAsync` NOT called when `iterationPath` is provided). | `tests/Twig.Domain.Tests/Services/WorkingSetServiceTests.cs` | DONE |
| WS-006 | TEST | Unit tests for `WorkingSet.AllIds` computation: active item inclusion, empty set, union of all categories, deduplication of overlapping IDs, null active item exclusion, `with`-expression freshness. | `tests/Twig.Domain.Tests/Services/WorkingSetAllIdsTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `WorkingSet.AllIds` contains the union of all ID sets
- [x] `WorkingSetService.ComputeAsync` returns correct IDs for all membership categories
- [x] `EvictExceptAsync` deletes only items NOT in the keep set
- [x] All new unit tests pass
- [x] Build succeeds (`dotnet build`)

---

### EPIC-002: SyncCoordinator Working Set Sync

**Goal**: Add `SyncWorkingSetAsync` to `SyncCoordinator` for batch-syncing stale items within the working set.

**Prerequisites**: EPIC-001 (depends on `WorkingSet` type)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| WS-007 | IMPL | Add `SyncWorkingSetAsync(WorkingSet, CancellationToken)` to `SyncCoordinator`. Iterates `workingSet.AllIds` excluding seeds (negative IDs), checks per-item `LastSyncedAt` against `cacheStaleMinutes`, identifies stale items. Fetches all stale items concurrently via `Task.WhenAll` calling `_adoService.FetchAsync`, then saves the batch through `_protectedCacheWriter.SaveBatchProtectedAsync(fetchedItems, ct)` — this overload computes protected IDs once internally (1 call to `SyncGuard.GetProtectedItemIdsAsync`) and uses `SaveBatchAsync` for efficient persistence. **Do NOT use the per-item `SaveProtectedAsync(item, ct)` overload inside `Task.WhenAll`** — it calls `SyncGuard.GetProtectedItemIdsAsync()` on each invocation (2 SQLite queries per item), producing 40-100 redundant queries for 20-50 stale items. Returns `SyncResult.Updated(n)`, `SyncResult.UpToDate`, or `SyncResult.Failed`. | `src/Twig.Domain/Services/SyncCoordinator.cs` | TO DO |
| WS-008 | TEST | Unit tests for `SyncWorkingSetAsync`: all fresh → UpToDate, mix stale/fresh → Updated(staleCount), all stale → Updated(allCount), network failure → Failed, seed IDs (negative) skipped, dirty items skipped by ProtectedCacheWriter, empty working set → UpToDate. | `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `SyncWorkingSetAsync` skips fresh items and fetches only stale ones
- [ ] Seed IDs (negative) are excluded from sync
- [ ] Fetched items are saved via `SaveBatchProtectedAsync` (batch overload), not per-item `SaveProtectedAsync`
- [ ] `SyncGuard.GetProtectedItemIdsAsync` is called at most once per `SyncWorkingSetAsync` invocation
- [ ] Network failures return `SyncResult.Failed` without throwing
- [ ] All tests pass

---

### EPIC-003: SetCommand Eviction on Context Switch

**Goal**: `twig set` triggers working set computation and cache eviction when the target item was fetched from ADO (cache miss). No eviction on cache hit.

**Prerequisites**: EPIC-001, EPIC-002

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| WS-009 | IMPL | Inject `WorkingSetService` into `SetCommand`. Call `WorkingSetService.ComputeAsync(item.IterationPath)` in **both** the `FetchedFromAdo` and `Found` paths — the working set is needed for sync in both cases. On `FetchedFromAdo` only: call `workItemRepo.EvictExceptAsync(workingSet.AllIds)` before sync. On `Found`: skip eviction (FR-012), proceed directly to sync. Pass `item.IterationPath` to avoid the redundant `GetCurrentIterationAsync()` ADO call (per DD-06). | `src/Twig/Commands/SetCommand.cs` | TO DO |
| WS-010 | IMPL | Replace `SyncCoordinator.SyncChildrenAsync` in `SetCommand` with `SyncCoordinator.SyncWorkingSetAsync(workingSet)` in **both** the `FetchedFromAdo` and `Found` paths — syncs the full working set (parents, children, sprint items) regardless of whether eviction occurred. The working set is computed in WS-009 for both paths. | `src/Twig/Commands/SetCommand.cs` | TO DO |
| WS-010a | IMPL | **Fix sync indicator visibility (FR-015, DD-10)**: In `SetCommand`'s TTY path, move the work item rendering from `Console.WriteLine` (before `RenderWithSyncAsync`) into the `buildCachedView` delegate so the formatted item is the cached view passed to `Live()`. This ensures the sync indicator ("⟳ syncing...") appears directly below the rendered item within the `Live()` context, not on a standalone blank line. The non-TTY path continues to use `Console.WriteLine` (no `Live()` context). | `src/Twig/Commands/SetCommand.cs` | TO DO |
| WS-011 | IMPL | Register `WorkingSetService` in `CommandServiceModule.cs` as singleton with DI factory lambda resolving `userDisplayName` from `TwigConfiguration.User.DisplayName`. | `src/Twig/DependencyInjection/CommandServiceModule.cs` | TO DO |
| WS-012 | IMPL | Update `SetCommand` DI registration in `CommandRegistrationModule.cs` to inject `WorkingSetService`. | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | TO DO |
| WS-013 | TEST | Tests: (a) cache miss → eviction fires, non-working-set items deleted; (b) cache hit → no eviction but sync still fires; (c) dirty items survive eviction; (d) working set items survive eviction; (e) `SyncWorkingSetAsync` called instead of `SyncChildrenAsync` in both paths; (f) `ComputeAsync` receives `item.IterationPath` (not null); (g) TTY path renders work item inside `buildCachedView` (not empty `Text("")`), sync indicator visible below item. | `tests/Twig.Cli.Tests/Commands/WorkingSetCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig set` cache miss triggers eviction — only working set + dirty items remain
- [ ] `twig set` cache hit does NOT trigger eviction but DOES call `ComputeAsync` + `SyncWorkingSetAsync`
- [ ] Dirty items survive eviction
- [ ] `SyncWorkingSetAsync` replaces `SyncChildrenAsync` in both paths
- [ ] `ComputeAsync` receives `item.IterationPath` to avoid redundant ADO call
- [ ] Sync indicator visible below rendered work item in TTY mode (FR-015)
- [ ] All tests pass, build succeeds

---

### EPIC-004: Read Command Sync + ActiveItemResolver Required

**Goal**: `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand` get working set sync after cached render. `ActiveItemResolver` becomes non-nullable. Dirty orphans visible in `twig ws`.

**Prerequisites**: EPIC-001, EPIC-002

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| WS-014 | IMPL | **StatusCommand**: Make `ActiveItemResolver` required (non-nullable). Remove fallback `workItemRepo.GetByIdAsync` path (lines 76-85). Inject `WorkingSetService` and `SyncCoordinator`. After rendering cached status, call `SyncWorkingSetAsync` via `RenderWithSyncAsync` — revise display if item changed. | `src/Twig/Commands/StatusCommand.cs` | TO DO |
| WS-015 | IMPL | **TreeCommand**: Make `ActiveItemResolver` required (non-nullable). Remove fallback path (lines 59-67). Inject `WorkingSetService` and `SyncCoordinator`. After rendering cached tree, call `SyncWorkingSetAsync` — revise tree if children/parent changed. | `src/Twig/Commands/TreeCommand.cs` | TO DO |
| WS-016 | IMPL | **NavigationCommands**: Make `ActiveItemResolver` required (non-nullable). Remove fallback paths in `UpAsync` (lines 53-61) and `DownAsync` (lines 112-120). **Note**: No `WorkingSetService` or `SyncCoordinator` injection needed — both `UpAsync` and `DownAsync` delegate to `SetCommand.ExecuteAsync()` for the actual context switch, which handles working set computation, eviction, and sync. NavigationCommands only need `ActiveItemResolver` for initial item resolution and parent/child lookup before delegation. | `src/Twig/Commands/NavigationCommands.cs` | TO DO |
| WS-017 | IMPL | **WorkspaceCommand**: Make `ActiveItemResolver` required (non-nullable). Remove fallback paths (lines 53-66 in async path, lines 156-169 in sync path). Inject `WorkingSetService`. Add "Unsaved changes" section: after rendering sprint items/seeds, call `WorkingSetService.ComputeAsync(iteration)` (pass the `iteration` variable already resolved at line 71) to obtain `workingSet.DirtyItemIds`, compute dirty orphans as `DirtyItemIds - sprintItemIds - seedIds`, fetch those items from cache via `workItemRepo.GetByIdAsync`, render as separate group with hint "Run 'twig save' to push these changes." **Note**: Do NOT call `SyncGuard.GetProtectedItemIdsAsync()` directly — `WorkspaceCommand`'s constructor does not include `IPendingChangeStore` (required by `SyncGuard`). Using `WorkingSetService` is consistent with WS-018's injection of `WorkingSetService` and avoids adding `IPendingChangeStore` to the constructor. | `src/Twig/Commands/WorkspaceCommand.cs` | TO DO |
| WS-018 | IMPL | Update `CommandRegistrationModule.cs` DI wiring for `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand` with non-nullable `ActiveItemResolver`, `WorkingSetService`, `SyncCoordinator` where needed. | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | TO DO |
| WS-019 | TEST | Update all existing tests for `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand` — supply non-nullable `ActiveItemResolver` mock. Verify sync-after-render behavior. | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/StatusCommandAsyncTests.cs`, `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs`, `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/CacheFirstReadCommandTests.cs` | TO DO |
| WS-020 | TEST | New tests for `WorkspaceCommand` dirty orphan display: (a) dirty orphans shown when present; (b) no orphan section when all dirty items are in sprint/seed scope; (c) orphan section includes hint text. | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/WorkspaceCommandAsyncTests.cs` | TO DO |
| WS-021 | TEST | Verify JSON/minimal output parity — no sync indicators leak into structured output. | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | TO DO |
| WS-021a | IMPL | **RefreshCommand**: Replace the current direct `adoService.QueryByWiqlAsync` → `SaveBatchAsync`/`SaveBatchProtectedAsync` path with: (1) keep the existing WIQL fetch for sprint items (this is the authoritative ADO query that populates the cache), (2) after saving the fetched items, call `WorkingSetService.ComputeAsync(iteration)` then `SyncCoordinator.SyncWorkingSetAsync(workingSet)` to sync stale parents/children/seeds in the working set. Do NOT trigger eviction (FR-013). Inject `WorkingSetService` and `SyncCoordinator`. | `src/Twig/Commands/RefreshCommand.cs` | TO DO |
| WS-021b | TEST | Tests for RefreshCommand: (a) `SyncWorkingSetAsync` called after sprint item save; (b) no eviction triggered; (c) working set sync fetches stale parents/children; (d) `--force` still overrides protected writes for sprint items but working set sync still respects protection. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `StatusCommand` renders from cache then revises after sync
- [ ] `TreeCommand` renders cached tree then revises after sync
- [ ] `NavigationCommands` work with non-nullable resolver
- [ ] `WorkspaceCommand` shows "Unsaved changes" section for dirty orphans
- [ ] No `ActiveItemResolver?` nullable parameters remain in these 4 commands
- [ ] JSON output is identical before and after for all modified commands
- [ ] `RefreshCommand` calls `SyncWorkingSetAsync` after its WIQL fetch
- [ ] `RefreshCommand` does NOT trigger eviction (FR-013)
- [ ] All tests pass, build succeeds

---

### EPIC-005: ActiveItemResolver Adoption in Remaining Commands

**Goal**: Commands that resolve work item context using raw `contextStore` + `workItemRepo.GetByIdAsync` switch to `ActiveItemResolver` for auto-fetch on cache miss.

**Prerequisites**: EPIC-003 and EPIC-004 must complete first (all three EPICs modify `CommandRegistrationModule.cs` — WS-012, WS-018, WS-028 — and concurrent changes would produce merge conflicts)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| WS-022 | IMPL | **SeedCommand**: Replace `contextStore.GetActiveWorkItemIdAsync()` + `workItemRepo.GetByIdAsync()` (lines 31-37) with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | `src/Twig/Commands/SeedCommand.cs` | TO DO |
| WS-023 | IMPL | **BranchCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` (line 44) with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. Remove `IContextStore` if no longer needed directly. | `src/Twig/Commands/BranchCommand.cs` | TO DO |
| WS-024 | IMPL | **CommitCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` (line 41) with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | `src/Twig/Commands/CommitCommand.cs` | TO DO |
| WS-025 | IMPL | **PrCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` (line 41) with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | `src/Twig/Commands/PrCommand.cs` | TO DO |
| WS-026 | IMPL | **StashCommand**: Replace `contextStore.GetActiveWorkItemIdAsync()` + `workItemRepo.GetByIdAsync()` (lines 52-57) with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | `src/Twig/Commands/StashCommand.cs` | TO DO |
| WS-027 | IMPL | **GitContextCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` (line 33) with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | `src/Twig/Commands/GitContextCommand.cs` | TO DO |
| WS-028 | IMPL | Update DI registrations in `CommandRegistrationModule.cs` for all 6 commands — inject `ActiveItemResolver`. | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | TO DO |
| WS-029 | TEST | Update tests for all 6 commands — supply `ActiveItemResolver` mock, verify auto-fetch behavior on cache miss, verify `NoContext` handling, **and verify `Unreachable` result handling** (these commands currently fail with generic "not found in cache" messages; after adopting `ActiveItemResolver`, the `Unreachable` variant should produce a specific error with the reason from the exception). | `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/BranchCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/CommitCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/PrCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/StashCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/GitContextCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] All 6 commands use `ActiveItemResolver.GetActiveItemAsync()` instead of raw cache lookup
- [ ] Auto-fetch on cache miss works for all 6 commands
- [ ] `Unreachable` result produces a specific error message with the failure reason
- [ ] DI registrations inject `ActiveItemResolver` for all 6 commands
- [ ] All existing tests updated with resolver mocks
- [ ] All tests pass, build succeeds

---

## References

- **PRD**: [`docs/projects/twig-working-set.prd.md`](twig-working-set.prd.md)
- **v0.4.0 Cache-First Architecture**: Shipped infrastructure including `ActiveItemResolver`, `SyncCoordinator`, `ProtectedCacheWriter`, `SyncGuard`, `IAsyncRenderer.RenderWithSyncAsync`
- **RFC 2119**: Requirement level keywords (MUST, SHOULD, MAY)
- **SQLite Parameter Limits**: Maximum 999 parameters per statement (SQLITE_MAX_VARIABLE_NUMBER default)
