# Expand twig-mcp with Read and Write Tools

| Field | Value |
|---|---|
| **Epic** | #1814 — Expand twig-mcp with read and write tools |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan adds seven new MCP tools to the `twig-mcp` server — five read-only navigation
tools (`twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint`) and two
write tools (`twig_new`, `twig_link`) — completing the tool surface required for Copilot
agents to perform autonomous work-item triage without falling back to the CLI. Each tool
follows the established `[McpServerToolType]` / `WorkspaceResolver` / `McpResultBuilder`
pattern, is registered via `WithTools<T>()` in `Program.cs` (AOT-safe), and includes full
xUnit + Shouldly + NSubstitute test coverage. The work is organized into three existing
Issues (#1817, #1818, #1819) with concrete Tasks under each.

---

## Background

### Current MCP Architecture

The `twig-mcp` server currently exposes **10 tools** across **4 tool classes**:

| Class | Tools | Purpose |
|---|---|---|
| `ContextTools` | `twig_set`, `twig_status` | Context management |
| `ReadTools` | `twig_tree`, `twig_workspace` | Read-only queries |
| `MutationTools` | `twig_state`, `twig_update`, `twig_note`, `twig_discard`, `twig_sync` | Write operations |
| `WorkspaceTools` | `twig_list_workspaces` | Workspace discovery |

All tools share a consistent architecture:

1. **`[McpServerToolType]` class** with primary constructor injecting `WorkspaceResolver`
2. **`[McpServerTool]` methods** returning `Task<CallToolResult>`
3. **Workspace resolution** via `resolver.Resolve(workspace)` with standard error catch pattern
4. **Active item resolution** via `ctx.ActiveItemResolver.GetActiveItemAsync(ct)` (for context-dependent tools)
5. **JSON formatting** via `McpResultBuilder` static methods using `Utf8JsonWriter`
6. **Registration** via `WithTools<T>()` in `Program.cs` (AOT-safe, no reflection)

### Key Infrastructure Components

- **`WorkspaceResolver`** — Routes tool calls to per-workspace `WorkspaceContext` via: explicit param → single-workspace default → active workspace → error.
- **`WorkspaceContext`** — Bundles all per-workspace services (repos, ADO client, sync, etc.). Created/cached by `WorkspaceContextFactory`.
- **`McpResultBuilder`** — Static utility for JSON response construction. Uses `Utf8JsonWriter` for AOT-safe serialization. Has `ToResult()`, `ToError()`, `BuildJson()`, and specialized `FormatX()` methods.
- **`ReadToolsTestBase`** — Abstract test base class with `BuildResolver()`, `BuildMultiResolver()`, `ParseResult()` helpers, and mock fields for all domain services.
- **`ContextToolsTestBase`** / **`MutationToolsTestBase`** — Test base classes extending `ReadToolsTestBase`.

### Domain Services Available on WorkspaceContext

| Property | Type | Used By New Tools |
|---|---|---|
| `WorkItemRepo` | `IWorkItemRepository` | show, children, parent, sprint |
| `AdoService` | `IAdoWorkItemService` | query, new, link |
| `ContextStore` | `IContextStore` | new (optional set) |
| `ActiveItemResolver` | `ActiveItemResolver` | show, children, parent, link |
| `SyncCoordinatorFactory` | `SyncCoordinatorFactory` | show (link sync), link (resync) |
| `IterationService` | `IIterationService` | sprint |
| `PendingChangeStore` | `IPendingChangeStore` | — |
| `ProcessConfigProvider` | `IProcessConfigurationProvider` | — |
| `Config` | `TwigConfiguration` | query (area defaults), sprint (display name), new (paths) |
| `ContextChangeService` | `ContextChangeService` | new (extend working set) |
| `PromptStateWriter` | `IPromptStateWriter` | new, link (post-mutation) |
| `LinkRepo` | `IWorkItemLinkRepository` | show (link enrichment), link (post-mutation display) |

### CLI Command Mapping

Each new MCP tool maps to existing CLI commands and domain services:

| MCP Tool | CLI Command | Key Domain Service | Core Logic |
|---|---|---|---|
| `twig_show` | `ShowCommand` | `IWorkItemRepository.GetByIdAsync` | Cache-first lookup with enrichment (children count, parent, links) |
| `twig_query` | `QueryCommand` | `WiqlQueryBuilder` + `IAdoWorkItemService.QueryByWiqlAsync` | WIQL build → ADO execute → cache → return |
| `twig_children` | `TreeCommand` (partial) | `IWorkItemRepository.GetChildrenAsync` | Direct child list for given parent ID |
| `twig_parent` | `TreeCommand` (partial) | `IWorkItemRepository.GetParentChainAsync` | Parent chain walk from given item |
| `twig_sprint` | `WorkspaceCommand` (partial) | `IWorkItemRepository.GetByIterationAsync` | Sprint items without full workspace envelope |
| `twig_new` | `NewCommand` | `SeedFactory.CreateUnparented` + `IAdoWorkItemService.CreateAsync` | Validate → create seed → publish → cache |
| `twig_link` | `LinkCommand` | `IAdoWorkItemService.AddLinkAsync/RemoveLinkAsync` | Parent/unparent/add-relation link operations |

---

## Problem Statement

Copilot agents using the MCP server can currently set context, view status/tree/workspace,
and mutate fields/state/notes. However, they **cannot**:

1. **Look up a work item by ID** without changing context (`twig_set` always sets the active item)
2. **Search/query** work items by filters (type, state, title, assignee, etc.)
3. **Navigate children** of any item (not just the active item)
4. **Walk the parent chain** of any item
5. **View sprint backlog** as a focused list (without full workspace envelope)
6. **Create new work items** directly in ADO
7. **Manage hierarchy links** (parent/unparent/add relations)

This forces agents to fall back to CLI commands via shell execution, breaking the
MCP-native tool interaction model and losing structured JSON responses.

---

## Goals and Non-Goals

### Goals

1. **Complete navigation surface**: Add `twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint` for full read-only work item navigation
2. **Complete mutation surface**: Add `twig_new` and `twig_link` for work item creation and linking
3. **Consistent patterns**: Follow established `[McpServerToolType]` / `WorkspaceResolver` / `McpResultBuilder` conventions exactly
4. **Full test coverage**: Every tool method has unit tests covering success, validation, error, and edge cases
5. **AOT safety**: All new code is AOT-compatible (no reflection, source-gen JSON only)
6. **Zero regression**: Existing 10 tools unchanged; `Program.cs` additions are additive only

### Non-Goals

1. **Batch show**: `ShowCommand` supports comma-separated batch IDs; MCP tool accepts single IDs only
2. **Editor integration**: `NewCommand` supports `--editor`; MCP tool skips this (headless environment)
3. **Spectre rendering**: No live/two-pass rendering — MCP returns JSON only
4. **WIQL passthrough**: No raw WIQL input — only structured filter parameters
5. **Seed operations**: `twig_new` creates and publishes to ADO immediately; no local-only seed mode
6. **Seed link management**: `twig_link` operates on published ADO items only

---

## Requirements

### Functional Requirements

| ID | Requirement |
|---|---|
| FR-1 | `twig_show(id)` returns work item details with children count, parent info, and links |
| FR-2 | `twig_show` does NOT change active context (read-only) |
| FR-3 | `twig_query` accepts structured filters: searchText, title, type, state, assignedTo, areaPath, iterationPath, createdSince, changedSince, top |
| FR-4 | `twig_query` builds WIQL via `WiqlQueryBuilder`, executes against ADO, caches results, returns items with truncation flag |
| FR-5 | `twig_children(id)` returns direct children of the specified work item ID |
| FR-6 | `twig_parent(id)` returns the parent chain (root → immediate parent) for the specified item |
| FR-7 | `twig_sprint` returns current sprint backlog items, optionally filtered by user |
| FR-8 | `twig_new` creates a work item in ADO with title, type, area, iteration, description, and optional parent |
| FR-9 | `twig_new` optionally sets the new item as active context |
| FR-10 | `twig_link` supports three operations: parent, unparent, and add-relation |
| FR-11 | `twig_link` validates parenting guards (no self-parent, no duplicate parent) |
| FR-12 | All tools support optional `workspace` parameter for multi-workspace routing |
| FR-13 | All tools return structured JSON via `McpResultBuilder` |

### Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-1 | AOT-compatible: no reflection, `PublishAot=true`, `TrimMode=full` |
| NFR-2 | `TreatWarningsAsErrors=true` — zero warnings in new code |
| NFR-3 | All JSON output via `Utf8JsonWriter` in `McpResultBuilder` (no `JsonSerializer`) |
| NFR-4 | Test coverage: every public tool method has ≥3 test cases (success, validation error, domain error) |
| NFR-5 | Registration in `Program.cs` uses `WithTools<T>()` only |

---

## Proposed Design

### Architecture Overview

The new tools fit into the existing MCP architecture without structural changes. We add
**two new tool classes** (`NavigationTools`, `CreationTools`) alongside the existing four,
keeping the pattern of grouping tools by read/write semantics:

```
Program.cs
  .WithTools<ContextTools>()        // existing: twig_set, twig_status
  .WithTools<ReadTools>()           // existing: twig_tree, twig_workspace
  .WithTools<NavigationTools>()     // NEW: twig_show, twig_query, twig_children, twig_parent, twig_sprint
  .WithTools<MutationTools>()       // existing: twig_state, twig_update, twig_note, twig_discard, twig_sync
  .WithTools<CreationTools>()       // NEW: twig_new, twig_link
  .WithTools<WorkspaceTools>()      // existing: twig_list_workspaces
```

**Rationale for two new classes vs. extending existing:**
- `ReadTools` currently has 2 tools (tree, workspace). Adding 5 more would create a 7-method class.
  Navigation tools have a distinct pattern (ID-based lookup vs. context-based) so a separate class
  improves cohesion.
- `MutationTools` currently has 5 tools. `twig_new` and `twig_link` are creation/structural
  mutations (vs. field/state mutations) warranting a separate class.
- Each class stays under 200 lines, matching existing file sizes.

### Key Components

#### 1. NavigationTools (new file)

```csharp
[McpServerToolType]
public sealed class NavigationTools(WorkspaceResolver resolver)
{
    // twig_show(id, workspace?) → item details with enrichment
    // twig_query(searchText?, title?, type?, state?, ..., workspace?) → query results
    // twig_children(id, workspace?) → child items list
    // twig_parent(id, workspace?) → parent chain
    // twig_sprint(all?, workspace?) → sprint backlog items
}
```

**Tool signatures:**

| Tool | Parameters | Returns |
|---|---|---|
| `twig_show` | `int id`, `string? workspace` | `{ id, title, type, state, assignedTo, areaPath, iterationPath, parentId, parentTitle, childCount, links[], isDirty, isSeed }` |
| `twig_query` | `string? searchText`, `string? title`, `string? type`, `string? state`, `string? assignedTo`, `string? areaPath`, `string? iterationPath`, `string? createdSince`, `string? changedSince`, `int top=25`, `string? workspace` | `{ items[], isTruncated, query, resultCount }` |
| `twig_children` | `int id`, `string? workspace` | `{ parentId, parentTitle, children[], childCount }` |
| `twig_parent` | `int id`, `string? workspace` | `{ itemId, itemTitle, parentChain[], depth }` |
| `twig_sprint` | `bool all=false`, `string? workspace` | `{ iteration, items[], itemCount }` |

#### 2. CreationTools (new file)

```csharp
[McpServerToolType]
public sealed class CreationTools(WorkspaceResolver resolver)
{
    // twig_new(title, type, area?, iteration?, description?, parent?, set?, workspace?) → created item
    // twig_link(operation, targetId, linkType?, workspace?) → link result
}
```

**Tool signatures:**

| Tool | Parameters | Returns |
|---|---|---|
| `twig_new` | `string title`, `string type`, `string? area`, `string? iteration`, `string? description`, `int? parent`, `bool set=false`, `string? workspace` | `{ id, title, type, state, areaPath, iterationPath, parentId }` |
| `twig_link` | `string operation` (parent/unparent/relation), `int targetId`, `string? linkType`, `string? workspace` | `{ sourceId, targetId, operation, linkType, links[] }` |

#### 3. McpResultBuilder Extensions (modified file)

Add 6 new `FormatX()` methods to the existing `McpResultBuilder`:

| Method | Purpose |
|---|---|
| `FormatShow(WorkItem, parent?, childCount, links)` | Format single item detail view |
| `FormatQueryResults(items, isTruncated, query, count)` | Format query result set |
| `FormatChildren(parentId, parentTitle, children)` | Format child items list |
| `FormatParentChain(itemId, itemTitle, parentChain)` | Format parent chain |
| `FormatSprint(iteration, items)` | Format sprint backlog |
| `FormatCreated(WorkItem)` | Format newly created item |
| `FormatLinkChange(sourceId, targetId, operation, linkType, links)` | Format link operation result |

All methods follow the existing `BuildJson(Action<Utf8JsonWriter>)` pattern.

### Data Flow

#### twig_show Flow
```
twig_show(id=42)
  → resolver.Resolve(workspace)
  → ctx.WorkItemRepo.GetByIdAsync(42)     # cache-first
  → ctx.WorkItemRepo.GetChildrenAsync(42)  # child count
  → ctx.WorkItemRepo.GetByIdAsync(parentId) # parent info (if exists)
  → ctx.LinkRepo.GetLinksAsync(42)          # non-hierarchy links (best-effort)
  → McpResultBuilder.FormatShow(item, parent, childCount, links)
```

#### twig_query Flow
```
twig_query(type="Bug", state="Active")
  → resolver.Resolve(workspace)
  → Build QueryParameters from tool params + ctx.Config defaults
  → WiqlQueryBuilder.Build(params) → WIQL string
  → ctx.AdoService.QueryByWiqlAsync(wiql, top)
  → ctx.AdoService.FetchBatchAsync(ids)
  → ctx.WorkItemRepo.SaveBatchAsync(items)   # cache
  → McpResultBuilder.FormatQueryResults(items, isTruncated, query, count)
```

#### twig_children / twig_parent Flow
```
twig_children(id=42)
  → resolver.Resolve(workspace)
  → ctx.WorkItemRepo.GetByIdAsync(42)         # validate item exists
  → ctx.WorkItemRepo.GetChildrenAsync(42)
  → McpResultBuilder.FormatChildren(42, title, children)

twig_parent(id=42)
  → resolver.Resolve(workspace)
  → ctx.WorkItemRepo.GetByIdAsync(42)         # validate item exists
  → ctx.WorkItemRepo.GetParentChainAsync(42)
  → McpResultBuilder.FormatParentChain(42, title, chain)
```

#### twig_sprint Flow
```
twig_sprint(all=false)
  → resolver.Resolve(workspace)
  → ctx.IterationService.GetCurrentIterationAsync()
  → ctx.WorkItemRepo.GetByIteration[AndAssignee]Async(iteration, [displayName])
  → McpResultBuilder.FormatSprint(iteration, items)
```

#### twig_new Flow
```
twig_new(title="Fix login", type="Bug", parent=42)
  → resolver.Resolve(workspace)
  → Validate type via WorkItemType.Parse()
  → Resolve area/iteration from params or ctx.Config defaults
  → SeedFactory.CreateUnparented(title, type, area, iteration, assignedTo, parent)
  → Set description field if provided
  → ctx.AdoService.CreateAsync(seed)           # publish to ADO
  → ctx.AdoService.FetchAsync(newId)           # fetch back
  → ctx.WorkItemRepo.SaveAsync(fetched)        # cache
  → (optional) ctx.ContextStore.SetActiveWorkItemIdAsync(newId)
  → (optional) ctx.ContextChangeService.ExtendWorkingSetAsync(newId)
  → ctx.PromptStateWriter.WritePromptStateAsync()
  → McpResultBuilder.FormatCreated(fetched)
```

#### twig_link Flow
```
twig_link(operation="parent", targetId=100)
  → resolver.Resolve(workspace)
  → ctx.ActiveItemResolver.GetActiveItemAsync()    # resolve active item
  → Validate guards (no self-parent, etc.)
  → ctx.AdoService.AddLinkAsync/RemoveLinkAsync(...)
  → Resync both items via SyncCoordinator
  → ctx.PromptStateWriter.WritePromptStateAsync()
  → McpResultBuilder.FormatLinkChange(...)
```

### Design Decisions

| Decision | Rationale |
|---|---|
| **Two new tool classes** (NavigationTools, CreationTools) vs. extending ReadTools/MutationTools | Keeps each class focused and under 200 lines. Navigation tools are ID-based lookups; creation tools are structural mutations — both distinct from existing tool semantics. |
| **`twig_show` is cache-only** (like CLI ShowCommand) | Consistent with CLI behavior. Item must be in cache via prior `twig_set` or `twig_query`. Avoids silent ADO calls on every show. |
| **`twig_query` hits ADO directly** | Queries must be live (stale cache defeats the purpose). Results are cached for subsequent show/tree operations. |
| **`twig_children` and `twig_parent` accept explicit ID** | Unlike `twig_tree` (which uses active context), navigation tools should work on any ID for agent flexibility. |
| **`twig_sprint` is separate from `twig_workspace`** | `twig_workspace` returns context + sprint + seeds + stale/dirty info. `twig_sprint` returns just sprint items — simpler, faster, focused. |
| **`twig_new` publishes immediately** | MCP is headless — no interactive seed editing. Agents provide all fields upfront. Matches agent workflow: create → set → continue. |
| **`twig_link` operates on active item** | Matches CLI `LinkCommand` pattern. Agent sets context first, then links. Prevents accidental operations on wrong items. |
| **Duration parsing reused from QueryCommand** | Same `Nd/Nw/Nm` pattern, extracted as a shared static method. |

---

## Dependencies

### External Dependencies

- `ModelContextProtocol` NuGet package (already referenced) — `[McpServerToolType]`, `[McpServerTool]`, `CallToolResult`
- No new external packages required

### Internal Dependencies

- `Twig.Domain` — `WorkItem`, `QueryParameters`, `WiqlQueryBuilder`, `SeedFactory`, `WorkItemType`, `AreaPath`, `IterationPath`, `WorkItemLink`, `Result<T>`
- `Twig.Infrastructure` — `IAdoWorkItemService`, `ConflictRetryHelper`, `AdoException`, `TwigConfiguration`
- `Twig.Mcp.Services` — `WorkspaceResolver`, `WorkspaceContext`, `McpResultBuilder`

### Sequencing Constraints

Issue #1817 (navigation tools) and #1818 (creation tools) are independent and can proceed
in parallel. Issue #1819 (Program.cs registration) depends on both and is the final step.

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | 5 read-only MCP tools: show, query, children, parent, sprint |
| `src/Twig.Mcp/Tools/CreationTools.cs` | 2 write MCP tools: new, link |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsTestBase.cs` | Test base class for navigation tools |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` | Tests for twig_show |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsQueryTests.cs` | Tests for twig_query |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs` | Tests for twig_children |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsParentTests.cs` | Tests for twig_parent |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsSprintTests.cs` | Tests for twig_sprint |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsTestBase.cs` | Test base class for creation tools |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsNewTests.cs` | Tests for twig_new |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkTests.cs` | Tests for twig_link |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add 7 new `FormatX()` methods for new tool responses |
| `src/Twig.Mcp/Program.cs` | Add 2 lines: `.WithTools<NavigationTools>()`, `.WithTools<CreationTools>()` |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Add tests for 7 new format methods |

---

## ADO Work Item Structure

### Epic #1814: Expand twig-mcp with read and write tools

---

### Issue #1817: Navigation Tools — Read-only MCP tools

**Goal:** Implement `twig_show`, `twig_query`, `twig_children`, `twig_parent`, and `twig_sprint` as MCP tools in a new `NavigationTools` class.

**Prerequisites:** None (independent)

**Tasks:**

| Task | Description | Files | Effort |
|---|---|---|---|
| T-1817.1 | Create `NavigationTools.cs` with `twig_show` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~80 LoC |
| T-1817.2 | Add `twig_query` to `NavigationTools` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~90 LoC |
| T-1817.3 | Add `twig_children` and `twig_parent` to `NavigationTools` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~70 LoC |
| T-1817.4 | Add `twig_sprint` to `NavigationTools` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~40 LoC |
| T-1817.5 | Add `McpResultBuilder.FormatShow/FormatQueryResults/FormatChildren/FormatParentChain/FormatSprint` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~120 LoC |
| T-1817.6 | Create `NavigationToolsTestBase.cs` and all test files for show/query/children/parent/sprint | `tests/Twig.Mcp.Tests/Tools/NavigationTools*.cs` | ~500 LoC |

**Acceptance Criteria:**
- [ ] `twig_show(id)` returns structured JSON with item details, children count, parent info, and links
- [ ] `twig_show` does NOT modify active context
- [ ] `twig_query` builds WIQL from structured filters, executes against ADO, caches, returns results
- [ ] `twig_query` returns truncation flag when results hit `top` limit
- [ ] `twig_query` respects configured default area paths when no explicit area filter
- [ ] `twig_children(id)` returns direct children as array with count
- [ ] `twig_parent(id)` returns parent chain root→immediate-parent with depth
- [ ] `twig_sprint` returns current iteration items, filtered by user when `all=false`
- [ ] All tools handle workspace resolution errors gracefully
- [ ] All tools handle missing/not-found items with clear error messages
- [ ] All test files pass with `dotnet test`
- [ ] Zero warnings with `TreatWarningsAsErrors=true`

---

### Issue #1818: Creation Tools — Write MCP tools

**Goal:** Implement `twig_new` and `twig_link` as MCP tools in a new `CreationTools` class.

**Prerequisites:** None (independent of #1817)

**Tasks:**

| Task | Description | Files | Effort |
|---|---|---|---|
| T-1818.1 | Create `CreationTools.cs` with `twig_new` | `src/Twig.Mcp/Tools/CreationTools.cs` | ~100 LoC |
| T-1818.2 | Add `twig_link` to `CreationTools` (parent, unparent, relation operations) | `src/Twig.Mcp/Tools/CreationTools.cs` | ~120 LoC |
| T-1818.3 | Add `McpResultBuilder.FormatCreated/FormatLinkChange` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~60 LoC |
| T-1818.4 | Create `CreationToolsTestBase.cs` and test files for new/link | `tests/Twig.Mcp.Tests/Tools/CreationTools*.cs` | ~400 LoC |

**Acceptance Criteria:**
- [ ] `twig_new` validates title (required), type (required, parsed via `WorkItemType.Parse`), parent (positive int if given)
- [ ] `twig_new` resolves area/iteration from params or config defaults
- [ ] `twig_new` creates via `IAdoWorkItemService.CreateAsync`, fetches back, caches
- [ ] `twig_new` optionally sets active context and extends working set when `set=true`
- [ ] `twig_new` updates prompt state after creation
- [ ] `twig_link("parent", targetId)` adds hierarchy-reverse link
- [ ] `twig_link("unparent")` removes existing parent link
- [ ] `twig_link("relation", targetId, linkType)` adds non-hierarchy link (Related, Predecessor, Successor)
- [ ] `twig_link` validates parenting guards (no self-parent, no duplicate parent)
- [ ] `twig_link` resyncs affected items after mutation
- [ ] ADO exceptions are caught and returned as `McpResultBuilder.ToError()`
- [ ] All test files pass with `dotnet test`
- [ ] Zero warnings with `TreatWarningsAsErrors=true`

---

### Issue #1819: Program.cs Registration and Integration Verification

**Goal:** Register new tool classes in `Program.cs` and verify end-to-end integration.

**Prerequisites:** #1817 and #1818 must be complete

**Tasks:**

| Task | Description | Files | Effort |
|---|---|---|---|
| T-1819.1 | Add `WithTools<NavigationTools>()` and `WithTools<CreationTools>()` to `Program.cs` | `src/Twig.Mcp/Program.cs` | ~5 LoC |
| T-1819.2 | Verify build succeeds with `dotnet build` (AOT-compatible, zero warnings) | — | Verification |
| T-1819.3 | Run full test suite and verify all tests pass | — | Verification |
| T-1819.4 | Add `McpResultBuilder` format method tests for all 7 new methods | `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | ~200 LoC |

**Acceptance Criteria:**
- [ ] `Program.cs` registers both new tool classes via `WithTools<T>()`
- [ ] `dotnet build` succeeds with zero warnings for `Twig.Mcp` project
- [ ] `dotnet test` passes for all test projects
- [ ] `dotnet publish -c Release` succeeds (AOT compilation validation)
- [ ] All 17 tools (10 existing + 7 new) are discoverable by MCP clients

---

## PR Groups

### PG-1: Navigation Tools (Read-Only)

**Issues/Tasks:** All of Issue #1817 (T-1817.1 through T-1817.6)

**Classification:** Deep — 3 new files + 2 modified files, complex query logic in twig_query

**Files:**
- `src/Twig.Mcp/Tools/NavigationTools.cs` (new)
- `src/Twig.Mcp/Services/McpResultBuilder.cs` (modified — 5 new format methods)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsTestBase.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsQueryTests.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsParentTests.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/NavigationToolsSprintTests.cs` (new)

**Estimated size:** ~900 LoC across 8 files

**Successor:** PG-3

---

### PG-2: Creation Tools (Write)

**Issues/Tasks:** All of Issue #1818 (T-1818.1 through T-1818.4)

**Classification:** Deep — 3 new files + 1 modified file, ADO mutation logic with error handling

**Files:**
- `src/Twig.Mcp/Tools/CreationTools.cs` (new)
- `src/Twig.Mcp/Services/McpResultBuilder.cs` (modified — 2 new format methods)
- `tests/Twig.Mcp.Tests/Tools/CreationToolsTestBase.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/CreationToolsNewTests.cs` (new)
- `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkTests.cs` (new)

**Estimated size:** ~680 LoC across 5 files

**Successor:** PG-3

**Note:** PG-1 and PG-2 can proceed in parallel since they modify different sections of `McpResultBuilder.cs`.

---

### PG-3: Registration and Integration

**Issues/Tasks:** All of Issue #1819 (T-1819.1 through T-1819.4)

**Classification:** Wide — 2 modified files, mechanical registration + integration tests

**Files:**
- `src/Twig.Mcp/Program.cs` (modified — 2 lines)
- `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` (modified — format method tests)

**Estimated size:** ~210 LoC across 2 files

**Prerequisites:** PG-1 and PG-2 must merge first

---

## Open Questions

| # | Question | Severity | Notes |
|---|---|---|---|
| 1 | Should `twig_show` auto-fetch from ADO if the item is not in cache, or require prior `twig_set`/`twig_query`? | Low | Plan assumes cache-only (matching CLI behavior). Agents can use `twig_set` to fetch. |
| 2 | Should `twig_link` support `reparent` as a third operation alongside `parent` and `unparent`? | Low | Plan includes only `parent`, `unparent`, and `relation`. Reparent can be done as unparent + parent. If desired, add as a follow-up. |
| 3 | Should `twig_query` support `--output ids` mode returning just IDs? | Low | Plan returns full item objects. IDs-only mode can be a follow-up optimization. |

---

## References

- Epic work item: [#1814](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1814)
- Issue #1817: [Navigation Tools](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1817)
- Issue #1818: [Creation Tools](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1818)
- Issue #1819: [Registration & Integration](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1819)
- Existing MCP server: `src/Twig.Mcp/`
- MCP SDK docs: [ModelContextProtocol NuGet](https://www.nuget.org/packages/ModelContextProtocol)
