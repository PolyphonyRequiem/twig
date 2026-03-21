---
goal: "Working Set — context-aware cache lifecycle with automatic sync and eviction"
version: 1.0
date_created: 2026-03-17
owner: Daniel Green
tags: architecture, performance, ux, cache
---

# Introduction

Twig's cache-first architecture (v0.4.0) introduced shared sync services and protected writes, but commands outside `SetCommand` still render stale cached data without refreshing, and the cache accumulates items indefinitely across context switches. This initiative introduces a **Working Set** — a computed, context-aware scope that defines which work items Twig considers "active." The working set governs three unified behaviors: **(1)** read commands automatically sync the working set after rendering cached data, **(2)** context switches evict items outside the new working set (preserving dirty items), and **(3)** `twig ws` displays the entire working set including dirty orphans from prior contexts.

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: This document uses standardized prefixes for traceability — `FR-` (functional requirements), `NFR-` (non-functional requirements), `FM-` (failure modes), `AC-` (acceptance criteria), and `RD-` (resolved decisions).

## 1. Goals and Non-Goals

- **Goal 1**: Read commands (`status`, `tree`, `ws`, `up`, `down`) automatically sync the working set after rendering cached data, with in-place revision and sync indicator
- **Goal 2**: Context switches (`twig set` on cache miss) evict non-dirty items outside the new working set, preventing unbounded cache growth
- **Goal 3**: `twig ws` displays the entire working set including dirty orphans from prior contexts in a dedicated "Unsaved changes" section
- **Goal 4**: Commands that resolve work item context (`seed`, `branch`, `commit`, `pr`, `stash`, `context`) use `ActiveItemResolver` for auto-fetch on cache miss
- **Goal 5**: `ActiveItemResolver` becomes non-nullable (required) in all commands that consume it, removing backward-compat fallback paths

### In Scope

- `WorkingSet` value object and `WorkingSetService` domain service
- `SyncCoordinator.SyncWorkingSetAsync()` method for batch sync
- `IWorkItemRepository.EvictExceptAsync(IReadOnlySet<int> keepIds)` for cache eviction
- Cache-first + sync indicator in `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand`
- Dirty orphan section in workspace view
- `ActiveItemResolver` adoption in `SeedCommand`, `BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `GitContextCommand`
- Remove nullable `ActiveItemResolver?` fallback paths in `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand`

### Out of Scope (deferred)

- Offline queue visibility (`twig pending`) — separate feature
- Background daemon/cron sync — separate feature
- Multi-project working sets — requires multi-project support first
- Working set persistence as a separate SQLite table — the cache IS the working set after eviction; no separate table needed (RD-001)
- Sprint lifecycle redesign — iteration drift (sprint rolls over between commands), automatic sprint item population on context switch, and sprint-aware cache warming are separate concerns. The working set uses whatever sprint items are currently in cache (populated by `twig refresh`).

## 2. Terminology

| Term | Definition |
|------|------------|
| Working Set | The computed set of work item IDs that Twig considers relevant for the current context. Composed of: active item, parent chain, children, sprint items, seeds, and dirty items. |
| Context Switch | A `twig set` invocation where the target item was not in cache (cache miss → ADO fetch). Triggers working set recomputation and cache eviction. |
| Dirty Orphan | A work item with pending local changes (notes, field edits) that falls outside the current sprint/tree context. Preserved during eviction; shown in workspace view. |
| Eviction | Deletion of cached work items that are not in the working set and not dirty. Triggered on context switch only. |
| Working Set Sync | A batch operation that fetches stale items within the working set from ADO and saves them through `ProtectedCacheWriter`. |

## 3. Solution Architecture

The Working Set is a domain concept computed from existing cache state. It does not require schema changes or new persistence — the cache itself becomes the working set after eviction.

```
┌─────────────────────────────────────────────────────────────────┐
│                        Command Layer                            │
│                                                                 │
│  twig set ─── WorkingSetService.ComputeAsync()                 │
│               ├── EvictExceptAsync(workingSet ∪ dirtyIds)       │
│               └── SyncCoordinator.SyncWorkingSetAsync()         │
│                                                                 │
│  twig status ─┐                                                │
│  twig tree   ─┤── Render from cache                            │
│  twig ws     ─┤── SyncCoordinator.SyncWorkingSetAsync()        │
│  twig up/down ┘── Revise display in-place                      │
│                                                                 │
│  twig ws ──────── Also shows dirty orphans section             │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                       Domain Services                           │
│                                                                 │
│  WorkingSetService                                             │
│  ├── ComputeAsync(ct) → WorkingSet                             │
│  │   reads: IContextStore, IWorkItemRepository,                │
│  │          IIterationService, SyncGuard                       │
│  │                                                             │
│  WorkingSet (value object)                                     │
│  ├── ActiveItemId: int?                                        │
│  ├── ParentChainIds: IReadOnlyList<int>                        │
│  ├── ChildrenIds: IReadOnlyList<int>                           │
│  ├── SprintItemIds: IReadOnlyList<int>                         │
│  ├── SeedIds: IReadOnlyList<int>                               │
│  ├── DirtyItemIds: IReadOnlySet<int>                           │
│  ├── IterationPath: IterationPath                              │
│  └── AllIds: IReadOnlySet<int> (computed union)                │
│                                                                 │
│  SyncCoordinator (extended)                                    │
│  └── SyncWorkingSetAsync(WorkingSet, ct) → SyncResult          │
│      fetches stale items, saves via ProtectedCacheWriter       │
│                                                                 │
│  IWorkItemRepository (extended)                                │
│  └── EvictExceptAsync(IReadOnlySet<int> keepIds, ct)           │
│      DELETE FROM work_items WHERE id NOT IN (keepIds)          │
└─────────────────────────────────────────────────────────────────┘
```

### Data Flow: `twig set 42` (cache miss — context switch)

```
1. ActiveItemResolver.ResolveByIdAsync(42)
   └── cache miss → FetchAsync(42) → SaveAsync → FetchedFromAdo(item)

2. WorkingSetService.ComputeAsync()
   ├── active item: 42
   ├── parent chain: GetParentChainAsync(42) → [10, 5]
   ├── children: GetChildrenAsync(42) → [43, 44, 45]
   ├── sprint items: GetByIterationAsync(item.IterationPath) → [42, 43, 60, 61]
   ├── seeds: GetSeedsAsync() → [-1, -2]
   └── dirty: SyncGuard.GetProtectedItemIdsAsync() → {78, 90}
   → WorkingSet.AllIds = {5, 10, 42, 43, 44, 45, 60, 61, -1, -2, 78, 90}

3. EvictExceptAsync({5, 10, 42, 43, 44, 45, 60, 61, -1, -2, 78, 90})
   └── DELETE old sprint items, old children, stale context

4. SyncCoordinator.SyncWorkingSetAsync(workingSet)
   └── Fetch stale items in working set from ADO → ProtectedCacheWriter

5. Render item + sync indicator
```

### Data Flow: `twig status` (steady state read)

```
1. ActiveItemResolver.GetActiveItemAsync() → Found(item)
2. Render item from cache immediately
3. WorkingSetService.ComputeAsync() → workingSet
4. SyncCoordinator.SyncWorkingSetAsync(workingSet)
5. If Updated → revise display in-place
```

### Data Flow: `twig ws` (workspace view)

```
1. Render sprint items + seeds + context from cache
2. WorkingSetService.ComputeAsync() → workingSet
3. SyncCoordinator.SyncWorkingSetAsync(workingSet)
4. If Updated → revise table in-place
5. Below sprint items: "Unsaved changes" section
   └── dirty items where id NOT IN sprint items ∪ seeds
```

## 4. Requirements

**Summary**: The working set introduces a cache lifecycle tied to context switches and automatic sync for read commands. All changes build on the existing cache-first infrastructure (v0.4.0).

**Items**:

- **FR-001**: `WorkingSetService.ComputeAsync()` MUST return a `WorkingSet` containing: active item, parent chain, children, sprint items (current iteration, filtered by assignee when configured), seeds, and dirty items
- **FR-002**: `twig set` on cache miss (ActiveItemResult.FetchedFromAdo) MUST trigger eviction of all non-dirty items outside the new working set
- **FR-003**: Eviction MUST NOT delete items with pending local changes (dirty flag or pending_changes rows)
- **FR-004**: `StatusCommand` MUST render from cache, then call `SyncWorkingSetAsync`, then revise display in-place if items changed
- **FR-005**: `TreeCommand` MUST render cached tree, then sync working set, then revise tree if children/parent changed
- **FR-006**: `WorkspaceCommand` MUST display dirty orphans (items with pending changes that are NOT in the current sprint or seed set) in a dedicated "Unsaved changes" section
- **FR-007**: `NavigationCommands` (up/down) MUST use `ActiveItemResolver` (non-nullable) and benefit from sync via delegation to `SetCommand`
- **FR-008**: `SeedCommand`, `BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `GitContextCommand` MUST use `ActiveItemResolver` for auto-fetch on cache miss
- **FR-009**: `SyncCoordinator.SyncWorkingSetAsync` MUST skip items that are fresh (within `cacheStaleMinutes`) and only fetch stale items
- **FR-010**: `SyncCoordinator.SyncWorkingSetAsync` MUST use `ProtectedCacheWriter` for all saves
- **FR-011**: `ActiveItemResolver` MUST be non-nullable (required parameter) in all commands that consume it — remove `ActiveItemResolver?` fallback paths
- **FR-012**: `twig set` on cache HIT MUST NOT trigger eviction — only cache miss triggers context switch eviction
- **FR-013**: `twig refresh` MUST NOT trigger eviction — it refreshes the current working set without changing scope
- **FR-014**: `twig refresh` MUST call `SyncWorkingSetAsync` after its WIQL fetch to sync stale parents/children/seeds in the working set
- **FR-015**: `SetCommand` MUST render the work item inside the `RenderWithSyncAsync` cached view so the sync indicator is visible adjacent to the rendered item, not on a blank line
- **NFR-001**: Working set computation MUST complete within 10ms (all local SQLite queries, no network)
- **NFR-002**: Eviction MUST be a single SQL DELETE statement, not per-item deletion
- **NFR-003**: `SyncWorkingSetAsync` MUST fetch stale items in parallel where possible (batch ADO calls)
- **CON-001**: All changes MUST be AOT-compatible (`PublishAot=true`, `PublishTrimmed=true`)
- **CON-002**: No new SQLite schema migrations — eviction uses existing `work_items` table
- **CON-003**: Domain layer (`Twig.Domain`) MUST NOT reference Infrastructure layer (`Twig.Infrastructure`)

## 5. Risk Classification

**Risk**: 🟡 MEDIUM

**Summary**: The eviction behavior is a significant cache lifecycle change. Incorrectly evicting dirty items or evicting during cache-hit scenarios would cause data loss. The sync-on-read changes touch 4 command files with existing async rendering logic.

**Items**:
- **RISK-001**: Eviction deletes a dirty item — mitigated by computing protected IDs via `SyncGuard` before eviction and including them in the keep set
- **RISK-002**: Eviction fires on cache hit (not just miss) — mitigated by gating on `ActiveItemResult.FetchedFromAdo` only
- **RISK-003**: `SyncWorkingSetAsync` overwhelms ADO with parallel fetches — mitigated by batching; working set is typically 20-50 items
- **RISK-004**: Dirty orphan display in `twig ws` confuses users — mitigated by clear section header "Unsaved changes" with hint to run `twig save`
- **ASSUMPTION-001**: Sprint items for the current iteration plus dirty items will typically fit in a working set of <100 items
- **ASSUMPTION-002**: `IIterationService.GetCurrentIterationAsync()` is fast enough to call during `ComputeAsync()` — it's already called in `WorkspaceCommand`

## 6. Dependencies

**Summary**: All dependencies are internal to the existing Twig codebase. No new external libraries.

**Items**:
- **DEP-001**: `SyncGuard` — existing static class, used for protected ID computation during eviction
- **DEP-002**: `ProtectedCacheWriter` — existing service, used by `SyncWorkingSetAsync` for safe batch saves
- **DEP-003**: `ActiveItemResolver` — existing service, made non-nullable in all consuming commands
- **DEP-004**: `SyncCoordinator` — existing service, extended with `SyncWorkingSetAsync`
- **DEP-005**: `IWorkItemRepository` — existing interface, extended with `EvictExceptAsync`
- **DEP-006**: `RenderWithSyncAsync` on `IAsyncRenderer` — existing method, adopted by `StatusCommand`, `TreeCommand`
- **DEP-007**: v0.4.0 cache-first architecture — all shared services must be in place (shipped)

## 7. Quality & Testing

**Summary**: Each domain service change has isolated unit tests. Command-level tests verify the integrated behavior (render → sync → revise). Eviction tests verify dirty items survive.

**Items**:
- **TEST-001**: `WorkingSetService.ComputeAsync` returns correct ID sets for all membership categories
- **TEST-002**: `EvictExceptAsync` deletes only items NOT in the keep set
- **TEST-003**: `EvictExceptAsync` preserves dirty items even if not in the keep set (dirty items are always in the keep set by construction)
- **TEST-004**: `SyncWorkingSetAsync` skips fresh items and fetches stale items
- **TEST-005**: `SyncWorkingSetAsync` uses `ProtectedCacheWriter` — dirty items not overwritten
- **TEST-006**: `SetCommand` triggers eviction on `FetchedFromAdo`, not on `Found`
- **TEST-007**: `StatusCommand` renders from cache then revises after sync
- **TEST-008**: `TreeCommand` renders cached tree then revises after sync
- **TEST-009**: `WorkspaceCommand` shows "Unsaved changes" section for dirty orphans
- **TEST-010**: `ActiveItemResolver` is non-nullable in all consuming commands — no fallback paths
- **TEST-011**: JSON/minimal output parity — no sync indicators leak into structured output
- **TEST-012**: Non-TTY output works without `Live()` rendering
- **TEST-013**: `twig refresh` does NOT trigger eviction

### Acceptance Criteria

| ID | Criterion | Verification | Traces To |
|----|-----------|--------------|-----------|
| AC-001 | `WorkingSet.AllIds` contains the union of active item, parents, children, sprint items, seeds, and dirty items | Automated test | FR-001 |
| AC-002 | After `twig set` cache miss, only working set + dirty items remain in `work_items` table | Automated test | FR-002, FR-003 |
| AC-003 | `twig status` renders from cache in <100ms, then shows sync indicator, then revises | Automated test (mock ADO) | FR-004, NFR-001 |
| AC-004 | `twig tree` renders cached tree, then revises in-place after sync | Automated test | FR-005 |
| AC-005 | `twig ws` includes "Unsaved changes" section with dirty orphans | Automated test | FR-006 |
| AC-006 | `twig set` cache HIT does NOT evict | Automated test | FR-012 |
| AC-007 | `twig refresh` does NOT evict | Automated test | FR-013 |
| AC-008 | All previously-nullable `ActiveItemResolver?` parameters are now required | Compile-time verification | FR-011 |
| AC-009 | JSON output is identical before and after for all modified commands | Automated test | TEST-011 |

## 8. Security Considerations

No new security boundaries introduced. Eviction is a local SQLite DELETE — no network, no authentication changes. All sync operations use existing authenticated ADO REST calls. `ProtectedCacheWriter` continues to prevent overwrites of locally modified items.

## 9. Deployment & Rollback

This ships as part of the Twig CLI binary. No server-side deployment. Rollback is reverting to the previous binary version. The cache is forward-compatible — eviction removes rows but doesn't change schema. An older binary reading a post-eviction cache simply has fewer cached items and will re-fetch as needed.

## 10. Resolved Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| RD-001 | No separate `working_set` table — the cache IS the working set after eviction | After eviction, every row in `work_items` is either in-scope or dirty. Querying the cache directly is simpler and avoids synchronization between two tables. |
| RD-002 | Eviction triggers only on `twig set` cache miss, not on cache hit | Cache hit means the item is already in the working set. Evicting on every `set` would be disruptive — user switches between two cached items frequently. |
| RD-003 | Eviction triggers only on `twig set`, not on `twig refresh` | `twig refresh` means "fetch latest for what I have." Eviction means "change what I have." Different intent. |
| RD-004 | `WorkingSetService` lives in `Twig.Domain.Services`, accepts `int cacheStaleMinutes` primitive | Same pattern as `SyncCoordinator` (RD from cache-first plan DD-13). Avoids Domain → Infrastructure circular reference. |
| RD-005 | `WorkingSetService.ComputeAsync` includes sprint items filtered by assignee when `DisplayName` is configured | Matches existing `WorkspaceCommand` behavior. Sprint items scoped to the user are the relevant working set. Falls back to all iteration items if no assignee configured. |
| RD-006 | `SyncWorkingSetAsync` fetches items individually (not batch WIQL) | Working set items already have IDs. Individual `FetchAsync` calls use `ProtectedCacheWriter` per-item. WIQL would require re-implementing the protected save logic for bulk results. ADO supports concurrent requests; 20-50 individual fetches complete in <2s. |
| RD-007 | `ActiveItemResolver` becomes non-nullable in all consuming commands | The nullable pattern was backward-compat scaffolding from EPIC-004. All tests have been updated. Removing it eliminates dead fallback code paths and makes the dependency explicit. |
| RD-008 | Dirty orphan section in `twig ws` shows items with pending changes NOT in the sprint/seed set | These items survived eviction because they're protected. The user needs visibility that they exist and need `twig save` or `twig discard`. |

## 11. Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Persist working set in a `working_set_items` table | Avoids recomputation on every read command | Two sources of truth; synchronization complexity; cache IS the working set after eviction | Rejected — RD-001 |
| Evict on every `twig set` (not just cache miss) | Simpler logic — always recompute | Disruptive when switching between cached items (common case); unnecessary DELETE + re-fetch | Rejected — RD-002 |
| Batch WIQL query for `SyncWorkingSetAsync` | Single ADO call | Can't use `ProtectedCacheWriter` per-item; WIQL has query complexity limits; items already identified by ID | Rejected — RD-006 |
| Keep `ActiveItemResolver` nullable with fallback | No test changes | Dead code paths; confusing conditional logic; resolution behavior differs between nullable and non-nullable callers | Rejected — RD-007 |
| TTL-based eviction (evict items older than N days) | No dependency on context switches | Doesn't align with user intent; keeps irrelevant items from the current sprint; misses the "context switch" signal | Rejected — context-aware eviction is more precise |

## 12. Files

### New Files

- **FILE-001**: `src/Twig.Domain/Services/WorkingSet.cs` — Value object with computed `AllIds` property
- **FILE-002**: `src/Twig.Domain/Services/WorkingSetService.cs` — Computes working set from cache state
- **FILE-003**: `tests/Twig.Domain.Tests/Services/WorkingSetServiceTests.cs` — Unit tests for working set computation
- **FILE-004**: `tests/Twig.Domain.Tests/Services/WorkingSetEvictionTests.cs` — Unit tests for eviction + sync integration
- **FILE-005**: `tests/Twig.Cli.Tests/Commands/WorkingSetCommandTests.cs` — Integration tests for command-level sync behavior

### Modified Files

- **FILE-010**: `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` — Add `EvictExceptAsync` method
- **FILE-011**: `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` — Implement `EvictExceptAsync`
- **FILE-012**: `src/Twig.Domain/Services/SyncCoordinator.cs` — Add `SyncWorkingSetAsync` method
- **FILE-013**: `src/Twig/Commands/SetCommand.cs` — Add eviction on cache miss, use `SyncWorkingSetAsync`
- **FILE-014**: `src/Twig/Commands/StatusCommand.cs` — Make `ActiveItemResolver` required, add working set sync + revise
- **FILE-015**: `src/Twig/Commands/TreeCommand.cs` — Make `ActiveItemResolver` required, add working set sync + revise
- **FILE-016**: `src/Twig/Commands/NavigationCommands.cs` — Make `ActiveItemResolver` required
- **FILE-017**: `src/Twig/Commands/WorkspaceCommand.cs` — Make `ActiveItemResolver` required, add dirty orphan section
- **FILE-018**: `src/Twig/Commands/SeedCommand.cs` — Adopt `ActiveItemResolver`
- **FILE-019**: `src/Twig/Commands/BranchCommand.cs` — Adopt `ActiveItemResolver`
- **FILE-020**: `src/Twig/Commands/CommitCommand.cs` — Adopt `ActiveItemResolver`
- **FILE-021**: `src/Twig/Commands/PrCommand.cs` — Adopt `ActiveItemResolver`
- **FILE-022**: `src/Twig/Commands/StashCommand.cs` — Adopt `ActiveItemResolver`
- **FILE-023**: `src/Twig/Commands/GitContextCommand.cs` — Adopt `ActiveItemResolver`
- **FILE-024**: `src/Twig/DependencyInjection/CommandRegistrationModule.cs` — Update DI for new required params
- **FILE-025**: `src/Twig/DependencyInjection/CommandServiceModule.cs` — Register `WorkingSetService`
- **FILE-026**: Various test files — Update constructors for non-nullable `ActiveItemResolver`, add new tests

## 13. Implementation Plan

### EPIC-001: Working Set Domain Model and Eviction

**Goal**: Introduce `WorkingSet` value object, `WorkingSetService`, and `IWorkItemRepository.EvictExceptAsync`. Pure domain/infrastructure additions — no command changes.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-001 | Create `WorkingSet` value object with properties: `ActiveItemId` (int?), `ParentChainIds` (IReadOnlyList\<int\>), `ChildrenIds` (IReadOnlyList\<int\>), `SprintItemIds` (IReadOnlyList\<int\>), `SeedIds` (IReadOnlyList\<int\>), `DirtyItemIds` (IReadOnlySet\<int\>), `IterationPath`, and computed `AllIds` (IReadOnlySet\<int\>) returning the union of all ID sets. | Not Started | `src/Twig.Domain/Services/WorkingSet.cs` |
| ITEM-002 | Create `WorkingSetService`. Constructor: `IContextStore`, `IWorkItemRepository`, `IPendingChangeStore`, `IIterationService`, `string? userDisplayName` (primitive — same pattern as SyncCoordinator's `cacheStaleMinutes`, avoids Domain→Infrastructure dependency). Method `ComputeAsync(CancellationToken)` → `WorkingSet`: reads active ID from context store, queries cache for parent chain/children/sprint items/seeds, queries `SyncGuard` for dirty IDs. Sprint items filtered by `userDisplayName` when non-null. | Not Started | `src/Twig.Domain/Services/WorkingSetService.cs` |
| ITEM-003 | Add `EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken)` to `IWorkItemRepository` interface. | Not Started | `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` |
| ITEM-004 | Implement `EvictExceptAsync` in `SqliteWorkItemRepository`. Single SQL: `DELETE FROM work_items WHERE id NOT IN (...)`. Use parameterized query with the keep set. | Not Started | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` |
| ITEM-005 | Unit tests for `WorkingSetService.ComputeAsync`: correct membership for each category, empty cache, no active item, missing parent chain, no sprint items. | Not Started | `tests/Twig.Domain.Tests/Services/WorkingSetServiceTests.cs` |
| ITEM-006 | Unit tests for `EvictExceptAsync`: deletes non-kept items, preserves kept items, handles empty keep set (deletes all), handles all-kept (deletes nothing). | Not Started | `tests/Twig.Domain.Tests/Services/WorkingSetEvictionTests.cs` |

### EPIC-002: SyncCoordinator Working Set Sync

**Goal**: Add `SyncWorkingSetAsync` to `SyncCoordinator`. Batch-syncs stale items within the working set.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-007 | Add `SyncWorkingSetAsync(WorkingSet workingSet, CancellationToken ct)` to `SyncCoordinator`. Iterates `workingSet.AllIds` (excluding seeds — negative IDs), checks `LastSyncedAt` per-item against `cacheStaleMinutes`, fetches stale items via `_adoService.FetchAsync`, saves through `ProtectedCacheWriter`. Returns `SyncResult.Updated(n)` with total count, `SyncResult.UpToDate` if none stale, `SyncResult.Failed` on network error. | Not Started | `src/Twig.Domain/Services/SyncCoordinator.cs` |
| ITEM-008 | Unit tests for `SyncWorkingSetAsync`: all fresh → UpToDate, mix of stale/fresh → Updated(staleCount), all stale → Updated(allCount), network failure → Failed, dirty items skipped by ProtectedCacheWriter, seed IDs (negative) skipped. | Not Started | `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` |

### EPIC-003: SetCommand Eviction on Context Switch

**Goal**: `twig set` triggers working set computation and cache eviction when the target item was fetched from ADO (cache miss). No eviction on cache hit.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-009 | Inject `WorkingSetService` into `SetCommand`. After `ActiveItemResolver.ResolveByIdAsync` returns `FetchedFromAdo`: call `WorkingSetService.ComputeAsync()`, then `workItemRepo.EvictExceptAsync(workingSet.AllIds)`. On `Found` (cache hit): no eviction. | Not Started | `src/Twig/Commands/SetCommand.cs` |
| ITEM-010 | Replace `SyncCoordinator.SyncChildrenAsync` in `SetCommand` with `SyncCoordinator.SyncWorkingSetAsync(workingSet)` — syncs the full working set (parents, children, sprint items) not just children. | Not Started | `src/Twig/Commands/SetCommand.cs` |
| ITEM-011 | Register `WorkingSetService` in `CommandServiceModule.cs` as singleton with DI factory lambda for `userDisplayName` from `TwigConfiguration`. | Not Started | `src/Twig/DependencyInjection/CommandServiceModule.cs` |
| ITEM-012 | Tests: (a) cache miss → eviction fires, non-working-set items deleted; (b) cache hit → no eviction; (c) dirty items survive eviction; (d) working set items survive eviction. | Not Started | `tests/Twig.Cli.Tests/Commands/WorkingSetCommandTests.cs` |

### EPIC-004: Read Command Sync + ActiveItemResolver Required

**Goal**: `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand` get working set sync after cached render. `ActiveItemResolver` becomes non-nullable in all consuming commands.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-013 | **StatusCommand**: Make `ActiveItemResolver` required (non-nullable). Remove fallback `workItemRepo.GetByIdAsync` path. Inject `WorkingSetService` and `SyncCoordinator`. After rendering cached status, call `SyncWorkingSetAsync` via `RenderWithSyncAsync` — revise display if item changed. | Not Started | `src/Twig/Commands/StatusCommand.cs` |
| ITEM-014 | **TreeCommand**: Make `ActiveItemResolver` required. Inject `WorkingSetService` and `SyncCoordinator`. After rendering cached tree, call `SyncWorkingSetAsync` — revise tree if children/parent changed. | Not Started | `src/Twig/Commands/TreeCommand.cs` |
| ITEM-015 | **NavigationCommands**: Make `ActiveItemResolver` required. Remove fallback path. Navigation delegates to `SetCommand` which handles sync. | Not Started | `src/Twig/Commands/NavigationCommands.cs` |
| ITEM-016 | **WorkspaceCommand**: Make `ActiveItemResolver` required. Remove fallback path. Add "Unsaved changes" section: after rendering sprint items/seeds, query `SyncGuard.GetProtectedItemIdsAsync()`, find IDs not in sprint/seed sets, fetch those items from cache, render as a separate group with hint "Run 'twig save' to push these changes." | Not Started | `src/Twig/Commands/WorkspaceCommand.cs` |
| ITEM-017 | Update `CommandRegistrationModule.cs` DI wiring for all commands with non-nullable `ActiveItemResolver`, `WorkingSetService`, `SyncCoordinator`. | Not Started | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` |
| ITEM-018 | Update all existing tests for `StatusCommand`, `TreeCommand`, `NavigationCommands`, `WorkspaceCommand` — supply non-nullable `ActiveItemResolver` mock. Add new tests for sync-after-render and dirty orphan display. | Not Started | `tests/Twig.Cli.Tests/Commands/` |

### EPIC-005: ActiveItemResolver Adoption in Remaining Commands

**Goal**: Commands that resolve work item context using raw `contextStore` + `workItemRepo.GetByIdAsync` switch to `ActiveItemResolver` for auto-fetch on cache miss. No sync indicator — just resolver adoption.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-019 | **SeedCommand**: Replace `contextStore.GetActiveWorkItemIdAsync()` + `workItemRepo.GetByIdAsync()` with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | Not Started | `src/Twig/Commands/SeedCommand.cs` |
| ITEM-020 | **BranchCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | Not Started | `src/Twig/Commands/BranchCommand.cs` |
| ITEM-021 | **CommitCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | Not Started | `src/Twig/Commands/CommitCommand.cs` |
| ITEM-022 | **PrCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | Not Started | `src/Twig/Commands/PrCommand.cs` |
| ITEM-023 | **StashCommand**: Replace `contextStore.GetActiveWorkItemIdAsync()` + `workItemRepo.GetByIdAsync()` with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | Not Started | `src/Twig/Commands/StashCommand.cs` |
| ITEM-024 | **GitContextCommand**: Replace `workItemRepo.GetByIdAsync(activeId)` with `ActiveItemResolver.GetActiveItemAsync()`. Add `ActiveItemResolver` to constructor. | Not Started | `src/Twig/Commands/GitContextCommand.cs` |
| ITEM-025 | Update DI registrations in `CommandRegistrationModule.cs` for all 6 commands. | Not Started | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` |
| ITEM-026 | Update tests for all 6 commands — supply `ActiveItemResolver` mock, verify auto-fetch behavior on cache miss. | Not Started | Various test files |

## 14. Change Log

- 2026-03-17: Initial PRD created from planning conversation. Working set concept crystallized from cache-first gap analysis.
