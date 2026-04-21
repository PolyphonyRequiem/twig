# Expand twig-mcp with Read & Write Tools

| Field | Value |
|---|---|
| **Epic** | #1814 — Expand twig-mcp with read and write tools |
| **Status** | ✅ Done |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan adds seven new MCP tools to the twig-mcp server, completing the tool
surface needed for Copilot agents to perform autonomous work-item triage without
falling back to the CLI. The new tools split into **read-only** operations
(`twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint`) and
**write** operations (`twig_new`, `twig_link`). Each tool follows the established
`[McpServerToolType]` / `WorkspaceResolver` / `McpResultBuilder` pattern, is
registered via `WithTools<T>()` in Program.cs (AOT-safe), and has full xUnit +
Shouldly + NSubstitute test coverage in `tests/Twig.Mcp.Tests/Tools/`.

---

## Background

### Current MCP Tool Surface

The twig-mcp server currently exposes 9 tools across 4 tool classes:

| Class | Tools | Category |
|---|---|---|
| `ContextTools` | `twig_set`, `twig_status` | Context management |
| `ReadTools` | `twig_tree`, `twig_workspace` | Read-only queries |
| `MutationTools` | `twig_state`, `twig_update`, `twig_note`, `twig_discard`, `twig_sync` | Mutations |
| `WorkspaceTools` | `twig_list_workspaces` | Workspace management |

All tool classes follow the same architecture:
1. Decorated with `[McpServerToolType]` and use `[McpServerTool]` + `[Description]` per method
2. Injected with `WorkspaceResolver` (primary constructor) for workspace resolution
3. Each tool method accepts an optional `workspace` parameter and `CancellationToken`
4. Workspace resolution follows: explicit param → single-workspace default → active workspace → error
5. Results are formatted via `McpResultBuilder` static methods using `Utf8JsonWriter`
6. Registered in `Program.cs` via `.WithTools<T>()` (AOT-safe, no reflection discovery)

### Key Domain Services Available

Services already available on `WorkspaceContext` that the new tools will consume:

| Service | Relevant Methods | Used By |
|---|---|---|
| `IWorkItemRepository` | `GetByIdAsync`, `GetChildrenAsync`, `GetParentChainAsync`, `SaveAsync`, `SaveBatchAsync`, `FindByPatternAsync` | twig_show, twig_children, twig_parent, twig_new |
| `IAdoWorkItemService` | `FetchAsync`, `FetchChildrenAsync`, `QueryByWiqlAsync`, `FetchBatchAsync`, `CreateAsync`, `AddLinkAsync` | twig_query, twig_new, twig_link, twig_show |
| `IIterationService` | `GetCurrentIterationAsync`, `GetTeamAreaPathsAsync` | twig_sprint |
| `IWorkItemLinkRepository` | `GetLinksAsync`, `SaveLinksAsync` | twig_link |
| `IProcessConfigurationProvider` | `GetConfiguration()` | twig_new (type validation) |
| `WiqlQueryBuilder` | `Build(QueryParameters)` | twig_query |
| `LinkTypeMapper` | `TryResolve`, `Resolve`, `SupportedTypes` | twig_link |
| `SeedFactory` | `Create`, `CreateUnparented` | twig_new |
| `SyncCoordinatorFactory` | `ReadOnly.SyncLinksAsync` | twig_link |
| `ActiveItemResolver` | `GetActiveItemAsync`, `ResolveByIdAsync` | twig_show (does NOT use this — reads by explicit ID) |

### Call-Site Audit: McpResultBuilder

`McpResultBuilder` is the shared formatting utility. The new tools will add methods
to it. Existing call sites:

| File | Method | Usage | Impact |
|---|---|---|---|
| `ContextTools.cs` | `Set` | `FormatWorkItemWithWorkingSet`, `ToError` | None — additive |
| `ContextTools.cs` | `Status` | `FormatStatus`, `ToError` | None — additive |
| `ReadTools.cs` | `Tree` | `FormatTree`, `ToError` | None — additive |
| `ReadTools.cs` | `Workspace` | `FormatWorkspace`, `ToError` | None — additive |
| `MutationTools.cs` | `State` | `FormatStateChange`, `ToError` | None — additive |
| `MutationTools.cs` | `Update` | `FormatFieldUpdate`, `ToError` | None — additive |
| `MutationTools.cs` | `Note` | `FormatNoteAdded`, `ToError` | None — additive |
| `MutationTools.cs` | `Discard` | `FormatDiscard*`, `ToError` | None — additive |
| `MutationTools.cs` | `Sync` | `FormatFlushSummary`, `ToError` | None — additive |
| `WorkspaceTools.cs` | `ListWorkspaces` | `ToResult` | None — additive |

All changes to `McpResultBuilder` are **purely additive** — new static methods only.
No existing methods are modified.

### Call-Site Audit: Program.cs Tool Registration

New tool classes must be registered in `Program.cs` via `.WithTools<T>()`. The
existing chain:

```csharp
.WithTools<ContextTools>()
.WithTools<ReadTools>()
.WithTools<MutationTools>()
.WithTools<WorkspaceTools>();
```

Two new registrations will be appended (see Proposed Design).

---

## Problem Statement

Copilot agents using twig-mcp currently face several gaps:

1. **No read-only lookup by ID** — `twig_set` changes the active context as a side effect.
   Agents need to inspect arbitrary work items without disturbing the user's focus.
2. **No search/query** — agents cannot discover work items by type, state, or title
   without using the CLI binary directly.
3. **No hierarchy traversal** — getting children or parent requires multiple
   `twig_tree` calls and parsing nested JSON, which is fragile and token-expensive.
4. **No sprint awareness** — agents cannot determine what iteration is current or
   what items are in the active sprint without `twig_workspace` (which returns far
   more data than needed).
5. **No work item creation** — agents must fall back to the CLI `twig seed` + `twig save`
   flow, which is multi-step and error-prone.
6. **No link creation** — agents cannot create relationships (related, predecessor,
   successor) between work items via MCP.

---

## Goals and Non-Goals

### Goals

1. Add 5 read-only tools: `twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint`
2. Add 2 write tools: `twig_new`, `twig_link`
3. Follow all existing patterns (tool classes, resolver, result builder, AOT registration)
4. Achieve ≥90% test coverage for all new tools with xUnit + Shouldly + NSubstitute
5. All 7 tools support multi-workspace via the `workspace` parameter

### Non-Goals

- **Batch creation** — `twig_new` creates a single work item per call (batch can be a future tool)
- **Link deletion** — `twig_link` only creates links; `twig_unlink` is out of scope
- **WIQL raw passthrough** — `twig_query` accepts structured parameters, not raw WIQL strings
- **Seed workflow** — `twig_new` creates work items directly in ADO (not local seeds); seed
  workflow remains CLI-only
- **TUI rendering** — MCP tools return JSON, not Spectre.Console formatted output

---

## Requirements

### Functional

| ID | Requirement |
|---|---|
| FR-1 | `twig_show` returns a single work item by numeric ID without changing active context |
| FR-2 | `twig_show` fetches from ADO if the item is not in the local cache |
| FR-3 | `twig_query` accepts optional filters: type, state, title, assignedTo, areaPath, iterationPath, top |
| FR-4 | `twig_query` executes WIQL via ADO API and caches results locally |
| FR-5 | `twig_query` returns truncation indicator when result count equals top limit |
| FR-6 | `twig_children` returns direct children of a work item by ID |
| FR-7 | `twig_parent` returns the parent work item (if any) for a given ID |
| FR-8 | `twig_sprint` returns current iteration path and optionally lists sprint items |
| FR-9 | `twig_new` creates a work item in ADO with type, title, and optional parent/description |
| FR-10 | `twig_new` validates type against process configuration when parent is specified |
| FR-11 | `twig_link` creates a relationship between two work items using friendly link type names |
| FR-12 | All tools support multi-workspace via the optional `workspace` parameter |

### Non-Functional

| ID | Requirement |
|---|---|
| NFR-1 | All new code is AOT-compatible (no reflection, source-gen JSON only) |
| NFR-2 | All new tools follow `TreatWarningsAsErrors` — zero warnings |
| NFR-3 | ADO API failures in read tools return `McpResultBuilder.ToError`, not exceptions |
| NFR-4 | Cache writes are best-effort — ADO is the source of truth |
| NFR-5 | No telemetry data leakage (no org/project/user names in any future telemetry) |

---

## Proposed Design

### Architecture Overview

The 7 new tools are organized into **two new tool classes**, following the existing
pattern of grouping by concern:

```
src/Twig.Mcp/Tools/
├── ContextTools.cs          (existing: twig_set, twig_status)
├── ReadTools.cs             (existing: twig_tree, twig_workspace)
├── MutationTools.cs         (existing: twig_state, twig_update, twig_note, twig_discard, twig_sync)
├── WorkspaceTools.cs        (existing: twig_list_workspaces)
├── NavigationTools.cs       (NEW: twig_show, twig_children, twig_parent, twig_sprint, twig_query)
└── CreationTools.cs         (NEW: twig_new, twig_link)
```

**Why two new classes instead of extending existing ones?**
- `ReadTools` currently owns tools that require active context (`twig_tree`, `twig_workspace`).
  The new read tools (`twig_show`, `twig_children`, `twig_parent`) operate on explicit IDs
  without requiring active context — a fundamentally different contract.
- `twig_query` and `twig_sprint` are discovery tools, not context-dependent reads.
- `twig_new` and `twig_link` are creation/mutation tools, but they don't operate on the
  active context like `MutationTools` does. Keeping them separate maintains SRP.

### Key Components

#### 1. NavigationTools (5 read-only tools)

```csharp
[McpServerToolType]
public sealed class NavigationTools(WorkspaceResolver resolver)
```

**twig_show** — Read-only work item lookup by ID
- Parameters: `int id`, `string? workspace`
- Flow: resolve workspace → `WorkItemRepo.GetByIdAsync(id)` → if null, `AdoService.FetchAsync(id)` → cache result → format response
- Key difference from `twig_set`: does NOT call `ContextStore.SetActiveWorkItemIdAsync`, does NOT call `ContextChangeService.ExtendWorkingSetAsync`
- Returns: id, title, type, state, assignedTo, areaPath, iterationPath, parentId, isDirty, isSeed, fields (description if present)

**twig_query** — WIQL search with structured filters
- Parameters: `string? searchText`, `string? type`, `string? state`, `string? title`, `string? assignedTo`, `string? areaPath`, `string? iterationPath`, `int top = 25`, `string? workspace`
- Flow: build `QueryParameters` → `WiqlQueryBuilder.Build()` → `AdoService.QueryByWiqlAsync(wiql, top)` → `AdoService.FetchBatchAsync(ids)` → `WorkItemRepo.SaveBatchAsync(items)` → format response
- Mirrors `QueryCommand.ExecuteCoreAsync` logic but returns structured JSON instead of TUI output
- Includes default area path resolution from workspace config
- Returns: array of work items, totalCount, isTruncated flag, queryDescription

**twig_children** — List children of a work item
- Parameters: `int id`, `string? workspace`
- Flow: resolve workspace → `WorkItemRepo.GetChildrenAsync(id)` → format array
- Returns: parentId, array of child work items, count

**twig_parent** — Get parent of a work item
- Parameters: `int id`, `string? workspace`
- Flow: resolve workspace → `WorkItemRepo.GetByIdAsync(id)` → if null or not in cache, `AdoService.FetchAsync(id)` → read ParentId → if ParentId exists, `WorkItemRepo.GetByIdAsync(parentId)` (or fetch from ADO) → format
- Returns: child item summary, parent item (or null if root)

**twig_sprint** — Current iteration info
- Parameters: `bool items = false`, `string? workspace`
- Flow: resolve workspace → `IterationService.GetCurrentIterationAsync()` → if `items=true`, `WorkItemRepo.GetByIterationAndAssigneeAsync()` → format
- Returns: iterationPath, optionally array of sprint items

#### 2. CreationTools (2 write tools)

```csharp
[McpServerToolType]
public sealed class CreationTools(WorkspaceResolver resolver)
```

**twig_new** — Create a work item in ADO
- Parameters: `string type`, `string title`, `int? parentId`, `string? description`, `string? assignedTo`, `string? workspace`
- Flow:
  1. Parse `type` via `WorkItemType.Parse()`
  2. If `parentId` specified: fetch parent from cache/ADO, validate child type via `ProcessConfiguration`
  3. Build field changes: System.Title, System.WorkItemType, optionally System.Description (with markdown→HTML conversion), System.AssignedTo
  4. Resolve area/iteration path from parent (if given) or workspace defaults
  5. Create seed via `SeedFactory.Create` or `SeedFactory.CreateUnparented`
  6. Publish to ADO via `AdoService.CreateAsync(seed)`
  7. Fetch back the created item via `AdoService.FetchAsync(newId)`
  8. Cache locally via `WorkItemRepo.SaveAsync`
- Returns: id (ADO-assigned), title, type, state, parentId, url

**twig_link** — Create a relationship between work items
- Parameters: `int sourceId`, `int targetId`, `string linkType`, `string? workspace`
- Flow:
  1. Validate `linkType` via `LinkTypeMapper.TryResolve(linkType, out adoType)`
  2. If invalid, return error listing `LinkTypeMapper.SupportedTypes`
  3. Call `AdoService.AddLinkAsync(sourceId, targetId, adoType)`
  4. Best-effort: sync links via `SyncCoordinatorFactory.ReadOnly.SyncLinksAsync`
- Returns: sourceId, targetId, linkType, success confirmation

#### 3. McpResultBuilder Extensions (additive only)

New static methods added to `McpResultBuilder`:

| Method | Used By | Output Schema |
|---|---|---|
| `FormatWorkItem(WorkItem, string?)` | twig_show | Full work item JSON with all fields |
| `FormatQueryResults(IReadOnlyList<WorkItem>, bool, string, string?)` | twig_query | Items array + truncation + query description |
| `FormatChildren(int, IReadOnlyList<WorkItem>, string?)` | twig_children | Parent ID + children array + count |
| `FormatParent(WorkItem, WorkItem?, string?)` | twig_parent | Child summary + parent (nullable) |
| `FormatSprint(IterationPath, IReadOnlyList<WorkItem>?, string?)` | twig_sprint | Iteration path + optional items |
| `FormatCreated(WorkItem, string?)` | twig_new | Created item details with ADO ID |
| `FormatLinked(int, int, string, string?)` | twig_link | Source, target, link type confirmation |

#### 4. Program.cs Registration

Two new `.WithTools<T>()` calls appended to the MCP server builder chain:

```csharp
.WithTools<ContextTools>()
.WithTools<ReadTools>()
.WithTools<MutationTools>()
.WithTools<WorkspaceTools>()
.WithTools<NavigationTools>()    // NEW
.WithTools<CreationTools>();     // NEW
```

### Data Flow

#### twig_show Data Flow
```
Agent → twig_show(id=42) → WorkspaceResolver.Resolve()
  → WorkItemRepo.GetByIdAsync(42)
  → [cache miss] → AdoService.FetchAsync(42) → WorkItemRepo.SaveAsync()
  → McpResultBuilder.FormatWorkItem()
  → JSON response (no context change)
```

#### twig_query Data Flow
```
Agent → twig_query(type="Bug", state="Active")
  → WorkspaceResolver.Resolve()
  → QueryParameters{TypeFilter="Bug", StateFilter="Active"}
  → WiqlQueryBuilder.Build() → WIQL string
  → AdoService.QueryByWiqlAsync(wiql, top) → int[] ids
  → AdoService.FetchBatchAsync(ids) → WorkItem[]
  → WorkItemRepo.SaveBatchAsync() (best-effort cache)
  → McpResultBuilder.FormatQueryResults()
  → JSON response
```

#### twig_new Data Flow
```
Agent → twig_new(type="Task", title="Fix bug", parentId=42)
  → WorkspaceResolver.Resolve()
  → WorkItemType.Parse("Task") → validate
  → WorkItemRepo.GetByIdAsync(42) [or FetchAsync] → parent
  → ProcessConfig.GetAllowedChildTypes(parent.Type) → validate "Task" is allowed
  → SeedFactory.Create(title, parent, processConfig, typeOverride)
  → AdoService.CreateAsync(seed) → int newId
  → AdoService.FetchAsync(newId) → WorkItem created
  → WorkItemRepo.SaveAsync(created) (cache)
  → McpResultBuilder.FormatCreated(created)
  → JSON response
```

### Design Decisions

| Decision | Rationale |
|---|---|
| **DD-1: Separate tool classes** | NavigationTools don't require active context; CreationTools don't modify active context. Keeps SRP and makes test base classes cleaner. |
| **DD-2: twig_show does not change context** | Core value proposition — agents can inspect items without side effects. Uses `GetByIdAsync` / `FetchAsync` only. |
| **DD-3: twig_query reuses WiqlQueryBuilder** | Avoids duplicating WIQL generation logic. QueryParameters already supports all needed filters. |
| **DD-4: twig_new creates directly in ADO** | MCP agents need real ADO IDs for linking and referencing. The seed→publish CLI flow is unnecessary overhead for programmatic creation. |
| **DD-5: twig_new uses SeedFactory for validation** | Reuses existing parent/child type validation and path inheritance logic rather than duplicating it. |
| **DD-6: twig_link uses LinkTypeMapper** | Existing bidirectional mapper handles friendly→ADO name translation with error messages listing supported types. |
| **DD-7: Cache-first with ADO fallback** | twig_show, twig_children, twig_parent check cache first, fall back to ADO fetch. Consistent with twig_set pattern. |
| **DD-8: twig_sprint returns iteration path** | Lightweight — just the current iteration. Optionally includes sprint items when `items=true` to avoid unnecessary fetches. |

---

## Dependencies

### External
- `ModelContextProtocol` NuGet package (already referenced)
- Azure DevOps REST API v7.1 (already integrated)

### Internal
- `WiqlQueryBuilder` (Domain) — used by twig_query
- `LinkTypeMapper` (Domain) — used by twig_link
- `SeedFactory` (Domain) — used by twig_new
- `WorkItemType.Parse` (Domain) — used by twig_new
- `McpResultBuilder` (Mcp/Services) — extended with new format methods
- `WorkspaceResolver` (Mcp/Services) — injected into both new tool classes

### Sequencing
- No external sequencing constraints. All dependent domain services already exist.
- Issue 1 (NavigationTools) and Issue 2 (CreationTools) can be developed in parallel
  since they occupy separate files and add independent McpResultBuilder methods.

---

## Open Questions

| # | Question | Severity | Notes |
|---|---|---|---|
| 1 | Should `twig_show` include the full field dictionary or just core fields? | Low | Proposal: include `description` field if present, plus all Fields dictionary entries. Agents often need the description. |
| 2 | Should `twig_query` support `searchText` (title OR description)? | Low | Proposal: yes, matching QueryCommand's existing behavior. Already supported by WiqlQueryBuilder. |
| 3 | Should `twig_new` support setting arbitrary fields beyond title/description/assignedTo? | Low | Proposal: not in V1 — keep it simple with the most common fields. Can add `fields` dictionary parameter later. |

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | 5 read-only MCP tools: twig_show, twig_query, twig_children, twig_parent, twig_sprint |
| `src/Twig.Mcp/Tools/CreationTools.cs` | 2 write MCP tools: twig_new, twig_link |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsTestBase.cs` | Shared test setup for NavigationTools (extends ReadToolsTestBase pattern) |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` | Unit tests for twig_show |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsQueryTests.cs` | Unit tests for twig_query |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs` | Unit tests for twig_children |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsParentTests.cs` | Unit tests for twig_parent |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsSprintTests.cs` | Unit tests for twig_sprint |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsTestBase.cs` | Shared test setup for CreationTools |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsNewTests.cs` | Unit tests for twig_new |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkTests.cs` | Unit tests for twig_link |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add 7 new static format methods (FormatWorkItem, FormatQueryResults, FormatChildren, FormatParent, FormatSprint, FormatCreated, FormatLinked) |
| `src/Twig.Mcp/Program.cs` | Add `.WithTools<NavigationTools>()` and `.WithTools<CreationTools>()` registrations |

---

## ADO Work Item Structure

Epic #1814 — "Expand twig-mcp with read and write tools"

### Issue 1: Navigation Tools — Read-only MCP tools

**Goal:** Implement 5 read-only tools (twig_show, twig_query, twig_children, twig_parent,
twig_sprint) that allow agents to inspect work items, search, and traverse hierarchy
without side effects.

**Prerequisites:** None (can start immediately)

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| 1.1 | Create `NavigationToolsTestBase.cs` extending `ReadToolsTestBase` with `CreateNavigationSut()` factory | `tests/Twig.Mcp.Tests/Tools/NavigationToolsTestBase.cs` | ~30 LoC |
| 1.2 | Implement `twig_show` in `NavigationTools.cs` + `FormatWorkItem` in `McpResultBuilder.cs` + `NavigationToolsShowTests.cs` (cache hit, cache miss/ADO fetch, ADO fail, workspace error) | `src/Twig.Mcp/Tools/NavigationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` | ~250 LoC |
| 1.3 | Implement `twig_query` in `NavigationTools.cs` + `FormatQueryResults` in `McpResultBuilder.cs` + `NavigationToolsQueryTests.cs` (happy path, no filters error, truncation, empty results, cache write) | `src/Twig.Mcp/Tools/NavigationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `tests/Twig.Mcp.Tests/Tools/NavigationToolsQueryTests.cs` | ~300 LoC |
| 1.4 | Implement `twig_children` + `twig_parent` in `NavigationTools.cs` + `FormatChildren`, `FormatParent` in `McpResultBuilder.cs` + `NavigationToolsChildrenTests.cs` + `NavigationToolsParentTests.cs` | `src/Twig.Mcp/Tools/NavigationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs`, `tests/Twig.Mcp.Tests/Tools/NavigationToolsParentTests.cs` | ~350 LoC |
| 1.5 | Implement `twig_sprint` in `NavigationTools.cs` + `FormatSprint` in `McpResultBuilder.cs` + `NavigationToolsSprintTests.cs` (iteration path, with items, without items) | `src/Twig.Mcp/Tools/NavigationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `tests/Twig.Mcp.Tests/Tools/NavigationToolsSprintTests.cs` | ~200 LoC |

**Acceptance Criteria:**
- [ ] All 5 read tools return correct JSON and pass unit tests
- [ ] `twig_show` does NOT modify active context
- [ ] `twig_query` builds correct WIQL and handles truncation
- [ ] `twig_children`/`twig_parent` handle missing items gracefully
- [ ] `twig_sprint` returns current iteration path
- [ ] All tools handle workspace resolution errors consistently
- [ ] Zero compiler warnings

### Issue 2: Creation Tools — Write MCP tools

**Goal:** Implement 2 write tools (twig_new, twig_link) that allow agents to create
work items and establish relationships between them.

**Prerequisites:** None (independent of Issue 1; can be developed in parallel)

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| 2.1 | Create `CreationToolsTestBase.cs` extending `MutationToolsTestBase` with `CreateCreationSut()` factory | `tests/Twig.Mcp.Tests/Tools/CreationToolsTestBase.cs` | ~30 LoC |
| 2.2 | Implement `twig_new` in `CreationTools.cs` + `FormatCreated` in `McpResultBuilder.cs` + `CreationToolsNewTests.cs` (happy path with parent, without parent, type validation, invalid type, missing title, ADO failure, description markdown conversion) | `src/Twig.Mcp/Tools/CreationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `tests/Twig.Mcp.Tests/Tools/CreationToolsNewTests.cs` | ~400 LoC |
| 2.3 | Implement `twig_link` in `CreationTools.cs` + `FormatLinked` in `McpResultBuilder.cs` + `CreationToolsLinkTests.cs` (happy path, invalid link type, ADO failure, supported types listing) | `src/Twig.Mcp/Tools/CreationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs`, `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkTests.cs` | ~200 LoC |

**Acceptance Criteria:**
- [ ] `twig_new` creates a work item in ADO and returns the assigned ID
- [ ] `twig_new` validates type against process configuration when parent is specified
- [ ] `twig_new` supports optional description with markdown→HTML conversion
- [ ] `twig_link` creates relationships using friendly link type names
- [ ] `twig_link` returns helpful error with supported types on invalid input
- [ ] Both tools handle ADO API failures gracefully
- [ ] Zero compiler warnings

### Issue 3: Program.cs Registration & Integration Verification

**Goal:** Register new tool classes in the MCP server builder and verify end-to-end
integration with the tool discovery pipeline.

**Prerequisites:** Issues 1 and 2 (tool classes must exist)

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---|---|---|---|
| 3.1 | Add `.WithTools<NavigationTools>()` and `.WithTools<CreationTools>()` to `Program.cs` | `src/Twig.Mcp/Program.cs` | ~5 LoC |
| 3.2 | Verify build succeeds with `dotnet build src/Twig.Mcp/` and all tests pass with `dotnet test tests/Twig.Mcp.Tests/` | N/A (verification) | ~10 min |

**Acceptance Criteria:**
- [ ] `dotnet build src/Twig.Mcp/` succeeds with zero warnings
- [ ] `dotnet test tests/Twig.Mcp.Tests/` passes all tests
- [ ] New tools are discoverable by MCP clients via tool listing

---

## PR Groups

### PG-1: Navigation Tools (read-only tools + tests)

**Type:** Deep
**Scope:** NavigationTools class, McpResultBuilder extensions (5 format methods), test base, 5 test files
**Estimated LoC:** ~1,100
**Estimated Files:** ~9

**Contents:**
- Task 1.1: NavigationToolsTestBase.cs
- Task 1.2: twig_show implementation + tests
- Task 1.3: twig_query implementation + tests
- Task 1.4: twig_children + twig_parent implementation + tests
- Task 1.5: twig_sprint implementation + tests

**Successors:** PG-3

### PG-2: Creation Tools (write tools + tests)

**Type:** Deep
**Scope:** CreationTools class, McpResultBuilder extensions (2 format methods), test base, 2 test files
**Estimated LoC:** ~650
**Estimated Files:** ~6

**Contents:**
- Task 2.1: CreationToolsTestBase.cs
- Task 2.2: twig_new implementation + tests
- Task 2.3: twig_link implementation + tests

**Successors:** PG-3

### PG-3: Registration & Integration

**Type:** Wide (trivial)
**Scope:** Program.cs registration (2 lines)
**Estimated LoC:** ~5
**Estimated Files:** 1

**Contents:**
- Task 3.1: Program.cs WithTools registration
- Task 3.2: Build + test verification

**Predecessors:** PG-1, PG-2

**Note:** PG-1 and PG-2 are independent and can be developed and reviewed in parallel.
PG-3 is a trivial integration commit that merges after both are complete. In practice,
PG-3 could be folded into whichever of PG-1/PG-2 lands second, reducing total PR count
to 2.
