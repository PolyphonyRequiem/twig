---
goal: "Cache-First Architecture — instant display, dirty-safe sync, modular DI"
version: 1.0
date_created: 2026-03-17
owner: Daniel Green
tags: architecture, performance, ux, refactor
---

# Introduction

Twig currently blocks on network fetches in most commands before displaying any output. This initiative redesigns the fetch/display lifecycle around a **cache-render-fetch-revise** principle: every command renders from cache immediately, then syncs in the background if stale, revising the display in-place. It also addresses a dirty-value overwrite bug in `SetCommand` and introduces modular DI to reduce constructor bloat and improve testability.

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: This document uses standardized prefixes for traceability — `FR-` (functional requirements), `NFR-` (non-functional requirements), `FM-` (failure modes), `AC-` (acceptance criteria), and `RD-` (resolved decisions).

## 1. Goals and Non-Goals

- **Goal 1**: Sub-100ms perceived latency for all read commands (set, status, tree, up, down, workspace)
- **Goal 2**: Never silently overwrite locally dirty data during background fetches
- **Goal 3**: Automatically fetch when context points to an uncached item instead of failing
- **Goal 4**: Single in-place sync status indicator (not log-style step sequences) for non-process commands
- **Goal 5**: Reduce DI registration complexity and command constructor parameter counts through modular vertical slicing
- **Goal 6**: Establish shared architectural patterns that all commands follow, eliminating per-command fetch/display logic duplication

### In Scope

- All context commands: `set`, `status`, `tree`, `workspace`, `sprint`
- All navigation commands: `up`, `down`
- All work item mutation commands: `state`, `note`, `update`, `edit`, `save`
- Infrastructure: `init`, `refresh`
- DI modularization of `Program.cs`
- New shared services: `ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator`
- New rendering primitive: `RenderWithSyncAsync` on `IAsyncRenderer`

### Out of Scope (deferred)

- `seed` command — creation workflow is orthogonal to cache-first reads
- `flow-start`, `flow-done`, `flow-close` — these are multi-step *processes* that correctly show step-by-step progress; they will adopt `ProtectedCacheWriter` and `ActiveItemResolver` but keep their sequential UX
- Git commands (`branch`, `commit`, `pr`, `stash`, `log`, `context`, `hooks`) — already cache-optimal or inherently require git operations
- System commands (`config`, `version`, `upgrade`, `changelog`, `ohmyposh`, `tui`) — no fetch behavior to change

## 2. Terminology

| Term | Definition |
|------|------------|
| Cache-render-fetch-revise | The command lifecycle: (1) render from cache immediately, (2) show sync indicator if background fetch is running, (3) revise rendered output if new data arrives, (4) dismiss indicator |
| Protected save | A `SaveAsync`/`SaveBatchAsync` variant that skips items with pending changes (as determined by `SyncGuard`) |
| Active item resolution | The process of resolving the active work item from cache, auto-fetching from ADO on cache miss |
| Sync indicator | A single in-place status line showing fetch progress (e.g., `⟳ syncing...` → `✓ up to date` or `✓ 3 items updated`) |
| Process command | A multi-step orchestration command (`init`, `flow-start`, `flow-done`, `flow-close`) that shows sequential step progress rather than in-place revision |
| Stale | Cache data older than `display.cacheStaleMinutes` (default: 5 minutes) |
| Dirty | A work item with rows in `pending_changes` or `is_dirty = 1` in `work_items` |
| Command module | A DI registration group that registers only the services needed by a logical command slice |

## 3. Solution Architecture

### 3.1 Current Architecture (problems)

```
┌─────────────┐     ┌──────────────┐     ┌───────────┐
│  Command     │────▶│  ADO Service  │────▶│  Display   │
│  (fetch first)│     │  (blocking)   │     │  (finally) │
└─────────────┘     └──────────────┘     └───────────┘
```

**Issues:**
1. Commands fetch before displaying → user waits 1-5s seeing nothing
2. `SetCommand.FetchChildrenAsync` + `SaveBatchAsync` overwrites dirty children (no SyncGuard check)
3. `StatusCommand`, `TreeCommand`, etc. fail with "not found in cache" if active item was evicted instead of auto-fetching
4. 58 DI registrations in a monolithic block; `FlowStartCommand` has 13 constructor parameters
5. Each command implements its own fetch/display/error pattern — no shared lifecycle

### 3.2 Target Architecture

```
┌───────────┐     ┌──────────────────┐     ┌──────────────────┐
│  Command   │────▶│  ActiveItemResolver │──▶│  Render (cached)  │
│            │     │  (cache or fetch) │     │  immediately      │
└───────────┘     └──────────────────┘     └────────┬─────────┘
                                                     │
                                           ┌─────────▼──────────┐
                                           │  SyncCoordinator    │
                                           │  (background fetch  │
                                           │   if stale)         │
                                           └─────────┬──────────┘
                                                     │
                                           ┌─────────▼──────────┐
                                           │  Revise display     │
                                           │  (in-place update   │
                                           │   or "up to date")  │
                                           └─────────┬──────────┘
                                                     │
                                           ┌─────────▼──────────┐
                                           │  ProtectedCacheWriter│
                                           │  (skip dirty items) │
                                           └────────────────────┘
```

### 3.3 New Shared Services

#### ActiveItemResolver

Replaces the repeated pattern across 15+ commands:
```csharp
var activeId = await contextStore.GetActiveWorkItemIdAsync();
if (activeId is null) { /* error */ return 1; }
var item = await workItemRepo.GetByIdAsync(activeId.Value);
if (item is null) { /* error */ return 1; }
```

Becomes:
```csharp
var result = await resolver.GetActiveItemAsync(ct);
// result: ActiveItemResult.Found(item) | .NoContext | .NotCached(id) → auto-fetch
```

On cache miss, `ActiveItemResolver` auto-fetches from ADO and caches the result. Commands never see "not found in cache" — they either get the item or a clean "no active context" error.

#### ProtectedCacheWriter

Wraps `IWorkItemRepository.SaveAsync` / `SaveBatchAsync` with SyncGuard protection:
```csharp
// Saves only items that have no pending changes; returns skipped IDs
Task<IReadOnlyList<int>> SaveBatchProtectedAsync(IEnumerable<WorkItem> items, CancellationToken ct);
```

Any item in `SyncGuard.GetProtectedItemIdsAsync()` is skipped. Callers can inspect skipped IDs to inform the user.

#### SyncCoordinator

Orchestrates the background fetch-and-revise cycle for read commands:
```csharp
Task<SyncResult> SyncActiveItemAsync(int itemId, CancellationToken ct);
Task<SyncResult> SyncChildrenAsync(int parentId, CancellationToken ct);
```

Returns `SyncResult`: `UpToDate | Updated(changedCount) | Failed(reason) | Skipped(reason)`. Commands don't implement fetch logic — they call `SyncCoordinator` and it handles staleness checks, protected saves, and cache updates.

### 3.4 Rendering: In-Place Sync Status

New `IAsyncRenderer` method:
```csharp
Task RenderWithSyncAsync<T>(
    Func<Task<T>> getCachedData,         // immediate: render from cache
    Func<T, Task<SyncResult>> syncData,  // background: fetch + update
    Action<T, IRenderable> buildDisplay, // how to render the data
    CancellationToken ct);
```

**UX contract for non-process commands:**
1. Render cached data immediately (panel, tree, status view)
2. Below the rendered output, show `⟳ syncing...` (single line, in-place)
3. On completion, replace with `✓ up to date` or `✓ 2 items updated` — then fade after 1s
4. If sync fails: `⚠ sync failed (offline)` — data shown is still the cached version

**UX contract for process commands** (`init`, `flow-*`):
- Keep existing step-by-step progress (these are multi-step orchestrations where each step has user-visible side effects)
- Adopt `ProtectedCacheWriter` and `ActiveItemResolver` but not the in-place sync pattern

### 3.5 DI Modularization

Current: 58 registrations in one monolithic block. `FlowStartCommand` has 13 constructor parameters.

Target: Vertical command modules that register services in groups:

```csharp
// Program.cs becomes:
services.AddTwigCoreServices();          // config, paths, SQLite, repos, stores
services.AddTwigNetworkServices(config); // auth, HTTP, ADO client, iteration
services.AddTwigRenderingServices();     // formatters, Spectre, pipeline factory, theme
services.AddTwigCommandServices();       // shared: ActiveItemResolver, ProtectedCacheWriter, SyncCoordinator, HintEngine
services.AddTwigCommands();              // all command classes (simple registrations)
```

The key improvement: shared services (`ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator`) absorb cross-cutting concerns that currently balloon constructor parameter lists. Commands take the shared services instead of raw repositories + ADO clients.

**Before** (StateCommand — 9 params):
```csharp
StateCommand(IContextStore, IWorkItemRepository, IAdoWorkItemService,
    IPendingChangeStore, IProcessConfigurationProvider, IConsoleInput,
    OutputFormatterFactory, HintEngine, IPromptStateWriter?)
```

**After** (StateCommand — 5 params):
```csharp
StateCommand(ActiveItemResolver, SyncCoordinator,
    IProcessConfigurationProvider, IConsoleInput,
    OutputFormatterFactory)
```

`SyncCoordinator` encapsulates `IWorkItemRepository`, `IAdoWorkItemService`, `IPendingChangeStore`, `IPromptStateWriter`, and `ProtectedCacheWriter` — those details are no longer surfaced to every command.

## 4. Requirements

**Summary**: The plan introduces three shared services, a new rendering primitive, modularized DI, and applies these across all in-scope commands. It must maintain backward compatibility for JSON/minimal output modes and non-TTY environments.

- **FR-001**: All read commands (`set`, `status`, `tree`, `workspace`, `up`, `down`) MUST render from cache before any network call
- **FR-002**: Background fetches MUST NOT overwrite items with pending local changes
- **FR-003**: Commands MUST auto-fetch from ADO when the active item is not in cache, instead of returning "not found"
- **FR-004**: Non-process commands MUST show a single in-place sync status indicator, not sequential log lines
- **FR-005**: Write commands (`state`, `update`, `save`) MUST still fetch latest revision for conflict detection before patching — but SHOULD display cached state immediately while fetching
- **FR-006**: `init` and `flow-*` commands retain step-by-step progress UX
- **FR-007**: JSON and minimal output modes MUST produce identical output to today (no sync indicators, no progressive rendering)
- **FR-008**: Non-TTY (piped/redirected) output MUST work without Live() rendering
- **NFR-001**: Read commands MUST display cached output within 100ms of invocation
- **NFR-002**: DI registration in `Program.cs` MUST be organized into named module methods, each responsible for a logical service group
- **CON-001**: All changes MUST be AOT-compatible (`PublishAot=true`, `PublishTrimmed=true`)
- **CON-002**: Spectre.Console `Live()` is the only AOT-safe progressive rendering API (no `SelectionPrompt<T>` — see ITEM-001A spike)
- **GUD-001**: Commands SHOULD accept shared services (`ActiveItemResolver`, `SyncCoordinator`) rather than raw infrastructure interfaces where possible
- **GUD-002**: Prefer composition over inheritance — shared services are injected, not base-classed

## 5. Risk Classification

**Risk**: 🟡 MEDIUM

**Summary**: This is a wide-reaching architectural change touching 15+ commands, the DI container, the rendering pipeline, and the persistence layer. The core risk is regressions in existing command behavior. Mitigated by the existing 1,034-test suite and incremental EPIC delivery.

- **RISK-001**: `ProtectedCacheWriter` changes the contract of `SaveBatchAsync` — existing callers (RefreshCommand) that call SaveBatchAsync directly must be updated to use the protected variant or explicitly opt out
- **RISK-002**: `SyncCoordinator` introduces concurrency between display rendering and background fetch — must ensure thread safety with SQLite (single writer, connection-per-thread)
- **RISK-003**: `ActiveItemResolver` auto-fetching on cache miss could mask cache corruption — should log/warn when this path is taken
- **RISK-004**: Spectre.Console `Live()` context cannot nest — commands that already use `Live()` (tree, workspace, disambiguation) need careful integration with the sync indicator
- **ASSUMPTION-001**: SQLite WAL mode allows concurrent reads while a write transaction is in progress (already the case for twig's SQLite configuration)
- **ASSUMPTION-002**: `CacheStaleMinutes` (default: 5) is an appropriate threshold for background sync decisions

## 6. Dependencies

**Summary**: No new external dependencies. All changes are within existing Twig source and use existing Spectre.Console and SQLite capabilities.

- **DEP-001**: Spectre.Console `Live()` API for in-place sync status rendering (already used)
- **DEP-002**: SQLite WAL mode for read-during-write concurrency (already configured)
- **DEP-003**: `SyncGuard` (already implemented in `Twig.Domain.Services`) for dirty item detection

## 7. Quality & Testing

**Summary**: Each EPIC includes test requirements. The shared services (`ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator`) get dedicated unit test classes. Existing command tests are updated to use the new services. The full 1,034-test suite must pass after each EPIC.

- **TEST-001**: `ActiveItemResolver` — cache hit, cache miss → auto-fetch, no context, fetch failure → offline error
- **TEST-002**: `ProtectedCacheWriter` — saves unprotected items, skips dirty items, returns skipped IDs, handles empty batch
- **TEST-003**: `SyncCoordinator` — stale check triggers sync, fresh data skips sync, sync failure returns graceful result, protected save applied
- **TEST-004**: Per-command integration tests: each updated command tested with (a) cached data, (b) stale data + successful sync, (c) stale data + failed sync, (d) dirty items during sync
- **TEST-005**: Rendering tests: `RenderWithSyncAsync` shows cached data + sync indicator, revises on update, handles sync failure

### Acceptance Criteria

| ID | Criterion | Verification | Traces To |
|----|-----------|--------------|-----------|
| AC-001 | `twig status` displays output within 100ms from cache (no network call) | Automated test: mock repo returns item; assert no ADO service calls; measure elapsed | NFR-001, FR-001 |
| AC-002 | `twig set <cached-id>` displays item immediately; children fetched only if stale | Automated test: mock cached item + fresh `last_synced_at`; assert `FetchChildrenAsync` NOT called | FR-001, FR-002 |
| AC-003 | `twig set <cached-id>` with stale children fetches in background and does NOT overwrite dirty children | Automated test: child in `pending_changes`; assert child row unchanged after sync | FR-002 |
| AC-004 | `twig status` auto-fetches when active item not in cache | Automated test: `GetByIdAsync` returns null; assert `FetchAsync` called; item displayed | FR-003 |
| AC-005 | JSON output mode produces identical output pre/post refactor | Automated test: capture JSON output for each command; assert exact match | FR-007 |
| AC-006 | Non-TTY output works without Live() rendering | Automated test: `isOutputRedirected = true`; assert no `IAsyncRenderer` calls | FR-008 |
| AC-007 | All 1,034+ existing tests pass after each EPIC | CI gate | All |

## 8. Security Considerations

No security considerations identified. This initiative changes display timing and cache access patterns but does not alter authentication, authorization, data handling, or input validation. All network calls use existing authenticated ADO client paths.

## 9. Deployment & Rollback

This is a CLI tool distributed as a self-contained AOT binary. Each EPIC is merged to the `feature/cache-first` branch and tested. The full initiative ships as v0.4.0 after all EPICs are complete. Rollback is `twig upgrade` to v0.3.0 (previous release).

## 10. Resolved Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| RD-001 | Background sync uses `SyncCoordinator` service, not per-command inline fetch logic | Centralizes staleness checks, protected saves, and cache updates. Eliminates 15+ copies of fetch-save patterns |
| RD-002 | `ProtectedCacheWriter` is a separate service wrapping `IWorkItemRepository`, not a change to the repository interface | Keeps existing direct-save paths available for `RefreshCommand --force` and other explicit overwrite scenarios |
| RD-003 | Process commands (`init`, `flow-*`) keep step-by-step UX | These commands have sequential side effects (state transitions, branch creation) where each step's success/failure informs the next. In-place revision would hide important workflow context |
| RD-004 | DI modules are static extension methods on `IServiceCollection`, not separate assemblies | Keeps deployment as a single AOT binary. Modules are organizational, not physical |
| RD-005 | `ActiveItemResolver` auto-fetches on cache miss and caches the result | "Not found in cache" is confusing for users who just ran `twig set <id>` in another terminal. Auto-fetch is the expected UX. Failure is reported as "unreachable / not found in ADO" |
| RD-006 | Sync indicator renders below the data panel, not inline | Inline would require full panel re-render. Below-panel is a single `Live()` context update that doesn't disturb the rendered data |
| RD-007 | `SyncCoordinator` reports `SyncResult` (discriminated union), commands decide how to display it | Keeps rendering logic in the command layer, sync logic in the service layer. Clean separation |

## 11. Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Modify `IWorkItemRepository.SaveBatchAsync` directly to be protected | Single save path, no wrapper service | Breaks callers that explicitly want to overwrite (RefreshCommand --force); changes shared interface contract | Rejected — wrapper service is safer |
| Use `IAsyncEnumerable` streaming for all commands (like WorkspaceCommand) | Proven pattern already in codebase | Overly complex for single-item commands (set, status); forces async iterator syntax on simple reads | Rejected — `RenderWithSyncAsync` is simpler for single-item cases |
| Abstract base class `CacheFirstCommand` | Centralizes lifecycle in one place | Inheritance hierarchy limits flexibility; AOT source generators don't work well with generic base classes in ConsoleAppFramework | Rejected — composition via injected services is preferred |
| Lazy DI resolution (resolve services only when command runs) | Reduces startup cost | Already fast (<50ms); adds complexity; ConsoleAppFramework resolves before command dispatch anyway | Rejected — modular registration is sufficient |

## 12. Files

### New Files

- **FILE-001**: `src/Twig.Domain/Services/ActiveItemResolver.cs` — resolves active item from cache or ADO
- **FILE-002**: `src/Twig.Domain/Services/ProtectedCacheWriter.cs` — SyncGuard-protected save operations
- **FILE-003**: `src/Twig.Domain/Services/SyncCoordinator.cs` — background fetch orchestration with staleness checks
- **FILE-004**: `src/Twig.Domain/Services/SyncResult.cs` — discriminated union for sync outcomes
- **FILE-005**: `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` — ADO client, auth, HTTP, iteration registration
- **FILE-006**: `src/Twig.Infrastructure/DependencyInjection/RenderingServiceModule.cs` — formatters, Spectre, pipeline factory registration
- **FILE-007**: `src/Twig/DependencyInjection/CommandServiceModule.cs` — shared command services (resolver, writer, coordinator, hint engine)
- **FILE-008**: `src/Twig/DependencyInjection/CommandRegistrationModule.cs` — all command class registrations
- **FILE-009**: `tests/Twig.Domain.Tests/Services/ActiveItemResolverTests.cs`
- **FILE-010**: `tests/Twig.Domain.Tests/Services/ProtectedCacheWriterTests.cs`
- **FILE-011**: `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs`

### Modified Files (significant changes)

- **FILE-012**: `src/Twig/Program.cs` — replace monolithic DI with module calls; slim down from ~400 lines of DI to ~20
- **FILE-013**: `src/Twig.Infrastructure/TwigServiceRegistration.cs` — becomes the `CoreServiceModule`; extracted network/rendering to new modules
- **FILE-014**: `src/Twig/Rendering/IAsyncRenderer.cs` — add `RenderWithSyncAsync` method
- **FILE-015**: `src/Twig/Rendering/SpectreRenderer.cs` — implement `RenderWithSyncAsync`
- **FILE-016**: `src/Twig/Commands/SetCommand.cs` — adopt `ActiveItemResolver`, `SyncCoordinator`, lazy child fetch
- **FILE-017**: `src/Twig/Commands/StatusCommand.cs` — adopt `ActiveItemResolver`, `SyncCoordinator`
- **FILE-018**: `src/Twig/Commands/TreeCommand.cs` — adopt `ActiveItemResolver`
- **FILE-019**: `src/Twig/Commands/NavigationCommands.cs` — adopt `ActiveItemResolver`
- **FILE-020**: `src/Twig/Commands/WorkspaceCommand.cs` — adopt `SyncCoordinator` (replace inline stale-while-revalidate)
- **FILE-021**: `src/Twig/Commands/StateCommand.cs` — adopt `ActiveItemResolver`, `SyncCoordinator`, reduce constructor params
- **FILE-022**: `src/Twig/Commands/NoteCommand.cs` — adopt `ActiveItemResolver`
- **FILE-023**: `src/Twig/Commands/UpdateCommand.cs` — adopt `ActiveItemResolver`, `SyncCoordinator`
- **FILE-024**: `src/Twig/Commands/EditCommand.cs` — adopt `ActiveItemResolver`
- **FILE-025**: `src/Twig/Commands/SaveCommand.cs` — adopt `ActiveItemResolver`, `ProtectedCacheWriter`
- **FILE-026**: `src/Twig/Commands/FlowStartCommand.cs` — adopt `ActiveItemResolver`, `ProtectedCacheWriter`
- **FILE-027**: `src/Twig/Commands/FlowDoneCommand.cs` — adopt `ActiveItemResolver`, `ProtectedCacheWriter`
- **FILE-028**: `src/Twig/Commands/FlowCloseCommand.cs` — adopt `ActiveItemResolver`, `ProtectedCacheWriter`
- **FILE-029**: `src/Twig/Commands/RefreshCommand.cs` — adopt `ProtectedCacheWriter` (replaces inline SyncGuard logic)

## 13. Implementation Plan

### EPIC-001: Foundation — Shared Services and Protected Saves

Introduce the three core shared services and their tests. No command changes yet — this is pure additive infrastructure.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-001 | Create `SyncResult` discriminated union: `UpToDate`, `Updated(int changedCount)`, `Failed(string reason)`, `Skipped(string reason)` | Not Started | FILE-004 |
| ITEM-002 | Create `ActiveItemResolver` service. Constructor takes `IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService`. Method `GetActiveItemAsync(CancellationToken)` returns `ActiveItemResult` (discriminated union: `Found(WorkItem)`, `NoContext`, `FetchedFromAdo(WorkItem)`, `Unreachable(int id, string reason)`). On cache miss: call `adoService.FetchAsync(id)` → `workItemRepo.SaveAsync(item)` → return `FetchedFromAdo`. On fetch failure: return `Unreachable`. Log to stderr when auto-fetch path is taken. | Not Started | FILE-001 |
| ITEM-003 | Create `ProtectedCacheWriter` service. Constructor takes `IWorkItemRepository`, `IPendingChangeStore`. Method `SaveBatchProtectedAsync(IEnumerable<WorkItem> items, CancellationToken)` calls `SyncGuard.GetProtectedItemIdsAsync()`, filters out protected IDs, calls `workItemRepo.SaveBatchAsync()` on remaining items, returns `IReadOnlyList<int>` of skipped IDs. Method `SaveProtectedAsync(WorkItem item, CancellationToken)` checks single item against protected set, saves or skips. | Not Started | FILE-002 |
| ITEM-004 | Create `SyncCoordinator` service. Constructor takes `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `ProtectedCacheWriter`, `IPromptStateWriter?`. Method `SyncItemAsync(int id, CancellationToken)`: check `last_synced_at` on cached item — if fresh (< `CacheStaleMinutes`), return `UpToDate`; if stale, `FetchAsync(id)` → `ProtectedCacheWriter.SaveProtectedAsync()` → return `Updated(1)` or `Skipped` if protected. Method `SyncChildrenAsync(int parentId, CancellationToken)`: `FetchChildrenAsync(parentId)` → `ProtectedCacheWriter.SaveBatchProtectedAsync()` → return result with changed/skipped counts. Read `CacheStaleMinutes` from `IContextStore` value `cache_stale_minutes` (set during init). | Not Started | FILE-003 |
| ITEM-005 | Register new services in `TwigServiceRegistration.AddTwigCoreServices()`: `ActiveItemResolver` (singleton), `ProtectedCacheWriter` (singleton), `SyncCoordinator` (singleton). `SyncCoordinator` needs `TwigConfiguration` for stale minutes — inject via factory. | Not Started | FILE-013 |
| ITEM-006 | Write unit tests for `ActiveItemResolver`: cache hit returns `Found`, cache miss → fetch → returns `FetchedFromAdo`, cache miss → fetch fails → returns `Unreachable`, no active id → returns `NoContext`. | Not Started | FILE-009 |
| ITEM-007 | Write unit tests for `ProtectedCacheWriter`: saves unprotected items, skips protected items, returns correct skipped IDs, handles empty input, handles all-protected input. | Not Started | FILE-010 |
| ITEM-008 | Write unit tests for `SyncCoordinator`: fresh item → `UpToDate`, stale item → fetches and saves → `Updated`, stale protected item → `Skipped`, fetch failure → `Failed`, children sync with mixed protected/unprotected. | Not Started | FILE-011 |

### EPIC-002: DI Modularization

Extract the monolithic DI setup in `Program.cs` into focused module methods. No behavioral changes — pure refactor with test parity.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-009 | Create `NetworkServiceModule.cs` as a static class with extension method `AddTwigNetworkServices(this IServiceCollection, TwigConfiguration)`. Move from `Program.cs`: `IAuthenticationProvider`, `HttpClient`, `IAdoWorkItemService`, `IAdoGitService` (conditional), `IIterationService` registrations. The `TwigConfiguration` parameter supplies org/project/team/git config needed by factories. | Not Started | FILE-005, FILE-012 |
| ITEM-010 | Create `RenderingServiceModule.cs` with `AddTwigRenderingServices(this IServiceCollection)`. Move from `Program.cs`: `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`, `OutputFormatterFactory`, `IAnsiConsole`, `SpectreTheme`, `IAsyncRenderer`, `RenderingPipelineFactory`. | Not Started | FILE-006, FILE-012 |
| ITEM-011 | Create `CommandServiceModule.cs` with `AddTwigCommandServices(this IServiceCollection)`. Move from `Program.cs`: `HintEngine`, `IEditorLauncher`, `IConsoleInput`. Register new shared services: `ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator` (these were added in EPIC-001 to core services; if they need CLI-layer dependencies, register factories here instead). | Not Started | FILE-007, FILE-012 |
| ITEM-012 | Create `CommandRegistrationModule.cs` with `AddTwigCommands(this IServiceCollection)`. Move ALL command class registrations from `Program.cs`. Each command is `AddSingleton<T>()` or `AddSingleton<T>(factory)`. Preserve existing factory lambdas. | Not Started | FILE-008, FILE-012 |
| ITEM-013 | Update `Program.cs` to call module methods. The `ConfigureServices` block becomes: `services.AddTwigCoreServices()`, `services.AddTwigNetworkServices(config)`, `services.AddTwigRenderingServices()`, `services.AddTwigCommandServices()`, `services.AddTwigCommands()`. Legacy migration and git remote detection stay in `Program.cs` (they depend on startup-time values). Total DI section shrinks from ~350 lines to ~30 lines. | Not Started | FILE-012 |
| ITEM-014 | Verify all 1,034+ tests pass. No behavioral change — this is a pure structural refactor. | Not Started | All test projects |

### EPIC-003: Rendering — In-Place Sync Status

Add the rendering primitive that commands will use for cache-render-fetch-revise.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-015 | Add `RenderWithSyncAsync` to `IAsyncRenderer` interface. Signature: `Task RenderWithSyncAsync(Func<Task<IRenderable>> buildCachedView, Func<Task<SyncResult>> performSync, Func<SyncResult, Task<IRenderable?>> buildRevisedView, CancellationToken ct)`. `buildCachedView` returns the initial Spectre renderable (rendered immediately). `performSync` runs the background sync. `buildRevisedView` gets the sync result and returns a revised renderable (or null if no visual change needed). | Not Started | FILE-014 |
| ITEM-016 | Implement `RenderWithSyncAsync` in `SpectreRenderer`. Pattern: (1) call `buildCachedView()`, render it as the Live target. (2) Below data, show `[dim]⟳ syncing...[/]` status line. (3) Call `performSync()`. (4) On `UpToDate`: replace status with `[dim]✓ up to date[/]`, wait 800ms, clear. (5) On `Updated(n)`: call `buildRevisedView()`, replace Live target with revised renderable, show `[green]✓ n items updated[/]`, wait 800ms, clear. (6) On `Failed`: show `[yellow]⚠ sync failed (offline)[/]`, persist (don't clear). (7) On `Skipped`: show `[dim]✓ up to date[/]` (user doesn't need to know about skip internals). | Not Started | FILE-015 |
| ITEM-017 | Add sync status rendering tests. Use `TestConsole` to verify: (a) cached view rendered before sync starts, (b) `UpToDate` shows and clears, (c) `Updated` replaces content, (d) `Failed` shows warning and persists. | Not Started | `tests/Twig.Cli.Tests/Rendering/` |

### EPIC-004: Adopt Shared Services in Read Commands

Update `set`, `status`, `tree`, `up`, `down`, `workspace` to use `ActiveItemResolver` and `SyncCoordinator`. This is where the UX improvement lands.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-018 | **SetCommand** — Replace inline active-item resolution and fetch logic with `ActiveItemResolver`. Replace `FetchChildrenAsync` + `SaveBatchAsync` with `SyncCoordinator.SyncChildrenAsync` (background, staleness-gated, protected). For TTY human output: use `RenderWithSyncAsync` to display item immediately, sync children in background, revise tree if new children found. For JSON/minimal/piped: display cached item, sync synchronously if stale (no Live rendering), report sync result to stderr. Remove `IAdoWorkItemService` from constructor — it's now inside `SyncCoordinator`. | Not Started | FILE-016 |
| ITEM-019 | **StatusCommand** — Replace inline `GetByIdAsync` + null check with `ActiveItemResolver.GetActiveItemAsync()`. Handle `FetchedFromAdo` result (show item normally — user shouldn't notice auto-fetch). Handle `Unreachable` result (show error). The async rendering path already works well — add sync indicator via `RenderWithSyncAsync` wrapper around existing `RenderStatusAsync`. | Not Started | FILE-017 |
| ITEM-020 | **TreeCommand** — Replace inline active-item resolution with `ActiveItemResolver`. Tree is read-only from cache — no sync needed (too complex to revise a tree in-place). Add a hint if cache is stale instead. | Not Started | FILE-018 |
| ITEM-021 | **NavigationCommands (Up/Down)** — Replace inline active-item resolution with `ActiveItemResolver`. For `DownAsync` no-arg path: children already loaded from cache. For `DownAsync` with pattern: same. Navigation delegates to `SetCommand` for the actual context switch, which now handles sync. | Not Started | FILE-019 |
| ITEM-022 | **WorkspaceCommand** — Replace inline stale-while-revalidate logic with `SyncCoordinator`. The existing `StreamWorkspaceData` async enumeration pattern is already close to the target UX — simplify it to delegate staleness/fetch/save to `SyncCoordinator` instead of implementing those inline. | Not Started | FILE-020 |
| ITEM-023 | Update all existing tests for SetCommand, StatusCommand, TreeCommand, NavigationCommands, WorkspaceCommand to use the new service mocks. Verify JSON/minimal output parity. Verify TTY rendering path. Verify auto-fetch on cache miss. Verify dirty items are not overwritten during sync. | Not Started | `tests/Twig.Cli.Tests/Commands/` |

### EPIC-005: Adopt Shared Services in Write Commands

Update `state`, `note`, `update`, `edit`, `save` to use `ActiveItemResolver` and `SyncCoordinator`. Write commands still need pre-patch conflict detection, but they can display cached state first.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-024 | **StateCommand** — Replace inline active-item resolution with `ActiveItemResolver`. The fetch-for-conflict pattern stays (required for revision-based conflict detection), but display the cached item's current state immediately while the conflict-check fetch runs. Use `SyncCoordinator.SyncItemAsync` for the pre-patch fetch instead of raw `adoService.FetchAsync`. Remove `IWorkItemRepository`, `IAdoWorkItemService`, `IPendingChangeStore` from constructor — encapsulated by `ActiveItemResolver` and `SyncCoordinator`. | Not Started | FILE-021 |
| ITEM-025 | **NoteCommand** — Replace inline active-item resolution with `ActiveItemResolver`. No sync needed (note is local-only until save). | Not Started | FILE-022 |
| ITEM-026 | **UpdateCommand** — Replace inline active-item resolution with `ActiveItemResolver`. Replace fetch-patch-fetch cycle with `SyncCoordinator` for the pre-patch fetch. Eliminate the redundant post-patch `FetchAsync` — apply the state change locally using the returned revision (same pattern as StateCommand). | Not Started | FILE-023 |
| ITEM-027 | **EditCommand** — Replace inline active-item resolution with `ActiveItemResolver`. No sync needed (edit is local-only until save). | Not Started | FILE-024 |
| ITEM-028 | **SaveCommand** — Replace inline active-item resolution with `ActiveItemResolver`. Replace `SaveBatchAsync` calls with `ProtectedCacheWriter` where applicable. The per-item conflict resolution flow stays. | Not Started | FILE-025 |
| ITEM-029 | **RefreshCommand** — Adopt `ProtectedCacheWriter` to replace inline SyncGuard logic. The `--force` flag should bypass protection (call raw `SaveBatchAsync` directly). | Not Started | FILE-029 |
| ITEM-030 | Update all tests for write commands. Verify conflict detection still works. Verify pending changes are preserved during sync. Verify reduced constructor parameters. | Not Started | `tests/Twig.Cli.Tests/Commands/` |

### EPIC-006: Adopt Shared Services in Flow Commands

Update `flow-start`, `flow-done`, `flow-close` to use `ActiveItemResolver` and `ProtectedCacheWriter`. These keep step-by-step UX.

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-031 | **FlowStartCommand** — Replace inline active-item resolution with `ActiveItemResolver`. Replace `SaveAsync` calls with `ProtectedCacheWriter.SaveProtectedAsync`. Keep step-by-step progress UX. Target: reduce constructor from 13 params to ~8. | Not Started | FILE-026 |
| ITEM-032 | **FlowDoneCommand** — Same pattern as ITEM-031. Delegates to SaveCommand (already updated in EPIC-005). | Not Started | FILE-027 |
| ITEM-033 | **FlowCloseCommand** — Same pattern. The explicit dirty-item guard (`GetDirtyItemIdsAsync` check) stays — it's a correctness check, not a sync concern. | Not Started | FILE-028 |
| ITEM-034 | Update flow command tests. Verify step-by-step UX preserved. Verify reduced constructor params. Verify protected saves. | Not Started | `tests/Twig.Cli.Tests/Commands/` |
