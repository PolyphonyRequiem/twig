# Workspace Mode Infrastructure

| Field | Value |
|-------|-------|
| **Work Item** | #1946 — Workspace Mode Infrastructure |
| **Type** | Issue (parent of Tasks) |
| **Author** | Daniel Green |
| **Status** | 🔨 In Progress — 1/3 PR groups merged |
| **Revision** | 0 |

---

## Executive Summary

This plan introduces the foundational infrastructure for **workspace modes** in twig — a mechanism that allows users to configure how their workspace selects, filters, and displays work items. The scope covers three independent but complementary sub-systems: (1) a domain model and SQLite persistence layer for workspace mode configuration (Sprint, Area, Recent modes with tracked/excluded items), (2) expanding the `display.tree_depth` configuration from a single integer into three independent dimensions (upward, downward, sideways) with backward-compatible parsing, and (3) a `workspace.working_level` configuration that dims items above a user-specified type level in tree views. Together, these changes establish the data model, schema, and rendering hooks that future workspace-mode-switching commands will build upon.

---

## Background

### Current Architecture

Twig's workspace is currently hardcoded to a **sprint-centric** model. The `WorkspaceCommand` queries items by iteration path (the current sprint) and optionally filters by assignee. There is no concept of workspace "mode" — the sprint is the only lens through which work items are viewed.

**Tree depth** is controlled by a single integer (`DisplayConfig.TreeDepth`, default 10) that governs only the **downward** child count. The parent chain is always fetched to root with no limit, and sibling counts are shown as dimmed indicators but siblings themselves are not rendered. There is no mechanism to control how far up the parent chain is displayed or to limit sideways (sibling) expansion.

**Rendering** treats all items in the tree equally — there is no visual distinction between the "working level" (e.g., Tasks a developer cares about) and higher-level container items (e.g., Epics, Features). The `SpectreTheme` provides color coding by type and state, but no dim/emphasis differentiation based on hierarchy level.

### Key Components Affected

| Component | Current Role | Impact |
|-----------|-------------|--------|
| `DisplayConfig` | Holds `TreeDepth` (single int) | Expanded to `TreeDepthConfig` (3 dimensions) |
| `TwigConfiguration` | Config POCO with `SetValue` switch | New `workspace` section, updated `display.treedepth` parsing |
| `TwigJsonContext` | Source-generated JSON context | New `[JsonSerializable]` registrations |
| `SqliteCacheStore` | Schema DDL, version management | Schema version bump, new tables |
| `TreeCommand` | Orchestrates tree rendering | Passes depth dimensions instead of single int |
| `WorkspaceCommand` | Sprint-centric workspace view | Receives depth dimensions |
| `SpectreRenderer` | Live tree rendering | Dim rendering for items above working level |
| `HumanOutputFormatter` | ANSI tree formatting | Dim rendering for items above working level |
| `IAsyncRenderer` | Async rendering contract | Updated `maxChildren` semantic |
| `ReadTools` (MCP) | MCP tree tool | Updated depth handling |
| `IProcessConfigurationProvider` | Process type hierarchy | Used to resolve working level |
| `BacklogHierarchyService` | Type level mapping | Used to determine "above working level" |

### Call-Site Audit: `config.Display.TreeDepth` Usage

| File | Method/Context | Current Usage | Impact |
|------|---------------|---------------|--------|
| `TwigConfiguration.cs:356` | `DisplayConfig.TreeDepth` property | Default value = 10 | Replace with `TreeDepthConfig` |
| `TwigConfiguration.cs:175-180` | `SetValue("display.treedepth", ...)` | Parses single int | Backward-compat: single int → downward |
| `TreeCommand.cs:63` | `ExecuteCoreAsync` | `depth ?? config.Display.TreeDepth` | Use `config.Display.TreeDepthConfig.Downward` |
| `ReadTools.cs:43` | MCP `twig_tree` | `depth ?? ctx.Config.Display.TreeDepth` | Use `.TreeDepthConfig.Downward` |
| `ConfigCommand.cs:68` | `twig config get` | `config.Display.TreeDepth.ToString()` | Return structured display |
| `TwigConfigurationTests.cs:41,153,170` | Default and load tests | Assert `TreeDepth == 10` or `== 5` | Update for new schema |
| `TwigConfigurationTests.cs:245-246` | `SetValue` test | Assert `TreeDepth == 5` after set | Update for backward compat |
| `ConfigCommandTests.cs:96,99` | Config get test | Sets `TreeDepth = 5` | Update for new schema |
| `TreeCommandTests.cs:107` | Tree depth override test | Sets `TreeDepth = 100` | Update for new schema |

### Call-Site Audit: `maxChildren` Parameter Flow

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `IAsyncRenderer.cs:29` | `RenderTreeAsync` | `int maxChildren` parameter | Rename/clarify as downward depth |
| `SpectreRenderer.cs:273,449` | `RenderTreeAsync`, `BuildTreeViewAsync` | Limits child count display | Apply downward depth |
| `IOutputFormatter.cs:14` | `FormatTree` | `int maxChildren` parameter | Apply downward depth |
| `HumanOutputFormatter.cs:213,218` | `FormatTree` overloads | Limits children displayed | Apply downward depth + upward trim |
| `MinimalOutputFormatter.cs:25` | `FormatTree` | Limits children | Apply downward depth |
| `JsonOutputFormatter.cs:110` | `FormatTree` | Limits children | Apply downward depth |
| `JsonCompactOutputFormatter.cs:38` | `FormatTree` | Limits children | Apply downward depth |

### Call-Site Audit: `GetParentChainAsync` (Upward Depth)

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SqliteWorkItemRepository.cs:101` | `GetParentChainAsync` | Walks to root (no limit) | No change (limit applied at render time) |
| `TreeCommand.cs:101,120,148,184` | Multiple tree build paths | Full parent chain | Trim to upward depth at render |
| `SpectreRenderer.cs:284,447` | `RenderTreeAsync`, `BuildTreeViewAsync` | Full parent chain rendered | Trim to upward depth |
| `HumanOutputFormatter.cs:241-258` | `FormatTree` | Renders all parents | Trim to upward depth |
| `WorkspaceCommand.cs:202` | Sprint hierarchy parents | Full chain for hierarchy | No change (workspace uses hierarchy) |
| `ReadTools.cs:40` | MCP tree | Full parent chain | Trim to upward depth |
| `NavigationCommands.cs:79,124,163` | Nav commands | Full chain | No change (navigation) |
| `SetCommand.cs:137,142` | Set context | Full chain for display | No change (uses ShowCommand) |

---

## Problem Statement

1. **No workspace mode abstraction.** Twig is locked into a sprint-centric view. Users working on area-path-scoped teams, cross-sprint initiatives, or recent-activity workflows have no way to configure their workspace lens. There is no domain model, no persistence layer, and no configuration schema for workspace modes.

2. **Tree depth is one-dimensional.** The single `TreeDepth` integer controls only downward child display count. Users cannot control how many parent levels are shown (upward depth) or whether siblings of parent items should be expanded (sideways depth). This limits tree readability for both deep hierarchies (too many parents clutter the view) and wide backlogs (no sibling context).

3. **No working-level emphasis.** In a typical backlog, developers focus on Tasks or User Stories, but tree views show all hierarchy levels with equal visual weight. There is no way to dim container items (Epics, Features) above the working level, making it harder to scan for actionable items.

---

## Goals and Non-Goals

### Goals

1. **Define a workspace mode domain model** — `WorkspaceMode` enum/value type and supporting entities (tracked items, excluded items, sprint iterations, area paths) that can represent Sprint, Area, and Recent modes.
2. **Persist mode configuration in SQLite** — New tables (`tracked_items`, `excluded_items`, `sprint_iterations`, `area_paths`) with a clean migration path for existing databases.
3. **Define `IWorkspaceModeStore` interface** — Read/write contract for mode configuration, enabling future mode-switching commands without infrastructure coupling.
4. **Expand tree depth to three dimensions** — Upward (default 2), downward (default 10), sideways (default 1) with backward-compatible config parsing.
5. **Wire depth dimensions into all tree renderers** — `TreeCommand`, `WorkspaceCommand`, `SpectreRenderer`, `HumanOutputFormatter`, MCP `ReadTools`, and all `IOutputFormatter` implementations.
6. **Add working-level configuration** — `workspace.working_level` config setting that uses the process configuration to determine which type hierarchy level to emphasize.
7. **Dim items above working level** — Visual de-emphasis in tree views via `SpectreRenderer` and `HumanOutputFormatter`.

### Non-Goals

- **Mode-switching commands** — No `twig mode set sprint` or `twig mode set area` commands in this work. Those build on the infrastructure defined here.
- **Mode-aware workspace queries** — The `WorkspaceCommand` will not change its query behavior in this work. Mode-aware filtering is a separate feature.
- **Recursive child depth** — The downward depth dimension controls immediate children of the focused item, not recursive depth through grandchildren. Recursive tree expansion is a separate feature.
- **Custom mode definitions** — Only Sprint, Area, and Recent modes are defined. User-defined custom modes are out of scope.

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1 | Define `WorkspaceMode` as a sealed type with Sprint, Area, Recent variants | Must |
| FR-2 | Create SQLite tables: `tracked_items`, `excluded_items`, `sprint_iterations`, `area_paths` | Must |
| FR-3 | Define `IWorkspaceModeStore` with read/write methods for mode configuration | Must |
| FR-4 | Migrate existing databases by bumping schema version and adding new tables | Must |
| FR-5 | Default to Sprint mode for existing workspaces | Must |
| FR-6 | Expand `display.tree_depth` to support `{ upward, downward, sideways }` object | Must |
| FR-7 | Parse single int as downward depth for backward compatibility | Must |
| FR-8 | Add CLI flags `--depth-up`, `--depth-down`, `--depth-side` on `twig tree` | Must |
| FR-9 | Trim parent chain to upward depth in all tree renderers | Must |
| FR-10 | Add `workspace.working_level` config setting | Must |
| FR-11 | Dim items above the working level in tree views | Must |
| FR-12 | Discover type hierarchy from `IProcessConfigurationProvider` for working level resolution | Must |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | All new types registered in `TwigJsonContext` for AOT compatibility |
| NFR-2 | Schema migration is non-destructive (existing data preserved) |
| NFR-3 | No reflection-based DI registration |
| NFR-4 | All new code covered by unit tests |
| NFR-5 | Zero behavioral change for users who do not configure new options |

---

## Proposed Design

### Architecture Overview

The design adds three independent but complementary subsystems layered across the existing Twig architecture:

```
┌─────────────────────────────────────────────────────────┐
│                    CLI / MCP Entry Points                │
│  TreeCommand  WorkspaceCommand  ReadTools                │
│  (--depth-up, --depth-down, --depth-side flags)          │
│  (working-level dim rendering)                          │
├─────────────────────────────────────────────────────────┤
│                      Rendering Layer                     │
│  SpectreRenderer  HumanOutputFormatter  IAsyncRenderer   │
│  (upward trim, downward limit, working-level dim)       │
├─────────────────────────────────────────────────────────┤
│                      Domain Layer                        │
│  WorkspaceMode  TreeDepthConfig  WorkingLevelResolver    │
│  IWorkspaceModeStore  BacklogHierarchyService            │
├─────────────────────────────────────────────────────────┤
│                   Infrastructure Layer                   │
│  SqliteWorkspaceModeStore  TwigConfiguration              │
│  SqliteCacheStore (schema v10)  TwigJsonContext           │
└─────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkspaceMode` (Domain Value Object)

```csharp
// src/Twig.Domain/ValueObjects/WorkspaceMode.cs
public sealed record WorkspaceMode
{
    public static readonly WorkspaceMode Sprint = new("Sprint");
    public static readonly WorkspaceMode Area = new("Area");
    public static readonly WorkspaceMode Recent = new("Recent");

    public string Value { get; }
    private WorkspaceMode(string value) => Value = value;

    public static WorkspaceMode? TryParse(string value) => value switch
    {
        "Sprint" => Sprint,
        "Area" => Area,
        "Recent" => Recent,
        _ => null
    };
}
```

Using a sealed record with static instances (same pattern as `WorkItemType`) rather than an enum, for extensibility and AOT-safe serialization.

#### 2. `TreeDepthConfig` (Domain Value Object)

```csharp
// src/Twig.Domain/ValueObjects/TreeDepthConfig.cs
public sealed record TreeDepthConfig(int Upward = 2, int Downward = 10, int Sideways = 1)
{
    /// <summary>
    /// Creates a TreeDepthConfig from a single int (backward compatibility).
    /// Maps the value to Downward, uses defaults for Upward and Sideways.
    /// </summary>
    public static TreeDepthConfig FromSingleValue(int downward)
        => new(Downward: downward);
}
```

#### 3. `IWorkspaceModeStore` (Domain Interface)

```csharp
// src/Twig.Domain/Interfaces/IWorkspaceModeStore.cs
public interface IWorkspaceModeStore
{
    Task<WorkspaceMode> GetActiveModeAsync(CancellationToken ct = default);
    Task SetActiveModeAsync(WorkspaceMode mode, CancellationToken ct = default);

    Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default);
    Task AddTrackedItemAsync(int id, string trackingMode, CancellationToken ct = default);
    Task RemoveTrackedItemAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<int>> GetExcludedItemIdsAsync(CancellationToken ct = default);
    Task AddExcludedItemAsync(int id, CancellationToken ct = default);
    Task RemoveExcludedItemAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<SprintIterationEntry>> GetSprintIterationsAsync(CancellationToken ct = default);
    Task SetSprintIterationsAsync(IReadOnlyList<SprintIterationEntry> entries, CancellationToken ct = default);

    Task<IReadOnlyList<AreaPathEntry>> GetAreaPathsAsync(CancellationToken ct = default);
    Task SetAreaPathsAsync(IReadOnlyList<AreaPathEntry> entries, CancellationToken ct = default);
}
```

#### 4. Schema Migration (SqliteCacheStore)

The current schema uses a **drop-and-recreate** strategy when the schema version changes. This is acceptable for new tables because:
- The cache is rebuilt from ADO on next sync
- The new workspace-mode tables store configuration, not cached data
- We need to add a migration to preserve workspace-mode configuration in a `context` key

**Approach:** Bump `SchemaVersion` from 9 to 10. Add new tables to the DDL. For the workspace mode default, write `workspace_mode = Sprint` into the `context` table during `EnsureSchema()` when schema is rebuilt.

New DDL additions:
```sql
CREATE TABLE tracked_items (
    id INTEGER PRIMARY KEY,
    mode TEXT NOT NULL DEFAULT 'single',
    created_at TEXT NOT NULL
);

CREATE TABLE excluded_items (
    id INTEGER PRIMARY KEY,
    created_at TEXT NOT NULL
);

CREATE TABLE sprint_iterations (
    expression TEXT NOT NULL,
    type TEXT NOT NULL DEFAULT 'relative',
    PRIMARY KEY (expression, type)
);

CREATE TABLE area_paths (
    path TEXT NOT NULL,
    semantics TEXT NOT NULL DEFAULT 'under',
    PRIMARY KEY (path, semantics)
);
```

#### 5. DisplayConfig Expansion

The `DisplayConfig.TreeDepth` (int) property is kept for backward compatibility but complemented by a `TreeDepthConfig` property:

```csharp
public sealed class DisplayConfig
{
    // Legacy: preserved for backward-compat JSON deserialization
    public int TreeDepth { get; set; } = 10;

    // New: structured depth config (populated from TreeDepth or explicit object)
    public TreeDepthConfig? TreeDepthDimensions { get; set; }

    /// <summary>
    /// Resolves the effective tree depth configuration.
    /// Priority: TreeDepthDimensions (explicit object) > TreeDepth (legacy single int).
    /// </summary>
    public TreeDepthConfig ResolveTreeDepth()
        => TreeDepthDimensions ?? TreeDepthConfig.FromSingleValue(TreeDepth);
}
```

This allows both config forms:
```json
// Legacy (backward compatible)
{ "display": { "treeDepth": 5 } }

// New (explicit dimensions)
{ "display": { "treeDepthDimensions": { "upward": 3, "downward": 15, "sideways": 2 } } }
```

#### 6. Working Level Resolution

```csharp
// src/Twig.Domain/Services/WorkingLevelResolver.cs
public static class WorkingLevelResolver
{
    /// <summary>
    /// Determines whether a work item is above the working level.
    /// Uses the backlog hierarchy type-level map to compare the item's type
    /// level against the configured working level type.
    /// Returns true if the item should be dimmed (above working level).
    /// </summary>
    public static bool IsAboveWorkingLevel(
        string itemTypeName,
        string workingLevelTypeName,
        IReadOnlyDictionary<string, int> typeLevelMap)
    {
        if (!typeLevelMap.TryGetValue(workingLevelTypeName, out var workingLevel))
            return false;
        if (!typeLevelMap.TryGetValue(itemTypeName, out var itemLevel))
            return false;
        return itemLevel < workingLevel; // lower level number = higher in hierarchy
    }
}
```

#### 7. Rendering Changes

**Parent chain trimming (upward depth):** Applied at the rendering layer, not the data layer. `GetParentChainAsync` continues to fetch the full chain — the renderer slices it to `TreeDepthConfig.Upward` levels. This preserves data for navigation while controlling visual output.

**Working-level dimming:** Both `SpectreRenderer` and `HumanOutputFormatter` check `WorkingLevelResolver.IsAboveWorkingLevel()` for each parent-chain item and apply dimmed styling. The focused item and children are never dimmed regardless of working level.

### Data Flow

**Tree depth resolution for `twig tree --depth-up 3 --depth-down 20`:**
```
CLI args → TwigCommands.Tree(depthUp=3, depthDown=20)
  → TreeCommand.ExecuteAsync()
    → Merge CLI overrides with config.Display.ResolveTreeDepth()
    → depthConfig = { Upward: 3, Downward: 20, Sideways: 1 }
    → parentChain = GetParentChainAsync(parentId)
    → trimmedParents = parentChain[^depthConfig.Upward..]
    → children limited to depthConfig.Downward
    → Renderer receives trimmedParents + limited children
```

**Working level dimming for `workspace.working_level = "Task"`:**
```
Config load → workspace.working_level = "Task"
  → SpectreRenderer receives workingLevelTypeName
  → For each parent chain item:
    → typeLevelMap.TryGetValue("Epic") → level 0
    → typeLevelMap.TryGetValue("Task") → level 2
    → 0 < 2 → dim this parent
  → Render with [dim] markup on parent nodes above working level
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Sealed record for `WorkspaceMode`** | Matches `WorkItemType` pattern; extensible without enum limitations; AOT-safe |
| **Keep `TreeDepth` property for backward compat** | Avoids breaking existing configs; `ResolveTreeDepth()` unifies both forms |
| **Trim parents at render layer, not data layer** | Preserves full chain for navigation; only visual output is affected |
| **Schema version bump (9→10)** | Drop-and-recreate is the established pattern; workspace mode defaults set on rebuild |
| **Working level stored as type name string, not level int** | Process-agnostic — type names are dynamic; level numbers are computed at runtime |
| **`IWorkspaceModeStore` as separate interface** | Single Responsibility; mode config is distinct from context or pending changes |

---

## Dependencies

### External Dependencies
- No new NuGet packages required
- All existing packages are sufficient

### Internal Dependencies
- `BacklogHierarchyService.GetTypeLevelMap()` — already exists, used for working level resolution
- `IProcessConfigurationProvider` — already exists, provides process type hierarchy
- `SqliteCacheStore` schema — modified (version bump)
- `TwigJsonContext` — modified (new `[JsonSerializable]` entries)

### Sequencing Constraints
- Issue #1953 (domain model + persistence) must complete before #1954 and #1955
- Issue #1954 (tree depth) and #1955 (working level) are independent of each other

---

## Impact Analysis

### Backward Compatibility
- **Config files:** Fully backward compatible. Single-int `treeDepth` continues to work via `ResolveTreeDepth()`. New `treeDepthDimensions` is optional.
- **Database:** Schema version bump triggers drop-and-recreate. Cache data is rebuilt on next sync. New workspace-mode tables are empty with Sprint default.
- **CLI flags:** Existing `--depth` flag preserved. New `--depth-up`, `--depth-down`, `--depth-side` flags are additive.
- **MCP tools:** `twig_tree` `depth` parameter continues to map to downward depth.

### Performance Implications
- Parent chain trimming reduces rendering work (fewer nodes to format)
- Working level check is O(1) per item via dictionary lookup
- New SQLite tables add negligible schema overhead

### Components Affected
- **6 source projects** touched (Domain, Infrastructure, CLI, MCP, tests, TestKit)
- **~15 source files** modified
- **~8 new files** created
- No deleted files

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Schema version bump triggers full cache rebuild for all users | High | Medium | Expected behavior — documented in release notes; rebuild is automatic on next sync |
| Backward-compat config parsing edge cases | Low | Medium | Comprehensive test coverage for both single-int and object forms |
| Working level type name not in `typeLevelMap` | Low | Low | `IsAboveWorkingLevel` returns false (no dimming) when type is unknown — safe fallback |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should the `--depth` flag on `twig tree` map to downward (current behavior) or become a composite override? | Low | Recommend: keep `--depth` as downward for backward compat; new `--depth-up`, `--depth-down`, `--depth-side` for explicit control |
| 2 | Should the default upward depth of 2 include the focused item or only ancestors? | Low | Recommend: 2 means "show up to 2 parent levels above focused item" — focused item is always shown |
| 3 | Should working-level dimming apply to the `twig workspace` table view or only tree views? | Low | Recommend: tree views only initially; workspace table has its own visual hierarchy via state grouping |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/WorkspaceMode.cs` | Workspace mode value object (Sprint, Area, Recent) |
| `src/Twig.Domain/ValueObjects/TreeDepthConfig.cs` | Three-dimensional tree depth configuration |
| `src/Twig.Domain/ValueObjects/TrackedItem.cs` | Tracked work item entry for workspace modes |
| `src/Twig.Domain/ValueObjects/SprintIterationEntry.cs` | Sprint iteration configuration entry |
| `src/Twig.Domain/Interfaces/IWorkspaceModeStore.cs` | Interface for workspace mode persistence |
| `src/Twig.Domain/Services/WorkingLevelResolver.cs` | Static helper to check if item is above working level |
| `src/Twig.Infrastructure/Persistence/SqliteWorkspaceModeStore.cs` | SQLite implementation of IWorkspaceModeStore |
| `tests/Twig.Domain.Tests/ValueObjects/WorkspaceModeTests.cs` | Domain model unit tests |
| `tests/Twig.Domain.Tests/ValueObjects/TreeDepthConfigTests.cs` | TreeDepthConfig unit tests |
| `tests/Twig.Domain.Tests/Services/WorkingLevelResolverTests.cs` | Working level resolution tests |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkspaceModeStoreTests.cs` | SQLite store tests |
| `tests/Twig.Infrastructure.Tests/Config/TreeDepthConfigParsingTests.cs` | Config parsing backward compat tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `WorkspaceConfig` section, `TreeDepthConfig` property on `DisplayConfig`, `ResolveTreeDepth()` method, update `SetValue` cases |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Bump SchemaVersion 9→10, add new table DDL |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Register new `[JsonSerializable]` types |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `IWorkspaceModeStore` → `SqliteWorkspaceModeStore` |
| `src/Twig/Commands/TreeCommand.cs` | Accept depth dimensions, trim parent chain, pass working level |
| `src/Twig/Commands/WorkspaceCommand.cs` | Pass working level info to renderer |
| `src/Twig/Program.cs` | Add `--depth-up`, `--depth-down`, `--depth-side` CLI flags to `Tree` command |
| `src/Twig/Rendering/SpectreRenderer.cs` | Apply upward trim, working-level dim rendering |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Update `RenderTreeAsync` signature for depth config |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Apply upward trim, working-level dim rendering |
| `src/Twig/Formatters/IOutputFormatter.cs` | Update `FormatTree` signature |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Update `FormatTree` signature |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Update `FormatTree` signature |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Update `FormatTree` signature |
| `src/Twig/Commands/ConfigCommand.cs` | Handle `display.treedepth` get for new schema |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Update depth handling for dimensions |
| `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Update tree depth tests for backward compat |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Update for depth dimensions |
| `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | Update for depth config |

---

## ADO Work Item Structure

### Issue #1953: Workspace Mode Domain Model & Persistence

**Goal:** Define the workspace mode domain model, SQLite tables, and `IWorkspaceModeStore` interface. This is the foundation that all future workspace-mode features build upon.

**Prerequisites:** None (first in sequence)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1: Define WorkspaceMode value object | Create `WorkspaceMode` sealed record with Sprint/Area/Recent static instances, `TryParse`, value equality. Follow `WorkItemType` pattern. | `src/Twig.Domain/ValueObjects/WorkspaceMode.cs` | ~30 LoC |
| T2: Define supporting value objects | Create `TrackedItem` (id, mode, createdAt), `SprintIterationEntry` (expression, type), workspace-specific `AreaPathConfig` (path, semantics) records. | `src/Twig.Domain/ValueObjects/TrackedItem.cs`, `src/Twig.Domain/ValueObjects/SprintIterationEntry.cs` | ~40 LoC |
| T3: Define IWorkspaceModeStore interface | Create read/write interface for mode configuration: get/set active mode, CRUD for tracked items, excluded items, sprint iterations, area paths. | `src/Twig.Domain/Interfaces/IWorkspaceModeStore.cs` | ~30 LoC |
| T4: Add SQLite tables and bump schema | Add `tracked_items`, `excluded_items`, `sprint_iterations`, `area_paths` to DDL. Bump SchemaVersion 9→10. Write default `workspace_mode=Sprint` in context table on rebuild. | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | ~40 LoC |
| T5: Implement SqliteWorkspaceModeStore | SQLite implementation of `IWorkspaceModeStore` using parameterized SQL. Follow patterns from `SqliteContextStore` and `SqlitePendingChangeStore`. | `src/Twig.Infrastructure/Persistence/SqliteWorkspaceModeStore.cs` | ~180 LoC |
| T6: Register in DI and JSON context | Register `IWorkspaceModeStore` in `TwigServiceRegistration.cs`. Add `[JsonSerializable]` entries for new types in `TwigJsonContext.cs`. | `src/Twig.Infrastructure/TwigServiceRegistration.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | ~15 LoC |
| T7: Unit tests for domain model | Test `WorkspaceMode.TryParse`, value equality, `TrackedItem` and `SprintIterationEntry` construction. | `tests/Twig.Domain.Tests/ValueObjects/WorkspaceModeTests.cs`, `tests/Twig.Domain.Tests/ValueObjects/TrackedItemTests.cs` | ~80 LoC |
| T8: Integration tests for SQLite store | Test SqliteWorkspaceModeStore CRUD operations with in-memory SQLite. Test schema rebuild creates tables. Test default mode is Sprint. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkspaceModeStoreTests.cs` | ~150 LoC |

**Acceptance Criteria:**
- [ ] `WorkspaceMode` has Sprint, Area, Recent variants with `TryParse`
- [ ] SQLite schema v10 creates four new tables without breaking existing tables
- [ ] `IWorkspaceModeStore` supports full CRUD for all workspace mode entities
- [ ] Default mode is Sprint for new and migrated databases
- [ ] All new types registered in `TwigJsonContext`
- [ ] Unit and integration tests pass

---

### Issue #1954: Tree Depth Configuration Expansion

**Goal:** Expand `display.tree_depth` from a single integer to three dimensions (upward, downward, sideways) with backward-compatible config parsing. Wire into all tree renderers.

**Prerequisites:** None (independent of #1953)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1: Define TreeDepthConfig value object | Create sealed record with Upward (default 2), Downward (default 10), Sideways (default 1) properties and `FromSingleValue` factory. | `src/Twig.Domain/ValueObjects/TreeDepthConfig.cs` | ~25 LoC |
| T2: Expand DisplayConfig with backward compat | Add `TreeDepthDimensions` property to `DisplayConfig`. Add `ResolveTreeDepth()` method. Update `SetValue` cases for `display.treedepth.upward`, `.downward`, `.sideways`. Keep legacy `TreeDepth` property. Register in `TwigJsonContext`. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | ~60 LoC |
| T3: Add CLI flags to Tree command | Add `--depth-up`, `--depth-down`, `--depth-side` parameters to `TwigCommands.Tree()` and `TreeCommand.ExecuteAsync()`. Merge CLI overrides with config via `ResolveTreeDepth()`. | `src/Twig/Program.cs`, `src/Twig/Commands/TreeCommand.cs` | ~40 LoC |
| T4: Apply upward depth trim in renderers | In `SpectreRenderer.BuildSpectreTreeAsync`, `BuildTreeViewAsync`, `RenderTreeAsync`, and `HumanOutputFormatter.FormatTree`: trim parent chain to upward depth from config. Update `IAsyncRenderer.RenderTreeAsync` signature to accept `TreeDepthConfig`. | `src/Twig/Rendering/SpectreRenderer.cs`, `src/Twig/Rendering/IAsyncRenderer.cs`, `src/Twig/Formatters/HumanOutputFormatter.cs` | ~80 LoC |
| T5: Update formatter interfaces and implementations | Update `IOutputFormatter.FormatTree`, `MinimalOutputFormatter`, `JsonOutputFormatter`, `JsonCompactOutputFormatter` to accept `TreeDepthConfig` or use downward depth. Update `ConfigCommand` for `display.treedepth` get. | `src/Twig/Formatters/IOutputFormatter.cs`, `src/Twig/Formatters/MinimalOutputFormatter.cs`, `src/Twig/Formatters/JsonOutputFormatter.cs`, `src/Twig/Formatters/JsonCompactOutputFormatter.cs`, `src/Twig/Commands/ConfigCommand.cs` | ~50 LoC |
| T6: Update MCP ReadTools | Update `ReadTools.Tree` to use `ResolveTreeDepth().Downward` and apply upward trim to parent chain. | `src/Twig.Mcp/Tools/ReadTools.cs` | ~15 LoC |
| T7: Unit tests for TreeDepthConfig | Test default values, `FromSingleValue`, `ResolveTreeDepth()` priority. Test backward-compat config parsing (single int → downward). Test `SetValue` for new paths. | `tests/Twig.Domain.Tests/ValueObjects/TreeDepthConfigTests.cs`, `tests/Twig.Infrastructure.Tests/Config/TreeDepthConfigParsingTests.cs` | ~100 LoC |
| T8: Update existing tests | Update `TwigConfigurationTests`, `TreeCommandTests`, `ConfigCommandTests` for new depth schema. | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs`, `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | ~60 LoC |

**Acceptance Criteria:**
- [ ] `TreeDepthConfig` supports upward (default 2), downward (default 10), sideways (default 1)
- [ ] Legacy single-int config `{ "treeDepth": 5 }` maps to downward=5, upward=2, sideways=1
- [ ] New object config `{ "treeDepthDimensions": { "upward": 3 } }` takes precedence
- [ ] `--depth-up`, `--depth-down`, `--depth-side` CLI flags override config
- [ ] Parent chain trimmed to upward depth in all tree renderers
- [ ] Existing `--depth` flag continues to control downward depth
- [ ] All tests pass including backward compatibility

---

### Issue #1955: Working Level Config with Dim Rendering

**Goal:** Add `workspace.working_level` configuration that dims items above the specified type level in tree views. Discover type hierarchy from `IProcessConfigurationProvider`.

**Prerequisites:** None (independent of #1953 and #1954, but benefits from #1954's renderer refactoring)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1: Add WorkspaceConfig section | Add `WorkspaceConfig` class with `WorkingLevel` string property. Add to `TwigConfiguration`. Update `SetValue` for `workspace.workinglevel`. Register in `TwigJsonContext`. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | ~30 LoC |
| T2: Create WorkingLevelResolver | Static helper with `IsAboveWorkingLevel(itemTypeName, workingLevelTypeName, typeLevelMap)` using `BacklogHierarchyService.GetTypeLevelMap()`. Returns bool for dimming decision. | `src/Twig.Domain/Services/WorkingLevelResolver.cs` | ~30 LoC |
| T3: Wire working level into TreeCommand | Load process config, compute `typeLevelMap`, resolve working level from config, pass to renderer. Apply to both Spectre async and sync formatter paths. | `src/Twig/Commands/TreeCommand.cs` | ~30 LoC |
| T4: Dim rendering in SpectreRenderer | In `BuildSpectreTreeAsync` and `RenderTreeAsync`: apply `[dim]` markup to parent chain items where `IsAboveWorkingLevel` returns true. Accept `workingLevelTypeName` parameter. | `src/Twig/Rendering/SpectreRenderer.cs` | ~40 LoC |
| T5: Dim rendering in HumanOutputFormatter | In `FormatTree`: apply dim ANSI codes to parent chain items above working level. Accept working level params in extended `FormatTree` overload. | `src/Twig/Formatters/HumanOutputFormatter.cs` | ~30 LoC |
| T6: Wire working level into WorkspaceCommand | Pass working level info when building sprint hierarchy views. Apply dimming in workspace tree sections. | `src/Twig/Commands/WorkspaceCommand.cs` | ~20 LoC |
| T7: Unit tests for WorkingLevelResolver | Test `IsAboveWorkingLevel` with Basic, Agile, Scrum type hierarchies. Test unknown type returns false. Test edge cases (working level = top level, bottom level). | `tests/Twig.Domain.Tests/Services/WorkingLevelResolverTests.cs` | ~80 LoC |
| T8: Integration tests for dim rendering | Test that SpectreRenderer and HumanOutputFormatter apply dim markup when working level is set. Test no dimming when working level is null. Use `TestConsole` for Spectre verification. | `tests/Twig.Cli.Tests/Rendering/WorkingLevelDimRenderingTests.cs` | ~100 LoC |

**Acceptance Criteria:**
- [ ] `workspace.working_level` config accepts type name string (e.g., "Task", "Issue")
- [ ] Items above working level are dimmed in `twig tree` output
- [ ] Focused item is never dimmed regardless of working level
- [ ] Children of focused item are never dimmed
- [ ] No dimming when `workspace.working_level` is not configured (default behavior preserved)
- [ ] Type hierarchy discovered from `IProcessConfigurationProvider` at runtime
- [ ] Works correctly across all ADO process templates (Agile, Scrum, CMMI, Basic)

---

## PR Groups

### PG-1: Domain Model & Persistence (Issue #1953)

| Attribute | Value |
|-----------|-------|
| **Tasks** | #1953 T1–T8 |
| **Classification** | Wide — many new files, straightforward patterns |
| **Est. LoC** | ~570 |
| **Est. Files** | ~12 (8 new, 4 modified) |
| **Successor** | None (independent, but unlocks future mode commands) |

**Rationale:** All domain model, persistence, and DI registration changes form a cohesive, self-contained PR. The schema version bump is the most impactful change but is the established migration pattern.

### PG-2: Tree Depth Expansion (Issue #1954)

| Attribute | Value |
|-----------|-------|
| **Tasks** | #1954 T1–T8 |
| **Classification** | Deep — touches many renderers, requires careful interface evolution |
| **Est. LoC** | ~430 |
| **Est. Files** | ~15 (3 new, 12 modified) |
| **Successor** | None (independent) |

**Rationale:** The tree depth expansion is a cross-cutting change that modifies interfaces, commands, renderers, formatters, and MCP tools. It's cohesive around the "tree depth" concept and best reviewed as a unit.

### PG-3: Working Level Dim Rendering (Issue #1955)

| Attribute | Value |
|-----------|-------|
| **Tasks** | #1955 T1–T8 |
| **Classification** | Deep — rendering logic changes, process-config integration |
| **Est. LoC** | ~360 |
| **Est. Files** | ~10 (4 new, 6 modified) |
| **Successor** | None (independent, benefits from PG-2 renderer familiarity) |

**Rationale:** Working level dimming is a focused rendering feature. While it touches some of the same files as PG-2, the changes are orthogonal (dimming vs. depth limiting). Reviewing separately keeps PRs focused.

### Execution Order

PG-1, PG-2, and PG-3 are **independent** and can be developed and merged in parallel. However, if serialized:
- **PG-1 first** — establishes the schema bump that PG-2 and PG-3 may need to coordinate with
- **PG-2 second** — modifies renderer interfaces that PG-3 also touches
- **PG-3 last** — smallest PR, benefits from merged PG-2 renderer changes

---

## References

- [Architecture Overview](../architecture/overview.md)
- [Data Layer Architecture](../architecture/data-layer.md)
- [CLI Command Architecture](../architecture/commands.md)
- ADO Work Items: #1946 (parent), #1953, #1954, #1955
