---
work_item_id: 1950
title: "Recent Mode"
type: Issue
---

# Recent Mode — Solution Design & Implementation Plan

| Field | Value |
|---|---|
| **Work Item** | #1950 (Issue) |
| **Child Issue** | #1963 — Implement recent mode: time window, who-changed filters, and caching strategy |
| **Author** | Copilot |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

Recent Mode introduces an activity-based workspace population strategy to the twig CLI. Instead of scoping workspace items by the current sprint iteration, recent mode shows work items changed within a configurable time window (default 14 days), with flexible who-changed filters: **user** (items the active user touched), **people** (items changed by a configured list), **team** (items changed by any team member), or **anyone** (all recently changed items). The feature leverages WIQL's `EVER` operator for the "user touched" semantic — catching items the user modified even if someone else changed them more recently — and caches results with TTL-based invalidation to keep performance under the 3-second target. Configuration persists to the existing `.twig/config` JSON and the SQLite context store.

## Background

### Current Architecture

The twig workspace is populated by a two-phase pattern:

1. **Refresh phase** (`RefreshCommand`): Builds a WIQL query scoped to `[System.IterationPath] = '<current sprint>'` (plus area path filters from config), executes against ADO REST, and saves results to the local SQLite cache. The `RefreshOrchestrator` handles fetch → conflict detection → batch save → ancestor hydration → working set sync.

2. **Display phase** (`WorkspaceCommand`): Reads items from the local SQLite cache via `IWorkItemRepository.GetByIterationAndAssigneeAsync()` (or `GetByIterationAsync()` for team view), combined with active context and seeds. A stale-while-revalidate pattern refreshes the cache in the background when `last_refreshed_at` exceeds `CacheStaleMinutes`.

Key components in the current flow:

| Component | Role |
|-----------|------|
| `WorkingSetService` | Computes the working set from local cache (iteration-scoped queries) |
| `WorkingSet` | Value object with `SprintItemIds`, `ActiveItemId`, `SeedIds`, etc. |
| `WiqlQueryBuilder` | Builds WIQL from `QueryParameters` (supports `ChangedSinceDays`) |
| `RefreshOrchestrator` | Fetch → save → hydrate → sync pipeline |
| `WorkspaceCommand` | Streams workspace data with async Spectre rendering |
| `Workspace` | Read model combining context item, sprint items, and seeds |
| `IContextStore` | Key-value persistence for active context and settings |

### WIQL Capabilities for Recent Mode

Research into ADO's WIQL engine reveals critical operators:

- **`EVER` operator**: `EVER [System.ChangedBy] = 'user'` scans all revisions to find items where the field EVER held that value. This directly solves the "user touched" semantic without requiring the expensive Work Item Updates API.
- **`@Me` macro**: Resolves to the authenticated user, avoiding hardcoded display names in WIQL.
- **`@Today - N` macro**: Date arithmetic for time windows.
- **`In Group` operator**: `[System.ChangedBy] In Group '[Project]\Team'` matches against ADO security groups/teams. Only checks the current (latest) `ChangedBy` value, not historical.
- **32K character limit**: WIQL queries cannot exceed 32,768 characters.

### Performance Analysis: WIQL vs Revision API

| Approach | Accuracy | Performance | Complexity |
|----------|----------|-------------|------------|
| `[System.ChangedBy] = @Me AND [System.ChangedDate] >= @Today - N` | Low — only latest changer | Fast (single WIQL) | Low |
| `EVER [System.ChangedBy] = @Me AND [System.ChangedDate] >= @Today - N` | Good — catches any historical touch by user on recently-active items | Fast (single WIQL, server-side revision scan) | Low |
| Work Item Updates API per item | Exact — scan revisions within window | Slow — N+1 API calls | High |

**Decision**: Use `EVER` + `ChangedDate` as the primary strategy. It provides the right semantic ("items I touched that are still active") in a single WIQL query. The slight over-inclusion (user touched 3 years ago, someone else changed this week) is actually useful — it surfaces items the user has context on that are currently active.

### Call-Site Audit

Recent mode modifies cross-cutting patterns (working set computation, workspace rendering, refresh orchestration). The following audit identifies all call sites that will be impacted:

| File | Method | Current Usage | Impact |
|------|--------|--------------|--------|
| `WorkspaceCommand.cs` | `StreamWorkspaceData()` | Queries `GetByIterationAndAssigneeAsync` | Must branch on recent mode to use recent item IDs |
| `WorkspaceCommand.cs` | `ExecuteSyncAsync()` | Queries `GetByIterationAndAssigneeAsync` | Same branching needed |
| `RefreshCommand.cs` | `ExecuteCoreAsync()` | Builds iteration-scoped WIQL | Must use `RecentModeQueryBuilder` when recent mode active |
| `WorkingSetService.cs` | `ComputeAsync()` | Queries `GetByIterationAndAssigneeAsync` | Must support recent mode item source |
| `WorkingSet.cs` | `SprintItemIds` | Used by `SyncCoordinator.SyncWorkingSetAsync` | Recent items flow through same property |
| `Workspace.cs` | `Build()` | Takes sprint items list | Recent items passed as sprint items (same shape) |
| `TwigConfiguration.cs` | `SetValue()` | Switch on known paths | Add `recent.*` paths |
| `TwigJsonContext.cs` | Serializable types | 94 attributes | Add `RecentConfig` |
| `Program.cs` | `Workspace()` | Routes to `WorkspaceCommand` | No change needed (flags passed through) |

## Problem Statement

The twig workspace is currently iteration-scoped: it shows items assigned to the current sprint. This has two significant limitations:

1. **Activity blindness**: Items the user actively worked on last week that moved to a different iteration (or have no iteration) disappear from the workspace, even though they represent recent context.
2. **No cross-iteration visibility**: Users working across sprints or on items without iteration paths have no way to see their recent activity in a single view.
3. **Team activity gaps**: There's no way to see what the team has been working on recently across iterations — only the current sprint's snapshot.

Recent mode solves these by defining workspace membership based on *change activity* rather than *iteration assignment*, with configurable filters for who made the changes.

## Goals and Non-Goals

### Goals

1. **G-1**: Populate workspace with items changed within a configurable time window (default 14 days)
2. **G-2**: Support four who-changed filter modes: `user` (default), `people`, `team`, `anyone`
3. **G-3**: Achieve < 3 second response time for the `user` filter mode on typical workspaces
4. **G-4**: Cache results with configurable TTL to avoid repeated expensive queries
5. **G-5**: Persist all recent mode configuration to `.twig/config` JSON
6. **G-6**: Integrate with existing workspace rendering (table, hierarchy, hints, JSON output)

### Non-Goals

- **NG-1**: Real-time streaming of changes (this is a point-in-time query, not a subscription)
- **NG-2**: Revision-level detail (showing exactly which fields changed) — this is a workspace population feature, not an audit log
- **NG-3**: Custom WIQL filter composition beyond the four who-changed modes
- **NG-4**: Replacing the default sprint-based workspace — recent mode is an opt-in alternative
- **NG-5**: UI for switching between sprint and recent modes in-session (configuration only)

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | When `recent.enabled` is `true` in config, workspace shows recently changed items instead of sprint items |
| FR-02 | `recent.windowDays` configures the lookback window (default: 14, range: 1–90) |
| FR-03 | `recent.whoChanged` accepts values: `user`, `people`, `team`, `anyone` (default: `user`) |
| FR-04 | `user` mode uses WIQL `EVER [System.ChangedBy]` to find items the authenticated user touched within the window |
| FR-05 | `people` mode queries against a configured list of display names (`recent.people`) |
| FR-06 | `team` mode uses WIQL `[System.ChangedBy] In Group` against the configured team |
| FR-07 | `anyone` mode queries all items changed within the window (scoped by area paths) |
| FR-08 | Recent mode results are cached in the SQLite context store with a configurable TTL |
| FR-09 | `twig config recent.enabled true` enables recent mode; `twig config recent.windowDays 7` sets window |
| FR-10 | `twig workspace` renders recent items using the same table/hierarchy format as sprint items |
| FR-11 | Area path scoping from config (`defaults.areaPaths`) applies to all recent mode queries |
| FR-12 | The workspace header/indicator changes to show "Recent (N days)" instead of the sprint name |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | `user` mode query + render completes in < 3 seconds for workspaces with ≤ 200 items |
| NFR-02 | Cache TTL defaults to `display.cacheStaleMinutes` (currently 5 minutes) |
| NFR-03 | No reflection — all new types registered in `TwigJsonContext` |
| NFR-04 | AOT-compatible — no dynamic code generation |
| NFR-05 | Process-agnostic — no hardcoded state/type names in query logic |
| NFR-06 | All new code follows `sealed class` / `sealed record` conventions |

## Proposed Design

### Architecture Overview

Recent mode introduces a parallel workspace population path alongside the existing sprint-based path. The mode is selected by configuration (`recent.enabled`), and when active, replaces the iteration-scoped WIQL query with a time-window + who-changed WIQL query. Cached results flow through the existing `WorkingSet` → `Workspace` → rendering pipeline unchanged.

```
┌─────────────────────┐     ┌──────────────────────┐
│  TwigConfiguration  │     │   IContextStore       │
│  recent.enabled     │────▶│  recent_item_ids      │
│  recent.windowDays  │     │  recent_last_queried  │
│  recent.whoChanged  │     └──────────┬───────────┘
│  recent.people      │               │
└────────┬────────────┘               │
         │                            │
         ▼                            ▼
┌────────────────────────┐  ┌──────────────────────────┐
│ RecentModeQueryBuilder │  │  RecentModeService        │
│ Generates WIQL per     │──│  Orchestrates query +     │
│ who-changed mode       │  │  caching + TTL            │
└────────────────────────┘  └──────────┬───────────────┘
                                       │
                            ┌──────────▼───────────────┐
                            │  WorkspaceCommand         │
                            │  Branches on recent mode  │
                            │  Uses recent items as     │
                            │  "sprint items" slot      │
                            └──────────────────────────┘
```

### Key Components

#### 1. `RecentConfig` (Configuration POCO)

New configuration section added to `TwigConfiguration`:

```csharp
public sealed class RecentConfig
{
    public bool Enabled { get; set; }
    public int WindowDays { get; set; } = 14;
    public string WhoChanged { get; set; } = "user";
    public List<string>? People { get; set; }
}
```

Added to `TwigConfiguration` as `public RecentConfig Recent { get; set; } = new();`

Configuration paths for `twig config`:
- `recent.enabled` → bool
- `recent.windowdays` → int (1–90)
- `recent.whochanged` → string enum (`user`, `people`, `team`, `anyone`)
- `recent.people` → semicolon-separated display names

#### 2. `WhoChangedFilter` (Enum)

```csharp
public enum WhoChangedFilter { User, People, Team, Anyone }
```

Parsed from the `recent.whoChanged` config string with case-insensitive matching.

#### 3. `RecentModeQueryBuilder` (WIQL Generator)

Static service that generates WIQL queries for each who-changed mode. Follows the same static pattern as `WiqlQueryBuilder`:

| Mode | WIQL Pattern |
|------|-------------|
| `user` | `EVER [System.ChangedBy] = '{user}' AND [System.ChangedDate] >= @Today - {N}` |
| `people` | `(EVER [System.ChangedBy] = '{p1}' OR EVER [System.ChangedBy] = '{p2}' ...) AND [System.ChangedDate] >= @Today - {N}` |
| `team` | `[System.ChangedBy] In Group '[{project}]\{team}' AND [System.ChangedDate] >= @Today - {N}` |
| `anyone` | `[System.ChangedDate] >= @Today - {N}` |

All modes also append area path filters from config (same logic as `RefreshCommand`) and are ordered by `[System.ChangedDate] DESC`.

**Design Decision — `EVER` for user/people, `In Group` for team**:
- The `EVER` operator scans revisions server-side, catching items the user touched even if someone else is the current `ChangedBy`. This is the "user touched" semantic the spec requires.
- For `team` mode, `In Group` operates only on the current `ChangedBy` value. Using `EVER` with `In Group` is not supported by WIQL. Fetching all team members and building N `EVER` clauses risks hitting the 32K WIQL limit for large teams. The `In Group` compromise is acceptable because team mode answers "what's active in the team" rather than "what did each team member touch."
- For `people` mode, we use `EVER` because the list is explicitly configured and bounded.

#### 4. `RecentModeService` (Orchestrator)

Domain service that coordinates recent mode query execution with caching:

```csharp
public sealed class RecentModeService(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    TwigConfiguration config)
{
    public async Task<IReadOnlyList<int>> GetRecentItemIdsAsync(
        bool forceRefresh = false, CancellationToken ct = default);
    
    public async Task RefreshRecentItemsAsync(CancellationToken ct = default);
}
```

**Cache strategy**:
- Stores recent item IDs as a JSON int array in `IContextStore` under key `recent_item_ids`
- Stores query timestamp under key `recent_last_queried_at`
- TTL check uses `display.cacheStaleMinutes` (default 5 minutes)
- `GetRecentItemIdsAsync()` returns cached IDs if fresh, otherwise refreshes
- `RefreshRecentItemsAsync()` always re-queries ADO, updates cache, and batch-saves fetched items to the work item repository

**Query execution flow**:
1. Build WIQL via `RecentModeQueryBuilder`
2. Execute `adoService.QueryByWiqlAsync(wiql, top: 200)` — capped at 200 to bound response time
3. Fetch full work items via `adoService.FetchBatchAsync(ids)`
4. Save to local cache via `workItemRepo.SaveBatchAsync(items)`
5. Store IDs in context store
6. Return IDs

### Data Flow

#### Workspace Command — Recent Mode Active

```
WorkspaceCommand.ExecuteAsync()
  │
  ├── Check config.Recent.Enabled
  │     └── true → recent mode path
  │
  ├── Stage 1: Context (unchanged)
  │     └── ActiveItemResolver.ResolveByIdAsync()
  │
  ├── Stage 2: Recent items (replaces sprint items)
  │     └── RecentModeService.GetRecentItemIdsAsync()
  │           ├── Cache hit → return cached IDs
  │           └── Cache miss → query ADO → save → return IDs
  │     └── workItemRepo.GetByIdsAsync(recentIds)
  │
  ├── Stage 3: Seeds (unchanged)
  │
  └── Stage 4: Stale-while-revalidate (adapted)
        └── Uses RecentModeService.RefreshRecentItemsAsync()
```

#### Refresh/Sync Command — Recent Mode Active

When `twig sync` runs with recent mode enabled:
1. Executes the standard iteration-scoped refresh (unchanged — sprint data stays in cache)
2. Additionally calls `RecentModeService.RefreshRecentItemsAsync()` to update recent mode cache
3. This ensures both sprint and recent data are fresh

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Use WIQL `EVER` operator for user/people filter | Server-side revision scan is fast and provides the "user touched" semantic without N+1 API calls |
| DD-02 | Cap recent query results at 200 items | Bounds response time and avoids overwhelming the workspace table |
| DD-03 | Store recent item IDs in context store (not a new table) | Reuses existing key-value infrastructure; IDs are small and ephemeral |
| DD-04 | Feed recent items through the `SprintItems` slot in `Workspace` | Avoids modifying `Workspace`, `WorkingSet`, or renderer contracts |
| DD-05 | Default window of 14 days | 7 days misses items from before a long weekend; 14 days covers a full sprint cycle |
| DD-06 | Recent mode is opt-in via `recent.enabled` | Sprint-based workspace remains the default; no breaking changes |
| DD-07 | Use `@Me` WIQL macro when user display name is unavailable | Falls back to config `user.displayName` for `EVER` clause when display name is known |
| DD-08 | Area paths from `defaults.areaPaths` apply to all recent mode queries | Prevents unscoped queries from returning items across the entire organization |

## Alternatives Considered

### Alternative 1: Work Item Updates API for per-revision scanning

Query `/wit/workitems/{id}/updates` for each candidate item to check revision-level `ChangedBy` within the time window.

**Pros**: Exact semantics — only items where the user made a change within the window.
**Cons**: Requires N+1 API calls (1 per candidate item), impractical for > 50 items, dramatically exceeds the 3-second performance target.

**Decision**: Rejected. The `EVER` operator provides sufficient accuracy with a single WIQL query.

### Alternative 2: Client-side revision filtering via `$expand=all` on work items

Fetch work items with expanded revisions and filter client-side.

**Pros**: Full accuracy with revision-level data.
**Cons**: Massive payload size (each revision includes all fields), slow download, high memory usage.

**Decision**: Rejected. Disproportionate resource consumption for minimal accuracy gain.

### Alternative 3: New SQLite table for recent mode cache

Create a `recent_items` table with `work_item_id`, `queried_at`, `who_changed_filter` columns.

**Pros**: More structured than key-value storage, supports per-filter caching.
**Cons**: Schema version bump required, adds migration complexity, over-engineered for storing a list of IDs with a timestamp.

**Decision**: Rejected. The context store's key-value pattern is sufficient and avoids schema changes.

## Dependencies

### External Dependencies

| Dependency | Description | Status |
|------------|-------------|--------|
| ADO REST API 7.1 | WIQL `EVER` operator support | Available (documented in WIQL syntax reference) |
| ADO REST API 7.1 | `@Me` macro in WIQL | Available |
| ADO REST API 7.1 | `In Group` operator for identity fields | Available |

### Internal Dependencies

| Dependency | Description |
|------------|-------------|
| `IAdoWorkItemService` | WIQL query execution and batch fetch |
| `IWorkItemRepository` | Local cache read/write |
| `IContextStore` | Key-value cache for recent item IDs and timestamps |
| `TwigConfiguration` | Config loading and `SetValue` paths |
| `TwigJsonContext` | Source-generated JSON for `RecentConfig` |

### Sequencing Constraints

None — this feature is self-contained and doesn't depend on other in-flight work.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `EVER` operator performance degrades for users with extensive history (thousands of revisions) | Low | Medium | The `ChangedDate` filter limits the result set; ADO indexes revision data. Monitor performance during testing. |
| `In Group` for team mode misses items where a team member changed an item but someone outside the team changed it after | Medium | Low | Acceptable trade-off documented in DD-07. Team mode answers "what's active" not "who touched what." |
| `people` mode with many names exceeds WIQL 32K limit | Low | Medium | Validate name count against estimated WIQL length; warn and truncate if approaching limit. |
| Config migration for users upgrading from older twig versions | Low | Low | `RecentConfig` defaults to `Enabled = false`, so existing users see no behavior change. |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should `twig workspace --recent` be a CLI flag override independent of `recent.enabled` config? | Low | Could be useful for one-off recent views without changing config. Recommend yes — add `--recent` flag that enables recent mode for the current invocation regardless of config. |
| OQ-2 | Should `@Me` WIQL macro be preferred over `config.User.DisplayName` for the user filter? | Low | `@Me` resolves server-side and is always accurate. Falls back to display name only if needed for `EVER` clause (which requires a literal value, not a macro — need to verify). |
| OQ-3 | Should the 200-item cap be configurable? | Low | Could add `recent.maxItems` config path. Default of 200 is likely sufficient. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Enums/WhoChangedFilter.cs` | Enum for the four who-changed filter modes |
| `src/Twig.Domain/Services/RecentModeQueryBuilder.cs` | Static WIQL query generator for recent mode |
| `src/Twig.Domain/Services/RecentModeService.cs` | Orchestrator for recent mode queries with caching |
| `tests/Twig.Domain.Tests/Services/RecentModeQueryBuilderTests.cs` | Unit tests for WIQL generation |
| `tests/Twig.Domain.Tests/Services/RecentModeServiceTests.cs` | Unit tests for service orchestration and caching |
| `tests/Twig.Domain.Tests/Enums/WhoChangedFilterTests.cs` | Unit tests for enum parsing |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `RecentConfig` class and `Recent` property; add `SetValue` paths for `recent.*` |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable(typeof(RecentConfig))]` |
| `src/Twig/Commands/WorkspaceCommand.cs` | Branch on `config.Recent.Enabled` to use `RecentModeService` for item population |
| `src/Twig/Commands/RefreshCommand.cs` | Add recent mode refresh alongside iteration refresh when enabled |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `RecentModeService` |
| `src/Twig/Program.cs` | Add `--recent` flag to workspace/ws commands |
| `src/Twig.Domain/Services/WorkingSetService.cs` | Add overload/parameter to accept recent item IDs instead of iteration-based query |

## ADO Work Item Structure

### Issue #1963: Implement recent mode: time window, who-changed filters, and caching strategy

**Goal**: Deliver the complete recent mode feature — configuration, query generation, caching, and CLI integration — enabling activity-based workspace population.

**Prerequisites**: None (self-contained feature)

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-1 | **Configuration model and persistence**: Create `RecentConfig` POCO with `Enabled`, `WindowDays`, `WhoChanged`, `People` properties. Add to `TwigConfiguration` as `Recent` property. Add `SetValue` switch cases for `recent.enabled`, `recent.windowdays`, `recent.whochanged`, `recent.people`. Register in `TwigJsonContext`. Add validation (1–90 days range, valid enum values). | `TwigConfiguration.cs`, `TwigJsonContext.cs` | ~120 LoC |
| T-2 | **WhoChangedFilter enum and parsing**: Create `WhoChangedFilter` enum (`User`, `People`, `Team`, `Anyone`). Add `Parse(string)` method with case-insensitive matching returning `Result<WhoChangedFilter>`. Add unit tests for all parse cases including invalid input. | `WhoChangedFilter.cs`, `WhoChangedFilterTests.cs` | ~80 LoC |
| T-3 | **RecentModeQueryBuilder**: Implement static WIQL query builder for recent mode. Generates WIQL per who-changed filter mode with `EVER` for user/people, `In Group` for team, date-only for anyone. Includes area path filter composition (reusing pattern from `RefreshCommand`). WIQL string escaping for user names. Comprehensive unit tests for all four modes, area path variations, and edge cases. | `RecentModeQueryBuilder.cs`, `RecentModeQueryBuilderTests.cs` | ~250 LoC |
| T-4 | **RecentModeService**: Create orchestrator service with `GetRecentItemIdsAsync()` and `RefreshRecentItemsAsync()`. Implements TTL-based caching using `IContextStore` keys `recent_item_ids` and `recent_last_queried_at`. Handles ADO query execution, batch fetch, cache save. Unit tests for cache hit/miss, refresh, error handling. | `RecentModeService.cs`, `RecentModeServiceTests.cs` | ~300 LoC |
| T-5 | **WorkspaceCommand integration**: Modify both async (Spectre) and sync paths to branch on `config.Recent.Enabled`. When active, use `RecentModeService.GetRecentItemIdsAsync()` + `workItemRepo.GetByIdsAsync()` instead of iteration-based queries. Update workspace header to show "Recent (N days)" indicator. Add `--recent` flag to `Program.cs` workspace/ws/sprint routes as a per-invocation override. Register `RecentModeService` in `CommandServiceModule.cs`. | `WorkspaceCommand.cs`, `Program.cs`, `CommandServiceModule.cs` | ~200 LoC |
| T-6 | **RefreshCommand integration**: When `config.Recent.Enabled` is true, additionally call `RecentModeService.RefreshRecentItemsAsync()` after the standard iteration refresh. Ensures `twig sync` populates the recent mode cache. | `RefreshCommand.cs` | ~50 LoC |

**Acceptance Criteria**:
- [ ] `twig config recent.enabled true` enables recent mode; subsequent `twig workspace` shows recently changed items
- [ ] `twig config recent.windowdays 7` changes the lookback window
- [ ] `twig config recent.whochanged user` shows items the authenticated user touched
- [ ] `twig config recent.whochanged anyone` shows all recently changed items in configured area paths
- [ ] `twig config recent.people "Alice;Bob"` configures people list for people mode
- [ ] `twig workspace --recent` enables recent mode for a single invocation regardless of config
- [ ] Workspace renders with "Recent (N days)" indicator when in recent mode
- [ ] Cache is used on repeated `twig workspace` calls within TTL window
- [ ] `twig sync` refreshes both sprint and recent mode caches when enabled
- [ ] All new types registered in `TwigJsonContext`
- [ ] `dotnet build` succeeds with zero warnings
- [ ] All new and existing tests pass

## PR Groups

### PG-1: Recent Mode Foundation

**Tasks**: T-1 (Config), T-2 (Enum), T-3 (Query Builder)
**Classification**: Deep — few files, complex WIQL generation logic
**Estimated LoC**: ~450
**Estimated Files**: ~8 (3 new + 2 modified + 3 test files)
**Successor**: PG-2

**Description**: Establishes the configuration model, enum, and WIQL query generation for recent mode. Self-contained and testable in isolation — no command changes, no service orchestration. All new types are registered for JSON serialization.

**Review focus**: WIQL correctness (especially `EVER` operator usage), config validation, edge cases in query builder.

### PG-2: Service Layer and Command Integration

**Tasks**: T-4 (Service), T-5 (Workspace integration), T-6 (Refresh integration)
**Classification**: Deep — touches orchestration logic and command branching
**Estimated LoC**: ~550
**Estimated Files**: ~8 (2 new + 4 modified + 2 test files)
**Predecessor**: PG-1

**Description**: Builds the `RecentModeService` orchestrator with caching, integrates into `WorkspaceCommand` (both async and sync paths), and hooks into `RefreshCommand` for cache population. Adds the `--recent` CLI flag.

**Review focus**: Cache correctness (TTL, invalidation), workspace branching logic, error handling for ADO failures, rendering correctness.

## References

- [WIQL Syntax Reference — EVER operator](https://learn.microsoft.com/azure/devops/boards/queries/wiql-syntax?view=azure-devops#more-query-examples)
- [WIQL — In Group operator](https://learn.microsoft.com/azure/devops/boards/queries/query-operators-variables?view=azure-devops#query-operators)
- [Query work item history and discussion fields](https://learn.microsoft.com/azure/devops/boards/queries/history-and-auditing?view=azure-devops)
- [ADO REST API 7.1 — WIQL Query](https://learn.microsoft.com/rest/api/azure/devops/wit/wiql/query-by-wiql)
- [Historical data representation in Analytics — revision filtering](https://learn.microsoft.com/azure/devops/report/powerbi/analytics-historical-filtering?view=azure-devops)

