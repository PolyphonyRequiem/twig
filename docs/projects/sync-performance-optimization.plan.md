# Sync Performance Optimization

**Epic:** #1611 — Sync Performance Optimization
**Author:** Copilot
**Status:** Draft
**Revision:** 6 — Addressing reviewer feedback (tech=88, read=91)

---

## Executive Summary

This plan addresses measurable performance bottlenecks in Twig's sync pipeline between the
local SQLite cache and Azure DevOps. Four optimization vectors target distinct layers of the
stack: (1) batch-fetching stale work items in `SyncCoordinator` to collapse N individual HTTP
round-trips into a single batch request; (2) parallelizing independent ADO network calls
during refresh and consolidating duplicated orchestration logic in `RefreshCommand`;
(3) introducing tiered cache TTLs so read-only commands tolerate longer staleness than
mutating commands, reducing unnecessary background syncs; and (4) enabling HTTP transport
optimizations — response compression (gzip/Brotli), explicit HTTP/2 preference, and
conditional GET requests via ETags for metadata endpoints in the long-lived MCP server
process. Together, these changes are expected to reduce HTTP round-trips by ~87% for
working-set syncs, cut refresh wall-clock time by ~66%, and lower API bandwidth
consumption — all without changing the user-facing CLI contract.

## Background

### Current Architecture

Twig's sync subsystem is a layered pipeline:

```
Commands (RefreshCommand, StatusCommand, TreeCommand, SetCommand, …)
    │
    ├─→ RefreshOrchestrator (full refresh pipeline)
    │     ├─→ IAdoWorkItemService.QueryByWiqlAsync()    → WIQL query → IDs
    │     ├─→ IAdoWorkItemService.FetchBatchAsync()     → batch GET by CSV IDs
    │     ├─→ IAdoWorkItemService.FetchAsync()          → single-item GET
    │     ├─→ IAdoWorkItemService.FetchChildrenAsync()  → WIQL + FetchBatchAsync
    │     ├─→ ProtectedCacheWriter.SaveBatchProtectedAsync() → guarded SQLite writes
    │     └─→ SyncCoordinator.SyncWorkingSetAsync()     → stale-item sync
    │
    ├─→ SyncCoordinator (per-item staleness sync)
    │     ├─→ N × GetByIdAsync()      → sequential SQLite staleness checks
    │     ├─→ N × FetchAsync()        → concurrent individual HTTP GETs
    │     └─→ SaveBatchProtectedAsync() → batch SQLite writes
    │
    └─→ AdoRestClient (HTTP transport)
          ├─→ HttpClient (bare singleton, no handler config)
          ├─→ Auth: PAT (Basic) or AzCli (Bearer)
          └─→ API version 7.1
```

**Key design decisions already in place** (from prior plan documents — see References):
- **DD-8** (from `set-command-sync-optimization.plan.md`): Per-item `LastSyncedAt` timestamps for granular staleness
- **DD-13** (from `set-command-sync-optimization.plan.md`): `SyncCoordinator` accepts `int cacheStaleMinutes` primitive (no Domain→Infrastructure dependency)
- **DD-15** (from `cache-aware-rendering.plan.md`): `SyncChildrenAsync` always fetches unconditionally
- **NFR-003** (from `set-command-sync-optimization.plan.md`): `SaveBatchProtectedAsync` computes protected IDs once (no N+1 SyncGuard queries)

### Caching Model

- Single TTL: `CacheStaleMinutes = 5` (configurable via `display.cachestaleminutes`)
- Per-item `last_synced_at` column in SQLite `work_items` table
- Protected items (dirty/pending changes) are never overwritten during sync
- Cache-first rendering: read-only commands render from cache immediately, then sync in background

### HTTP Transport

The `HttpClient` is registered as a bare singleton with **no custom configuration**:

| Feature | Status |
|---------|--------|
| Compression (Accept-Encoding) | ❌ Not configured |
| HTTP/2 | ⚠️ Auto (runtime default) |
| Conditional GET (If-None-Match) | ❌ Not used for reads |
| Optimistic concurrency (If-Match) | ✅ Used for PATCH operations |
| Retry policy | ❌ None (429 detected but not retried) |
| Connection pooling tuning | ❌ Default limits |

### Call-Site Audit: `FetchAsync` (individual HTTP calls)

| File | Method | Line | Purpose | Scope for Batching |
|------|--------|------|---------|-------------------|
| `SyncCoordinator.cs` | `FetchStaleAndSaveAsync` | 141 | Concurrent N×FetchAsync via Task.WhenAll | **Primary target** — replace with FetchBatchAsync |
| `SyncCoordinator.cs` | `SyncItemAsync` | 62 | Single-item sync | No — single item by design |
| `RefreshOrchestrator.cs` | `FetchItemsAsync` | 78 | Active item (not in sprint) | No — single conditional fetch |
| `RefreshCommand.cs` | `ExecuteCoreAsync` | 170 | Active item (not in sprint) | No — single conditional fetch |
| `ActiveItemResolver.cs` | `ResolveByIdAsync` | 51 | Cache miss auto-fetch | No — single item |
| `FlowTransitionService.cs` | various | 117 | Pre-transition freshness check | No — single item |
| `PendingChangeFlusher.cs` | `FlushAsync` | 90, 117 | Post-flush resync | Possible batch after all flushes |
| `ConflictRetryHelper.cs` | `PatchWithRetryAsync` | 30 | 412 conflict recovery | No — single item |
| Various commands | PatchAsync flow | — | Post-PATCH re-fetch | No — single item per mutation |

### Call-Site Audit: `SyncCoordinator` Methods

| File | Method | Call | Notes |
|------|--------|------|-------|
| `RefreshCommand.cs:229` | `ExecuteCoreAsync` | `SyncWorkingSetAsync(workingSet)` | Post-refresh working set sync |
| `RefreshOrchestrator.cs:135` | `SyncWorkingSetAsync` | `SyncWorkingSetAsync(workingSet)` | Delegated from RefreshCommand |
| `StatusOrchestrator.cs:67-72` | `SyncWorkingSetAsync` | `SyncWorkingSetAsync(WorkItem item)` — public method accepts `WorkItem`, internally computes `WorkingSet` from `item.IterationPath`, then calls `_syncCoordinator.SyncWorkingSetAsync(workingSet)` | Background sync for status |
| `StatusCommand.cs:158,284` | `ExecuteCoreAsync` | `SyncWorkingSetAsync(workingSet)` | Two-pass render sync |
| `TreeCommand.cs:139,249` | `ExecuteCoreAsync` | `SyncWorkingSetAsync(workingSet)` | Two-pass render sync |
| `SetCommand.cs:237` | `ExecuteCoreAsync` | `SyncItemSetAsync([id, ...parents])` | Target + parent chain |
| `ShowCommand.cs:125` | `ExecuteCoreAsync` | `SyncItemSetAsync([id])` | Single item sync |
| `StatusCommand.cs:125,225` | `ExecuteCoreAsync` | `SyncLinksAsync(item.Id)` | Link fetch for display |
| `TreeCommand.cs:109,124,217` | `ExecuteCoreAsync` | `SyncLinksAsync(item.Id)` | Link fetch for tree |
| `LinkCommand.cs:153` | `ResyncItemAsync` | `SyncLinksAsync(id)` | Post-mutation resync |
| `ReadTools.cs:65` | `Tree` | `SyncLinksAsync(item.Id)` | MCP tool link sync |

## Problem Statement

Twig's sync pipeline has four distinct performance problems:

1. **N+1 HTTP calls in working-set sync.** `SyncCoordinator.FetchStaleAndSaveAsync` fans out
   `N` concurrent individual `FetchAsync` calls via `Task.WhenAll`. While concurrent, each call
   is a separate HTTP request+response round-trip with TLS overhead, auth header injection,
   and JSON deserialization. The ADO batch endpoint (`/_apis/wit/workitems?ids=1,2,3...`)
   already exists and is used by `RefreshOrchestrator` — but `SyncCoordinator` does not use it.
   For a typical 20-item working set with 8 stale items, this means 8 HTTP requests instead of 1.

2. **Sequential network calls in refresh.** `RefreshCommand.ExecuteCoreAsync` and
   `RefreshOrchestrator.FetchItemsAsync` issue three ADO calls sequentially:
   (a) `FetchBatchAsync(sprintIds)`, (b) `FetchAsync(activeId)`,
   (c) `FetchChildrenAsync(activeId)`. Calls (b) and (c) are independent of (a)'s result and
   can run concurrently. Additionally, post-refresh metadata syncs (`ProcessTypeSyncService`,
   `FieldDefinitionSyncService`) are called sequentially and are independent of each other.

3. **Uniform cache TTL.** All commands use the same 5-minute `CacheStaleMinutes` for staleness
   decisions. Read-only display commands (`status`, `tree`, `show`, `workspace`) would tolerate
   10–15 minutes of staleness without user impact, while mutating commands (`set`, `edit`, `save`)
   benefit from aggressive freshness. The uniform TTL causes unnecessary background syncs on
   every `status`/`tree` invocation.

4. **No HTTP transport optimizations.** The `HttpClient` is a bare singleton with no
   `AutomaticDecompression`, no explicit `HttpVersion` preference, and no conditional GET
   caching. ADO responses are typically 2–10 KB JSON per work item; gzip compression alone
   would reduce bandwidth by 40–60%. HTTP/2 multiplexing would reduce connection setup
   overhead for concurrent requests. ETag-based conditional GETs for metadata endpoints
   (iterations, field definitions, process config) would eliminate redundant payloads entirely
   when data hasn't changed.

## Goals and Non-Goals

### Goals

- **G-1** _(Issue #1612)_: Replace N concurrent `FetchAsync` calls in `SyncCoordinator.FetchStaleAndSaveAsync`
  with `FetchBatchAsync`, reducing HTTP round-trips from N to ⌈N/200⌉ (typically 1).
- **G-2** _(Issue #1612)_: Replace N sequential `GetByIdAsync` staleness checks with a single batch
  `GetByIdsAsync` call, reducing SQLite round-trips from N to 1.
- **G-3** _(Issue #1613)_: Parallelize the three sequential ADO fetch calls (sprint, active, children)
  in `RefreshOrchestrator.FetchItemsAsync` using `Task.WhenAll`, reducing refresh wall-clock time.
- **G-4** _(Issue #1613)_: Parallelize post-refresh metadata syncs (process types + field definitions)
  at the call site in `RefreshCommand.ExecuteCoreAsync` (not inside `RefreshOrchestrator`).
- **G-5** _(Issue #1613)_: Consolidate `RefreshCommand` inline logic that duplicates `RefreshOrchestrator`.
- **G-6** _(Issue #1614)_: Introduce tiered cache TTLs: longer for read-only commands, shorter for mutating commands.
- **G-7** _(Issue #1616)_: Enable gzip/Brotli response decompression on `HttpClient`.
- **G-8** _(Issue #1616)_: Set explicit HTTP/2 preference with HTTP/1.1 fallback.
- **G-9** _(Issue #1616)_: Implement conditional GET (If-None-Match) for metadata endpoints that rarely change.

### Non-Goals

- **NG-1**: Connection pooling tuning — .NET's default `SocketsHttpHandler` limits are adequate
  for CLI workloads.
- **NG-2**: Adding Polly or a resilience library — 429/5xx retries are a separate concern.
  The current ADO rate-limit detection throws `AdoRateLimitException`; retry wrapping is out of scope.
- **NG-3**: Persistent HTTP caching (on-disk response cache) — beyond ETag freshness checks,
  full response caching adds complexity for marginal CLI benefit.
- **NG-4**: Changing the cache-first rendering pattern — the two-pass `RenderWithSyncAsync`
  approach is architecturally sound and not a bottleneck.
- **NG-5**: Changing the `SyncCoordinator` public API contract — callers continue to use the
  same methods; internal implementation changes only.

## Requirements

### Functional

- **FR-1**: `SyncCoordinator.FetchStaleAndSaveAsync` must use `FetchBatchAsync` for stale items
  instead of N×`FetchAsync`. Items that ADO returns 404 for must still be evicted correctly.
- **FR-2**: The staleness check loop must be replaced with a batch query that returns all
  items with their `LastSyncedAt` in one database call.
- **FR-3**: `RefreshOrchestrator.FetchItemsAsync` must run FetchBatchAsync (sprint),
  FetchAsync (active), and FetchChildrenAsync (children) concurrently where possible.
- **FR-4**: Post-refresh metadata syncs (ProcessTypeSyncService, FieldDefinitionSyncService)
  must run concurrently.
- **FR-5**: Cache TTL must support per-command-category overrides without changing the
  `SyncCoordinator` constructor signature (DD-13 compatibility).
- **FR-6**: HTTP responses from ADO must be decompressed automatically (gzip/Brotli).
- **FR-7**: Metadata GET requests (iteration, fields, process config) should use
  If-None-Match when a cached ETag is available.
- **FR-8**: `RefreshCommand.ExecuteCoreAsync` must delegate fetch/save/conflict/hydration
  logic to `RefreshOrchestrator`, eliminating inline duplication of orchestrator functionality.
  The command retains only WIQL construction, output formatting, telemetry, and profile metadata.

### Non-Functional

- **NFR-1**: All changes must be AOT-compatible (`PublishAot=true`, `TrimMode=full`).
- **NFR-2**: No new NuGet dependencies (use built-in .NET HttpClientHandler capabilities).
- **NFR-3**: Zero behavioral change for existing CLI commands — output, exit codes, and error
  handling must remain identical.
- **NFR-4**: All existing tests must pass without modification (except where test setup
  explicitly exercises changed internal behavior).
- **NFR-5**: Telemetry data privacy rules remain enforced — no new PII leakage vectors.

## Proposed Design

### Architecture Overview

The optimization touches four layers but preserves all public interfaces:

```
┌──────────────────────────────────────────────────────────────────┐
│ Commands (RefreshCommand, StatusCommand, TreeCommand, …)         │
│  ├─ Pass config TTL value (CacheRelaxedMinutes/CacheEagerMinutes)│
│  └─ RefreshCommand delegates fully to RefreshOrchestrator         │
├──────────────────────────────────────────────────────────────────┤
│ Domain Services                                                  │
│  ├─ SyncCoordinator                                              │
│  │   ├─ FetchStaleAndSaveAsync: batch staleness → FetchBatchAsync│
│  │   └─ Accepts int? cacheStaleMinutesOverride for TTL override  │
│  └─ RefreshOrchestrator                                          │
│      └─ FetchItemsAsync: Task.WhenAll(sprint, active, children)  │
├──────────────────────────────────────────────────────────────────┤
│ Infrastructure                                                   │
│  ├─ IWorkItemRepository.GetByIdsAsync() (already exists)         │
│  ├─ AdoRestClient (unchanged public API)                         │
│  └─ NetworkServiceModule (HttpClient handler configuration)      │
│      ├─ AutomaticDecompression: GZip | Brotli                   │
│      └─ HttpVersion: 2.0 with downgrade policy                  │
└──────────────────────────────────────────────────────────────────┘
```

### Key Component: Batch Staleness + FetchBatchAsync (Issue #1612)

**Current flow** in `SyncCoordinator.FetchStaleAndSaveAsync`:
```
for each candidateId:                    ← N sequential SQLite reads
    GetByIdAsync(id) → check LastSyncedAt
Task.WhenAll(staleIds.Select(FetchAsync)) ← N concurrent HTTP requests
SaveBatchProtectedAsync(items)            ← 1 batch write
```

**New flow**:
```
GetByIdsAsync(candidateIds) → batch check LastSyncedAt  ← 1 SQLite query
FetchBatchAsync(staleIds)                               ← 1 HTTP request (≤200 IDs)
SaveBatchProtectedAsync(items)                          ← 1 batch write (unchanged)
```

The key change is replacing the `foreach` loop calling `GetByIdAsync` one-by-one with
`GetByIdsAsync` (which already exists on `IWorkItemRepository`), then filtering stale items
in memory. The concurrent `FetchAsync` fan-out is replaced with `FetchBatchAsync` which
issues a single HTTP GET with comma-separated IDs.

**Error handling change**: `FetchBatchAsync` returns successfully fetched items but does not
surface per-item 404s the same way individual `FetchAsync` calls do. The "not found" eviction
logic (lines 153-159) needs adaptation: after `FetchBatchAsync`, compare returned IDs against
requested IDs — missing IDs are treated as "not found" and evicted from cache. This preserves
the existing ghost-eviction behavior.

**Batch error semantics (fail-all vs partial)**: Unlike the current N×FetchAsync pattern
(where `Task.WhenAll` yields partial success — some items may succeed while others fail with
401/429/timeout), `FetchBatchAsync` is an all-or-nothing HTTP call: a single network error
(401, 429, timeout) fails ALL items in the batch. This is **acceptable** for the working-set
sync use case because:
1. Auth errors (401) and rate-limit errors (429) are inherently session-wide — retrying
   individual items wouldn't help.
2. Timeout errors on a single batch request are less likely than timeouts on N individual
   requests (fewer round-trips = fewer failure opportunities).
3. The existing `SyncResult.Failed` return path already handles total failure gracefully.
4. If partial-batch resilience becomes needed (e.g., for very large working sets), chunked
   `FetchBatchAsync` calls (≤200 IDs each) naturally provide chunk-level isolation without
   requiring per-item retry. This is a future optimization, not a blocker.

**`PartiallyUpdated` dead-code cleanup**: The current `FetchStaleAndSaveAsync` returns
`SyncResult.PartiallyUpdated` when some per-item fetches succeed and others fail (line 170).
With `FetchBatchAsync` (all-or-nothing HTTP semantics), this mixed-result branch becomes
architecturally unreachable — the batch either returns all items or throws entirely. The
`PartiallyUpdated` return path will be removed from `FetchStaleAndSaveAsync`, and the
associated tests will be updated to reflect batch semantics: 3 tests assert `PartiallyUpdated`
directly (`SyncWorkingSetAsync` ×2, `SyncItemSetAsync` ×1) and require rewrite, plus 1 negative
test (`AllFetchesFail_ReturnsFailed_NotPartiallyUpdated`) needs cosmetic cleanup of its
name and comments — 4 tests affected total. The `SyncResult.PartiallyUpdated`
type itself and its `SpectreRenderer` handler (line 1078) are **also removed** — no code path
emits this result after the batch change. Retaining dead code for hypothetical future chunking
is premature; if chunked batches are added later, the type and handler can be re-added then.

### Key Component: Parallel Refresh (Issue #1613)

**Current flow** in `RefreshOrchestrator.FetchItemsAsync`:
```
sprintItems = await FetchBatchAsync(realIds)   ← blocks
activeItem  = await FetchAsync(activeId)       ← blocks
childItems  = await FetchChildrenAsync(activeId) ← blocks
```

**New flow**:
```
var sprintTask  = FetchBatchAsync(realIds)
var activeTask  = (activeId not in realIds) ? FetchAsync(activeId) : Task.FromResult(null)
var childTask   = FetchChildrenAsync(activeId)
await Task.WhenAll(sprintTask, activeTask, childTask)
```

The active-item and child-item fetches are independent of the sprint-item fetch. The only
dependency is that `activeId` must be known before starting (it comes from `IContextStore`,
which is a local SQLite read).

**Post-refresh metadata sync parallelization** (in `RefreshCommand.ExecuteCoreAsync`):

`SyncProcessTypesAsync` and `SyncFieldDefinitionsAsync` are separate public methods on
`RefreshOrchestrator`, each wrapping a single static service call. They are currently called
sequentially in `RefreshCommand.ExecuteCoreAsync` (not inside `FetchItemsAsync`). The
parallelization target is this call site in the command, not the orchestrator:

```
// Current: sequential in RefreshCommand.ExecuteCoreAsync
await ProcessTypeSyncService.SyncAsync(...)
await FieldDefinitionSyncService.SyncAsync(...)

// New: parallel — either inline in RefreshCommand or via new RefreshOrchestrator method
await Task.WhenAll(
    ProcessTypeSyncService.SyncAsync(...),
    FieldDefinitionSyncService.SyncAsync(...))
```

> **SQLite write contention note**: Both `ProcessTypeSyncService` and `FieldDefinitionSyncService`
> write to SQLite (`IProcessTypeStore` and `IFieldDefinitionStore`). WAL mode serializes writes,
> so the parallelization benefit comes **exclusively from overlapping network I/O** — the ADO
> HTTP round-trips for `/wit/workitemtypes` and `/wit/fields` run concurrently, and the
> subsequent SQLite writes serialize automatically. This still yields meaningful wall-clock
> improvement because each metadata fetch involves 100–300ms of network latency that would
> otherwise be additive.

**RefreshCommand consolidation**: The inline refresh logic in `RefreshCommand.ExecuteCoreAsync`
(lines 110–225) substantially duplicates `RefreshOrchestrator.FetchItemsAsync`. The command
will be refactored to delegate fully to `RefreshOrchestrator`, retaining only command-specific
concerns (WIQL construction, output formatting, telemetry, profile metadata).

### Key Component: Tiered Cache TTL (Issue #1614)

Three TTL tiers are defined by new `DisplayConfig` properties:

```csharp
public int CacheEagerMinutes { get; set; } = 2;     // mutating commands
public int CacheRelaxedMinutes { get; set; } = 15;  // read-only display commands
// CacheStaleMinutes (existing) = 5 — default/Normal tier
```

**Integration with SyncCoordinator (DD-13 compatible)**:

The `SyncCoordinator` constructor already accepts `int cacheStaleMinutes` as a primitive
(DD-13). The per-call override is passed as an optional parameter to `SyncWorkingSetAsync`
and `SyncItemSetAsync`:

```csharp
public async Task<SyncResult> SyncWorkingSetAsync(
    WorkingSet workingSet,
    int? cacheStaleMinutesOverride = null,  // ← new optional parameter
    CancellationToken ct = default)
```

When `cacheStaleMinutesOverride` is non-null, it replaces `_cacheStaleMinutes` for the
staleness threshold in that call only. Commands pass the config value directly (e.g.,
`_config.CacheRelaxedMinutes`) — no intermediate enum type needed.

**Command categorization** (no new abstraction needed — each command knows its tier):
- **Eager (2 min / `_config.CacheEagerMinutes`)**: `SetCommand`

> **Note**: `EditCommand`, `SaveCommand`, `UpdateCommand`, and `StateCommand` are mutating
> commands but do not call `SyncCoordinator` — they perform direct `adoService.FetchAsync()`
> calls for conflict resolution. The tiered TTL applies only to `SyncCoordinator`-mediated
> staleness checks, so these commands are unaffected and excluded from tier classification.
- **Normal (5 min / `_config.CacheStaleMinutes`)**: `RefreshCommand`, `SyncCommand`
- **Relaxed (15 min / `_config.CacheRelaxedMinutes`)**: `StatusCommand`, `TreeCommand`, `ShowCommand`, `WorkspaceCommand`

The tier minute values are configurable via `DisplayConfig`:

```csharp
public sealed class DisplayConfig
{
    public int CacheStaleMinutes { get; set; } = 5;         // Normal tier (existing)
    public int CacheEagerMinutes { get; set; } = 2;         // Eager tier
    public int CacheRelaxedMinutes { get; set; } = 15;      // Relaxed tier
}
```

### Key Component: HTTP Transport (Issue #1616)

**1. Response decompression** — Configure `HttpClientHandler` with automatic decompression:

```csharp
var handler = new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli
};
services.AddSingleton(new HttpClient(handler));
```

This is transparent to all callers — the `Accept-Encoding` header is added automatically,
and response decompression happens in the handler pipeline. AOT-compatible; no reflection.

**2. HTTP/2 preference** — Set default request version:

```csharp
var httpClient = new HttpClient(handler)
{
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
};
```

This prefers HTTP/2 but falls back to HTTP/1.1 if the server doesn't support it. ADO
(`dev.azure.com`) supports HTTP/2 via ALPN.

**3. Conditional GET for metadata** — Metadata endpoints (iterations, field definitions,
process configuration) are fetched on every `refresh` but change infrequently. Per-endpoint
cached response fields in `AdoIterationService` store both the ETag and the deserialized
domain object, enabling conditional GET requests:

```csharp
// Per-endpoint ETag cache — stores the ETag alongside the transformed domain response
// so 304 Not Modified can return the cached result without re-downloading or re-parsing.
private (string ETag, IterationPath Value)? _iterationETagCache;
private (string ETag, ProcessConfigurationData Value)? _processConfigETagCache;
private (string ETag, IReadOnlyList<FieldDefinition> Value)? _fieldDefsETagCache;
```

When a cached ETag exists, the `If-None-Match` header is added to the GET request.
A `304 Not Modified` response (which carries no body) returns the cached domain object
from the tuple. A `200 OK` response updates both the ETag and the cached domain object.

Each cache field is accessed by exactly one method path, so no `ConcurrentDictionary`
is needed — nullable tuple fields provide sufficient thread safety for the parallel
metadata sync scenario (PG-2), since each `Task.WhenAll` branch writes to a distinct field.

**CLI vs MCP scoping**: In the short-lived CLI process, each metadata endpoint is called
at most once per invocation, so the ETag dictionary starts cold and never achieves a 304
hit. The feature provides **primary value in the MCP server** (`src/Twig.Mcp`), which is
a long-lived process where `GetCurrentIterationAsync` is called on every `twig.workspace`
invocation, and `RefreshOrchestrator` metadata syncs occur on repeated `twig.sync` calls.
Over a typical multi-hour MCP session, ETags eliminate redundant metadata re-downloads
without persisting anything to disk.

The implementation is identical for both modes (same `AdoIterationService` class) — the
CLI simply never sees a 304 because it never makes a second call to the same endpoint.
This is harmless (zero overhead on cache miss) and avoids conditional code paths.

This is applied selectively to `AdoIterationService` methods that fetch metadata.
The following table shows the distinct HTTP endpoints and which public methods use them:

| HTTP Endpoint | Public Methods | Shared via | ETag Benefit |
|---------------|---------------|------------|--------------|
| `/_apis/work/teamsettings/iterations` | `GetCurrentIterationAsync` | Direct call | **Yes** — no in-memory cache; every invocation hits the network. Primary MCP beneficiary |
| `/_apis/wit/workitemtypes` | 3 methods via shared `GetWorkItemTypesResponseAsync` | `_workItemTypesCache` (lazy `Task<T>`) | **No** — already lazily cached per-process; ETags add no value beyond the existing cache |
| `/_apis/wit/fields` | `GetFieldDefinitionsAsync` | Direct call | **Yes** — no in-memory cache. Benefits MCP repeated syncs |
| `/_apis/work/processconfiguration` | `GetProcessConfigurationAsync` | Direct call | **Yes** — no in-memory cache. Benefits MCP repeated syncs |

**Important**: Although there are 4 distinct HTTP endpoints, ETag support provides value for
only **3 endpoints** — iterations, field definitions, and process configuration. The work item
types endpoint (`/_apis/wit/workitemtypes`) is already lazily cached via `_workItemTypesCache`
(`Task<AdoWorkItemTypeListResponse?>`) in `GetWorkItemTypesResponseAsync` (line 319). Since
ETags are in-memory only (lost between process invocations), and `_workItemTypesCache` already
prevents repeated HTTP calls within a single process lifetime, adding ETags to this endpoint
would be a no-op. ETag wrapping is therefore limited to the 3 endpoints that lack in-memory caching.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Use `GetByIdsAsync` (existing) instead of adding `GetStaleIdsAsync` | Avoids new repository method; in-memory filtering is fast for typical working set sizes (10-50 items). See A2 |
| Replace Task.WhenAll+FetchAsync with FetchBatchAsync | Single HTTP request vs N requests; batch API is already proven in RefreshOrchestrator |
| Accept all-or-nothing failure semantics for FetchBatchAsync | Auth/rate-limit errors are session-wide anyway; chunked batches (≤200) provide natural isolation for large sets |
| Per-call TTL override vs constructor change | Preserves DD-13 (primitive injection); commands pass `_config.CacheRelaxedMinutes` / `_config.CacheEagerMinutes` directly |
| Commands reference config values directly for TTL tier | No intermediate `CacheTier` enum needed — `_config.CacheRelaxedMinutes` is already named and expressive |
| Inline ETag tuples in AdoIterationService vs separate ETagCache class | Single caller (3 endpoints); per-endpoint typed fields avoid generics. See A3 |
| ETags scoped to MCP (in-memory only) vs SQLite-persisted ETags | MCP is long-lived and benefits from 304 savings; CLI calls each endpoint once (zero benefit from persistence). See A3 |
| Remove `PartiallyUpdated` return path, type, and renderer | With FetchBatchAsync (all-or-nothing), per-item mixed success/failure is architecturally impossible (YAGNI) |
| HttpClientHandler vs DelegatingHandler for compression | HttpClientHandler.AutomaticDecompression is the standard .NET approach; no custom handler needed |
| No Polly dependency | Retry policies are out of scope (NG-2); built-in .NET handler capabilities suffice |

## Alternatives Considered

### A1: Inject `CacheTier` via DI instead of per-call parameter

**Approach**: Register `SyncCoordinator` with a `CacheTier` determined at DI composition time
based on the command being run.

**Pros**: Clean separation; no optional parameters.
**Cons**: ConsoleAppFramework resolves all singletons before command dispatch — the same
`SyncCoordinator` instance serves all commands. Would require scoped or transient registration,
complicating the DI model for minimal benefit.

**Decision**: Per-call override parameter is simpler and preserves singleton semantics.

### A2: Add a batch `GetStaleItemIdsAsync` method to `IWorkItemRepository`

**Approach**: Push the staleness-threshold filter into SQL: `SELECT id FROM work_items WHERE
last_synced_at < @threshold AND id IN (...)`.

**Pros**: Reduces data transfer from DB (only IDs, not full items).
**Cons**: Adds a new repository method for one caller; `GetByIdsAsync` already returns full
items which we need for the `LastSyncedAt` check. The overhead of returning full items for
10-50 items is negligible.

**Decision**: Use existing `GetByIdsAsync` and filter in memory.

### A3: Shared `ETagCache` service vs per-service inline fields

**Approach**: Extract a shared `IETagCache` service registered as a singleton, injected into
any service that needs conditional GET support. Stores `ConcurrentDictionary<string, (string ETag, object CachedResponse)>`.

**Pros**: Centralizes ETag logic; reusable if other services (e.g., `AdoWorkItemService`) adopt
conditional GETs in the future. Single point for metrics/logging.
**Cons**: Adds a new interface + implementation + DI registration for a feature used by exactly
one service (`AdoIterationService`) targeting 3 endpoints. The cache key would need URL-based
namespacing to avoid collisions. Generics or `object` casting for the cached response type add
complexity. The CLI is short-lived (single command invocation), so the "reuse across services"
benefit is speculative.

**Decision**: Inline per-endpoint `(string ETag, T)` nullable tuple fields. Single caller;
extraction is premature. If a second service needs ETag support, extract then (YAGNI). This
also aligns with the existing `_workItemTypesCache` pattern in `AdoIterationService` which
is inline.

### A4: HTTP/3 (QUIC) instead of HTTP/2

**Approach**: Set `DefaultRequestVersion = HttpVersion.Version30` with downgrade policy.

**Pros**: HTTP/3 eliminates head-of-line blocking at the transport layer (QUIC vs TCP); lower
latency on lossy networks.
**Cons**: (1) .NET's HTTP/3 support requires `msquic` native library which adds AOT/trimming
complexity and is not bundled on all platforms. (2) Azure DevOps (`dev.azure.com`) does not
advertise HTTP/3 support via `Alt-Svc` headers as of this writing. (3) HTTP/2's multiplexing
already provides the key benefit (concurrent streams over a single connection) for the
CLI's workload of ≤10 concurrent requests.

**Decision**: HTTP/2 with `RequestVersionOrLower` fallback. HTTP/3 has no server-side support
from ADO and adds native dependency risk for AOT builds. Revisit when ADO advertises HTTP/3.

## Dependencies

- **Internal**: No cross-team dependencies. All changes are within the Twig codebase.
- **External**: No new NuGet packages. Uses built-in `System.Net.Http` capabilities.
- **Sequencing**:
  - **Required**: Issue #1612 must complete before #1613 (the parallelization work
    builds on the batch-fetch foundation and both touch `SyncCoordinator`).
  - **Preferred**: #1614 ideally merges after #1612 for a clean merge (both modify
    `SyncCoordinator.cs`), but this is a convenience preference — not a functional dependency.
    Merge conflicts would be minor and mechanical.
  - **Independent**: #1616 has no dependency on any other issue.

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|------------|------|
| `SyncCoordinator.cs` | Rewrite `FetchStaleAndSaveAsync`; add TTL override parameter | Medium — core sync logic |
| `RefreshOrchestrator.cs` | Parallelize `FetchItemsAsync`; parallelize metadata syncs | Medium — refresh pipeline |
| `RefreshCommand.cs` | Delegate to `RefreshOrchestrator`; remove duplicate logic | Low — code deletion |
| `StatusOrchestrator.cs` | Forward `cacheStaleMinutesOverride` through wrapper | Low — parameter passthrough |
| `NetworkServiceModule.cs` | Configure `HttpClientHandler` | Low — DI wiring |
| `AdoIterationService.cs` | Add ETag caching for metadata endpoints | Low — additive |
| `TwigConfiguration.cs` | Add `CacheEagerMinutes`, `CacheRelaxedMinutes` | Low — config extension |
| `DisplayConfig` | Two new int properties with defaults | Low — backward compatible |
| `StatusCommand.cs`, `TreeCommand.cs`, etc. | Pass TTL tier to sync calls | Low — parameter addition |

> **Note**: `ReadTools.cs` (Twig.Mcp) is unaffected — it calls only `SyncLinksAsync` which is not modified.

### Backward Compatibility

- **Config**: New `CacheEagerMinutes` and `CacheRelaxedMinutes` properties have sensible
  defaults (2 and 15 respectively). Existing config files without these properties will
  deserialize to defaults via `System.Text.Json` default handling. No migration needed.
- **CLI Contract**: No changes to command names, arguments, output formats, or exit codes.
- **API**: `SyncCoordinator` public method signatures gain optional parameters with defaults
  matching current behavior. All existing callers compile without changes.

### Performance (theoretical estimates)

| Metric | Before | After (estimated) | Expected Improvement |
|--------|--------|-------|-------------|
| HTTP requests per working-set sync (20 items, 8 stale) | 8 | 1 | ~87% reduction |
| SQLite reads per working-set sync (20 items) | 20 | 1 | ~95% reduction |
| Refresh wall-clock time (sprint + active + children) | 3 × latency | 1 × latency | ~66% reduction (network-bound) |
| ADO response payload size (with gzip) | 100% | ~40-60% | ~40-60% bandwidth savings (typical JSON compression) |
| Background sync frequency (status/tree) | Every 5 min | Every 15 min | ~66% fewer syncs |

> **Note**: These are theoretical estimates based on round-trip reduction analysis. Actual
> improvements depend on network latency, ADO response times, and working set size.
> **Validation methodology**: Compare `twig refresh --verbose` and `twig status` timing output
> before and after each PR group merges, using a representative sprint backlog (≥15 items,
> ≥5 stale). Measure wall-clock time, HTTP request count (via `--verbose` diagnostics), and
> bandwidth delta (if proxy/Fiddler capture is feasible). Per-PR-group validation ensures
> each optimization vector's contribution is isolated.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `FetchBatchAsync` returns partial results (some IDs missing) without error | Medium | Medium | Compare returned IDs against requested; treat missing as "not found" and evict |
| `FetchBatchAsync` all-or-nothing failure (401, 429, timeout) fails entire batch | Medium | Medium | Auth/rate-limit are session-wide — fail-all is equivalent. `SyncResult.Failed` handles this. Chunked batches (≤200 IDs) provide natural isolation for large sets |
| Parallel refresh introduces race condition in conflict detection | Low | High | Conflict detection runs after all fetches complete (same barrier as current sequential flow) |
| ETag cache tuples consume memory in long-lived MCP process | Low | Low | At most 3 cached domain objects (iteration path, process config, field definitions); negligible memory footprint even over multi-hour sessions |
| ETags provide zero benefit in CLI mode | N/A | None | By design — CLI calls each endpoint once. No overhead on cache miss (single `if` check). No conditional code paths between CLI and MCP |
| HTTP/2 not supported by corporate proxy | Low | Low | `RequestVersionOrLower` policy falls back to HTTP/1.1 automatically |
| Relaxed TTL causes stale display data | Medium | Low | Users can run `twig sync` for immediate refresh; `--no-refresh` already exists |
| Removing `PartiallyUpdated` entirely eliminates partial-failure reporting | Low | Low | Batch semantics make per-item failure impossible; if chunked batches are added later, the type and handler can be re-added then |

## Files Affected

### New Files

_No new files required._

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/SyncCoordinator.cs` | Replace N×GetByIdAsync with GetByIdsAsync; replace N×FetchAsync with FetchBatchAsync; add cacheStaleMinutesOverride parameter; adapt not-found eviction logic |
| `src/Twig.Domain/Services/StatusOrchestrator.cs` | Add cacheStaleMinutesOverride parameter to SyncWorkingSetAsync(WorkItem) wrapper; forward override to SyncCoordinator |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | Parallelize FetchItemsAsync (Task.WhenAll for sprint+active+children); parallelize metadata syncs |
| `src/Twig/Commands/RefreshCommand.cs` | Delegate to RefreshOrchestrator; remove duplicate inline refresh logic; retain WIQL construction, output formatting, telemetry, profile metadata |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Configure HttpClientHandler with AutomaticDecompression, HTTP/2 default version |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Add per-endpoint `(ETag, T)` nullable tuple cache fields for 3 metadata endpoints (iterations, fields, process config); add If-None-Match headers; handle 304 responses by returning cached domain objects |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add CacheEagerMinutes and CacheRelaxedMinutes properties to DisplayConfig (no SetValue entries — not user-configurable) |
| `src/Twig/Commands/StatusCommand.cs` | Pass `_config.CacheRelaxedMinutes` to `SyncWorkingSetAsync` |
| `src/Twig/Commands/TreeCommand.cs` | Pass `_config.CacheRelaxedMinutes` to `SyncWorkingSetAsync` |
| `src/Twig/Commands/ShowCommand.cs` | Pass `_config.CacheRelaxedMinutes` to `SyncItemSetAsync` |
| `src/Twig/Commands/SetCommand.cs` | Pass `_config.CacheEagerMinutes` to `SyncItemSetAsync` |
| `src/Twig/Commands/WorkspaceCommand.cs` | Substitute `config.Display.CacheStaleMinutes` → `config.Display.CacheRelaxedMinutes` in `IsCacheStale` call (line 86); no SyncCoordinator involvement |
| `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | Update tests for batch fetch; add not-found-via-batch tests; add TTL override tests |
| `tests/Twig.Domain.Tests/Services/StatusOrchestratorTests.cs` | Update test for cacheStaleMinutesOverride passthrough |
| `tests/Twig.Domain.Tests/Services/RefreshOrchestratorTests.cs` | Update tests for parallel fetch verification |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Update for delegated-to-orchestrator pattern |
| `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Add ETag/304 handling tests |

## ADO Work Item Structure

> **Effort sizing legend**: **S** = Small (≤4 hours), **M** = Medium (4–8 hours), **L** = Large (>8 hours).
> Issue #1615 is not part of this epic — the jump from #1614 to #1616 is an ADO numbering
> artifact (#1615 belongs to a different project).

### Issue #1612: Replace N+1 FetchAsync with FetchBatchAsync in SyncCoordinator

**Goal**: Eliminate N individual HTTP requests in `SyncCoordinator.FetchStaleAndSaveAsync` by
replacing them with a single `FetchBatchAsync` call, and replace N sequential SQLite reads
with a single `GetByIdsAsync` batch query.

**Prerequisites**: None — this is the foundational change.

**Tasks**:

| Task ID | Description | Files | Effort | Traces To |
|---------|-------------|-------|--------|-----------|
| T-1612.1 | Replace N×GetByIdAsync loop with GetByIdsAsync batch query in FetchStaleAndSaveAsync | `SyncCoordinator.cs` | S | FR-2 |
| T-1612.2 | Replace Task.WhenAll+FetchAsync with FetchBatchAsync in FetchStaleAndSaveAsync; adapt not-found detection to compare returned IDs vs requested IDs; remove unreachable `PartiallyUpdated` return path (dead code under batch semantics) | `SyncCoordinator.cs` | M | FR-1 |
| T-1612.3 | Update SyncCoordinator unit tests for batch fetch behavior: rewrite 3 tests that assert `PartiallyUpdated` (now unreachable under batch semantics); clean up 1 negative test that references the type; add batch happy path, not-found-via-batch, all-stale, none-stale scenarios | `SyncCoordinatorTests.cs` | M | NFR-4 |

**Acceptance Criteria**:
- [ ] `FetchStaleAndSaveAsync` issues exactly 1 SQLite query for staleness (not N)
- [ ] `FetchStaleAndSaveAsync` issues exactly ⌈N/200⌉ HTTP requests (not N)
- [ ] Items missing from `FetchBatchAsync` response are evicted from cache (ghost prevention)
- [ ] `PartiallyUpdated` return path, `SyncResult.PartiallyUpdated` type, and its `SpectreRenderer` handler are all removed (unreachable under batch semantics; YAGNI applies to dead code)
- [ ] All existing `SyncCoordinator` tests pass (3 PartiallyUpdated-asserting tests rewritten + 1 negative test cleaned up = 4 tests affected)
- [ ] New tests cover: batch fetch happy path, all-stale, none-stale, not-found eviction via ID comparison

---

### Issue #1613: Parallelize network calls and deduplicate refresh logic

**Goal**: Run independent ADO fetch calls concurrently in `RefreshOrchestrator.FetchItemsAsync`,
parallelize post-refresh metadata syncs, and consolidate `RefreshCommand` to delegate to
`RefreshOrchestrator` instead of reimplementing inline logic.

**Prerequisites**: #1612 (batch fetch foundation should be stable first).

**Tasks**:

| Task ID | Description | Files | Effort | Traces To |
|---------|-------------|-------|--------|-----------|
| T-1613.1 | Parallelize FetchItemsAsync (sprint + active + children in Task.WhenAll) and post-refresh metadata syncs (ProcessTypeSyncService + FieldDefinitionSyncService in Task.WhenAll) | `RefreshOrchestrator.cs` | M | FR-3, FR-4 |
| T-1613.2 | Refactor RefreshCommand to delegate to RefreshOrchestrator; remove inline fetch/save/conflict/hydration logic; retain WIQL construction, output formatting, telemetry, and profile metadata | `RefreshCommand.cs`, `RefreshOrchestrator.cs` | L | FR-8 |
| T-1613.3 | Update tests for parallel fetch and consolidated refresh flow | `RefreshOrchestratorTests.cs`, `RefreshCommandTests.cs` | M | NFR-4 |

**Acceptance Criteria**:
- [ ] `RefreshOrchestrator.FetchItemsAsync` uses `Task.WhenAll` for independent fetch calls
- [ ] Metadata syncs run concurrently (visible in test via mock call ordering)
- [ ] `RefreshCommand` no longer contains inline ADO fetch/save logic
- [ ] Conflict detection still works correctly (runs after all fetches complete)
- [ ] All refresh-related tests pass

---

### Issue #1614: Tiered cache TTL for read-only vs read-write commands

**Goal**: Introduce configurable cache TTL tiers so read-only commands tolerate longer staleness
(reducing background sync frequency) while mutating commands get fresh data.

**Prerequisites**: None — independent of #1612/#1613.

**Tasks**:

| Task ID | Description | Files | Effort | Traces To |
|---------|-------------|-------|--------|-----------|
| T-1614.1 | Add `CacheEagerMinutes` (default 2) and `CacheRelaxedMinutes` (default 15) properties to `DisplayConfig`; **no** `SetValue` switch cases — these are sensible defaults with no user story for ad-hoc configuration | `TwigConfiguration.cs` | S | FR-5 |
| T-1614.2 | Add cacheStaleMinutesOverride optional parameter to SyncWorkingSetAsync and SyncItemSetAsync in SyncCoordinator; update StatusOrchestrator.SyncWorkingSetAsync(WorkItem) wrapper to accept and forward the override | `SyncCoordinator.cs`, `StatusOrchestrator.cs` | S | FR-5 |
| T-1614.3 | Update all command callers to pass tier-appropriate TTL: read-only commands (`StatusCommand`, `TreeCommand`, `ShowCommand`) pass `_config.CacheRelaxedMinutes`; `SetCommand` passes `_config.CacheEagerMinutes`; `WorkspaceCommand` substitutes `CacheStaleMinutes` → `CacheRelaxedMinutes` in its `IsCacheStale` call | `StatusCommand.cs`, `TreeCommand.cs`, `ShowCommand.cs`, `SetCommand.cs`, `WorkspaceCommand.cs` | S | FR-5 |
| T-1614.4 | Add tests for tiered TTL behavior: relaxed tier uses longer threshold, eager tier uses shorter | `SyncCoordinatorTests.cs` | S | NFR-4 |

**Acceptance Criteria**:
- [ ] `DisplayConfig` has `CacheEagerMinutes` (default 2) and `CacheRelaxedMinutes` (default 15) properties
- [ ] Read-only commands pass `_config.CacheRelaxedMinutes` to sync methods
- [ ] Mutating commands pass `_config.CacheEagerMinutes` to sync methods
- [ ] Existing config files without new properties deserialize to defaults correctly
- [ ] Tests verify different TTL thresholds are applied per tier
- [ ] All existing tests pass

---

### Issue #1616: HTTP transport optimizations (compression, HTTP/2, ETags)

**Goal**: Enable HTTP-level transport optimizations to reduce bandwidth, improve connection
efficiency, and eliminate redundant metadata fetches.

**Prerequisites**: None — independent of other issues.

**Tasks**:

| Task ID | Description | Files | Effort | Traces To |
|---------|-------------|-------|--------|-----------|
| T-1616.1 | Configure HttpClientHandler with AutomaticDecompression (GZip + Brotli) and HTTP/2 default version in NetworkServiceModule | `NetworkServiceModule.cs` | S | FR-6, G-8 |
| T-1616.2 | Add ETag support to AdoIterationService: 3 per-endpoint `(string ETag, T CachedResponse)?` nullable tuple cache fields + `If-None-Match` header injection + 304 Not Modified handling that returns cached domain object. Excludes `GetWorkItemTypesResponseAsync` (already lazily cached). Primary beneficiary: MCP server (long-lived process) | `AdoIterationService.cs` | M | FR-7 |
| T-1616.3 | Add tests for 304 handling (verify cached response returned), ETag tracking (verify If-None-Match sent on second call), compression header verification | `AdoIterationServiceTests.cs` | M | NFR-4 |

**Acceptance Criteria**:
- [ ] HTTP responses are automatically decompressed (gzip/Brotli)
- [ ] HttpClient prefers HTTP/2 with HTTP/1.1 fallback
- [ ] Metadata endpoints (iterations, fields, process config) send `If-None-Match` when cached ETag exists
- [ ] 304 Not Modified responses return cached domain object from `(ETag, T)` tuple without payload re-download
- [ ] ETag cache stores both the ETag string and the deserialized domain response (not just ETag)
- [ ] All existing HTTP-related tests pass
- [ ] AOT compatibility verified (no reflection in new code)

## PR Groups

| PG | Scope | Files | Est. LoC | Classification | Predecessors |
|----|-------|-------|----------|----------------|--------------|
| PG-1 | Issue #1612 — Tasks T-1612.1, T-1612.2, T-1612.3 | 2 (`SyncCoordinator.cs`, `SyncCoordinatorTests.cs`) | ~200 | Deep — core sync logic rewrite in a few files | None |
| PG-2 | Issue #1613 — Tasks T-1613.1, T-1613.2, T-1613.3 | ~5 (`RefreshOrchestrator.cs`, `RefreshCommand.cs`, test files) | ~400 | Deep — refresh pipeline restructuring | PG-1 (required) |
| PG-3 | Issue #1614 — Tasks T-1614.1 through T-1614.4 | ~9 (`TwigConfiguration.cs`, `SyncCoordinator.cs`, `StatusOrchestrator.cs`, 5 command files, test files) | ~200 | Wide — small changes across many command files | None (preferred: after PG-1 for clean merge on `SyncCoordinator.cs`) |
| PG-4 | Issue #1616 — Tasks T-1616.1, T-1616.2, T-1616.3 | ~3 (`NetworkServiceModule.cs`, `AdoIterationService.cs`, test file) | ~250 | Deep — transport layer config and ETag integration | None (fully independent) |

**Execution order**: PG-1 → PG-2 → {PG-3, PG-4} (PG-3 and PG-4 can run in parallel after PG-2)

> **Dependency qualifiers**: PG-2's dependency on PG-1 is **required** (both modify
> `SyncCoordinator`; PG-2's parallelization builds on PG-1's batch semantics). PG-3's
> preference for ordering after PG-1 is **preferred** — it reduces merge conflict risk on
> `SyncCoordinator.cs` but is not a functional dependency. PG-4 is fully independent.

> **Classification legend**: **Deep** = few files with complex logic changes requiring careful
> review. **Wide** = many files with mechanical/repetitive changes (e.g., parameter additions
> across command files).

## Open Questions

_None — all design decisions resolved during codebase analysis and prior review rounds._

## References

- ADO REST API — [Get Work Items Batch](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/get-work-items-batch) (up to 200 IDs per request)
- .NET HttpClientHandler — [AutomaticDecompression](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.automaticdecompression)
- .NET HTTP/2 — [HttpClient HTTP/2 support](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/http2)
- Existing plan: `docs/projects/set-command-sync-optimization.plan.md` — prior SyncCoordinator optimization work (source of DD-8, DD-13, NFR-003)
- Existing plan: `docs/projects/cache-aware-rendering.plan.md` — cache-first rendering design (source of DD-15)
- Existing plan: `docs/projects/archive/twig-process-sync-cleanup.plan.md` — `GetWorkItemTypesResponseAsync` caching

---

