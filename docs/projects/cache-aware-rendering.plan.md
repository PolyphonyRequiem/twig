# Cache-Aware Rendering & Live Refresh

**Epic:** #1519  
> **Status**: 🔨 In Progress  
**Revision:** 4 — Addressed tech (88) and readability (92) review feedback: (1) Added Issue #1522 for MCP `ContextChangeService` integration gap; (2) Updated Background narrative from pre-implementation baseline to post-implementation current state; (3) Added `ContextTools.cs` to `SetActiveWorkItemIdAsync` call-site audit; (4) Fixed `~varies` line numbers to exact values; (5) Added `--no-refresh`/`--no-live` forward-reference in Executive Summary; (6) Clarified context-change data flow step 4; (7) Moved Completion section to appendix position after References.

| Rev | Changes |
|-----|---------|
| 1 | Initial draft |
| 2 | Expanded design, added call-site audits |
| 3 | Corrected line-number references post-implementation, added Open Questions, consolidated `--no-refresh`/`--no-live` into DD-01 |
| 4 | MCP integration gap (Issue #1522), narrative updated to post-implementation state, exact call-site line numbers |

---

## Executive Summary

This design implements a **two-pass rendering pattern** across all twig display commands (`status`, `show`, `tree`, `workspace`) and a **context-change working set extension** for `twig set`. The solution introduces: (1) immediate cache-first rendering with cache-age indicators, (2) background ADO sync with Spectre.Console Live region updates, (3) prominent dirty-item indicators, (4) a `--no-refresh` opt-out flag (orthogonal to `--no-live` — see [DD-01](#design-decisions) for the distinction), and (5) automatic working set expansion when `twig set` targets an out-of-sprint item via `ContextChangeService`. Issues #1520 (two-pass rendering) and #1521 (context change extension) are delivered. **Remaining gap:** the MCP server (`twig-mcp`) calls `SetActiveWorkItemIdAsync` in `ContextTools.Set()` but does not invoke `ContextChangeService`, leaving MCP consumers with the same orphaned-context problem that CLI consumers had before this epic. Issue #1522 addresses this gap.

## Background

### Current Architecture

Twig uses a layered architecture:
- **Domain layer** (`Twig.Domain`): Aggregates (`WorkItem`), services (`SyncCoordinator`, `WorkingSetService`, `ActiveItemResolver`, `ContextChangeService`), interfaces (`IWorkItemRepository`, `IAdoWorkItemService`)
- **Infrastructure layer** (`Twig.Infrastructure`): SQLite persistence (`SqliteWorkItemRepository`), ADO REST client, configuration
- **CLI layer** (`Twig`): Commands, rendering (`SpectreRenderer`), formatters (`HumanOutputFormatter`, `JsonOutputFormatter`)
- **MCP layer** (`Twig.Mcp`): MCP server exposing `ContextTools`, `ReadTools`, `MutationTools` — shares domain services but has its own DI registrations

### Two-Pass Rendering (Implemented — Issue #1520)

All display commands now implement the two-pass cache-then-refresh pattern:
- `IAsyncRenderer.RenderWithSyncAsync()` is the cache-render-fetch-revise primitive
- `SpectreRenderer.RenderWithSyncAsync()` uses `Spectre.Console.Live` regions with sync status indicators (`⟳ syncing...`, `✓ up to date`, `⚠ sync failed`)
- `StatusCommand` (line 147): full two-pass — builds status view via `BuildStatusViewAsync`, syncs working set, rebuilds on completion
- `TreeCommand` (line 129): full two-pass — builds tree view via `BuildTreeViewAsync` as cached view, syncs working set, rebuilds from fresh data
- `ShowCommand` (line 123): full two-pass — builds status view from cache, syncs item by ID, rebuilds; `--no-refresh` opt-out available
- `WorkspaceCommand`: cache-age display on sprint items, `--no-refresh` flag
- `CacheAgeFormatter` and `DirtyStateSummary` utilities provide cache-age formatting and dirty-state summary rendering

### Context Change Extension (Implemented — Issue #1521)

`ContextChangeService` is registered in CLI DI (`CommandServiceModule`) and integrated into:
- `SetCommand` (line 229): calls `ExtendWorkingSetAsync` after setting context
- `NewCommand` (line 147): calls `ExtendWorkingSetAsync` when `--set` flag is used
- `FlowStartCommand` (line 167): calls `ExtendWorkingSetAsync` after context set

### Remaining Gap: MCP Server

`ContextChangeService` is **not registered** in MCP DI (`Twig.Mcp/Program.cs`) and **not called** from `ContextTools.Set()`. The MCP `twig.set` tool (line 74) calls `contextStore.SetActiveWorkItemIdAsync(item.Id, ct)` and performs inline parent chain hydration (lines 61–72) followed by a `SyncItemSetAsync` (line 79), but does not invoke `ContextChangeService.ExtendWorkingSetAsync()`. This means MCP consumers experience the same orphaned-context problem described in Problem Statement #5 — children and related links are not proactively cached.

### Key Existing Components

| Component | Location | Relevance |
|-----------|----------|-----------|
| `WorkItem.LastSyncedAt` | `Aggregates/WorkItem.cs:53` | Cache staleness timestamp (already tracked) |
| `WorkItem.IsDirty` | `Aggregates/WorkItem.cs:41` | Dirty flag (already tracked) |
| `PendingChangeRecord` | `Common/PendingChangeRecord.cs` | Change details (type, field, old/new values) |
| `IPendingChangeStore.GetChangesAsync()` | `Interfaces/IPendingChangeStore.cs` | Retrieves pending changes per item |
| `RenderWithSyncAsync()` | `Rendering/SpectreRenderer.cs:1036` | Existing Live region two-pass primitive |
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

| File | Method | Current Rendering | Has Sync? | Has `--no-live`? | Has `--no-refresh`? | Status |
|------|--------|-------------------|-----------|------------------|---------------------|--------|
| `Commands/StatusCommand.cs` | `ExecuteCoreAsync` | `RenderWithSyncAsync` (L147) + `BuildStatusViewAsync` | ✅ Working set sync | ✅ `noLive` param | ✅ `noRefresh` param | ✅ Done |
| `Commands/TreeCommand.cs` | `ExecuteCoreAsync` | `RenderWithSyncAsync` (L129) + `BuildTreeViewAsync` | ✅ Working set sync | ✅ `noLive` param | ✅ `noRefresh` param | ✅ Done |
| `Commands/ShowCommand.cs` | `ExecuteCoreAsync` | `RenderWithSyncAsync` (L123) + `BuildStatusViewAsync` | ✅ Item sync | ❌ N/A (DD-07) | ✅ `noRefresh` param | ✅ Done |
| `Commands/QueryCommand.cs` | `ExecuteCoreAsync` | `FormatQueryResults` (formatter only) | ❌ Always fetches from ADO | ❌ No flag | ❌ No flag | N/A (NG-03) |
| `Commands/WorkspaceCommand.cs` | `ExecuteCoreAsync` | `RenderWorkspaceAsync` (streaming) | ✅ Via streaming chunks | ✅ `noLive` param | ✅ `noRefresh` param | ✅ Done |
| `Commands/SetCommand.cs` | `ExecuteCoreAsync` | `RenderStatusAsync` (post-set display) | ✅ Item + parent chain sync | ❌ No flag | ❌ No flag | ✅ ContextChangeService integrated (L229) |
| `Commands/NewCommand.cs` | `ExecuteAsync` | `FormatSuccess` (simple output) | N/A (creates in ADO) | ❌ No flag | ❌ No flag | ✅ ContextChangeService integrated |
| `Twig.Mcp/Tools/ContextTools.cs` | `Set` | `McpResultBuilder.FormatWorkItem` (text) | ✅ Item + parent chain sync (L79) | N/A (MCP) | N/A (MCP) | ❌ **Missing ContextChangeService** |

### Call-Site Audit: `RenderWithSyncAsync` Callers

| File | Line | Usage | Notes |
|------|------|-------|-------|
| `Commands/StatusCommand.cs` | 147 | Full two-pass: builds status view → syncs working set → rebuilds from fresh data | `--no-refresh` bypasses to static render |
| `Commands/TreeCommand.cs` | 129 | Full two-pass: builds tree view via `BuildTreeViewAsync` → syncs working set → rebuilds tree | `--no-refresh` bypasses to direct tree render |
| `Commands/ShowCommand.cs` | 123 | Full two-pass: builds status view → syncs item by ID → rebuilds from fresh data | `--no-refresh` bypasses to static render |

### Call-Site Audit: `contextStore.SetActiveWorkItemIdAsync` (Context Change Points)

| File | Line | Scenario | Should Trigger Working Set Extension? | Currently Calls ContextChangeService? |
|------|------|----------|--------------------------------------|--------------------------------------|
| `Commands/SetCommand.cs` | 147 | `twig set <id>` | ✅ Yes — primary use case | ✅ Yes (L229) |
| `Commands/NewCommand.cs` | 143 | `twig new --set` | ✅ Yes — just created, needs graph | ✅ Yes |
| `Commands/FlowStartCommand.cs` | 162 | `twig flow start` | ✅ Yes — starting work on an item | ✅ Yes |
| **`Twig.Mcp/Tools/ContextTools.cs`** | **74** | **`twig.set` via MCP** | **✅ Yes — same rationale as CLI `set`** | **❌ No — gap** |
| `Commands/HookHandlerCommand.cs` | 64 | git post-checkout hook | ❌ No — implicit, should be lightweight | ❌ No (intentional) |
| `Commands/NavigationCommands.cs` | 64 | `twig up/down/root` | ❌ No — target is already cached; no graph expansion needed | ❌ No (intentional) |
| `Commands/NavigationHistoryCommands.cs` | 41 | `twig back` | ❌ No — navigates to previously-visited item already in cache; recording a duplicate history entry is intentionally bypassed (DD-04 in nav-history design) | ❌ No (intentional) |
| `Commands/NavigationHistoryCommands.cs` | 71 | `twig fore` | ❌ No — same rationale as `back`; restores forward-stack context | ❌ No (intentional) |
| `Commands/NavigationHistoryCommands.cs` | 177 | `twig history` (interactive picker) | ❌ No — picks from previously-visited items already in cache | ❌ No (intentional) |
| `Commands/SeedPublishCommand.cs` | 43, 60 | `twig seed publish` (active seed remapped) | ❌ No — context update is a side-effect of ID remapping (seed→ADO); the item was just published so it's already fresh. Extension would be redundant. | ❌ No (intentional) |
| `Commands/StashCommand.cs` | 122 | `twig stash pop` | ❌ No — restoring a previously-cached context | ❌ No (intentional) |

## Problem Statement

1. **Stale data confusion**: Users see outdated work item states with no indication that the data is from cache. The `LastSyncedAt` field exists on every `WorkItem` but is never displayed.

2. **Hidden dirty state**: Items with uncommitted local changes (`IsDirty=true`, pending `FieldChange` records) are rendered identically to clean items. Users don't know which items have unsaved modifications.

3. **Inconsistent sync behavior**: `StatusCommand` does a proper two-pass render-then-sync, `TreeCommand` does a half-hearted post-render sync with an empty cached view, `ShowCommand` never syncs, and `QueryCommand` always fetches live but doesn't indicate this.

4. **No opt-out for scripting**: There's no way to skip the background sync for offline or scripted use cases. `--no-live` disables the entire Spectre renderer, not just the sync.

5. **Orphaned context after `twig set`**: When setting context to an out-of-sprint item, only the item itself (and parent chain) are fetched. Children, grandchildren, and related links are not proactively cached, leaving `twig tree` and `twig status` with incomplete data. *(Resolved in CLI by Issue #1521 — `ContextChangeService` now extends the working set.)*

6. **MCP `twig.set` shares the orphaned-context problem**: `ContextTools.Set()` in `Twig.Mcp` calls `SetActiveWorkItemIdAsync` (line 74) and performs inline parent chain hydration, but does not invoke `ContextChangeService`. MCP consumers (Copilot agents) calling `twig.set` get the same incomplete cache that CLI users had before Issue #1521 — children and related links are not proactively fetched. The existing inline parent chain logic in `ContextTools.Set()` (lines 61–72) duplicates functionality that `ContextChangeService.HydrateParentChainAsync` already provides.

## Goals and Non-Goals

### Goals
- **G-1**: All display commands (`status`, `show`, `tree`, `workspace`) render cached data immediately with a cache-age indicator when data is stale
- **G-2**: Background ADO sync updates the display in-place via Spectre.Console Live regions on all display commands
- **G-3**: Dirty items display a visible indicator (e.g., `●`) and a brief summary of pending changes
- **G-4**: `--no-refresh` flag on all display commands skips the background sync pass
- **G-5**: `twig set` to an out-of-sprint item proactively fetches: parents to root, 2 levels of children, 1 level of related links
- **G-6**: All context-change scenarios (`set`, `new --set`, `flow start`) use a single `ContextChangeService` codepath
- **G-7**: Working set extension is additive — never removes existing cached items
- **G-8**: MCP `twig.set` uses `ContextChangeService` for working set extension, achieving parity with CLI `twig set`

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
| FR-06 | Dirty items show a trailing hint: `(unsaved — run 'twig save' to push)` |
| FR-07 | `--no-refresh` flag on `status`, `show`, `tree`, `workspace` skips sync pass |
| FR-08 | `--no-refresh` is independent of `--no-live` — can use rich rendering without sync |
| FR-09 | `twig set` to out-of-sprint item fetches parents to root |
| FR-10 | `twig set` to out-of-sprint item fetches 2 levels of children |
| FR-11 | `twig set` to out-of-sprint item fetches 1 level of related links |
| FR-12 | Context change working set extension is additive (never evicts) |
| FR-13 | All context-change scenarios use `ContextChangeService` |
| FR-14 | MCP `twig.set` invokes `ContextChangeService.ExtendWorkingSetAsync()` after setting context |
| FR-15 | MCP `twig.set` replaces inline parent chain hydration with `ContextChangeService` (eliminates code duplication) |


### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Cache-first render completes in < 100ms (no network I/O) |
| NFR-02 | Background sync is awaited within Spectre.Console Live regions (not fire-and-forget). See DD-08 for the sole fire-and-forget exception. |
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
│  │ SyncCoordinator    │  │ ContextChangeService       │  │
│  │ (existing)         │  │                            │  │
│  │ - SyncItemSetAsync │  │ - ExtendWorkingSetAsync()  │  │
│  │ - SyncWorkingSet   │  │   → fetch parents to root  │  │
│  │ - SyncChildrenAsync│  │   → fetch 2 levels children│  │
│  └───────────────────┘  │   → fetch 1 level links     │  │
│                          └──────┬─────────────────────┘  │
└─────────────────────────────────┼────────────────────────┘
                                  │
                    ┌─────────────▼──────────────┐
                    │  Consumers of               │
                    │  ContextChangeService       │
                    │                             │
                    │  CLI:                       │
                    │  ✅ SetCommand (L229)       │
                    │  ✅ NewCommand              │
                    │  ✅ FlowStartCommand        │
                    │                             │
                    │  MCP:                       │
                    │  ❌ ContextTools.Set (gap)  │
                    └─────────────────────────────┘
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

**Note:** `ShowCommand` gains `--no-refresh` only — see DD-07 for rationale.

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

**Note:** The `buildRevisedView` callback (L159-181 in `StatusCommand`) rebuilds the full status `IRenderable` from fresh cache data after sync. This is what makes Pass 2 visually update the display. Returning `null` on sync failure preserves the Pass 1 cached view.

### Data Flow: Context Change Working Set Extension

```
User runs: twig set 1234  (item #1234 is NOT in current sprint)
  │
  ├─ 1. ActiveItemResolver.ResolveByIdAsync(1234)
  │     └─ Cache miss → fetch from ADO → save to cache
  │
  ├─ 2. Set active context: contextStore.SetActiveWorkItemIdAsync(1234)
  ├─ 3. Record navigation history
  ├─ 4. Display item (renders via RenderStatusAsync — static, no two-pass sync;
  │     SetCommand uses a direct render, not RenderWithSyncAsync)
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
| DD-01 | `--no-refresh` is a new flag, independent of `--no-live` | **Canonical distinction:** `--no-live` disables the Spectre async renderer entirely (intended for piped/scripted output where a TTY is unavailable). `--no-refresh` keeps rich Spectre rendering but skips the background ADO sync pass (intended for offline use or fast cached reads). The two flags are orthogonal. |
| DD-02 | Cache age displayed only when exceeds threshold | Showing "cached 0s ago" on every item adds noise. Only show when data might actually be stale. |
| DD-03 | Dirty indicator uses `●` character | Consistent with git/VS Code conventions. Visible in all terminals. |
| DD-04 | `ContextChangeService` is a new service, not an extension of `SyncCoordinator` | Separation of concerns: sync = cache freshness, context change = graph expansion. |
| DD-05 | Children fetched to depth 2 (fixed) | Matches the Epic→Issue→Task hierarchy depth. |
| DD-06 | Related links limited to depth 1 | Prevents exponential graph expansion. Related items are informational, not structural. |
| DD-07 | `ShowCommand` gains sync but not `--no-live` | `show` was cache-only; adding sync changes semantics, but `--no-refresh` provides opt-out. `ShowCommand` has no async rendering path, so `--no-live` is irrelevant (see DD-01). |
| DD-08 | Working set extension is fire-and-forget with error swallowing | Same pattern as existing post-render syncs. Extension failures must never fail the command. |
| DD-09 | `StatusCommand` revised view callback rebuilds the status view after sync | The `buildRevisedView` callback (L159-181) re-fetches the item, children, and parent from cache after sync, then rebuilds the full status `IRenderable`. Returning `null` on failure preserves the Pass 1 cached view. This is the core of the two-pass pattern. |
| DD-10 | MCP `ContextTools.Set()` should use `ContextChangeService` and remove inline parent chain hydration | The inline parent chain hydration (lines 61–72) duplicates `ContextChangeService.HydrateParentChainAsync`. Replacing it with a single `ExtendWorkingSetAsync` call simplifies the code, adds child/link hydration for free, and establishes parity with CLI `SetCommand`. The inline `SyncItemSetAsync` (line 79) can be removed since `ContextChangeService` handles parent chain sync. |

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
- Issue #1522 depends on Issue #1521: `ContextChangeService` must exist and be tested before it can be registered in MCP DI. (Issues #1520 and #1521 are delivered.)

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
| Context change extension increases ADO API call volume in CI/automation | Medium | Medium | Scripts calling `twig set` repeatedly could generate bursts of 3-5 ADO API calls per context change. Mitigated by `--no-refresh` for scripted use cases; future work could add rate limiting or a `--no-extend` flag. |
| MCP extension may slow `twig.set` response for agents | Low | Low | `ExtendWorkingSetAsync` runs best-effort after the MCP response is built. Extension failures are swallowed — no impact on tool result. Agents may see slightly longer total round-trip time but receive the formatted response before extension completes. |

## Open Questions

> Questions OQ-01 through OQ-05 were resolved during implementation. OQ-06 is new in Revision 4.

| ID | Question | Severity | Resolution |
|----|----------|----------|------------|
| OQ-01 | Should `--no-refresh` also suppress `ContextChangeService` graph extension? | Low | No — `--no-refresh` controls display sync only. Graph extension is a cache operation, not a display concern. The two are independent. |
| OQ-02 | Should `ShowCommand` gain `--no-live` in addition to `--no-refresh`? | Low | No — `ShowCommand` has no async rendering path. `--no-live` is irrelevant. See DD-07. |
| OQ-03 | What is the maximum reasonable depth for child hydration in `ContextChangeService`? | Low | Fixed at 2 levels (matches Epic→Issue→Task hierarchy). |
| OQ-04 | Should `NavigationCommands` (`up`/`down`/`root`) trigger `ContextChangeService`? | Low | No — navigation targets are already in cache (same tree). Extension would be redundant. See call-site audit. |
| OQ-05 | Should context-change extension be rate-limited for CI/automation scenarios? | Low | Not in this epic. The `--no-refresh` flag covers scripted use cases. |
| OQ-06 | Should MCP `twig.set` run `ExtendWorkingSetAsync` before or after returning the tool result? | Low | After — the response should include the formatted work item immediately. Extension runs best-effort after the response is built, same as CLI `SetCommand` pattern (L229 runs after display output). |

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
| `tests/Twig.Cli.Tests/Rendering/WorkspaceCacheAgeTests.cs` | Integration tests for WorkspaceCommand cache-age display |
| `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` | Integration tests for context change working set extension |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommand_ContextChangeTests.cs` | Integration tests for FlowStartCommand context change extension |
| `tests/Twig.Cli.Tests/Commands/NewCommand_ContextChangeTests.cs` | Integration tests for NewCommand `--set` context change extension |
| `tests/Twig.Mcp.Tests/Tools/ContextTools_ContextChangeTests.cs` | Integration tests for MCP `twig.set` context change extension |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/StatusCommand.cs` | Add `noRefresh` parameter, integrate cache-age and dirty indicators into cached view, `buildRevisedView` callback (L159-181) rebuilds status view from fresh data |
| `src/Twig/Commands/TreeCommand.cs` | Add `noRefresh` parameter, integrate sync into tree render with revised view |
| `src/Twig/Commands/ShowCommand.cs` | Add `noRefresh` parameter, add optional two-pass sync with `RenderWithSyncAsync` |
| `src/Twig/Commands/WorkspaceCommand.cs` | Add `noRefresh` parameter, display cache-age on sprint items |
| `src/Twig/Commands/SetCommand.cs` | Add `ContextChangeService.ExtendWorkingSetAsync()` call after context set |
| `src/Twig/Commands/NewCommand.cs` | Add `ContextChangeService.ExtendWorkingSetAsync()` when `--set` is used |
| `src/Twig/Commands/FlowStartCommand.cs` | Add `ContextChangeService.ExtendWorkingSetAsync()` after context set (L162) |
| `src/Twig/Rendering/SpectreRenderer.cs` | Add cache-age display in status/tree view builders, add dirty indicator rendering |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add cache-age suffix and dirty indicator to `FormatWorkItem` output |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | No change — JSON consumers can call ADO directly for live data |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `ContextChangeService` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Wire `ContextChangeService` into `SetCommand`, `NewCommand`, `FlowStartCommand` |
| `src/Twig/Program.cs` | Add `--no-refresh` parameter to `Status`, `Tree`, `Show`, `Workspace` commands |
| `src/Twig.Mcp/Tools/ContextTools.cs` | Replace inline parent chain hydration with `ContextChangeService.ExtendWorkingSetAsync()`; remove redundant `SyncItemSetAsync` call |
| `src/Twig.Mcp/Program.cs` | Register `ContextChangeService` in MCP DI container |

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
| T-1520-5 | **Add `--no-refresh` flag to `StatusCommand`** — Add `noRefresh` parameter. When true, skip `RenderWithSyncAsync` and use direct `RenderStatusAsync`. The `buildRevisedView` callback (L159-181) rebuilds the full status `IRenderable` from fresh cache data after sync completes. Wire through `Program.cs`. Add tests verifying: sync is skipped with `--no-refresh`, revised view rebuilds on sync success, `null` returned on sync failure. | `src/Twig/Commands/StatusCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/StatusCommand_CacheAwareTests.cs` | M (~150 LoC) |
| T-1520-6 | **Integrate two-pass sync into `TreeCommand`** — Replace the degenerate empty-cached-view `RenderWithSyncAsync` with a proper pattern: build tree view as cached view, sync working set, then rebuild tree as revised view. Add `noRefresh` parameter. Wire through `Program.cs`. | `src/Twig/Commands/TreeCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/TreeCommand_CacheAwareTests.cs` | L (~250 LoC) |
| T-1520-7 | **Add two-pass sync to `ShowCommand`** — Add `RenderWithSyncAsync` usage: render cached item immediately, sync item by ID, revise display. Add `noRefresh` parameter only (see DD-07). Wire through `Program.cs`. Add tests verifying sync behavior and `--no-refresh` bypass. | `src/Twig/Commands/ShowCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/ShowCommand_CacheAwareTests.cs` | M (~120 LoC) |
| T-1520-8 | **Add cache-age to `WorkspaceCommand` sprint items** — Add cache-age suffix to stale sprint items in workspace table. Add `noRefresh` flag. Wire through `Program.cs`. Add tests verifying cache-age display on stale items. | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Rendering/WorkspaceCacheAgeTests.cs` | S (~80 LoC) |

**Acceptance Criteria:**
- [x] All display commands render cached data first, then refresh
- [x] Cache age is shown when data exceeds `CacheStaleMinutes`
- [x] Dirty items are visibly marked with `●` and change summary
- [x] `--no-refresh` flag skips the live sync pass on all display commands
- [x] All new code has unit tests with ≥ 90% branch coverage

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
| T-1521-4 | **Integrate into `NewCommand` and `FlowStartCommand`** — When `--set` flag is used in `NewCommand`, call `ContextChangeService.ExtendWorkingSetAsync()`. Similarly for `FlowStartCommand` after context is set (L162). Add integration tests for both commands verifying context change triggers working set extension. | `src/Twig/Commands/NewCommand.cs`, `src/Twig/Commands/FlowStartCommand.cs`, `tests/Twig.Cli.Tests/Commands/NewCommand_ContextChangeTests.cs`, `tests/Twig.Cli.Tests/Commands/FlowStartCommand_ContextChangeTests.cs` | S (~80 LoC) |
| T-1521-5 | **Add integration tests for context change extension** — Test scenarios: (1) set to out-of-sprint item → parents/children/links fetched, (2) set to in-sprint item → minimal additional fetches, (3) network failure during extension → command still succeeds, (4) additive guarantee → existing cache items not removed. | `tests/Twig.Cli.Tests/Commands/SetCommand_ContextChangeTests.cs` | M (~200 LoC) |

**Acceptance Criteria:**
- [x] `twig set` to an out-of-sprint item fetches parents to root
- [x] `twig set` fetches 2 levels of children
- [x] `twig set` fetches 1 level of related links
- [x] Context change logic is in a single shared `ContextChangeService` codepath
- [x] Working set extension is additive (never removes existing items)
- [x] All context change points (`set`, `new --set`, `flow start`) use `ContextChangeService`
- [x] Extension failures never cause the command to fail

---

### Issue #1522: MCP `twig.set` ContextChangeService integration

**Goal:** Integrate `ContextChangeService` into the MCP server so that `twig.set` via MCP achieves parity with CLI `twig set` — setting context to an out-of-sprint item proactively extends the working set with parent chain, children, and related links.

**Prerequisites:** Issue #1521 (ContextChangeService must exist and be tested)

#### Tasks

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|----------------|--------|
| T-1522-1 | **Register `ContextChangeService` in MCP DI** — Add factory-based registration in `Twig.Mcp/Program.cs` using the same pattern as `SyncCoordinator` (explicit `sp => new ContextChangeService(...)` for AOT robustness). Wire dependencies: `IWorkItemRepository`, `IAdoWorkItemService`, `SyncCoordinator`, `ProtectedCacheWriter`, `IWorkItemLinkRepository` (optional/nullable). | `src/Twig.Mcp/Program.cs` | XS (~15 LoC) | TO DO |
| T-1522-2 | **Integrate into `ContextTools.Set()`** — Inject `ContextChangeService` into `ContextTools` constructor. Replace the inline parent chain hydration (lines 61–72) and `SyncItemSetAsync` call (line 79) with a single `contextChangeService.ExtendWorkingSetAsync(item.Id, ct)` call after `contextStore.SetActiveWorkItemIdAsync`. Run best-effort (same fire-and-forget pattern as CLI `SetCommand`). | `src/Twig.Mcp/Tools/ContextTools.cs` | S (~40 LoC net reduction) | TO DO |
| T-1522-3 | **Add integration tests for MCP context change** — Test scenarios: (1) `twig.set` invokes `ExtendWorkingSetAsync` (covers both numeric ID and pattern inputs — same assertion, same code path), (2) extension failure does not affect tool result. Child/link hydration behavior is already fully covered by `ContextChangeServiceTests.cs`; these tests verify wiring only. | `tests/Twig.Mcp.Tests/Tools/ContextTools_ContextChangeTests.cs` | S (~80 LoC) | TO DO |

**Acceptance Criteria:**
- [ ] `ContextChangeService` is registered in MCP DI container
- [ ] `ContextTools.Set()` calls `ExtendWorkingSetAsync` after setting context
- [ ] Inline parent chain hydration in `ContextTools.Set()` is removed (replaced by `ContextChangeService`)
- [ ] Extension failures never fail the `twig.set` MCP tool call
- [ ] MCP `twig.set` fetches parents, 2 levels of children, and 1 level of links (parity with CLI)

## PR Groups

### PG-1: Two-Pass Rendering for Status and Tree Commands ✅
**Tasks:** T-1520-1, T-1520-2, T-1520-3, T-1520-4, T-1520-5, T-1520-6  
**Classification:** Deep — new domain utilities plus rendering pipeline and two key commands  
**Estimated LoC:** ~850  
**Files:** ~14 (source + test)  
**Description:** Pure domain utilities for cache-age formatting and dirty-state summarization, followed by integration into `SpectreRenderer` and `HumanOutputFormatter`. Adds `--no-refresh` to `StatusCommand` and `TreeCommand`. Fixes `TreeCommand`'s degenerate `RenderWithSyncAsync` usage. The formatting utilities have no callers until the renderer changes ship — batching into one PR avoids a useless intermediate state.  
**Status:** ✅ Merged (PR #29)  
**Successors:** PG-2

### PG-2: Two-Pass Rendering for Show and Workspace ✅
**Tasks:** T-1520-7, T-1520-8  
**Classification:** Wide — mechanical application of the same pattern to remaining commands  
**Estimated LoC:** ~250  
**Files:** ~8 (source + test)  
**Description:** Extends the two-pass pattern to `ShowCommand` (adds `--no-refresh` only, not `--no-live`) and `WorkspaceCommand`. Includes integration tests for both. All patterns established in PG-1.  
**Status:** ✅ Merged (PR #30)  
**Successors:** None

### PG-3: Context Change Working Set Extension ✅
**Tasks:** T-1521-1, T-1521-2, T-1521-3, T-1521-4, T-1521-5  
**Classification:** Deep — new domain service with graph traversal logic  
**Estimated LoC:** ~660  
**Files:** ~12 (source + test, including FlowStartCommand and NewCommand context-change tests)  
**Description:** Introduces `ContextChangeService` (with nullable `IWorkItemLinkRepository` for graceful degradation), registers it in DI, and integrates into `SetCommand`, `NewCommand`, and `FlowStartCommand`. Comprehensive tests for parent/child/link hydration across all three integration points.  
**Status:** ✅ Merged (PR #33)  
**Successors:** PG-4

### PG-4: MCP ContextChangeService Integration
**Tasks:** T-1522-1, T-1522-2, T-1522-3  
**Classification:** Deep — small scope but modifies MCP server DI and tool behavior  
**Estimated LoC:** ~120 (net reduction in `ContextTools.cs` offset by new test file)
**Files:** ~3 (source + test)  
**Description:** Registers `ContextChangeService` in MCP DI, replaces the inline parent chain hydration in `ContextTools.Set()` with a single `ExtendWorkingSetAsync` call, and adds integration tests. Small change surface but semantically significant — establishes MCP–CLI parity for context change behavior.  
**Status:** TO DO  
**Successors:** None

### PR Group Execution Order

```
PG-1 ──► PG-2                    (✅ Done)
  │
  └──► PG-3 ──► PG-4             (PG-3 ✅ Done, PG-4 remaining)
```

PG-4 depends on PG-3 because `ContextChangeService` must exist before it can be registered in MCP DI.

## References

- Existing `RenderWithSyncAsync` implementation: `src/Twig/Rendering/SpectreRenderer.cs:1036-1107`
- Spectre.Console Live API: https://spectreconsole.net/live/live-display
- ADO REST API — Get Work Item: `_apis/wit/workitems/{id}?$expand=relations`
- Existing `SyncCoordinator` design: `src/Twig.Domain/Services/SyncCoordinator.cs`
- Set Command sync optimization plan: `docs/projects/set-command-sync-optimization.plan.md`

---

## Appendix: Completion Log

**Issues #1520, #1521 completed:** 2026-04-15  
**Merged PRs:** PR #29 (Status/Tree two-pass), PR #30 (Show/Workspace two-pass), PR #33 (Context change working set extension)  
**Issues Closed:** #1520 (Two-pass rendering), #1521 (Context change auto-extends working set)

All three initial PR groups shipped successfully. The two-pass cache-then-refresh rendering pattern is now active across `StatusCommand`, `TreeCommand`, `ShowCommand`, and `WorkspaceCommand`. Cache-age indicators and dirty-state summaries provide visual feedback on data freshness. The `--no-refresh` flag is available on all display commands. `ContextChangeService` automatically extends the working set with parent chains, child graphs, and related links when context changes via `set`, `new --set`, or `flow start` in the CLI.

**Remaining:** Issue #1522 (MCP integration) — PG-4.

