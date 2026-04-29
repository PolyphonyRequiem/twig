# Tree/Query Realignment — Consolidate Tree, Refresh, States; Move Area

> **Epic:** #2152 — Tree/Query Realignment  
> **Status**: 🔨 In Progress
> **Revision:** 1 (Reviewed — no blocking issues)  
> **Spec:** [docs/specs/tree-query-commands.spec.md](../specs/tree-query-commands.spec.md)

---

## Executive Summary

The tree, read-only, and process-discovery command surface has accumulated standalone commands that fragment related functionality across too many entry points. `tree` exists alongside `show` despite both operating on a single work item. `refresh` duplicates the pull phase of `sync`. `states` provides a narrow view of process configuration. `area` was already relocated under `workspace` but its standalone entry points remain visible. This plan consolidates the surface per the functional spec: **merge `tree` into `show --tree` and `workspace --tree`** (standalone command removed, hidden alias retained), **merge `refresh` into `sync --pull-only`** (standalone command removed, hidden alias retained), **rename `states` to `process`** and expand it to expose types, fields, states, and transitions, **add `ids` output format** to all list-like commands, **enforce sync-first behavior** for machine output formats, and **add a `twig_process` MCP tool** for process discovery. The `twig_query` MCP tool already exists in `NavigationTools.cs` and requires no changes.

---

## Background

### Current State

Seven commands share overlapping or fragmented responsibilities:

| Command | Purpose | Overlap/Problem |
|---------|---------|-----------------|
| `tree` | Hierarchy rendering (single item → parent chain + children) | Duplicates `show` with hierarchy view; `show` can't render tree |
| `show` | Single item detail card | No hierarchy/tree mode |
| `workspace` | Sprint backlog dashboard | No full-hierarchy tree mode |
| `refresh` | Pull from ADO cache | Duplicates `sync` pull phase exactly |
| `sync` | Push + pull (wraps `refresh` internally) | Already has the pull logic via RefreshCommand dependency |
| `states` | List workflow states for active item's type | Too narrow — agents and users need types, fields, transitions |
| `area` | Area path management | Already moved to `workspace area`; standalone aliases remain with deprecation hints |

### Architecture

All commands follow the same structural pattern:
- Constructor with primary DI injection via `CommandContext` + domain services
- `ExecuteAsync` → telemetry wrapper via `TelemetryHelper.TrackCommand` or inline Stopwatch
- Rendering via `ctx.Resolve(outputFormat)` → `(IOutputFormatter, IAsyncRenderer?)` tuple
- TTY path: `SpectreRenderer` with `RenderWithSyncAsync` (two-pass: cached → sync → revised)
- Non-TTY path: `IOutputFormatter.Format*()` methods → Console.WriteLine

Key services:
- `SyncCoordinatorFactory` — creates read-only or read-write sync coordinators
- `RefreshOrchestrator` — manages full refresh lifecycle (WIQL fetch, conflict resolution, ancestor hydration)
- `IProcessTypeStore` — reads/writes process type metadata from SQLite
- `IProcessConfigurationProvider` — builds `ProcessConfiguration` from dynamic type data
- `IFieldDefinitionStore` — cached field definitions from ADO
- `OutputFormatterFactory` — resolves formatters; currently supports `human`, `json`, `json-compact`, `minimal`

### Call-Site Audit

| Symbol | File | Current Usage | Impact |
|--------|------|---------------|--------|
| `TreeCommand` | `Commands/TreeCommand.cs` | 258 lines, standalone command with Spectre two-pass + sync path | **Extract logic → service, delete command** |
| `TreeCommand` (registration) | `Program.cs:401` | `Tree()` → resolves TreeCommand | **Replace with hidden alias → show --tree** |
| `TreeCommand` (help) | `Program.cs:1016-1017` | "tree" in Views section of GroupedHelp | **Remove from Views, note as hidden alias** |
| `ShowCommand` | `Commands/ShowCommand.cs` | 410 lines, single item + batch | **Add --tree flag, integrate tree rendering** |
| `ShowCommand` (registration) | `Program.cs:340` | `Show()` with `id`, `output`, `noRefresh` | **Add tree parameter** |
| `RefreshCommand` | `Commands/RefreshCommand.cs` | 276 lines, full refresh orchestration | **Keep as internal service, remove standalone registration** |
| `RefreshCommand` (registration) | `Program.cs:698` | Hidden alias with deprecation hint | **Change to route through sync --pull-only** |
| `SyncCommand` | `Commands/SyncCommand.cs` | 107 lines, wraps FlushAll + RefreshCommand | **Add --pull-only flag** |
| `SyncCommand` (registration) | `Program.cs:694` | `Sync()` with `output`, `force` | **Add pullOnly parameter** |
| `StatesCommand` | `Commands/StatesCommand.cs` | 86 lines, type-scoped state list | **Replace with ProcessCommand** |
| `StatesCommand` (registration) | `Program.cs:375` | `States()` → resolves StatesCommand | **Replace with Process(), hidden states alias** |
| `WorkspaceCommand` | `Commands/WorkspaceCommand.cs` | ~450 lines, sprint backlog dashboard | **Add --tree flag** |
| `WorkspaceCommand` (registration) | `Program.cs:711` | `Workspace()` | **Add tree parameter** |
| `OutputFormatterFactory` | `Formatters/OutputFormatterFactory.cs` | 25 lines, resolves by format name | **Add "ids" format** |
| `IOutputFormatter` | `Formatters/IOutputFormatter.cs` | Interface with format methods | **May need ids-specific methods** |
| `ReadTools.Tree` | `Mcp/Tools/ReadTools.cs:21` | `twig_tree` MCP tool | **Delegate to twig_show + tree=true** |
| `ReadTools.Workspace` | `Mcp/Tools/ReadTools.cs:74` | `twig_workspace` MCP tool | **Add tree parameter** |
| `NavigationTools.Show` | `Mcp/Tools/NavigationTools.cs:21` | `twig_show` MCP tool | **Add tree parameter** |
| `MutationTools.Sync` | `Mcp/Tools/MutationTools.cs:463` | `twig_sync` MCP tool | **Add pull_only parameter** |
| `GroupedHelp.KnownCommands` | `Program.cs:1009` | Set of all command names | **Add "process", keep "tree" + "states" as aliases** |

### Prior Art

The [Context Commands Realignment](context-commands-realignment.plan.md) (Epic #2149) established the pattern for command consolidation: `status` was deleted and absorbed into `show`, `set` was slimmed to context-switch-only. This plan follows the same pattern for tree/read commands.

---

## Problem Statement

1. **`tree` is a standalone command** — users must learn two separate commands (`show` and `tree`) for viewing a single work item. The hierarchy view should be a mode of `show`, not a separate command.
2. **`workspace` lacks a tree mode** — the full-backlog hierarchy (previously `twig tree --all`) has no home after tree consolidation without adding `--tree` to workspace.
3. **`refresh` duplicates `sync` pull phase** — `SyncCommand` already wraps `RefreshCommand` internally. Standalone `refresh` is redundant.
4. **`states` is too narrow** — agents and scripts need to discover work item types, field definitions, and state transitions, not just a list of states for one type.
5. **No `ids` output format** — shell-piping workflows require bare numeric IDs (one per line) from list-like commands. Only `query` supports this today via inline handling.
6. **Machine output doesn't sync first** — `workspace` in json/minimal format returns stale cache data. The spec mandates sync-first for all machine formats.
7. **No `twig_process` MCP tool** — agents cannot discover process configuration (types, states, fields) programmatically.

---

## Goals and Non-Goals

### Goals

- **G-1:** Merge `tree` into `show --tree` (single item) and `workspace --tree` (full backlog) with hidden backward-compat alias
- **G-2:** Merge `refresh` into `sync --pull-only` with hidden backward-compat alias
- **G-3:** Rename `states` to `process` and expand to cover types, fields, states, and transitions
- **G-4:** Add `ids` output format to `show` (batch/tree), `workspace`, `workspace area view`
- **G-5:** Enforce sync-first behavior for json/minimal/ids formats across all affected commands
- **G-6:** Add `twig_process` MCP tool for process discovery
- **G-7:** Enhance MCP tools: `twig_show` gains `tree` flag, `twig_workspace` gains `tree` flag, `twig_sync` gains `pull_only` flag

### Non-Goals

- **NG-1:** Changing `query` command behavior (already implements all required features including `ids` format and MCP tool)
- **NG-2:** Modifying `area` command subcommand behavior (already moved to `workspace area` with working hidden aliases)
- **NG-3:** Adding new interactive TUI modes
- **NG-4:** Changing the sync/refresh orchestration logic itself (only changing how it's exposed)
- **NG-5:** Modifying `twig_tree` MCP tool behavior beyond making it delegate to `twig_show` with tree=true

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `twig show --tree` renders the focused item's parent chain + children as hierarchy tree |
| FR-02 | `twig show <id> --tree` renders a specific item's hierarchy |
| FR-03 | `twig workspace --tree` renders full backlog as hierarchy tree (all roots) |
| FR-04 | `twig tree` (hidden alias) maps to `twig show --tree` |
| FR-05 | `twig sync --pull-only` performs pull-only refresh (skip flush phase) |
| FR-06 | `twig sync --pull-only --force` bypasses dirty guard |
| FR-07 | `twig refresh` (hidden alias) maps to `twig sync --pull-only` |
| FR-08 | `twig process` (no args) lists all work item types with state counts |
| FR-09 | `twig process <type>` shows states, fields, and transitions for that type |
| FR-10 | `ids` format outputs bare numeric IDs, one per line |
| FR-11 | `ids` format supported by: show (batch/tree), workspace, workspace area view |
| FR-12 | json/minimal/ids formats sync before emitting (sync-first behavior) |
| FR-13 | `twig_show` MCP tool accepts `tree` boolean parameter |
| FR-14 | `twig_workspace` MCP tool accepts `tree` boolean parameter |
| FR-15 | `twig_sync` MCP tool accepts `pull_only` boolean parameter |
| FR-16 | `twig_process` MCP tool returns JSON process configuration |
| FR-17 | Hidden aliases emit no deprecation hint (unlike area aliases which do) |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | AOT-compatible — no reflection, all JSON via TwigJsonContext |
| NFR-02 | TreatWarningsAsErrors must pass |
| NFR-03 | All new commands must have telemetry instrumentation |
| NFR-04 | All existing test suites must pass |
| NFR-05 | No increase in binary size beyond the new code additions |

---

## Proposed Design

### Architecture Overview

The consolidation follows three structural patterns:

1. **Command Absorption** — Tree rendering logic is extracted from `TreeCommand` into a shared `TreeRenderingService` that `ShowCommand` and `WorkspaceCommand` can both invoke when `--tree` is active. The standalone `TreeCommand.cs` is deleted; a hidden alias routes `twig tree` → `twig show --tree`.

2. **Flag Promotion** — `SyncCommand` gains `--pull-only` which skips the flush phase and routes directly to `RefreshCommand.ExecuteAsync`. The existing `SyncCommand → RefreshCommand` delegation pattern is preserved — `RefreshCommand` stays as the internal implementation but loses its standalone CLI registration. A hidden alias routes `twig refresh` → `twig sync --pull-only`.

3. **Command Replacement** — `StatesCommand` is replaced by `ProcessCommand` which uses the same `IProcessTypeStore` and `IFieldDefinitionStore` but exposes a broader surface (all types, fields, transitions). The hidden alias `twig states` still works but routes to `process` scoped to the active item's type.

### Key Components

#### TreeRenderingService

New shared service that encapsulates the tree rendering logic currently in `TreeCommand`:
- Accepts a work item ID, output format, depth, and rendering options
- Builds `WorkTree` from parent chain + children + descendants
- Handles Spectre two-pass rendering (cached → sync → revised) for TTY
- Handles sync-first for machine formats (json/minimal/ids)
- Used by both `ShowCommand` (when `--tree`) and `WorkspaceCommand` (when `--tree`)

```
TreeCommand.cs (258 LoC) → TreeRenderingService.cs (~200 LoC, reusable)
                          → ShowCommand.cs (+~30 LoC integration)
                          → WorkspaceCommand.cs (+~20 LoC integration)
```

#### ProcessCommand

Replaces `StatesCommand` with expanded functionality:
- No-args mode: queries `IProcessTypeStore.GetAllAsync()` and formats as type list with state counts
- With-type mode: queries `IProcessTypeStore.GetByNameAsync()` + `IFieldDefinitionStore.GetAllAsync()` and formats states (with categories and colors), fields (with reference names and data types), and transitions (from `ProcessConfiguration.TypeConfigs`)
- All output formats supported: human, json, minimal

#### IdsOutputFormatter

New formatter implementing `IOutputFormatter` for the `ids` format:
- `FormatTree()` → extracts all IDs in tree order, one per line
- `FormatWorkspace()` → extracts all sprint item IDs, one per line
- `FormatQueryResults()` → already handled inline in QueryCommand (no change)
- `FormatWorkItem()` → single ID
- All other format methods → empty string (not applicable)

#### Sync-First Machine Output

Pattern for machine formats across commands:
```
if (isMachineFormat && !noRefresh) {
    await syncCoordinator.SyncWorkingSetAsync(workingSet);
    // Reload from cache
}
// Emit output
```
Already implemented in `ShowCommand` (lines 139-165). Needs to be added to `WorkspaceCommand` sync path and the new tree rendering path.

### Data Flow

#### Show --tree (TTY path)
```
ShowCommand.ExecuteAsync(tree=true)
  → TreeRenderingService.RenderAsync(id, renderer, fmt)
    → activeItemResolver.ResolveByIdAsync(id)
    → workItemRepo.GetParentChainAsync()
    → workItemRepo.GetChildrenAsync()
    → WorkTreeFetcher.FetchDescendantsAsync()  [descendants to maxDepth]
    → WorkTree.Build()
    → SpectreRenderer.RenderWithSyncAsync()
       → BuildTreeViewAsync() [cached]
       → SyncCoordinator.SyncWorkingSetAsync() [background]
       → BuildTreeViewAsync() [fresh]
```

#### Show --tree (machine path)
```
ShowCommand.ExecuteAsync(tree=true, output="json")
  → TreeRenderingService.RenderAsync(id, fmt=json)
    → SyncCoordinator.SyncItemSetAsync([id]) [sync-first]
    → workItemRepo.GetParentChainAsync()
    → workItemRepo.GetChildrenAsync()
    → WorkTree.Build()
    → fmt.FormatTree(tree)
    → Console.WriteLine()
```

#### Sync --pull-only
```
SyncCommand.ExecuteAsync(pullOnly=true)
  → [skip Phase 1: FlushAll]
  → RefreshCommand.ExecuteAsync(force)  [Phase 2 only]
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Extract `TreeRenderingService` rather than inline tree logic in ShowCommand | ShowCommand is already 410 lines. Tree rendering is ~200 LoC of complex Spectre wiring. A service keeps both commands readable and testable. |
| Keep `RefreshCommand` as internal service | RefreshCommand has 276 lines of well-structured orchestration logic. Moving it all into SyncCommand would create a 380+ line monolith. Instead, SyncCommand conditionally calls it. |
| `ids` format as a dedicated formatter class | Follows existing formatter pattern (Human, Json, JsonCompact, Minimal). Consistent with `OutputFormatterFactory` design. |
| Process command uses no-args / with-type pattern | Matches the spec exactly. No-args lists types (overview), with-type shows details (drill-down). |
| Hidden aliases emit no deprecation hint | Unlike `area` aliases (which hint at `workspace area`), `tree` and `refresh` are being removed conceptually, not just moved. The hidden alias is permanent backward compat, not a migration path. |

---

## Dependencies

### External Dependencies
- No new library dependencies required
- All changes use existing Spectre.Console, ConsoleAppFramework, and SQLite dependencies

### Internal Dependencies
- `IProcessTypeStore` — existing, used by ProcessCommand
- `IFieldDefinitionStore` — existing, used by ProcessCommand
- `RefreshOrchestrator` — existing, used by SyncCommand via RefreshCommand
- `WorkTree` / `WorkTreeFetcher` — existing, used by TreeRenderingService
- `SyncCoordinatorFactory` — existing, used for sync-first behavior

### Sequencing Constraints
- PG-1 (Tree) and PG-2 (Sync/Process/IDs) are independent and can be developed in parallel
- Within PG-1: TreeRenderingService must be extracted before show --tree can be wired
- Within PG-2: SyncCommand --pull-only and ProcessCommand are independent; IdsOutputFormatter is needed before ids format can be wired into commands

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Tree rendering performance regresses when called through ShowCommand | Low | Medium | TreeRenderingService preserves identical code paths; no additional abstraction overhead |
| Hidden aliases break in ConsoleAppFramework | Low | High | Pattern already established (save, refresh, area have hidden aliases); test with existing alias tests |
| IdsOutputFormatter missing methods cause compile errors | Low | Low | Implement all IOutputFormatter methods (return empty string for N/A methods) |
| Sync-first in workspace delays output | Medium | Low | `--no-refresh` is the escape hatch; only applies to machine formats where consumers expect fresh data |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should `twig tree --all` map to `twig workspace --tree` or `twig show --tree --all`? | Low | Spec says workspace --tree. The hidden alias `twig tree` maps to `show --tree`, but `twig tree --all` needs special routing. Recommend: `twig tree --all` maps to `workspace --tree` via Program.cs routing. |
| OQ-2 | Should ProcessCommand's `states` alias show all types or just the active item's type? | Low | Recommend: preserve current StatesCommand behavior (active item's type only) for the `states` alias, while `process` without args shows all types. |
| OQ-3 | Does `twig_tree` MCP tool need to remain as a separately registered tool or can it be removed? | Low | Spec says "retained for backward compatibility." Keep as hidden alias that delegates to `twig_show` with tree=true. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/TreeRenderingService.cs` | Extracted tree rendering logic shared by ShowCommand and WorkspaceCommand |
| `src/Twig/Commands/ProcessCommand.cs` | Replaces StatesCommand with expanded process discovery |
| `src/Twig/Formatters/IdsOutputFormatter.cs` | New `ids` output format — bare numeric IDs, one per line |
| `src/Twig.Mcp/Tools/ProcessTools.cs` | `twig_process` MCP tool for process discovery |
| `tests/Twig.Cli.Tests/Commands/ProcessCommandTests.cs` | Tests for ProcessCommand |
| `tests/Twig.Cli.Tests/Commands/ShowCommand_TreeTests.cs` | Tests for show --tree integration |
| `tests/Twig.Cli.Tests/Commands/WorkspaceCommand_TreeTests.cs` | Tests for workspace --tree |
| `tests/Twig.Cli.Tests/Commands/SyncCommand_PullOnlyTests.cs` | Tests for sync --pull-only |
| `tests/Twig.Cli.Tests/Formatters/IdsOutputFormatterTests.cs` | Tests for ids output format |
| `tests/Twig.Mcp.Tests/Tools/ProcessToolsTests.cs` | Tests for twig_process MCP tool |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/ShowCommand.cs` | Add `--tree` flag; delegate to TreeRenderingService when tree=true |
| `src/Twig/Commands/WorkspaceCommand.cs` | Add `--tree` flag; delegate to TreeRenderingService or inline full-backlog tree rendering |
| `src/Twig/Commands/SyncCommand.cs` | Add `--pull-only` flag; skip flush phase when set |
| `src/Twig/Program.cs` | Wire new commands, hidden aliases (tree→show --tree, refresh→sync --pull-only, states→process), add process registration, update GroupedHelp |
| `src/Twig/Formatters/OutputFormatterFactory.cs` | Add "ids" format mapping to IdsOutputFormatter |
| `src/Twig/Formatters/IOutputFormatter.cs` | No changes expected (ids formatter implements existing interface) |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | May need adjustments for tree output when called from ShowCommand |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | May need adjustments for tree output when called from ShowCommand |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | May need adjustments for tree output when called from ShowCommand |
| `src/Twig/DependencyInjection/` | Register TreeRenderingService, ProcessCommand in DI container |
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Add `tree` parameter to `twig_show` |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Make `twig_tree` delegate to NavigationTools.Show with tree=true; add `tree` parameter to `twig_workspace` |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Add `pull_only` parameter to `twig_sync` |
| `src/Twig.Mcp/Program.cs` | Register ProcessTools |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add process result formatting method |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | May need ProcessTypeStore/FieldDefinitionStore exposure |
| `src/Twig.Mcp/Services/Batch/ToolDispatcher.cs` | Update twig_show dispatch to pass tree parameter |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Update/migrate tests to ShowCommand_TreeTests |
| `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | Migrate async tree tests |
| `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | Migrate link tree tests |
| `tests/Twig.Cli.Tests/Commands/StatesCommandTests.cs` | Migrate to ProcessCommandTests |
| `tests/Twig.Cli.Tests/Commands/SyncCommandTests.cs` | Add pull-only tests |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandDeprecationTests.cs` | Update to verify routing through sync --pull-only |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs` | Update for twig_tree → twig_show delegation |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsWorkspaceTests.cs` | Add tree parameter tests |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsSyncTests.cs` | Add pull_only parameter tests |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` | Add tree parameter tests |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig/Commands/TreeCommand.cs` | Logic extracted to TreeRenderingService; standalone command removed |
| `src/Twig/Commands/StatesCommand.cs` | Replaced by ProcessCommand |

---

## ADO Work Item Structure

All work is under **Epic #2152 — Tree/Query Realignment**.

### Issue 1: Tree → Show --tree consolidation

**Goal:** Extract tree rendering from standalone `TreeCommand` into a shared `TreeRenderingService`, add `--tree` flag to `ShowCommand`, register hidden alias, and update MCP tools.

**Prerequisites:** None (first issue)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2152.1.1 | Extract TreeRenderingService from TreeCommand | `src/Twig/Commands/TreeRenderingService.cs` (new), `src/Twig/Commands/TreeCommand.cs` (source) | M |
| T-2152.1.2 | Add `--tree` flag to ShowCommand, integrate TreeRenderingService | `src/Twig/Commands/ShowCommand.cs` | M |
| T-2152.1.3 | Register hidden `tree` alias in Program.cs, route `tree --all` to workspace --tree | `src/Twig/Program.cs` | S |
| T-2152.1.4 | Delete TreeCommand.cs, update DI registrations | `src/Twig/Commands/TreeCommand.cs` (delete), `src/Twig/Program.cs`, DI config | S |
| T-2152.1.5 | Add `tree` parameter to `twig_show` MCP tool | `src/Twig.Mcp/Tools/NavigationTools.cs` | S |
| T-2152.1.6 | Make `twig_tree` MCP tool delegate to twig_show with tree=true | `src/Twig.Mcp/Tools/ReadTools.cs` | S |
| T-2152.1.7 | Migrate tree tests to show --tree tests | Tests (multiple files) | M |

**Acceptance Criteria:**
- [ ] `twig show --tree` renders identical output to current `twig tree`
- [ ] `twig show <id> --tree` renders specific item hierarchy
- [ ] `twig tree` (hidden alias) works without deprecation hint
- [ ] `twig_show` MCP tool with tree=true returns tree JSON
- [ ] `twig_tree` MCP tool delegates to twig_show
- [ ] All existing tree tests pass (migrated to new location)

### Issue 2: Workspace --tree — full backlog tree mode

**Goal:** Add `--tree` flag to `WorkspaceCommand` for full hierarchy rendering and update MCP tool.

**Prerequisites:** Issue 1 (TreeRenderingService must exist)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2152.2.1 | Add `--tree` flag to WorkspaceCommand with full-backlog hierarchy rendering | `src/Twig/Commands/WorkspaceCommand.cs` | M |
| T-2152.2.2 | Add `tree` parameter to `twig_workspace` MCP tool | `src/Twig.Mcp/Tools/ReadTools.cs` | S |
| T-2152.2.3 | Add workspace --tree tests | `tests/Twig.Cli.Tests/Commands/WorkspaceCommand_TreeTests.cs` (new) | M |
| T-2152.2.4 | Update GroupedHelp to remove standalone `tree` from Views | `src/Twig/Program.cs` | S |

**Acceptance Criteria:**
- [ ] `twig workspace --tree` renders full backlog hierarchy
- [ ] `twig tree --all` (hidden alias) routes to `workspace --tree`
- [ ] `twig_workspace` MCP tool with tree=true returns tree JSON
- [ ] Output formats (human, json, minimal) supported in tree mode

### Issue 3: Refresh → Sync --pull-only consolidation

**Goal:** Add `--pull-only` flag to `SyncCommand`, remove standalone `RefreshCommand` registration, update hidden alias and MCP tool.

**Prerequisites:** None (independent)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2152.3.1 | Add `--pull-only` flag to SyncCommand that skips flush phase | `src/Twig/Commands/SyncCommand.cs` | S |
| T-2152.3.2 | Update hidden `refresh` alias to route through `sync --pull-only` (no deprecation hint) | `src/Twig/Program.cs` | S |
| T-2152.3.3 | Add `pull_only` parameter to `twig_sync` MCP tool | `src/Twig.Mcp/Tools/MutationTools.cs` | S |
| T-2152.3.4 | Add sync --pull-only tests, update refresh deprecation tests | Tests (multiple files) | M |

**Acceptance Criteria:**
- [ ] `twig sync --pull-only` performs refresh without flush
- [ ] `twig sync --pull-only --force` bypasses dirty guard
- [ ] `twig refresh` routes to `sync --pull-only` silently (no deprecation hint)
- [ ] `twig_sync` MCP tool with pull_only=true skips flush
- [ ] Existing sync tests pass

### Issue 4: States → Process command expansion

**Goal:** Replace `StatesCommand` with `ProcessCommand` that exposes types, states, fields, and transitions. Add `twig_process` MCP tool.

**Prerequisites:** None (independent)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2152.4.1 | Create ProcessCommand with no-args (list types) and with-type (details) modes | `src/Twig/Commands/ProcessCommand.cs` (new) | M |
| T-2152.4.2 | Register `twig process` in Program.cs, add hidden `states` alias | `src/Twig/Program.cs` | S |
| T-2152.4.3 | Delete StatesCommand.cs | `src/Twig/Commands/StatesCommand.cs` (delete) | S |
| T-2152.4.4 | Create `twig_process` MCP tool in ProcessTools.cs | `src/Twig.Mcp/Tools/ProcessTools.cs` (new), `src/Twig.Mcp/Program.cs` | M |
| T-2152.4.5 | Add McpResultBuilder.FormatProcess* methods | `src/Twig.Mcp/Services/McpResultBuilder.cs` | S |
| T-2152.4.6 | Add process command tests and MCP tests | Tests (multiple files) | M |

**Acceptance Criteria:**
- [ ] `twig process` lists all types with state counts
- [ ] `twig process Task` shows states, fields, transitions for Task type
- [ ] `twig states` (hidden alias) shows states for active item's type (backward compat)
- [ ] `twig_process` MCP tool returns JSON process configuration
- [ ] All output formats (human, json, minimal) supported

### Issue 5: IDs output format and sync-first behavior

**Goal:** Add `ids` output format to formatter pipeline and ensure machine formats sync before emitting.

**Prerequisites:** None (independent, but should be implemented alongside or after other issues)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2152.5.1 | Create IdsOutputFormatter implementing IOutputFormatter | `src/Twig/Formatters/IdsOutputFormatter.cs` (new) | M |
| T-2152.5.2 | Register "ids" format in OutputFormatterFactory | `src/Twig/Formatters/OutputFormatterFactory.cs` | S |
| T-2152.5.3 | Add ids support to ShowCommand (batch and tree modes) | `src/Twig/Commands/ShowCommand.cs` | S |
| T-2152.5.4 | Add ids support to WorkspaceCommand | `src/Twig/Commands/WorkspaceCommand.cs` | S |
| T-2152.5.5 | Add sync-first behavior to WorkspaceCommand for machine formats | `src/Twig/Commands/WorkspaceCommand.cs` | M |
| T-2152.5.6 | Register IdsOutputFormatter in DI container | `src/Twig/Program.cs` or DI config | S |
| T-2152.5.7 | Add ids formatter tests and sync-first behavior tests | Tests (multiple files) | M |

**Acceptance Criteria:**
- [ ] `twig workspace -o ids` outputs bare work item IDs, one per line
- [ ] `twig show --batch 1,2,3 -o ids` outputs IDs one per line
- [ ] `twig show --tree -o ids` outputs all tree IDs in depth-first order
- [ ] `twig workspace area -o ids` outputs area-filtered item IDs
- [ ] Machine formats (json, minimal, ids) trigger sync before output in workspace
- [ ] `--no-refresh` skips sync for all formats

---

## PR Groups

### PG-1: Tree Consolidation (Show --tree + Workspace --tree)

**Issues:** Issue 1, Issue 2  
**Classification:** Deep — complex rendering pipeline changes, Spectre integration, significant test migration  
**Estimated LoC:** ~1,000 (new + modified)  
**Files:** ~25

**Contents:**
- TreeRenderingService extraction from TreeCommand
- ShowCommand --tree integration
- WorkspaceCommand --tree integration
- TreeCommand deletion
- Hidden tree alias in Program.cs
- MCP tool enhancements (twig_show tree, twig_workspace tree, twig_tree delegation)
- GroupedHelp updates
- All tree-related test migration and new tests

**Successor:** None (independent of PG-2)

### PG-2: Sync/Process/IDs Consolidation

**Issues:** Issue 3, Issue 4, Issue 5  
**Classification:** Wide — many files touched with mechanical changes, new command and formatter  
**Estimated LoC:** ~1,200 (new + modified)  
**Files:** ~25

**Contents:**
- SyncCommand --pull-only flag
- Hidden refresh alias update (no deprecation hint)
- ProcessCommand (replaces StatesCommand)
- IdsOutputFormatter
- MCP tools (twig_sync pull_only, twig_process new)
- Sync-first behavior for workspace machine formats
- GroupedHelp updates
- All related tests

**Successor:** None (independent of PG-1)

**Execution Order:** PG-1 and PG-2 can be developed and reviewed in parallel. No merge dependency between them.

---

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type |
|-------|------|----------------|--------------|------|
| PG-1 | tree-consolidation | Issues 1–2 / T-2152.1.1–1.7, T-2152.2.1–2.4 | None | Deep |
| PG-2 | sync-process-ids | Issues 3–5 / T-2152.3.1–3.4, T-2152.4.1–4.6, T-2152.5.1–5.7 | None | Wide |

### Execution Order

**PG-1** and **PG-2** are fully independent and can be developed, reviewed, and merged in any order or in parallel. There is no merge-time dependency between them.

**Within PG-1** the internal sequencing is:
1. T-2152.1.1 — Extract `TreeRenderingService` (unblocks everything else in PG-1)
2. T-2152.1.2 / T-2152.1.3 / T-2152.1.4 — Wire ShowCommand, hidden alias, and TreeCommand deletion (parallel)
3. T-2152.1.5 / T-2152.1.6 — MCP tool updates (parallel, after TreeRenderingService exists)
4. T-2152.2.1 — WorkspaceCommand --tree (depends on TreeRenderingService from step 1)
5. T-2152.2.2 / T-2152.2.4 — MCP workspace tree + GroupedHelp (parallel with 2.1)
6. T-2152.1.7 / T-2152.2.3 — Test migration and new tests (after implementation complete)

**Within PG-2** the internal sequencing is:
1. T-2152.5.1 — Create `IdsOutputFormatter` (unblocks ids wiring in commands)
2. T-2152.3.1 / T-2152.4.1 — `SyncCommand --pull-only` and `ProcessCommand` (parallel, independent)
3. T-2152.5.2 / T-2152.5.3 / T-2152.5.4 / T-2152.5.5 / T-2152.5.6 — Wire ids format + sync-first into commands (after step 1)
4. T-2152.3.2 / T-2152.4.2 / T-2152.4.3 — Program.cs wiring and file deletions (after step 2)
5. T-2152.3.3 / T-2152.4.4 / T-2152.4.5 — MCP tools (parallel)
6. T-2152.3.4 / T-2152.4.6 / T-2152.5.7 — All tests (after implementation complete)

### Validation Strategy

**PG-1 — Tree Consolidation**
- `dotnet build` must succeed with `TreatWarningsAsErrors=true` (TreeCommand.cs deleted, no dangling refs)
- `twig show --tree` output must be byte-for-byte identical to previous `twig tree` output (regression test)
- `twig tree` (hidden alias) must route silently — no deprecation text in stdout/stderr
- `twig tree --all` must route to workspace --tree (integration test)
- All migrated TreeCommand tests must pass under new ShowCommand_Tree* test files
- New WorkspaceCommand_TreeTests must cover human, json, minimal output formats
- MCP: `twig_show` with `tree=true` returns hierarchy JSON; `twig_tree` delegates correctly

**PG-2 — Sync / Process / IDs**
- `dotnet build` must succeed (StatesCommand.cs deleted, no dangling refs)
- `twig sync --pull-only` skips flush — verify via mock: `FlushAllAsync` not called, `RefreshOrchestrator` called
- `twig refresh` routes to sync --pull-only silently — no deprecation text
- `twig process` (no-args) lists all types; `twig process <Type>` lists states, fields, transitions
- `twig states` (hidden alias) shows active item's type states (backward-compat parity test)
- `twig workspace -o ids` emits bare IDs, one per line; same for `twig show --batch <ids> -o ids`
- Machine formats (json/minimal/ids) on workspace trigger sync before emit; `--no-refresh` skips it
- `twig_process` MCP tool returns valid JSON process configuration
- All migrated StatesCommand tests pass under ProcessCommandTests

---

## References

- [Functional Spec: tree-query-commands.spec.md](../specs/tree-query-commands.spec.md)
- [Prior Art: Context Commands Realignment](context-commands-realignment.plan.md) (Epic #2149)
- [Prior Art: Mutation Commands Realignment](mutation-commands-realignment.plan.md)
