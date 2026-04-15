# Cache-Aware Rendering & Live Refresh

**Epic:** #1519  
> **Status**: ✅ Done  
**Revision:** 2 — Addressed tech (82→) and readability (88→) review feedback

---

## Executive Summary

This design implements a **two-pass rendering pattern** across all twig display commands (`status`, `show`, `tree`, `query`, `workspace`) and a **context-change working set extension** for `twig set`. Today, most display commands render from a local SQLite cache that may be stale, with no visual indication of staleness or dirty state. Users see outdated work item states (e.g., items shown as "To Do" when they're actually "Done" in ADO), eroding trust in CLI output. The solution introduces: (1) immediate cache-first rendering with cache-age indicators, (2) background ADO sync with Spectre.Console Live region updates, (3) prominent dirty-item indicators, (4) a `--no-refresh` opt-out flag, and (5) automatic working set expansion when `twig set` targets an out-of-sprint item. The existing `RenderWithSyncAsync` primitive in `SpectreRenderer` provides the foundation — this work generalizes that pattern, adds cache-age metadata to the display, enriches dirty-item rendering, and introduces a `ContextChangeService` for consistent working set expansion.

## Background

### Current Architecture

Twig uses a layered architecture:
- **Domain layer** (`Twig.Domain`): Aggregates (`WorkItem`), services (`SyncCoordinator`, `WorkingSetService`, `ActiveItemResolver`), interfaces (`IWorkItemRepository`, `IAdoWorkItemService`)
- **Infrastructure layer** (`Twig.Infrastructure`): SQLite persistence (`SqliteWorkItemRepository`), ADO REST client, configuration
- **CLI layer** (`Twig`): Commands, rendering (`SpectreRenderer`), formatters (`HumanOutputFormatter`, `JsonOutputFormatter`)

### Existing Two-Pass Rendering

The codebase already implements a partial two-pass pattern:
- `IAsyncRenderer.RenderWithSyncAsync()` exists as a cache-render-fetch-revise primitive
- `SpectreRenderer.RenderWithSyncAsync()` uses `Spectre.Console.Live` regions with sync status indicators (`⟳ syncing...`, `✓ up to date`, `⚠ sync failed`)
- `StatusCommand` uses `RenderWithSyncAsync` to render cached status then sync the working set
- `TreeCommand` uses `RenderWithSyncAsync` but only for a post-render sync (renders a blank `Text(" ")` as the cached view)

### Current Gaps
1. **No cache-age display**: `WorkItem.LastSyncedAt` exists but is never shown to users
2. **Inconsistent two-pass adoption**: `StatusCommand` uses it properly; `TreeCommand` uses it awkwardly (empty cached view); `ShowCommand` and `QueryCommand` don't use it at all
3. **No dirty-item indicators**: `WorkItem.IsDirty` exists but display commands don't render it
4. **No `--no-refresh` flag**: Only `--no-live` exists (which disables the entire async renderer, not just the sync pass)
5. **`twig set` doesn't expand working set**: Context changes to out-of-sprint items leave the cache with an isolated item — no parents, children, or related links

### Key Existing Components

| Component | Location | Relevance |
|-----------|----------|-----------|
| `WorkItem.LastSyncedAt` | `Aggregates/WorkItem.cs:53` | Cache staleness timestamp (already tracked) |
| `WorkItem.IsDirty` | `Aggregates/WorkItem.cs:41` | Dirty flag (already tracked) |
| `PendingChangeRecord` | `Common/PendingChangeRecord.cs` | Change details (type, field, old/new values) |
| `IPendingChangeStore.GetChangesAsync()` | `Interfaces/IPendingChangeStore.cs` | Retrieves pending changes per item |
| `RenderWithSyncAsync()` | `Rendering/SpectreRenderer.cs:969` | Existing Live region two-pass primitive |
| `SyncCoordinator.SyncWorkingSetAsync()` | `Services/SyncCoordinator.cs:85` | Syncs stale items in working set |
| `SyncCoordinator.SyncItemSetAsync()` | `Services/SyncCoordinator.cs:109` | Syncs explicit item IDs |
| `SyncCoordinator.SyncChildrenAsync()` | `Services/SyncCoordinator.cs:182` | Fetches all children of a parent |
| `SyncCoordinator.SyncLinksAsync()` | `Services/SyncCoordinator.cs:202` | Fetches item + links from ADO |
| `WorkingSetService.ComputeAsync()` | `Services/WorkingSetService.cs:40` | Computes working set from cache |
| `ActiveItemResolver.ResolveByIdAsync()` | `Services/ActiveItemResolver.cs:43` | Cache-hit → auto-fetch pattern |
| `RenderingPipelineFactory.Resolve()` | `Rendering/RenderingPipelineFactory.cs:18` | Routes async vs sync rendering |
| `SpectreRenderer.BuildStatusViewAsync()` | `Rendering/SpectreRenderer.cs` | Builds status IRenderable |
| `DisplayConfig.CacheStaleMinutes` | `Infrastructure/Config/TwigConfiguration.cs:336` | Staleness threshold (default: 5 min, configurable) |

### Call-Site Audit: Commands Using Display Rendering

| File | Method | Current Rendering | Has Sync? | Has `--no-live`? | Impact |
|------|--------|-------------------|-----------|------------------|--------|
| `Commands/StatusCommand.cs` | `ExecuteCoreAsync` | `RenderWithSyncAsync` + `RenderStatusAsync` | ✅ Working set sync | ✅ `noLive` param | Add cache-age, dirty indicators, `--no-refresh` |
| `Commands/TreeCommand.cs` | `ExecuteCoreAsync` | `RenderTreeAsync` + blank `RenderWithSyncAsync` | ✅ Post-render sync | ✅ `noLive` param | Integrate sync into tree render, add cache-age |
| `Commands/ShowCommand.cs` | `ExecuteCoreAsync` | `RenderStatusAsync` (no sync) | ❌ Cache-only | ❌ No flag | Add `--no-refresh`, optional sync pass |
| `Commands/QueryCommand.cs` | `ExecuteCoreAsync` | `FormatQueryResults` (formatter only) | ❌ Always fetches from ADO | ❌ No flag | Already live — cache results, add age display |
| `Commands/WorkspaceCommand.cs` | `ExecuteCoreAsync` | `RenderWorkspaceAsync` (streaming) | ✅ Via streaming chunks | ✅ `noLive` param | Add cache-age to sprint items |
| `Commands/SetCommand.cs` | `ExecuteCoreAsync` | `RenderStatusAsync` (post-set display) | ✅ Item + parent chain sync | ❌ No flag | Add `ContextChangeService` integration |
| `Commands/NewCommand.cs` | `ExecuteAsync` | `FormatSuccess` (simple output) | N/A (creates in ADO) | ❌ No flag | Add `ContextChangeService` when `--set` |

### Call-Site Audit: `RenderWithSyncAsync` Callers

| File | Line | Usage | Notes |
|------|------|-------|-------|
| `Commands/StatusCommand.cs` | 136 | Full two-pass: builds status view → syncs working set → revises | Proper usage; needs `--no-refresh` bypass |
| `Commands/TreeCommand.cs` | 119 | Degenerate: empty cached view → syncs → no revision | Should integrate sync into tree rendering |

### Call-Site Audit: `contextStore.SetActiveWorkItemIdAsync` (Context Change Points)

| File | Line | Scenario | Should Trigger Working Set Extension? |
|------|------|----------|--------------------------------------|
| `Commands/SetCommand.cs` | 146 | `twig set <id>` | ✅ Yes — primary use case |
| `Commands/NewCommand.cs` | 134 | `twig new --set` | ✅ Yes — just created, needs graph |
| `Commands/FlowStartCommand.cs` | 161 | `twig flow start` | ✅ Yes — starting work on an item |
| `Commands/HookHandlerCommand.cs` | ~varies | git post-checkout hook | ❌ No — implicit, should be lightweight |
| `Commands/NavigationCommands.cs` | ~varies | `twig up/down/root` | ❌ No — target is already cached; no graph expansion needed |
| `Commands/NavigationHistoryCommands.cs` | 41 | `twig back` | ❌ No — navigates to previously-visited item already in cache; recording a duplicate history entry is intentionally bypassed (DD-04 in nav-history design) |
| `Commands/NavigationHistoryCommands.cs` | 71 | `twig fore` | ❌ No — same rationale as `back`; restores forward-stack context |
| `Commands/NavigationHistoryCommands.cs` | 177 | `twig history` (interactive picker) | ❌ No — picks from previously-visited items already in cache |
| `Commands/SeedPublishCommand.cs` | 43, 60 | `twig seed publish` (active seed remapped) | ❌ No — context update is a side-effect of ID remapping (seed→ADO); the item was just published so it's already fresh. Extension would be redundant. |
| `Commands/StashCommand.cs` | ~varies | `twig stash pop` | ❌ No — restoring a previously-cached context |

## Problem Statement

1. **Stale data confusion**: Users see outdated work item states with no indication that the data is from cache. The `LastSyncedAt` field exists on every `WorkItem` but is never displayed.

2. **Hidden dirty state**: Items with uncommitted local changes (`IsDirty=true`, pending `FieldChange` records) are rendered identically to clean items. Users don't know which items have unsaved modifications.

3. **Inconsistent sync behavior**: `StatusCommand` does a proper two-pass render-then-sync, `TreeCommand` does a half-hearted post-render sync with an empty cached view, `ShowCommand` never syncs, and `QueryCommand` always fetches live but doesn't indicate this.

4. **No opt-out for scripting**: There's no way to skip the background sync for offline or scripted use cases. `--no-live` disables the entire Spectre renderer, not just the sync.

5. **Orphaned context after `twig set`**: When setting context to an out-of-sprint item, only the item itself (and parent chain) are fetched. Children, grandchildren, and related links are not proactively cached, leaving `twig tree` and `twig status` with incomplete data.

## Goals and Non-Goals

### Goals
- **G-1**: All display commands (`status`, `show`, `tree`, `workspace`) render cached data immediately with a cache-age indicator when data is stale
- **G-2**: Background ADO sync updates the display in-place via Spectre.Console Live regions on all display commands
- **G-3**: Dirty items display a visible indicator (e.g., `●`) and a brief summary of pending changes
- **G-4**: `--no-refresh` flag on all display commands skips the background sync pass
- **G-5**: `twig set` to an out-of-sprint item proactively fetches: parents to root, 2 levels of children, 1 level of related links
- **G-6**: All context-change scenarios (`set`, `new --set`, `flow start`) use a single `ContextChangeService` codepath
- **G-7**: Working set extension is additive — never removes existing cached items

### Non-Goals
- **NG-1**: Real-time push notifications from ADO (polling only)
- **NG-2**: Automatic sync on any command that doesn't display data (e.g., `twig update`, `twig note`)
- **NG-3**: Changing the `QueryCommand` to use cache-first rendering (it inherently fetches from ADO)
- **NG-4**: Adding sync to the TUI (`Twig.Tui`) — that's a separate concern
- **NG-5**: Changing the `--no-live` flag semantics — it stays as-is for backward compatibility

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | All display commands render cached data first, then sync in the background |
| FR-02 | Cache age is shown when `LastSyncedAt` exceeds `CacheStaleMinutes` threshold (default: **5 minutes**, configurable via `display.cachestaleminutes` in `TwigConfiguration.DisplayConfig`) |
| FR-03 | Cache age format: `(cached Xm ago)`, `(cached Xh ago)`, or `(cached Xd ago)` |
| FR-04 | Dirty items display a `●` indicator with a change summary |
| FR-05 | Dirty item summary format: `local: Title changed, State → Doing` |
| FR-06 | Dirty items show a tooltip: `(unsaved — run 'twig save' to push)` |
| FR-07 | `--no-refresh` flag on `status`, `show`, `tree`, `workspace` skips sync pass |
| FR-08 | `--no-refresh` is independent of `--no-live` — can use rich rendering without sync |
| FR-09 | `twig set` to out-of-sprint item fetches parents to root |
| FR-10 | `twig set` to out-of-sprint item fetches 2 levels of children |
| FR-11 | `twig set` to out-of-sprint item fetches 1 level of related links |
| FR-12 | Context change working set extension is additive (never evicts) |
| FR-13 | All context-change scenarios use `ContextChangeService` |


### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Cache-first render completes in < 100ms (no network I/O) |
| NFR-02 | Background sync is awaited within Spectre.Console Live regions (not fire-and-forget). The sync runs inside `RenderWithSyncAsync`'s Live context so the display can update on completion. Only `ContextChangeService.ExtendWorkingSetAsync` (post-display in `SetCommand`) is truly fire-and-forget with error swallowing. |
| NFR-03 | AOT compatible — no reflection, all types in `TwigJsonContext` |
| NFR-04 | Working set extension fetches are parallelized where possible |
| NFR-05 | `--no-refresh` commands work fully offline |

## Proposed Design

### Architecture Overview

The design introduces three new components and modifies the existing rendering pipeline:

```
┌─────────────────────────────────────────────────────────┐
│  CLI Commands (StatusCommand, TreeCommand, ShowCommand)  │
│                                                         │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ --no-refresh  │  │ Cache Age    │  │ Dirty         │  │
│  │ flag bypass   │  │ Formatter    │  │ Indicator     │  │
│  └──────┬───────┘  └──────┬───────┘  └──────┬────────┘  │
│         │                 │                 │            │
│  ┌──────▼─────────────────▼─────────────────▼────────┐  │
│  │          RenderWithSyncAsync (existing)             │  │
│  │  Pass 1: buildCachedView() — with age + dirty      │  │
│  │  Pass 2: performSync() → buildRevisedView()        │  │
│  └────────────────────────┬──────────────────────────┘  │
└───────────────────────────┼─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│  Domain Services                                         │
│                                                         │
│  ┌───────────────────┐  ┌────────────────────────────┐  │
│  │ SyncCoordinator    │  │ ContextChangeService (NEW) │  │
│  │ (existing)         │  │                            │  │
│  │ - SyncItemSetAsync │  │ - ExtendWorkingSetAsync()  │  │
│  │ - SyncWorkingSet   │  │   → fetch parents to root  │  │
│  │ - SyncChildrenAsync│  │   → fetch 2 levels children│  │
│  └───────────────────┘  │   → fetch 1 level links     │  │
│                          └────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. Cache Age Formatting (`CacheAgeFormatter`)

**Location:** `src/Twig.Domain/Services/CacheAgeFormatter.cs`  
**Responsibility:** Pure static utility that formats `LastSyncedAt` into a human-readable age string.

```
CacheAgeFormatter.Format(DateTimeOffset? lastSyncedAt, int staleMinutes)
  → null                     // when lastSyncedAt is null or within threshold
  → "(cached 3m ago)"        // when stale
  → "(cached 2h ago)"        // when > 60 minutes
  → "(cached 1d ago)"        // when > 24 hours
```

This is a pure function with no dependencies — easily unit tested. Commands and formatters call it when rendering work item headers. The `staleMinutes` parameter maps to `TwigConfiguration.Display.CacheStaleMinutes` (default: **5 minutes**).

#### 2. Dirty State Summary (`DirtyStateSummary`)

**Location:** `src/Twig.Domain/Services/DirtyStateSummary.cs`  
**Responsibility:** Builds a concise summary of pending changes for a dirty work item.

```
DirtyStateSummary.Build(IReadOnlyList<PendingChangeRecord> changes)
  → null                                          // when no changes
  → "local: Title changed"                        // single field change
  → "local: Title changed, State → Doing"         // field + state change
  → "local: 3 field changes, 1 note"              // many changes
```

Takes `PendingChangeRecord` list (already available from `IPendingChangeStore`) and produces a one-line summary. The `ChangeType` and `FieldName` fields on `PendingChangeRecord` provide the necessary detail.

#### 3. `ContextChangeService` (New Domain Service)

**Location:** `src/Twig.Domain/Services/ContextChangeService.cs`  
**Responsibility:** Single codepath for all context-change scenarios. Orchestrates working set extension when the target item is outside the current sprint.

```csharp
public sealed class ContextChangeService(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    SyncCoordinator syncCoordinator,
    ProtectedCacheWriter protectedCacheWriter,
    IWorkItemLinkRepository? linkRepo = null)
{
    /// Extends the working set around the given item.
    /// Additive only — never removes existing cached items.
    public async Task ExtendWorkingSetAsync(int itemId, CancellationToken ct = default)
    {
        // 1. Fetch parents to root (upstream chain)
        await HydrateParentChainAsync(itemId, ct);

        // 2. Fetch 2 levels of children (downstream graph)
        await HydrateChildrenAsync(itemId, depth: 2, ct);

        // 3. Fetch 1 level of related links (lateral)
        //    Silently skipped when linkRepo is null (e.g., link persistence not registered)
        if (linkRepo is not null)
            await HydrateRelatedLinksAsync(itemId, ct);
    }
}
```

**`IWorkItemLinkRepository?` nullable rationale:** Related link persistence is an optional infrastructure registration. When null, `HydrateRelatedLinksAsync` is silently skipped — parent chain and child graph hydration still proceed. This avoids a hard dependency on `IWorkItemLinkRepository` being registered, which simplifies testing and allows graceful degradation if link persistence is disabled.

**Design decision: Why a new service instead of extending `SyncCoordinator`?**
`SyncCoordinator` handles cache staleness and item sync. `ContextChangeService` handles working set expansion — a distinct concern. The context service *uses* `SyncCoordinator` and `IAdoWorkItemService` but adds the graph-traversal logic. Keeping them separate follows the existing pattern where each service has a focused responsibility.

#### 4. `--no-refresh` Opt-Out

`RenderingPipelineFactory` does **not** change. Commands own the `noRefresh` logic: when the flag is set, commands call the direct render method (`RenderStatusAsync`, `RenderTreeAsync`) instead of `RenderWithSyncAsync`. This keeps the renderer interface unchanged — the opt-out lives in commands where it belongs. This is distinct from `--no-live`, which disables the Spectre renderer entirely.

**Note on `ShowCommand` and `--no-live`:** `ShowCommand` does not currently have a `--no-live` parameter and will not gain one — it has no async rendering path. It only gains `--no-refresh` to control whether a background sync occurs after the initial cached display.

### Data Flow: Two-Pass Rendering (status command example)

```
User runs: twig status
  │
  ├─ 1. Resolve active item from cache (< 10ms)
  ├─ 2. Load pending changes from SQLite (< 5ms)
  ├─ 3. Format cache age: CacheAgeFormatter.Format(item.LastSyncedAt, config.Display.CacheStaleMinutes)
  ├─ 4. Format dirty summary: DirtyStateSummary.Build(pendingChanges)
  ├─ 5. Build cached view IRenderable (with age + dirty indicators)
  │
  ├─ [PASS 1] Display cached view immediately via Live region
  │     "► #1234 Fix login bug  (cached 15m ago)  ● local: State → Doing"
  │     "⟳ syncing..."
  │
  ├─ 6. SyncCoordinator.SyncWorkingSetAsync() — awaited inside Live region
  │
  ├─ [PASS 2] On sync complete (buildRevisedView callback):
  │     ├─ If UpToDate: flash "✓ up to date", clear → return rebuilt status view (same data)
  │     ├─ If Updated: rebuild view with fresh data, flash "✓ 3 items updated" → return rebuilt status view
  │     └─ If Failed: show "⚠ sync failed (offline)" → return null (keep cached view)
  │
  └─ 7. Exit
```

**Note:** The `buildRevisedView` callback (currently returning `null` in `StatusCommand` at line 148) will be changed to rebuild the full status `IRenderable` from fresh cache data after sync. This is what makes Pass 2 visually update the display. Returning `null` on sync failure preserves the Pass 1 cached view.

### Data Flow: Context Change Working Set Extension

```
User runs: twig set 1234  (item #1234 is NOT in current sprint)
  │
  ├─ 1. ActiveItemResolver.ResolveByIdAsync(1234)
  │     └─ Cache miss → fetch from ADO → save to cache
  │
  ├─ 2. Set active context: contextStore.SetActiveWorkItemIdAsync(1234)
  ├─ 3. Record navigation history
  ├─ 4. Display item (same as twig status)
  │
  ├─ 5. ContextChangeService.ExtendWorkingSetAsync(1234)
  │     ├─ 5a. HydrateParentChainAsync: fetch #1234's parent, grandparent, ... to root
  │     ├─ 5b. HydrateChildrenAsync(depth=2):
  │     │     ├─ Fetch children of #1234 (level 1)
  │     │     └─ Fetch children of each child (level 2)
  │     └─ 5c. HydrateRelatedLinksAsync:
  │           └─ Fetch related links for #1234 (Related, Tested By, etc.)
  │
  └─ 6. Working set eviction (existing, uses expanded set)
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | `--no-refresh` is a new flag, independent of `--no-live` | `--no-live` disables Spectre rendering entirely (for piped output). `--no-refresh` keeps rich rendering but skips network I/O. Different use cases. |
| DD-02 | Cache age displayed only when exceeds threshold | Showing "cached 0s ago" on every item adds noise. Only show when data might actually be stale. |
| DD-03 | Dirty indicator uses `●` character | Consistent with git/VS Code conventions. Visible in all terminals. |
| DD-04 | `ContextChangeService` is a new service, not an extension of `SyncCoordinator` | Separation of concerns: sync = cache freshness, context change = graph expansion. |
| DD-05 | Children fetched to depth 2 (not configurable initially) | Matches the Epic→Issue→Task hierarchy depth. Configurable depth is a future enhancement. |
| DD-06 | Related links limited to depth 1 | Prevents exponential graph expansion. Related items are informational, not structural. |
| DD-07 | `ShowCommand` gets optional sync (opt-in via absence of `--no-refresh`) | `show` is documented as cache-only read. Adding sync changes semantics — but with `--no-refresh` default-off, the new behavior is the default and users can opt out. `ShowCommand` does **not** get a `--no-live` flag — it currently has no async rendering path (no `RenderWithSyncAsync` usage), so `--no-live` is irrelevant. It will gain `--no-refresh` only. |
| DD-08 | Working set extension is fire-and-forget with error swallowing | Same pattern as existing post-render syncs. Extension failures must never fail the command. |
| DD-09 | `StatusCommand` revised view callback returns a rebuilt status view after sync | Currently returns `null` (placeholder). This plan changes it to rebuild the status `IRenderable` from fresh data after sync completes, so the Live region displays updated state. This is the core of the two-pass pattern. |

## Alternatives Considered

### Alternative 1: Extend `RenderWithSyncAsync` to Accept Cache-Age and Dirty Metadata

**Approach:** Pass cache-age and dirty state as parameters to `RenderWithSyncAsync` so the renderer handles all indicators internally.

**Pros:** Centralizes indicator rendering logic.  
**Cons:** `RenderWithSyncAsync` is a generic primitive that doesn't know about work items. Adding work-item-specific metadata couples it to the domain. The current design keeps it generic.

**Decision:** Rejected. Cache-age and dirty indicators are built into the `buildCachedView` callback, keeping `RenderWithSyncAsync` generic.

### Alternative 2: Make `--no-refresh` the Default, Require `--refresh` to Sync

**Approach:** Inverse the opt-in/opt-out direction.

**Pros:** Faster default experience, fully offline by default.  
**Cons:** Users would see stale data by default with no auto-correction. The whole point of the feature is that live refresh is the default — users shouldn't have to remember to sync.

**Decision:** Rejected. Sync-by-default matches user expectations.

### Alternative 3: Extend `WorkingSetService` Instead of Creating `ContextChangeService`

**Approach:** Add `ExtendForContextChange()` to `WorkingSetService`.

**Pros:** Fewer services to manage.  
**Cons:** `WorkingSetService` computes a working set from cache state (pure query). Context change extension *mutates* the cache (fetches and saves items). Mixing query and mutation in one service violates the existing separation.

**Decision:** Rejected. Separate service maintains the query/mutation boundary.

## Dependencies

### External Dependencies
- **Spectre.Console** (existing) — `Live` regions for in-place updates
- **SQLite** (existing) — cache persistence
- **ADO REST API** (existing) — work item fetch, link fetch

### Internal Dependencies
- `SyncCoordinator` — existing sync primitives
- `ProtectedCacheWriter` — existing dirty-item-safe cache writes
- `IAdoWorkItemService` — existing ADO API abstraction
- `RenderingPipelineFactory` — existing rendering router

### Sequencing Constraints
- Issue #1520 (two-pass rendering) must be implemented before Issue #1521 (context change extension) because `ContextChangeService` will benefit from the two-pass rendering pattern for displaying fetch progress.

## Impact Analysis

### Components Affected
| Component | Type of Change | Backward Compatible? |
|-----------|---------------|---------------------|
| `StatusCommand` | Modified — add `--no-refresh`, cache-age, dirty display | ✅ Yes (additive) |
| `TreeCommand` | Modified — integrate sync into tree render, add cache-age | ✅ Yes (additive) |
| `ShowCommand` | Modified — add optional sync, cache-age, dirty display | ✅ Yes (was cache-only, now syncs by default) |
| `WorkspaceCommand` | Modified — add cache-age to sprint items | ✅ Yes (additive) |
| `SetCommand` | Modified — add `ContextChangeService` call | ✅ Yes (additive, post-display) |
| `NewCommand` | Modified — add `ContextChangeService` when `--set` | ✅ Yes (additive) |
| `FlowStartCommand` | Modified — add `ContextChangeService` call | ✅ Yes (additive) |
| `RenderingPipelineFactory` | No change needed — `noRefresh` handled at command level | ✅ N/A |
| `SpectreRenderer` | Modified — cache-age in headers, dirty indicators | ✅ Yes (visual only) |
| `HumanOutputFormatter` | Modified — cache-age, dirty display in text output | ✅ Yes (additive) |
| `JsonOutputFormatter` | No change | ✅ N/A |
| `Program.cs` (`TwigCommands`) | Modified — pass `--no-refresh` flag | ✅ Yes (new optional param) |

### Performance Implications
- **Pass 1 (cached):** No regression — pure SQLite reads (< 100ms)
- **Pass 2 (sync):** Same as current sync behavior — no new overhead
- **Context change extension:** Adds 3-5 ADO API calls on `twig set` for out-of-sprint items. These run post-display and are fire-and-forget, so command responsiveness is unchanged.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Live region flicker on rapid updates | Medium | Low | Existing `SyncStatusDelay` (800ms) provides debounce; test with slow networks |
| Context change extension creates large cache for deeply nested items | Low | Medium | Depth limits (2 children, 1 link) bound the expansion; eviction still runs |
| `--no-refresh` flag name conflicts with future flags | Low | Low | Prefix is specific; `--cached` alias could be added later |
| `ShowCommand` adding sync changes its documented "cache-only" contract | Medium | Medium | Document the change; `--no-refresh` preserves old behavior |

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| Q-1 | Should `ShowCommand` sync by default, or should it remain cache-only with opt-in `--refresh`? The current doc says "cache-only, no sync." Adding sync changes semantics. | Low | **Resolved** — DD-07 decides: sync by default, `--no-refresh` preserves old cache-only behavior. New behavior matches user expectations and is consistent with other display commands. |
| Q-2 | Should cache-age display include the actual timestamp (e.g., `cached at 2:15 PM`) or just relative time? | Low | Recommend: relative time only — simpler, timezone-agnostic |
| Q-3 | Should `NavigationCommands` (up/down/root) trigger working set extension? | Low | **Resolved: No** — these navigate within existing cache. Extension only for `set`/`new --set`/`flow start` |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/CacheAgeFormatter.cs` | Pure static utility to format `LastSyncedAt` into human-readable cache age strings |
| `src/Twig.Domain/Services/DirtyStateSummary.cs` | Pure static utility to build concise dirty-change summaries from `PendingChangeRecord` lists |
| `src/Twig.Domain/Services/ContextChangeService.cs` | Domain service for working set extension on context changes — single codepath for `set`/`new --set`/`flow start` |
| `tests/Twig.Domain.Tests/Services/CacheAgeFormatterTests.cs` | Unit tests for cache age formatting logic |
| `tests/Twig.Domain.Tests/Services/DirtyStateSummaryTests.cs` | Unit tests for dirty state summary building |
| `tests/Twig.Domain.Tests/Services/ContextChangeServiceTests.cs` | Unit tests for context change working set extension |
| `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | Integration tests for two-pass status rendering |
| `tests/Twig.Cli.Tests/Commands/TreeCommand_CacheAwareTests.cs` | Integration tests for two-pass tree rendering |
| `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | Integration tests for ShowCommand sync behavior and `--no-refresh` flag |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommand_CacheAwareTests.cs` | Integration tests for WorkspaceCommand cache-age display |
| `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` | Integration tests for context change working set extension |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommand_ContextChangeTests.cs` | Integration tests for FlowStartCommand context change extension |
| `tests/Twig.Cli.Tests/Commands/NewCommand_ContextChangeTests.cs` | Integration tests for NewCommand `--set` context change extension |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/StatusCommand.cs` | Add `noRefresh` parameter, integrate cache-age and dirty indicators into cached view, change `buildRevisedView` callback (line 148) from returning `null` to rebuilding status view from fresh data |
| `src/Twig/Commands/TreeCommand.cs` | Add `noRefresh` parameter, integrate sync into tree render with revised view |
| `src/Twig/Commands/ShowCommand.cs` | Add `noRefresh` parameter, add optional two-pass sync with `RenderWithSyncAsync` |
| `src/Twig/Commands/WorkspaceCommand.cs` | Add `noRefresh` parameter, display cache-age on sprint items |
| `src/Twig/Commands/SetCommand.cs` | Add `ContextChangeService.ExtendWorkingSetAsync()` call after context set |
| `src/Twig/Commands/NewCommand.cs` | Add `ContextChangeService.ExtendWorkingSetAsync()` when `--set` is used |
| `src/Twig/Commands/FlowStartCommand.cs` | Add `ContextChangeService.ExtendWorkingSetAsync()` after context set (line 161) |
| `src/Twig/Rendering/SpectreRenderer.cs` | Add cache-age display in status/tree view builders, add dirty indicator rendering |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add cache-age suffix and dirty indicator to `FormatWorkItem` output |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | No change — JSON consumers can call ADO directly for live data |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `ContextChangeService` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Wire `ContextChangeService` into `SetCommand`, `NewCommand`, `FlowStartCommand` |
| `src/Twig/Program.cs` | Add `--no-refresh` parameter to `Status`, `Tree`, `Show`, `Workspace` commands |

## ADO Work Item Structure

**Epic:** #1519 — Cache-Aware Rendering & Live Refresh

### Issue #1520: Two-pass cache-then-refresh rendering for all display commands

**Goal:** All display commands render cached data first with cache-age and dirty indicators, then sync in the background with Live region updates. `--no-refresh` flag provides opt-out.

**Prerequisites:** None — this is the foundation.

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|----------------|
| T-1520-1 | **Create `CacheAgeFormatter` utility** — Pure static class with `Format(DateTimeOffset? lastSyncedAt, int staleMinutes)` → `string?`. Handles null, within-threshold (returns null), minutes, hours, days formatting. Add comprehensive unit tests. | `src/Twig.Domain/Services/CacheAgeFormatter.cs`, `tests/Twig.Domain.Tests/Services/CacheAgeFormatterTests.cs` | S (~100 LoC) |
| T-1520-2 | **Create `DirtyStateSummary` utility** — Pure static class with `Build(IReadOnlyList<PendingChangeRecord> changes)` → `string?`. Handles empty list (null), single field change, state change (shows old→new), note count, and mixed changes with truncation. Add comprehensive unit tests. | `src/Twig.Domain/Services/DirtyStateSummary.cs`, `tests/Twig.Domain.Tests/Services/DirtyStateSummaryTests.cs` | S (~120 LoC) |
| T-1520-3 | **Add cache-age and dirty indicators to `SpectreRenderer` status view** — Modify `BuildStatusViewAsync` to include cache-age suffix in the item header row and a dirty indicator row when `IsDirty` is true. Use `CacheAgeFormatter` and `DirtyStateSummary`. Pass `CacheStaleMinutes` config value through to the renderer. | `src/Twig/Rendering/SpectreRenderer.cs` | M (~150 LoC) |
| T-1520-4 | **Add cache-age and dirty to `HumanOutputFormatter`** — Modify `FormatWorkItem` and `FormatStatusSummary` to include cache-age suffix and dirty indicator. This covers the `--no-live` / piped-output path. | `src/Twig/Formatters/HumanOutputFormatter.cs` | S (~80 LoC) |
| T-1520-5 | **Add `--no-refresh` flag to `StatusCommand`** — Add `noRefresh` parameter. When true, skip `RenderWithSyncAsync` and use direct `RenderStatusAsync`. Change the `buildRevisedView` callback (currently returning `null` at line 148) to rebuild the full status `IRenderable` from fresh cache data after sync completes. Wire through `Program.cs`. Add tests verifying: sync is skipped with `--no-refresh`, revised view rebuilds on sync success, `null` returned on sync failure. | `src/Twig/Commands/StatusCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | M (~150 LoC) |
| T-1520-6 | **Integrate two-pass sync into `TreeCommand`** — Replace the degenerate empty-cached-view `RenderWithSyncAsync` with a proper pattern: build tree view as cached view, sync working set, then rebuild tree as revised view. Add `noRefresh` parameter. Wire through `Program.cs`. | `src/Twig/Commands/TreeCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/TreeCommand_CacheAwareTests.cs` | L (~250 LoC) |
| T-1520-7 | **Add two-pass sync to `ShowCommand`** — Add `RenderWithSyncAsync` usage: render cached item immediately, sync item by ID, revise display. Add `noRefresh` parameter (but **not** `--no-live` — `ShowCommand` has no async rendering path). Show is currently cache-only; this adds network I/O (guarded by `--no-refresh`). Wire through `Program.cs`. Add tests verifying sync behavior and `--no-refresh` bypass. | `src/Twig/Commands/ShowCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | M (~120 LoC) |
| T-1520-8 | **Add cache-age to `WorkspaceCommand` sprint items** — Add cache-age suffix to stale sprint items in workspace table. Add `noRefresh` flag. Wire through `Program.cs`. Add tests verifying cache-age display on stale items. | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/WorkspaceCommand_CacheAwareTests.cs` | S (~80 LoC) |

**Acceptance Criteria:**
- [ ] All display commands render cached data first, then refresh
- [ ] Cache age is shown when data exceeds `CacheStaleMinutes`
- [ ] Dirty items are visibly marked with `●` and change summary
- [ ] `--no-refresh` flag skips the live sync pass on all display commands
- [ ] All new code has unit tests with ≥ 90% branch coverage

---

### Issue #1521: Context change auto-extends working set with parent chain and downstream graph

**Goal:** When `twig set` (or equivalent) changes context to an out-of-sprint item, automatically extend the local cache with the work graph around that item: parents to root, 2 levels of children, 1 level of related links.

**Prerequisites:** Issue #1520 (display commands should be able to render the newly-fetched items)

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|----------------|
| T-1521-1 | **Create `ContextChangeService`** — Domain service with `ExtendWorkingSetAsync(int itemId, CancellationToken)`. Implements parent chain hydration (iterative fetch up to root), 2-level child fetch (parallel `SyncChildrenAsync` calls), and 1-level related link fetch (`SyncLinksAsync`, silently skipped if `IWorkItemLinkRepository` is null). All fetches are additive (save to cache, never evict). All errors are swallowed (fire-and-forget pattern). Add comprehensive unit tests with mocked `IAdoWorkItemService`. | `src/Twig.Domain/Services/ContextChangeService.cs`, `tests/Twig.Domain.Tests/Services/ContextChangeServiceTests.cs` | L (~300 LoC) |
| T-1521-2 | **Register `ContextChangeService` in DI** — Add factory registration in `CommandServiceModule`. Wire dependencies: `IWorkItemRepository`, `IAdoWorkItemService`, `SyncCoordinator`, `ProtectedCacheWriter`, `IWorkItemLinkRepository` (optional/nullable). | `src/Twig/DependencyInjection/CommandServiceModule.cs` | XS (~20 LoC) |
| T-1521-3 | **Integrate into `SetCommand`** — After setting active context, call `ContextChangeService.ExtendWorkingSetAsync(item.Id)`. Run as fire-and-forget after display output. Ensure eviction uses the expanded working set. | `src/Twig/Commands/SetCommand.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | M (~60 LoC) |
| T-1521-4 | **Integrate into `NewCommand` and `FlowStartCommand`** — When `--set` flag is used in `NewCommand`, call `ContextChangeService.ExtendWorkingSetAsync()`. Similarly for `FlowStartCommand` after context is set (line 161). Add integration tests for both commands verifying context change triggers working set extension. | `src/Twig/Commands/NewCommand.cs`, `src/Twig/Commands/FlowStartCommand.cs`, `tests/Twig.Cli.Tests/Commands/NewCommand_ContextChangeTests.cs`, `tests/Twig.Cli.Tests/Commands/FlowStartCommand_ContextChangeTests.cs` | S (~80 LoC) |
| T-1521-5 | **Add integration tests for context change extension** — Test scenarios: (1) set to out-of-sprint item → parents/children/links fetched, (2) set to in-sprint item → minimal additional fetches, (3) network failure during extension → command still succeeds, (4) additive guarantee → existing cache items not removed. | `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` | M (~200 LoC) |

**Acceptance Criteria:**
- [ ] `twig set` to an out-of-sprint item fetches parents to root
- [ ] `twig set` fetches 2 levels of children
- [ ] `twig set` fetches 1 level of related links
- [ ] Context change logic is in a single shared `ContextChangeService` codepath
- [ ] Working set extension is additive (never removes existing items)
- [ ] All context change points (`set`, `new --set`, `flow start`) use `ContextChangeService`
- [ ] Extension failures never cause the command to fail

## PR Groups

### PR Group 1: Two-Pass Rendering for Status and Tree Commands
**Tasks:** T-1520-1, T-1520-2, T-1520-3, T-1520-4, T-1520-5, T-1520-6  
**Classification:** Deep — new domain utilities plus rendering pipeline and two key commands  
**Estimated LoC:** ~850  
**Files:** ~14 (source + test)  
**Description:** Pure domain utilities for cache-age formatting and dirty-state summarization, followed by integration into `SpectreRenderer` and `HumanOutputFormatter`. Adds `--no-refresh` to `StatusCommand` and `TreeCommand`. Fixes `TreeCommand`'s degenerate `RenderWithSyncAsync` usage. The formatting utilities have no callers until the renderer changes ship — batching into one PR avoids a useless intermediate state.  
**Successors:** PR Group 2

### PR Group 2: Two-Pass Rendering for Show and Workspace
**Tasks:** T-1520-7, T-1520-8  
**Classification:** Wide — mechanical application of the same pattern to remaining commands  
**Estimated LoC:** ~250  
**Files:** ~8 (source + test)  
**Description:** Extends the two-pass pattern to `ShowCommand` (adds `--no-refresh` only, not `--no-live`) and `WorkspaceCommand`. Includes integration tests for both. All patterns established in PR Group 1.  
**Successors:** None — can ship in parallel with PR Group 3

### PR Group 3: Context Change Working Set Extension
**Tasks:** T-1521-1, T-1521-2, T-1521-3, T-1521-4, T-1521-5  
**Classification:** Deep — new domain service with graph traversal logic  
**Estimated LoC:** ~660  
**Files:** ~12 (source + test, including FlowStartCommand and NewCommand context-change tests)  
**Description:** Introduces `ContextChangeService` (with nullable `IWorkItemLinkRepository` for graceful degradation), registers it in DI, and integrates into `SetCommand`, `NewCommand`, and `FlowStartCommand`. Comprehensive tests for parent/child/link hydration across all three integration points.  
**Successors:** None — can ship in parallel with PR Group 2

### PR Group Execution Order

```
PR Group 1 ──► PR Group 2
          │
          └──► PR Group 3
```

PR Groups 2 and 3 are independent after PR Group 1 and can be reviewed/merged in parallel.

## References

- Existing `RenderWithSyncAsync` implementation: `src/Twig/Rendering/SpectreRenderer.cs:969-1048`
- Spectre.Console Live API: https://spectreconsole.net/live/live-display
- ADO REST API — Get Work Item: `_apis/wit/workitems/{id}?$expand=relations`
- Existing `SyncCoordinator` design: `src/Twig.Domain/Services/SyncCoordinator.cs`
- Set Command sync optimization plan: `docs/projects/set-command-sync-optimization.plan.md`

