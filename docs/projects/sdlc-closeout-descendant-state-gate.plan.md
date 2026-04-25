---
work_item_id: 2070
type: Issue
title: "SDLC close_out: drill into Issues to verify Task states before Epic closure"
status: Draft
revision: 0
---

# SDLC close_out: Drill Into Issues to Verify Task States Before Epic Closure

| Field | Value |
|-------|-------|
| **Work Item** | [#2070](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/2070) |
| **Type** | Issue |
| **Status** | Draft |

## Executive Summary

The SDLC close_out agent (Phase 5) can transition an Epic to "Done" while Tasks underneath child Issues remain incomplete, because `twig_tree` and `twig_children` only return one level of children. This plan introduces a `DescendantVerificationService` domain service that recursively walks the full work item hierarchy, classifies each descendant's state via the process-agnostic `StateCategoryResolver`, and returns a structured pass/fail verdict. A new `twig_verify_descendants` MCP tool exposes this service as a single deterministic call the close_out agent invokes at Step 1c instead of manually iterating with `twig_children`. The result is a fail-safe gate that prevents premature Epic closure when any descendant — at any depth — remains incomplete.

## Background

### Current Architecture

The SDLC lifecycle is orchestrated by the Conductor apex workflow `twig-sdlc-full.yaml` (external `twig-conductor-workflows` registry):

```
state_detector → planning → implementation → close_out (agent) → retrospective → $end
```

The **close_out agent** (`claude-opus-4.6-1m`) follows an 11-step procedure defined in `close-out.prompt.md`. Step 1c ("Verify all child items Done") uses `twig tree --output json` to inspect the Epic's children. However, `twig tree` returns a display-oriented tree with only **direct children** of the focused item — for an Epic, this means Issues are visible but their child Tasks are not.

### How close_out currently verifies child states (Step 1c)

```
Step 1c: Verify all child items are Done
  - twig set <id>
  - twig tree --output json — inspect all children
  - If ANY child Issue or Task is NOT in state "Done": block/rollback
```

The prompt says "Issue or Task" but `twig tree` only returns **one level** of children:

| Tool | Returns | Depth |
|------|---------|-------|
| `twig_tree` | Focus item + direct children + parent chain | 1 level of children |
| `twig_children <id>` | Direct children of given work item | 1 level |
| `twig_batch` | Parallel/sequential multi-tool execution | N/A |

### Existing child-state verification pattern

The CLI already has a proven **one-level** child-state verification in `FlowCloseCommand.cs` (lines 119–164):

```csharp
var children = await adoService.FetchChildrenAsync(targetId, ct);
var processConfig = processConfigProvider.GetConfiguration();
var incomplete = children.Where(child =>
    !processConfig.TypeConfigs.TryGetValue(child.Type, out var cfg) ||
    StateCategoryResolver.Resolve(child.State, cfg.StateEntries)
        is not (StateCategory.Completed or StateCategory.Resolved or StateCategory.Removed))
    .ToList();
```

Key design decisions from this pattern:
- **Terminal states**: `Completed ∪ Resolved ∪ Removed` (process-agnostic via `StateCategory`)
- **Unmapped types → non-terminal** (conservative: if we don't recognize the type, treat it as incomplete)
- **ADO-authoritative with cache fallback**: tries ADO REST first, falls back to local SQLite cache

### Call-Site Audit

The new tool is **additive** — no existing contracts are modified. Related components:

| File | Component | Current Usage | Impact |
|------|-----------|---------------|--------|
| `FlowCloseCommand.cs` | Child-state gate (L119–164) | 1-level check before CLI `flow-close` | Future refactor candidate — could reuse `DescendantVerificationService` |
| `StateCategoryResolver.cs` | `Resolve(state, entries)` | Maps state → `StateCategory` | **Reused** by new service (no changes) |
| `ProcessConfigExtensions.cs` | `ComputeChildProgress()` | Counts Resolved/Completed children | Conceptually related, different scope |
| `NavigationTools.cs` | `twig_children` (L91) | Lists direct children from cache | New tool uses same `GetChildrenAsync` recursively |
| `MutationTools.cs` | `twig_state` (L22) | Raw state change, no child verification | SDLC agent calls this to close; new tool gates *before* this call |
| `WorkspaceContext.cs` | `FetchWithFallbackAsync` | Cache-first, ADO-fallback fetch | Reused by new tool for reliable item retrieval |
| `ToolDispatcher.cs` | Batch tool routing | Routes tool names → methods | **Must register** new `twig_verify_descendants` route |
| `McpResultBuilder.cs` | JSON formatting | Builds structured JSON for all tools | **Must add** `FormatVerificationResult` method |

### MCP Tool Architecture

MCP tools are organized by concern in `src/Twig.Mcp/Tools/`:

| Class | Category | Tools |
|-------|----------|-------|
| `NavigationTools` | Read-only navigation | `twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint` |
| `ReadTools` | Display-oriented reads | `twig_tree`, `twig_workspace` |
| `MutationTools` | State changes | `twig_state`, `twig_update`, `twig_note`, `twig_discard`, `twig_sync` |
| `ContextTools` | Context management | `twig_set`, `twig_status` |
| `CreationTools` | Work item creation | `twig_new`, `twig_find_or_create`, `twig_link`, `twig_link_artifact` |
| `BatchTools` | Batch operations | `twig_batch` |

The new `twig_verify_descendants` tool fits best in **NavigationTools** — it is read-only, takes an explicit ID, and does not modify context.

## Problem Statement

When the SDLC close_out agent runs Step 1c, it calls `twig tree` to verify all children of the Epic are in "Done" state. This check only sees **direct children** (Issues) — not their grandchildren (Tasks). The result:

1. **Premature closure**: An Epic is transitioned to "Done" while Tasks under its Issues are still "Active", "To Do", or "In Progress".
2. **Silent data loss**: The incomplete Tasks become orphaned — they belong to a "Done" parent hierarchy, making them invisible in standard board views.
3. **Unreliable automation**: The close_out agent believes it performed due diligence, but the verification was structurally incomplete.

The same gap exists in `FlowCloseCommand.cs` (the CLI `twig flow-close`), though that is a lower priority since CLI users have interactive visibility.

## Goals and Non-Goals

### Goals

1. **G-1**: Provide a single MCP tool call (`twig_verify_descendants`) that recursively verifies all descendants of a work item are in terminal state
2. **G-2**: Return structured JSON with a pass/fail verdict, total/incomplete counts, and a list of incomplete items with their depth, type, state, and parent
3. **G-3**: Ensure the verification is process-agnostic — works with Agile, Scrum, Basic, CMMI, and custom process templates
4. **G-4**: Bound recursion depth to prevent runaway traversal (configurable, default 5 levels)
5. **G-5**: Cache-first traversal with ADO fallback for items not in the local cache

### Non-Goals

- **NG-1**: Modifying the close_out agent prompt or workflow YAML (lives in external `twig-conductor-workflows` repo — will be documented as a follow-up)
- **NG-2**: Refactoring `FlowCloseCommand.cs` to use the new service (future work — the CLI command works as-is for its 1-level scope)
- **NG-3**: Adding automatic remediation (e.g., auto-transitioning incomplete Tasks) — this tool is read-only verification
- **NG-4**: Recursive ADO REST calls for deeply nested hierarchies — the tool uses the local cache with single-item ADO fallback, not recursive ADO children fetches

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| **FR-1** | `twig_verify_descendants` accepts a `workItemId` (int, required) and optional `maxDepth` (int, default 5) |
| **FR-2** | The tool recursively fetches children from the local cache via `IWorkItemRepository.GetChildrenAsync` |
| **FR-3** | For each descendant, the tool resolves its `StateCategory` using `StateCategoryResolver.Resolve` with the authoritative `StateEntry[]` from `ProcessConfiguration` |
| **FR-4** | Terminal states are `Completed ∪ Resolved ∪ Removed`; all others are "incomplete" |
| **FR-5** | Items with unmapped types (not in `ProcessConfiguration.TypeConfigs`) are treated as non-terminal (conservative) |
| **FR-6** | The result includes: `passed` (bool), `totalDescendants` (int), `incompleteCount` (int), `maxDepthReached` (bool), and `incompleteItems` array |
| **FR-7** | Each incomplete item in the array includes: `id`, `title`, `type`, `state`, `stateCategory`, `parentId`, `depth` |
| **FR-8** | Recursion stops at `maxDepth` levels. If items exist at the boundary, `maxDepthReached` is set to true |
| **FR-9** | The tool is accessible via `twig_batch` (registered in `ToolDispatcher`) |
| **FR-10** | The root item itself is NOT included in the descendant check — only its children and their children |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| **NFR-1** | AOT-compatible: no reflection, all JSON via `Utf8JsonWriter` |
| **NFR-2** | Cache-first: fetches from SQLite, falls back to ADO for individual items not in cache |
| **NFR-3** | Bounded: max 500 descendants per call to prevent memory pressure |
| **NFR-4** | Process-agnostic: uses `ProcessConfiguration` dynamically, never hardcodes state names or type names |

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│  MCP Server                                             │
│  ┌────────────────────┐   ┌──────────────────────────┐  │
│  │ NavigationTools     │   │ ToolDispatcher (batch)   │  │
│  │  twig_verify_       │   │  "twig_verify_descendants"│  │
│  │  descendants()      │◄──│  → navigationTools       │  │
│  └─────────┬──────────┘   │    .VerifyDescendants()   │  │
│            │               └──────────────────────────┘  │
│            ▼                                             │
│  ┌────────────────────────────────────────────┐          │
│  │ WorkspaceContext                            │          │
│  │  .ProcessConfigProvider                     │          │
│  │  .WorkItemRepo                              │          │
│  │  .FetchWithFallbackAsync()                  │          │
│  └─────────┬──────────────────────────────────┘          │
│            │                                             │
└────────────┼─────────────────────────────────────────────┘
             ▼
┌─────────────────────────────────────────────────────────┐
│  Domain Layer                                           │
│  ┌─────────────────────────────────────────────────────┐│
│  │ DescendantVerificationService (static)              ││
│  │                                                     ││
│  │  VerifyAsync(                                       ││
│  │    rootId: int,                                     ││
│  │    getChildren: Func<int, CancellationToken,        ││
│  │      Task<IReadOnlyList<WorkItem>>>,                ││
│  │    processConfig: ProcessConfiguration,             ││
│  │    maxDepth: int = 5,                               ││
│  │    maxDescendants: int = 500,                       ││
│  │    ct: CancellationToken)                           ││
│  │  → DescendantVerificationResult                     ││
│  └─────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────┐│
│  │ DescendantVerificationResult (sealed record)        ││
│  │  Passed, TotalDescendants, IncompleteCount,         ││
│  │  MaxDepthReached, TruncatedByLimit,                 ││
│  │  IncompleteItems: IReadOnlyList<IncompleteItem>     ││
│  └─────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────┐│
│  │ IncompleteItem (sealed record)                      ││
│  │  Id, Title, Type, State, StateCategory, ParentId,   ││
│  │  Depth                                              ││
│  └─────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `DescendantVerificationService` (Domain Service)

**Location**: `src/Twig.Domain/Services/DescendantVerificationService.cs`

A **static** service class (no DI dependencies — all injected via parameters) that performs BFS traversal of the work item hierarchy:

```csharp
public static class DescendantVerificationService
{
    public static async Task<DescendantVerificationResult> VerifyAsync(
        int rootId,
        Func<int, CancellationToken, Task<IReadOnlyList<WorkItem>>> getChildren,
        ProcessConfiguration processConfig,
        int maxDepth = 5,
        int maxDescendants = 500,
        CancellationToken ct = default)
}
```

**Why static with `Func` injection**: Following the pattern of `StateCategoryResolver` (static, pure-function style), this service has no state. The `getChildren` delegate allows the MCP layer to inject cache-first-ADO-fallback behavior without the domain service depending on infrastructure interfaces.

**Algorithm**:
1. BFS starting from `rootId` children (depth 1)
2. For each item at each level, resolve `StateCategory` via `StateCategoryResolver.Resolve`
3. Non-terminal items are added to `incompleteItems` list
4. Continue to next depth level by enqueuing children of current level
5. Stop when: max depth reached, max descendants reached, or no more children
6. Return `DescendantVerificationResult`

#### 2. `DescendantVerificationResult` (Read Model)

**Location**: `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs`

```csharp
public sealed record DescendantVerificationResult(
    bool Passed,
    int TotalDescendants,
    int IncompleteCount,
    bool MaxDepthReached,
    bool TruncatedByLimit,
    IReadOnlyList<IncompleteItem> IncompleteItems);

public sealed record IncompleteItem(
    int Id,
    string Title,
    string Type,
    string State,
    string StateCategory,
    int? ParentId,
    int Depth);
```

#### 3. `twig_verify_descendants` MCP Tool

**Location**: Added to `src/Twig.Mcp/Tools/NavigationTools.cs`

```csharp
[McpServerTool(Name = "twig_verify_descendants"),
 Description("Recursively verify all descendants of a work item are in terminal state")]
public async Task<CallToolResult> VerifyDescendants(
    [Description("Work item ID to verify")] int id,
    [Description("Max recursion depth (default: 5)")] int maxDepth = 5,
    [Description("Target workspace")] string? workspace = null,
    CancellationToken ct = default)
```

The tool:
1. Resolves workspace context
2. Fetches root item via `FetchWithFallbackAsync` (validates it exists)
3. Gets `ProcessConfiguration` from context
4. Calls `DescendantVerificationService.VerifyAsync` with a `getChildren` delegate that wraps `WorkItemRepo.GetChildrenAsync`
5. Formats result via `McpResultBuilder.FormatVerificationResult`

#### 4. `McpResultBuilder.FormatVerificationResult`

Produces structured JSON:

```json
{
  "rootId": 100,
  "rootTitle": "Epic: Ship v2.0",
  "passed": false,
  "totalDescendants": 12,
  "incompleteCount": 3,
  "maxDepthReached": false,
  "truncatedByLimit": false,
  "incompleteItems": [
    {
      "id": 205,
      "title": "Write unit tests",
      "type": "Task",
      "state": "Active",
      "stateCategory": "InProgress",
      "parentId": 103,
      "depth": 2
    }
  ],
  "workspace": "org/project"
}
```

### Data Flow

```
Agent calls twig_verify_descendants(id=100, maxDepth=5)
  │
  ├─ NavigationTools.VerifyDescendants()
  │   ├─ WorkspaceResolver.TryResolve(workspace) → ctx
  │   ├─ ctx.FetchWithFallbackAsync(100) → rootItem
  │   ├─ ctx.ProcessConfigProvider.GetConfiguration() → config
  │   └─ DescendantVerificationService.VerifyAsync(
  │       rootId=100,
  │       getChildren=ctx.WorkItemRepo.GetChildrenAsync,
  │       processConfig=config,
  │       maxDepth=5)
  │       │
  │       ├─ BFS Level 1: GetChildrenAsync(100) → [Issue#101, Issue#102]
  │       │   ├─ Issue#101: state="Done" → Completed ✓
  │       │   └─ Issue#102: state="Doing" → InProgress ✗ → add to incompleteItems
  │       │
  │       ├─ BFS Level 2: GetChildrenAsync(101) → [Task#201, Task#202]
  │       │   │            GetChildrenAsync(102) → [Task#203]
  │       │   ├─ Task#201: state="Done" → Completed ✓
  │       │   ├─ Task#202: state="Active" → InProgress ✗ → add to incompleteItems
  │       │   └─ Task#203: state="To Do" → Proposed ✗ → add to incompleteItems
  │       │
  │       └─ Return DescendantVerificationResult(
  │           Passed=false, TotalDescendants=5,
  │           IncompleteCount=3, IncompleteItems=[...])
  │
  └─ McpResultBuilder.FormatVerificationResult(rootItem, result)
      → CallToolResult with JSON
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Static service with `Func` delegate** | Domain layer stays free of infrastructure interfaces. MCP layer injects the actual data source. Follows `StateCategoryResolver` pattern. |
| **BFS over DFS** | BFS reports items in depth order (Issues before Tasks), which is more natural for the agent. Also makes depth limiting straightforward. |
| **Cache-first, no recursive ADO** | ADO `FetchChildrenAsync` is expensive (1 REST call per level). The cache is populated during `twig sync`. Missing items get individual ADO fallback via `FetchWithFallbackAsync` at the tool level, not during traversal. |
| **Max 500 descendants** | Prevents memory pressure for degenerate hierarchies. Typical Epics have 3–20 descendants. |
| **Root item excluded** | The caller already knows the root's state. Verification is about descendants only. |
| **`maxDepthReached` flag** | Allows the agent to know verification was incomplete and decide to increase depth or investigate manually. |
| **Tool in `NavigationTools`** | Read-only, takes explicit ID, no context mutation — fits the Navigation category. |

## Dependencies

### External Dependencies

- **ModelContextProtocol SDK**: Already in use — provides `[McpServerTool]`, `CallToolResult`, etc.
- No new NuGet packages required.

### Internal Dependencies

- `StateCategoryResolver` — reused as-is
- `ProcessConfiguration` — reused as-is
- `IWorkItemRepository.GetChildrenAsync` — reused as-is
- `WorkspaceContext.FetchWithFallbackAsync` — reused as-is
- `McpResultBuilder` — extended with new formatter method
- `ToolDispatcher` — extended with new route

### Sequencing Constraints

None — this is a purely additive feature. All dependencies already exist.

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should the tool also accept a `targetCategories` parameter to customize what counts as "terminal"? | Low | Current hardcoded `Completed ∪ Resolved ∪ Removed` matches `FlowCloseCommand` exactly. Can be added later if needed. |
| OQ-2 | Should the close_out agent prompt update be tracked as a separate ADO Task or follow-up Issue? | Low | The prompt lives in an external repo. Documenting as a follow-up is sufficient. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/DescendantVerificationService.cs` | Static domain service: BFS traversal with state verification |
| `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs` | Read model records: `DescendantVerificationResult` + `IncompleteItem` |
| `tests/Twig.Domain.Tests/Services/DescendantVerificationServiceTests.cs` | Unit tests for the domain service |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsVerifyDescendantsTests.cs` | Unit tests for the MCP tool |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Add `VerifyDescendants` method with `[McpServerTool]` attribute |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatVerificationResult` static method |
| `src/Twig.Mcp/Services/Batch/ToolDispatcher.cs` | Add `"twig_verify_descendants"` route in switch expression |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Add test for `FormatVerificationResult` formatting |

## ADO Work Item Structure

This is an Issue (#2070) — Tasks are defined directly under it.

### Issue #2070: SDLC close_out — drill into Issues to verify Task states before Epic closure

**Goal**: Provide a recursive descendant state verification MCP tool that the close_out agent can use to prevent premature Epic closure.

**Prerequisites**: None — purely additive.

**Tasks**:

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-1 | Create `DescendantVerificationResult` read model records | `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs` | ~30 LoC |
| T-2 | Implement `DescendantVerificationService` with BFS traversal and state classification | `src/Twig.Domain/Services/DescendantVerificationService.cs` | ~80 LoC |
| T-3 | Write domain-layer unit tests for `DescendantVerificationService` covering all edge cases | `tests/Twig.Domain.Tests/Services/DescendantVerificationServiceTests.cs` | ~250 LoC |
| T-4 | Add `FormatVerificationResult` to `McpResultBuilder` and add `twig_verify_descendants` to `NavigationTools` | `src/Twig.Mcp/Services/McpResultBuilder.cs`, `src/Twig.Mcp/Tools/NavigationTools.cs` | ~80 LoC |
| T-5 | Register `twig_verify_descendants` in `ToolDispatcher` for batch support | `src/Twig.Mcp/Services/Batch/ToolDispatcher.cs` | ~10 LoC |
| T-6 | Write MCP-layer unit tests for the tool and result builder | `tests/Twig.Mcp.Tests/Tools/NavigationToolsVerifyDescendantsTests.cs`, `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | ~200 LoC |

**Acceptance Criteria**:

- [ ] `twig_verify_descendants` returns `passed: true` when all descendants are in terminal state
- [ ] `twig_verify_descendants` returns `passed: false` with structured `incompleteItems` when any descendant is non-terminal
- [ ] Works with all standard process templates (Agile, Scrum, Basic, CMMI)
- [ ] Respects `maxDepth` parameter and reports `maxDepthReached` accurately
- [ ] Items with unmapped types are treated as non-terminal (conservative)
- [ ] Bounded by `maxDescendants` (500) with `truncatedByLimit` flag
- [ ] Registered in `ToolDispatcher` for batch operations
- [ ] All tests pass; build succeeds with no warnings

## PR Groups

### PG-1: Descendant verification — domain + MCP + tests

**Classification**: Deep (few files, complex recursive logic)

**Tasks included**: T-1, T-2, T-3, T-4, T-5, T-6

**Rationale**: All six tasks form a single cohesive feature. The domain service, MCP tool, result builder, batch registration, and tests are tightly coupled — splitting them would create a PR that compiles but has no consumer, and a second PR that adds a consumer for a not-yet-reviewed service. A single PR (~650 LoC, 8 files) is well within the ≤2000 LoC / ≤50 files guardrails and is more reviewable as a unit.

**Estimated LoC**: ~650
**Estimated files**: 8 (4 new, 4 modified)
**Successors**: None — single PR group.

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | `PG-1-descendant-verification` | #2070 / T-1, T-2, T-3, T-4, T-5, T-6 | None | Deep |

### Execution Order

**PG-1** is the only group and ships everything. The internal task order within the PR is:

1. **T-1** — Domain records (`DescendantVerificationResult`, `IncompleteItem`) — foundation for all other tasks
2. **T-2** — `DescendantVerificationService` BFS implementation — depends on T-1 types
3. **T-3** — Domain unit tests — depends on T-1 + T-2
4. **T-4** — `McpResultBuilder.FormatVerificationResult` + `NavigationTools.VerifyDescendants` — depends on T-1 + T-2
5. **T-5** — `ToolDispatcher` batch registration — depends on T-4 (method must exist)
6. **T-6** — MCP unit tests — depends on T-4 + T-5

All six tasks form one cohesive feature: splitting would produce a PR that compiles but has no consumer, and a follow-on PR adding a consumer for an unreviewed service. A single PR (~650 LoC, 8 files) is well within the ≤2,000 LoC / ≤50 files guardrails.

### Validation Strategy

**PG-1 — `PG-1-descendant-verification`**

| Gate | Command | Expected |
|------|---------|----------|
| Build | `dotnet build Twig.slnx` | No warnings, no errors (warnings-as-errors enabled) |
| Domain tests | `dotnet test tests/Twig.Domain.Tests` | All pass (T-3 covers BFS edge cases, depth limits, unmapped types) |
| MCP tests | `dotnet test tests/Twig.Mcp.Tests` | All pass (T-6 covers tool integration and JSON formatting) |
| Full suite | `dotnet test Twig.slnx` | No regressions in any other test project |

Self-containment is guaranteed because the feature is purely additive — no existing signatures are changed, only new types and methods are added.

## References

- [ADO #2070](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/2070) — Parent Issue
- `FlowCloseCommand.cs` (L119–164) — Existing 1-level child-state verification pattern
- `StateCategoryResolver.cs` — Process-agnostic state classification
- `ProcessConfigBuilder.cs` (TestKit) — Standard process template builders for tests
- External: `twig-conductor-workflows` repo — close_out agent prompt (follow-up update needed)
