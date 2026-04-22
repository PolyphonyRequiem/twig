# Read-Only Lookup Tools: twig_show, twig_children, twig_parent

| Field | Value |
|---|---|
| **Issue** | #1813 — Read-only lookup tools: twig_show, twig_children, twig_parent |
| **Parent Epic** | #1812 — Expand twig-mcp with 7 new tools |
| **Status** | ✅ Done |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan covers the three read-only MCP navigation tools — `twig_show`, `twig_children`,
and `twig_parent` — that allow Copilot agents to look up work item details, enumerate
direct children, and traverse the parent relationship without mutating active context.
**All three tools are already fully implemented** in `NavigationTools.cs`, registered via
`WithTools<NavigationTools>()` in `Program.cs`, and backed by 18+ unit tests that all
pass. The `McpResultBuilder` format methods (`FormatWorkItem`, `FormatChildren`,
`FormatParent`) are also complete with dedicated tests. The remaining work is limited to
verification and formal acceptance sign-off.

---

## Background

### Current MCP Architecture

The `twig-mcp` server exposes **17 tools** across **6 tool classes**:

| Class | Tools | Purpose |
|---|---|---|
| `ContextTools` | `twig_set`, `twig_status` | Context management |
| `ReadTools` | `twig_tree`, `twig_workspace` | Read-only queries (context-dependent) |
| `NavigationTools` | `twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint` | Read-only navigation (ID-based) |
| `MutationTools` | `twig_state`, `twig_update`, `twig_note`, `twig_discard`, `twig_sync` | Write operations |
| `CreationTools` | `twig_new`, `twig_find_or_create`, `twig_link` | Creation and linking |
| `WorkspaceTools` | `twig_list_workspaces` | Workspace discovery |

All tools follow a consistent pattern:

1. **`[McpServerToolType]` sealed class** with primary constructor injecting `WorkspaceResolver`
2. **`[McpServerTool]` methods** with `[Description]` annotations on parameters, returning `Task<CallToolResult>`
3. **Workspace resolution** via `resolver.TryResolve(workspace, out var ctx, out var err)` with one-line guard
4. **Data retrieval** via `WorkspaceContext` service properties (repos, ADO client, etc.)
5. **JSON formatting** via `McpResultBuilder` static methods using `Utf8JsonWriter` (AOT-safe)
6. **Registration** via `WithTools<T>()` in `Program.cs` (no reflection)

### Prior Art — Parent Plan

The parent Epic #1812 was initially planned in `docs/projects/expand-mcp-tools.plan.md`
(Epic #1814, now #1812) which defined all 7 new tools across 3 Issues:
- Issue #1817 → NavigationTools (twig_show, twig_query, twig_children, twig_parent, twig_sprint)
- Issue #1818 → CreationTools (twig_new, twig_link)
- Issue #1819 → Program.cs registration

Issue #1813 extracts the three ID-based lookup tools from the broader navigation set.

### Call-Site Audit

The three tools in scope are self-contained MCP endpoint methods with no cross-cutting
callers. The services they consume are injected via `WorkspaceContext`:

| Service Call | Used By | File | Impact |
|---|---|---|---|
| `ctx.FetchWithFallbackAsync(id, ct)` | `Show`, `Parent` | `WorkspaceContext.cs` | None — read-only |
| `ctx.WorkItemRepo.GetChildrenAsync(id, ct)` | `Children` | `IWorkItemRepository` | None — read-only |
| `ctx.WorkItemRepo.GetByIdAsync(id, ct)` | `FetchWithFallbackAsync` (internal) | `IWorkItemRepository` | None — read-only |
| `ctx.AdoService.FetchAsync(id, ct)` | `FetchWithFallbackAsync` (fallback) | `IAdoWorkItemService` | None — read-only, best-effort cache warm |
| `ctx.WorkItemRepo.SaveAsync(item, ct)` | `FetchWithFallbackAsync` (cache warm) | `IWorkItemRepository` | Write — best-effort only |
| `McpResultBuilder.FormatWorkItem(item, workspace)` | `Show` | `McpResultBuilder.cs` | None — pure function |
| `McpResultBuilder.FormatChildren(id, children, workspace)` | `Children` | `McpResultBuilder.cs` | None — pure function |
| `McpResultBuilder.FormatParent(child, parent, workspace)` | `Parent` | `McpResultBuilder.cs` | None — pure function |

No modifications to shared services, base classes, or interfaces are needed.

---

## Problem Statement

Copilot agents using the MCP server need to look up work item details, navigate child
hierarchies, and traverse parent relationships **without** changing the active context.
Prior to these tools, agents had to use `twig_set` (which modifies active context) just
to inspect an item, or fall back to CLI commands via shell execution, breaking the
structured JSON interaction model.

The three tools address this gap:
1. **`twig_show(id)`** — Reads a work item by ID without touching context
2. **`twig_children(id)`** — Lists direct children of any work item
3. **`twig_parent(id)`** — Returns the immediate parent of any work item

---

## Goals and Non-Goals

### Goals

1. **Read-only item lookup**: `twig_show` returns full work item details (core fields, paths, extra fields) without modifying active context
2. **Child enumeration**: `twig_children` returns direct children as a structured array with count
3. **Parent traversal**: `twig_parent` returns the immediate parent (or null for root items) with cache-first, ADO-fallback resolution
4. **Consistent patterns**: All tools follow the established `[McpServerToolType]` / `WorkspaceResolver` / `McpResultBuilder` pattern
5. **Full test coverage**: Each tool has ≥3 test cases covering success, error, and edge cases
6. **AOT safety**: All code is AOT-compatible (no reflection, `Utf8JsonWriter` only)
7. **Zero regression**: No changes to existing tools or behavior

### Non-Goals

1. **Batch show**: Single ID per call (batch lookup is a separate concern)
2. **Context modification**: These tools MUST NOT modify the active work item context
3. **Full parent chain**: `twig_parent` returns the immediate parent only, not the full root→parent chain (use `twig_tree` for full hierarchy)
4. **Enriched show**: `twig_show` returns the raw work item without pre-computing children count or links (agents compose with `twig_children` separately)
5. **New shared infrastructure**: No new base classes, interfaces, or cross-cutting services

---

## Requirements

### Functional Requirements

| ID | Requirement | Status |
|---|---|---|
| FR-1 | `twig_show(id)` returns work item with core fields (id, title, type, state, assignedTo, isDirty, isSeed, parentId), paths (areaPath, iterationPath), and extra fields | ✅ Implemented |
| FR-2 | `twig_show` does NOT change active context (no call to `SetActiveWorkItemIdAsync`) | ✅ Implemented + tested |
| FR-3 | `twig_show` uses cache-first lookup with ADO fallback via `FetchWithFallbackAsync` | ✅ Implemented |
| FR-4 | `twig_show` warms cache on ADO fallback (best-effort, non-fatal) | ✅ Implemented |
| FR-5 | `twig_children(id)` returns `{ parentId, children[], count, workspace }` from cache | ✅ Implemented |
| FR-6 | `twig_parent(id)` returns `{ child: {...}, parent: {...}, workspace }` with null parent for root items | ✅ Implemented |
| FR-7 | `twig_parent` resolves child via `FetchWithFallbackAsync` (cache-first, ADO fallback) | ✅ Implemented |
| FR-8 | `twig_parent` resolves parent via `FetchWithFallbackAsync` (best-effort — null if fetch fails) | ✅ Implemented |
| FR-9 | All three tools support optional `workspace` parameter for multi-workspace routing | ✅ Implemented |
| FR-10 | All three tools return structured JSON via `McpResultBuilder` format methods | ✅ Implemented |
| FR-11 | Workspace resolution errors return `McpResultBuilder.ToError()` | ✅ Implemented |
| FR-12 | `OperationCanceledException` propagates (not caught) | ✅ Implemented + tested |

### Non-Functional Requirements

| ID | Requirement | Status |
|---|---|---|
| NFR-1 | AOT-compatible: no reflection, `Utf8JsonWriter` only | ✅ Verified |
| NFR-2 | `TreatWarningsAsErrors=true` — zero warnings | ✅ Verified (369 tests pass) |
| NFR-3 | Test coverage: each tool has ≥3 test cases | ✅ Show: 7, Children: 3, Parent: 8 |
| NFR-4 | Registration in `Program.cs` via `WithTools<NavigationTools>()` | ✅ Verified |

---

## Proposed Design

### Architecture Overview

The three tools are methods on the existing `NavigationTools` sealed class (which also
contains `twig_query` and `twig_sprint`). The class uses primary constructor injection
of `WorkspaceResolver`:

```
Program.cs
  .WithTools<NavigationTools>()     ← AOT-safe registration
      ↓
NavigationTools(WorkspaceResolver resolver)
  ├── Show(id, workspace?)         → McpResultBuilder.FormatWorkItem()
  ├── Children(id, workspace?)     → McpResultBuilder.FormatChildren()
  └── Parent(id, workspace?)       → McpResultBuilder.FormatParent()
```

### Key Components

#### 1. NavigationTools.Show (twig_show)

```csharp
[McpServerTool(Name = "twig_show")]
public async Task<CallToolResult> Show(int id, string? workspace = null, CancellationToken ct = default)
```

**Flow:**
1. Resolve workspace via `resolver.TryResolve(workspace, ...)`
2. Fetch item via `ctx.FetchWithFallbackAsync(id, ct)` — cache-first, ADO fallback
3. On cache miss + ADO success: best-effort save to cache
4. On cache miss + ADO failure: return error with `#id not found` message
5. Format via `McpResultBuilder.FormatWorkItem(item, workspace)` — includes all core fields, area/iteration paths, and extra fields dictionary

**Response shape:**
```json
{
  "id": 42, "title": "...", "type": "Task", "state": "Doing",
  "assignedTo": "...", "isDirty": false, "isSeed": false,
  "parentId": 5, "areaPath": "...", "iterationPath": "...",
  "fields": { "System.Description": "..." },
  "workspace": "org/project"
}
```

#### 2. NavigationTools.Children (twig_children)

```csharp
[McpServerTool(Name = "twig_children")]
public async Task<CallToolResult> Children(int id, string? workspace = null, CancellationToken ct = default)
```

**Flow:**
1. Resolve workspace
2. Fetch children via `ctx.WorkItemRepo.GetChildrenAsync(id, ct)` — cache-only
3. Format via `McpResultBuilder.FormatChildren(id, children, workspace)`

**Response shape:**
```json
{
  "parentId": 42,
  "children": [{ "id": 11, "title": "...", "type": "Task", ... }, ...],
  "count": 2,
  "workspace": "org/project"
}
```

#### 3. NavigationTools.Parent (twig_parent)

```csharp
[McpServerTool(Name = "twig_parent")]
public async Task<CallToolResult> Parent(int id, string? workspace = null, CancellationToken ct = default)
```

**Flow:**
1. Resolve workspace
2. Fetch child item via `ctx.FetchWithFallbackAsync(id, ct)` — cache-first, ADO fallback
3. If child has `ParentId`: fetch parent via `FetchWithFallbackAsync(parentId, ct)` — best-effort (null on failure)
4. If child has no `ParentId`: parent is null
5. Format via `McpResultBuilder.FormatParent(child, parent, workspace)`

**Response shape:**
```json
{
  "child": { "id": 10, "title": "...", "type": "Task", ... },
  "parent": { "id": 1, "title": "...", "type": "Epic", ..., "areaPath": "...", "iterationPath": "..." },
  "workspace": "org/project"
}
```

### Design Decisions

| Decision | Rationale |
|---|---|
| **Cache-first with ADO fallback** for `Show` and `Parent` | Consistent with `FetchWithFallbackAsync` pattern used across the MCP server. Avoids unnecessary ADO calls for cached items. |
| **Cache-only** for `Children` | Children are always loaded from the local SQLite cache. The cache is warmed by `twig_set` (which extends the working set). No fallback needed since `GetChildrenAsync` returns empty for unknown parents. |
| **Immediate parent only** (not full chain) for `Parent` | Keeps the tool simple and composable. Agents can call `twig_parent` iteratively or use `twig_tree` for the full hierarchy. |
| **Best-effort parent resolution** | If the parent fetch fails, `Parent` returns null rather than erroring. The child was found, so the tool succeeds with partial data. |
| **No context mutation** | These are read-only tools. `twig_set` is the only tool that modifies active context. Verified by tests. |
| **Shared NavigationTools class** with `twig_query` and `twig_sprint` | All 5 tools are ID-based read-only lookups (distinct from context-dependent `ReadTools`). Grouping by semantic cohesion. |

---

## Dependencies

### External Dependencies

- `ModelContextProtocol` NuGet package (already referenced) — `[McpServerToolType]`, `[McpServerTool]`, `CallToolResult`
- No new external packages required

### Internal Dependencies

- `Twig.Domain` — `WorkItem`, `IWorkItemRepository`, `IAdoWorkItemService`
- `Twig.Mcp.Services` — `WorkspaceResolver`, `WorkspaceContext`, `McpResultBuilder`
- `Twig.TestKit` — `WorkItemBuilder` (tests only)

### Sequencing Constraints

None — all three tools are already implemented and merged to `main`. The remaining work
is verification only.

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| (none) | All files already exist |

### Modified Files

| File Path | Changes |
|---|---|
| (none) | No modifications needed — implementation is complete |

### Existing Files (for reference)

| File Path | Role | Status |
|---|---|---|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Tool class with Show, Children, Parent methods (lines 18–120) | ✅ Complete |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | FormatWorkItem, FormatChildren, FormatParent methods | ✅ Complete |
| `src/Twig.Mcp/Program.cs` | `WithTools<NavigationTools>()` registration (line 59) | ✅ Complete |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsTestBase.cs` | Shared test base extending ReadToolsTestBase | ✅ Complete |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsShowTests.cs` | 7 tests for twig_show | ✅ Complete |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs` | 3 tests for twig_children | ✅ Complete |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsParentTests.cs` | 8 tests for twig_parent | ✅ Complete |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Tests for FormatWorkItem, FormatChildren, FormatParent | ✅ Complete |

---

## ADO Work Item Structure

### Issue #1813: Read-Only Lookup Tools — twig_show, twig_children, twig_parent

**Goal:** Verify that `twig_show`, `twig_children`, and `twig_parent` are fully
implemented, tested, registered, and meeting all acceptance criteria.

**Prerequisites:** None

**Tasks:**

| Task ID | Description | Files | Effort |
|---|---|---|---|
| T-1813.1 | Verify `twig_show` implementation: cache hit, ADO fallback, cache warm, no context mutation, response shape | `NavigationTools.cs`, `NavigationToolsShowTests.cs` | Verification only |
| T-1813.2 | Verify `twig_children` implementation: cache lookup, empty result, response shape with workspace | `NavigationTools.cs`, `NavigationToolsChildrenTests.cs` | Verification only |
| T-1813.3 | Verify `twig_parent` implementation: cache-first child, ADO fallback, root item null parent, best-effort parent fetch, response shape | `NavigationTools.cs`, `NavigationToolsParentTests.cs` | Verification only |
| T-1813.4 | Verify McpResultBuilder format methods: FormatWorkItem, FormatChildren, FormatParent test coverage | `McpResultBuilderTests.cs` | Verification only |
| T-1813.5 | Run full test suite and verify zero warnings, zero failures, AOT compatibility | All test projects | Verification only |

**Acceptance Criteria:**
- [x] `twig_show(id)` returns structured JSON with item details, paths, and extra fields
- [x] `twig_show` does NOT modify active context (verified by test: `Show_DoesNotModifyActiveContext`)
- [x] `twig_show` uses cache-first with ADO fallback (verified by tests: cache hit, cache miss → ADO, cache miss + ADO fail)
- [x] `twig_children(id)` returns direct children as array with count
- [x] `twig_children` handles empty children (returns `[]` and `count: 0`)
- [x] `twig_parent(id)` returns child + parent objects with workspace key
- [x] `twig_parent` handles root items (null parent)
- [x] `twig_parent` handles ADO fallback for child (cache miss)
- [x] `twig_parent` handles parent fetch failure gracefully (returns null parent)
- [x] All three tools support optional `workspace` parameter
- [x] All three tools handle workspace resolution errors
- [x] `OperationCanceledException` propagates in all tools
- [x] McpResultBuilder format methods have dedicated unit tests
- [x] All 369 MCP tests pass with `dotnet test`
- [x] Zero warnings with `TreatWarningsAsErrors=true`

---

## PR Groups

### PG-1: Read-Only Lookup Tools (Verification Only)

**Issues/Tasks:** Issue #1813 (T-1813.1 through T-1813.5)

**Classification:** N/A — No code changes required. Implementation was completed as part
of the parent epic's PG-1 (Navigation Tools) which has already merged to `main`.

**Files:** None modified

**Estimated size:** 0 LoC (verification only)

**Note:** The implementation was delivered in prior PRs under Epic #1812. This issue
exists for tracking and formal acceptance. No additional PR is needed.

---

## Test Coverage Summary

### twig_show (NavigationToolsShowTests.cs) — 7 tests

| Test | Scenario |
|---|---|
| `Show_CacheHit_ReturnsItemWithoutAdoCall` | Cache hit returns item, no ADO call |
| `Show_CacheMiss_FetchesFromAdoAndCaches` | ADO fallback fetches and warms cache |
| `Show_CacheMissAdoFails_ReturnsError` | Both cache miss and ADO failure → error |
| `Show_DoesNotModifyActiveContext` | No call to `SetActiveWorkItemIdAsync` |
| `Show_ResponseIncludesPathsAndWorkspace` | Response has areaPath, iterationPath, workspace |
| `Show_ItemWithParent_ResponseContainsParentId` | parentId field is non-null when item has parent |
| `Show_ItemWithFields_ResponseContainsFieldsObject` | Extra fields included in response |
| `Show_CancellationRequested_PropagatesException` | OperationCanceledException not swallowed |

### twig_children (NavigationToolsChildrenTests.cs) — 3 tests

| Test | Scenario |
|---|---|
| `Children_HappyPath_ReturnsChildrenArray` | Returns 2 children with correct structure |
| `Children_NoChildren_ReturnsEmptyArrayAndZeroCount` | Empty array, count = 0 |
| `Children_Response_IncludesWorkspaceKey` | Workspace key in response |

### twig_parent (NavigationToolsParentTests.cs) — 8 tests

| Test | Scenario |
|---|---|
| `Parent_BothInCache_ReturnsChildAndParent` | Both items cached |
| `Parent_RootItem_ReturnsNullParent` | Root item → null parent |
| `Parent_ChildCacheMiss_FetchesFromAdo` | ADO fallback for child |
| `Parent_ChildCacheMissAndAdoFails_ReturnsError` | Child not found → error |
| `Parent_ParentCacheMiss_FetchesParentFromAdo` | ADO fallback for parent |
| `Parent_ParentFetchFails_ReturnsNullParentBestEffort` | Parent fetch failure → null (not error) |
| `Parent_Response_IncludesWorkspaceKey` | Workspace key in response |
| `Parent_Cancelled_PropagatesException` | OperationCanceledException not swallowed |

### McpResultBuilder (McpResultBuilderTests.cs) — relevant tests

| Test | Scenario |
|---|---|
| `FormatWorkItem_ProducesFullWorkItemJson` | Full item serialization |
| `FormatWorkItem_WithFields_IncludesFieldsObject` | Extra fields included |
| `FormatWorkItem_NoFields_OmitsFieldsObject` | No fields → no fields object |
| `FormatWorkItem_NullParentId_WritesNull` | Null parentId handling |
| `FormatWorkItem_WithWorkspace_IncludesWorkspaceKey` | Workspace key |
| `FormatWorkItem_NullWorkspace_WritesNull` | Null workspace |
| `FormatWorkItem_NullFieldValue_WritesNullInFields` | Null field value |
| `FormatWorkItem_SeedItem_ReflectsIsSeed` | isSeed flag |
| `FormatChildren_ProducesExpectedStructure` | Children array structure |
| `FormatChildren_NoChildren_WritesEmptyArray` | Empty children |
| `FormatChildren_WithWorkspace_IncludesWorkspace` | Workspace key |
| `FormatParent_WithParent_ProducesExpectedStructure` | Parent structure |
| `FormatParent_NullParent_WritesNull` | Null parent |
| `FormatParent_WithWorkspace_IncludesWorkspace` | Workspace key |

---

## Open Questions

| # | Question | Severity | Notes |
|---|---|---|---|
| 1 | Should `twig_show` be enriched with children count and links in a follow-up? | Low | The parent plan (expand-mcp-tools.plan.md) originally envisioned enrichment, but the current implementation keeps `twig_show` lean. Agents compose `twig_show` + `twig_children` for the same effect. |

---

## References

- Parent plan: `docs/projects/expand-mcp-tools.plan.md` (Epic #1812)
- Implementation: `src/Twig.Mcp/Tools/NavigationTools.cs`
- Registration: `src/Twig.Mcp/Program.cs` (line 59)
- Test suite: `tests/Twig.Mcp.Tests/Tools/NavigationTools*.cs`
