---
work_item_id: 1949
title: "Area Mode"
type: Issue
---

# Area Mode — Solution Design & Implementation Plan

| Field | Value |
|---|---|
| **Work Item** | #1949 — Area Mode |
| **Type** | Issue (under Epic #1945 — Workspace Modes & Tracking) |
| **Author** | Generated via codebase analysis |
| **Status** | 🔨 In Progress — 1/2 PR groups merged |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

Area Mode adds ADO area-path filtering to the twig workspace, enabling users to
scope their views to one or more configured area paths. The feature consists of
two complementary capabilities: (1) **workspace area configuration** — CLI
commands to add, remove, and list area-path filters with exact-match or "under"
(subtree) semantics, plus auto-population from team settings via the ADO
`teamfieldvalues` API; and (2) an **area-filtered read-only view** that renders
work items scoped to configured areas while dimming out-of-area parent items for
structural context. The codebase already has significant infrastructure in place
— the `AreaPath` value object, `AreaPathEntry` configuration model with
`IncludeChildren` flag, WIQL `UNDER`/`=` operator support in `WiqlQueryBuilder`,
and the `GetTeamAreaPathsAsync()` endpoint in `IIterationService` — making this
primarily an integration and rendering effort rather than foundational
architecture.

---

## Background

### Current State

Twig's workspace view is currently scoped by **iteration path** (sprint) and
optionally by **assignee** (user display name). Area paths exist in the codebase
but are only partially utilized:

| Component | Area Path Support | Status |
|---|---|---|
| `AreaPath` value object (`ValueObjects/AreaPath.cs`) | Validated, backslash-separated, segments cached | ✅ Complete |
| `WorkItem.AreaPath` property | Stored on aggregate, mapped from ADO | ✅ Complete |
| `AreaPathEntry` config type | `Path` + `IncludeChildren` flag | ✅ Complete |
| `DefaultsConfig.ResolveAreaPaths()` | 3-tier fallback: Entries → Paths → Path | ✅ Complete |
| `WiqlQueryBuilder.AppendDefaultAreaPaths()` | OR-joined UNDER/= clauses | ✅ Complete |
| `RefreshCommand` WIQL building | Area filter applied to refresh queries | ✅ Complete |
| `IIterationService.GetTeamAreaPathsAsync()` | Fetches `teamfieldvalues` from ADO | ✅ Complete |
| `InitCommand` auto-population | Stores team areas in config during init | ✅ Complete |
| SQLite `work_items.area_path` column | Stored but not indexed | ⚠️ Partial |
| `IWorkItemRepository` area queries | No `GetByAreaAsync()` methods | ❌ Missing |
| CLI area management commands | No `twig area add/remove/list` | ❌ Missing |
| Area-filtered workspace view | No rendering with dimmed parents | ❌ Missing |
| `twig workspace --area` / `twig area` commands | Not implemented | ❌ Missing |

### Call-Site Audit: Area Path Usage

| File | Method/Location | Current Usage | Impact |
|---|---|---|---|
| `Domain/ValueObjects/AreaPath.cs` | `Parse()`, segments | Value object — validated parsing | None (extend with `IsUnder()`) |
| `Domain/Aggregates/WorkItem.cs` | `AreaPath` property, `CreateSeed()` | Immutable on aggregate | None (read-only consumer) |
| `Infrastructure/Ado/AdoResponseMapper.cs` | `MapWorkItem()` | Extracts `System.AreaPath` from ADO | None |
| `Infrastructure/Config/TwigConfiguration.cs` | `DefaultsConfig`, `AreaPathEntry` | Config storage + 3-tier resolution | Extend with `area` sub-commands |
| `Domain/Services/WiqlQueryBuilder.cs` | `AppendDefaultAreaPaths()` | WIQL clause building | None (already supports both modes) |
| `Commands/RefreshCommand.cs` | WIQL area filter (lines 70-99) | Applies config area paths to refresh | None |
| `Commands/QueryCommand.cs` | `--areaPath` parameter | Explicit CLI filter | None |
| `Commands/ConfigCommand.cs` | `defaults.areapath` / `defaults.areapaths` | Read/write config keys | Extend with `defaults.areapathentries` |
| `Infrastructure/Persistence/SqliteWorkItemRepository.cs` | `GetByIterationAsync()` | No area filtering | Add area-filtered queries |
| `Infrastructure/Persistence/SqliteCacheStore.cs` | DDL schema | `area_path` column, no index | Add index |
| `Commands/WorkspaceCommand.cs` | `ExecuteAsync()` / `ExecuteSyncAsync()` | Sprint + assignee filtering only | Add area filtering path |
| `Rendering/SpectreRenderer.cs` | `RenderWorkspaceAsync()` | Table rows with Spectre markup | Add dim styling for out-of-area items |
| `ReadModels/SprintHierarchy.cs` | `Build()`, `BuildAssigneeTree()` | `IsSprintItem` flag on nodes | Reuse pattern for `IsInArea` flag |
| `Mcp/Tools/NavigationTools.cs` | `twig_query` | Passes `DefaultAreaPaths` to WIQL | None (already working) |
| `Domain/Interfaces/IIterationService.cs` | `GetTeamAreaPathsAsync()` | Returns `(Path, IncludeChildren)` list | Reuse for auto-populate |
| `Infrastructure/Serialization/TwigJsonContext.cs` | `[JsonSerializable]` attributes | `AreaPathEntry`, `List<AreaPathEntry>` | May need new types |

---

## Problem Statement

Users working in large ADO projects with many teams need to focus their twig
workspace on specific area paths. Currently, twig shows all work items in the
current sprint regardless of area, which creates noise when a project has dozens
of teams with overlapping iterations. The `twig refresh` command already filters
by configured area paths, but the workspace view and ad-hoc queries do not
leverage this filtering — and there is no ergonomic way to manage area-path
configuration beyond editing the config file directly.

Specific pain points:
1. **No CLI for area management** — adding/removing area paths requires manual
   JSON editing of `.twig/config`.
2. **No area-scoped workspace view** — `twig workspace` shows all sprint items
   regardless of area, overwhelming users in multi-team projects.
3. **No "under" vs "exact" control in views** — the config supports both, but
   there's no command surface to manage this distinction.
4. **No team auto-populate at runtime** — `twig init` fetches team areas, but
   there's no way to re-sync them after team settings change.

---

## Goals and Non-Goals

### Goals
- **G-1**: Provide `twig area add`, `twig area remove`, `twig area list`, and
  `twig area sync` commands for managing workspace area-path filters.
- **G-2**: Support both `exact` (=) and `under` (subtree) match semantics per
  area-path entry, defaulting to `under`.
- **G-3**: Provide a `twig area` view (and `twig workspace --area` flag) that
  renders only work items matching configured area paths, with out-of-area
  parent items dimmed for hierarchy context.
- **G-4**: Auto-populate area paths from ADO team settings via
  `GetTeamAreaPathsAsync()` with `twig area sync`.
- **G-5**: Area filtering works against the local SQLite cache — no additional
  ADO API calls for the view itself.

### Non-Goals
- **NG-1**: Area Mode does not replace Sprint Mode — it is an orthogonal filter
  that can combine with sprint filtering.
- **NG-2**: No server-side query modification — area filtering is applied
  locally to cached data. The existing `RefreshCommand` WIQL area filter
  continues to handle server-side scoping.
- **NG-3**: No interactive area-path picker/browser — auto-populate from team
  settings is sufficient for initial release.
- **NG-4**: No per-view area-path overrides (e.g., different areas for workspace
  vs tree) — single workspace-level configuration.
- **NG-5**: No area-path hierarchy visualization (tree of all areas) — out of
  scope for this issue.

---

## Requirements

### Functional Requirements

| ID | Requirement |
|---|---|
| **FR-1** | `twig area add <path> [--exact]` adds an area path to the workspace config. Default semantics: `under` (subtree). With `--exact`: exact match only. |
| **FR-2** | `twig area remove <path>` removes a configured area path. |
| **FR-3** | `twig area list` displays currently configured area paths with their match semantics. |
| **FR-4** | `twig area sync` fetches team area paths from ADO (`teamfieldvalues` API) and replaces the current config. |
| **FR-5** | `twig area` (no subcommand) renders an area-filtered workspace view. |
| **FR-6** | Area filtering queries the local SQLite cache by `area_path` column. |
| **FR-7** | Out-of-area parent items are included in the view for hierarchy context but rendered with `[dim]` styling. |
| **FR-8** | Area-path configuration is persisted in `.twig/config` under `defaults.areaPathEntries`. |
| **FR-9** | The area view works in all output formats: human (Spectre), JSON, and minimal. |

### Non-Functional Requirements

| ID | Requirement |
|---|---|
| **NFR-1** | Area filtering must operate entirely from cache — no ADO API calls for view rendering. |
| **NFR-2** | Adding a SQLite index on `area_path` for efficient filtering. |
| **NFR-3** | All new types registered in `TwigJsonContext` for AOT compatibility. |
| **NFR-4** | No reflection — all serialization via source-generated JSON context. |
| **NFR-5** | Tests follow existing patterns: xUnit + Shouldly + NSubstitute. |

---

## Proposed Design

### Architecture Overview

Area Mode layers onto the existing workspace architecture without modifying the
core data flow. The design follows the same patterns used by Sprint Mode:

```
┌─────────────────┐     ┌──────────────────────┐     ┌────────────────────┐
│   CLI Layer      │     │   Domain Layer        │     │  Infrastructure    │
│                  │     │                       │     │                    │
│  AreaCommand     │────▶│  AreaPathFilter (VO)  │     │  SqliteWorkItem    │
│  (add/remove/    │     │  AreaFilterService    │◀───▶│  Repository        │
│   list/sync)     │     │  (domain logic)       │     │  (area queries)    │
│                  │     │                       │     │                    │
│  WorkspaceCmd    │────▶│  SprintHierarchy      │     │  SqliteContextStore│
│  (--area flag)   │     │  .IsInArea flag on    │     │  (mode persistence)│
│                  │     │  hierarchy nodes      │     │                    │
│  SpectreRenderer │     │                       │     │  TwigConfiguration │
│  (dim styling)   │     │  Workspace ReadModel  │     │  (area entries)    │
└─────────────────┘     └──────────────────────┘     └────────────────────┘
```

### Key Components

#### 1. `AreaPathFilter` — Value Object (`Domain/ValueObjects/`)

A lightweight value object that encapsulates an area-path match specification:

```csharp
public readonly record struct AreaPathFilter(string Path, bool IncludeChildren)
{
    public bool Matches(AreaPath candidate) { ... }
}
```

- `Matches()` implements both exact (`=`) and subtree (`UNDER`) semantics.
- Exact match: `candidate.Value == Path` (ordinal, case-insensitive).
- Under match: `candidate.Value == Path` OR
  `candidate.Value.StartsWith(Path + "\\", OrdinalIgnoreCase)`.
- Reuses the existing `AreaPathEntry` config shape for serialization.

#### 2. `AreaFilterService` — Domain Service (`Domain/Services/`)

Pure domain logic for area-path matching against work item collections:

```csharp
public static class AreaFilterService
{
    public static IReadOnlyList<WorkItem> FilterByArea(
        IReadOnlyList<WorkItem> items,
        IReadOnlyList<AreaPathFilter> filters);
    
    public static bool IsInArea(
        AreaPath itemPath,
        IReadOnlyList<AreaPathFilter> filters);
}
```

- `FilterByArea()` returns items matching ANY configured filter (OR semantics).
- `IsInArea()` checks a single item — used for dimming logic in rendering.
- Stateless, static — no DI registration needed.

#### 3. `AreaCommand` — CLI Command (`Commands/`)

Implements the `twig area` command group with subcommands:

| Subcommand | Signature | Behavior |
|---|---|---|
| `add` | `twig area add <path> [--exact]` | Appends to `AreaPathEntries`, deduplicates by path |
| `remove` | `twig area remove <path>` | Removes matching entry from `AreaPathEntries` |
| `list` | `twig area list` | Displays configured entries with semantics indicator |
| `sync` | `twig area sync` | Fetches team areas via ADO API, replaces config |
| *(default)* | `twig area` | Renders area-filtered workspace view |

The `add` and `remove` subcommands persist changes to `.twig/config` via
`TwigConfiguration.SaveAsync()`. The `sync` subcommand reuses
`IIterationService.GetTeamAreaPathsAsync()`.

#### 4. Repository Extensions (`Infrastructure/Persistence/`)

New query methods on `IWorkItemRepository` and `SqliteWorkItemRepository`:

```csharp
Task<IReadOnlyList<WorkItem>> GetByAreaPathsAsync(
    IReadOnlyList<(string Path, bool IncludeChildren)> areaPaths,
    CancellationToken ct = default);
```

SQL implementation uses parameterized LIKE for `UNDER` semantics and exact
`=` for exact match, OR-joined across entries. A new index on `area_path`
ensures performance.

#### 5. Area-Filtered Workspace View

The area view follows the same two-pass rendering pattern as the sprint view:

1. **Filter**: Query local cache for items matching configured area paths.
2. **Hydrate parents**: Walk parent chains for hierarchy context.
3. **Build hierarchy**: Reuse `SprintHierarchy.Build()` with an extended node
   model that tracks both `IsSprintItem` and `IsInArea`.
4. **Render**: Spectre table with `[dim]` markup for out-of-area parents.

The `SprintHierarchyNode.IsSprintItem` property already provides the exact
pattern needed — out-of-area parents are analogous to non-sprint parent context
nodes, which are already dimmed in the current rendering.

### Data Flow — `twig area` (View)

```
1. Read AreaPathEntries from TwigConfiguration
2. Query SqliteWorkItemRepository.GetByAreaPathsAsync(entries)
3. Walk parent chains for hierarchy context (existing GetParentChainAsync)
4. Mark each parent as in-area or out-of-area via AreaFilterService.IsInArea()
5. Build SprintHierarchy with IsSprintItem = IsInArea
6. Render via SpectreRenderer — dim out-of-area nodes
```

### Data Flow — `twig area add <path>`

```
1. Validate path via AreaPath.Parse()
2. Check for duplicates in config.Defaults.AreaPathEntries
3. Append new AreaPathEntry(Path, IncludeChildren: !exact)
4. Save config via TwigConfiguration.SaveAsync()
5. Display confirmation
```

### Data Flow — `twig area sync`

```
1. Call IIterationService.GetTeamAreaPathsAsync()
2. Map results to AreaPathEntry list
3. Replace config.Defaults.AreaPathEntries
4. Save config via TwigConfiguration.SaveAsync()
5. Display synced entries
```

### Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| **DD-1**: Area filtering scope | Local cache only | Avoids API calls during view rendering; `twig refresh` already handles server-side area filtering |
| **DD-2**: Default match semantics | `under` (subtree) | Matches ADO team settings default; most intuitive for hierarchical areas |
| **DD-3**: Config storage location | `defaults.areaPathEntries` | Reuses existing `AreaPathEntry` type; 3-tier fallback in `ResolveAreaPaths()` already handles this |
| **DD-4**: Parent dimming pattern | Reuse `IsSprintItem` flag | `SprintHierarchyNode.IsSprintItem=false` already dims parent context — same visual treatment applies to out-of-area parents |
| **DD-5**: `twig area` as default action | Show area-filtered view | Consistent with `twig workspace` (show) and `twig sprint` (show) patterns |
| **DD-6**: Schema version bump | Not required | No DDL changes needed — area_path column exists; index can be added conditionally |
| **DD-7**: Dedup on add | By path string (case-insensitive) | Prevents duplicate entries; user can remove and re-add to change semantics |
| **DD-8**: SQLite area index | Add to DDL + bump schema | Enables efficient `WHERE area_path = ? OR area_path LIKE ?` queries |

---

## Dependencies

### External Dependencies
- **Azure DevOps REST API** — `teamsettings/teamfieldvalues` endpoint (already integrated via `AdoIterationService`)
- **Spectre.Console** — `[dim]` markup for out-of-area parent rendering (already in use)

### Internal Dependencies
- `AreaPath` value object — extend with `IsUnder()` method
- `AreaPathEntry` config type — already exists, no changes needed
- `DefaultsConfig.ResolveAreaPaths()` — already returns the right shape
- `IIterationService.GetTeamAreaPathsAsync()` — already implemented
- `SprintHierarchy` — reuse parent-context dimming pattern
- `WiqlQueryBuilder` — no changes needed (already supports area filtering)

### Sequencing Constraints
- Issue #1961 (add/remove/sync) must be completed before #1962 (area view) because
  the view depends on configured area paths and the `AreaPathFilter` value object.

---

## Impact Analysis

### Components Affected
| Component | Change Type | Scope |
|---|---|---|
| `Domain/ValueObjects/AreaPathFilter.cs` | **New** | Value object for area matching |
| `Domain/ValueObjects/AreaPath.cs` | **Modified** | Add `IsUnder()` helper |
| `Domain/Services/AreaFilterService.cs` | **New** | Static filtering logic |
| `Commands/AreaCommand.cs` | **New** | CLI command class |
| `Program.cs` (TwigCommands) | **Modified** | Register `twig area` subcommands |
| `DependencyInjection/CommandRegistrationModule.cs` | **Modified** | Register AreaCommand |
| `Infrastructure/Persistence/SqliteWorkItemRepository.cs` | **Modified** | Add area-filtered queries |
| `Domain/Interfaces/IWorkItemRepository.cs` | **Modified** | Add `GetByAreaPathsAsync()` |
| `Infrastructure/Persistence/SqliteCacheStore.cs` | **Modified** | Add area_path index, bump schema |
| `Rendering/SpectreRenderer.cs` | **Modified** | Area view rendering with dimming |
| `Rendering/IAsyncRenderer.cs` | **Modified** | Add `RenderAreaViewAsync()` |
| `Formatters/IOutputFormatter.cs` | **Modified** | Add `FormatAreaView()` |
| `Formatters/HumanOutputFormatter.cs` | **Modified** | Area view formatting |
| `Formatters/JsonOutputFormatter.cs` | **Modified** | Area view JSON output |
| `Infrastructure/Config/TwigConfiguration.cs` | **Modified** | Add `area.*` config paths |
| `Infrastructure/Serialization/TwigJsonContext.cs` | **Modified** | Register new types if any |

### Backward Compatibility
- No breaking changes. Area Mode is purely additive.
- Existing `defaults.areaPathEntries` config is preserved — commands read/write the same field.
- Schema version bump (9→10) will rebuild the cache, which is the existing migration pattern.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Schema version bump drops cached data | Low | Medium | This is the existing pattern; `twig refresh` re-populates. Users expect this on upgrades. |
| Area-path matching edge cases (case sensitivity, trailing backslash) | Medium | Low | `AreaPath.Parse()` already validates format; matching uses `OrdinalIgnoreCase` for robustness. |
| Large area-path lists cause slow SQLite queries | Low | Low | OR-joined LIKE clauses with index are efficient for typical team sizes (<10 entries). |

---

## Open Questions

| # | Question | Severity | Notes |
|---|---|---|---|
| **OQ-1** | Should `twig area` combine with sprint filtering (intersection) or be independent? | Low | Recommendation: independent filter on cached items. Sprint filtering is handled by `twig refresh` server-side. Area view queries the full cache, not just sprint items. |
| **OQ-2** | Should `twig area sync` merge with existing entries or replace them? | Low | Recommendation: replace. Team settings are the canonical source; manual additions should be re-added after sync if needed. The `list` command shows what was synced. |
| **OQ-3** | Should schema version be bumped for the area_path index, or add it conditionally? | Low | Recommendation: bump schema (9→10). The conditional-add approach adds complexity for marginal benefit — cache rebuild on upgrade is the existing pattern. |

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig.Domain/ValueObjects/AreaPathFilter.cs` | Value object for area-path matching with exact/under semantics |
| `src/Twig.Domain/Services/AreaFilterService.cs` | Static domain service for filtering work items by area paths |
| `src/Twig/Commands/AreaCommand.cs` | CLI command implementing `twig area add/remove/list/sync` and the area view |
| `tests/Twig.Domain.Tests/ValueObjects/AreaPathFilterTests.cs` | Unit tests for `AreaPathFilter` matching logic |
| `tests/Twig.Domain.Tests/Services/AreaFilterServiceTests.cs` | Unit tests for `AreaFilterService` |
| `tests/Twig.Cli.Tests/Commands/AreaCommandTests.cs` | Integration tests for area CLI commands |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig.Domain/ValueObjects/AreaPath.cs` | Add `IsUnder(string parentPath)` helper method |
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Add `GetByAreaPathsAsync()` method |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Implement `GetByAreaPathsAsync()` with area-path SQL filtering |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Add `idx_work_items_area` index to DDL; bump schema version 9→10 |
| `src/Twig/Program.cs` | Register `twig area` command group (add/remove/list/sync + default view) |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `AreaCommand` in DI |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `area.*` config key handling in `SetValue()`/`GetValue()` |
| `src/Twig/Commands/WorkspaceCommand.cs` | Wire `--area` flag to invoke area-filtered view |
| `src/Twig/Rendering/SpectreRenderer.cs` | Implement area-view table rendering with `[dim]` for out-of-area parents |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Add optional `RenderAreaViewAsync()` method (or reuse `RenderWorkspaceAsync` with area data) |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatAreaView()` method |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement area-view human output |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement area-view JSON output |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Register `AreaPathFilter` if used in serialization |

---

## ADO Work Item Structure

### Issue #1961: Implement workspace area add/remove with exact/under semantics and team auto-populate

**Goal**: Provide the `twig area` command group for managing area-path filters
in the workspace configuration, plus team auto-sync from ADO.

**Prerequisites**: None (foundational)

#### Tasks

| Task | Description | Files | Effort |
|---|---|---|---|
| **T-1961.1** | Create `AreaPathFilter` value object with `Matches()` logic for exact and under semantics | `src/Twig.Domain/ValueObjects/AreaPathFilter.cs`, `tests/Twig.Domain.Tests/ValueObjects/AreaPathFilterTests.cs` | Small |
| **T-1961.2** | Add `IsUnder()` helper to `AreaPath` value object | `src/Twig.Domain/ValueObjects/AreaPath.cs`, `tests/Twig.Domain.Tests/ValueObjects/AreaPathTests.cs` | Small |
| **T-1961.3** | Create `AreaFilterService` static domain service with `FilterByArea()` and `IsInArea()` | `src/Twig.Domain/Services/AreaFilterService.cs`, `tests/Twig.Domain.Tests/Services/AreaFilterServiceTests.cs` | Small |
| **T-1961.4** | Implement `AreaCommand` with `add`, `remove`, `list`, `sync` subcommands | `src/Twig/Commands/AreaCommand.cs`, `src/Twig/Program.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Medium |
| **T-1961.5** | Wire area config paths in `TwigConfiguration.SetValue()`/`GetValue()` and extend `ConfigCommand` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs`, `src/Twig/Commands/ConfigCommand.cs` | Small |
| **T-1961.6** | Add unit and integration tests for area commands | `tests/Twig.Cli.Tests/Commands/AreaCommandTests.cs` | Medium |

**Acceptance Criteria**:
- [ ] `twig area add "Project\Team A"` adds entry with `IncludeChildren=true`
- [ ] `twig area add "Project\Team A" --exact` adds entry with `IncludeChildren=false`
- [ ] `twig area remove "Project\Team A"` removes the entry
- [ ] `twig area list` displays all configured entries with semantics indicator
- [ ] `twig area sync` fetches team areas from ADO and replaces config
- [ ] Duplicate paths are rejected on add
- [ ] `AreaPathFilter.Matches()` correctly handles exact and under semantics
- [ ] All tests pass with `TreatWarningsAsErrors=true`

---

### Issue #1962: Implement twig area read-only view with dimmed out-of-area parents

**Goal**: Render an area-filtered workspace view that shows work items matching
configured area paths, with out-of-area parent items dimmed for context.

**Prerequisites**: Issue #1961 (depends on `AreaPathFilter` and `AreaFilterService`)

#### Tasks

| Task | Description | Files | Effort |
|---|---|---|---|
| **T-1962.1** | Add `GetByAreaPathsAsync()` to `IWorkItemRepository` and implement in SQLite repository | `src/Twig.Domain/Interfaces/IWorkItemRepository.cs`, `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Medium |
| **T-1962.2** | Add area_path index to SQLite DDL and bump schema version 9→10 | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Small |
| **T-1962.3** | Implement area-filtered view in `AreaCommand` (default action) with parent hydration and hierarchy building | `src/Twig/Commands/AreaCommand.cs`, `src/Twig/Commands/WorkspaceCommand.cs` | Medium |
| **T-1962.4** | Add area-view rendering to `SpectreRenderer` with `[dim]` styling for out-of-area parents | `src/Twig/Rendering/SpectreRenderer.cs`, `src/Twig/Rendering/IAsyncRenderer.cs` | Medium |
| **T-1962.5** | Add area-view formatting to `IOutputFormatter`, `HumanOutputFormatter`, and `JsonOutputFormatter` | `src/Twig/Formatters/IOutputFormatter.cs`, `src/Twig/Formatters/HumanOutputFormatter.cs`, `src/Twig/Formatters/JsonOutputFormatter.cs` | Small |
| **T-1962.6** | Add tests for area-view rendering and repository queries | `tests/Twig.Cli.Tests/Commands/AreaCommandTests.cs`, `tests/Twig.Infrastructure.Tests/Persistence/AreaQueryTests.cs` | Medium |

**Acceptance Criteria**:
- [ ] `twig area` renders work items matching configured area paths
- [ ] Parent items outside configured areas appear with `[dim]` styling
- [ ] View works in human, JSON, and minimal output formats
- [ ] `GetByAreaPathsAsync()` correctly handles both exact and under semantics
- [ ] SQLite area_path index exists after schema rebuild
- [ ] Schema version is bumped from 9 to 10
- [ ] Area view shows no items when no area paths are configured (with helpful message)
- [ ] All tests pass

---

## PR Groups

### PG-1: Area Configuration Foundation

**Type**: Deep  
**Scope**: Domain value objects, domain service, CLI commands for add/remove/list/sync  
**Issues**: #1961 (all tasks)  
**Tasks**: T-1961.1, T-1961.2, T-1961.3, T-1961.4, T-1961.5, T-1961.6  
**Estimated LoC**: ~800  
**Estimated Files**: ~12

**Description**: Introduces the `AreaPathFilter` value object, `AreaFilterService`,
and the full `twig area add/remove/list/sync` command surface. This PR establishes
the configuration foundation that the area view depends on. Includes domain
tests, CLI integration tests, and config wiring.

**Successor**: PG-2

---

### PG-2: Area-Filtered View

**Type**: Deep  
**Scope**: Repository queries, SQLite schema, rendering pipeline, output formatting  
**Issues**: #1962 (all tasks)  
**Tasks**: T-1962.1, T-1962.2, T-1962.3, T-1962.4, T-1962.5, T-1962.6  
**Estimated LoC**: ~900  
**Estimated Files**: ~14

**Description**: Implements the area-filtered workspace view with parent dimming.
Adds `GetByAreaPathsAsync()` to the repository layer, bumps the SQLite schema
for the area_path index, and extends the rendering pipeline to support
area-aware dimming. Includes repository tests, rendering tests, and formatter
tests.

**Predecessor**: PG-1

---

## References

- ADO Team Field Values API: `GET /{project}/{team}/_apis/work/teamsettings/teamfieldvalues`
- Existing area-path infrastructure: `AreaPath.cs`, `AreaPathEntry`, `DefaultsConfig.ResolveAreaPaths()`
- Sprint hierarchy dimming pattern: `SprintHierarchyNode.IsSprintItem`
- Epic #1945: Workspace Modes & Tracking (parent epic)


