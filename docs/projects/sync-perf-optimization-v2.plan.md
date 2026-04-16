# Sync Performance Optimization — Solution Design (v2)

**Epic:** #1611 — Sync Performance Optimization
**Author:** Copilot (Principal Architect)
> **Status**: ✅ Done

---

## Executive Summary

This design introduces tiered cache TTLs into Twig's sync pipeline so that read-only display commands (`status`, `tree`, `show`) tolerate 15 minutes of cache staleness while mutating commands (`set`, `link`) maintain the existing 5-minute threshold. The mechanism is a `SyncCoordinatorFactory` that holds two pre-built `SyncCoordinator` instances — `ReadOnly` and `ReadWrite` — without changing the `SyncCoordinator` constructor (preserving DD-13). Commands inject the factory and select the appropriate tier. This is the final optimization in Epic #1611, which has already delivered batch sync (#1612), HTTP transport improvements (#1616), and parallel refresh (#1613, pending test updates). The tiered TTL reduces unnecessary API calls during read-heavy workflows, where users frequently run `status`/`tree` within the 5–15 minute staleness window.

---

## Background

### Current Architecture (post-#1612, #1616, partial #1613)

Twig's sync pipeline sits between the local SQLite cache (WAL mode, per-workspace at `.twig/{org}/{project}/twig.db`) and Azure DevOps REST API (v7.1). The pipeline has already been optimized in several ways:

1. **Batch sync (Done — #1612):** `SyncCoordinator.FetchStaleAndSaveAsync` now uses `GetByIdsAsync` for staleness checks and `FetchBatchAsync` for HTTP fetching, reducing N+1 round-trips to batch operations.

2. **HTTP transport (Done — #1616):** `HttpClient` is configured with `SocketsHttpHandler` providing automatic gzip/Brotli decompression and HTTP/2 multiplexing with HTTP/1.1 fallback. In-memory `Task<T>?` caching is enabled for metadata endpoints (`GetProcessConfigurationAsync`, `GetFieldDefinitionsAsync`, `GetWorkItemTypesWithStatesAsync`).

3. **Parallel refresh (Doing — #1613):** `RefreshOrchestrator.FetchItemsAsync` parallelizes `FetchAsync(activeId)` and `FetchChildrenAsync(activeId)` using `Task.WhenAll`. `RefreshCommand.ExecuteCoreAsync` delegates fetch/save/conflict logic to the orchestrator. Post-refresh metadata syncs (`ProcessTypeSyncService` and `FieldDefinitionSyncService`) run concurrently via `Task.WhenAll`. Remaining: task #1658 (test updates for parallel fetch verification and delegated pattern).

4. **Tiered TTL (To Do — #1614):** All commands still share a single `CacheStaleMinutes = 5` value. No differentiation between read-only and mutating command staleness requirements.

### Epic #1611 Progress

| Issue | Title | Status | Notes |
|-------|-------|--------|-------|
| #1612 | Batch sync | ✅ Done | All 4 tasks merged |
| #1616 | HTTP transport | ✅ Done | All 3 tasks merged |
| #1613 | Parallel refresh | 🔄 Doing | 3/4 tasks done; #1658 (test updates) remaining |
| #1614 | Tiered cache TTL | 📋 To Do | All 6 tasks pending — primary focus of this plan |

### Key Design Decisions Referenced

- **DD-8:** Per-item `LastSyncedAt` timestamps for staleness (not global cache timestamp)
- **DD-13:** `SyncCoordinator` accepts `int cacheStaleMinutes` primitive (not `TwigConfiguration`) to avoid Domain → Infrastructure circular reference
- **DD-15:** `SyncChildrenAsync` always fetches unconditionally (no per-parent staleness check)
- **FR-013:** Working-set sync does NOT evict items
- **NFR-003:** `ProtectedCacheWriter.SaveBatchProtectedAsync` computes protected IDs once internally

### Call-Site Audit: SyncCoordinator

#### Source Code — Constructor Call Sites

`SyncCoordinator` has two constructors: a primary 6-parameter constructor (with `IWorkItemLinkRepository?`) and a secondary 5-parameter convenience constructor that delegates to the primary with `linkRepo: null`. The factory uses the 6-parameter constructor exclusively (passing `IWorkItemLinkRepository` from DI). The 5-parameter constructor is used only in tests that don't exercise link-related sync paths — no production code uses it. The factory does not need to expose or wrap the 5-parameter overload.

| File | Method | Usage | Impact of factory change |
|------|--------|-------|--------------------------|
| `src/Twig/DependencyInjection/CommandServiceModule.cs:47-53` | `AddTwigCommandServices()` | DI factory, 6-param ctor with `IWorkItemLinkRepository` | Replace with `SyncCoordinatorFactory` registration |
| `src/Twig.Mcp/Program.cs:51-57` | Host builder | DI factory, 6-param ctor | Mirror CLI factory pattern; both tiers = `CacheStaleMinutes` |

#### Source Code — Method Call Sites

| File | Method | API Called | Tier |
|------|--------|-----------|------|
| `StatusCommand.cs` | `ExecuteCoreAsync` | `SyncWorkingSetAsync`, `SyncLinksAsync` | ReadOnly |
| `TreeCommand.cs` | `ExecuteCoreAsync` | `SyncWorkingSetAsync`, `SyncLinksAsync` | ReadOnly |
| `ShowCommand.cs` | `ExecuteCoreAsync` | `SyncItemSetAsync` | ReadOnly |
| `StatusOrchestrator.cs` | `SyncWorkingSetAsync` | `SyncWorkingSetAsync` | ReadOnly |
| `SetCommand.cs` | `ExecuteCoreAsync` | `SyncItemSetAsync`, `SyncLinksAsync` | ReadWrite |
| `LinkCommand.cs` | `ResyncItemAsync` | `SyncLinksAsync` | ReadWrite |
| `RefreshCommand.cs` | `ExecuteCoreAsync` | (via orchestrator) | ReadWrite |
| `RefreshOrchestrator.cs` | `SyncWorkingSetAsync` | `SyncWorkingSetAsync` | ReadWrite |
| `ContextTools.cs` (MCP) | `Set` | `SyncItemSetAsync` | ReadWrite |
| `ReadTools.cs` (MCP) | `Tree` | `SyncLinksAsync` | ReadWrite |
| `MutationTools.cs` (MCP) | `Sync` | `SyncItemSetAsync` | ReadWrite |
| `ContextChangeService.cs` | `ExtendWorkingSetAsync` | `SyncChildrenAsync`, `SyncLinksAsync` | ReadWrite (via backward-compat DI) |

#### Test Files — `new SyncCoordinator` Instantiation Sites

| File | Instantiation Sites | Needs Factory Migration |
|------|--------------------|-----------------------|
| `SyncCoordinatorTests.cs` | 3 (lines 28, 739, 768) | No — tests coordinator directly |
| `RefreshOrchestratorTests.cs` | 1 (constructor) | Yes |
| `StatusOrchestratorTests.cs` | 1 (constructor) | Yes |
| `RefreshCommandTestBase.cs` | 1 (constructor) | Yes |
| `SetCommandTests.cs` | 1 (constructor) | Yes |
| `LinkCommandTests.cs` | 1 (constructor) | Yes |
| `ShowCommandTests.cs` | 1 (constructor) | Yes |
| `StatusCommandTests.cs` | 1 (constructor) | Yes |
| `TreeCommandTests.cs` | 1 (constructor) | Yes |
| `CacheFirstReadCommandTests.cs` | 1 (constructor) | Yes |
| `CommandFormatterWiringTests.cs` | 3 (constructor) | Yes |
| `CacheRefreshTests.cs` | 2 (constructor) | Yes |
| `NavigationCommandsInteractiveTests.cs` | 1 | Yes |
| `NextPrevCommandTests.cs` | 1 | Yes |
| `OfflineModeTests.cs` | 1 | Yes |
| `PromptStateIntegrationTests.cs` | 3 | Yes |
| `RefreshCommandDeprecationTests.cs` | 1 (via base) | Yes (via base) |
| `RefreshCommandProfileTests.cs` | 1 (via base) | Yes (via base) |
| `RefreshDirtyGuardTests.cs` | 1 (via base) | Yes (via base) |
| `SetCommandDisambiguationTests.cs` | 1 | Yes |
| `ShowCommand_CacheAwareTests.cs` | 1 | Yes |
| `StatusCommand_CacheAwareTests.cs` | 1 | Yes |
| `SyncCommandTests.cs` | 0 (via base class) | Yes (via base class) |
| `TreeCommandAsyncTests.cs` | 1 | Yes |
| `TreeCommandLinkTests.cs` | 1 | Yes |
| `TreeCommand_CacheAwareTests.cs` | 1 | Yes |
| `TreeNavCommandTests.cs` | 1 | Yes |
| `WorkingSetCommandTests.cs` | 1 | Yes |
| `ContextToolsTestBase.cs` (MCP) | 1 (target-typed `new()` on line 13) | Yes — `CreateSyncCoordinator()` has 1 instantiation (line 13, target-typed `new()` — **not** `new SyncCoordinator(…)`) but is called from 2 places within this file: `CreateStatusOrchestrator()` (line 20, needs `.ReadOnly`) and `CreateSut()` (line 27, needs `.ReadWrite`). A 3rd call site exists in `MutationToolsTestBase.CreateMutationSut()` (line 23, needs `.ReadWrite`). All 3 call sites must be updated to use the appropriate factory tier. Grep for `new SyncCoordinator` will **miss** this file — search for `CreateSyncCoordinator` as well. |
| `ReadToolsTestBase.cs` (MCP) | 1 | No — `ReadTools` still injects `SyncCoordinator` directly; test base unaffected |
| `MutationToolsTestBase.cs` (MCP) | 0 (delegates to `CreateSyncCoordinator()` in `ContextToolsTestBase`) | No — `MutationTools` still injects `SyncCoordinator` directly; test base unaffected |
| `ProgramBootstrapTests.cs` (MCP) | 1 | Yes — mirrors MCP DI graph; passes `SyncCoordinator` to orchestrator constructors. Must register `SyncCoordinatorFactory`, update orchestrator registrations, add backward-compat `SyncCoordinator`. |
| `ContextChangeServiceTests.cs` | 2 | No — uses `SyncCoordinator` directly (not via factory). Backward-compat DI registration handles this. |
| `FlowStartCommand_ContextChangeTests.cs` | 1 | No — `FlowStartCommand` injects `ContextChangeService` which resolves `SyncCoordinator` via backward-compat DI; test constructs `SyncCoordinator` directly |
| `NewCommand_ContextChangeTests.cs` | 1 | No — same pattern as `FlowStartCommand_ContextChangeTests`; `ContextChangeService` uses backward-compat DI |
| `SetCommand_ContextChangeTests.cs` | 1 | No — same pattern; context-change scenario constructs `SyncCoordinator` directly for `ContextChangeService` |
| `ProtectedCacheWriterTests.cs` | 1 | No — tests `ProtectedCacheWriter` directly; `SyncCoordinator` used only in integration scenario |

**Total:** 37 test files, 40 direct instantiation sites. 8 files do NOT need factory migration (see "No" rows above). **Net: 29 files need factory migration** — 25 direct edits + 4 auto-fixed via `RefreshCommandTestBase` update.

---

## Problem Statement

The remaining performance bottleneck:

**Uniform cache TTL:** All commands share a single `CacheStaleMinutes = 5` value. Read-only display commands (`status`, `tree`, `show`) that call `SyncWorkingSetAsync` trigger network requests every 5 minutes even though users would tolerate 10–15 minutes of staleness for display. Mutating commands (`set`) that call `SyncItemSetAsync` need aggressive freshness. There's no mechanism to differentiate.

**Secondary:** Issue #1613's final task (#1658) — test updates for the parallel refresh work — is still pending.

---

## Goals and Non-Goals

### Goals

| Goal | Description | Status | Issue |
|------|-------------|--------|-------|
| **G1** | Replace per-item `FetchAsync` calls with batch `GetByIdsAsync` for staleness checks | ✅ Done | #1612 |
| **G2** | Replace per-item `FetchAsync` calls with `FetchBatchAsync` for HTTP fetching | ✅ Done | #1612 |
| **G3** | Evict confirmed-deleted items during batch sync | ✅ Done | #1612 |
| **G4** | Parallelize `FetchAsync(activeId)` and `FetchChildrenAsync(activeId)` via `Task.WhenAll` | ✅ Done | #1613 |
| **G5** | Consolidate ~118 lines of inline fetch/save/conflict logic from `RefreshCommand.ExecuteCoreAsync` into `RefreshOrchestrator` | ✅ Done | #1613 |
| **G6** | Introduce tiered cache TTLs per command category without changing `SyncCoordinator` constructor signature | 📋 To Do | #1614 |
| **G7** | Enable automatic gzip/Brotli decompression on `HttpClient` | ✅ Done | #1616 |
| **G8** | Enable HTTP/2 multiplexing with HTTP/1.1 fallback | ✅ Done | #1616 |
| **G9** | In-memory `Task<T>?` caching for metadata endpoints (`GetProcessConfigurationAsync`, `GetFieldDefinitionsAsync`, `GetWorkItemTypesWithStatesAsync`) | ✅ Done | #1616 |
| **G10** | Complete test updates for parallel fetch verification and delegated refresh pattern | 🔄 Doing | #1658 |

### Non-Goals

- **NG1:** Changing the user-facing CLI contract (command names, flags, output format)
- **NG2:** Adding new NuGet dependencies
- **NG3:** Introducing an `ISyncCoordinator` interface — `SyncCoordinator` remains a concrete sealed class. A factory pattern provides tiered access without the abstraction overhead of an interface. See [Alternatives Considered](#alternatives-considered).
- **NG4:** Changing the `SyncCoordinator` constructor signature (DD-13 compatibility)
- **NG5:** Modifying eviction behavior (FR-013)
- **NG6:** User-configurable read-only TTL via `twig config` — the `CacheStaleMinutesReadOnly` property will default to 15 minutes. A `twig config display.cachestaleminutesreadonly <value>` command is deferred until there is a demonstrated user need. The property exists on `DisplayConfig` and can be set via config file if needed, but no `SetValue` case is added for interactive configuration.
- **NG7:** Achieving a specific percentage reduction in refresh wall-clock time — the degree of improvement depends on individual usage patterns (frequency of read-only commands within the 5–15 min window)
- **NG8:** Implementing server-side caching or CDN *(from v1)*
- **NG9:** Optimizing SQLite schema or indexing *(from v1)*

---

## Requirements

### Functional Requirements

- **FR-1:** `SyncCoordinator.FetchStaleAndSaveAsync` must use `GetByIdsAsync` for staleness checks and `FetchBatchAsync` for fetching stale items — ✅ Done (#1612)
- **FR-2:** Items confirmed deleted in ADO (not found in batch response) must still be evicted from local cache — ✅ Done (#1612)
- **FR-3:** `RefreshCommand` must delegate fetch/save logic to `RefreshOrchestrator.FetchItemsAsync` — ✅ Done (#1613)
- **FR-4:** Active item fetch and children fetch must run concurrently when both are needed — ✅ Done (#1613)
- **FR-5:** `ProcessTypeSyncService.SyncAsync` and `FieldDefinitionSyncService.SyncAsync` must run concurrently during refresh — ✅ Done (#1613)
- **FR-6:** Tiered TTLs must differentiate between read-only and mutating command categories — 📋 To Do (#1614)
  - **FR-6a:** `CacheStaleMinutesReadOnly` default = 15 minutes
  - **FR-6b:** Existing `CacheStaleMinutes` default unchanged at 5 minutes
- **FR-7:** Metadata endpoints (`GetProcessConfigurationAsync`, `GetFieldDefinitionsAsync`) must cache responses in-memory for the process lifetime, using a `Task<T>?` field pattern consistent with the existing `_workItemTypesCache` in `AdoIterationService` — ✅ Done (#1616)
- **FR-8:** `HttpClient` must request gzip and Brotli decompression — ✅ Done (#1616)

### Non-Functional Requirements

- **NFR-1:** Zero behavioral change for existing CLI commands (same output, same exit codes)
- **NFR-2:** AOT-compatible (`PublishAot=true`, `TrimMode=full`, `InvariantGlobalization=true`)
- **NFR-3:** No reflection-based JSON serialization (all new DTOs added to `TwigJsonContext`)
- **NFR-4:** `TreatWarningsAsErrors=true` compliance
- **NFR-5:** All existing tests must pass without modification (except test setup changes for new constructor signatures)

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│  CLI Commands (StatusCommand, TreeCommand, ShowCommand)       │
│  Issue #1614: Inject SyncCoordinatorFactory, use .ReadOnly   │
├─────────────────────────────────────────────────────────────┤
│  CLI Commands (SetCommand, LinkCommand, RefreshCommand)       │
│  Issue #1614: Inject SyncCoordinatorFactory, use .ReadWrite  │
├─────────────────────────────────────────────────────────────┤
│  Domain Services (StatusOrchestrator → .ReadOnly)            │
│  Domain Services (RefreshOrchestrator → .ReadWrite)          │
├─────────────────────────────────────────────────────────────┤
│  SyncCoordinatorFactory  ← NEW                               │
│    ├── ReadOnly  (SyncCoordinator w/ CacheStaleMinutesRO)   │
│    └── ReadWrite (SyncCoordinator w/ CacheStaleMinutes)     │
├─────────────────────────────────────────────────────────────┤
│  SyncCoordinator (unchanged — DD-13 preserved)               │
└─────────────────────────────────────────────────────────────┘
```

### Key Component: SyncCoordinatorFactory (Issue #1614)

**Design principle:** The `SyncCoordinator` constructor signature must not change (DD-13). Tiered TTLs are implemented via a factory that holds two pre-built coordinator instances.

```csharp
// src/Twig.Domain/Services/SyncCoordinatorFactory.cs
public sealed class SyncCoordinatorFactory
{
    public SyncCoordinatorFactory(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ProtectedCacheWriter protectedCacheWriter,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository? linkRepo,
        int readOnlyStaleMinutes,
        int readWriteStaleMinutes)
    {
        // Guard: read-only TTL must be >= read-write TTL to preserve design intent.
        // If a user sets display.cachestaleminutes to 20, the read-only default (15) would
        // invert the tiers, causing read-only commands to refresh MORE frequently than
        // mutating commands. Clamp read-only to at least the read-write value.
        if (readOnlyStaleMinutes < readWriteStaleMinutes)
            readOnlyStaleMinutes = readWriteStaleMinutes;

        // Edge case: 0 or negative values are accepted without error.
        // - 0 means "always stale" — every sync call triggers a network fetch.
        //   This is a valid (if aggressive) configuration for debugging or testing.
        // - Negative values behave identically to 0 because DateTimeOffset arithmetic
        //   with a negative TimeSpan.FromMinutes always evaluates as stale.
        // No ArgumentOutOfRangeException is thrown — this matches the existing
        // SyncCoordinator behavior where _cacheStaleMinutes is unchecked.

        ReadOnly = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readOnlyStaleMinutes);
        ReadWrite = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readWriteStaleMinutes);
    }

    public SyncCoordinator ReadOnly { get; }
    public SyncCoordinator ReadWrite { get; }
}
```

**Command classification:**

| Category | Commands | TTL | Property |
|----------|----------|-----|----------|
| Read-only | `StatusCommand`, `TreeCommand`, `ShowCommand`, `StatusOrchestrator` | `CacheStaleMinutesReadOnly` (15) | `factory.ReadOnly` |
| Mutating | `SetCommand`, `LinkCommand`, `RefreshCommand`, `RefreshOrchestrator` | `CacheStaleMinutes` (5) | `factory.ReadWrite` |

**StatusCommand tier rationale:** `StatusCommand` calls both `SyncWorkingSetAsync` (working set sync) and `SyncLinksAsync` (link cache sync). Both are cache-warming operations for display — neither represents a user mutation. `SyncLinksAsync` writes fetched link data to the local cache but is semantically read-only from the user's perspective. Therefore both use `.ReadOnly`.

**MCP tier:** MCP tool classes (`ContextTools`, `ReadTools`, `MutationTools`) inject `SyncCoordinator` directly — they are unaffected by the factory. However, MCP _does_ register `StatusOrchestrator` and `RefreshOrchestrator`, which will change to accept `SyncCoordinatorFactory`. Therefore MCP's `Program.cs` must register the factory (both tiers = `CacheStaleMinutes`) and update orchestrator registrations. A backward-compat `SyncCoordinator` registration (`factory.ReadWrite`) ensures the tool classes continue resolving.

### TTL Scope Clarification

The tiered TTL only applies to operations that perform per-item staleness checks before fetching:
- **`SyncWorkingSetAsync`** — checks `LastSyncedAt` per item, skips fresh items → TTL-gated ✅
- **`SyncItemSetAsync`** — checks `LastSyncedAt` per item, skips fresh items → TTL-gated ✅
- **`SyncItemAsync`** — checks `LastSyncedAt` for a single item → TTL-gated ✅

The following operations **always fetch unconditionally** (no TTL check) and are unaffected by the tiered TTL:
- **`SyncChildrenAsync`** — always calls `FetchChildrenAsync(parentId)` and saves all children (DD-15: unconditional children fetch)
- **`SyncLinksAsync`** — always calls `FetchWithLinksAsync(itemId)` and saves the item + links

This means the tiered TTL's impact is concentrated on `SyncWorkingSetAsync` calls in read-only commands (`StatusCommand`, `TreeCommand`) and `SyncItemSetAsync` calls in `ShowCommand`. The `SyncLinksAsync` calls in `StatusCommand` and `TreeCommand` still fetch unconditionally regardless of tier.

### Configuration Change

```csharp
public sealed class DisplayConfig
{
    public int CacheStaleMinutes { get; set; } = 5;          // mutating commands
    public int CacheStaleMinutesReadOnly { get; set; } = 15; // display commands
}
```

Config key: `display.cachestaleminutesreadonly` — property exists on `DisplayConfig` for config-file-based overrides. Interactive `twig config` support deferred (NG6).

### DI Registration Changes

**CLI (`CommandServiceModule.cs`):**
```csharp
// Replace SyncCoordinator with SyncCoordinatorFactory
services.AddSingleton<SyncCoordinatorFactory>(sp => new SyncCoordinatorFactory(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<IWorkItemLinkRepository>(),
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutesReadOnly,
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));

// Backward compat — register SyncCoordinator for any remaining direct consumers
services.AddSingleton(sp => sp.GetRequiredService<SyncCoordinatorFactory>().ReadWrite);
```

**MCP (`Program.cs`):**

MCP registers `StatusOrchestrator` and `RefreshOrchestrator`, which will accept `SyncCoordinatorFactory` after T-1614-4/T-1614-5. Both tiers use `CacheStaleMinutes` (MCP tools are agent-driven and need fresh data):

```csharp
// Register factory — both tiers use CacheStaleMinutes (no read-only distinction in MCP)
builder.Services.AddSingleton(sp =>
{
    var staleMinutes = sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes;
    return new SyncCoordinatorFactory(
        sp.GetRequiredService<IWorkItemRepository>(),
        sp.GetRequiredService<IAdoWorkItemService>(),
        sp.GetRequiredService<ProtectedCacheWriter>(),
        sp.GetRequiredService<IPendingChangeStore>(),
        sp.GetRequiredService<IWorkItemLinkRepository>(),
        readOnlyStaleMinutes: staleMinutes,
        readWriteStaleMinutes: staleMinutes);
});

// Backward compat — MCP tool classes (ContextTools, ReadTools, MutationTools) inject SyncCoordinator directly
builder.Services.AddSingleton(sp => sp.GetRequiredService<SyncCoordinatorFactory>().ReadWrite);
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Factory pattern for tiered TTLs | Avoids changing `SyncCoordinator` constructor (DD-13), avoids introducing an interface (NG3), keeps Domain layer clean. Commands inject factory and pick the appropriate tier. See [Alternatives Considered](#alternatives-considered). |
| `CacheStaleMinutesReadOnly` default = 15 | Meaningful staleness relief for display commands without noticeable user impact. Property exists on `DisplayConfig`; interactive `twig config` support deferred (NG6). |
| TTL inversion guard in factory constructor | If a user sets `display.cachestaleminutes` to 20, the read-only default (15) would cause read-only commands to refresh more frequently than mutating commands — inverting the design intent. The factory clamps `readOnlyStaleMinutes` to `max(readOnlyStaleMinutes, readWriteStaleMinutes)`. Silent clamping chosen over throwing because a configuration mistake should degrade gracefully. |
| Backward-compatible `SyncCoordinator` registration | Registering `sp => factory.ReadWrite` as `SyncCoordinator` ensures any unconverted consumers still resolve correctly. Safety net during migration. |
| MCP uses RW for both tiers | MCP tool classes inject `SyncCoordinator` directly and are agent-driven; they need fresh data. MCP registers `SyncCoordinatorFactory` (both tiers = `CacheStaleMinutes`) because orchestrators require it, plus a backward-compat `SyncCoordinator` registration for tool classes. |

---

## Alternatives Considered

### 1. `ISyncCoordinator` Interface (NG3)

**Approach:** Define an `ISyncCoordinator` interface and register two named/keyed implementations — one for read-only, one for read-write. Commands inject `ISyncCoordinator` with a qualifier attribute or keyed DI.

**Pros:**
- Standard DI pattern; familiar to .NET developers
- Enables mocking in tests without constructing real instances

**Cons:**
- Adds a new abstraction layer for a class with a stable API — `SyncCoordinator` is sealed and has no planned variants
- Keyed DI or named registration adds complexity (especially with AOT/source-gen constraints)
- Every consumer must know the key/qualifier, coupling them to the tier concept
- `SyncCoordinator` is a concrete sealed class by design (NG3 from v1); introducing an interface reverses that decision

**Decision:** Rejected. The factory pattern achieves the same tiered access with less ceremony. Consumers inject `SyncCoordinatorFactory` and explicitly choose `.ReadOnly` or `.ReadWrite`, making the tier selection visible in code.

### 2. Parameter Passing (TTL as Method Argument)

**Approach:** Add a `cacheStaleMinutes` parameter to each `Sync*Async` method, allowing callers to pass their preferred TTL per call.

**Pros:**
- No new types; minimal API surface change
- Per-call granularity (different TTLs for different operations within one command)

**Cons:**
- Violates DD-13's intent — the `cacheStaleMinutes` is a construction-time concern, not a per-call concern
- Pollutes every sync method signature with a parameter that is constant per command
- Requires every caller to know and pass the correct value, risking inconsistency
- Breaking change to all existing call sites

**Decision:** Rejected. TTL is a per-command-category concern, not a per-call concern. The factory captures this at DI registration time, keeping method signatures clean.

### 3. Two Separate DI Registrations (No Factory)

**Approach:** Register two `SyncCoordinator` instances in DI using keyed services — `"ReadOnly"` and `"ReadWrite"` — and inject them with `[FromKeyedServices]`.

**Pros:**
- No new `SyncCoordinatorFactory` class
- Leverages .NET 8+ keyed services

**Cons:**
- `[FromKeyedServices]` attribute is not AOT-friendly with all DI containers
- ConsoleAppFramework's source-generated DI may not support keyed injection
- Less discoverable than a factory — the tier selection is hidden in attributes rather than explicit property access

**Decision:** Rejected. The factory is explicit, AOT-safe, and works with ConsoleAppFramework's source-generated DI without special attributes.

---

## Dependencies

### Prerequisites (must be true before starting remaining work)

| Dependency | Status | Required By |
|------------|--------|-------------|
| Issue #1612 (Batch sync) merged | ✅ Done | Issue #1614 (factory wraps `SyncCoordinator` whose constructor depends on batch APIs) |
| Issue #1616 (HTTP transport) merged | ✅ Done | None — independent optimization |
| Issue #1613 tasks T-1613-1 through T-1613-3 merged | ✅ Done | Issue #1614 preferred (to avoid merge conflicts in shared test files); not strictly required |
| Issue #1613 task #1658 (test updates) | 🔄 Doing | Should complete before PG-4 (T-1614-6 test migration) to avoid double-editing `RefreshOrchestratorTests.cs` and `RefreshCommandTests.cs` |

### Internal Dependencies (between remaining work items)

| Dependency | Rationale |
|------------|-----------|
| T-1614-2 (factory) must complete before T-1614-3 (DI registration) | DI registers the factory — factory must exist first |
| T-1614-3 (DI) must complete before T-1614-4 and T-1614-5 (command migration) | Commands inject the factory — DI must register it first |
| T-1614-4 and T-1614-5 (command migration) must complete before T-1614-6 (test migration) | Tests construct commands — command signatures must be final first |
| T-1614-2 (factory) must complete before T-1614-7 (factory tests) | Factory must exist before its tests |
| T-1614-1 (config) can run in parallel with T-1614-2 | Config and factory are independent files |
| #1658 should complete before T-1614-6 | Avoids double-editing `RefreshOrchestratorTests.cs` |

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `SyncCoordinatorFactory.cs` | New file | Low — simple factory with inversion guard |
| `DisplayConfig` | Add property | Low — additive |
| `TwigConfiguration.cs` | Add `CacheStaleMinutesReadOnly` (default 15) | Low — additive, no `SetValue` case (NG6) |
| `CommandServiceModule.cs` | DI registration change | Low — mechanical |
| `Program.cs` (MCP) | DI registration change | Low — mechanical, mirrors CLI pattern |
| `StatusCommand.cs` | Inject factory, use `.ReadOnly` | Low — mechanical |
| `TreeCommand.cs` | Inject factory, use `.ReadOnly` | Low — mechanical |
| `ShowCommand.cs` | Inject factory, use `.ReadOnly` | Low — mechanical |
| `SetCommand.cs` | Inject factory, use `.ReadWrite` | Low — mechanical |
| `LinkCommand.cs` | Inject factory, use `.ReadWrite` | Low — mechanical |
| `StatusOrchestrator.cs` | Inject factory, use `.ReadOnly` | Low — mechanical |
| `RefreshOrchestrator.cs` | Inject factory, use `.ReadWrite` | Low — mechanical |
| `RefreshCommand.cs` | No change needed — delegates to orchestrator | None |
| ~25 test files | Factory migration (direct code edits) | Medium — wide, mechanical |
| `SyncCoordinatorFactoryTests.cs` | New test file | Low |

### Backward Compatibility

- **CLI contract:** Unchanged. Same commands, flags, output formats.
- **Configuration:** New `display.cachestaleminutesreadonly` key. Existing `display.cachestaleminutes` unchanged. Missing key uses default (15 min).
- **DI:** `SyncCoordinator` remains resolvable as `factory.ReadWrite` for backward compat.
- **TTL inversion:** Factory silently clamps `readOnlyStaleMinutes ≥ readWriteStaleMinutes`. No error thrown — graceful degradation.

---

## Security Considerations

This design does not change authentication, authorization, data protection, or network trust boundaries. All sync operations continue to use the existing `HttpClient` with per-request Azure DevOps PAT authentication. The tiered TTL adjusts local cache freshness thresholds only — it does not alter what data is fetched, stored, or transmitted.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Factory injection breaks test setup | Medium | Medium | 25 test files need direct migration + 4 auto-fixed via base class = 29 total. Pattern is mechanical: replace `new SyncCoordinator(…)` with `new SyncCoordinatorFactory(…)`. Inline construction preferred (matches existing convention). |
| TTL inversion from user config | Low | Medium | If `display.cachestaleminutes` is set higher than `display.cachestaleminutesreadonly` (default 15), read-only commands would refresh more than mutating commands. Factory constructor clamps `readOnlyStaleMinutes ≥ readWriteStaleMinutes` to prevent this. |
| MCP DI resolution fails | Low | High | `ProgramBootstrapTests` validates DI graph. Update factory registration and orchestrator registrations in MCP `Program.cs` before running MCP tests. |
| Missing call site in migration | Low | Medium | Call-site audit table above is exhaustive. Build + test will catch any missed sites via constructor parameter mismatch. |
| PR review difficulty for PG-4 (~38 files) | Medium | Low | Changes are self-consistent: each file follows the same mechanical pattern (replace `SyncCoordinator` → `SyncCoordinatorFactory`). Classify as **wide** PR to set reviewer expectations. Alphabetical file ordering aids review. |

---

## Resolved Design Questions

All design questions have been resolved. No blocking open questions remain.

| # | Question | Resolution | Version |
|---|----------|------------|---------|
| DQ-1 | Should `SyncCoordinator` be replaced with an `ISyncCoordinator` interface for tiered access? | No — factory pattern provides tiered access without abstraction overhead. `SyncCoordinator` remains a concrete sealed class (NG3). See [Alternatives Considered](#alternatives-considered). | v2.0 |
| DQ-2 | Does MCP need `SyncCoordinatorFactory` registration? | Yes — MCP registers `StatusOrchestrator` and `RefreshOrchestrator`, which accept `SyncCoordinatorFactory`. Both tiers use `CacheStaleMinutes` (no read-only distinction in MCP). A backward-compat `SyncCoordinator` registration (`factory.ReadWrite`) is added for tool classes. | v2.3 |
| DQ-3 | Should `twig config` support interactive `CacheStaleMinutesReadOnly` configuration? | Deferred (NG6). The property exists on `DisplayConfig` for config-file overrides, but no `SetValue` case is added for interactive configuration until user need is demonstrated. | v2.2 |
| DQ-4 | How should TTL inversion (read-only < read-write) be handled? | Factory constructor clamps `readOnlyStaleMinutes` to `max(readOnlyStaleMinutes, readWriteStaleMinutes)`. Silent clamping chosen over throwing — configuration mistakes degrade gracefully. | v2.4 |
| DQ-5 | What happens with zero or negative TTL values? | 0 means "always stale" (every sync triggers a fetch) — valid for debugging. Negative values behave identically to 0. No `ArgumentOutOfRangeException` — matches existing `SyncCoordinator` unchecked behavior. | v2.5 |
| DQ-6 | Should test files use a shared factory helper or inline construction? | Inline construction preferred — matches existing convention where each test constructs its own `SyncCoordinator`. No shared helper needed. | v2.5 |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/SyncCoordinatorFactory.cs` | Factory providing read-only and read-write `SyncCoordinator` instances with TTL inversion guard |
| `tests/Twig.Domain.Tests/Services/SyncCoordinatorFactoryTests.cs` | Factory smoke tests: property construction, TTL inversion clamping, tier behavior verification |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `CacheStaleMinutesReadOnly` to `DisplayConfig` (default 15). No `SetValue` case — interactive `twig config` deferred (NG6). |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `SyncCoordinatorFactory`; add backward-compat `SyncCoordinator` registration; update `StatusOrchestrator` and `RefreshOrchestrator` registrations |
| `src/Twig.Mcp/Program.cs` | Register `SyncCoordinatorFactory` (both tiers = `CacheStaleMinutes`); update orchestrator registrations; add backward-compat `SyncCoordinator` registration |
| `src/Twig.Domain/Services/StatusOrchestrator.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadOnly` |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadWrite` |
| `src/Twig/Commands/StatusCommand.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadOnly` |
| `src/Twig/Commands/TreeCommand.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadOnly` |
| `src/Twig/Commands/ShowCommand.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadOnly` |
| `src/Twig/Commands/SetCommand.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadWrite` |
| `src/Twig/Commands/LinkCommand.cs` | Change `SyncCoordinator` parameter to `SyncCoordinatorFactory`; use `.ReadWrite` |
| `src/Twig/Commands/RefreshCommand.cs` | Confirmed: no direct `SyncCoordinator` usage — delegates entirely to `RefreshOrchestrator`. No changes needed for #1614. |
| `tests/Twig.Domain.Tests/Services/RefreshOrchestratorTests.cs` | Update for factory injection + parallel fetch verification (#1658) |
| `tests/Twig.Domain.Tests/Services/StatusOrchestratorTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTestBase.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/LinkCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/ShowCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/CacheFirstReadCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/CommandFormatterWiringTests.cs` | Update for factory injection (3 sites) |
| `tests/Twig.Cli.Tests/Rendering/CacheRefreshTests.cs` | Update for factory injection (2 sites) |
| `tests/Twig.Cli.Tests/Commands/NavigationCommandsInteractiveTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/NextPrevCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/OfflineModeTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | Update for factory injection (3 sites) |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandDeprecationTests.cs` | Update via base class |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandProfileTests.cs` | Update via base class |
| `tests/Twig.Cli.Tests/Commands/RefreshDirtyGuardTests.cs` | Update via base class |
| `tests/Twig.Cli.Tests/Commands/SetCommandDisambiguationTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/SyncCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeCommand_CacheAwareTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | Update for factory injection |
| `tests/Twig.Cli.Tests/Commands/WorkingSetCommandTests.cs` | Update for factory injection |
| `tests/Twig.Mcp.Tests/ProgramBootstrapTests.cs` | Update to mirror MCP DI changes: register `SyncCoordinatorFactory`, update orchestrator registrations, add backward-compat `SyncCoordinator` |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsTestBase.cs` | Update `CreateStatusOrchestrator()` to create `SyncCoordinatorFactory` and pass it; `CreateSut()` auto-fixed |

---

## ADO Work Item Structure

### Epic #1611: Sync Performance Optimization (existing)

---

### Issue #1612: Replace N+1 FetchAsync with FetchBatchAsync in SyncCoordinator (existing — DONE ✅)

**Status:** Done — all 4 tasks completed and merged.

---

### Issue #1616: HTTP transport optimizations (compression, HTTP/2, in-memory caching) (existing — DONE ✅)

**Status:** Done — all 3 tasks completed and merged.

---

### Issue #1613: Parallelize network calls and deduplicate refresh logic (existing — DOING 🔄)

**Goal:** Parallelize independent ADO fetch calls in `RefreshOrchestrator.FetchItemsAsync`, parallelize post-refresh metadata syncs in `RefreshCommand`, and consolidate `RefreshCommand` inline logic that duplicates `RefreshOrchestrator`.

**Prerequisites:** Issue #1612 merged ✅

**Tasks:**

| Task ID | Description | Files | Status |
|---------|-------------|-------|--------|
| T-1613-1 | Parallelize `FetchAsync(activeId)` + `FetchChildrenAsync(activeId)` in `RefreshOrchestrator.FetchItemsAsync` using `Task.WhenAll` | `RefreshOrchestrator.cs` | ✅ Done |
| T-1613-2 | Remove inline fetch/save/conflict logic from `RefreshCommand.ExecuteCoreAsync`; delegate to `RefreshOrchestrator.FetchItemsAsync(wiql, force, ct)` | `RefreshCommand.cs` | ✅ Done |
| T-1613-3 | Parallelize `ProcessTypeSyncService.SyncAsync` + `FieldDefinitionSyncService.SyncAsync` in `RefreshCommand` post-refresh section | `RefreshCommand.cs` | ✅ Done |
| #1658 | Update `RefreshOrchestratorTests` for parallel fetch verification and `RefreshCommandTests` for delegated pattern | `RefreshOrchestratorTests.cs`, `RefreshCommandTests.cs` | 🔄 Doing |

**Acceptance Criteria:**
- [x] `RefreshOrchestrator.FetchItemsAsync` runs active item fetch and children fetch concurrently
- [x] `RefreshCommand.ExecuteCoreAsync` no longer contains inline fetch/save/conflict logic
- [x] `ProcessTypeSyncService` and `FieldDefinitionSyncService` run concurrently during refresh
- [ ] All `RefreshCommandTests` and `RefreshOrchestratorTests` pass with new parallel/delegated patterns
- [x] Total line count of `RefreshCommand.cs` reduced by ≥100 lines

**Remaining work for #1658:**

1. **RefreshOrchestratorTests updates:**
   - Verify that `FetchAsync(activeId)` and `FetchChildrenAsync(activeId)` are called (confirming delegation works)
   - Verify that when `activeId` is in the sprint batch, only `FetchChildrenAsync` runs (conditional gate)

2. **RefreshCommandTests updates:**
   - Verify that `ExecuteCoreAsync` delegates to `orchestrator.FetchItemsAsync` (not inline fetch)
   - Verify post-refresh metadata syncs run (process types + field definitions)
   - Verify `Task.WhenAll` pattern for concurrent metadata syncs
   - Remove/update any tests that assert inline fetch/save behavior no longer present

---

### Issue #1614: Tiered cache TTL for read-only vs read-write commands (existing — TO DO 📋)

**Goal:** Introduce tiered cache TTLs so read-only display commands tolerate longer staleness (15 min) while mutating commands maintain aggressive freshness (5 min), without changing the `SyncCoordinator` constructor signature.

**Prerequisites:** Issue #1612 merged ✅. Issue #1613 completion preferred (to avoid merge conflicts in test files) but not strictly required.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1614-1 | Add `CacheStaleMinutesReadOnly` property to `DisplayConfig` with default 15. No `SetValue` case — interactive `twig config` deferred (NG6). | `TwigConfiguration.cs` | ~10 LoC | TO DO |
| T-1614-2 | Create `SyncCoordinatorFactory` with `ReadOnly` and `ReadWrite` properties. Include TTL inversion guard: clamp `readOnlyStaleMinutes` to `max(readOnlyStaleMinutes, readWriteStaleMinutes)` to prevent read-only commands from refreshing more frequently than mutating commands when user overrides `display.cachestaleminutes`. | `SyncCoordinatorFactory.cs` (new) | ~40 LoC | TO DO |
| T-1614-3 | Update DI registration: CLI `CommandServiceModule.cs` — register `SyncCoordinatorFactory`, add backward-compat `SyncCoordinator` as `factory.ReadWrite`. MCP `Program.cs` — register `SyncCoordinatorFactory` (both tiers = `CacheStaleMinutes`), update `StatusOrchestrator` and `RefreshOrchestrator` registrations to pass factory, add backward-compat `SyncCoordinator` registration for tool classes. | `CommandServiceModule.cs`, `Program.cs` (MCP) | ~30 LoC | TO DO |
| T-1614-4 | Update read-only commands (`StatusCommand`, `TreeCommand`, `ShowCommand`) and `StatusOrchestrator` to inject `SyncCoordinatorFactory` and use `.ReadOnly`. `StatusCommand` uses `.ReadOnly` for both `SyncWorkingSetAsync` and `SyncLinksAsync`. | `StatusCommand.cs`, `TreeCommand.cs`, `ShowCommand.cs`, `StatusOrchestrator.cs` | ~30 LoC | TO DO |
| T-1614-5 | Update mutating commands (`SetCommand`, `LinkCommand`) and `RefreshOrchestrator` to inject `SyncCoordinatorFactory` and use `.ReadWrite`. `RefreshCommand` confirmed to have no direct `SyncCoordinator` usage — delegates entirely to `RefreshOrchestrator`. No changes needed for `RefreshCommand`. | `SetCommand.cs`, `LinkCommand.cs`, `RefreshOrchestrator.cs` | ~25 LoC | TO DO |
| T-1614-6 | Update ~25 test files for factory injection pattern. **Preferred approach: inline construction** — replace `new SyncCoordinator(…, staleMinutes)` with `new SyncCoordinatorFactory(…, readOnlyStaleMinutes: 30, readWriteStaleMinutes: 30)` in each test's constructor. This matches the existing convention where each test constructs its own `SyncCoordinator` inline; no shared helper is needed. In `ContextToolsTestBase.cs`, update `CreateSyncCoordinator()` to return a `SyncCoordinatorFactory` (or adapt callers to use one). **⚠️ Note:** This file uses target-typed `new()` syntax (line 13: `new(…)` instead of `new SyncCoordinator(…)`) — grep searches for `new SyncCoordinator` will miss it. The method `CreateSyncCoordinator()` has 1 instantiation site (line 13) but is called from **3 places**: `CreateStatusOrchestrator()` (line 20), `CreateSut()` (line 27), and `MutationToolsTestBase.CreateMutationSut()` (line 23) — all three call sites will produce factory instances post-migration and must be updated to use the appropriate tier (`.ReadOnly` for `StatusOrchestrator`, `.ReadWrite` for `ContextTools` and `MutationTools`). In `ProgramBootstrapTests.cs`, mirror the updated MCP DI graph. Use same TTL value (30) for both tiers in test code where the distinction doesn't matter. | Multiple test files (see Files Affected) | ~120 LoC | TO DO |
| T-1614-7 | Create `SyncCoordinatorFactoryTests.cs` with: (a) smoke test verifying `ReadOnly` and `ReadWrite` property construction with the correct TTL values, and (b) TTL inversion guard test verifying that `readOnlyStaleMinutes < readWriteStaleMinutes` results in `ReadOnly` using the `readWriteStaleMinutes` value. | `SyncCoordinatorFactoryTests.cs` (new) | ~40 LoC | TO DO |

**Acceptance Criteria:**
- [ ] `SyncCoordinator` constructor signature unchanged (DD-13)
- [ ] Read-only commands use `CacheStaleMinutesReadOnly` (default 15)
- [ ] Mutating commands use `CacheStaleMinutes` (default 5)
- [ ] `SyncCoordinatorFactory` clamps `readOnlyStaleMinutes ≥ readWriteStaleMinutes`
- [ ] `SyncCoordinatorFactoryTests` verify property construction and inversion guard
- [ ] MCP registers `SyncCoordinatorFactory` (both tiers = `CacheStaleMinutes`) and backward-compat `SyncCoordinator`
- [ ] All existing tests pass
- [ ] `SyncCoordinator` remains resolvable via DI (backward compat registration)

---

## PR Groups

| PR Group | Title | Issues/Tasks | Type | Est. LoC | Predecessors | Status |
|----------|-------|-------------|------|----------|--------------|--------|
| PG-3b | Refresh test updates | #1613: #1658 | **deep** (few files, test verification) | ~80 | PG-3 | ✅ Merged |
| PG-4 | Tiered cache TTL (config + factory + DI + commands + tests) | #1614: all tasks | **wide** (~38 files, mechanical DI wiring) | ~335 | PG-2 | 📋 To Do |

**Execution order:**
```
PG-3b (finish #1658 tests) ──── parallel ────────┐
                                                  │ both complete
PG-4 (all #1614 tasks in one PR) ── parallel ────→ Epic #1611 done
```

PG-3b and PG-4 can execute in **parallel** — they share no file edits (PG-3b touches `RefreshOrchestratorTests.cs` and `RefreshCommandTests.cs`; PG-4 also touches `RefreshOrchestratorTests.cs`, but the #1658 test updates should land first per the dependency note to avoid double-editing). Both must complete before Epic #1611 can be marked Done.

---

## References

- DD-8: Per-item `LastSyncedAt` staleness — `SyncCoordinator.cs` comments
- DD-13: `int cacheStaleMinutes` primitive to avoid Domain→Infrastructure dependency — `CommandServiceModule.cs:45`
- DD-15: Unconditional children fetch — `SyncCoordinator.SyncChildrenAsync` comments
- FR-013: No eviction during working-set sync
- NFR-003: Batch protected save computes protected IDs once — `ProtectedCacheWriter`
- v1 plan: `docs/projects/sync-perf-optimization.plan.md`

---

## Revision History

| Version | Changes |
|---------|---------|
| v2.0 | Initial v2 plan: tiered TTL design with `SyncCoordinatorFactory`, command classification, DI registration strategy, alternatives considered. |
| v2.1 | Added backward-compat `SyncCoordinator` DI registration for `ContextChangeService` and unconverted consumers. |
| v2.2 | Deferred FR-6c (user-configurable read-only TTL via `twig config`) to NG6. |
| v2.3 | Resolved MCP Program.cs contradiction: MCP DOES need factory registration because orchestrators accept `SyncCoordinatorFactory`. Added `ProgramBootstrapTests` and `ContextToolsTestBase` to migration scope (29 files, up from 27). Renamed Open Questions → Resolved Design Questions. Specified preferred test helper approach for T-1614-6. |
| v2.4 | Revised per tech (88) / readability (83) review feedback. Key changes: (1) Added TTL inversion guard to `SyncCoordinatorFactory` constructor — clamps `readOnlyStaleMinutes ≥ readWriteStaleMinutes`. (2) Added T-1614-7 for `SyncCoordinatorFactoryTests` with factory smoke tests, inversion guard verification, and tier behavior tests. (3) Restated G1–G10 in-document for standalone readability (was G6–G7 with reference). (4) Added FR-1 through FR-8 and NFR-1 through NFR-5 summaries inline (was cross-reference). (5) Restored "Open Questions" section name. (6) Added Revision History appendix. (7) Reconciled file counts: body and Risks table both say 29 files need migration. (8) Updated PG-4 to ~38 files / ~335 LoC. (9) Added TTL inversion risk and PR review difficulty risk to Risks table. |
| v2.5 | Revised per tech (90) / readability (86) review feedback. Key changes: (1) **Goals**: Enumerated all G1–G10 in-document with full descriptions and status badges — v2.4 claimed this was done but only had parenthetical references. (2) **Requirements**: Enumerated FR-1 through FR-8 in-document with full descriptions and status badges — v2.4 had only a parenthetical reference. Expanded NFRs from single prose sentence to itemized NFR-1 through NFR-5 bullet list for verifiable compliance. (3) **Section reorder**: Moved Dependencies section before Impact Analysis and Risks to restore logical flow (design decisions → what they depend on → what they affect → what can go wrong). (4) **Open Questions → Resolved Design Questions**: Renamed section and added lead-in sentence to clarify no blocking questions remain. Added zero/negative TTL resolution (v2.5). (5) **5-param constructor**: Added explanatory paragraph in call-site audit noting the secondary 5-parameter `SyncCoordinator` constructor (delegates to 6-param with `linkRepo: null`); factory uses 6-param exclusively. (6) **Zero/negative TTL edge case**: Documented in factory constructor code block — 0 = "always stale" (valid for debugging), negative behaves identically; matches existing `SyncCoordinator` unchecked behavior. (7) **PG-4 reviewer guidance**: Added ordered review strategy (config → factory → factory tests → DI → commands/orchestrators → test files) with LoC estimates per step to reduce reviewer friction for the ~38-file wide PR. |
| v2.6 | Revised per tech (93) / readability (88) review feedback. Key changes: (1) **Goals table**: Replaced abbreviated G1–G10 references with a full table listing all 10 goals, one-line descriptions, status badges, and associated Issue numbers — the v2.5 revision claimed this was done but the document still only described G6 and G10 explicitly. (2) **Resolved Design Questions section**: Added the missing section with a table of 6 resolved questions (DQ-1 through DQ-6), each with resolution text and version — the v2.5 revision history stated this section was added but it was not present in the document body. Also added an explicit Open Questions stub confirming no blocking questions remain. (3) **Security Considerations section**: Added one-paragraph acknowledgment that the design does not change authentication, authorization, data protection, or network trust boundaries. (4) **PR Groups diagram**: Labeled arrows with "parallel" and "both complete" annotations to clarify that PG-3b and PG-4 execute concurrently. Added note about `RefreshOrchestratorTests.cs` shared between PG-3b and PG-4 dependency. (5) **T-1614-6 migration note**: Added warning that `ContextToolsTestBase.cs` uses target-typed `new()` syntax (line 13) — grep for `new SyncCoordinator` will miss this file. (6) **ContextToolsTestBase call-site audit**: Expanded audit entry to clarify that `CreateSyncCoordinator()` is called from 3 places (lines 20, 27 within file + `MutationToolsTestBase` line 23), each requiring the appropriate tier selection post-migration (`.ReadOnly` for `StatusOrchestrator`, `.ReadWrite` for `ContextTools` and `MutationTools`). |