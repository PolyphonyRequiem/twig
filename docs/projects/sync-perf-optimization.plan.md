# Sync Performance Optimization — Solution Design

**Epic:** #1611 — Sync Performance Optimization
> **Status**: ✅ Done
**Author:** Copilot (Principal Architect)

---

## Executive Summary

This design addresses four measurable performance bottlenecks in Twig's sync pipeline between the local SQLite cache and Azure DevOps. The optimizations target: (1) replacing N+1 HTTP/SQLite calls in `SyncCoordinator.FetchStaleAndSaveAsync` with batch operations, (2) parallelizing sequential network calls in `RefreshOrchestrator.FetchItemsAsync` and consolidating ~118 lines of duplicated inline logic from `RefreshCommand.ExecuteCoreAsync` into the orchestrator, (3) introducing tiered cache TTLs so read-only commands (`status`, `tree`, `show`) tolerate longer staleness while mutating commands (`set`, `edit`, `save`) remain aggressive, and (4) enabling HTTP transport optimizations (gzip/Brotli decompression, HTTP/2, in-memory response caching for metadata). Expected outcomes: ~97% reduction in HTTP round-trips for working-set syncs (N items from N calls to 1), ~66% cut in refresh wall-clock time, and lower API bandwidth — all without changing the user-facing CLI contract or introducing new NuGet dependencies.

---

## Background

### Current Architecture

Twig's sync pipeline sits between the local SQLite cache (WAL mode, per-workspace at `.twig/{org}/{project}/twig.db`) and Azure DevOps REST API (v7.1). Three primary paths exercise this pipeline:

1. **Working-set sync** (`SyncCoordinator.SyncWorkingSetAsync` / `SyncItemSetAsync`): Called by `StatusCommand`, `TreeCommand`, `ShowCommand`, and `RefreshCommand` after the bulk refresh. Filters working-set IDs, checks per-item `LastSyncedAt` staleness, fetches stale items, and saves through `ProtectedCacheWriter`.

2. **Full refresh** (`RefreshCommand.ExecuteCoreAsync`): Builds a WIQL query for the current sprint, fetches sprint items via `FetchBatchAsync`, fetches the active item and its children, hydrates ancestors, syncs the working set, then syncs metadata (process types, field definitions, type appearances, global profile).

3. **Single-item set sync** (`SyncCoordinator.SyncItemSetAsync`): Used by `SetCommand` to sync the target item plus its parent chain (`[item.Id, ..parentChainIds]`). `SyncItemAsync` (single-item by ID) exists but has zero production callers. MCP `ReadTools` calls `SyncLinksAsync` (not `SyncItemAsync`).

All three paths share a single `SyncCoordinator` instance registered as a singleton, constructed with `int cacheStaleMinutes` from `TwigConfiguration.Display.CacheStaleMinutes` (default: 5 minutes). The `HttpClient` is a bare `services.AddSingleton<HttpClient>()` with no custom handler configuration.

### Key Design Decisions Referenced

- **DD-8:** Per-item `LastSyncedAt` timestamps for staleness (not global cache timestamp)
- **DD-13:** `SyncCoordinator` accepts `int cacheStaleMinutes` primitive (not `TwigConfiguration`) to avoid Domain → Infrastructure circular reference
- **DD-15:** `SyncChildrenAsync` always fetches unconditionally (no per-parent staleness check)
- **FR-013:** Working-set sync does NOT evict items
- **NFR-003:** `ProtectedCacheWriter.SaveBatchProtectedAsync` computes protected IDs once internally

### Call-Site Audit

#### SyncCoordinator Constructor Call Sites

| File | Method | Usage | Impact of cacheStaleMinutes change |
|------|--------|-------|-------------------------------------|
| `src/Twig/DependencyInjection/CommandServiceModule.cs:47-53` | `AddTwigCommandServices()` | DI factory, 6-param ctor with `IWorkItemLinkRepository` | Primary: tiered TTL must be injected here |
| `src/Twig.Mcp/Program.cs:52-58` | Host builder | DI factory, 6-param ctor | Must mirror CLI registration pattern |
| 20+ test files | Test setup | 5-param ctor with hardcoded `30` | Tests unaffected (use explicit values) |

#### SyncCoordinator Method Call Sites

| File | Method | API Called | Impact |
|------|--------|-----------|--------|
| `RefreshCommand.cs:229` | `ExecuteCoreAsync` | `SyncWorkingSetAsync(workingSet)` | Batch optimization target |
| `StatusCommand.cs:158,284` | `ExecuteAsync` | `SyncWorkingSetAsync(workingSet)` | Batch optimization target |
| `StatusCommand.cs:125,225` | `ExecuteAsync` | `SyncLinksAsync(itemId)` | Single-item link sync, unaffected |
| `StatusOrchestrator.cs:72` | `SyncWorkingSetAsync` | `SyncWorkingSetAsync(workingSet)` | Batch optimization target |
| `RefreshOrchestrator.cs:135` | `SyncWorkingSetAsync` | `SyncWorkingSetAsync(workingSet)` | Batch optimization target |
| `TreeCommand.cs:139,249` | `ExecuteAsync` | `SyncWorkingSetAsync(workingSet)` | Batch optimization target |
| `TreeCommand.cs:109,124,217` | `ExecuteAsync` | `SyncLinksAsync(itemId)` | Single-item link sync, unaffected |
| `ShowCommand.cs:125` | `ExecuteAsync` | `SyncItemSetAsync([id])` | Batch optimization target |
| `SetCommand.cs:237` | `ExecuteAsync` | `SyncItemSetAsync([id, ...parentChainIds])` | Batch optimization target |
| `SetCommand.cs:158` | `ExecuteAsync` | `SyncLinksAsync(itemId)` | Single-item link sync, unaffected |
| `ReadTools.cs:65` (MCP) | `GetWorkItemLinksAsync` | `SyncLinksAsync(itemId)` | Single-item link sync, unaffected |
| `LinkCommand.cs:153` | `ResyncItemAsync` | `SyncLinksAsync(id)` | Single-item link sync, unaffected |
| (No production callers) | — | `SyncChildrenAsync(parentId)` | DD-15 unconditional fetch; test-only |

#### HttpClient Usage (Singleton)

| File | Class | Usage | Impact |
|------|-------|-------|--------|
| `NetworkServiceModule.cs:38` | DI registration | `services.AddSingleton<HttpClient>()` | Transport optimization target |
| `AdoRestClient.cs:40` | Constructor field | `_http = httpClient` | All ADO REST calls go through `SendAsync` |
| `AdoIterationService.cs:38` | Constructor field | `_http = httpClient` | All iteration/metadata calls |
| `AdoGitClient.cs` | Constructor field | Same pattern | PR/git calls |

#### ProcessTypeSyncService.SyncAsync Call Sites

| File | Line | Context |
|------|------|---------|
| `RefreshCommand.cs:259` | Post-refresh | try-catch wrapper |
| `RefreshOrchestrator.cs:141` | `SyncProcessTypesAsync` | No error handling (caller wraps) |
| `InitCommand.cs:248` | During init | try-catch wrapper |

#### FieldDefinitionSyncService.SyncAsync Call Sites

| File | Line | Context |
|------|------|---------|
| `RefreshCommand.cs:269` | Post-refresh | try-catch wrapper |
| `RefreshOrchestrator.cs:147` | `SyncFieldDefinitionsAsync` | No error handling (caller wraps) |
| `InitCommand.cs:261` | During init | try-catch wrapper |

---

## Problem Statement

Four distinct performance bottlenecks exist in Twig's sync pipeline:

1. **N+1 anti-pattern in `FetchStaleAndSaveAsync`**: Lines 128–151 of `SyncCoordinator.cs` issue N sequential `GetByIdAsync` SQLite queries to check staleness, then fan out N concurrent `FetchAsync` HTTP calls via `Task.WhenAll`. For a working set of 30 items, this means 30 SQLite round-trips and up to 30 parallel HTTP GET requests — despite `GetByIdsAsync` (batch SELECT with WHERE IN) and `FetchBatchAsync` (single HTTP GET with CSV IDs, chunked ≤200) already existing in the codebase.

2. **Sequential network calls in refresh**: `RefreshOrchestrator.FetchItemsAsync` issues three ADO calls sequentially: (a) `FetchBatchAsync(realIds)` for sprint items, (b) `FetchAsync(activeId)` for the active item (if outside sprint), (c) `FetchChildrenAsync(activeId)` for children. Calls (b) and (c) are independent of (a)'s result but wait for it to complete. Post-refresh metadata syncs (`ProcessTypeSyncService.SyncAsync`, `FieldDefinitionSyncService.SyncAsync`) are also sequential despite being independent. Additionally, `RefreshCommand.ExecuteCoreAsync` contains ~118 lines (lines 108–225) of inline fetch/save/conflict logic that duplicates `RefreshOrchestrator.FetchItemsAsync` — the orchestrator exists and is DI-registered but `RefreshCommand` does not delegate to it in production code.

3. **Uniform cache TTL**: All commands share a single `CacheStaleMinutes = 5` value. Read-only display commands (`status`, `tree`, `show`) that call `SyncWorkingSetAsync` trigger network requests every 5 minutes even though users would tolerate 10–15 minutes of staleness for display. Mutating commands (`set`) that call `SyncItemSetAsync` need aggressive freshness. There's no mechanism to differentiate. (Note: `StatusCommand` calls both `SyncWorkingSetAsync` and `SyncLinksAsync` — see [Component 3: Tiered Cache TTL](#component-3-tiered-cache-ttl-issue-1614).)

4. **No HTTP transport optimizations**: `HttpClient` is registered as `services.AddSingleton<HttpClient>()` with zero configuration. ADO REST API responses average 5–20 KB per item but support gzip/Brotli compression and HTTP/2 multiplexing. None of these are enabled. Metadata endpoints (`GetWorkItemTypesWithStatesAsync`, `GetProcessConfigurationAsync`, `GetFieldDefinitionsAsync`) return data that changes only when the process template changes — ideal candidates for in-memory response caching to avoid repeat HTTP calls within the same CLI invocation.

---

## Goals and Non-Goals

### Goals

1. **G1:** Replace N SQLite queries in `FetchStaleAndSaveAsync` with 1 batch `GetByIdsAsync` call
2. **G2:** Replace N HTTP `FetchAsync` fan-out calls with 1 `FetchBatchAsync` call (chunked ≤200)
3. **G3:** Parallelize independent ADO calls in refresh (active item + children concurrent with sprint batch post-processing)
4. **G4:** Parallelize post-refresh metadata syncs (ProcessTypeSyncService + FieldDefinitionSyncService)
5. **G5:** Consolidate `RefreshCommand` inline logic into `RefreshOrchestrator` to eliminate duplication
6. **G6:** Introduce tiered cache TTLs per command category without changing `SyncCoordinator` constructor signature
7. **G7:** Enable gzip/Brotli `AutomaticDecompression` on `HttpClient`
8. **G8:** Set explicit HTTP/2 preference with HTTP/1.1 downgrade policy
9. **G9:** Extend in-memory response caching to remaining metadata endpoints in `AdoIterationService` (`GetProcessConfigurationAsync`, `GetFieldDefinitionsAsync`)

### Non-Goals

- **NG1:** Changing the user-facing CLI contract (command names, flags, output format)
- **NG2:** Adding new NuGet dependencies
- **NG3:** Introducing an `ISyncCoordinator` interface (SyncCoordinator remains a concrete sealed class)
- **NG4:** Changing the `SyncCoordinator` constructor signature (DD-13 compatibility)
- **NG5:** Modifying eviction behavior (FR-013)
- **NG6:** Implementing server-side caching or CDN
- **NG7:** Optimizing SQLite schema or indexing (out of scope)

---

## Requirements

### Functional Requirements

- **FR-1:** `SyncCoordinator.FetchStaleAndSaveAsync` must use `GetByIdsAsync` for staleness checks and `FetchBatchAsync` for fetching stale items
- **FR-2:** Items confirmed deleted in ADO (not found in batch response) must still be evicted from local cache
- **FR-3:** `RefreshCommand` must delegate fetch/save logic to `RefreshOrchestrator.FetchItemsAsync`
- **FR-4:** Active item fetch and children fetch must run concurrently when both are needed
- **FR-5:** `ProcessTypeSyncService.SyncAsync` and `FieldDefinitionSyncService.SyncAsync` must run concurrently during refresh
- **FR-6:** Tiered TTLs must be configurable per command category (read-only vs mutating)
- **FR-7:** Metadata endpoints (`GetProcessConfigurationAsync`, `GetFieldDefinitionsAsync`) must cache responses in-memory for the process lifetime, using a `Task<T>?` field pattern consistent with the existing `_workItemTypesCache` in `AdoIterationService` (line 24: `Task<AdoWorkItemTypeListResponse?>?`). Cache field types must match return types: `Task<ProcessConfigurationData>?` and `Task<IReadOnlyList<FieldDefinition>>?` (non-nullable inner types).
- **FR-8:** `HttpClient` must request gzip and Brotli decompression

### Non-Functional Requirements

- **NFR-1:** Zero behavioral change for existing CLI commands (same output, same exit codes)
- **NFR-2:** AOT-compatible (`PublishAot=true`, `TrimMode=full`, `InvariantGlobalization=true`)
- **NFR-3:** No reflection-based JSON serialization (all new DTOs added to `TwigJsonContext`)
- **NFR-4:** `TreatWarningsAsErrors=true` compliance
- **NFR-5:** All existing tests must pass without modification (except test setup changes for new signatures)

---

## Proposed Design

### Architecture Overview

The optimization targets four independent layers of the stack, each addressed by a separate Issue:

```
┌─────────────────────────────────────────────────────────────┐
│  CLI Commands (RefreshCommand, StatusCommand, TreeCommand)   │
│  Issue #1614: Tiered TTL → pass category-specific minutes   │
├─────────────────────────────────────────────────────────────┤
│  Domain Services (SyncCoordinator, RefreshOrchestrator)      │
│  Issue #1612: Batch SQLite + batch HTTP in FetchStaleAndSave│
│  Issue #1613: Parallelize ADO calls, consolidate refresh    │
├─────────────────────────────────────────────────────────────┤
│  Infrastructure (HttpClient, AdoRestClient, AdoIterService)  │
│  Issue #1616: Compression, HTTP/2, in-memory caching   │
└─────────────────────────────────────────────────────────────┘
```

_Issue numbers reference child Issues defined in [ADO Work Item Structure](#ado-work-item-structure) below: #1612 (batch sync), #1613 (parallel refresh), #1614 (tiered TTL), #1616 (HTTP transport). #1615 is not part of this epic — the gap in numbering is an ADO artifact; #1615 belongs to a separate, unrelated work stream._

### Key Components

#### Component 1: Batch Sync in SyncCoordinator (Issue #1612)

**Current (N+1):**
```csharp
// N sequential SQLite queries
foreach (var id in candidateIds)
{
    var existing = await _workItemRepo.GetByIdAsync(id, ct);
    if (existing?.LastSyncedAt is null || ...) staleIds.Add(id);
}
// N concurrent HTTP calls
var fetchResults = await Task.WhenAll(staleIds.Select(id => _adoService.FetchAsync(id, ct)));
```

**Proposed (batch):**
```csharp
// 1 SQLite query
var existingItems = await _workItemRepo.GetByIdsAsync(candidateIds, ct);
var existingMap = existingItems.ToDictionary(x => x.Id);
var staleIds = candidateIds.Where(id =>
    !existingMap.TryGetValue(id, out var existing) ||
    existing.LastSyncedAt is null ||
    DateTimeOffset.UtcNow - existing.LastSyncedAt.Value >= threshold
).ToList();

// 1 HTTP request (chunked ≤200)
var fetchedItems = await _adoService.FetchBatchAsync(staleIds, ct);
```

**Error handling change:** The batch API returns successfully-fetched items only. Items present in `staleIds` but absent from `fetchedItems` (by comparing IDs) are treated as "not found" and evicted. Network/auth errors still throw and are caught by the existing `catch` block in `SyncWorkingSetAsync` / `SyncItemSetAsync`.

#### Component 2: Parallel Refresh + Consolidation (Issue #1613)

**Current flow in RefreshCommand.ExecuteCoreAsync (sequential):**
```
WIQL query → FetchBatchAsync(sprint) → FetchAsync(active) → FetchChildrenAsync(active) →
save → ancestors → working set → user name → appearances →
ProcessTypeSyncService → FieldDefinitionSyncService → profile → timestamp
```

**Proposed flow (delegated + parallel):**
```
WIQL query → RefreshOrchestrator.FetchItemsAsync(wiql, force) →
                  ├─ FetchBatchAsync(sprint)
                  ├─ FetchAsync(active)        ← only if activeId ∉ sprint
                  ├─ FetchChildrenAsync(active) ← always (concurrent with above via Task.WhenAll)
                  └─ save → conflicts
ancestors → working set (remain sequential — depend on saved data)
Task.WhenAll(ProcessTypeSyncService, FieldDefinitionSyncService) →
profile → timestamp
```

> **Conditional gate:** When `activeId` is already inside the sprint batch (`realIds.Contains(activeId.Value)`), only `FetchChildrenAsync` runs — no `Task.WhenAll` needed since `FetchAsync` is skipped. The parallelization applies only when both calls are needed (active item outside sprint).

`RefreshCommand` will be reduced to: WIQL building, delegating to `RefreshOrchestrator.FetchItemsAsync(wiql, force, ct)`, and post-refresh metadata/UI logic. The ~118 lines of inline fetch/save/conflict-detection code (lines 108–225) will be removed.

**RefreshOrchestrator interface contract:**
- `FetchItemsAsync(string wiql, bool force, CancellationToken ct)` — existing signature, unchanged
- WIQL building stays in `RefreshCommand` (depends on Infrastructure config: iteration path, area paths, sort order)
- `RefreshCommand` passes the constructed WIQL string into `FetchItemsAsync`; the orchestrator calls `QueryByWiqlAsync` internally
- Return type `RefreshFetchResult` provides `ItemCount`, `Conflicts`, `PhantomsCleansed` — sufficient for RefreshCommand's post-fetch UI logic

#### Component 3: Tiered Cache TTL (Issue #1614)

**Design principle:** The `SyncCoordinator` constructor signature must not change (DD-13). Instead, tiered TTLs are implemented at the DI registration site in `CommandServiceModule.cs`.

**Approach:** Add a `CacheStaleTier` configuration to `DisplayConfig`:

```csharp
public sealed class DisplayConfig
{
    public int CacheStaleMinutes { get; set; } = 5;          // default / mutating
    public int CacheStaleMinutesReadOnly { get; set; } = 15; // display commands
}
```

Register two named `SyncCoordinator` instances — one for read-only commands (15 min) and one for mutating commands (5 min). However, since DI doesn't support named registrations without a factory pattern and `SyncCoordinator` is a concrete class, we instead use a **factory approach**:

**Chosen approach — SyncCoordinatorFactory:**

```csharp
public sealed class SyncCoordinatorFactory
{
    private readonly SyncCoordinator _readOnly;
    private readonly SyncCoordinator _readWrite;

    public SyncCoordinatorFactory(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ProtectedCacheWriter protectedCacheWriter,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository? linkRepo,
        int readOnlyStaleMinutes,
        int readWriteStaleMinutes)
    {
        _readOnly = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readOnlyStaleMinutes);
        _readWrite = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readWriteStaleMinutes);
    }

    public SyncCoordinator ReadOnly => _readOnly;
    public SyncCoordinator ReadWrite => _readWrite;
}
```

Commands that currently inject `SyncCoordinator` will instead inject `SyncCoordinatorFactory` and use `.ReadOnly` or `.ReadWrite` as appropriate. The `SyncCoordinator` class itself is unchanged — DD-13 fully preserved.

**Command classification:**

| Category | Commands | TTL | Coordinator |
|----------|----------|-----|-------------|
| Read-only | `TreeCommand`, `ShowCommand` | `CacheStaleMinutesReadOnly` (15) | `factory.ReadOnly` |
| Display+Links | `StatusCommand` | `CacheStaleMinutesReadOnly` (15) | `factory.ReadOnly` |
| Mutating | `SetCommand`, `RefreshCommand` | `CacheStaleMinutes` (5) | `factory.ReadWrite` |
| Pass-through | `LinkCommand` | `CacheStaleMinutes` (5) | `factory.ReadWrite` |

**StatusCommand tier rationale:** `StatusCommand` calls both `SyncWorkingSetAsync` (working set sync) and `SyncLinksAsync` (link cache sync). Both are cache-warming operations for display purposes — neither represents a user mutation. `SyncLinksAsync` writes fetched link data to the local cache but this is semantically read-only from the user's perspective: the user is viewing status, not editing items. Therefore both calls use the `ReadOnly` tier (15 min tolerance). If a user needs truly fresh link data, `twig refresh` (which uses `ReadWrite`) provides that path.

Orchestrators (`StatusOrchestrator`, `RefreshOrchestrator`) will accept the factory and use the appropriate tier internally.

#### Component 4: HTTP Transport Optimizations (Issue #1616)

**4a. Automatic Decompression:**

Replace `services.AddSingleton<HttpClient>()` in `NetworkServiceModule.cs` with:

```csharp
services.AddSingleton<HttpClient>(_ =>
{
    var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
    };
    return new HttpClient(handler);
});
```

This is AOT-safe — `SocketsHttpHandler` and `DecompressionMethods` are in `System.Net.Http`.

**4b. HTTP/2 with Fallback:**

```csharp
var handler = new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
};
var client = new HttpClient(handler)
{
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
};
```

**4c. In-Memory Response Caching for Metadata Endpoints:**

`AdoIterationService` already caches `_workItemTypesCache` (line 24) as a `Task<T>?` field (`Task<AdoWorkItemTypeListResponse?>?`), using null-coalescing assignment (`_workItemTypesCache ??= FetchWorkItemTypesAsync(ct)`) to deduplicate concurrent requests. Extend this same `Task<T>?` pattern to `GetProcessConfigurationAsync` and `GetFieldDefinitionsAsync`:

```csharp
private Task<ProcessConfigurationData>? _processConfigCache;
private Task<IReadOnlyList<FieldDefinition>>? _fieldDefinitionsCache;
```

Note: The inner types are **non-nullable** because `GetProcessConfigurationAsync` returns `Task<ProcessConfigurationData>` (returns an empty `ProcessConfigurationData` on error, never null) and `GetFieldDefinitionsAsync` returns `Task<IReadOnlyList<FieldDefinition>>` (returns `Array.Empty<FieldDefinition>()` on error, never null). Using nullable inner types (e.g., `Task<ProcessConfigurationData?>?`) would produce CS8619 nullable warnings promoted to errors under `TreatWarningsAsErrors=true`.

Each method checks its cache field before making an HTTP call and assigns the fetch task on first invocation. This is per-process-lifetime (singletons), using the same `Task<T>?` deduplication pattern as `_workItemTypesCache`. No `SendConditionalAsync`, no ETag headers, no 304 handling — the in-memory cache prevents repeat HTTP calls entirely without additional HTTP round-trips.

> **Note:** ETag-based conditional GETs were considered but rejected. ETags only provide value across process invocations (avoiding re-download when data is unchanged). Since the CLI is short-lived and ETags would not be persisted to SQLite, they offer zero benefit over in-memory caching for the same-invocation case, at ~110 LoC of added complexity (`SendConditionalAsync`, ETag dict, response body dict, 304 handling, and tests).

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Batch fetch instead of per-item error isolation | `FetchBatchAsync` is all-or-nothing per chunk. Missing items (deleted in ADO) are detected by comparing returned IDs against requested IDs. This loses per-item error messages but gains ~97% HTTP reduction (for a 30-item working set: 30 calls → 1). Acceptable because the batch approach eliminates the partial-success scenario entirely: either the HTTP call succeeds (all items returned minus deleted ones) or it fails (exception thrown). `SyncResult.PartiallyUpdated` will no longer be produced by `FetchStaleAndSaveAsync`. **Dead code cleanup:** The `PartiallyUpdated` branch in `SpectreRenderer.RenderWithSyncAsync` (line 1078) becomes unreachable and must be removed to avoid potential unreachable-code warnings under strict analyzer config (`TreatWarningsAsErrors=true`). |
| Eviction semantics change (batch) | **Behavioral change:** The current per-item approach distinguishes "deleted" (exception message contains "not found") from other fetch errors (network, permission, deserialization) — deleted items are evicted while other failures are reported individually. The proposed batch approach conflates these: items present in `staleIds` but absent from `fetchedItems` are treated as "not found" and evicted, regardless of the actual reason for absence. **Mitigation:** ADO's batch endpoint (`_apis/wit/workitems?ids=...`) returns all items the caller has permission to see; items missing from the response are genuinely deleted or the caller lacks read permission. Permission-denied items would have also failed in the per-item approach (thrown AdoException), so the behavioral difference is minimal. This semantic change is documented here and in the acceptance criteria. |
| Factory pattern for tiered TTLs | Avoids changing `SyncCoordinator` constructor (DD-13), avoids introducing an interface (NG3), and keeps the Domain layer clean. Commands inject the factory and pick the appropriate tier. |
| In-memory caching over ETag conditional GETs | ETags only benefit cross-invocation scenarios where the server returns unchanged data. Since the CLI is short-lived and ETags would not be persisted, they provide zero benefit over simple in-memory response caching for same-invocation repeat calls. Extending the existing `_workItemTypesCache` `Task<T>?` pattern achieves the same result at ~110 LoC less. |
| Consolidate into RefreshOrchestrator (not new class) | `RefreshOrchestrator` already exists, is DI-registered, has the right dependencies, and has test coverage. Using it eliminates the duplication rather than creating a new abstraction. |
| `CacheStaleMinutesReadOnly` default value | **15 minutes.** Provides meaningful staleness relief for display commands without being so stale that users notice. Tunable via `twig config display.cachestaleminutes_readonly <value>`. |
| `RefreshCommand` inline logic retention | **WIQL building stays in `RefreshCommand`** (depends on Infrastructure config: iteration path, area paths, sort order). Fetch/save/conflict logic delegates to `RefreshOrchestrator.FetchItemsAsync(wiql, force, ct)`. |

---

## Alternatives Considered

### For Tiered TTL

**Alternative A: Method parameter override.** Add an optional `int? overrideCacheStaleMinutes` parameter to `SyncWorkingSetAsync` and `SyncItemSetAsync`. Commands pass their preferred TTL at call time.
- *Pros:* No new types, no factory.
- *Cons:* Changes `SyncCoordinator` public API. Every caller must know and pass the right value. Increases cognitive load. Rejected.

**Alternative B: Two DI registrations with marker types.** Register `SyncCoordinator` twice using wrapper types like `ReadOnlySyncCoordinator` and `ReadWriteSyncCoordinator`.
- *Pros:* Strong typing at injection site.
- *Cons:* Requires new types in Domain layer, pollutes the type hierarchy, wrapper classes add no value. Rejected.

**Chosen: Factory pattern (Component 3 above).** Clean separation, no Domain changes, DD-13 preserved.

### For Batch Error Handling

**Alternative A: Hybrid approach.** Use `FetchBatchAsync` for the happy path, fall back to individual `FetchAsync` for items that fail.
- *Pros:* Maintains per-item error isolation.
- *Cons:* Complex branching, two code paths, only marginally better than current approach on error. Rejected — batch failures are rare (network/auth errors affect all items equally).

**Chosen: Compare-and-evict.** Compare returned IDs against requested IDs. Missing items are evicted. Simple, deterministic.

---

## Dependencies

### External Dependencies
- Azure DevOps REST API v7.1 (existing — no version change)
- .NET 10 `SocketsHttpHandler` (existing runtime, no new packages)

### Internal Dependencies
- `IWorkItemRepository.GetByIdsAsync` (already exists — batch SELECT with WHERE IN)
- `IAdoWorkItemService.FetchBatchAsync` (already exists — CSV IDs, chunked ≤200)
- `ProtectedCacheWriter.SaveBatchProtectedAsync` (already exists)
- `RefreshOrchestrator` (already exists, DI-registered, tested)

### Sequencing Constraints
- Issue #1616 (HTTP transport) has no dependencies — can start immediately
- Issue #1612 (batch sync) has no dependencies — can start immediately
- Issue #1613 (parallel refresh) depends on #1612 being merged — primarily to avoid merge conflicts in `SyncCoordinator.cs` (both Issues modify this file), and secondarily because `RefreshCommand` calls `SyncWorkingSetAsync` which benefits from the batch pattern
- Issue #1614 (tiered TTL) depends on #1612 being merged — for merge conflict avoidance only. `SyncCoordinatorFactory` wraps `SyncCoordinator` via its existing constructor regardless of internal batching, but both Issues modify `SyncCoordinator`-adjacent code and share 31 test files

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `SyncCoordinator` | Internal refactor (FetchStaleAndSaveAsync) | Medium — core sync logic |
| `RefreshCommand` | Major refactor (delegate to orchestrator) | Medium — removes ~118 lines |
| `RefreshOrchestrator` | Internal refactor (parallelize fetches) | Low — isolated change |
| `NetworkServiceModule` | HttpClient configuration | Low — additive |
| `AdoIterationService` | Extend in-memory response caching (`Task<T>?` pattern) | Low — additive |
| `CommandServiceModule` | Factory registration | Low — DI wiring |
| `StatusOrchestrator` | Inject factory instead of coordinator | Low — trivial |
| `DisplayConfig` | Add `CacheStaleMinutesReadOnly` property | Low — additive |

### Backward Compatibility
- **CLI contract:** Unchanged. Same commands, same flags, same output formats.
- **Configuration:** New `display.cachestaleminutes_readonly` config key. Existing `display.cachestaleminutes` unchanged. Missing key uses default (15 min).
- **Test impact:** 31 test files (with 38 instantiation sites) reference `SyncCoordinator` directly. **Unit tests** that construct `SyncCoordinator` with explicit `cacheStaleMinutes` and test the coordinator directly (e.g., `SyncCoordinatorTests`) need batch-behavior updates but not factory changes. **Command-level and integration tests** that inject `SyncCoordinator` into commands will need to inject `SyncCoordinatorFactory` instead — approximately 24 command test files (`StatusCommandTests`, `TreeCommandTests`, `ShowCommandTests`, `SetCommandTests`, `LinkCommandTests`, `RefreshCommandTests`, `RefreshDirtyGuardTests`, `SyncCommandTests`, `OfflineModeTests`, `PromptStateIntegrationTests`, `CacheFirstReadCommandTests`, `*_CacheAwareTests`, `TreeCommandLinkTests`, `TreeNavCommandTests`, `NavigationCommandsInteractiveTests`, `NextPrevCommandTests`, `WorkingSetCommandTests`, `CommandFormatterWiringTests`, etc.) plus `StatusOrchestratorTests`, `RefreshOrchestratorTests`, and `CacheRefreshTests`.

### Performance Implications
- Working-set sync (30 items): 30 HTTP → 1 HTTP (~97% reduction), 30 SQLite → 1 SQLite
- Refresh wall-clock: ~3 sequential ADO calls → 1 sequential + 1 parallel pair; 2 sequential metadata syncs → 1 parallel pair
- HTTP bandwidth: gzip/Brotli compression reduces payload by ~60-80%
- Metadata endpoints: in-memory `Task<T>?` caching eliminates repeat HTTP calls within the same CLI invocation

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Batch fetch masks per-item errors | Low | Low | Compare returned IDs against requested; evict missing items. Network/auth errors still throw. See "Eviction semantics change" design decision for full behavioral analysis. |
| ADO API doesn't support HTTP/2 | Low | Low | `RequestVersionOrLower` policy falls back to HTTP/1.1 transparently |
| Factory injection changes break test setup | Medium | Medium | 31 test files (38 instantiation sites) reference `SyncCoordinator`; ~27 need factory migration. Mitigated by mechanical find-and-replace pattern — each test constructs a `SyncCoordinatorFactory` wrapping two `SyncCoordinator` instances with explicit TTLs. |
| Parallel fetch changes error ordering | Low | Low | Conflict detection logic is order-independent (checks protectedIds set) |
| Thread-safety of `??=` cache pattern | Low | Medium | The `_cache ??= FetchAsync(ct)` pattern is not atomic; concurrent callers could both start fetching. This is pre-existing for `_workItemTypesCache` (line 321, comment: "safe because CLI is single-threaded") but becomes more relevant with G4's metadata parallelization. **Mitigation:** `ProcessTypeSyncService.SyncAsync` calls `GetWorkItemTypesWithStatesAsync` and `GetProcessConfigurationAsync`; `FieldDefinitionSyncService.SyncAsync` calls `GetFieldDefinitionsAsync`. When parallelized, these target *different* cache fields, so no race occurs on the same field. The pre-existing `_workItemTypesCache` field could theoretically race if both services call `GetWorkItemTypesWithStatesAsync`, but `FieldDefinitionSyncService` does not. Risk accepted as-is. |
| Eviction of permission-denied items | Low | Low | ADO batch endpoint returns all items the caller can read. If an item is genuinely permission-denied, the per-item approach would also have failed (thrown exception, not evicted). The batch approach evicts instead. Acceptable because permission changes mid-session are rare and the item would be re-fetched on next sync. |

---

## Open Questions

None at this time. All design decisions are resolved:

- **ETag vs in-memory caching:** Resolved in favor of in-memory `Task<T>?` pattern (see Design Decisions — ~110 LoC savings, zero benefit from ETags in a short-lived CLI process).
- **Factory vs named DI registrations:** Resolved in favor of `SyncCoordinatorFactory` (see Alternatives Considered).
- **Thread-safety of `??=` cache pattern:** Accepted as-is — parallelized metadata syncs target different cache fields, so no same-field race occurs (see Risks and Mitigations).

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/SyncCoordinatorFactory.cs` | Factory providing read-only and read-write `SyncCoordinator` instances |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/SyncCoordinator.cs` | Refactor `FetchStaleAndSaveAsync`: replace N `GetByIdAsync` with 1 `GetByIdsAsync`, replace N `FetchAsync` with 1 `FetchBatchAsync`, update not-found detection to compare-and-evict. `SyncResult.PartiallyUpdated` is no longer produced. |
| `src/Twig.Domain/Services/SyncResult.cs` | Remove `PartiallyUpdated` variant and `SyncItemFailure` record (dead code after batch refactor) |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | Parallelize `FetchAsync(activeId)` + `FetchChildrenAsync(activeId)` using `Task.WhenAll`; change `SyncCoordinator` to `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig/Commands/RefreshCommand.cs` | Remove ~118 lines (108–225) of inline fetch/save/conflict logic; delegate to `RefreshOrchestrator.FetchItemsAsync(wiql, force, ct)`; parallelize `ProcessTypeSyncService` + `FieldDefinitionSyncService`; inject `SyncCoordinatorFactory` |
| `src/Twig/Commands/StatusCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` for both `SyncWorkingSetAsync` and `SyncLinksAsync` |
| `src/Twig/Commands/TreeCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig/Commands/ShowCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig/Commands/SetCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig/Commands/LinkCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig.Domain/Services/StatusOrchestrator.cs` | Change `SyncCoordinator` to `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `SyncCoordinatorFactory`; remove bare `SyncCoordinator` registration |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Replace bare `HttpClient` with `SocketsHttpHandler` (decompression + HTTP/2) |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `CacheStaleMinutesReadOnly` to `DisplayConfig` with default 15; add config key parsing |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Add `Task<ProcessConfigurationData>?` and `Task<IReadOnlyList<FieldDefinition>>?` cache fields; wrap `GetProcessConfigurationAsync` and `GetFieldDefinitionsAsync` with `??=` pattern |
| `src/Twig/Rendering/SpectreRenderer.cs` | Remove `SyncResult.PartiallyUpdated` branch (line 1078) — dead code after batch refactor |
| `src/Twig.Mcp/Program.cs` | Register `SyncCoordinatorFactory` with both tiers set to `CacheStaleMinutes` (read-write TTL — MCP has no read-only commands). Update `RefreshOrchestrator` (line 69) and `StatusOrchestrator` (line 81) registrations to resolve `SyncCoordinatorFactory`. Register `SyncCoordinator` as `factory.ReadWrite` for direct consumers (`ReadTools`). |
| `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | Update tests for batch behavior, add batch-eviction tests, remove `PartiallyUpdated` assertions |
| `tests/Twig.Domain.Tests/Services/RefreshOrchestratorTests.cs` | Add tests for parallel fetch, update for factory injection |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Update for delegated orchestrator pattern, factory injection |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/LinkCommandTests.cs` | Update for factory injection |
| `tests/Twig.Domain.Tests/Services/StatusOrchestratorTests.cs` | Update for factory injection |
| `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Add in-memory caching tests (second call returns cached value, no second HTTP call) |
| `tests/Twig.Cli.Tests/Commands/CacheFirstReadCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/CommandFormatterWiringTests.cs` | Update for factory injection (3 instantiation sites) |
| `tests/Twig.Cli.Tests/Commands/NavigationCommandsInteractiveTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/NextPrevCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/OfflineModeTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | Update for factory injection (3 instantiation sites) |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandDeprecationTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandProfileTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/RefreshDirtyGuardTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/SyncCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommand_CacheAwareTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/WorkingSetCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Rendering/CacheRefreshTests.cs` | Update for factory injection (2 instantiation sites) |

---

## ADO Work Item Structure

### Epic #1611: Sync Performance Optimization (existing)

> **Note:** Issue #1615 is not part of this epic — the gap in numbering (#1614 → #1616) is an ADO artifact; #1615 belongs to a separate, unrelated work stream.

---

### Issue #1612: Replace N+1 FetchAsync with FetchBatchAsync in SyncCoordinator (existing)

**Goal:** Eliminate the N+1 anti-pattern in `SyncCoordinator.FetchStaleAndSaveAsync` by replacing N sequential `GetByIdAsync` calls with 1 `GetByIdsAsync` batch call, and N concurrent `FetchAsync` HTTP calls with 1 `FetchBatchAsync` call.

**Prerequisites:** None — this is the foundational change.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1612-1 | Replace N `GetByIdAsync` staleness checks with batch `GetByIdsAsync`. Satisfies: G1, FR-1 | `SyncCoordinator.cs` | ~30 LoC |
| T-1612-2 | Replace N `FetchAsync` fan-out with `FetchBatchAsync` + compare-and-evict for not-found detection. Satisfies: G2, FR-1, FR-2 | `SyncCoordinator.cs` | ~40 LoC |
| T-1612-3 | Remove dead code: delete `SyncResult.PartiallyUpdated` variant and `SyncItemFailure` from `SyncResult.cs`; remove `PartiallyUpdated` branch in `SpectreRenderer.RenderWithSyncAsync` (line 1078) — this branch becomes unreachable after batch refactor and will trigger warnings under `TreatWarningsAsErrors=true`. Satisfies: NFR-4 | `SyncResult.cs`, `SpectreRenderer.cs` | ~-25 LoC |
| T-1612-4 | Update `SyncCoordinatorTests` for batch behavior; add not-found eviction tests; remove `PartiallyUpdated` test assertions. Satisfies: NFR-5 | `SyncCoordinatorTests.cs` | ~80 LoC |

**Acceptance Criteria:**
- [ ] `FetchStaleAndSaveAsync` issues exactly 1 `GetByIdsAsync` call (not N `GetByIdAsync`)
- [ ] `FetchStaleAndSaveAsync` issues exactly 1 `FetchBatchAsync` call (not N `FetchAsync`)
- [ ] Items deleted in ADO (present in staleIds but absent from batch response) are evicted from cache
- [ ] `SyncResult.PartiallyUpdated` variant and `SyncItemFailure` record removed from `SyncResult.cs`
- [ ] `PartiallyUpdated` branch removed from `SpectreRenderer.RenderWithSyncAsync` (no dead code)
- [ ] All existing `SyncCoordinatorTests` pass (updated for batch behavior)
- [ ] New tests verify batch staleness check and not-found eviction

---

### Issue #1613: Parallelize network calls and deduplicate refresh logic (existing)

**Goal:** Parallelize independent ADO fetch calls in `RefreshOrchestrator.FetchItemsAsync`, parallelize post-refresh metadata syncs in `RefreshCommand`, and consolidate `RefreshCommand` inline logic that duplicates `RefreshOrchestrator`.

**Prerequisites:** Issue #1612 merged — primarily to avoid merge conflicts in `SyncCoordinator.cs` (both Issues modify this file), and secondarily because `RefreshCommand` calls `SyncWorkingSetAsync` which benefits from the batch pattern.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1613-1 | Parallelize `FetchAsync(activeId)` + `FetchChildrenAsync(activeId)` in `RefreshOrchestrator.FetchItemsAsync` using `Task.WhenAll`. Satisfies: G3, FR-4 | `RefreshOrchestrator.cs` | ~20 LoC |
| T-1613-2 | Remove inline fetch/save/conflict logic (~118 lines, 108–225) from `RefreshCommand.ExecuteCoreAsync`; delegate to `RefreshOrchestrator.FetchItemsAsync(wiql, force, ct)`. WIQL building stays in `RefreshCommand`. Satisfies: G5, FR-3 | `RefreshCommand.cs` | ~-118 LoC (net removal) |
| T-1613-3 | Parallelize `ProcessTypeSyncService.SyncAsync` + `FieldDefinitionSyncService.SyncAsync` in `RefreshCommand` post-refresh section. Satisfies: G4, FR-5 | `RefreshCommand.cs` | ~15 LoC |
| T-1613-4 | Update `RefreshOrchestratorTests` for parallel fetch verification; update `RefreshCommandTests` for delegated pattern. Satisfies: NFR-5 | `RefreshOrchestratorTests.cs`, `RefreshCommandTests.cs` | ~60 LoC |

**Acceptance Criteria:**
- [ ] `RefreshOrchestrator.FetchItemsAsync` runs active item fetch and children fetch concurrently
- [ ] `RefreshCommand.ExecuteCoreAsync` no longer contains inline fetch/save/conflict logic
- [ ] `ProcessTypeSyncService` and `FieldDefinitionSyncService` run concurrently during refresh
- [ ] All existing `RefreshCommandTests` and `RefreshOrchestratorTests` pass
- [ ] Total line count of `RefreshCommand.cs` reduced by ≥100 lines

---

### Issue #1614: Tiered cache TTL for read-only vs read-write commands (existing)

**Goal:** Introduce tiered cache TTLs so read-only display commands tolerate longer staleness (15 min) while mutating commands maintain aggressive freshness (5 min), without changing the `SyncCoordinator` constructor signature.

**Prerequisites:** Issue #1612 merged — for merge conflict avoidance only. `SyncCoordinatorFactory` wraps `SyncCoordinator` via its existing constructor regardless of internal batching, but both Issues modify `SyncCoordinator`-adjacent code and share test files.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1614-1 | Add `CacheStaleMinutesReadOnly` property to `DisplayConfig` with default 15; add config key parsing in `SetValue`. Satisfies: G6, FR-6 | `TwigConfiguration.cs` | ~15 LoC |
| T-1614-2 | Create `SyncCoordinatorFactory` with `ReadOnly` and `ReadWrite` properties. Satisfies: G6, FR-6 | `SyncCoordinatorFactory.cs` (new) | ~30 LoC |
| T-1614-3 | Update DI registration: CLI (`CommandServiceModule.cs`) — register `SyncCoordinatorFactory`, remove bare `SyncCoordinator` singleton (all CLI consumers migrate in T-1614-4/5). MCP (`Program.cs`) — register `SyncCoordinatorFactory` with both tiers set to `CacheStaleMinutes` (read-write TTL); update `RefreshOrchestrator` (line 69) and `StatusOrchestrator` (line 81) registrations to resolve `SyncCoordinatorFactory`; register `SyncCoordinator` as `factory.ReadWrite` for direct consumers (`ReadTools`). Without this, DI resolution for MCP orchestrators will fail at runtime. Satisfies: G6 | `CommandServiceModule.cs`, `Program.cs` (MCP) | ~25 LoC |
| T-1614-4 | Update read-only commands (`StatusCommand`, `TreeCommand`, `ShowCommand`) and `StatusOrchestrator` to inject factory and use `.ReadOnly`. `StatusCommand` uses `.ReadOnly` for both `SyncWorkingSetAsync` and `SyncLinksAsync` (see StatusCommand tier rationale in design). Satisfies: G6, FR-6 | Multiple command files, `StatusOrchestrator.cs` | ~30 LoC |
| T-1614-5 | Update mutating commands (`SetCommand`, `LinkCommand`, `RefreshCommand`) and `RefreshOrchestrator` to inject factory and use `.ReadWrite`. Satisfies: G6, FR-6 | Multiple command files, `RefreshOrchestrator.cs` | ~25 LoC |
| T-1614-6 | Update affected test files for factory injection pattern. 27 test files need factory migration (24 command tests + 2 orchestrator tests + 1 rendering test); 38 total instantiation sites. Construct `SyncCoordinatorFactory` directly with explicit `readOnlyStaleMinutes` and `readWriteStaleMinutes` parameters (use the same value, e.g. `30`, for both tiers in tests where the distinction doesn't matter). Satisfies: NFR-5 | Multiple test files (see Files Affected) | ~90 LoC |

**Acceptance Criteria:**
- [ ] `SyncCoordinator` constructor signature unchanged (DD-13)
- [ ] Read-only commands use `CacheStaleMinutesReadOnly` (default 15)
- [ ] Mutating commands use `CacheStaleMinutes` (default 5)
- [ ] `twig config display.cachestaleminutes_readonly <value>` works
- [ ] All existing tests pass

---

### Issue #1616: HTTP transport optimizations (compression, HTTP/2, in-memory caching) (existing)

**Goal:** Configure `HttpClient` with gzip/Brotli decompression and HTTP/2 preference, and extend in-memory response caching to remaining metadata endpoints.

**Prerequisites:** None — this is independent of other Issues.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1616-1 | Replace `services.AddSingleton<HttpClient>()` with `SocketsHttpHandler` configuration: `AutomaticDecompression = GZip \| Brotli`, `DefaultRequestVersion = HTTP/2`, `DefaultVersionPolicy = RequestVersionOrLower`. Satisfies: G7, G8, FR-8 | `NetworkServiceModule.cs` | ~15 LoC |
| T-1616-2 | Extend in-memory response caching to `GetProcessConfigurationAsync` and `GetFieldDefinitionsAsync` in `AdoIterationService`, using the existing `Task<T>?` null-coalescing pattern from `_workItemTypesCache`. Cache fields: `Task<ProcessConfigurationData>?` and `Task<IReadOnlyList<FieldDefinition>>?` (non-nullable inner types). Satisfies: G9, FR-7 | `AdoIterationService.cs` | ~15 LoC |
| T-1616-3 | Add tests for in-memory caching: verify second call returns the cached value without issuing a second HTTP call. Satisfies: NFR-5 | `AdoIterationServiceTests.cs` | ~20 LoC |

**Acceptance Criteria:**
- [ ] `HttpClient` has `AutomaticDecompression` set to `GZip | Brotli`
- [ ] `HttpClient` has `DefaultRequestVersion` set to `HTTP/2` with `RequestVersionOrLower` policy
- [ ] `GetProcessConfigurationAsync` and `GetFieldDefinitionsAsync` return cached response on repeat calls within the same process lifetime
- [ ] No new NuGet dependencies
- [ ] AOT-compatible (no reflection)
- [ ] All existing tests pass

---

## PR Groups

| PR Group | Title | Issues/Tasks | Type | Est. LoC | Predecessors |
|----------|-------|-------------|------|----------|--------------|
| PG-1 | HTTP transport optimizations | #1616: T-1616-1, T-1616-2, T-1616-3 | **deep** (few files, complex HTTP logic) | ~45 | None |
| PG-2 | Batch sync in SyncCoordinator | #1612: T-1612-1, T-1612-2, T-1612-3, T-1612-4 | **deep** (few files + tests, core algorithm change) | ~125 | None |
| PG-3 | Parallel refresh + consolidation | #1613: T-1613-1, T-1613-2, T-1613-3, T-1613-4 | **deep** (2 files + tests, orchestration refactor) | ~185 | PG-2 |
| PG-4 | Tiered cache TTL | #1614: T-1614-1 through T-1614-6 | **wide** (1 new file + ~12 modified source files + 27 test files, mechanical DI wiring) | ~210 | PG-2, PG-3 |

**Execution order:**
```
PG-1 ─────────────────────────────┐
                                   ├─→ all merged → Epic #1611 done
PG-2 ──→ PG-3 ──→ PG-4 ──────────┘
```

PG-1 and PG-2 can execute in parallel (no code overlap). PG-2, PG-3, and PG-4 form a linear chain: each pair modifies overlapping files (`SyncCoordinator.cs` and adjacent code for PG-2→PG-3; `RefreshOrchestrator.cs` and test files for PG-3→PG-4), making concurrent development impractical. PG-3 additionally benefits from PG-2's batch sync pattern in `SyncWorkingSetAsync`. PG-1 is independent and can merge at any point.

---

## References

- DD-8: Per-item `LastSyncedAt` staleness — `SyncCoordinator.cs` comments
- DD-13: `int cacheStaleMinutes` primitive to avoid Domain→Infrastructure dependency — `CommandServiceModule.cs:45`
- DD-15: Unconditional children fetch — `SyncCoordinator.SyncChildrenAsync` comments
- FR-013: No eviction during working-set sync — `RefreshCommand.cs:227`
- NFR-003: Batch protected save computes protected IDs once — `ProtectedCacheWriter`
- ADO REST API batch limit: 200 IDs — `AdoRestClient.MaxBatchSize`

