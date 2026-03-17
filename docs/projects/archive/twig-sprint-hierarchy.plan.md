# Sprint Hierarchy View — Solution Design & Implementation Plan

**Revision**: 3  
**Revision Notes**: Addresses technical review feedback (score 80/100). See [Revision Notes](#revision-notes) at end of document.  
**Date**: 2026-03-16  
**Author**: Copilot (Principal Software Architect)

---

## Executive Summary

This document proposes adding hierarchical display to the sprint view (`FormatSprintView`) in the Twig CLI. Today, the `twig sprint` command renders sprint items as a flat list grouped by assignee. This design adds parent-chain context within each assignee group — nesting sprint items under their parent work items (Features, User Stories, etc.) using box-drawing characters, consistent with the existing `FormatTree` visual language. The approach is "Assignee-first, hierarchy within": assignee grouping remains the primary axis; hierarchy is a secondary, visual enhancement within each group. A new `SprintHierarchy` read model encapsulates the tree-building logic, and a `CeilingComputer` utility determines how far up the parent chain to display. `ProcessConfigurationData` is persisted to SQLite during `twig refresh` to avoid network calls at display time. JSON and Minimal formatters remain flat. All changes are AOT-compatible.

> **Implementation Progress**: EPIC-0 (ProcessConfigurationData Caching) is COMPLETE. EPIC-1 (CeilingComputer return type) and EPIC-2 (SprintHierarchy read model) are fully implemented and tested — code on disk, build passing, all tests green. EPIC-3 (Workspace Model Extension) is fully implemented and tested. EPIC-4 (WorkspaceCommand Integration) is COMPLETE — all 13 tests passing. EPIC-5 (Hierarchical Formatter Rendering) is COMPLETE — all 1,150 tests passing (11 new hierarchy formatter tests + all existing tests green).

---

## Background

### Current State

The Twig CLI provides a sprint view via `twig sprint` (alias for `twig workspace --all`). This view:

1. Groups sprint items by assignee (lines 212–239 of `HumanOutputFormatter.cs`)
2. Renders each item as a flat line: `marker badge #ID Title [State] dirty`
3. Shows no parent context — a Task sits at the same visual level as a User Story

The tree view (`FormatTree`, lines 74–125) already renders parent chains with box-drawing characters and dimmed parent context. However, `FormatTree` is scoped to a single focused item with its parent chain and children — not a batch view.

### Motivation

In real sprints, teams work on Tasks and User Stories that belong to Features and Epics. Without hierarchy, the sprint view lacks context:
- A developer cannot see which Feature their Tasks belong to
- Shared parent context (e.g., three User Stories under the same Feature) is invisible
- The view cannot convey structural relationships between work items

### Prior Art in the Codebase

| Component | Location | Relevance |
|-----------|----------|-----------|
| `FormatTree` parent chain | `HumanOutputFormatter.cs:80–87` | Dimmed parent rendering with colored badges |
| `FormatTree` children | `HumanOutputFormatter.cs:98–116` | Box-drawing (`├──`, `└──`) for nested children |
| `BacklogHierarchyService.InferParentChildMap` | `BacklogHierarchyService.cs:18–46` | Builds level-ordered hierarchy from `ProcessConfigurationData` |
| `ProcessConfigurationData` | `ProcessConfigurationData.cs:1–27` | Backlog level definitions (Portfolio → Requirement → Task) |
| `ProcessTypeSyncService.SyncAsync` | `ProcessTypeSyncService.cs:21–51` | Already calls `GetProcessConfigurationAsync` during refresh; persists `ProcessTypeRecord` objects but NOT `ProcessConfigurationData` itself |
| `WorkItem.ParentId` | `WorkItem.cs:37` | Parent link for hierarchy traversal |
| `IWorkItemRepository.GetParentChainAsync` | `SqliteWorkItemRepository.cs:69–93` | Walks `parent_id` chain from cache; returns root→parent ordered chain |
| `RefreshCommand` ancestor hydration | `RefreshCommand.cs:112–123` | Iteratively fetches orphan parents up to 5 levels deep |
| `GetOrphanParentIdsAsync` | `SqliteWorkItemRepository.cs:136–155` | Finds parent IDs not yet in cache |
| `SqliteCacheStore.metadata` table | `SqliteCacheStore.cs:126–129` | Key-value metadata table — can store serialized `ProcessConfigurationData` |
| `BacklogLevelConfiguration.WorkItemTypeNames` | `ProcessConfigurationData.cs:25` | `IReadOnlyList<string>` — a single backlog level can contain **multiple** type names (e.g., `["User Story", "Backlog Item"]` in hybrid process templates) |

---

## Problem Statement

The sprint view (`FormatSprintView`) renders work items as a flat list, losing the hierarchical context that teams rely on to understand how Tasks relate to User Stories and Features. This makes it difficult to:

1. **See parent context**: Which Feature does this Task belong to?
2. **Identify shared scope**: Three User Stories under the same Feature are visually disconnected
3. **Distinguish hierarchy levels**: Tasks and User Stories appear at the same indentation level

---

## Goals and Non-Goals

### Goals

1. **G1**: Render sprint items hierarchically within each assignee group, showing parent context
2. **G2**: Compute a "ceiling level" so parent chains are trimmed to a useful depth
3. **G3**: Share parent context nodes — a Feature appearing once with multiple children beneath it
4. **G4**: Maintain visual consistency with `FormatTree` (box-drawing, dimmed parents, colored badges)
5. **G5**: Keep the `FormatSprintView` interface backward-compatible or minimally changed
6. **G6**: All changes must be AOT-compatible (no reflection)
7. **G7**: No network calls on the `twig sprint` hot path — all hierarchy data comes from SQLite cache

### Non-Goals

- **NG1**: Hierarchical rendering in JSON or Minimal formatters (remain flat)
- **NG2**: Hierarchical rendering in `FormatWorkspace` (personal workspace view stays flat)
- **NG3**: Interactive collapsing/expanding of hierarchy nodes
- **NG4**: Fetching parent data from ADO at display time (rely on cached data from `RefreshCommand`)
- **NG5**: Version or changelog updates

---

## Requirements

### Functional Requirements

- **FR-001**: A `CeilingComputer` static method takes sprint item type names and `ProcessConfigurationData`, returning the ceiling backlog level type names as `IReadOnlyList<string>` (all type names belonging to the ceiling level). If no level exists above the highest, returns `null`. A single backlog level can contain multiple type names (e.g., `BacklogLevelConfiguration.WorkItemTypeNames` may be `["User Story", "Backlog Item"]`), so returning a single `string?` would silently discard ceiling-level types.
- **FR-002**: A `SprintHierarchy` read model builds a tree structure per assignee from flat sprint items and their parent chains, using the ceiling level to trim.
- **FR-003**: `HumanOutputFormatter.FormatSprintView` renders each assignee group hierarchically: parent context nodes dimmed, sprint items in full color, with box-drawing nesting.
- **FR-004**: Sprint items with no parent (`ParentId == null`) appear at root level within their assignee group, unchanged from today.
- **FR-005**: Non-sprint parents (context nodes) render dimmed with colored type badge, showing title and state, but without active/dirty markers.
- **FR-006**: Active context marker (cyan `●`) and dirty marker (yellow `•`) still apply to sprint items within the hierarchy.
- **FR-007**: `WorkspaceCommand.ExecuteAsync` fetches parent chains for sprint items and builds the `SprintHierarchy` before calling the formatter. Parent chains MUST be fetched via `IWorkItemRepository.GetParentChainAsync` (not `GetByIdAsync` with manual walking).
- **FR-008**: JSON and Minimal formatters continue to work (flat) if `FormatSprintView` signature changes.
- **FR-009**: `ProcessConfigurationData` MUST be persisted to SQLite during `twig refresh` (and `twig init`) so that `WorkspaceCommand` reads it from cache — no network call to `GetProcessConfigurationAsync` on the sprint display path.
- **FR-010**: `SprintHierarchy.AssigneeGroups` MUST be ordered alphabetically by assignee name (case-insensitive), matching the existing `FormatSprintView` ordering (line 227: `.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)`).

### Non-Functional Requirements

- **NFR-001**: AOT-compatible — no reflection, no `dynamic`, no `System.Text.Json` source-gen gaps.
- **NFR-002**: Performance — parent chain data AND process configuration come from SQLite cache. Zero network calls during `twig sprint` rendering.
- **NFR-003**: All existing `SprintViewFormatterTests` must continue to pass.

---

## Proposed Design

### Architecture Overview

```
RefreshCommand.ExecuteAsync (or InitCommand)
    │
    ├── ... existing refresh logic ...
    ├── ProcessTypeSyncService.SyncAsync(...)     ← already calls GetProcessConfigurationAsync
    │     └── persist ProcessConfigurationData    ← NEW: serialize to metadata table
    └── ... rest of refresh ...

WorkspaceCommand.ExecuteAsync(all: true)
    │
    ├── Fetch sprint items (existing)
    ├── Fetch parent chains for sprint items             ← NEW (from SQLite cache)
    ├── Read ProcessConfigurationData from metadata      ← NEW (from SQLite cache, NO network call)
    ├── CeilingComputer.Compute(types, config)           ← NEW
    ├── SprintHierarchy.Build(items, parents, ceilingTypes) ← NEW
    │
    └── fmt.FormatSprintView(workspace, staleDays)
            │
            └── HumanOutputFormatter: render hierarchically (via ws.Hierarchy)
            └── JsonOutputFormatter:  render flat (ignore ws.Hierarchy)
            └── MinimalOutputFormatter: render flat (ignore ws.Hierarchy)
```

### Key Components

#### 1. `CeilingComputer` (static utility class)

**Location**: `src/Twig.Domain/Services/CeilingComputer.cs`

**Responsibility**: Given a set of work item type names present in the sprint and the `ProcessConfigurationData` (backlog hierarchy), compute the ceiling type names — **all type names** belonging to the backlog level one above the highest level represented in the sprint items.

> **Design note (Issue 1 fix)**: `BacklogLevelConfiguration.WorkItemTypeNames` is `IReadOnlyList<string>` — a single backlog level (e.g., RequirementBacklog) can contain multiple type names such as `["User Story", "Backlog Item"]` in Scrum templates or hybrid processes. Returning `string?` would silently discard all but one type from the ceiling level. For example, if the ceiling level is RequirementBacklog with types `["User Story", "Backlog Item"]`, and a sprint item's parent is a `Backlog Item`, a `string?` return of `"User Story"` would cause that parent to be invisible, making the child item incorrectly appear at root level.

```
Input:  ["Task", "User Story"], ProcessConfigurationData (Epic > Feature > User Story > Task)
Output: ["Feature"] (one level above "User Story", the highest sprint item level)

Input:  ["Task"], ProcessConfigurationData (Epic > Feature > [User Story, Backlog Item] > Task)
Output: ["User Story", "Backlog Item"] (all types from the requirement level, one above task)

Input:  ["Epic"], ProcessConfigurationData (Epic > Feature > User Story > Task)
Output: null (no level above Epic)
```

**Algorithm**:
1. Build an ordered list of backlog levels from `ProcessConfigurationData`: `PortfolioBacklogs[0..N]` + `RequirementBacklog` + `TaskBacklog` (top to bottom).
2. For each level, collect all work item type names.
3. Find the highest level index where any sprint item type name matches.
4. If that index is 0 (top level) or no match found, return `null`.
5. Otherwise, return **all** type names from `levels[highestIndex - 1].WorkItemTypeNames`.

**Returns**: `IReadOnlyList<string>?` — all type names in the ceiling level, or `null` if no parent context is needed.

**Signature change** (existing `CeilingComputer.cs` at line 21):
```csharp
// Before:
public static string? Compute(IReadOnlyList<string>? sprintItemTypeNames, ProcessConfigurationData? config)

// After:
public static IReadOnlyList<string>? Compute(IReadOnlyList<string>? sprintItemTypeNames, ProcessConfigurationData? config)
```

#### 2. `SprintHierarchyNode` (tree node record)

**Location**: `src/Twig.Domain/ReadModels/SprintHierarchy.cs`

**Responsibility**: Represents a node in the sprint hierarchy tree. Each node wraps a `WorkItem` and indicates whether it's a sprint item (in-scope) or a context-only parent.

```csharp
public sealed class SprintHierarchyNode
{
    public WorkItem Item { get; }
    public bool IsSprintItem { get; }           // true = in the sprint; false = parent context only
    public List<SprintHierarchyNode> Children { get; }
}
```

#### 3. `SprintHierarchy` (read model)

**Location**: `src/Twig.Domain/ReadModels/SprintHierarchy.cs`

**Responsibility**: Builds and holds the per-assignee hierarchical tree from flat sprint items, their parent chains, and the ceiling level.

> **Design note (Issue 1 fix)**: `ceilingTypeNames` is `IReadOnlyList<string>?` (not `string?`) to match the multi-type ceiling level from `CeilingComputer`.

> **Design note (Issue 4 fix)**: `AssigneeGroups` MUST be ordered alphabetically by assignee name (case-insensitive). The `Build` method uses a `SortedDictionary<string, ...>(StringComparer.OrdinalIgnoreCase)` internally, matching the existing `FormatSprintView` ordering (line 227: `.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)`). This ensures the formatter can iterate `AssigneeGroups` directly without re-sorting.

```csharp
public sealed class SprintHierarchy
{
    // Ordered alphabetically by assignee name (case-insensitive) — see FR-010
    public IReadOnlyDictionary<string, IReadOnlyList<SprintHierarchyNode>> AssigneeGroups { get; }

    public static SprintHierarchy Build(
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyDictionary<int, WorkItem> parentLookup,
        IReadOnlyList<string>? ceilingTypeNames);
}
```

**Build Algorithm**:
1. Group sprint items by assignee (same as current `FormatSprintView` grouping: `item.AssignedTo ?? "(unassigned)"`).
2. Store groups in a `SortedDictionary<string, List<SprintHierarchyNode>>(StringComparer.OrdinalIgnoreCase)` to enforce alphabetical ordering.
3. For each assignee group:
   a. For each sprint item, walk up `ParentId` chain using `parentLookup` until reaching a parent whose type matches **any** type in `ceilingTypeNames` (case-insensitive), or `ParentId` is null, or parent is not in `parentLookup`.
   b. Build tree nodes: parent context nodes (`IsSprintItem = false`) and sprint item nodes (`IsSprintItem = true`).
   c. Deduplicate shared parents within the assignee group — if two sprint items share a parent (by ID), that parent node appears once with both items as children.
   d. Items without parents or whose parent chain doesn't reach the ceiling appear at root level.
4. Return the assembled `SprintHierarchy`.

#### 4. `ProcessConfigurationData` Caching

**Location**: `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` (or a new `IProcessConfigurationDataStore` interface/implementation)

> **Design note (Issue 3 fix)**: `ProcessConfigurationData` is NOT currently persisted to SQLite. `ProcessTypeSyncService.SyncAsync` already calls `GetProcessConfigurationAsync` during `twig refresh` (line 29), but only uses the result to infer parent-child maps for `ProcessTypeRecord`. The `ProcessConfigurationData` itself (with backlog level names and ordering) is discarded. This means every `twig sprint` invocation would require a live ADO API round-trip to `GET /{project}/_apis/work/processconfiguration`, adding latency to a frequently-used CLI command and failing silently when offline.
>
> **Solution**: Serialize `ProcessConfigurationData` as JSON and persist it in the existing `metadata` table (key: `process_configuration_data`) during `ProcessTypeSyncService.SyncAsync`. Read it back in `WorkspaceCommand`. This eliminates the network call from the sprint display path entirely.

**Persistence approach**:
- `ProcessTypeSyncService.SyncAsync` already has `processConfig` in hand (line 29). After the existing persist loop, serialize it to JSON and save to `metadata`.
- New method on `IProcessTypeStore`: `Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config)` and `Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync()`.
- Serialization: Use `System.Text.Json` with AOT-compatible source generation. Add `[JsonSerializable(typeof(ProcessConfigurationData))]` and `[JsonSerializable(typeof(BacklogLevelConfiguration))]` to `TwigJsonContext`.
- The `metadata` table already exists (`SqliteCacheStore.cs:126–129`) as a key-value store.

#### 5. `Workspace` Model Extension

**Location**: `src/Twig.Domain/ReadModels/Workspace.cs`

**Change**: Add an optional `SprintHierarchy?` property to `Workspace`. This keeps the read model self-contained and avoids changing `FormatSprintView`'s signature.

```csharp
public SprintHierarchy? Hierarchy { get; }
```

The `Workspace.Build` factory gets an optional `hierarchy` parameter. Existing callers pass `null` (no breaking change for `FormatWorkspace` and tests).

#### 6. `IOutputFormatter.FormatSprintView` — No Signature Change

The `FormatSprintView(Workspace ws, int staleDays)` signature remains unchanged. `HumanOutputFormatter` reads `ws.Hierarchy` to decide between hierarchical and flat rendering. If `ws.Hierarchy` is `null`, it falls back to the current flat rendering. JSON and Minimal formatters ignore `ws.Hierarchy`.

#### 7. `WorkspaceCommand` Changes

**Location**: `src/Twig/Commands/WorkspaceCommand.cs`

When `all == true` (sprint view), after fetching sprint items:
1. Collect unique `ParentId` values from sprint items (skip null).
2. For each unique parent ID, call `workItemRepo.GetParentChainAsync` to fetch the full root→parent chain (data should be cached from `RefreshCommand` ancestor hydration). **Note (Issue 2 fix)**: Use `GetParentChainAsync` — NOT `GetByIdAsync`. `GetParentChainAsync` (lines 69–93 of `SqliteWorkItemRepository.cs`) already walks the `parent_id` chain and returns `IReadOnlyList<WorkItem>` ordered root→parent. Using `GetByIdAsync` would require manually reimplementing this chain walk, duplicating existing infrastructure.
3. Build a `parentLookup` dictionary (`Dictionary<int, WorkItem>`) from all items returned by the parent chain calls.
4. Read `ProcessConfigurationData` from the SQLite cache via `processTypeStore.GetProcessConfigurationDataAsync()` — **no network call**. If cache is empty (e.g., never refreshed), fall back to null hierarchy.
5. Compute ceiling via `CeilingComputer.Compute(...)`.
6. Build `SprintHierarchy`.
7. Pass hierarchy into `Workspace.Build(...)`.

**Efficiency**: Parent chain data is already in SQLite cache (from ITEM-155 ancestor hydration). `GetParentChainAsync` reads from local DB, not network. We collect unique parent IDs first to avoid redundant chain walks. `ProcessConfigurationData` is read from the `metadata` table (a single row read), adding negligible overhead.

#### 8. Formatter Rendering (HumanOutputFormatter)

**Location**: `src/Twig/Formatters/HumanOutputFormatter.cs`

Within `FormatSprintView`, replace the flat item loop per assignee with hierarchical rendering when `ws.Hierarchy` is available:

```
Alice Smith (3):
  ▪ Feature: Auth System [Active]          ← dimmed, context parent
    ├── ● □ #42 Login endpoint [Active] •  ← sprint item, active, dirty
    └── □ #43 Logout endpoint [New]        ← sprint item
  □ #44 Fix typo [Active]                  ← sprint item, no parent
```

**Rendering rules**:
- Parent context nodes: `{indent}{typeColor}{badge}{Reset} {Dim}{title}{Reset} [{stateColor}{state}{Reset}]` (same as `FormatTree` parent chain rendering)
- Sprint item nodes: `{indent}{connector}{marker} {typeColor}{badge}{Reset} #{id} {title} [{stateColor}{state}{Reset}]{dirty}` (same visual as current flat rendering, but indented and with box-drawing connector)
- Box-drawing characters: `├── ` and `└── ` for sibling/last-child (same as `FormatTree` children section)
- Vertical continuation line: `│   ` for non-last siblings' children
- Indentation: 2 spaces per nesting level, within the assignee group's existing 6-space indent
- **Assignee group ordering**: Iterate `ws.Hierarchy.AssigneeGroups` directly — the dictionary is pre-sorted alphabetically (FR-010), matching the existing `FormatSprintView` behavior.

### Data Flow

```
RefreshCommand / InitCommand
  └── Fetches sprint items from ADO
  └── Ancestor hydration: fetches parent chains up to 5 levels
  └── ProcessTypeSyncService.SyncAsync:
  │     ├── GetProcessConfigurationAsync → HTTP (already done today)
  │     ├── persist ProcessTypeRecord objects (existing)
  │     └── persist ProcessConfigurationData to metadata table ← NEW
  └── All data saved to SQLite cache

WorkspaceCommand.ExecuteAsync(all: true)
  └── Reads sprint items from SQLite (GetByIterationAsync)
  └── Reads parent chains from SQLite (GetParentChainAsync)           ← NEW
  └── Reads ProcessConfigurationData from SQLite metadata             ← NEW (no network!)
  └── CeilingComputer.Compute(...)                                    ← NEW
  └── SprintHierarchy.Build(...)                                      ← NEW
  └── Workspace.Build(context, sprintItems, seeds, hierarchy)
  └── fmt.FormatSprintView(workspace, staleDays)
        └── HumanOutputFormatter reads ws.Hierarchy
        └── Renders hierarchically per assignee group
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Hierarchy on `Workspace` model** (not a new `FormatSprintView` parameter) | Avoids changing the `IOutputFormatter` interface. JSON/Minimal formatters naturally ignore the property. Existing tests pass with `Hierarchy = null`. |
| **`CeilingComputer` as a static pure function** | Consistent with `BacklogHierarchyService` pattern. Easily testable, no state. |
| **`CeilingComputer.Compute` returns `IReadOnlyList<string>?`** (not `string?`) | A single backlog level can contain multiple type names (e.g., `["User Story", "Backlog Item"]` from `BacklogLevelConfiguration.WorkItemTypeNames`). Returning `string?` would silently discard ceiling-level types, causing parent nodes of the non-returned type to be invisible. See FR-001. |
| **`SprintHierarchy` as a domain read model** | Follows existing pattern (`Workspace`, `WorkTree`). Tree-building is domain logic, not formatter logic. |
| **`SprintHierarchy.AssigneeGroups` uses `SortedDictionary`** | The current `FormatSprintView` explicitly sorts groups with `.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)` (line 227). Using a `SortedDictionary<string, ..., StringComparer.OrdinalIgnoreCase>` ensures the formatter can iterate `AssigneeGroups` directly without re-sorting, and the display order is identical to today. |
| **Ceiling = one level above highest sprint item level** | Shows meaningful context without overwhelming the view. A sprint of Tasks and User Stories shows Features as context. A sprint of only Tasks shows User Stories as context. |
| **Parent chain data from SQLite cache** | `RefreshCommand` already hydrates ancestors (ITEM-155). No additional network calls needed. If parent data is missing, items simply appear at root level (graceful degradation). |
| **`ProcessConfigurationData` persisted to SQLite** | `ProcessTypeSyncService.SyncAsync` already fetches `ProcessConfigurationData` during `twig refresh` (line 29) but discards it after inferring parent-child maps. Persisting it to the `metadata` table (serialized as JSON) adds one write during refresh and eliminates a network call from the frequently-used `twig sprint` command. The alternative (calling `GetProcessConfigurationAsync` in `WorkspaceCommand`) would add a mandatory ADO API round-trip on every `twig sprint` invocation — unacceptable for a CLI command users run frequently, and would fail silently when offline. |
| **Parent chains via `GetParentChainAsync`** (not `GetByIdAsync`) | `GetParentChainAsync` already walks the `parent_id` chain in SQLite and returns the full root→parent chain. Using `GetByIdAsync` would require manually reimplementing this walk, duplicating existing infrastructure. |
| **Flat fallback when `Hierarchy` is null** | Backward-compatible. The personal workspace view (`FormatWorkspace`) and tests that don't supply hierarchy continue to work. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Change `FormatSprintView` signature** to accept `SprintHierarchy` directly | Explicit dependency | Breaks `IOutputFormatter` contract; all 3 implementations must update | Rejected — adding to `Workspace` is less invasive |
| **Build hierarchy inside the formatter** | Keeps `WorkspaceCommand` simple | Formatters become responsible for domain logic; violates separation of concerns | Rejected — domain read model is the right place |
| **Use `IProcessConfigurationProvider`** (cached `ProcessConfiguration`) for ceiling | Already registered in DI | `ProcessConfiguration` has `TypeConfig` with child types but not the backlog level ordering needed for ceiling computation. `ProcessConfigurationData` has the explicit level hierarchy. | Rejected — need `ProcessConfigurationData` with level ordering |
| **Call `GetProcessConfigurationAsync` in `WorkspaceCommand`** (live network call) | Simple — no caching infrastructure | Adds a mandatory ADO API round-trip on every `twig sprint` invocation. Medium likelihood / **Medium-High impact** for a frequently-used CLI command. Fails silently when offline. | Rejected — cache `ProcessConfigurationData` in SQLite instead (see Component 4) |
| **Use `IProcessTypeStore` records to infer levels** | Data already in SQLite | `ProcessTypeRecord` stores states and child types but not the backlog level names/ordering. Cannot determine "one level above" without the level hierarchy. | Rejected — need `ProcessConfigurationData` |
| **Return `string?` from `CeilingComputer.Compute`** | Simpler API | Silently discards all but one type name from the ceiling level. `BacklogLevelConfiguration.WorkItemTypeNames` is `IReadOnlyList<string>` — a level can have multiple types (e.g., `["User Story", "Backlog Item"]`). Parent nodes of the non-returned type would be invisible. | Rejected — return `IReadOnlyList<string>?` |

---

## Dependencies

### Internal Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| `RefreshCommand` ancestor hydration (ITEM-155) | ✅ Done | Parent chain data already cached in SQLite |
| `BacklogHierarchyService` | ✅ Exists | Reuse pattern for level ordering; `CeilingComputer` follows same design |
| `ProcessConfigurationData` | ✅ Exists | Backlog level definitions available via `IIterationService` |
| `ProcessTypeSyncService.SyncAsync` | ✅ Exists | Already calls `GetProcessConfigurationAsync` during refresh (line 29). Needs modification to persist `ProcessConfigurationData` to metadata table. |
| `IWorkItemRepository.GetParentChainAsync` | ✅ Exists | Walks `parent_id` chain from cache |
| `SqliteCacheStore.metadata` table | ✅ Exists | Key-value store for `ProcessConfigurationData` serialization |
| `TwigJsonContext` | ✅ Exists | Needs new `[JsonSerializable]` entries for `ProcessConfigurationData` and `BacklogLevelConfiguration` |

### External Dependencies

None. All data comes from the SQLite cache. No network calls on the `twig sprint` display path.

### Sequencing Constraints

- ~~`CeilingComputer` must be updated (return type change) before `SprintHierarchy` (hierarchy needs the ceiling type list).~~ ✅ DONE (EPIC-1)
- ~~`SprintHierarchy` must be implemented before `WorkspaceCommand` changes.~~ ✅ DONE (EPIC-2)
- `ProcessConfigurationData` caching (EPIC-0) must be implemented before `WorkspaceCommand` changes (it needs to read from cache).
- `Workspace` model extension (EPIC-3) must be implemented before formatter changes.
- All remaining domain components must be complete before `WorkspaceCommand` integration (EPIC-4).

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `Workspace` read model | New optional property `Hierarchy` and updated `Build` factory |
| `WorkspaceCommand` | Additional logic when `all == true` to fetch parents, read cached process config, compute ceiling, build hierarchy |
| `HumanOutputFormatter.FormatSprintView` | New hierarchical rendering path |
| `CeilingComputer` | Return type changes from `string?` to `IReadOnlyList<string>?` |
| `ProcessTypeSyncService` | Persist `ProcessConfigurationData` to metadata table after existing sync |
| `IProcessTypeStore` | New methods: `SaveProcessConfigurationDataAsync`, `GetProcessConfigurationDataAsync` |
| `SqliteProcessTypeStore` | Implement new methods (read/write metadata table) |
| `TwigJsonContext` | Add `[JsonSerializable]` for `ProcessConfigurationData`, `BacklogLevelConfiguration`, `List<BacklogLevelConfiguration>` |
| `IOutputFormatter` | **No change** (signature stays the same) |
| `JsonOutputFormatter` / `MinimalOutputFormatter` | **No change** (ignore `ws.Hierarchy`) |
| `FormatWorkspace` | **No change** (hierarchy only for sprint view) |

### Backward Compatibility

- `Workspace.Build(context, sprintItems, seeds)` — existing 3-arg overload continues to work (hierarchy defaults to `null`).
- All existing tests pass because they don't supply hierarchy data, and the formatter falls back to flat rendering.
- `IOutputFormatter.FormatSprintView` signature is unchanged.
- ~~`CeilingComputer.Compute` return type change is source-breaking for existing callers that assign to `string?`. The only callers are tests (`CeilingComputerTests.cs`) — these must be updated to expect `IReadOnlyList<string>?`.~~ ✅ DONE — all `CeilingComputerTests` already use `IReadOnlyList<string>?`-compatible assertions.
- `WorkspaceCommand` constructor change (adding `IProcessTypeStore`) is source-breaking for direct constructor calls. Affected test files: `WorkspaceCommandTests.cs` (1 call at line 36) and `UserScopedWorkspaceTests.cs` (6 calls at lines 57, 83, 108, 132, 166, 189). Both must be updated in ITEM-008.

### Performance Implications

- `WorkspaceCommand` makes additional `GetParentChainAsync` calls when `all == true`. These read from SQLite (local I/O, sub-millisecond per call). With `N` unique parent IDs, this is `O(N × chain_depth)` SQLite reads.
- `CeilingComputer.Compute` is `O(levels × types)` — trivial.
- `SprintHierarchy.Build` is `O(items × chain_depth)` — trivial for typical sprint sizes (10–50 items).
- `ProcessConfigurationData` read from `metadata` table: single row read, JSON deserialization — sub-millisecond.
- **No network calls** during `twig sprint` rendering (all data comes from SQLite cache).

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Parent data missing from cache (ancestors not hydrated) | Low | Low | Graceful degradation: items appear at root level if parent chain is incomplete |
| `ProcessConfigurationData` not in cache (user hasn't run `twig refresh` since upgrade) | Medium | Low | Graceful degradation: `GetProcessConfigurationDataAsync` returns null → hierarchy is null → flat rendering (identical to today). First `twig refresh` after upgrade populates the cache. |
| Custom process templates with non-standard backlog levels | Low | Medium | `CeilingComputer` uses `ProcessConfigurationData` levels directly; works with any configuration. Test with custom types. |
| Performance regression from additional SQLite reads | Low | Low | Reads are local; typical overhead is < 10ms for 50 items. Profile if needed. |
| `CeilingComputer` return type change breaks existing callers | N/A | N/A | ✅ RESOLVED — tests already updated in EPIC-1. |
| `WorkspaceCommand` constructor change breaks test files | Low | Low | 7 constructor calls across 2 test files (`WorkspaceCommandTests.cs`, `UserScopedWorkspaceTests.cs`). Both updated in ITEM-008. |

---

## Open Questions

| ID | Question | Status |
|----|----------|--------|
| OQ-1 | Should the ceiling be configurable (e.g., user can set max parent depth)? | **Deferred** — start with the computed ceiling; add config if users request it |
| OQ-2 | When a sprint item IS the ceiling-level type (e.g., a Feature in the sprint with Tasks under it), should it render as a sprint item or context? | **Resolved** — it renders as a sprint item (full color, with markers). Its children that are also sprint items appear nested beneath it. |

---

## Implementation Phases

### Phase 0: ProcessConfigurationData Caching
**Exit Criteria**: `ProcessConfigurationData` is serialized to SQLite during `twig refresh`/`twig init` and can be read back. No network call needed at display time.

### Phase 1: Domain Foundation (CeilingComputer + SprintHierarchy)
**Exit Criteria**: `CeilingComputer` return type updated, domain logic implemented and fully tested. No UI or command changes.  
**Status**: ✅ COMPLETE — EPIC-1 and EPIC-2 are done. Build passes, all tests green.

### Phase 2: Model & Command Integration
**Exit Criteria**: `Workspace` model extended, `WorkspaceCommand` builds hierarchy for sprint view using cached data.

### Phase 3: Formatter Rendering
**Exit Criteria**: `HumanOutputFormatter.FormatSprintView` renders hierarchically. Existing tests pass. New rendering tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ReadModels/SprintHierarchy.cs` | `SprintHierarchy` read model and `SprintHierarchyNode` — builds per-assignee hierarchy trees |
| `tests/Twig.Domain.Tests/ReadModels/SprintHierarchyTests.cs` | Unit tests for hierarchy tree building |
| `tests/Twig.Cli.Tests/Formatters/SprintHierarchyFormatterTests.cs` | Tests for hierarchical rendering in `HumanOutputFormatter` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/CeilingComputer.cs` | Change return type from `string?` to `IReadOnlyList<string>?`; return all type names from ceiling level |
| `src/Twig.Domain/Interfaces/IProcessTypeStore.cs` | Add `SaveProcessConfigurationDataAsync` and `GetProcessConfigurationDataAsync` methods |
| `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` | Implement new methods (serialize/deserialize `ProcessConfigurationData` to/from `metadata` table) |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for `ProcessConfigurationData`, `BacklogLevelConfiguration`, `List<BacklogLevelConfiguration>` |
| `src/Twig.Domain/Services/ProcessTypeSyncService.cs` | After existing persist loop, serialize and save `ProcessConfigurationData` to metadata |
| `src/Twig.Domain/ReadModels/Workspace.cs` | Add optional `SprintHierarchy? Hierarchy` property; update `Build` factory with optional parameter |
| `src/Twig/Commands/WorkspaceCommand.cs` | When `all == true`: fetch parent chains via `GetParentChainAsync`, read cached process config, compute ceiling, build hierarchy, pass to `Workspace.Build` |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Update `FormatSprintView` to render hierarchically when `ws.Hierarchy` is available |
| `tests/Twig.Domain.Tests/Services/CeilingComputerTests.cs` | Update existing tests for new `IReadOnlyList<string>?` return type; add multi-type-per-level tests |
| `tests/Twig.Domain.Tests/Services/ProcessTypeSyncServiceTests.cs` | Verify `ProcessConfigurationData` is persisted during sync |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | Verify hierarchy building when `all == true`; update constructor to pass `IProcessTypeStore` mock |
| `tests/Twig.Cli.Tests/Commands/UserScopedWorkspaceTests.cs` | Update all 6 `new WorkspaceCommand(...)` constructor calls (lines 57, 83, 108, 132, 166, 189) to pass `IProcessTypeStore` mock when ITEM-007 adds the constructor parameter |

### Deleted Files

None.

---

## Implementation Plan

### EPIC-0: ProcessConfigurationData Caching

**Goal**: Persist `ProcessConfigurationData` to SQLite during `twig refresh`/`twig init` so that `WorkspaceCommand` can read it without a network call.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-100 | IMPL | Add `[JsonSerializable(typeof(ProcessConfigurationData))]`, `[JsonSerializable(typeof(BacklogLevelConfiguration))]`, and `[JsonSerializable(typeof(List<BacklogLevelConfiguration>))]` to `TwigJsonContext.cs` for AOT-compatible serialization. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-101 | IMPL | Add two new methods to `IProcessTypeStore`: `Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config, CancellationToken ct = default)` and `Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync(CancellationToken ct = default)`. | `src/Twig.Domain/Interfaces/IProcessTypeStore.cs` | DONE |
| ITEM-102 | IMPL | Implement `SaveProcessConfigurationDataAsync` and `GetProcessConfigurationDataAsync` in `SqliteProcessTypeStore`. Serialize `ProcessConfigurationData` as JSON via `TwigJsonContext`. Store in the existing `metadata` table with key `process_configuration_data`. Read back and deserialize. Handle missing/corrupt data gracefully (return `null`). | `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` | DONE |
| ITEM-103 | IMPL | Update `ProcessTypeSyncService.SyncAsync` to call `processTypeStore.SaveProcessConfigurationDataAsync(processConfig)` after the existing `ProcessTypeRecord` persist loop (after line 48). The `processConfig` variable is already in scope (line 29). | `src/Twig.Domain/Services/ProcessTypeSyncService.cs` | DONE |
| ITEM-104 | TEST | Update `ProcessTypeSyncServiceTests` to verify `SaveProcessConfigurationDataAsync` is called during sync. Add round-trip test: save `ProcessConfigurationData`, load it back, verify all levels and type names are preserved. | `tests/Twig.Domain.Tests/Services/ProcessTypeSyncServiceTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `ProcessConfigurationData` is serialized and stored in `metadata` table during `twig refresh`
- [x] `GetProcessConfigurationDataAsync` returns the persisted data with all levels and type names intact
- [x] `GetProcessConfigurationDataAsync` returns `null` if no data is in the cache (graceful degradation)
- [x] AOT-compatible (source-generated JSON serialization)
- [x] All existing `ProcessTypeSyncServiceTests` pass unchanged

**Status**: DONE (2026-03-16) — `SaveProcessConfigurationDataAsync` and `GetProcessConfigurationDataAsync` added to `IProcessTypeStore` and implemented in `SqliteProcessTypeStore`. `ProcessTypeSyncService.SyncAsync` wired to persist after sync. AOT JSON registration added to `TwigJsonContext`. All 1136 tests passing. Note: `WorkspaceCommand` read path (consuming `GetProcessConfigurationDataAsync`) is deferred to EPIC-4.

---

### EPIC-1: Ceiling Computation (Return Type Fix)

**Goal**: Update `CeilingComputer.Compute` to return `IReadOnlyList<string>?` instead of `string?`, capturing all type names in the ceiling level.

**Prerequisites**: None.

**Status**: ✅ COMPLETE — All code and tests are on disk and passing.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Updated `CeilingComputer.Compute` return type from `string?` to `IReadOnlyList<string>?`. Line 61 now returns `ceilingLevel.WorkItemTypeNames` (all type names from the ceiling level, not just the first). XML doc updated. | `src/Twig.Domain/Services/CeilingComputer.cs` | DONE |
| ITEM-002 | TEST | Updated `CeilingComputerTests.cs`: all 12 tests use `IReadOnlyList<string>?`-compatible assertions (`result.ShouldBe(new[] {"Feature"})` etc.). Test `Compute_MultipleTypesPerLevel_ReturnsAllTypeNames` (line 117) verifies both `"Initiative"` and `"Scenario"` are returned. All tests pass. | `tests/Twig.Domain.Tests/Services/CeilingComputerTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `CeilingComputer.Compute` returns correct ceiling for standard Agile/Scrum/CMMI hierarchies
- [x] Returns `null` when no level exists above the highest sprint item level
- [x] Returns `null` for null/empty inputs
- [x] Returns ALL type names from the ceiling level (not just the first)
- [x] Existing tests updated for new return type
- [x] All tests pass

---

### EPIC-2: SprintHierarchy Read Model

**Goal**: Implement the `SprintHierarchy` read model that builds per-assignee hierarchical trees from flat sprint items and their parent chains.

**Prerequisites**: EPIC-1 (needs ceiling type list for trimming).

**Status**: ✅ COMPLETE — All code and tests are on disk and passing.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-003 | IMPL | `SprintHierarchy.Build` accepts `IReadOnlyList<string>? ceilingTypeNames` (plural). Uses `SortedDictionary<string, ..., StringComparer.OrdinalIgnoreCase>` for both grouped and result dictionaries (FR-010). `BuildAssigneeTree` matches ceiling against `ceilingTypeNames.Any(t => string.Equals(..., OrdinalIgnoreCase))`. Groups by `item.AssignedTo ?? "(unassigned)"`. All on disk and verified. | `src/Twig.Domain/ReadModels/SprintHierarchy.cs` | DONE |
| ITEM-004 | TEST | All 13 `SprintHierarchyTests` use `IReadOnlyList<string>?` ceiling parameter (`new[] { "Feature" }` syntax). Tests 11–13 cover multi-type ceiling, alphabetical ordering, and unassigned grouping. All tests pass. | `tests/Twig.Domain.Tests/ReadModels/SprintHierarchyTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `SprintHierarchy.Build` produces correct tree for standard scenarios
- [x] Shared parents are deduplicated within an assignee group
- [x] Items without parents appear at root level
- [x] Sprint items that are also parents get `IsSprintItem = true`
- [x] Parent chains are trimmed at or above the ceiling level
- [x] Ceiling with multiple type names works correctly (all types matched)
- [x] `AssigneeGroups` is alphabetically ordered by key (case-insensitive)
- [x] All tests pass (including new multi-type and ordering tests)
- [x] Empty `ceilingTypeNames` list treated identically to `null` (flat output)
- [x] Root nodes and children sorted deterministically by `Item.Id`
- [x] `Build_EmptyCeilingTypeNames_ItemsFlat` test verifies empty-list equivalence

**Status**: DONE (2026-03-16) — Review fixes applied: empty-list guard on `ceilingTypeNames` (line 93), discard pattern for IDE0059 (line 121), deterministic `Item.Id` sort on roots and children (lines 143–159), new test `Build_EmptyCeilingTypeNames_ItemsFlat`.

---

### EPIC-3: Workspace Model Extension

**Goal**: Extend the `Workspace` read model to carry `SprintHierarchy` data without breaking existing consumers.

**Prerequisites**: EPIC-2.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-005 | IMPL | Add `SprintHierarchy? Hierarchy` property to `Workspace`. Update `Build` factory to accept optional `SprintHierarchy? hierarchy = null` parameter. Store in private constructor. Existing callers pass 3 args (no change needed). | `src/Twig.Domain/ReadModels/Workspace.cs` | DONE |
| ITEM-006 | TEST | Verify existing `WorkspaceTests` still pass. Add test: `Build_WithHierarchy_ExposesHierarchy` — builds workspace with a non-null hierarchy, verifies `ws.Hierarchy` is accessible. Add test: `Build_WithoutHierarchy_HierarchyIsNull` — existing 3-arg Build sets `Hierarchy` to null. | `tests/Twig.Domain.Tests/ReadModels/WorkspaceTests.cs` | DONE |

**Acceptance Criteria**:
- [x] All existing `WorkspaceTests` pass unchanged
- [x] `Workspace.Build` with 3 args sets `Hierarchy` to `null`
- [x] `Workspace.Build` with 4 args stores and exposes the `SprintHierarchy`

**Status**: DONE (2026-03-16) — `Workspace` read model extended with optional `SprintHierarchy? Hierarchy` property. Private constructor and `Build` factory updated; backward compatibility preserved via `= null` default. Two new tests added and passing.

---

### EPIC-4: WorkspaceCommand Integration

**Goal**: Wire up `WorkspaceCommand.ExecuteAsync` to fetch parent chain data, read cached process configuration, compute ceiling, build hierarchy, and pass it to `Workspace.Build` when in sprint mode (`all == true`).

**Prerequisites**: EPIC-0 (cached process config), EPIC-3 (Workspace model with Hierarchy).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-007 | IMPL | In `WorkspaceCommand.ExecuteAsync`, when `all == true`: (1) Collect unique `ParentId` values from sprint items (skip null). (2) For each unique parent ID, call `workItemRepo.GetParentChainAsync` to fetch the full root→parent chain. Build a `parentLookup` dictionary (`Dictionary<int, WorkItem>`) from all returned items. **CRITICAL**: Use `GetParentChainAsync` — NOT `GetByIdAsync`. `GetParentChainAsync` (lines 69–93 of `SqliteWorkItemRepository.cs`) already walks the `parent_id` chain and returns the full root→parent list. (3) Call `processTypeStore.GetProcessConfigurationDataAsync()` to read `ProcessConfigurationData` from SQLite cache — **NO network call**. If null (cache empty), skip hierarchy (fall back to flat). (4) Call `CeilingComputer.Compute(sprintTypeNames, config)`. (5) Call `SprintHierarchy.Build(sprintItems, parentLookup, ceilingTypeNames)`. (6) Pass hierarchy to `Workspace.Build`. **Note**: `WorkspaceCommand` needs `IProcessTypeStore` injected (add constructor parameter). Since `IProcessTypeStore` is already registered in DI (`Program.cs:64`), adding it to `WorkspaceCommand`'s primary constructor requires zero changes to `Program.cs`. | `src/Twig/Commands/WorkspaceCommand.cs` | DONE |
| ITEM-008 | TEST | Update `WorkspaceCommandTests` (1 constructor call at line 36) and `UserScopedWorkspaceTests` (6 constructor calls at lines 57, 83, 108, 132, 166, 189): add `IProcessTypeStore` mock to all `new WorkspaceCommand(...)` calls. In `WorkspaceCommandTests`, verify that when `all == true`, `GetParentChainAsync` (NOT `GetByIdAsync`) is called for parent IDs. Mock `IProcessTypeStore.GetProcessConfigurationDataAsync` returning a standard Agile hierarchy `ProcessConfigurationData`. Verify that when `GetProcessConfigurationDataAsync` returns null, hierarchy is null (flat rendering). | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/UserScopedWorkspaceTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `WorkspaceCommand` fetches parent data via `GetParentChainAsync` and builds hierarchy when `all == true`
- [x] Reads `ProcessConfigurationData` from SQLite cache — no network call
- [x] Gracefully falls back to flat view if cached process config is null
- [x] All existing `WorkspaceCommandTests` pass (constructor updated with `IProcessTypeStore` mock)
- [x] All existing `UserScopedWorkspaceTests` pass (6 constructor calls updated with `IProcessTypeStore` mock)
- [x] Sprint items without parents are handled correctly (no crash)

**Status**: DONE (2026-03-16) — `IProcessTypeStore` added as 8th constructor parameter to `WorkspaceCommand`. `ExecuteAsync` wires `GetParentChainAsync` for unique parent IDs, reads `ProcessConfigurationData` from SQLite cache, calls `CeilingComputer.Compute`, builds `SprintHierarchy`, and passes to `Workspace.Build` when `all == true`. Null process config gracefully falls back to flat (hierarchy null). `WorkspaceCommandTests` (1 call) and `UserScopedWorkspaceTests` (6 calls) updated with `IProcessTypeStore` mock. 3 new test methods added. All 13 tests passing.

---

### EPIC-5: Hierarchical Formatter Rendering

**Goal**: Update `HumanOutputFormatter.FormatSprintView` to render hierarchically when `ws.Hierarchy` is available, falling back to flat rendering otherwise.

**Prerequisites**: EPIC-4.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-009 | IMPL | In `HumanOutputFormatter.FormatSprintView`: when `ws.Hierarchy` is not null, replace the flat item loop per assignee with a recursive tree renderer. For each assignee group in `ws.Hierarchy.AssigneeGroups` (iterate directly — dictionary is pre-sorted alphabetically per FR-010): render root nodes, then recursively render children with box-drawing connectors (`├──`, `└──`, `│   `). Parent context nodes (IsSprintItem=false): render dimmed (`{typeColor}{badge}{Reset} {Dim}{title}{Reset} [{stateColor}{state}{Reset}]`). Sprint item nodes: render with full color and markers (`{marker} {typeColor}{badge}{Reset} #{id} {title} [{stateColor}{state}{Reset}]{dirty}`). When `ws.Hierarchy` is null, keep existing flat rendering. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-010 | TEST | Create `SprintHierarchyFormatterTests.cs`. Test cases: (1) Hierarchical output contains box-drawing characters. (2) Parent context nodes are dimmed. (3) Sprint items show active marker and dirty marker. (4) Items without parents render at root. (5) Shared parents appear once with multiple children. (6) Fallback to flat when hierarchy is null. (7) Empty sprint still shows "0 items". (8) Verify existing `SprintViewFormatterTests` still pass. (9) Assignee groups render in alphabetical order. | `tests/Twig.Cli.Tests/Formatters/SprintHierarchyFormatterTests.cs` | DONE |

**Acceptance Criteria**:
- [x] Hierarchical rendering uses box-drawing characters consistent with `FormatTree`
- [x] Parent context nodes render dimmed with colored badges (no active/dirty markers)
- [x] Sprint items render with full color, active marker, and dirty marker
- [x] Flat fallback works when `ws.Hierarchy` is null
- [x] Assignee groups render in alphabetical order (same as current flat view)
- [x] All existing `SprintViewFormatterTests` pass unchanged
- [x] New formatter tests pass

**Status**: DONE (2026-03-16) — `FormatSprintView` branches on `ws.Hierarchy`: hierarchical path when non-null, flat fallback otherwise. Extracted flat rendering into `RenderFlatSprint` (pure refactor). Added `RenderHierarchicalSprint`, `RenderHierarchyNodeLine`, `RenderHierarchyChildren`, and `CountSprintItems` helpers. Parent context nodes render dimmed with colored badges (no markers). Sprint items render with active marker (cyan ●) and dirty marker (yellow •). 11 new `SprintHierarchyFormatterTests` added. All 1,150 tests passing (439 Domain + 453 CLI + 258 Infrastructure).

---

## References

| Resource | Location |
|----------|----------|
| `HumanOutputFormatter.FormatSprintView` | `src/Twig/Formatters/HumanOutputFormatter.cs:197–273` |
| `HumanOutputFormatter.FormatTree` | `src/Twig/Formatters/HumanOutputFormatter.cs:74–125` |
| `WorkspaceCommand.ExecuteAsync` | `src/Twig/Commands/WorkspaceCommand.cs:1–74` |
| `BacklogHierarchyService.InferParentChildMap` | `src/Twig.Domain/Services/BacklogHierarchyService.cs:18–46` |
| `ProcessConfigurationData` | `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs` |
| `BacklogLevelConfiguration.WorkItemTypeNames` | `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs:25` |
| `ProcessTypeSyncService.SyncAsync` | `src/Twig.Domain/Services/ProcessTypeSyncService.cs:21–51` |
| `CeilingComputer.Compute` | `src/Twig.Domain/Services/CeilingComputer.cs:21–63` |
| `Workspace` read model | `src/Twig.Domain/ReadModels/Workspace.cs` |
| `WorkTree` read model | `src/Twig.Domain/ReadModels/WorkTree.cs` |
| `WorkItem.ParentId` | `src/Twig.Domain/Aggregates/WorkItem.cs:37` |
| `SqliteWorkItemRepository.GetParentChainAsync` | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs:69–93` |
| `SqliteProcessTypeStore` | `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` |
| `SqliteCacheStore.metadata` table | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs:126–129` |
| `TwigJsonContext` | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` |
| `RefreshCommand` ancestor hydration | `src/Twig/Commands/RefreshCommand.cs:112–123` |
| Existing sprint view tests | `tests/Twig.Cli.Tests/Formatters/SprintViewFormatterTests.cs` |
| `CeilingComputerTests` | `tests/Twig.Domain.Tests/Services/CeilingComputerTests.cs` |
| `UserScopedWorkspaceTests` | `tests/Twig.Cli.Tests/Commands/UserScopedWorkspaceTests.cs` |
| `BacklogHierarchyServiceTests` | `tests/Twig.Domain.Tests/Services/BacklogHierarchyServiceTests.cs` |

---

## Revision Notes

### Revision 3 (2026-03-16)

**Addresses technical review feedback (score 80/100). Three critical issues and status accuracy corrected:**

1. **Critical Issue 1 — EPIC-1 and EPIC-2 status inaccuracy** (plan didn't reflect reality → corrected): Verified on disk: `CeilingComputer.Compute` already returns `IReadOnlyList<string>?`, all `CeilingComputerTests` already use `result.ShouldBe(new[] { ... })` assertions, `SprintHierarchy.Build` already accepts `IReadOnlyList<string>? ceilingTypeNames`, and all 13 `SprintHierarchyTests` pass with `new[] { ... }` array syntax. Build succeeds with 0 errors, 0 warnings. ITEM-001 through ITEM-004 all marked DONE with updated descriptions reflecting actual on-disk state. EPIC-1 and EPIC-2 marked ✅ COMPLETE with status banners. ITEM-002 description corrected — previous revision incorrectly instructed "rename to `ReturnsAllTypeNames`" when the test was already renamed. ITEM-003 description corrected — previous revision described changes as future work when all were already implemented. Sequencing constraints updated to reflect EPIC-1/EPIC-2 completion.

2. **Critical Issue 2 — `UserScopedWorkspaceTests.cs` missing from scope** (concrete gap → fixed): `UserScopedWorkspaceTests.cs` contains 6 direct `new WorkspaceCommand(...)` constructor calls (lines 57, 83, 108, 132, 166, 189). When ITEM-007 adds `IProcessTypeStore` to `WorkspaceCommand`'s constructor, all 6 become compile errors. Added file to: Modified Files table, ITEM-008 description and Files column, EPIC-4 acceptance criteria, backward compatibility section, risks table, and references.

3. **Critical Issue 3 — ITEM-002 description factually incorrect** (misleading instructions → corrected): ITEM-002 previously said "rename `Compute_MultipleTypesPerLevel_ReturnsFirstTypeName` (line 111) to `Compute_MultipleTypesPerLevel_ReturnsAllTypeNames`". Verified: test is already named `Compute_MultipleTypesPerLevel_ReturnsAllTypeNames` (line 117) and already asserts `result.ShouldBe(new[] { "Initiative", "Scenario" })`. Updated ITEM-002 to describe actual state (completed work) rather than giving incorrect future instructions.

4. **Additional improvement — ITEM-007 Program.cs note**: Added explicit note that `IProcessTypeStore` is already registered in DI at `Program.cs:64`, so adding it to `WorkspaceCommand`'s primary constructor requires zero changes to `Program.cs`.

5. **Additional improvement — Progress banner**: Added implementation progress summary to Executive Summary so readers immediately know which epics are done vs. remaining.

### Revision 2 (2026-03-16)

**Addresses technical review feedback (score 80/100). Four issues corrected:**

1. **Issue 1 — CeilingComputer return type** (design bug → fixed): Changed `CeilingComputer.Compute` return type from `string?` to `IReadOnlyList<string>?`. `BacklogLevelConfiguration.WorkItemTypeNames` is `IReadOnlyList<string>` — a single backlog level can contain multiple type names (e.g., `["User Story", "Backlog Item"]`). The `string?` return silently discarded all but the first type, causing parent nodes of non-returned types to be invisible. Updated: FR-001, Component 1 (CeilingComputer), Component 3 (SprintHierarchy.Build parameter), EPIC-1, EPIC-2.

2. **Issue 2 — ITEM-007 contradicted design section** (implementation error → fixed): ITEM-007 step (2) incorrectly specified `GetByIdAsync` with manual chain walking. Corrected to use `GetParentChainAsync`, which already walks the `parent_id` chain and returns root→parent ordered results (lines 69–93 of `SqliteWorkItemRepository.cs`). Updated: Component 7, ITEM-007, ITEM-008.

3. **Issue 3 — `GetProcessConfigurationAsync` network call** (understated risk → redesigned): Elevated `ProcessConfigurationData` caching from "deferred optional work" (OQ-1) to a primary deliverable. `ProcessTypeSyncService.SyncAsync` already calls `GetProcessConfigurationAsync` during `twig refresh` (line 29) but discards the result after inferring parent-child maps. New design: serialize `ProcessConfigurationData` as JSON to the existing `metadata` SQLite table during sync, read it back in `WorkspaceCommand`. Eliminates the ADO network call from `twig sprint` entirely. Added: new Component 4 (ProcessConfigurationData Caching), EPIC-0, FR-009, G7. Updated: architecture overview, data flow, design decisions, alternatives considered, dependencies, impact analysis, risks table. Removed: OQ-1 (resolved).

4. **Issue 4 — AssigneeGroups ordering unspecified** (behavioral regression risk → fixed): Specified that `SprintHierarchy.Build` MUST use `SortedDictionary<string, ..., StringComparer.OrdinalIgnoreCase>` to produce alphabetically-ordered assignee groups. The current `FormatSprintView` sorts with `.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)` (line 227). Without this specification, assignee display order would silently change. Added: FR-010, design note on Component 3, updated ITEM-003, ITEM-004, ITEM-009, ITEM-010 with ordering requirements.
