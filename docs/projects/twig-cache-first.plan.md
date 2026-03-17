# Cache-First Architecture — Solution Design & Implementation Plan

> **Status**: Draft  
> **Revision**: 6.0  
> **Date**: 2026-03-17
> **Owner**: Daniel Green  
> **PRD**: [docs/projects/twig-cache-first.prd.md](./twig-cache-first.prd.md)

---

## Executive Summary

The Twig CLI will deliver **sub-100ms perceived latency for all read commands** by rendering from the SQLite cache immediately, then syncing in the background and revising the display in-place. Three shared domain services — `ActiveItemResolver`, `ProtectedCacheWriter`, and `SyncCoordinator` — consolidate the active-item resolution, dirty-guard, and staleness-check patterns currently duplicated across 15+ commands. This also fixes a dirty-value overwrite bug in `SetCommand` where `SaveBatchAsync` ignores `SyncGuard` protection. DI registration is modularized from the ~330-line inline block in `Program.cs` into 5 focused extension methods. The primary benefit of shared services is **pattern consolidation** — replacing repeated boilerplate with single-call services — rather than dramatic constructor parameter reduction (most commands retain `IWorkItemRepository` and `IAdoWorkItemService` for write operations).

---

## Background

### Current Architecture

Twig is a .NET 9 AOT-compiled CLI (`ConsoleAppFramework`) that manages Azure DevOps work items via a local SQLite cache. The project follows clean architecture: `Twig.Domain` (aggregates, interfaces, services), `Twig.Infrastructure` (SQLite persistence, ADO REST client, config), and `Twig` (CLI commands, rendering, DI).

**Data flow today** (e.g., `twig set 42`):
1. `SetCommand.ExecuteAsync` → `workItemRepo.GetByIdAsync(42)` (cache lookup)
2. If miss → `adoService.FetchAsync(42)` (blocking network call) → `workItemRepo.SaveAsync(item)`
3. Always → `adoService.FetchChildrenAsync(42)` (blocking) → `workItemRepo.SaveBatchAsync(children)` (**no dirty guard**)
4. Set context → Display output

**Key problems identified in the codebase:**
1. **Blocking fetches**: `SetCommand` always calls `FetchChildrenAsync` before display (line 104). `StatusCommand` returns "not found in cache" if cache miss (line 56). No command renders from cache first.
2. **Dirty overwrite bug**: `SetCommand` line 106 calls `SaveBatchAsync(children)` without checking `SyncGuard.GetProtectedItemIdsAsync()`. A child with pending local changes (dirty flag or pending_changes rows) will be silently overwritten.
3. **No auto-fetch on cache miss**: `StatusCommand`, `TreeCommand`, `NavigationCommands` all fail with "not found in cache" rather than auto-fetching from ADO.
4. **Monolithic DI**: `Program.cs` has ~260 lines of inline DI registration (beyond the already-extracted `AddTwigCoreServices()`). `FlowStartCommand` has 13 constructor parameters. The same `IContextStore` + `IWorkItemRepository` + null-check pattern is repeated in every command.
5. **No in-place sync status**: `WorkspaceCommand` has a rudimentary stale-while-revalidate pattern (lines 67–112) but other commands have nothing.

### Prior Art in Codebase

- **`SyncGuard`** (`Twig.Domain.Services.SyncGuard`): Already computes the union of dirty + pending item IDs. Used by `RefreshCommand` but not by `SetCommand` or any sync path.
- **`WorkspaceCommand.IsCacheStale()`**: A static method that compares `last_refreshed_at` timestamp against `CacheStaleMinutes`. Used by `WorkspaceCommand` and `StatusCommand` for hint display, but not for triggering background sync.
- **`RenderingPipelineFactory`**: Already splits sync (formatter-only) and async (SpectreRenderer Live()) rendering paths based on output format, TTY detection, and `--no-live`.
- **`TwigServiceRegistration.AddTwigCoreServices()`**: An existing modularization pattern in Infrastructure for core services (config, paths, SQLite repos). The proposed DI modules follow this same pattern.
- **`SpectreRenderer`**: Already uses `Live()` contexts for workspace streaming, tree building, and work item display. The proposed `RenderWithSyncAsync` follows the same `Live()` → `UpdateTarget()` → `Refresh()` pattern.

---

## Problem Statement

1. **Perceived latency**: All read commands (`set`, `status`, `tree`, `up`, `down`, `workspace`) block on network before display. Users wait 1–5 seconds seeing nothing.
2. **Data loss**: `SetCommand.FetchChildrenAsync` + `SaveBatchAsync` overwrites locally dirty children without checking `SyncGuard`. Users who run `twig note` on a child item, then `twig set <parent>`, lose their pending note.
3. **Confusing cache miss errors**: "Work item #X not found in cache" when the item was evicted or set in another terminal. The expected UX is auto-fetch, not manual recovery.
4. **DI complexity**: ~330 lines of inline registrations in `Program.cs` (lines 33–382) beyond `AddTwigCoreServices()`. Commands accept 7–13 raw infrastructure interfaces. Adding a new cross-cutting concern (like staleness checks) requires touching every command constructor.
5. **Duplicated patterns**: Active-item resolution (`GetActiveWorkItemIdAsync` → null check → `GetByIdAsync` → null check) appears in 15+ commands with slight variations.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|----|------|---------|
| G-1 | Sub-100ms perceived latency for all read commands | Automated test: mock repo + no ADO calls; assert elapsed < 100ms |
| G-2 | Never silently overwrite dirty data during background fetches | Automated test: child in pending_changes; assert unchanged after sync |
| G-3 | Auto-fetch on cache miss instead of "not found" errors | Automated test: GetByIdAsync returns null; assert FetchAsync called |
| G-4 | Single in-place sync indicator for non-process commands | Visual test: `⟳ syncing...` → `✓ up to date` replaces in-place |
| G-5 | Reduce DI registration complexity and consolidate patterns | Program.cs DI section < 30 lines; shared services replace inline resolution/save boilerplate |
| G-6 | Shared architectural patterns eliminating per-command duplication | ActiveItemResolver replaces 15+ inline resolution patterns |

### Non-Goals

- **`seed` command**: Creation workflow is orthogonal to cache-first reads.
- **`flow-*` commands in-place sync**: These are multi-step processes with sequential side effects. They adopt `ActiveItemResolver` and `ProtectedCacheWriter` but keep step-by-step UX.
- **Git commands** (`branch`, `commit`, `pr`, `stash`, `log`, `context`, `hooks`): Already cache-optimal or inherently require git operations.
- **System commands** (`config`, `version`, `upgrade`, `changelog`, `ohmyposh`, `tui`): No fetch behavior to change.
- **Offline mode redesign**: Out of scope. `SyncCoordinator` returns `Failed(reason)` on network errors; commands display cached data with a warning.

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-001 | All read commands MUST render from cache before any network call | MUST |
| FR-002 | Background fetches MUST NOT overwrite items with pending local changes | MUST |
| FR-003 | Commands MUST auto-fetch from ADO when active item is not in cache | MUST |
| FR-004 | Non-process commands MUST show a single in-place sync status indicator | MUST |
| FR-005 | Write commands SHOULD display cached state immediately while fetching latest revision | SHOULD |
| FR-006 | `init` and `flow-*` commands retain step-by-step progress UX | MUST |
| FR-007 | JSON and minimal output modes MUST produce identical output to today | MUST |
| FR-008 | Non-TTY (piped/redirected) output MUST work without Live() rendering | MUST |

### Non-Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| NFR-001 | Read commands MUST display cached output within 100ms of invocation | MUST |
| NFR-002 | DI registration in Program.cs MUST be organized into named module methods | MUST |

### Constraints

| ID | Constraint |
|----|------------|
| CON-001 | All changes MUST be AOT-compatible (`PublishAot=true`, `PublishTrimmed=true`) |
| CON-002 | Spectre.Console `Live()` is the only AOT-safe progressive rendering API (no `SelectionPrompt<T>`) |
| CON-003 | SQLite WAL mode for concurrent read/write (already configured) |

---

## Proposed Design

### Architecture Overview

```
┌───────────────┐     ┌──────────────────────┐     ┌──────────────────┐
│   Command      │────▶│  ActiveItemResolver   │────▶│  Render (cached)  │
│   (e.g. set)   │     │  cache hit | auto-fetch│     │  immediately      │
└───────────────┘     └──────────────────────┘     └────────┬─────────┘
                                                            │
                                                  ┌─────────▼──────────┐
                                                  │  SyncCoordinator    │
                                                  │  (if stale →       │
                                                  │   background fetch) │
                                                  └─────────┬──────────┘
                                                            │
                                                  ┌─────────▼──────────┐
                                                  │  ProtectedCacheWriter│
                                                  │  (skip dirty items) │
                                                  └─────────┬──────────┘
                                                            │
                                                  ┌─────────▼──────────┐
                                                  │  RenderWithSyncAsync │
                                                  │  (revise in-place   │
                                                  │   or "up to date")  │
                                                  └────────────────────┘
```

### Key Components

#### 1. ActiveItemResolver (`Twig.Domain.Services`)

**Responsibility**: Replaces the repeated 4-line active-item resolution pattern across 15+ commands.

**Interface**:
```csharp
// Resolves the active-context item (from IContextStore → cache → auto-fetch)
// Returns: Found(WorkItem) | NoContext | FetchedFromAdo(WorkItem) | Unreachable(int id, string reason)
Task<ActiveItemResult> GetActiveItemAsync(CancellationToken ct);

// Resolves a specific item by ID (cache → auto-fetch)
// Returns: Found(WorkItem) | FetchedFromAdo(WorkItem) | Unreachable(int id, string reason)
Task<ActiveItemResult> ResolveByIdAsync(int id, CancellationToken ct);
```

**Dependencies**: `IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService`

**Behavior**:
- `GetActiveWorkItemIdAsync()` → null → return `NoContext`
- `GetByIdAsync(id)` → non-null → return `Found(item)`
- `GetByIdAsync(id)` → null → `FetchAsync(id)` → `SaveAsync(item)` → return `FetchedFromAdo(item)`
- Fetch throws → return `Unreachable(id, ex.Message)`
- Logs to stderr when auto-fetch path is taken (cache miss is not normal in steady state)

**Design rationale**: Composition over inheritance. Commands inject `ActiveItemResolver` rather than inheriting from a base class. This aligns with ConsoleAppFramework's pattern of concrete command classes and avoids AOT issues with generic base classes.

#### 2. ProtectedCacheWriter (`Twig.Domain.Services`)

**Responsibility**: Wraps `IWorkItemRepository.SaveAsync`/`SaveBatchAsync` with `SyncGuard` protection to prevent dirty-value overwrites.

**Interface**:
```csharp
Task<IReadOnlyList<int>> SaveBatchProtectedAsync(IEnumerable<WorkItem> items, CancellationToken ct);
Task<bool> SaveProtectedAsync(WorkItem item, CancellationToken ct); // true = saved, false = skipped
```

**Dependencies**: `IWorkItemRepository`, `IPendingChangeStore`

**Behavior**:
- Calls `SyncGuard.GetProtectedItemIdsAsync(repo, pendingStore)` once per batch
- Filters out any items whose ID is in the protected set
- Calls `SaveBatchAsync()` on remaining items
- Returns list of skipped IDs so callers can inform the user

**Design rationale**: Separate service rather than modifying `IWorkItemRepository` directly. `RefreshCommand --force` and explicit overwrite scenarios still call raw `SaveBatchAsync`.

#### 3. SyncCoordinator (`Twig.Domain.Services`)

**Responsibility**: Orchestrates background fetch-and-revise cycle for read commands. Commands never implement fetch logic directly.

**Interface**:
```csharp
Task<SyncResult> SyncItemAsync(int id, CancellationToken ct);
Task<SyncResult> SyncChildrenAsync(int parentId, CancellationToken ct);
```

**Dependencies**: `IWorkItemRepository`, `IAdoWorkItemService`, `ProtectedCacheWriter`, `int cacheStaleMinutes`

> **Layer constraint**: `SyncCoordinator` lives in `Twig.Domain.Services`. `TwigConfiguration` lives in `Twig.Infrastructure.Config`. `Twig.Domain.csproj` has **no** project reference to `Twig.Infrastructure`, and adding one would create a circular reference (Domain → Infrastructure → Domain). Therefore, `SyncCoordinator` accepts `int cacheStaleMinutes` as a constructor primitive, injected via a DI factory lambda in the CLI layer: `sp => new SyncCoordinator(..., sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes)`. See DD-13.

**Behavior**:
- `SyncItemAsync`: Reads the `LastSyncedAt` property from the cached `WorkItem` (populated from the SQLite `last_synced_at` column — see E1-T1a). Compares against `cacheStaleMinutes`. If fresh → `SyncResult.UpToDate`. If stale → `FetchAsync(id)` → `ProtectedCacheWriter.SaveProtectedAsync()` → `Updated(1)` or `Skipped`.
- `SyncChildrenAsync`: Unconditionally fetches children — `FetchChildrenAsync(parentId)` → `ProtectedCacheWriter.SaveBatchProtectedAsync()` → result with changed/skipped counts. No per-parent staleness check (see DD-15).
- On fetch failure → `SyncResult.Failed(reason)`. Commands display cached data with warning.

> **Note**: `SyncCoordinator` operates on individual items and children by ID. `WorkspaceCommand`'s iteration-scoped refresh is a fundamentally different data-access pattern: it re-resolves the current iteration via `IIterationService.GetCurrentIterationAsync()` (an ADO network call for iteration metadata, not work item data), then re-queries the local cache by iteration path. This remains inline in `WorkspaceCommand`. See DD-8, DD-9.

#### 4. SyncResult (`Twig.Domain.Services`)

**Discriminated union** for sync outcomes:
```csharp
public abstract record SyncResult
{
    public sealed record UpToDate : SyncResult;
    public sealed record Updated(int ChangedCount) : SyncResult;
    public sealed record Failed(string Reason) : SyncResult;
    public sealed record Skipped(string Reason) : SyncResult;
}
```

#### 5. RenderWithSyncAsync (`Twig.Rendering.IAsyncRenderer`)

**New method on the existing `IAsyncRenderer` interface**:
```csharp
Task RenderWithSyncAsync(
    Func<Task<IRenderable>> buildCachedView,
    Func<Task<SyncResult>> performSync,
    Func<SyncResult, Task<IRenderable?>> buildRevisedView,
    CancellationToken ct);
```

**UX contract** (TTY, human format):
1. Call `buildCachedView()` → render immediately as `Live()` target
2. Below data, show `[dim]⟳ syncing...[/]`
3. Call `performSync()`
4. `UpToDate` → replace status with `[dim]✓ up to date[/]`, wait 800ms, clear
5. `Updated(n)` → call `buildRevisedView()` → replace Live target, show `[green]✓ n items updated[/]`, wait 800ms, clear
6. `Failed` → show `[yellow]⚠ sync failed (offline)[/]`, persist
7. `Skipped` → show `[dim]✓ up to date[/]`

**Non-TTY/JSON/Minimal**: `RenderWithSyncAsync` is never called. Commands use `IOutputFormatter` path with synchronous sync (if stale). Sync result goes to stderr.

> **API note**: The `performSync` delegate is `Func<Task<SyncResult>>` without an explicit `CancellationToken` parameter. Callers capture `ct` via closure. This is an intentional simplification — adding a `CancellationToken` parameter would make the delegate signature `Func<CancellationToken, Task<SyncResult>>` which clutters the call sites without adding cancellation semantics beyond what closure capture provides.

### Data Flow: `twig set 42` (After)

```
1. ActiveItemResolver.ResolveByIdAsync(42)
   └─ workItemRepo.GetByIdAsync(42) → item (cache hit)
   └─ return Found(item)

2. RenderWithSyncAsync(
     buildCachedView: () => BuildWorkItemPanel(item),
     performSync: () => SyncCoordinator.SyncChildrenAsync(42),
     buildRevisedView: (result) => result is Updated ? RebuildPanel() : null)

3. SyncCoordinator.SyncChildrenAsync(42):
   └─ adoService.FetchChildrenAsync(42) → children
   └─ ProtectedCacheWriter.SaveBatchProtectedAsync(children)
      └─ SyncGuard.GetProtectedItemIdsAsync() → {45, 48} (dirty)
      └─ Save children except 45, 48
      └─ return Updated(5), skipped: [45, 48]

4. Display: panel rendered immediately at step 2;
   sync indicator shows "⟳ syncing..." then "✓ 5 items updated"
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Shared services via composition, not base class | ConsoleAppFramework resolves concrete classes; generic base classes cause AOT issues. Injected services are testable, composable, and don't impose inheritance constraints. |
| DD-2 | `ProtectedCacheWriter` is separate from `IWorkItemRepository` | `RefreshCommand --force` needs raw save. Changing the interface would break the explicit-overwrite escape hatch. |
| DD-3 | `SyncCoordinator` returns `SyncResult`, commands decide display | Clean separation: sync logic in domain services, rendering logic in CLI layer. |
| DD-4 | Process commands keep step-by-step UX | Flow commands have sequential side effects (state transitions, branch creation) where each step's success/failure informs the next. |
| DD-5 | DI modules are extension methods, not assemblies | Keeps single AOT binary. Modules are organizational, not physical. Follows existing `AddTwigCoreServices()` pattern. |
| DD-6 | Sync indicator renders below data panel | Inline would require full re-render. Below-panel is a single `Live()` `UpdateTarget` call. |
| DD-7 | `ActiveItemResolver` auto-fetches on cache miss | "Not found in cache" is confusing UX. Auto-fetch is expected behavior. Failure → "unreachable/not found in ADO". |
| DD-8 | `SyncCoordinator` uses per-item `last_synced_at`, not the global `last_refreshed_at` | Per-item staleness is more precise — a recently-synced item should not be re-fetched just because the global workspace hasn't refreshed. The `last_synced_at` column already exists in `work_items` (written at `SqliteWorkItemRepository.SaveWorkItem` line 210). Resolves OQ-1. |
| DD-9 | `WorkspaceCommand` keeps its inline stale-while-revalidate; not delegated to `SyncCoordinator` | `WorkspaceCommand` re-resolves the current iteration via `IIterationService.GetCurrentIterationAsync()` (iteration metadata, not work item data) — a fundamentally different data-access pattern from `SyncCoordinator`'s per-item fetch. Adding `SyncWorkspaceAsync` would introduce iteration semantics into `SyncCoordinator`, violating single responsibility. |
| DD-10 | `ActiveItemResolver.GetActiveItemAsync()` resolves by active context ID only; `SetCommand`'s pattern-match disambiguation remains inline | Pattern-match resolution (string → `FindByPatternAsync` → multiple matches → interactive picker or static list) is structurally different from active-context resolution (ID → cache lookup → auto-fetch). Merging these into one service would conflate two distinct UX flows. Resolves OQ-3. |
| DD-11 | `CacheStaleMinutes` sourced from `TwigConfiguration.Display.CacheStaleMinutes` via DI factory, not `IContextStore` | The existing codebase reads staleness threshold from `config.Display.CacheStaleMinutes` (default: 5). This is a configuration setting, not a runtime context value. `IContextStore` is for mutable per-workspace state (active item, timestamps). The value is injected as `int cacheStaleMinutes` primitive into `SyncCoordinator` via DI factory to avoid a Domain → Infrastructure circular reference (see DD-13). |
| DD-12 | Shared services (`ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator`) registered in `CommandServiceModule.cs` (CLI layer), not in `TwigServiceRegistration.AddTwigCoreServices()` (Infrastructure layer) | These services depend on `IAdoWorkItemService` which is registered with CLI-layer factory logic (org/project from `TwigConfiguration`). Registering in Infrastructure would require Infrastructure to know about CLI composition details. The domain service classes live in `Twig.Domain.Services`; the DI wiring belongs in the CLI entry point. `SyncCoordinator` additionally requires a DI factory lambda to extract `cacheStaleMinutes` from `TwigConfiguration`. |
| DD-13 | `SyncCoordinator` accepts `int cacheStaleMinutes` as a primitive, not `TwigConfiguration` | `TwigConfiguration` is defined in `Twig.Infrastructure.Config`. `Twig.Domain.csproj` has no `ProjectReference` to `Twig.Infrastructure`, and `Twig.Infrastructure.csproj` references `Twig.Domain` — introducing the dependency would create a compile-time circular reference: Domain → Infrastructure → Domain. Passing a primitive avoids the circular dependency while keeping the configuration value accessible. The DI factory in `CommandServiceModule.cs` extracts the value: `sp => new SyncCoordinator(..., sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes)`. |
| DD-14 | `ActiveItemResolver` exposes both `GetActiveItemAsync()` and `ResolveByIdAsync(int id)` | `GetActiveItemAsync()` resolves the item that is currently set as the active context (reads `IContextStore.GetActiveWorkItemIdAsync()` → cache → auto-fetch). `ResolveByIdAsync(int id)` resolves a specific item by ID (cache → auto-fetch). `SetCommand` receives a user-supplied ID/pattern as its argument — it needs `ResolveByIdAsync` for the numeric-ID path, not `GetActiveItemAsync`. Commands like `StatusCommand` that operate on the active context use `GetActiveItemAsync`. Pattern-match disambiguation remains inline in `SetCommand` (see DD-10). |
| DD-15 | `SyncChildrenAsync` has no per-parent staleness check — always fetches | Children sync is a cache-warming operation triggered after a context switch (e.g., `twig set 42`). There is no per-parent `last_synced_at` for children as a group — individual child items have their own `last_synced_at`, but the parent-children relationship has no aggregate staleness timestamp. Performing an unconditional fetch is cheap (single ADO call) and ensures the child list is always current. `SyncItemAsync` uses per-item staleness because individual items are re-synced frequently; children are only fetched in the context of a parent switch. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Modify `IWorkItemRepository.SaveBatchAsync` to be protected | Single save path | Breaks `RefreshCommand --force`; changes shared interface contract | **Rejected** — wrapper service is safer |
| Abstract base class `CacheFirstCommand` | Centralizes lifecycle | Inheritance limits flexibility; AOT source generators don't work well with generic base classes in ConsoleAppFramework | **Rejected** — composition preferred |
| `IAsyncEnumerable` streaming for all commands | Already proven in WorkspaceCommand | Overly complex for single-item commands (set, status); forces async iterator syntax on simple reads | **Rejected** — `RenderWithSyncAsync` is simpler for single-item cases |
| Lazy DI resolution | Reduces startup cost | Already fast (<50ms); ConsoleAppFramework resolves before command dispatch | **Rejected** — modular registration is sufficient |

---

## Dependencies

### External Dependencies

None new. All changes use existing libraries:
- **Spectre.Console** (`Live()` API) — already used for progressive rendering
- **SQLite** (WAL mode) — already configured for concurrent read/write

### Internal Dependencies

- **`SyncGuard`** (`Twig.Domain.Services`): Existing static class used by `ProtectedCacheWriter`. No changes needed.
- **`RenderingPipelineFactory`**: Existing factory that gates async vs sync rendering. Extended (not replaced) with `RenderWithSyncAsync` support.
- **`TwigServiceRegistration.AddTwigCoreServices()`**: Existing pattern. New DI modules follow the same convention.

### Sequencing Constraints

- EPIC-001 (shared services) must complete before EPIC-003–006 (command adoption)
- EPIC-002 (DI modularization) is independent — can run in parallel with EPIC-001
- EPIC-003 (rendering) must complete before EPIC-004 (read command adoption uses `RenderWithSyncAsync`)
- EPIC-005 and EPIC-006 depend on EPIC-001 but are independent of each other

---

## Impact Analysis

### Components Affected

| Layer | Components | Change Type |
|-------|-----------|-------------|
| Domain Services | New: `ActiveItemResolver`, `ActiveItemResult`, `ProtectedCacheWriter`, `SyncCoordinator`, `SyncResult` | Additive |
| Infrastructure | `TwigServiceRegistration` → rename class to `CoreServiceModule`; new `NetworkServiceModule`. **`AddTwigCoreServices()` extension method name is preserved** — callers continue using the same method name. | Refactor |
| CLI DI | New: `RenderingServiceModule`, `CommandServiceModule`, `CommandRegistrationModule` (all in `src/Twig/DependencyInjection/`) | Additive |
| CLI Rendering | `IAsyncRenderer` + `SpectreRenderer` | Extended |
| CLI Commands | 14 command files updated | Modified |
| CLI Entry | `Program.cs` DI block | Simplified |
| Twig.Tui | `Twig.Tui/Program.cs` line 41 calls `services.AddTwigCoreServices()`. No changes needed — the extension method name is preserved (DD-5). If the method signature or namespace changes, this caller must be updated. | Verified — no change |

### Backward Compatibility

- **JSON output**: MUST produce identical output. No sync indicators, no progressive rendering. Verified by existing test suite + new parity tests.
- **Minimal output**: Same as JSON — no behavioral change.
- **Non-TTY**: `RenderingPipelineFactory` already gates async rendering. Non-TTY always uses sync path.
- **`--no-live` flag**: Existing flag continues to force sync path.
- **Exit codes**: No changes. Same error/success codes as today.

### Performance Implications

- **Positive**: Read commands display output in <100ms (cache hit). Background sync runs after display.
- **Neutral**: Write commands still fetch latest revision for conflict detection. Display of cached state is new (positive) but fetch latency is unchanged.
- **Risk**: `SyncGuard.GetProtectedItemIdsAsync()` is called on every sync. This queries two SQLite tables. Current data volumes are small (dozens of items). No concern.

---

## Security Considerations

No new security boundaries are introduced. All data flows remain local (SQLite cache) or use existing authenticated ADO REST calls. `ProtectedCacheWriter` strengthens data integrity by preventing silent overwrites of locally modified items.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `ProtectedCacheWriter` changes contract — existing callers miss protected variant | Medium | High | `RefreshCommand` is the only direct `SaveBatchAsync` caller outside `ProtectedCacheWriter`. Explicit audit in EPIC-005. `--force` flag bypasses protection. |
| `SyncCoordinator` introduces concurrency between render and fetch — SQLite thread safety | Low | High | SQLite WAL mode already supports concurrent reads during writes (ASSUMPTION-001). `SyncCoordinator` runs fetch sequentially, not in parallel with cache reads. |
| `ActiveItemResolver` auto-fetch masks cache corruption | Low | Medium | Log to stderr when auto-fetch path is taken. Cache corruption is a separate concern addressed by `twig refresh`. |
| Spectre `Live()` cannot nest — commands using `Live()` for disambiguation + sync indicator | Medium | Medium | `RenderWithSyncAsync` wraps the entire command output (including disambiguation) in a single `Live()` context. Disambiguation completes before sync starts. |
| Regression in existing command behavior across 14 modified files | Medium | High | Existing 1,662-test suite. Each EPIC has exit criteria requiring full test pass. Incremental delivery reduces blast radius. |

---

## Open Questions

| ID | Question | Owner | Status |
|----|----------|-------|--------|
| OQ-1 | ~~Should `SyncCoordinator` use `last_synced_at` per-item or the global `last_refreshed_at`?~~ | Daniel Green | **Resolved → DD-8**: Use per-item `last_synced_at`. The column already exists in the SQLite schema. Add `LastSyncedAt` property to `WorkItem` aggregate and map it in `SqliteWorkItemRepository.MapRow`. |
| OQ-2 | What is the appropriate delay before clearing the sync indicator? PRD says 800ms. Is this configurable or hardcoded? | Daniel Green | Open |
| OQ-3 | ~~Should `ActiveItemResolver` resolve by pattern in addition to active context?~~ | Daniel Green | **Resolved → DD-10, DD-14**: No pattern resolution. `ActiveItemResolver` has two methods: `GetActiveItemAsync()` for active-context resolution (used by StatusCommand, TreeCommand, etc.) and `ResolveByIdAsync(int id)` for explicit-ID resolution (used by SetCommand's numeric-ID path). Pattern-match disambiguation in `SetCommand` (string → cache search → picker) is a distinct code path that remains inline. |
| OQ-4 | Should `ProtectedCacheWriter` report skipped items to the user via the sync indicator, or silently skip? PRD says "callers can inspect skipped IDs to inform the user" but UX is unspecified. | Daniel Green | Open |
| OQ-5 | Should `ActiveItemResolver` be extended with a `SetActiveContextAsync(int id)` method to absorb `IContextStore.SetActiveWorkItemIdAsync()` calls? This would allow `FlowStartCommand` and similar commands to drop `IContextStore` from their constructor, reducing params by 1. Trade-off: `ActiveItemResolver` becomes a "context manager" rather than a pure "resolver", which may violate single responsibility. | Daniel Green | Open |

---

## Implementation Phases

### Phase 1: Foundation (EPIC-001 + EPIC-002)
**Exit criteria**: Three shared services implemented, tested, and registered in DI. Program.cs DI modularized. All existing tests pass. No command behavioral changes.

### Phase 2: Rendering (EPIC-003)
**Exit criteria**: `RenderWithSyncAsync` implemented in `SpectreRenderer`. Rendering tests pass with `TestConsole`. No command changes yet.

### Phase 3: Read Commands (EPIC-004)
**Exit criteria**: `set`, `status`, `tree`, `up`, `down`, `workspace` use `ActiveItemResolver` and `SyncCoordinator`. Cache-first display verified. Dirty protection verified. JSON parity verified.

### Phase 4: Write + Flow Commands (EPIC-005 + EPIC-006)
**Exit criteria**: All write and flow commands use shared services. Pattern consolidation complete. Full test suite passes. 

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/ActiveItemResolver.cs` | Resolves active item from cache or ADO with auto-fetch |
| `src/Twig.Domain/Services/ActiveItemResult.cs` | Discriminated union for active-item resolution outcomes (Found, NoContext, FetchedFromAdo, Unreachable) |
| `src/Twig.Domain/Services/ProtectedCacheWriter.cs` | SyncGuard-protected save operations |
| `src/Twig.Domain/Services/SyncCoordinator.cs` | Background fetch orchestration with staleness checks |
| `src/Twig.Domain/Services/SyncResult.cs` | Discriminated union for sync outcomes |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Auth, HTTP, ADO client, iteration DI registrations |
| `src/Twig/DependencyInjection/RenderingServiceModule.cs` | Formatters, Spectre, pipeline factory DI registrations (CLI layer — depends on Spectre.Console) |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Shared command services DI (resolver, writer, coordinator) |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | All command class DI registrations |
| `tests/Twig.Domain.Tests/Services/ActiveItemResolverTests.cs` | Unit tests for ActiveItemResolver |
| `tests/Twig.Domain.Tests/Services/ProtectedCacheWriterTests.cs` | Unit tests for ProtectedCacheWriter |
| `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | Unit tests for SyncCoordinator |
| `tests/Twig.Cli.Tests/Rendering/RenderWithSyncTests.cs` | Rendering tests for RenderWithSyncAsync |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Replace ~330-line inline DI block with 5 module extension method calls |
| `src/Twig.Domain/Aggregates/WorkItem.cs` | Add `LastSyncedAt` property (read-only, set during persistence mapping) |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Map `last_synced_at` column to `WorkItem.LastSyncedAt` in `MapRow` |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Rename class to `CoreServiceModule`; preserve `AddTwigCoreServices()` method name; extract network registrations to new module |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Add `RenderWithSyncAsync` method |
| `src/Twig/Rendering/SpectreRenderer.cs` | Implement `RenderWithSyncAsync` with Live() context |
| `src/Twig/Commands/SetCommand.cs` | Adopt `ActiveItemResolver`, `SyncCoordinator`, `RenderWithSyncAsync`. Remove `IAdoWorkItemService` from constructor. |
| `src/Twig/Commands/StatusCommand.cs` | Adopt ActiveItemResolver, SyncCoordinator |
| `src/Twig/Commands/TreeCommand.cs` | Adopt ActiveItemResolver |
| `src/Twig/Commands/NavigationCommands.cs` | Adopt ActiveItemResolver |
| `src/Twig/Commands/WorkspaceCommand.cs` | Adopt `ActiveItemResolver` for initial active-item resolution. Keep existing stale-while-revalidate pattern (DD-9). |
| `src/Twig/Commands/StateCommand.cs` | Adopt `ActiveItemResolver`, remove `IContextStore` |
| `src/Twig/Commands/NoteCommand.cs` | Adopt ActiveItemResolver |
| `src/Twig/Commands/UpdateCommand.cs` | Adopt ActiveItemResolver, SyncCoordinator |
| `src/Twig/Commands/EditCommand.cs` | Adopt ActiveItemResolver |
| `src/Twig/Commands/SaveCommand.cs` | Adopt ActiveItemResolver, ProtectedCacheWriter |
| `src/Twig/Commands/FlowStartCommand.cs` | Adopt `ActiveItemResolver`, `ProtectedCacheWriter`. Remove unused `IPendingChangeStore`. Constructor 13→14 params. |
| `src/Twig/Commands/FlowDoneCommand.cs` | Adopt ActiveItemResolver, ProtectedCacheWriter |
| `src/Twig/Commands/FlowCloseCommand.cs` | Adopt ActiveItemResolver, ProtectedCacheWriter |
| `src/Twig/Commands/RefreshCommand.cs` | Adopt `ProtectedCacheWriter` for save path; `--force` bypasses protection |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | All changes are additive or in-place refactors |

---

## Implementation Plan

### EPIC-001: Foundation — Shared Services and Protected Saves

**Status**: DONE
**Completed**: 2026-03-17

**Goal**: Introduce the three core shared services (`ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator`) and `SyncResult` discriminated union with comprehensive unit tests. No command changes — pure additive infrastructure.

**Prerequisites**: None

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `SyncResult` discriminated union with variants: `UpToDate`, `Updated(int ChangedCount)`, `Failed(string Reason)`, `Skipped(string Reason)` | `src/Twig.Domain/Services/SyncResult.cs` | DONE |
| E1-T1-ar | IMPL | Create `ActiveItemResult` discriminated union with variants: `Found(WorkItem)`, `NoContext`, `FetchedFromAdo(WorkItem)`, `Unreachable(int Id, string Reason)`. Separate file for consistency with `SyncResult`. | `src/Twig.Domain/Services/ActiveItemResult.cs` | DONE |
| E1-T1a | IMPL | Add `LastSyncedAt` property to `WorkItem` aggregate: `public DateTimeOffset? LastSyncedAt { get; init; }`. This exposes the existing `last_synced_at` SQLite column (already written on every save at `SqliteWorkItemRepository.SaveWorkItem` line 210) to the domain layer so `SyncCoordinator` can check per-item staleness. **No schema migration needed** — the column already exists. | `src/Twig.Domain/Aggregates/WorkItem.cs` | DONE |
| E1-T1b | IMPL | Update `SqliteWorkItemRepository.MapRow` to read the `last_synced_at` column and set `WorkItem.LastSyncedAt`. Parse with `DateTimeOffset.Parse(..., CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)`, matching the existing `SeedCreatedAt` pattern. | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | DONE |
| E1-T2 | IMPL | Create `ActiveItemResolver` service. Constructor: `IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService`. **Two public methods**: (1) `GetActiveItemAsync(CancellationToken)` returns `ActiveItemResult` discriminated union (`Found(WorkItem)`, `NoContext`, `FetchedFromAdo(WorkItem)`, `Unreachable(int id, string reason)`). Reads active ID from `IContextStore`, then cache → auto-fetch. (2) `ResolveByIdAsync(int id, CancellationToken)` returns `ActiveItemResult` (`Found(WorkItem)`, `FetchedFromAdo(WorkItem)`, `Unreachable(int id, string reason)`) — resolves a specific item by ID (cache → auto-fetch), used by `SetCommand` for explicit numeric-ID paths (see DD-14). On cache miss: `FetchAsync(id)` → `SaveAsync(item)` → `FetchedFromAdo`. On failure: `Unreachable`. Log to stderr on auto-fetch path. Pattern-match disambiguation in `SetCommand` remains inline (DD-10). | `src/Twig.Domain/Services/ActiveItemResolver.cs` | DONE |
| E1-T3 | IMPL | Create `ProtectedCacheWriter` service. Constructor: `IWorkItemRepository`, `IPendingChangeStore`. `SaveBatchProtectedAsync`: calls `SyncGuard.GetProtectedItemIdsAsync()`, filters out protected IDs, calls `SaveBatchAsync()`, returns skipped IDs. `SaveProtectedAsync`: single-item variant. | `src/Twig.Domain/Services/ProtectedCacheWriter.cs` | DONE |
| E1-T4 | IMPL | Create `SyncCoordinator` service. Constructor: `IWorkItemRepository`, `IAdoWorkItemService`, `ProtectedCacheWriter`, `int cacheStaleMinutes` (primitive — see DD-13; injected via DI factory from `TwigConfiguration.Display.CacheStaleMinutes`). `SyncItemAsync(int id, ct)`: read `WorkItem.LastSyncedAt` from `workItemRepo.GetByIdAsync(id)`, compare against `cacheStaleMinutes`. If fresh → `UpToDate`. If stale or null → `FetchAsync(id)` → `ProtectedCacheWriter.SaveProtectedAsync()` → `Updated(1)` or `Skipped`. `SyncChildrenAsync(int parentId, ct)`: unconditionally calls `FetchChildrenAsync(parentId)` → `ProtectedCacheWriter.SaveBatchProtectedAsync()` → return counts. No per-parent staleness check (see DD-15). **Does not handle workspace-scope refresh** — that pattern remains in `WorkspaceCommand` (see DD-9). | `src/Twig.Domain/Services/SyncCoordinator.cs` | DONE |
| E1-T5 | TEST | Unit tests for `ActiveItemResolver`: (a) `GetActiveItemAsync` — cache hit → `Found`, cache miss → fetch → `FetchedFromAdo`, fetch fails → `Unreachable`, no active ID → `NoContext`. (b) `ResolveByIdAsync` — cache hit → `Found`, cache miss → fetch → `FetchedFromAdo`, fetch fails → `Unreachable`. | `tests/Twig.Domain.Tests/Services/ActiveItemResolverTests.cs` | DONE |
| E1-T6 | TEST | Unit tests for `ProtectedCacheWriter`: saves unprotected items, skips protected items, returns correct skipped IDs, handles empty input, handles all-protected input. | `tests/Twig.Domain.Tests/Services/ProtectedCacheWriterTests.cs` | DONE |
| E1-T7 | TEST | Unit tests for `SyncCoordinator`: fresh item (recent `LastSyncedAt`) → `UpToDate`, stale item → fetches and saves → `Updated`, stale protected item → `Skipped`, fetch failure → `Failed`, children sync always fetches regardless of parent staleness (DD-15), children sync with mixed protected/unprotected, null `LastSyncedAt` → treated as stale. | `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | DONE |
| E1-T8 | TEST | Unit tests for `WorkItem.LastSyncedAt` mapping: verify `SqliteWorkItemRepository.MapRow` correctly parses `last_synced_at` column into `WorkItem.LastSyncedAt`. | `tests/Twig.Infrastructure.Tests/Persistence/` | DONE |

**Acceptance Criteria**:
- [x] `WorkItem.LastSyncedAt` is populated when reading from SQLite cache
- [x] `ActiveItemResolver.GetActiveItemAsync` resolves from cache with zero ADO calls
- [x] `ActiveItemResolver.GetActiveItemAsync` auto-fetches on cache miss and caches the result
- [x] `ActiveItemResolver.ResolveByIdAsync` resolves by explicit ID from cache or auto-fetch
- [x] `ProtectedCacheWriter` skips items in `SyncGuard.GetProtectedItemIdsAsync()` result set
- [x] `SyncCoordinator` accepts `int cacheStaleMinutes` (not `TwigConfiguration`) — no Domain → Infrastructure dependency
- [x] `SyncCoordinator` returns `UpToDate` for items with recent `LastSyncedAt` (within `cacheStaleMinutes`)
- [x] `SyncCoordinator` returns `Updated(n)` for stale data with successful fetch
- [x] `SyncCoordinator` returns `Failed(reason)` on network error
- [x] All existing tests pass (no regressions)
- [x] **No DI registration changes in this EPIC** — registration handled in EPIC-002 (see DD-12)

---

### EPIC-002: DI Modularization

**Goal**: Extract the monolithic DI block in `Program.cs` into focused module methods. Pure structural refactor — no behavioral changes.

**Prerequisites**: None (independent of EPIC-001)

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Create `NetworkServiceModule.cs` with `AddTwigNetworkServices(this IServiceCollection, TwigConfiguration)`. Move: `IAuthenticationProvider`, `HttpClient`, `IAdoWorkItemService`, `IAdoGitService` (conditional), `IIterationService`. | `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs`, `src/Twig/Program.cs` | TO DO |
| E2-T2 | IMPL | Create `RenderingServiceModule.cs` in `src/Twig/DependencyInjection/` (CLI layer, NOT Infrastructure — `Twig.Infrastructure` does not reference `Spectre.Console`) with `AddTwigRenderingServices(this IServiceCollection)`. Move: `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter`, `OutputFormatterFactory`, `IAnsiConsole`, `SpectreTheme`, `IAsyncRenderer`, `RenderingPipelineFactory`. | `src/Twig/DependencyInjection/RenderingServiceModule.cs`, `src/Twig/Program.cs` | TO DO |
| E2-T3 | IMPL | Create `CommandServiceModule.cs` with `AddTwigCommandServices(this IServiceCollection)`. Move: `HintEngine`, `IEditorLauncher`, `IConsoleInput`. **Register shared services from EPIC-001 here** (see DD-12): `ActiveItemResolver` (singleton), `ProtectedCacheWriter` (singleton), `SyncCoordinator` (singleton, via factory lambda: `sp => new SyncCoordinator(sp.GetRequiredService<IWorkItemRepository>(), sp.GetRequiredService<IAdoWorkItemService>(), sp.GetRequiredService<ProtectedCacheWriter>(), sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes)` — see DD-13). This is the single registration point for these services — they are NOT registered in `TwigServiceRegistration.AddTwigCoreServices()`. | `src/Twig/DependencyInjection/CommandServiceModule.cs`, `src/Twig/Program.cs` | TO DO |
| E2-T4 | IMPL | Create `CommandRegistrationModule.cs` with `AddTwigCommands(this IServiceCollection)`. Move ALL command class registrations. Preserve factory lambdas. | `src/Twig/DependencyInjection/CommandRegistrationModule.cs`, `src/Twig/Program.cs` | TO DO |
| E2-T5 | IMPL | Update `Program.cs` to call module methods. DI section becomes ~30 lines: `AddTwigCoreServices()`, `AddTwigNetworkServices(config)`, `AddTwigRenderingServices()`, `AddTwigCommandServices()`, `AddTwigCommands()`. Legacy migration and git remote detection stay in `Program.cs`. | `src/Twig/Program.cs` | TO DO |
| E2-T6 | TEST | Verify all existing tests pass. No behavioral change expected. | All test projects | TO DO |

**Acceptance Criteria**:
- [ ] `Program.cs` DI section is ≤ 30 lines (excluding legacy migration and git remote detection)
- [ ] Each module method is a static extension on `IServiceCollection`
- [ ] All existing tests pass unchanged
- [ ] AOT build succeeds (`dotnet publish` with trimming)

---

### EPIC-003: Rendering — In-Place Sync Status

**Goal**: Add the `RenderWithSyncAsync` rendering primitive that commands will use for cache-render-fetch-revise.

**Prerequisites**: EPIC-001 (needs `SyncResult` type)

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Add `RenderWithSyncAsync` to `IAsyncRenderer` interface. Signature: `Task RenderWithSyncAsync(Func<Task<IRenderable>> buildCachedView, Func<Task<SyncResult>> performSync, Func<SyncResult, Task<IRenderable?>> buildRevisedView, CancellationToken ct)`. | `src/Twig/Rendering/IAsyncRenderer.cs` | TO DO |
| E3-T2 | IMPL | Implement `RenderWithSyncAsync` in `SpectreRenderer`. Pattern: (1) render cached view via `Live()`, (2) show `[dim]⟳ syncing...[/]` status below, (3) call `performSync()`, (4) on `UpToDate` show `✓ up to date` then clear, (5) on `Updated` revise view and show count, (6) on `Failed` show `⚠ sync failed` and persist, (7) on `Skipped` show `✓ up to date`. | `src/Twig/Rendering/SpectreRenderer.cs` | TO DO |
| E3-T3 | TEST | Rendering tests using `TestConsole`: (a) cached view rendered before sync, (b) `UpToDate` shows and clears, (c) `Updated` replaces content, (d) `Failed` shows warning and persists. | `tests/Twig.Cli.Tests/Rendering/RenderWithSyncTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `RenderWithSyncAsync` renders cached view before calling sync
- [ ] Sync indicator replaces in-place (not appended as new lines)
- [ ] `UpToDate` status clears after brief delay
- [ ] `Failed` status persists (not cleared)
- [ ] Non-TTY path never calls `RenderWithSyncAsync`

---

### EPIC-004: Adopt Shared Services in Read Commands

**Goal**: Update `set`, `status`, `tree`, `up`, `down`, `workspace` to use `ActiveItemResolver` and `SyncCoordinator`. This is where the user-facing UX improvement lands.

**Prerequisites**: EPIC-001, EPIC-003

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1a | IMPL | **SetCommand — numeric-ID resolution**: Replace inline `workItemRepo.GetByIdAsync` + `adoService.FetchAsync` (lines 37–46) with `ActiveItemResolver.ResolveByIdAsync(id)`. | `src/Twig/Commands/SetCommand.cs` | TO DO |
| E4-T1b | IMPL | **SetCommand — parent chain hydration**: Replace inline `adoService.FetchAsync(parentId)` + `workItemRepo.SaveAsync(parent)` (lines 90–101) with `ActiveItemResolver.ResolveByIdAsync(parentId)`. | `src/Twig/Commands/SetCommand.cs` | TO DO |
| E4-T1c | IMPL | **SetCommand — children sync**: Replace `FetchChildrenAsync` (line 104) + `SaveBatchAsync` (line 106) with `SyncCoordinator.SyncChildrenAsync`. TTY path uses `RenderWithSyncAsync`; non-TTY calls `SyncChildrenAsync` synchronously (always fetches — see DD-15). | `src/Twig/Commands/SetCommand.cs` | TO DO |
| E4-T1d | IMPL | **SetCommand — constructor cleanup**: Remove `IAdoWorkItemService` (all 3 usages absorbed by sub-tasks above). Add `ActiveItemResolver`, `SyncCoordinator`. Pattern-match disambiguation (lines 48–87) remains inline per DD-10. | `src/Twig/Commands/SetCommand.cs` | TO DO |
| E4-T2 | IMPL | **StatusCommand**: Replace inline `GetByIdAsync` + null check with `ActiveItemResolver.GetActiveItemAsync()`. Handle all result variants. Add sync indicator via `RenderWithSyncAsync` wrapper. | `src/Twig/Commands/StatusCommand.cs` | TO DO |
| E4-T3 | IMPL | **TreeCommand**: Replace inline active-item resolution with `ActiveItemResolver`. Tree is read-only from cache — no background sync needed. `ActiveItemResolver.GetActiveItemAsync()` provides auto-fetch-on-miss for the active item (G-3). Add stale hint if cache is stale. | `src/Twig/Commands/TreeCommand.cs` | TO DO |
| E4-T4 | IMPL | **NavigationCommands (Up/Down)**: Replace inline active-item resolution with `ActiveItemResolver`. Navigation delegates to `SetCommand` for sync. | `src/Twig/Commands/NavigationCommands.cs` | TO DO |
| E4-T5 | IMPL | **WorkspaceCommand**: Adopt `ActiveItemResolver` for initial active-item resolution (line 51). Keep existing `StreamWorkspaceData` stale-while-revalidate pattern as-is (DD-9). | `src/Twig/Commands/WorkspaceCommand.cs` | TO DO |

> **E4-T5 implementation notes**: The refresh path (lines 67–112) re-reads item data from cache (`GetByIterationAsync`, `GetSeedsAsync`) and calls `iterationService.GetCurrentIterationAsync()` at line 81 — an ADO network call for iteration metadata, not work item data. This path never calls `SaveAsync`/`SaveBatchAsync`, so `ProtectedCacheWriter` is not applicable. The `IsCacheStale` static method remains. Only the initial active-item resolution at line 51 changes to use `ActiveItemResolver.GetActiveItemAsync()` for auto-fetch-on-miss behavior.
>
> **UX note**: `WorkspaceCommand`'s refresh path re-queries local cache after getting a fresh iteration path — it does not fetch fresh work item data from ADO. Users who need updated field values (e.g., state changes made in ADO web) must still run `twig refresh`. This is existing behavior, unchanged by this plan.
| E4-T6 | TEST | Update tests for all 5 read commands. Verify: (a) cached data displayed immediately, (b) stale data + successful sync, (c) stale data + failed sync, (d) dirty items not overwritten, (e) JSON output parity, (f) auto-fetch on cache miss. | `tests/Twig.Cli.Tests/Commands/` | TO DO |

**Acceptance Criteria**:
- [ ] `twig status` displays output from cache with no ADO calls
- [ ] `twig set <cached-id>` with stale children fetches in background and does NOT overwrite dirty children
- [ ] `twig status` auto-fetches when active item not in cache
- [ ] JSON output mode produces identical output pre/post refactor
- [ ] Non-TTY output works without `Live()` rendering
- [ ] All existing tests pass

---

### EPIC-005: Adopt Shared Services in Write Commands

**Goal**: Update `state`, `note`, `update`, `edit`, `save`, `refresh` to use `ActiveItemResolver`, `SyncCoordinator`, and `ProtectedCacheWriter`. Write commands still need conflict detection but display cached state first.

**Prerequisites**: EPIC-001

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E5-T1 | IMPL | **StateCommand**: Replace inline resolution (`contextStore.GetActiveWorkItemIdAsync()` line 37 → `workItemRepo.GetByIdAsync()` line 44) with `ActiveItemResolver.GetActiveItemAsync()`. Remove `IContextStore`, add `ActiveItemResolver`. Net param count: 9→9. | `src/Twig/Commands/StateCommand.cs` | TO DO |

> **E5-T1 implementation notes**: All other constructor dependencies remain actively used: `IWorkItemRepository` (SaveAsync, GetChildrenAsync), `IAdoWorkItemService` (FetchAsync for pre-patch conflict detection, PatchAsync for state mutation), `IPendingChangeStore` (AutoPushNotesHelper), `IProcessConfigurationProvider`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `IPromptStateWriter?`. The benefit is pattern consolidation (replacing 4-line boilerplate), not parameter reduction.
| E5-T2 | IMPL | **NoteCommand**: Replace inline resolution with `ActiveItemResolver`. No sync needed (local-only). | `src/Twig/Commands/NoteCommand.cs` | TO DO |
| E5-T3 | IMPL | **UpdateCommand**: Replace inline resolution with `ActiveItemResolver`. Use `SyncCoordinator` for pre-patch fetch. | `src/Twig/Commands/UpdateCommand.cs` | TO DO |
| E5-T4 | IMPL | **EditCommand**: Replace inline resolution with `ActiveItemResolver`. No sync needed (local-only). | `src/Twig/Commands/EditCommand.cs` | TO DO |
| E5-T5 | IMPL | **SaveCommand**: Replace inline resolution with `ActiveItemResolver`. Replace `SaveBatchAsync` calls with `ProtectedCacheWriter` where applicable. Conflict resolution flow stays. | `src/Twig/Commands/SaveCommand.cs` | TO DO |
| E5-T6a | IMPL | **RefreshCommand — protected save path**: Replace unguarded `SaveBatchAsync` calls (lines 158–163) with `ProtectedCacheWriter.SaveBatchProtectedAsync()`. `--force` bypasses protection (calls raw `SaveBatchAsync`). | `src/Twig/Commands/RefreshCommand.cs` | TO DO |
| E5-T6b | IMPL | **RefreshCommand — conflict detection as informational**: Preserve `FindConflictsAsync` (lines 96–109) for user-facing diagnostic output. Change interaction: instead of conflicts blocking the save, they produce informational warnings alongside the skip. | `src/Twig/Commands/RefreshCommand.cs` | TO DO |

> **E5-T6 behavioral change**: The current code (lines 91–163) uses an **all-or-nothing** approach: computes `protectedIds` via `SyncGuard`, passes them to `FindConflictsAsync` for revision-conflict detection, then either aborts entirely if any conflict exists, or saves ALL items without filtering. Adopting `ProtectedCacheWriter` changes this to **per-item skip**: protected items are silently excluded from saves rather than blocking the entire refresh. This means a protected item with no revision conflict (currently saved during non-`--force` refresh) will be skipped after this change. This is the intended behavior (preventing any overwrite of dirty data), but it is a behavioral change, not a pure refactor.
| E5-T7 | TEST | Update tests for all 6 write commands. Verify conflict detection, pending change preservation, `RefreshCommand` preserves `FindConflictsAsync` revision-conflict output. **New tests for RefreshCommand behavioral change** (E5-T6a/b): (a) protected item with no revision conflict is now skipped (previously saved), (b) protected item with revision conflict is skipped and warning shown, (c) `--force` saves all items including protected. | `tests/Twig.Cli.Tests/Commands/` | TO DO |

**Acceptance Criteria**:
- [ ] `StateCommand` constructor has 9 parameters
- [ ] `RefreshCommand --force` bypasses `ProtectedCacheWriter` and saves all items
- [ ] `RefreshCommand` (no --force) skips protected items via `ProtectedCacheWriter`
- [ ] `RefreshCommand` preserves revision-conflict detection output as informational warnings for skipped items
- [ ] Conflict detection still works in `StateCommand`, `UpdateCommand`, `SaveCommand`
- [ ] Pending changes preserved during sync for all write commands
- [ ] All existing tests pass

---

### EPIC-006: Adopt Shared Services in Flow Commands

**Goal**: Update `flow-start`, `flow-done`, `flow-close` to use `ActiveItemResolver` and `ProtectedCacheWriter`. Keep step-by-step UX.

**Prerequisites**: EPIC-001, EPIC-005 (SaveCommand dependency)

**Tasks**:

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E6-T1a | IMPL | **FlowStartCommand — resolution + protection**: Replace inline active-item resolution with `ActiveItemResolver`. Replace unguarded `workItemRepo.SaveAsync` calls with `ProtectedCacheWriter.SaveProtectedAsync`. Keep step-by-step progress UX. | `src/Twig/Commands/FlowStartCommand.cs` | TO DO |
| E6-T1b | IMPL | **FlowStartCommand — constructor cleanup**: Remove `IPendingChangeStore` (line 42: unused placeholder). Add `ActiveItemResolver`, `ProtectedCacheWriter`. Param count: 13→14. See OQ-5 for future consolidation. | `src/Twig/Commands/FlowStartCommand.cs` | TO DO |

> **E6-T1 retained dependencies**: `IWorkItemRepository` (GetByIterationAsync, SaveAsync for non-protected paths), `IAdoWorkItemService` (PatchAsync lines 173, 209), `IContextStore` (SetActiveWorkItemIdAsync line 155), `IProcessConfigurationProvider`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`, `RenderingPipelineFactory?`, `IGitService?`, `IIterationService?`, `IPromptStateWriter?`. The complexity reduction is in method bodies (~30 lines of inline fetch/save/guard logic replaced), not constructor params.
| E6-T2 | IMPL | **FlowDoneCommand**: Same pattern. Delegates to SaveCommand (already updated in EPIC-005). | `src/Twig/Commands/FlowDoneCommand.cs` | TO DO |
| E6-T3 | IMPL | **FlowCloseCommand**: Same pattern. Explicit dirty-item guard stays (correctness check). | `src/Twig/Commands/FlowCloseCommand.cs` | TO DO |
| E6-T4 | TEST | Update flow command tests. Verify step-by-step UX preserved, protected saves, `IPendingChangeStore` removal from `FlowStartCommand`. | `tests/Twig.Cli.Tests/Commands/` | TO DO |

**Acceptance Criteria**:
- [ ] `FlowStartCommand` removes unused `IPendingChangeStore` and adds `ActiveItemResolver`, `ProtectedCacheWriter`
- [ ] Step-by-step progress UX unchanged for all flow commands
- [ ] Protected saves prevent dirty-value overwrites
- [ ] All existing flow command tests pass
- [ ] JSON/minimal output parity maintained

---

## References

- **PRD**: `docs/projects/twig-cache-first.prd.md` — Full requirements with acceptance criteria
- **SyncGuard**: `src/Twig.Domain/Services/SyncGuard.cs` — Existing dirty-item detection
- **SpectreRenderer**: `src/Twig/Rendering/SpectreRenderer.cs` — Existing `Live()` rendering patterns
- **TwigServiceRegistration**: `src/Twig.Infrastructure/TwigServiceRegistration.cs` — Existing DI module pattern
- **RenderingPipelineFactory**: `src/Twig/Rendering/RenderingPipelineFactory.cs` — TTY/non-TTY rendering path gating
- **Spectre.Console Live()**: https://spectreconsole.net/live/live-display — Progressive rendering API
- **RFC 2119**: https://datatracker.ietf.org/doc/html/rfc2119 — Requirement level keywords

---

## Revision History

| Revision | Date | Summary |
|----------|------|---------|
| 4.0 | 2026-03-17 | Initial architecture with corrected constructor counts, resolved OQ-1 and OQ-3, added DD-8 through DD-14. |
| 5.0 | 2026-03-17 | Readability revision: rewrote executive summary for user-benefit-first framing; decomposed dense task descriptions (E4-T1, E5-T1, E5-T6, E6-T1) into sub-tasks with prose notes beneath tables; simplified Modified Files entries to describe *what* changes (not implementation guidance); cleaned acceptance criteria in EPIC-005 and EPIC-006 to remove inline rationale; condensed DD-8 and DD-9 rationale; added Security Considerations section; added this Revision History. |
| 6.0 | 2026-03-17 | Addressed technical and readability review feedback: (1) Added DD-15 resolving SyncChildrenAsync staleness inconsistency — children always fetch unconditionally, removed "if stale" from E4-T1c; (2) Corrected DI line count from ~260 to ~330 (Program.cs lines 33–382); (3) Added ActiveItemResult.cs to New Files table and E1-T1-ar task; (4) Added Twig.Tui to Impact Analysis with confirmation that AddTwigCoreServices() method name is preserved; (5) Simplified remaining implementation-heavy Modified Files entries (Program.cs, RefreshCommand, StateCommand); (6) Removed parenthetical commentary from EPIC-005 acceptance criteria; (7) Added API note about CancellationToken closure capture in RenderWithSyncAsync; (8) Clarified TreeCommand auto-fetch-on-miss via ActiveItemResolver in E4-T3. |
