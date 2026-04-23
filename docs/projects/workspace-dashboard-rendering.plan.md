# Workspace Dashboard Rendering

> **Work Item:** #1951 — Workspace Dashboard Rendering
> **Parent Epic:** #1945 — Workspace Modes & Tracking
> **Status:** Draft
> **Revision:** 0
> **Revision Notes:** Initial draft.

---

## Executive Summary

This design transforms `twig workspace` from a flat-list sprint display into a **tree-based dashboard** that groups items by workspace mode sections (Sprint → Area → Recent → Manual), deduplicates items that appear in multiple modes, adds seed/stale indicators, renders an exclusion footer, and defaults to a hierarchical tree rendering with working-level focus and configurable depth (upward, downward, sideways). The implementation extends the existing `WorkspaceCommand`, `SpectreRenderer`, and `HumanOutputFormatter` without breaking existing behavior — the current flat rendering becomes the fallback when no hierarchy data is available, and JSON/minimal formatters continue to emit their current schemas.

## Background

### Current Architecture

The workspace command (`twig workspace` / `twig ws`) currently operates in two rendering paths:

1. **Async Spectre path** (TTY, human format, `!--no-live`): Streams `WorkspaceDataChunk` variants through `SpectreRenderer.RenderWorkspaceAsync()`, which builds a Spectre `Table` with columns for ID, Type, Title, and State. Items are grouped by `StateCategory` (Proposed → InProgress → Completed) with separator rows between categories. Seeds are appended after a visual separator.

2. **Sync path** (JSON, minimal, `--no-live`, piped output): Calls `IOutputFormatter.FormatWorkspace()` or `FormatSprintView()` on the `Workspace` read model. `HumanOutputFormatter.FormatWorkspace()` renders items grouped by state category with optional hierarchical rendering via `SprintHierarchy`.

The `Workspace` read model (in `Twig.Domain.ReadModels`) composes three data sources:
- **ContextItem**: The active work item (nullable)
- **SprintItems**: Items in the current iteration, optionally filtered by assignee
- **Seeds**: Local seed work items

The `SprintHierarchy` read model organises sprint items into per-assignee hierarchical trees using process configuration to determine parent-child type relationships and ceiling types. It supports virtual group nodes for unparented items.

### Relevant Components

| Component | Location | Role |
|-----------|----------|------|
| `WorkspaceCommand` | `src/Twig/Commands/WorkspaceCommand.cs` | Orchestrates data loading, delegates to renderer or formatter |
| `TreeCommand` | `src/Twig/Commands/TreeCommand.cs` | Renders focused work-item tree with parent chain + children |
| `SpectreRenderer` | `src/Twig/Rendering/SpectreRenderer.cs` | Spectre.Console async rendering (workspace table, tree, status) |
| `IAsyncRenderer` | `src/Twig/Rendering/IAsyncRenderer.cs` | Async renderer interface with `RenderWorkspaceAsync` |
| `HumanOutputFormatter` | `src/Twig/Formatters/HumanOutputFormatter.cs` | ANSI text formatting for sync path |
| `IOutputFormatter` | `src/Twig/Formatters/IOutputFormatter.cs` | Formatter interface: `FormatWorkspace`, `FormatSprintView` |
| `Workspace` | `src/Twig.Domain/ReadModels/Workspace.cs` | Composite read model (context + sprint + seeds) |
| `SprintHierarchy` | `src/Twig.Domain/ReadModels/SprintHierarchy.cs` | Hierarchical sprint item organiser |
| `WorkTree` | `src/Twig.Domain/ReadModels/WorkTree.cs` | Tree read model for focused item + parent chain + children |
| `BacklogHierarchyService` | `src/Twig.Domain/Services/BacklogHierarchyService.cs` | Infers parent→child type maps from process config |
| `CeilingComputer` | `src/Twig.Domain/Services/CeilingComputer.cs` | Determines ceiling type for parent chain trimming |
| `StateCategoryResolver` | `src/Twig.Domain/Services/StateCategoryResolver.cs` | Maps state names to `StateCategory` enum |
| `SpectreTheme` | `src/Twig/Rendering/SpectreTheme.cs` | Type badges, state colors, icon mode support |
| `TwigConfiguration` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Configuration POCO with `Display`, `Seed` sections |
| `RenderingPipelineFactory` | `src/Twig/Rendering/RenderingPipelineFactory.cs` | Resolves sync vs async rendering path |
| `WorkingSetService` | `src/Twig.Domain/Services/WorkingSetService.cs` | Computes working set for background sync |

### Call-Site Audit

The following call sites consume workspace rendering and will be affected:

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `WorkspaceCommand.cs` | `ExecuteAsync` | Dispatches to Spectre or sync path | Needs tree-based rendering integration |
| `WorkspaceCommand.cs` | `ExecuteSyncAsync` | Calls `FormatWorkspace` / `FormatSprintView` | Needs tree data sourcing |
| `SpectreRenderer.cs` | `RenderWorkspaceAsync` | Table-based progressive rendering | Needs tree rendering mode |
| `HumanOutputFormatter.cs` | `FormatWorkspace` | Flat or hierarchical text rendering | Needs mode-sectioned output |
| `HumanOutputFormatter.cs` | `FormatSprintView` | Team view text rendering | Minimal impact (team view) |
| `JsonOutputFormatter.cs` | `FormatWorkspace` | JSON schema output | Schema additions only |
| `MinimalOutputFormatter.cs` | `FormatWorkspace` | Pipe-friendly output | No change needed |
| `ReadTools.cs` (MCP) | `Workspace` | MCP tool for workspace data | No change (uses `Workspace.Build`) |

## Problem Statement

1. **Flat rendering hides hierarchy**: The workspace shows items as flat lists grouped by state category, forcing users to mentally reconstruct parent-child relationships.

2. **No mode-sectioned output**: When workspace modes (sprint, area, recent, manual) are introduced by sibling Issues, items from different modes will overlap. The dashboard needs deduplication and mode-section headers.

3. **Missing seed indicators**: Seeds lack distinctive visual markers beyond the "Seeds" separator label.

4. **No exclusion footer**: Items excluded by manual untracking have no visibility.

5. **Fixed tree depth**: Tree rendering uses a single `TreeDepth` config value (default 10) without distinguishing upward (parent chain), downward (children), and sideways (sibling) dimensions.

6. **Working-level focus missing**: All items render at equal prominence — parents should be dimmed relative to the user's working level.

## Goals and Non-Goals

### Goals

1. **G-1**: Default `twig workspace` to tree-based rendering where each sprint item is shown under its parent hierarchy, with the user's working level (requirement-level types like User Story, Issue, Bug) rendered prominently and items above dimmed.

2. **G-2**: Render workspace output in mode sections (Sprint → Area → Recent → Manual) with a mode header, deduplicate items across sections (first-mode-wins, manual always shown), and render an exclusion summary footer.

3. **G-3**: Add seed indicators using configurable icon mode (Unicode ● / Nerd Font  symbols) to distinguish seeds from published items.

4. **G-4**: Support three-dimensional tree depth configuration: `display.treeDepthUp` (default 2), `display.treeDepthDown` (default 10), `display.treeDepthSideways` (default 1).

5. **G-5**: Maintain backward compatibility — existing JSON, minimal, and `--no-live` output formats continue unchanged.

### Non-Goals

- **NG-1**: Implementing workspace modes themselves (sprint, area, recent) — that's covered by sibling Issues #1946–#1950.
- **NG-2**: Interactive workspace navigation (keyboard-driven traversal) — that's `twig nav`.
- **NG-3**: Git worktree support or multi-workspace merging.
- **NG-4**: Changing the `twig tree` command behavior — this Issue focuses on `twig workspace` rendering.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | Workspace renders items in a tree hierarchy by default (parents → children), with working-level items prominent and ancestors dimmed |
| FR-02 | Mode sections are rendered with headers: `Sprint`, `Area`, `Recent`, `Manual` — empty sections are omitted |
| FR-03 | Items appearing in multiple modes show in the first mode encountered; manual inclusions always show in the Manual section |
| FR-04 | Exclusion footer shows count and IDs of manually excluded items |
| FR-05 | Seeds display a distinctive icon indicator (● in unicode mode, configurable in nerd mode) |
| FR-06 | Tree depth is configurable per-dimension: `display.treeDepthUp` (parent chain), `display.treeDepthDown` (children), `display.treeDepthSideways` (sibling count indicator) |
| FR-07 | The `--flat` flag restores the legacy flat rendering for backward compatibility |
| FR-08 | JSON and minimal formatters include mode section metadata in output without breaking existing schema |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | No new reflection usage — all serializable types added to `TwigJsonContext` |
| NFR-02 | AOT-compatible: no dynamic loading, no `System.Reflection.Emit` |
| NFR-03 | Tests cover both Spectre and sync rendering paths |
| NFR-04 | Working-level detection uses `IProcessConfigurationProvider` — no hardcoded type names |

## Proposed Design

### Architecture Overview

The design introduces three new concepts into the rendering pipeline:

```
WorkspaceCommand
    │
    ├── Data Loading (unchanged)
    │     ├── ContextItem → ActiveItemResolver
    │     ├── SprintItems → IWorkItemRepository
    │     └── Seeds → IWorkItemRepository
    │
    ├── NEW: Mode Section Builder
    │     ├── Receives workspace data + mode metadata
    │     ├── Groups items by mode (Sprint/Area/Recent/Manual)
    │     ├── Deduplicates: first-mode-wins, manual always shown
    │     └── Produces WorkspaceSections read model
    │
    ├── NEW: Workspace Tree Builder
    │     ├── Converts flat items into hierarchical trees per section
    │     ├── Uses process config for type hierarchy inference
    │     ├── Applies working-level focus (dimming above working level)
    │     └── Respects depth config (up/down/sideways)
    │
    └── Rendering
          ├── SpectreRenderer.RenderWorkspaceAsync (Spectre path)
          │     └── NOW: Renders Spectre Tree per section
          ├── HumanOutputFormatter.FormatWorkspace (Sync path)
          │     └── NOW: Renders ANSI tree per section
          └── JSON/Minimal (unchanged schema + mode metadata)
```

### Key Components

#### 1. `WorkspaceSections` Read Model (NEW)

**Location:** `src/Twig.Domain/ReadModels/WorkspaceSections.cs`

A read model that partitions workspace items into mode-labelled sections with deduplication:

```csharp
public sealed class WorkspaceSections
{
    public IReadOnlyList<WorkspaceSection> Sections { get; }
    public IReadOnlyList<int> ExcludedItemIds { get; }  // manually excluded
    
    public static WorkspaceSections Build(
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyList<WorkItem> areaItems,     // future: from area mode
        IReadOnlyList<WorkItem> recentItems,   // future: from recent mode
        IReadOnlyList<WorkItem> manualItems,   // future: from manual overlay
        IReadOnlyList<int> excludedIds);
}

public sealed record WorkspaceSection(
    string ModeName,           // "Sprint", "Area", "Recent", "Manual"
    IReadOnlyList<WorkItem> Items,
    bool IsTreeRendered);      // false for Manual mode?
```

For the initial implementation (since sibling Issues #1946–#1950 haven't landed), this will have a single Sprint section with all items. The architecture is designed to accept additional mode inputs as they become available.

#### 2. Working-Level Focus Service (NEW)

**Location:** `src/Twig.Domain/Services/WorkingLevelResolver.cs`

Determines the "working level" from process configuration — this is the requirement-level backlog type(s) that the user primarily works with:

```csharp
public static class WorkingLevelResolver
{
    /// <summary>
    /// Returns the backlog level index for the "working level" —
    /// the requirement backlog level. Items at this level render prominently;
    /// items above render dimmed (context ancestors).
    /// Returns null when process config is unavailable.
    /// </summary>
    public static int? Resolve(ProcessConfigurationData? config);
}
```

The working level is the **requirement backlog** level (index of `config.RequirementBacklog` in the ordered levels list). Portfolio backlog items render dimmed, requirement-level items render prominently, task-level items render normally.

#### 3. Tree Depth Configuration (EXTENDED)

**Location:** `src/Twig.Infrastructure/Config/TwigConfiguration.cs` — extend `DisplayConfig`

```csharp
public sealed class DisplayConfig
{
    // Existing
    public int TreeDepth { get; set; } = 10;
    
    // New three-dimensional depth config
    public int TreeDepthUp { get; set; } = 2;       // parent chain limit
    public int TreeDepthDown { get; set; } = 10;     // children depth limit
    public int TreeDepthSideways { get; set; } = 1;  // sibling indicators
}
```

`TreeDepth` remains as the default for `twig tree` command; the new properties are for workspace tree rendering specifically. The `twig config` command needs new set-value cases.

#### 4. Workspace Tree Rendering in SpectreRenderer (MODIFIED)

The existing `RenderWorkspaceAsync` transitions from a table-based layout to a tree-based layout:

- Each mode section renders as a collapsible tree header
- Items within each section render as a Spectre `Tree` using the same `BuildSpectreTreeAsync` pattern as `TreeCommand`
- Working-level items render with `[bold]` markup; ancestors render `[dim]`
- Seeds render with a distinctive icon prefix (● or  depending on `display.icons` config)

The table-based rendering is preserved behind a `--flat` flag for backward compatibility.

#### 5. Seed Indicator Enhancement (MODIFIED)

**Location:** `src/Twig/Rendering/SpectreTheme.cs` + `src/Twig/Formatters/HumanOutputFormatter.cs`

Add seed-specific formatting:

```csharp
// SpectreTheme
internal string FormatSeedIndicator(string iconMode)
    => iconMode == "nerd" ? "[green] [/]" : "[green]●[/]";
```

### Data Flow

1. `WorkspaceCommand.ExecuteAsync()` loads context, sprint items, and seeds (unchanged)
2. **NEW**: Build `WorkspaceSections` from loaded data (initially single Sprint section)
3. **NEW**: For each section, build tree hierarchy using `SprintHierarchy.Build()` with process config
4. **NEW**: Resolve working level via `WorkingLevelResolver`
5. Pass sections + working level + depth config to renderer
6. Renderer produces tree-based output (Spectre path) or ANSI text (sync path)

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Tree rendering as default, `--flat` for legacy | Trees convey hierarchy naturally; users accustomed to flat can opt back in |
| Dedup: first-mode-wins, manual always shown | Prevents item duplication across mode sections while ensuring manual inclusions are visible |
| Working level = requirement backlog | Most users work at the User Story / Issue level; this is configurable per process template |
| Three-dimensional depth uses separate config keys | Avoids overloading `treeDepth` which is used by `twig tree` command |
| Seed indicator via SpectreTheme | Consistent with existing badge/icon pattern; icon mode already supported |
| `WorkspaceSections` as forward-compatible read model | Sibling Issues will feed data into the same structure without refactoring |

## Dependencies

### Internal Dependencies

- **#1946 Workspace Mode Infrastructure**: The mode sectioning will show a single Sprint section until mode infrastructure lands. The `WorkspaceSections` read model is designed to accept additional mode inputs.
- **Process Configuration Provider**: Required for working-level resolution and hierarchy building. Already available via `IProcessTypeStore.GetProcessConfigurationDataAsync()`.

### External Dependencies

- **Spectre.Console**: Already used — `Tree`, `Live`, `Markup`, `Rows` — no new packages needed.

### Sequencing Constraints

- This Issue can proceed independently of #1946–#1950 (mode infrastructure) — the workspace will render a single Sprint section initially.
- Tasks within this Issue should be implemented in order: configuration first, then domain models, then rendering.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Tree rendering slower than flat table for large sprints (100+ items) | Low | Medium | Tree depth limits cap rendered nodes; `--flat` provides fallback |
| Mode section headers add noise when only Sprint mode exists | Medium | Low | Omit section header when only one section is non-empty |
| Working-level detection fails on unknown process templates | Low | Medium | Fallback: treat all items as working level (no dimming) |

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| Q-1 | Should the `--flat` flag be persisted in config or remain CLI-only? | Low | Open |
| Q-2 | When mode infrastructure (#1946) lands, should mode headers always show or only when >1 mode is active? | Low | Open — design assumes omit-when-single for now |
| Q-3 | Should workspace tree rendering share the `twig tree`'s sync-then-revise pattern, or is single-pass sufficient? | Low | Open — starting with single-pass; sync integration is additive |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ReadModels/WorkspaceSections.cs` | Mode-sectioned workspace read model with dedup logic |
| `src/Twig.Domain/Services/WorkingLevelResolver.cs` | Resolves working level from process configuration |
| `tests/Twig.Domain.Tests/ReadModels/WorkspaceSectionsTests.cs` | Unit tests for section building and dedup |
| `tests/Twig.Domain.Tests/Services/WorkingLevelResolverTests.cs` | Unit tests for working-level resolution |
| `tests/Twig.Cli.Tests/Rendering/WorkspaceTreeRenderTests.cs` | Tests for tree-based workspace rendering |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `TreeDepthUp`, `TreeDepthDown`, `TreeDepthSideways` to `DisplayConfig`; add `SetValue` cases |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for any new types if needed |
| `src/Twig/Commands/WorkspaceCommand.cs` | Integrate tree-based rendering, pass depth config, add `--flat` flag |
| `src/Twig/Rendering/SpectreRenderer.cs` | Add tree-based workspace rendering mode to `RenderWorkspaceAsync` |
| `src/Twig/Rendering/SpectreTheme.cs` | Add `FormatSeedIndicator` method |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Extend `RenderWorkspaceAsync` signature if needed for tree data |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Update `FormatWorkspace` for mode-sectioned tree output |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add overload or extend `FormatWorkspace` signature if needed |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Add mode section metadata to workspace JSON |
| `src/Twig/Program.cs` | Add `--flat` flag to workspace/ws commands |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | Add tests for tree rendering and flat fallback |
| `tests/Twig.Cli.Tests/Rendering/WorkspaceCacheAgeTests.cs` | Verify cache age still works in tree mode |

## ADO Work Item Structure

### Issue #1964: Implement mode-sectioned workspace output with dedup, seed indicators, and exclusion footer

**Goal:** Render workspace output in mode sections, deduplicate items across sections, add seed visual indicators, and render an exclusion footer for manually excluded items.

**Prerequisites:** None (can start immediately)

#### Tasks

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1964-A | Create `WorkspaceSections` read model with mode-section builder and dedup logic | `src/Twig.Domain/ReadModels/WorkspaceSections.cs`, `tests/Twig.Domain.Tests/ReadModels/WorkspaceSectionsTests.cs` | ~150 LoC |
| T-1964-B | Add seed indicator formatting to `SpectreTheme` and `HumanOutputFormatter` (Unicode + Nerd Font) | `src/Twig/Rendering/SpectreTheme.cs`, `src/Twig/Formatters/HumanOutputFormatter.cs`, tests | ~80 LoC |
| T-1964-C | Update `HumanOutputFormatter.FormatWorkspace` to render mode-sectioned output with section headers, dedup, and exclusion footer | `src/Twig/Formatters/HumanOutputFormatter.cs`, `tests/Twig.Cli.Tests/Formatters/` | ~200 LoC |
| T-1964-D | Update `SpectreRenderer.RenderWorkspaceAsync` to render mode-sectioned output with section headers, dedup, seed indicators | `src/Twig/Rendering/SpectreRenderer.cs`, `src/Twig/Rendering/IAsyncRenderer.cs`, tests | ~250 LoC |
| T-1964-E | Wire `WorkspaceCommand` to build `WorkspaceSections` and pass to renderers; update `WorkspaceDataChunk` if needed | `src/Twig/Commands/WorkspaceCommand.cs`, tests | ~120 LoC |

**Acceptance Criteria:**
- [ ] Workspace output shows mode section headers when data is available
- [ ] Items appearing in multiple modes show only in the first mode (dedup)
- [ ] Manual inclusions always appear in the Manual section
- [ ] Seeds display distinctive icon indicators (● unicode / configurable nerd)
- [ ] Exclusion footer shows count of manually excluded items
- [ ] JSON formatter includes mode section metadata
- [ ] All existing workspace tests pass unchanged

### Issue #1965: Default to tree-based rendering with working-level focus and depth config

**Goal:** Make tree-based hierarchy the default workspace rendering, with working-level prominence, dimmed ancestors, and three-dimensional depth configuration.

**Prerequisites:** T-1964-A (WorkspaceSections model) should be ready, but not strictly blocking — can develop in parallel against `SprintHierarchy` directly.

#### Tasks

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1965-A | Create `WorkingLevelResolver` service and unit tests | `src/Twig.Domain/Services/WorkingLevelResolver.cs`, `tests/Twig.Domain.Tests/Services/WorkingLevelResolverTests.cs` | ~100 LoC |
| T-1965-B | Add three-dimensional depth config (`TreeDepthUp`, `TreeDepthDown`, `TreeDepthSideways`) to `DisplayConfig` and wire `SetValue` + serialization | `src/Twig.Infrastructure/Config/TwigConfiguration.cs`, config tests | ~80 LoC |
| T-1965-C | Implement tree-based workspace rendering in `SpectreRenderer` with working-level focus (dim ancestors, bold working level) | `src/Twig/Rendering/SpectreRenderer.cs`, `tests/Twig.Cli.Tests/Rendering/WorkspaceTreeRenderTests.cs` | ~300 LoC |
| T-1965-D | Implement tree-based workspace rendering in `HumanOutputFormatter` for sync path with working-level focus | `src/Twig/Formatters/HumanOutputFormatter.cs`, tests | ~200 LoC |
| T-1965-E | Add `--flat` flag to `twig workspace`/`ws`/`sprint` commands; wire depth config into workspace command | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Program.cs`, tests | ~120 LoC |

**Acceptance Criteria:**
- [ ] `twig workspace` defaults to tree-based rendering
- [ ] Working-level items (requirement backlog) render prominently; ancestors render dimmed
- [ ] Tree depth respects `display.treeDepthUp`, `display.treeDepthDown`, `display.treeDepthSideways` config
- [ ] `--flat` flag restores legacy flat rendering
- [ ] `twig config display.treeDepthUp <n>` sets depth correctly
- [ ] Process config unavailable: fallback to flat rendering (no crash)
- [ ] All existing workspace and tree tests pass

## PR Groups

### PG-1: Mode-Sectioned Output & Seed Indicators (Deep)

**Tasks:** T-1964-A, T-1964-B, T-1964-C, T-1964-D, T-1964-E
**Issue:** #1964
**Estimated LoC:** ~800
**Files:** ~12
**Type:** Deep — structural changes to rendering pipeline
**Predecessor:** None

**Description:** Introduces `WorkspaceSections` read model, seed indicators, mode-section headers, dedup logic, and exclusion footer across both rendering paths. This PR establishes the sectioned output architecture that PG-2 builds on.

### PG-2: Tree-Based Rendering with Focus & Depth (Deep)

**Tasks:** T-1965-A, T-1965-B, T-1965-C, T-1965-D, T-1965-E
**Issue:** #1965
**Estimated LoC:** ~800
**Files:** ~10
**Type:** Deep — tree rendering algorithm, working-level detection, depth configuration
**Predecessor:** PG-1 (mode sections provide the structure that tree rendering fills)

**Description:** Switches workspace default to tree-based rendering, adds working-level focus with dimmed ancestors, introduces three-dimensional depth configuration, and provides `--flat` fallback. Builds on the sectioned structure from PG-1.

## References

- Parent Epic #1945: Workspace Modes & Tracking — defines the overall mode architecture
- Sibling Issue #1946: Workspace Mode Infrastructure — mode enum, persistence, config
- Sibling Issue #1947: Manual Track/Untrack Overlay — manual inclusions/exclusions
- `SprintHierarchy.Build()` — existing hierarchical sprint item organiser
- `BacklogHierarchyService` — existing parent-child type inference from process config
- `CeilingComputer` — existing ceiling-type computation for parent chain trimming
