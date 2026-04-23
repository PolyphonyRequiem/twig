# Manual Track/Untrack Overlay — Solution Design & Implementation Plan

> **Work Item:** #1947 — Manual Track/Untrack Overlay  
> **Parent Epic:** #1945 — Workspace Modes & Tracking  
> **Type:** Issue → Tasks  
> **Author:** Daniel Green  
> **Status:** Draft  
> **Revision:** 0  
> **Revision Notes:** Initial draft.

---

## Executive Summary

This plan introduces a **manual tracking overlay** for the twig workspace — a persistent, user-curated layer that lets users pin individual work items (`track`) or entire sub-trees (`track-tree`) to their workspace, regardless of sprint/iteration scoping. The overlay also supports exclusions (`exclude`) to hide noisy items, and auto-cleanup policies that automatically untrack items when they reach a completed state. The implementation adds two new SQLite tables (`tracked_items`, `excluded_items`), a new domain service (`TrackingService`), a new repository interface (`ITrackingRepository`), five CLI sub-commands under `twig workspace`, and integration with the existing sync/refresh pipeline for tree re-exploration. This design is additive — it does not modify the existing sprint-based workspace model, but layers tracked/excluded items alongside sprint and seed collections.

---

## Background

### Current State

The twig workspace is currently scoped exclusively by **sprint iteration**. The `WorkspaceCommand` queries items matching the current iteration path (optionally filtered by user assignee), combines them with seeds, and renders the unified `Workspace` read model. There is no mechanism for a user to:

1. Pin a work item from a different iteration to their workspace view
2. Track an entire parent–child tree across syncs
3. Exclude a noisy sprint item from appearing in the workspace
4. Have items automatically untracked when they reach a completed state

The `Workspace` read model (in `Twig.Domain.ReadModels`) currently contains three collections:
- `ContextItem` — the active context (nullable)
- `SprintItems` — items in the current iteration
- `Seeds` — locally-created work items

The `WorkingSet` (in `Twig.Domain.Services`) computes the union of active item, parent chain, children, sprint items, seeds, and dirty items — used by `SyncCoordinator` to know which items to keep fresh.

### Architecture Context

| Component | Role |
|-----------|------|
| `SqliteCacheStore` | Schema DDL (v9), table lifecycle, WAL mode |
| `IContextStore` / `SqliteContextStore` | Key-value store for active context + settings |
| `IWorkItemRepository` / `SqliteWorkItemRepository` | CRUD for cached work items |
| `WorkspaceCommand` | Builds `Workspace` read model, renders output |
| `WorkingSetService` | Computes scope for sync coordination |
| `SyncCoordinator` / `SyncCoordinatorFactory` | Tiered-TTL sync between cache and ADO |
| `RefreshOrchestrator` | Full refresh: fetch → conflict → save → hydrate ancestors |
| `TwigConfiguration` | JSON config POCO with `SetValue()` reflection-free mutation |
| `TwigJsonContext` | AOT-compatible source-gen JSON serialization |
| `StateCategoryResolver` | Process-agnostic state → category mapping |
| `IProcessConfigurationProvider` | Dynamic process config (no hardcoded template names) |
| `Program.cs` / `GroupedHelp` | Command routing, known-command registry |

### Prior Art

- **Seeds** already provide an "always-visible" overlay (negative IDs, `is_seed=1`). The tracking overlay follows a similar pattern: items that appear in the workspace regardless of iteration scope.
- **Navigation history** (`navigation_history` table) tracks visited items with timestamps — a simpler version of the tracking persistence pattern.
- **SyncCoordinator.SyncChildrenAsync()** already fetches all children of a parent unconditionally — this will be reused for track-tree re-exploration.
- **`IContextStore.SetValueAsync()`** stores arbitrary key-value settings — auto-cleanup policy configuration will use this pattern.

### Call-Site Audit

The following components will be modified or extended. This audit ensures no cross-cutting behavior is missed:

| File | Method/Area | Current Usage | Impact |
|------|-------------|---------------|--------|
| `Workspace.cs` | `Build()`, `ListAll()` | Combines context + sprint + seeds | **Add** tracked items collection, **filter** excluded items |
| `WorkspaceCommand.cs` | `ExecuteAsync()`, `ExecuteSyncAsync()` | Queries sprint items + seeds | **Add** tracked item queries, exclusion filtering, footer |
| `WorkingSet.cs` | `ComputeAllIds()` | Union of sprint/seed/context/dirty | **Add** tracked item IDs to union |
| `WorkingSetService.cs` | `ComputeAsync()` | Queries 7 data sources | **Add** 8th source: tracked item IDs |
| `RefreshOrchestrator.cs` | `FetchItemsAsync()` | Fetches sprint + active + children | **Add** track-tree re-exploration |
| `SqliteCacheStore.cs` | `Ddl`, `DropAllTables()`, `SchemaVersion` | Schema v9 | **Bump** to v10, add 2 tables |
| `TwigConfiguration.cs` | Config POCOs | No tracking config | **Add** `TrackingConfig` sub-config |
| `TwigJsonContext.cs` | JSON context | No tracking types | **Add** `[JsonSerializable]` for new types |
| `Program.cs` | `TwigCommands` class | No tracking commands | **Add** 5 `workspace track/untrack/...` commands |
| `GroupedHelp.cs` | `KnownCommands` set | No tracking commands | **Add** new command names |
| `CommandRegistrationModule.cs` | `AddCoreCommands()` | No tracking command | **Add** `TrackingCommand` registration |
| `CommandServiceModule.cs` | `AddTwigCommandServices()` | No tracking service | **Add** `TrackingService` registration |
| `TwigServiceRegistration.cs` | `AddTwigCoreServices()` | No tracking repo | **Add** `ITrackingRepository` registration |
| `IOutputFormatter.cs` | Formatter interface | No tracking output | **Add** `FormatTrackedItems()` method |
| MCP `ReadTools.cs` | `twig_workspace` | Returns sprint + seeds | **Add** tracked items to response |

---

## Problem Statement

Users working across multiple sprints, supporting escalations, or monitoring parent epics have no way to pin arbitrary work items to their workspace view. The workspace is rigidly scoped to the current iteration, forcing users to either:

1. **Switch context repeatedly** with `twig set` to view items outside their sprint
2. **Lose visibility** of cross-sprint items they're actively monitoring
3. **Cannot hide noisy items** that are in-sprint but not personally relevant (e.g., team-shared tasks)
4. **Manual cleanup burden** — items that complete should naturally leave the tracked set

---

## Goals and Non-Goals

### Goals

1. **G-1:** Users can pin individual items to the workspace via `twig workspace track <id>`
2. **G-2:** Users can pin an item with its tree (parent chain + children) via `twig workspace track-tree <id>`
3. **G-3:** Users can remove tracked items via `twig workspace untrack <id>`
4. **G-4:** Users can exclude items from workspace view via `twig workspace exclude <id>`
5. **G-5:** Users can list and manage exclusions via `twig workspace exclusions`
6. **G-6:** Track-tree items automatically re-explore their tree on sync/refresh
7. **G-7:** Auto-cleanup policies remove completed items from tracking
8. **G-8:** Tracked items persist across CLI sessions (SQLite-backed)
9. **G-9:** Tracked items appear in the workspace alongside sprint items
10. **G-10:** Exclusion count shown in workspace footer

### Non-Goals

- **NG-1:** Workspace modes (sprint, area, recent) — separate issue under Epic #1945
- **NG-2:** Tree depth configuration (upward/downward/sideways) — separate issue under Epic #1945
- **NG-3:** Rendering changes to the Spectre.Console live view — tracked items render in the sync path first; live Spectre streaming is a follow-up
- **NG-4:** MCP tool exposure for track/untrack mutations — read-only inclusion in `twig_workspace` is in scope; mutation tools are deferred
- **NG-5:** Bulk track/untrack (multiple IDs in one command) — single-ID operations only for v1

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| **FR-1** | `twig workspace track <id>` persists item ID with `mode=single` to `tracked_items` table |
| **FR-2** | `twig workspace track-tree <id>` persists item ID with `mode=tree` to `tracked_items` table |
| **FR-3** | `twig workspace untrack <id>` removes the item from `tracked_items` |
| **FR-4** | `twig workspace exclude <id>` adds item ID to `excluded_items` table |
| **FR-5** | `twig workspace exclusions` lists all excluded item IDs; supports `--clear` to remove all |
| **FR-6** | Workspace view includes tracked items in a "Tracked" section |
| **FR-7** | Excluded items are silently filtered from sprint items display |
| **FR-8** | Track-tree items re-explore on sync: fetch the tracked item, resolve parent chain, fetch children |
| **FR-9** | Transitive inclusions: new ADO children appear; removed children are trimmed |
| **FR-10** | Auto-cleanup `on-complete` policy: untrack items whose state category is `Completed` |
| **FR-11** | Auto-cleanup `on-complete-and-past` policy: untrack when `Completed` AND iteration < current |
| **FR-12** | Default auto-cleanup policy: `none` (no automatic removal) |
| **FR-13** | Exclusion count shown in workspace footer (e.g., "2 items excluded") |
| **FR-14** | Tracking a seed (negative ID) is rejected with an error message |
| **FR-15** | Tracking an already-tracked item is idempotent (no error, upserts mode) |
| **FR-16** | Untracking a non-tracked item returns a "not tracked" info message |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| **NFR-1** | AOT-compatible: no reflection in new types; all JSON types in `TwigJsonContext` |
| **NFR-2** | Process-agnostic: state completion detection via `StateCategoryResolver`, not hardcoded names |
| **NFR-3** | Schema migration: bump `SchemaVersion` to 10; existing databases auto-rebuild |
| **NFR-4** | Parameterized SQL: all queries use `@param` binding, no string interpolation |
| **NFR-5** | Test coverage: unit tests for domain service, repository, and command output |

---

## Proposed Design

### Architecture Overview

The tracking overlay is implemented as three layers following the existing codebase patterns:

```
┌─────────────────────────────────────────────────────────────────┐
│                     CLI COMMAND LAYER                            │
│  TrackingCommand (5 sub-commands)                                │
│  ├── workspace track <id>      → TrackingService.TrackAsync()    │
│  ├── workspace track-tree <id> → TrackingService.TrackTreeAsync()│
│  ├── workspace untrack <id>    → TrackingService.UntrackAsync()  │
│  ├── workspace exclude <id>    → TrackingService.ExcludeAsync()  │
│  └── workspace exclusions      → TrackingService.ListExclusions()│
└────────────────────────┬────────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────────┐
│                     DOMAIN SERVICE LAYER                         │
│  TrackingService                                                 │
│  ├── Track/TrackTree/Untrack/Exclude/ListExclusions              │
│  ├── GetTrackedItemsAsync() → used by WorkspaceCommand           │
│  ├── GetExcludedIdsAsync() → used by WorkspaceCommand            │
│  ├── SyncTrackedTreesAsync() → used by RefreshOrchestrator       │
│  └── ApplyCleanupPolicyAsync() → called during sync              │
│                                                                  │
│  ITrackingRepository (interface)                                 │
│  ├── tracked_items CRUD                                          │
│  └── excluded_items CRUD                                         │
└────────────────────────┬────────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────────┐
│                     PERSISTENCE LAYER                            │
│  SqliteTrackingRepository : ITrackingRepository                  │
│  ├── tracked_items table (id, work_item_id, mode, tracked_at)    │
│  └── excluded_items table (id, work_item_id, excluded_at)        │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `ITrackingRepository` (Domain Interface)

```csharp
public interface ITrackingRepository
{
    Task<IReadOnlyList<TrackedItem>> GetAllTrackedAsync(CancellationToken ct = default);
    Task<TrackedItem?> GetTrackedByWorkItemIdAsync(int workItemId, CancellationToken ct = default);
    Task UpsertTrackedAsync(int workItemId, TrackingMode mode, CancellationToken ct = default);
    Task RemoveTrackedAsync(int workItemId, CancellationToken ct = default);
    Task RemoveTrackedBatchAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default);

    Task<IReadOnlyList<ExcludedItem>> GetAllExcludedAsync(CancellationToken ct = default);
    Task AddExcludedAsync(int workItemId, CancellationToken ct = default);
    Task RemoveExcludedAsync(int workItemId, CancellationToken ct = default);
    Task ClearAllExcludedAsync(CancellationToken ct = default);
}
```

#### 2. Value Objects

```csharp
// Tracking mode enum
public enum TrackingMode { Single, Tree }

// Tracked item record
public sealed record TrackedItem(int WorkItemId, TrackingMode Mode, DateTimeOffset TrackedAt);

// Excluded item record
public sealed record ExcludedItem(int WorkItemId, DateTimeOffset ExcludedAt);

// Cleanup policy enum
public enum TrackingCleanupPolicy { None, OnComplete, OnCompleteAndPast }
```

#### 3. `TrackingService` (Domain Service)

Core orchestration service. Dependencies: `ITrackingRepository`, `IWorkItemRepository`, `IProcessConfigurationProvider`, `IProcessTypeStore`.

Key methods:
- **`TrackAsync(int id)`** — Validates ID > 0, upserts with `Single` mode, syncs item to cache
- **`TrackTreeAsync(int id)`** — Upserts with `Tree` mode, syncs item + children to cache
- **`UntrackAsync(int id)`** — Removes from tracked_items
- **`ExcludeAsync(int id)`** — Adds to excluded_items
- **`GetTrackedItemsAsync()`** — Returns all tracked IDs for workspace composition
- **`GetExcludedIdsAsync()`** — Returns all excluded IDs for workspace filtering
- **`SyncTrackedTreesAsync(SyncCoordinator)`** — For each `Tree` mode item: fetch item, fetch children, trim removed
- **`ApplyCleanupPolicyAsync(policy, currentIteration)`** — Evaluates tracked items against completion state; removes matching

#### 4. `SqliteTrackingRepository` (Infrastructure)

Two new tables added to the schema DDL:

```sql
CREATE TABLE tracked_items (
    work_item_id INTEGER PRIMARY KEY,
    mode TEXT NOT NULL DEFAULT 'single',
    tracked_at TEXT NOT NULL
);

CREATE TABLE excluded_items (
    work_item_id INTEGER PRIMARY KEY,
    excluded_at TEXT NOT NULL
);
```

#### 5. Configuration Extension

New `TrackingConfig` sub-config in `TwigConfiguration`:

```csharp
public sealed class TrackingConfig
{
    public string CleanupPolicy { get; set; } = "none";  // "none" | "on-complete" | "on-complete-and-past"
}
```

Accessible via `twig config tracking.cleanuppolicy <value>`.

#### 6. Workspace Read Model Extension

The `Workspace` class gains a new collection:

```csharp
public IReadOnlyList<WorkItem> TrackedItems { get; }
```

And `Build()` gains a `trackedItems` parameter. The `ListAll()` method includes tracked items in its union. Exclusion filtering is applied by the caller (`WorkspaceCommand`) before passing sprint items to `Build()`.

#### 7. WorkingSet Extension

`WorkingSet` gains `TrackedItemIds` to include tracked items in the sync scope:

```csharp
public IReadOnlyList<int> TrackedItemIds { get; init; } = [];
```

`ComputeAllIds()` includes these in the union.

### Data Flow

#### Track Command Flow

```
User: twig workspace track 42
  → TrackingCommand.Track(42)
    → TrackingService.TrackAsync(42)
      → Validate: id > 0 (reject seeds)
      → ITrackingRepository.UpsertTrackedAsync(42, Single)
      → SyncCoordinator.SyncItemAsync(42) // ensure item is in cache
      → Output: "Tracking #42: Some Title"
```

#### Workspace View with Tracking

```
User: twig workspace
  → WorkspaceCommand.ExecuteAsync()
    → [existing] Get context, sprint items, seeds
    → [NEW] TrackingService.GetTrackedItemsAsync()
      → ITrackingRepository.GetAllTrackedAsync()
      → IWorkItemRepository.GetByIdsAsync(trackedIds)
      → Return resolved WorkItem list
    → [NEW] TrackingService.GetExcludedIdsAsync()
      → Filter excluded IDs from sprint items
    → Build Workspace(context, filteredSprintItems, seeds, trackedItems)
    → Render with "Tracked" section + exclusion footer
```

#### Sync with Track-Tree Re-Exploration

```
twig sync (pull phase)
  → RefreshOrchestrator.FetchItemsAsync(wiql, force)
    → [existing] Fetch sprint items, active item, children
  → [NEW] TrackingService.SyncTrackedTreesAsync(syncCoordinator)
    → For each tracked item with mode=Tree:
      → SyncCoordinator.SyncItemAsync(id)         // refresh the root
      → SyncCoordinator.SyncChildrenAsync(id)      // re-explore children
      → Compare cached children vs. fresh children
      → New children: automatically included (additive)
      → Removed children: evicted from cache if not otherwise scoped
  → [NEW] TrackingService.ApplyCleanupPolicyAsync(policy, iteration)
    → For each tracked item:
      → Resolve state category via StateCategoryResolver
      → If policy=on-complete AND category=Completed → untrack
      → If policy=on-complete-and-past AND category=Completed AND iteration < current → untrack
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| **DD-1** | Tracking is table-based, not context-store key-value | Supports multi-item queries, mode differentiation, batch operations. Key-value would require JSON serialization/deserialization for every access. |
| **DD-2** | `work_item_id` is `INTEGER PRIMARY KEY` (not autoincrement) | One tracking entry per work item; upsert semantics via `INSERT OR REPLACE`. |
| **DD-3** | Exclusions are separate from untracking | Exclusion hides a sprint item; untracking removes a manually-pinned item. Different intent, different table. |
| **DD-4** | Auto-cleanup uses `StateCategoryResolver`, not hardcoded state names | Process-agnostic: works with Agile ("Closed"), Scrum ("Done"), CMMI ("Closed"), Basic ("Done"). |
| **DD-5** | Schema version bumps to 10 | Forces auto-rebuild of all databases. Accepted cost: users run `twig sync` after upgrade to re-populate. |
| **DD-6** | Track-tree re-exploration happens during refresh, not on every workspace view | Avoids N+1 ADO calls on read-only commands. Sync is the explicit "go fetch" moment. |
| **DD-7** | Tracked items are included in `WorkingSet.AllIds` | Ensures SyncCoordinator keeps tracked items fresh during working set sync. |
| **DD-8** | Exclusions only filter sprint items, not tracked items | If you explicitly track something, you want to see it. Exclusions are for hiding auto-scoped sprint noise. |
| **DD-9** | Commands are `workspace track`, not `track` top-level | Follows the established `seed <verb>`, `nav <verb>` compound command pattern. Groups tracking under the workspace namespace. |
| **DD-10** | Cleanup policy stored in config file, not DB | Config is the established pattern for behavioral settings (see `FlowConfig`, `DisplayConfig`). Policy applies across all tracked items uniformly. |

---

## Dependencies

### External Dependencies
- None — all implementation uses existing libraries (Microsoft.Data.Sqlite, Spectre.Console, ConsoleAppFramework)

### Internal Dependencies
- `StateCategoryResolver` — for process-agnostic completion detection (auto-cleanup)
- `SyncCoordinator` — for track-tree re-exploration during refresh
- `IProcessConfigurationProvider` / `IProcessTypeStore` — for state category resolution
- `SqliteCacheStore` — schema DDL extension (version bump)

### Sequencing Constraints
- Schema version bump (v9 → v10) must land with the first PR that adds the new tables
- `ITrackingRepository` and `SqliteTrackingRepository` must be implemented before `TrackingService`
- `TrackingService` must be implemented before CLI commands and sync integration
- The three existing child tasks (#1956, #1957, #1958) naturally sequence: commands first, sync integration second, auto-cleanup third

---

## Impact Analysis

### Components Affected

| Component | Scope of Change |
|-----------|----------------|
| `SqliteCacheStore` | Schema DDL extension (2 tables, version bump 9→10) |
| `Workspace` read model | New `TrackedItems` property + `Build()` parameter |
| `WorkingSet` | New `TrackedItemIds` property |
| `WorkingSetService` | Additional query for tracked IDs |
| `WorkspaceCommand` | Tracked item query, exclusion filtering, footer rendering |
| `RefreshOrchestrator` | Track-tree sync + auto-cleanup hook |
| `TwigConfiguration` | New `TrackingConfig` sub-config |
| `Program.cs` | 5 new command registrations + `GroupedHelp` entries |
| `IOutputFormatter` / impls | Tracked items section + exclusion footer |
| MCP `ReadTools.cs` | Tracked items in `twig_workspace` response |

### Backward Compatibility

- **Schema rebuild:** Version bump forces rebuild. Users must `twig sync` after upgrading. This is the established pattern (8 previous version bumps).
- **Config file:** New `tracking` key is optional with defaults. Existing config files are forward-compatible.
- **CLI surface:** All new commands are additive. No existing commands change behavior.
- **Workspace output:** Sprint items continue to render as before. Tracked items appear in a new section. Exclusion footer is additive.

### Performance Implications

- **Workspace view:** +2 SQLite queries (tracked items, excluded items). Negligible at expected scale (<100 tracked items).
- **Sync/refresh:** Track-tree re-exploration adds N ADO calls (one `FetchChildrenAsync` per tree-tracked item). Bounded by number of tree-tracked items, typically <5.
- **Auto-cleanup:** Linear scan of tracked items against state categories. O(T) where T = tracked count.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Schema version bump forces cache rebuild on upgrade | Certain | Low | Established pattern; `twig sync` re-populates in seconds |
| Track-tree with deeply nested items causes excessive ADO calls | Low | Medium | Honor `Display.TreeDepth` config; cap children at configured depth |
| Tracked item deleted in ADO causes persistent ghost in tracking | Low | Low | SyncCoordinator already evicts "not found" items; cleanup policy will handle |
| User tracks hundreds of items, bloating workspace | Low | Low | Workspace footer shows count; future enhancement could add a cap |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `workspace exclude` support excluding an item that is also tracked (conflicting intent)? | Low | Proposed: tracked wins — exclude only hides sprint-scoped items. Document this in help text. |
| 2 | Should `workspace exclusions --clear` also accept `--remove <id>` for targeted removal? | Low | Proposed: yes, `workspace exclusions --remove <id>` for single removal, `--clear` for all. |
| 3 | Should tracked items appear in the Spectre.Console live rendering path (async streaming)? | Low | Proposed: defer to follow-up. Tracked items render in sync path first. Live path can load from cache without streaming. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Interfaces/ITrackingRepository.cs` | Repository interface for tracked + excluded items |
| `src/Twig.Domain/ValueObjects/TrackedItem.cs` | Value object: `TrackedItem(WorkItemId, Mode, TrackedAt)` |
| `src/Twig.Domain/ValueObjects/ExcludedItem.cs` | Value object: `ExcludedItem(WorkItemId, ExcludedAt)` |
| `src/Twig.Domain/Enums/TrackingMode.cs` | Enum: `Single`, `Tree` |
| `src/Twig.Domain/Enums/TrackingCleanupPolicy.cs` | Enum: `None`, `OnComplete`, `OnCompleteAndPast` |
| `src/Twig.Domain/Services/TrackingService.cs` | Domain service: track, untrack, exclude, sync trees, cleanup |
| `src/Twig.Infrastructure/Persistence/SqliteTrackingRepository.cs` | SQLite-backed `ITrackingRepository` |
| `src/Twig/Commands/TrackingCommand.cs` | CLI command: 5 sub-commands under `workspace` |
| `tests/Twig.Domain.Tests/Services/TrackingServiceTests.cs` | Unit tests for `TrackingService` |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteTrackingRepositoryTests.cs` | Integration tests for SQLite repo |
| `tests/Twig.Cli.Tests/Commands/TrackingCommandTests.cs` | CLI command tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Bump `SchemaVersion` 9→10; add `tracked_items` + `excluded_items` DDL; add to `DropAllTables` list |
| `src/Twig.Domain/ReadModels/Workspace.cs` | Add `TrackedItems` property; extend `Build()`, `ListAll()`, `GetDirtyItems()` |
| `src/Twig.Domain/Services/WorkingSet.cs` | Add `TrackedItemIds` property; include in `ComputeAllIds()` |
| `src/Twig.Domain/Services/WorkingSetService.cs` | Query tracked IDs from `ITrackingRepository`; include in working set |
| `src/Twig/Commands/WorkspaceCommand.cs` | Query tracked items and exclusions; filter sprint items; pass to `Workspace.Build()` |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | Add `SyncTrackedTreesAsync()` call after main refresh; add cleanup hook |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `ITrackingRepository` / `SqliteTrackingRepository` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `TrackingService` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `TrackingCommand` |
| `src/Twig/Program.cs` | Add 5 `[Command("workspace track")]` etc. entries; add to `GroupedHelp.KnownCommands` |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `TrackingConfig` class + `Tracking` property + `SetValue` cases |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for `TrackingConfig` |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatTrackedItems()` method |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement `FormatTrackedItems()` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Include tracked items in workspace JSON |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement `FormatTrackedItems()` (no-op or minimal) |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Include tracked items in compact JSON |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Include tracked items in `twig_workspace` response |

---

## ADO Work Item Structure

The parent Issue #1947 already has three child Tasks. Tasks are defined below under each existing child.

### Issue #1956: Implement workspace track, track-tree, untrack, exclude, and exclusions commands

**Goal:** Deliver the core tracking/exclusion persistence layer (domain + infrastructure) and all five CLI sub-commands.

**Prerequisites:** None (foundational)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1956.1 | **Schema & repository:** Bump schema to v10. Add `tracked_items` and `excluded_items` DDL. Implement `ITrackingRepository` interface and `SqliteTrackingRepository`. Register in `TwigServiceRegistration`. Write repository integration tests. | `SqliteCacheStore.cs`, `ITrackingRepository.cs`, `SqliteTrackingRepository.cs`, `TwigServiceRegistration.cs`, `SqliteTrackingRepositoryTests.cs` | M |
| T-1956.2 | **Value objects & enums:** Create `TrackedItem`, `ExcludedItem` records and `TrackingMode`, `TrackingCleanupPolicy` enums. Add to `TwigJsonContext`. | `TrackedItem.cs`, `ExcludedItem.cs`, `TrackingMode.cs`, `TrackingCleanupPolicy.cs`, `TwigJsonContext.cs` | S |
| T-1956.3 | **TrackingService (core methods):** Implement `TrackAsync`, `TrackTreeAsync`, `UntrackAsync`, `ExcludeAsync`, `GetTrackedItemsAsync`, `GetExcludedIdsAsync`, `ListExclusionsAsync`. Register in `CommandServiceModule`. Write unit tests. | `TrackingService.cs`, `CommandServiceModule.cs`, `TrackingServiceTests.cs` | L |
| T-1956.4 | **CLI commands & configuration:** Implement `TrackingCommand` with 5 sub-commands. Add `TrackingConfig` to `TwigConfiguration` with `SetValue` support. Register in `CommandRegistrationModule`. Update `Program.cs` with command routing and `GroupedHelp.KnownCommands`. | `TrackingCommand.cs`, `TwigConfiguration.cs`, `CommandRegistrationModule.cs`, `Program.cs` | L |
| T-1956.5 | **Workspace integration & formatting:** Extend `Workspace` read model with `TrackedItems`. Modify `WorkspaceCommand` to query tracked items, filter exclusions, render "Tracked" section and exclusion footer. Extend all `IOutputFormatter` implementations. | `Workspace.cs`, `WorkspaceCommand.cs`, `IOutputFormatter.cs`, `HumanOutputFormatter.cs`, `JsonOutputFormatter.cs`, `MinimalOutputFormatter.cs`, `JsonCompactOutputFormatter.cs` | L |

**Acceptance Criteria:**
- [ ] `twig workspace track <id>` persists tracking and confirms with item title
- [ ] `twig workspace track-tree <id>` persists with tree mode
- [ ] `twig workspace untrack <id>` removes tracking
- [ ] `twig workspace exclude <id>` adds exclusion
- [ ] `twig workspace exclusions` lists all exclusions; `--clear` removes all; `--remove <id>` removes one
- [ ] Tracked items appear in workspace view under a "Tracked" section
- [ ] Excluded sprint items are hidden; footer shows exclusion count
- [ ] Tracking a seed (negative ID) is rejected
- [ ] Tracking an already-tracked item upserts silently
- [ ] All new code is AOT-compatible (no reflection)
- [ ] Schema version is 10; existing databases auto-rebuild

### Issue #1957: Integrate track-tree sync — re-explore tree on refresh, manage transitive inclusions

**Goal:** When sync/refresh runs, tree-tracked items re-explore their parent chain and children from ADO. New ADO children appear automatically; removed children are trimmed.

**Prerequisites:** #1956 (tracking repository and service must exist)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1957.1 | **TrackingService.SyncTrackedTreesAsync:** Implement tree re-exploration logic. For each `Tree` mode item: call `SyncCoordinator.SyncItemAsync()` to refresh the root, then `SyncCoordinator.SyncChildrenAsync()` to re-explore children. Handle "not found" (item deleted in ADO) by auto-untracking. | `TrackingService.cs` | M |
| T-1957.2 | **RefreshOrchestrator integration:** Call `TrackingService.SyncTrackedTreesAsync()` after main refresh fetch completes, before ancestor hydration. Wire `TrackingService` dependency into `RefreshOrchestrator` constructor. Update `CommandServiceModule` registration. | `RefreshOrchestrator.cs`, `CommandServiceModule.cs` | M |
| T-1957.3 | **WorkingSet integration:** Add `TrackedItemIds` to `WorkingSet`. Modify `WorkingSetService.ComputeAsync()` to query tracked item IDs from `ITrackingRepository` and include them. Write unit tests. | `WorkingSet.cs`, `WorkingSetService.cs`, `WorkingSetServiceTests.cs`, `WorkingSetAllIdsTests.cs` | M |
| T-1957.4 | **MCP workspace tool:** Extend `twig_workspace` MCP tool in `ReadTools.cs` to include tracked items in the response. | `ReadTools.cs` | S |

**Acceptance Criteria:**
- [ ] `twig sync` re-explores tree-tracked items: refreshes root, fetches children
- [ ] New children added in ADO appear in workspace after sync
- [ ] Children removed in ADO no longer appear after sync
- [ ] Tracked item deleted in ADO is auto-untracked
- [ ] Tracked item IDs are included in `WorkingSet.AllIds` for sync coordination
- [ ] MCP `twig_workspace` tool includes tracked items

### Issue #1958: Implement auto-cleanup policies for tracked items on completion

**Goal:** Add configurable auto-cleanup policies that remove items from tracking when they reach a completed state, optionally also requiring the item to be in a past iteration.

**Prerequisites:** #1956 (tracking persistence), #1957 (sync integration — cleanup runs during sync)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1958.1 | **TrackingService.ApplyCleanupPolicyAsync:** Implement policy evaluation. Load all tracked items, resolve each item's state category via `StateCategoryResolver` using process type entries from `IProcessTypeStore`. For `on-complete`: untrack if `Completed`. For `on-complete-and-past`: untrack if `Completed` AND item's iteration path < current iteration. Use `ITrackingRepository.RemoveTrackedBatchAsync()` for efficient removal. | `TrackingService.cs` | M |
| T-1958.2 | **RefreshOrchestrator cleanup hook:** Call `TrackingService.ApplyCleanupPolicyAsync()` after tree sync completes during refresh. Pass current iteration and cleanup policy from config. | `RefreshOrchestrator.cs` | S |
| T-1958.3 | **Tests:** Unit tests for cleanup policy evaluation: `None` does nothing, `OnComplete` removes completed items, `OnCompleteAndPast` requires both conditions. Test with different process templates (state names resolved via `StateCategoryResolver`). | `TrackingServiceTests.cs` | M |

**Acceptance Criteria:**
- [ ] Default policy is `none` — no auto-removal
- [ ] `twig config tracking.cleanuppolicy on-complete` enables completion-based cleanup
- [ ] `twig config tracking.cleanuppolicy on-complete-and-past` enables completion+iteration cleanup
- [ ] Cleanup runs during `twig sync` after tree re-exploration
- [ ] Cleanup is process-agnostic (works with Agile/Scrum/CMMI/Basic)
- [ ] Cleanup logs removed items to stderr (informational)

---

## PR Groups

| PG | Name | Tasks | Type | Est. LoC | Est. Files | Predecessor |
|----|------|-------|------|----------|------------|-------------|
| **PG-1** | Tracking foundation: schema, repository, value objects, service, commands | T-1956.1, T-1956.2, T-1956.3, T-1956.4, T-1956.5 | Deep | ~1200 | ~25 | — |
| **PG-2** | Sync integration: tree re-exploration, working set, MCP, auto-cleanup | T-1957.1, T-1957.2, T-1957.3, T-1957.4, T-1958.1, T-1958.2, T-1958.3 | Deep | ~800 | ~15 | PG-1 |

**PG-1** delivers the complete user-facing track/untrack/exclude experience — all 5 commands work, items appear in workspace, exclusions filter sprint items. This is independently shippable.

**PG-2** adds the "smart" behaviors: tree re-exploration on sync, transitive inclusion management, working set integration, MCP exposure, and auto-cleanup policies. Depends on PG-1 for the repository and service foundations.

---

## References

- Epic #1945: Workspace Modes & Tracking (parent design context)
- `StateCategoryResolver` — process-agnostic state classification
- `SqliteCacheStore` schema versioning pattern (8 prior version bumps)
- `SyncCoordinator.SyncChildrenAsync()` — existing tree exploration primitive
- ConsoleAppFramework compound command pattern (`[Command("workspace track")]`)

