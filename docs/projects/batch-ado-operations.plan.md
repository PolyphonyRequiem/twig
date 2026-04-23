# Batch ADO Operations for Seed Publish and Multi-Item Edits

**Work Item:** #1884  
**Type:** Issue  
**Status:** ✅ Done  
**Plan Revision:** 0  
**Revision Notes:** Initial draft.

---

## Executive Summary

This design introduces concurrency-aware batching for Azure DevOps REST API operations
in twig's seed publish pipeline and multi-item edit flows. Today, both `PublishAllAsync`
and `BatchCommand.ExecuteMultiItemAsync` process items **sequentially** — each item
incurs 2–3 HTTP round-trips (fetch → patch/create → resync fetch), producing O(3N)
serial HTTP calls. For a typical seed publish of 10+ items, this translates to 30+
serial HTTP calls at ~200ms each ≈ 6+ seconds of pure network wait.

The proposal introduces three coordinated optimizations: (1) **level-parallel seed
publish** — within each topological level, independent seeds are created concurrently
with a configurable concurrency limit and consolidated post-create batch fetch;
(2) **parallel multi-item batch** — `BatchCommand` and `PendingChangeFlusher` process
independent items concurrently with batch pre-fetch and batch post-fetch; (3) a shared
**`AdoConcurrencyThrottle`** that enforces a process-wide concurrency cap (default 4)
to prevent ADO rate-limit (429) responses while remaining responsive to Retry-After
headers. Expected outcome: 3–5× wall-time improvement for batch operations with
zero behavioral changes for single-item commands.

---

## Background

### Current Architecture

The twig CLI interacts with Azure DevOps through a layered architecture:

| Layer | Component | Responsibility |
|-------|-----------|----------------|
| CLI Commands | `BatchCommand`, `SeedPublishCommand`, `SyncCommand` | User-facing CLI; orchestrates multi-step flows |
| Domain Services | `SeedPublishOrchestrator`, `SyncCoordinator`, `PendingChangeFlusher` | Business logic: validation, topological sort, conflict resolution |
| Infrastructure | `AdoRestClient : IAdoWorkItemService` | HTTP calls to ADO REST API (7.1) |
| Infrastructure | `ConflictRetryHelper` | Optimistic concurrency retry (one retry on 412) |
| Infrastructure | `AutoPushNotesHelper` | Side-effect: flushes staged notes on successful push |

**ADO REST API constraints (7.1):**
- No batch create/update endpoint — each work item requires individual POST/PATCH calls.
- Batch **read** via `GET _apis/wit/workitems?ids=x,y,z` (max 200 per request) — already used in `FetchBatchAsync`.
- Rate limiting returns HTTP 429 with `Retry-After` header.
- Optimistic concurrency via `If-Match` header (revision-based).

### Current Seed Publish Flow (Sequential)

`SeedPublishOrchestrator.PublishAllAsync` performs:
1. Load all seeds → topological sort via `SeedDependencyGraph.Sort()`
2. For each seed **sequentially** in topo order:
   a. Validate → guard parent published → create in ADO (1 HTTP)
   b. Fetch back full item (1 HTTP)
   c. Transactional local DB update (remap IDs, delete old, save new)
   d. Promote seed links to ADO relations (1 HTTP per link)
   e. Best-effort backlog ordering (2 HTTP: fetch siblings + patch)
   f. Post-publish cache refresh (1 HTTP)

**Total per seed: 3–6 HTTP calls, all sequential.**

### Current Batch Command Flow (Sequential)

`BatchCommand.ExecuteMultiItemAsync` performs:
1. For each item ID **sequentially**:
   a. Resolve from cache → fetch remote (1 HTTP)
   b. Conflict resolution → build patch → PATCH (1 HTTP)
   c. Add comment if `--note` (1 HTTP)
   d. Auto-push pending notes (1+ HTTP)
   e. Resync cache (1 HTTP)

**Total per item: 3–4 HTTP calls, all sequential.**

### Current PendingChangeFlusher Flow (Sequential)

`PendingChangeFlusher.FlushAsync` processes items sequentially with the same
fetch → patch → resync pattern (3 HTTP calls per item).

### Call-Site Audit

| File | Method | Pattern | Impact |
|------|--------|---------|--------|
| `SeedPublishOrchestrator.cs` | `PublishAllAsync` | Sequential `foreach` over topo-sorted seeds calling `PublishAsync` | **Primary target** — parallelize per-level |
| `BatchCommand.cs` | `ExecuteMultiItemAsync` | Sequential `foreach` over `itemIds` calling `ProcessItemAsync` | **Primary target** — parallelize independent items |
| `PendingChangeFlusher.cs` | `FlushAsync` | Sequential `foreach` over `itemIds` with fetch/patch/resync | **Primary target** — parallelize flush |
| `McpPendingChangeFlusher.cs` | `FlushAllAsync` | Sequential `foreach` over dirty IDs with fetch/patch/resync | **Secondary target** — same pattern as CLI flusher |
| `SyncCoordinator.cs` | `FetchStaleAndSaveAsync` | Already uses `Task.WhenAll` for concurrent fetch | **No change** — already parallelized |
| `RefreshOrchestrator.cs` | `FetchItemsAsync` | Parallel fetch for active + children via `Task.WhenAll` | **No change** — already parallelized |
| `BacklogOrderer.cs` | `TryOrderAsync` | 2 HTTP calls per seed (siblings fetch + patch) | **Deferrable** — best-effort, non-blocking |
| `SeedLinkPromoter.cs` | `PromoteLinksAsync` | Sequential `foreach` over links calling `AddLinkAsync` | **Deferrable** — low frequency, best-effort |
| `AutoPushNotesHelper.cs` | `PushAndClearAsync` | Sequential `foreach` over notes calling `AddCommentAsync` | **Deferrable** — typically 0–1 notes |

---

## Problem Statement

1. **Seed publish is O(3N) sequential HTTP calls.** Publishing 10 seeds under a parent
   hierarchy takes ~6–12s of pure network time. When seeding from a plan document
   (which can produce 20+ seeds), this delay is noticeable and frustrating.

2. **Multi-item batch command is equally sequential.** `twig batch --ids 1,2,3,4,5
   --state Done` makes 15–20 sequential HTTP calls. MCP agents routinely batch 5–10
   state transitions, experiencing multi-second delays.

3. **PendingChangeFlusher (sync push phase) is sequential.** Flushing 5 dirty items
   during `twig sync` takes 15+ sequential HTTP calls, blocking the pull phase.

4. **No concurrency control.** The codebase lacks a shared concurrency limiter. Naively
   parallelizing with `Task.WhenAll` risks triggering ADO rate limits (429), especially
   when multiple twig instances share a PAT.

---

## Goals and Non-Goals

### Goals

1. **G-1:** Reduce wall-time for `twig seed publish --all` by 3–5× for typical
   hierarchies (5–15 seeds) by parallelizing independent operations within each
   topological level.

2. **G-2:** Reduce wall-time for `twig batch --ids ... --state/--set/--note` by 2–4×
   by parallelizing independent item operations and consolidating batch fetches.

3. **G-3:** Reduce wall-time for `twig sync` push phase by 2–4× by parallelizing
   `PendingChangeFlusher` operations.

4. **G-4:** Introduce a shared `AdoConcurrencyThrottle` that caps in-process ADO
   request concurrency (default 4), respects 429 Retry-After headers, and is reusable
   across all ADO-calling code paths.

5. **G-5:** Maintain identical behavior for single-item operations (no regression).

6. **G-6:** Maintain AOT compatibility — no reflection, all new serializable types
   registered in `TwigJsonContext`.

### Non-Goals

- **NG-1:** ADO `$batch` multipart endpoint — the Work Items API does not support
  batch create/update via multipart; individual endpoints are the only option.
- **NG-2:** Cross-process concurrency coordination — this is a single-process optimization.
  Shared-PAT scenarios with multiple twig instances are out of scope.
- **NG-3:** Parallelizing within a single seed's publish flow (create → fetch-back →
  local DB is inherently sequential).
- **NG-4:** Parallelizing `BacklogOrderer` or `SeedLinkPromoter` — these are best-effort
  side-effects with low call volume.
- **NG-5:** Retry/backoff for transient 5xx errors — existing exception handling is sufficient.

---

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `SeedPublishOrchestrator.PublishAllAsync` publishes seeds within the same topological level concurrently |
| FR-2 | `SeedPublishOrchestrator.PublishAllAsync` respects parent → child ordering: children only publish after parent succeeds |
| FR-3 | `BatchCommand.ExecuteMultiItemAsync` processes items concurrently (fetch, patch, note, resync) |
| FR-4 | `BatchCommand` preserves result ordering — output order matches input `--ids` order |
| FR-5 | `PendingChangeFlusher.FlushAsync` processes items concurrently |
| FR-6 | A shared `AdoConcurrencyThrottle` limits in-process ADO request concurrency (configurable, default 4) |
| FR-7 | When ADO returns 429, the throttle pauses all queued requests for the `Retry-After` duration |
| FR-8 | Post-operation cache resync uses `FetchBatchAsync` instead of per-item `FetchAsync` where possible |
| FR-9 | Partial failure: if one item fails in a parallel batch, remaining items continue processing |
| FR-10 | `McpPendingChangeFlusher.FlushAllAsync` also benefits from parallel processing |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible: no reflection, source-gen serialization only |
| NFR-2 | Default concurrency of 4 chosen to stay well below ADO's per-user rate limit threshold |
| NFR-3 | Single-item code paths have zero overhead (throttle only engages when concurrency > 1) |
| NFR-4 | Thread-safe SQLite access: all DB writes happen sequentially (SQLite WAL single-writer) |
| NFR-5 | Test coverage for parallel scenarios including partial failure and throttle behavior |

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│  CLI Commands / MCP Tools                                 │
│  ┌────────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │ SeedPublish     │  │ BatchCommand │  │ SyncCommand  │  │
│  │ Command         │  │              │  │              │  │
│  └───────┬────────┘  └──────┬───────┘  └──────┬───────┘  │
├──────────┼──────────────────┼──────────────────┼──────────┤
│  Domain Services            │                  │          │
│  ┌───────┴────────┐  ┌──────┴───────┐  ┌──────┴───────┐  │
│  │SeedPublish     │  │ BatchCommand │  │PendingChange │  │
│  │Orchestrator    │  │ (multi-item) │  │Flusher       │  │
│  │ + LevelPublish │  │ + ParallelOp │  │ + ParallelOp │  │
│  └───────┬────────┘  └──────┬───────┘  └──────┬───────┘  │
├──────────┼──────────────────┼──────────────────┼──────────┤
│  Infrastructure             │                  │          │
│  ┌──────────────────────────┴──────────────────┴───────┐  │
│  │          AdoRestClient : IAdoWorkItemService         │  │
│  │  ┌───────────────────────────────────────────────┐  │  │
│  │  │        AdoConcurrencyThrottle (new)            │  │  │
│  │  │  • SemaphoreSlim(maxConcurrency)              │  │  │
│  │  │  • 429 Retry-After global pause               │  │  │
│  │  └───────────────────────────────────────────────┘  │  │
│  └─────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `AdoConcurrencyThrottle` (New — Infrastructure)

**Responsibility:** Process-wide concurrency limiter for ADO HTTP requests.

```csharp
internal sealed class AdoConcurrencyThrottle : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private volatile DateTimeOffset _pauseUntil = DateTimeOffset.MinValue;

    public AdoConcurrencyThrottle(int maxConcurrency = 4);

    /// <summary>
    /// Acquires a slot, honoring any active Retry-After pause.
    /// Returns a disposable that releases the slot.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct);

    /// <summary>
    /// Called when a 429 is received. Sets the global pause timestamp.
    /// All subsequent AcquireAsync calls wait until the pause expires.
    /// </summary>
    public void SetPause(TimeSpan retryAfter);
}
```

**Design decisions:**
- **SemaphoreSlim** — lightweight, async-compatible, AOT-safe.
- **Global pause on 429** — rather than per-request retry, all pending requests wait.
  This prevents thundering-herd retry storms.
- **Default concurrency = 4** — ADO's per-user rate limit is ~100 requests/5 seconds.
  With 4 concurrent requests at ~200ms each, we generate ~20 requests/second — well
  within limits.
- **Integration point:** Injected into `AdoRestClient.SendAsync` as an optional
  dependency. When null (tests, single-item paths), no throttling is applied.

#### 2. `SeedPublishOrchestrator` Changes (Domain)

**New internal method:** `PublishLevelAsync(IReadOnlyList<int> levelIds, ...)`

The `PublishAllAsync` method is refactored to:
1. Build a **level map** from the topological sort — group seeds by their dependency depth.
2. For each level, call `PublishLevelAsync` which publishes all seeds in that level
   concurrently via `Task.WhenAll`.
3. After each level completes, batch-fetch all newly created IDs using `FetchBatchAsync`
   for consolidated cache resync.
4. SQLite writes remain sequential within each level's post-publish phase — the `foreach`
   over publish results does the transactional DB work serially.

**Key constraint:** Within a level, seeds are independent (no parent→child edges), so
concurrent creation is safe. Cross-level ordering is preserved by the sequential level loop.

#### 3. `BatchCommand.ExecuteMultiItemAsync` Changes (CLI)

Refactored from sequential `foreach` to concurrent processing:
1. **Batch pre-fetch:** Collect all item IDs, use `FetchBatchAsync` to fetch all remote
   items in one HTTP call (replaces per-item `FetchAsync` in `ProcessItemAsync`).
2. **Parallel processing:** Process items concurrently via `Task.WhenAll` with the
   throttle. Each item's `ProcessItemAsync` receives the pre-fetched remote item
   instead of fetching individually.
3. **Batch post-fetch:** After all patches complete, batch-fetch all successful IDs
   using `FetchBatchAsync` for consolidated cache resync (replaces per-item resync).
4. **Result ordering:** Use index-preserving concurrent collection (`ConcurrentDictionary<int, result>`)
   and re-order by input position before output.

#### 4. `PendingChangeFlusher` Changes (CLI + MCP)

Same pattern as BatchCommand:
1. Batch pre-fetch remote items.
2. Parallel patch/note operations.
3. Batch post-fetch for cache resync.

**Notes-only items** (FR-9 in existing code) continue to bypass conflict resolution
and skip the pre-fetch for those specific items.

#### 5. `IAdoWorkItemService` Interface Extension

One new method to support batch resync:

```csharp
// Already exists — no change needed:
Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct);
```

The existing `FetchBatchAsync` is sufficient. No new interface methods are needed.

### Data Flow: Level-Parallel Seed Publish

```
Level 0 (roots):  [seed-1, seed-2, seed-3]  ← concurrent CreateAsync
                         ↓
Level 0 post:     FetchBatchAsync([new-1, new-2, new-3])  ← single HTTP call
                  Sequential DB: remap IDs, delete seeds, save items
                         ↓
Level 1 (children): [seed-4, seed-5]  ← concurrent CreateAsync
                         ↓
Level 1 post:     FetchBatchAsync([new-4, new-5])
                  Sequential DB: remap IDs, delete seeds, save items
```

### Data Flow: Parallel Multi-Item Batch

```
Input: --ids 10,20,30,40,50  --state Done

Step 1: FetchBatchAsync([10,20,30,40,50])  ← 1 HTTP call (replaces 5)
Step 2: Parallel PATCH:
          PatchAsync(10, ...) ─┐
          PatchAsync(20, ...) ─┤  concurrent (throttle: max 4)
          PatchAsync(30, ...) ─┤
          PatchAsync(40, ...) ─┤
          PatchAsync(50, ...) ─┘
Step 3: FetchBatchAsync([10,20,30,40,50])  ← 1 HTTP call (replaces 5)
Step 4: Sequential SaveBatchAsync  ← SQLite batch write
```

**Before:** 15 HTTP calls sequential (3 per item × 5 items)  
**After:** 2 + 5 = 7 HTTP calls (2 batch + 5 concurrent patches)

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | SemaphoreSlim-based throttle in Infrastructure (not Domain) | The throttle is an HTTP concern, not business logic. Domain services are unaware of concurrency details. |
| DD-2 | Default concurrency = 4 | Conservative: ~20 req/s at 200ms latency. ADO allows ~20 req/s per user. Leaves headroom for other tools. |
| DD-3 | Throttle injected into AdoRestClient.SendAsync | All ADO HTTP calls automatically throttled — no caller changes needed. But batch-level concurrency is controlled by the Domain/CLI layer. |
| DD-4 | Level-parallel publish (not pipeline) | A pipelined approach (start level N+1 while N is still doing DB work) adds complexity. Level-parallel is simpler and captures most of the benefit since HTTP latency dominates. |
| DD-5 | Batch pre-fetch + batch post-fetch | Consolidating fetches from N individual calls to 1 batch call. `FetchBatchAsync` already exists and handles chunking (max 200). |
| DD-6 | SQLite writes remain sequential | WAL mode supports concurrent reads but only one writer. Parallelizing DB writes would require a write queue — complexity not justified for sub-ms local ops. |
| DD-7 | Result ordering via index-preserving collection | `BatchCommand` output must match `--ids` input order. Using `ConcurrentDictionary<int, result>` keyed by input index preserves order. |
| DD-8 | Partial failure semantics preserved | Existing behavior: batch continues past individual failures, collects errors. Parallel version maintains this — `Task.WhenAll` results are inspected individually. |

---

## Dependencies

### External
- **Azure DevOps REST API 7.1** — no new API features required. All operations use
  existing individual endpoints plus the existing batch read endpoint.

### Internal
- **`SeedDependencyGraph.Sort`** — must be extended to return level information
  (currently returns a flat topological order).
- **`ConflictRetryHelper`** — unchanged; called within each parallel task.
- **`FetchBatchAsync`** — unchanged; already handles chunking.

### Sequencing
- `AdoConcurrencyThrottle` must be implemented first as it's consumed by all other changes.
- Seed publish changes depend on `SeedDependencyGraph` level-map extension.
- BatchCommand and PendingChangeFlusher changes are independent of each other.

---

## Impact Analysis

### Components Affected

| Component | Change Type | Backward Compatible |
|-----------|------------|---------------------|
| `AdoRestClient` | Modified — throttle integration in `SendAsync` | Yes — throttle is optional |
| `SeedPublishOrchestrator` | Modified — level-parallel publish | Yes — same public API, same results |
| `SeedDependencyGraph` | Modified — add level-map output | Yes — additive method |
| `BatchCommand` | Modified — parallel multi-item path | Yes — same CLI interface |
| `PendingChangeFlusher` | Modified — parallel flush | Yes — same public API |
| `McpPendingChangeFlusher` | Modified — parallel flush | Yes — same public API |
| `TwigServiceRegistration` | Modified — register throttle | Yes — additive |

### Performance

| Scenario | Before (sequential) | After (parallel, concurrency=4) |
|----------|--------------------|---------------------------------|
| 10-seed publish (3 levels) | ~30 HTTP calls, ~6s | ~12 concurrent + 3 batch = ~2s |
| 5-item batch state change | ~15 HTTP calls, ~3s | 2 batch + 5 concurrent = ~1.5s |
| 5-item sync push | ~15 HTTP calls, ~3s | 2 batch + 5 concurrent = ~1.5s |
| 1-item update (no change) | 3 HTTP calls, ~0.6s | 3 HTTP calls, ~0.6s |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ADO rate limiting (429) under parallel load | Medium | Medium | `AdoConcurrencyThrottle` with global pause on 429; default concurrency of 4 is conservative |
| SQLite write contention from concurrent tasks | Low | High | All DB writes remain sequential; only HTTP calls are parallelized |
| Seed publish partial failure leaves orphaned ADO items | Low | Medium | Existing behavior — seeds published before failure are valid. DB transaction per-seed ensures local consistency |
| HttpClient thread-safety | Low | High | `HttpClient` is documented thread-safe for `SendAsync`. Single instance shared across concurrent calls is the recommended pattern |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should the concurrency limit be configurable via `twig config` or only compile-time? | Low | Default of 4 is sufficient for v1. Config support can be added later without breaking changes. |
| 2 | Should `BacklogOrderer.TryOrderAsync` also be parallelized across siblings? | Low | It's best-effort and currently runs once per seed. Deferring to a future optimization. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/Ado/AdoConcurrencyThrottle.cs` | Process-wide concurrency limiter with 429 Retry-After support |
| `tests/Twig.Infrastructure.Tests/Ado/AdoConcurrencyThrottleTests.cs` | Unit tests for throttle: concurrency cap, pause behavior, disposal |
| `tests/Twig.Domain.Tests/Services/SeedDependencyGraphLevelTests.cs` | Tests for level-map computation from topological sort |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/SeedDependencyGraph.cs` | Add `SortWithLevels()` method returning `IReadOnlyList<IReadOnlyList<int>>` (list of levels) |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | Refactor `PublishAllAsync` to process levels concurrently; add `PublishLevelAsync` helper; batch post-fetch |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Integrate `AdoConcurrencyThrottle` in `SendAsync`; accept throttle via constructor |
| `src/Twig/Commands/BatchCommand.cs` | Refactor `ExecuteMultiItemAsync` with batch pre-fetch, parallel `ProcessItemAsync`, batch post-fetch |
| `src/Twig/Commands/PendingChangeFlusher.cs` | Refactor `FlushAsync` with batch pre-fetch, parallel processing, batch post-fetch |
| `src/Twig.Mcp/Services/McpPendingChangeFlusher.cs` | Same parallel refactor as CLI PendingChangeFlusher |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `AdoConcurrencyThrottle` as singleton |
| `tests/Twig.Domain.Tests/Services/SeedPublishOrchestratorTests.cs` | Add tests for level-parallel publish, partial failure |
| `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs` | Add tests for parallel multi-item batch, batch fetch consolidation |
| `tests/Twig.Cli.Tests/Commands/PendingChangeFlusherTests.cs` | Add tests for parallel flush |

---

## ADO Work Item Structure

### Issue: #1884 — Batch ADO operations for seed publish and multi-item edits

**Goal:** Introduce concurrency-aware batching for ADO REST API operations to reduce
wall-time for seed publish, multi-item batch commands, and sync push.

**Prerequisites:** None (standalone issue).

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1884.1 | **Create `AdoConcurrencyThrottle`** — SemaphoreSlim-based concurrency limiter with 429 Retry-After global pause. Register in DI. Write unit tests. | `AdoConcurrencyThrottle.cs`, `AdoConcurrencyThrottleTests.cs`, `TwigServiceRegistration.cs` | S |
| T-1884.2 | **Integrate throttle into `AdoRestClient.SendAsync`** — Accept `AdoConcurrencyThrottle?` via constructor, acquire/release around HTTP calls, call `SetPause` on 429 before rethrowing. | `AdoRestClient.cs` | S |
| T-1884.3 | **Extend `SeedDependencyGraph` with level-map** — Add `SortWithLevels()` returning `(IReadOnlyList<IReadOnlyList<int>> Levels, IReadOnlySet<int> CyclicIds)`. Reuse existing sort logic, group by depth. Write tests. | `SeedDependencyGraph.cs`, `SeedDependencyGraphLevelTests.cs` | S |
| T-1884.4 | **Refactor `SeedPublishOrchestrator.PublishAllAsync` for level-parallel publish** — Replace sequential loop with level-based concurrency. Batch post-fetch via `FetchBatchAsync`. Sequential DB writes per level. | `SeedPublishOrchestrator.cs`, `SeedPublishOrchestratorTests.cs` | M |
| T-1884.5 | **Refactor `BatchCommand.ExecuteMultiItemAsync` for parallel processing** — Batch pre-fetch, concurrent `ProcessItemAsync`, batch post-fetch. Preserve result ordering. Accept pre-fetched remote in `ProcessItemAsync`. | `BatchCommand.cs`, `BatchCommandTests.cs` | M |
| T-1884.6 | **Refactor `PendingChangeFlusher.FlushAsync` and `McpPendingChangeFlusher.FlushAllAsync` for parallel processing** — Same batch pre-fetch + parallel patch + batch post-fetch pattern. | `PendingChangeFlusher.cs`, `McpPendingChangeFlusher.cs`, `PendingChangeFlusherTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig seed publish --all` with 10 seeds completes in ≤40% of current wall-time
- [ ] `twig batch --ids 1,2,3,4,5 --state Done` completes in ≤50% of current wall-time
- [ ] `twig sync` push phase with 5 dirty items completes in ≤50% of current wall-time
- [ ] Single-item operations have no measurable performance regression
- [ ] All existing tests pass without modification
- [ ] ADO 429 rate-limit responses trigger global pause, not per-request retry storm
- [ ] Partial failures in batch operations are reported per-item (existing behavior preserved)

---

## PR Groups

| PG | Name | Tasks | Type | Est. LoC | Est. Files | Dependencies |
|----|------|-------|------|----------|------------|--------------|
| PG-1 | Concurrency throttle + AdoRestClient integration | T-1884.1, T-1884.2 | Deep | ~200 | ~5 | None |
| PG-2 | Level-parallel seed publish | T-1884.3, T-1884.4 | Deep | ~350 | ~6 | PG-1 |
| PG-3 | Parallel batch command + flush | T-1884.5, T-1884.6 | Wide | ~400 | ~8 | PG-1 |

**Execution order:** PG-1 → (PG-2 ∥ PG-3)

PG-2 and PG-3 are independent of each other but both depend on PG-1 (the throttle).
They can be developed and reviewed in parallel after PG-1 merges.

