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

The SDLC close_out agent can currently transition an Epic to "Done" without verifying that Tasks underneath its child Issues are also in terminal states — because `twig tree` only returns direct children (Issues), not grandchildren (Tasks). This plan introduces a new `twig_verify_descendants` MCP tool backed by a `DescendantVerificationService` domain service that performs deterministic, recursive, process-agnostic child-state verification in a single MCP call. The close_out agent prompt (Step 1c) is updated to call this tool instead of relying on manual `twig_children` iteration, and Step 1e gains Task-level rollback logic. The result is a fail-safe gate that prevents premature Epic closure when any descendant remains incomplete.

## Background

### Current Architecture

The SDLC lifecycle is orchestrated by the Conductor apex workflow `twig-sdlc-full.yaml`:

```
state_detector (script) → planning → implementation → close_out (agent) → retrospective → $end
```

The **close_out agent** (`claude-opus-4.6-1m`) is a single LLM agent defined in the apex workflow. Its behavior is governed by two prompt files in the external `twig-conductor-workflows` registry:

- **`close-out.system.md`** — role definition, verification rules, postconditions
- **`close-out.prompt.md`** — step-by-step instructions (Steps 0–10)

### How close_out currently verifies child states (Step 1c)

```
Step 1c: Verify all child items are Done
  - twig set <id> --output json
  - twig tree --output json — inspect all children
  - If ANY child Issue or Task is NOT in state "Done": ...
```

The prompt says "Issue or Task" but **`twig tree` only returns direct children** of the focused item. For an Epic, this means:
- ✅ Issues are visible (direct children of Epic)
- ❌ Tasks are **NOT visible** (children of Issues, not of Epic)

### Tool behavior analysis

| Tool | What it returns | Depth |
|------|----------------|-------|
| `twig_tree` | Focus item + direct children + parent chain | 1 level of children |
| `twig_children <id>` | Direct children of the given work item | 1 level |
| `twig_batch` | Parallel/sequential execution of multiple tool calls | N/A |

The `twig tree` MCP tool (`ReadTools.cs`) calls `GetChildrenAsync(item.Id)` which returns only direct children from the local SQLite cache. There is **no recursive child traversal** in any existing MCP tool.

### Existing child-state verification pattern

The CLI already has a proven one-level child-state verification pattern in `FlowCloseCommand.cs` (lines 119–164):

```csharp
// FlowCloseCommand.cs — existing one-level verification
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
- **ADO-authoritative with cache fallback**: tries ADO first, falls back to local SQLite cache

### MCP tool architecture

MCP tools are organized into tool classes in `src/Twig.Mcp/Tools/`:

| Class | Category | Example Tools |
|-------|----------|---------------|
| `NavigationTools` | Read-only navigation | `twig_show`, `twig_query`, `twig_children`, `twig_parent` |
| `ReadTools` | Display-oriented reads | `twig_tree`, `twig_workspace` |
| `MutationTools` | State changes | `twig_state`, `twig_update`, `twig_note` |
| `ContextTools` | Context management | `twig_set`, `twig_status` |
| `CreationTools` | Work item creation | `twig_new`, `twig_find_or_create`, `twig_link` |
| `BatchTools` | Batch operations | `twig_batch` |

All tools resolve per-workspace services through `WorkspaceResolver` → `WorkspaceContext` which bundles `IWorkItemRepository`, `IAdoWorkItemService`, `IProcessConfigurationProvider`, and other services.

### Call-site audit

The new `twig_verify_descendants` tool is **additive** — it does not modify any existing contracts. Related components:

| File | Component | Current Usage | Impact |
|------|-----------|---------------|--------|
| `FlowCloseCommand.cs` | Child-state gate (lines 119–164) | Checks 1 level of children before CLI `flow-close` | None — parallel concern, could later be refactored to use shared service |
| `StateCategoryResolver.cs` | `Resolve(state, entries)` | Maps state → `StateCategory` | Reused by new tool (no changes) |
| `ProcessConfigExtensions.cs` | `ComputeChildProgress()` | Counts Resolved/Completed children | Conceptually related but different scope |
| `NavigationTools.cs` | `twig_children` (line 91) | Lists direct children from cache | New tool uses same `GetChildrenAsync` method recursively |
| `MutationTools.cs` | `twig_state` (line 22) | Raw state change, no child verification | The SDLC agent calls this to close — new tool gates before this call |
| `WorkspaceContext.cs` | `FetchWithFallbackAsync` | Cache-first, ADO-fallback fetch | Pattern reused by new service |

### Related prior art

1. **`load-work-tree.ps1`** (implementation phase) — handles Epic → Issue → Task hierarchy by calling `twig tree --depth 2` and iterating `$child.children`. However, this script relies on CLI JSON formatter behavior, not MCP JSON output shape.

2. **`detect-state.ps1`** — counts `doneCount`, `doingCount`, `todoCount` for direct children only. Affects routing accuracy but not close_out behavior. Out of scope.

3. **`pr-finalizer.prompt.md`** Step 5b — has "Verify Task states match reality" but only checks Tasks within completed PR groups, not all Tasks across the entire hierarchy.

## Problem Statement

The close_out agent can transition an Epic to "Done" when Issues show as "Done" but their child Tasks are still in non-terminal states. Specifically:

1. **No Task visibility**: `twig tree` from the Epic level shows Issues but not Tasks. The prompt says "If ANY child Issue or Task is NOT in state Done" but the tool output physically cannot surface Task states at the grandchild level.

2. **N+1 fragility**: The alternative — instructing the LLM to call `twig_children` for each Issue individually — is fragile. The agent may skip Issues, misparse results, or fail to aggregate properly. This is non-deterministic behavior for a critical safety gate.

3. **Incomplete rollback**: Step 1e rolls back orphaned "Doing" Issues but does not address orphaned "Doing" Tasks under those Issues. Tasks stuck in "Doing" after a partial workflow run create misleading state on the ADO board.

4. **Postcondition gap**: The system prompt's postcondition says "If all children Done" without explicitly requiring Task-level verification, making the contract ambiguous.

## Goals and Non-Goals

### Goals

1. **Single-call deterministic verification**: Add a new `twig_verify_descendants` MCP tool that recursively checks all descendants of a work item to a configurable depth, returning a structured pass/fail verdict with details on any incomplete items
2. **Process-agnostic terminal state detection**: Use `StateCategoryResolver` with `ProcessConfiguration` to determine terminal states, supporting all ADO process templates (Basic, Agile, Scrum, CMMI)
3. **Update close_out prompt**: Modify Step 1c in `close-out.prompt.md` to call `twig_verify_descendants` and block Epic closure if any descendants are incomplete
4. **Task-level rollback**: Enhance Step 1e to roll back orphaned "Doing" Tasks in addition to Issues
5. **Actionable output**: The tool's response must list each incomplete item with ID, title, type, state, and parent ID so the agent can surface meaningful information

### Non-Goals

1. **Modifying `FlowCloseCommand`** — The CLI command has its own one-level verification gate. Refactoring it to use the shared service is future work.
2. **Auto-transitioning incomplete items** — The tool reports state; it does not fix it. The agent decides what to do (Step 1e handles rollback).
3. **Enforcing closure at the `twig_state` layer** — Adding child verification to the generic `twig_state` MCP tool would break its contract as a mutation primitive.
4. **Enhancing `detect-state.ps1`** — That script handles routing, not closure. A similar gap exists there but it's a separate concern.
5. **Adding recursive depth to `twig_tree`** — The tree tool is designed for focused single-level views. A dedicated verification tool is the right abstraction.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | New MCP tool `twig_verify_descendants` accepts a work item ID and optional `maxDepth` (default 2) |
| FR-2 | Tool recursively fetches children up to `maxDepth` levels, starting from the given ID |
| FR-3 | Each descendant is classified as terminal or non-terminal using `StateCategoryResolver` with process configuration |
| FR-4 | Tool returns structured JSON: `verified` (bool), `totalChecked` (int), `incompleteCount` (int), `incomplete` (array of items with id, title, type, state, parentId, depth) |
| FR-5 | If `maxDepth` is 0, tool checks direct children only (degenerate case) |
| FR-6 | Items with unmapped types (no `TypeConfig`) are treated as non-terminal (conservative) |
| FR-7 | The close_out prompt Step 1c calls `twig_verify_descendants` with depth=2 to cover Epic→Issue→Task |
| FR-8 | Step 1e rolls back orphaned "Doing" Tasks in addition to Issues |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Tool completes within the MCP response timeout (uses ADO-first with cache fallback) |
| NFR-2 | AOT-compatible: no reflection, uses `Utf8JsonWriter` directly (same as all existing MCP formatters) |
| NFR-3 | Process-agnostic: no hardcoded state names or type names |
| NFR-4 | Zero impact on existing tools (additive change only) |
| NFR-5 | `TreatWarningsAsErrors=true` compliance |

## Proposed Design

### Architecture Overview

```
close_out agent (Step 1c)
    │
    ▼
twig_verify_descendants(id, maxDepth=2)
    │
    ▼ WorkspaceResolver → WorkspaceContext
    │
NavigationTools.VerifyDescendants()
    │
    ├─── DescendantVerificationService  (new domain service)
    │         │
    │         ├── IAdoWorkItemService.FetchChildrenAsync()  (ADO — authoritative)
    │         ├── IWorkItemRepository.GetChildrenAsync()     (cache — fallback)
    │         ├── IWorkItemRepository.SaveBatchAsync()       (best-effort cache warm)
    │         ├── ProcessConfiguration.TypeConfigs            (state config lookup)
    │         └── StateCategoryResolver.Resolve()             (terminal classification)
    │
    ▼
McpResultBuilder.FormatVerification()  (new format method)
    │
    ▼
Structured JSON response → agent decision
```

### Key Components

#### 1. `DescendantVerificationResult` (new read model)

**Location**: `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs`

```csharp
public sealed record DescendantVerificationResult(
    int RootId,
    bool AllTerminal,
    int TotalChecked,
    IReadOnlyList<IncompleteDescendant> Incomplete);

public sealed record IncompleteDescendant(
    int Id, string Title, string Type, string State, int? ParentId, int Depth);
```

**Design rationale**: Immutable records match the existing read model pattern (`WorkTree`, `Workspace`, `QueryResult`). The `Depth` field allows the agent to understand where in the hierarchy the incomplete item sits (1 = Issue, 2 = Task).

#### 2. `DescendantVerificationService` (new domain service)

**Location**: `src/Twig.Domain/Services/DescendantVerificationService.cs`

**Responsibilities**:
- Recursively traverses the work item hierarchy from a root ID to a configurable depth
- At each level, fetches children via ADO (authoritative) with local cache fallback
- Classifies each descendant's state using `StateCategoryResolver` with process configuration
- Accumulates results into a `DescendantVerificationResult`
- Best-effort cache warm for fetched items

```csharp
public sealed class DescendantVerificationService(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IProcessConfigurationProvider processConfigProvider)
{
    public async Task<DescendantVerificationResult> VerifyAsync(
        int rootId, int maxDepth = 2, CancellationToken ct = default);
}
```

**Algorithm** (breadth-first):
1. Initialize `currentLevel = [rootId]`, `depth = 0`, `incomplete = []`, `totalChecked = 0`
2. While `depth < maxDepth` and `currentLevel` is non-empty:
   a. For each ID in `currentLevel`, fetch children (ADO-first, cache fallback)
   b. Best-effort save fetched children to cache
   c. For each child, resolve `StateCategory` via `StateCategoryResolver`
   d. If not terminal (`Completed`, `Resolved`, or `Removed`), add to `incomplete`
   e. Collect all child IDs as `nextLevel`
   f. `totalChecked += children.Count`, `depth++`, `currentLevel = nextLevel`
3. Return `DescendantVerificationResult(rootId, incomplete.Count == 0, totalChecked, incomplete)`

**Key design decision**: ADO-first with cache fallback matches the `FlowCloseCommand` pattern. Children may not be in the local cache if they were created by another agent or manually in ADO. This ensures accuracy for the critical closure gate.

#### 3. `NavigationTools.VerifyDescendants()` (new MCP tool)

**Location**: `src/Twig.Mcp/Tools/NavigationTools.cs` (added method)

```csharp
[McpServerTool(Name = "twig_verify_descendants"),
 Description("Verify all descendants of a work item are in terminal state")]
public async Task<CallToolResult> VerifyDescendants(
    [Description("Root work item ID")] int id,
    [Description("Maximum depth to check (default: 2)")] int maxDepth = 2,
    [Description("Target workspace")] string? workspace = null,
    CancellationToken ct = default)
```

**Responsibilities**:
- Resolves workspace via `WorkspaceResolver`
- Creates `DescendantVerificationService` with workspace-scoped services
- Calls `VerifyAsync(id, maxDepth)` and formats via `McpResultBuilder.FormatVerification`

#### 4. `McpResultBuilder.FormatVerification()` (new format method)

**Location**: `src/Twig.Mcp/Services/McpResultBuilder.cs` (added static method)

**Output JSON shape**:
```json
{
  "rootId": 100,
  "verified": false,
  "totalChecked": 12,
  "incompleteCount": 2,
  "incomplete": [
    { "id": 205, "title": "Implement parser", "type": "Task", "state": "Active", "parentId": 102, "depth": 2 },
    { "id": 208, "title": "Write tests", "type": "Task", "state": "To Do", "parentId": 103, "depth": 2 }
  ],
  "workspace": "org/project"
}
```

### Data Flow

```
Epic #100 (focused)
  │
  ├─ VerifyAsync(100, maxDepth=2)
  │
  │  Level 1: FetchChildrenAsync(100) → [Issue-101 Done, Issue-102 Done, Issue-103 Doing]
  │    ├─ Issue-103 Doing → incomplete (depth=1)
  │    └─ totalChecked += 3
  │
  │  Level 2: FetchChildrenAsync(101) → [Task-201 Done, Task-202 Done]
  │           FetchChildrenAsync(102) → [Task-203 Done, Task-204 Active]
  │           FetchChildrenAsync(103) → [Task-205 To Do]
  │    ├─ Task-204 Active → incomplete (depth=2)
  │    ├─ Task-205 To Do → incomplete (depth=2)
  │    └─ totalChecked += 5
  │
  │  Result: { verified: false, totalChecked: 8, incompleteCount: 3, incomplete: [...] }
  │
  └─ Agent: blocks Epic closure, proceeds to Step 1e rollback
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | New domain service, not inline in MCP tool | Enables future reuse by `FlowCloseCommand`; keeps MCP tools thin per existing pattern |
| DD-2 | ADO-first with cache fallback (not cache-only) | Children may not be in the local cache. ADO fetch ensures accuracy for this critical gate. Matches `FlowCloseCommand` pattern. |
| DD-3 | Terminal = `Completed ∪ Resolved ∪ Removed` | Same definition as `FlowCloseCommand` (line 152). Process-agnostic via `StateCategory`. |
| DD-4 | `maxDepth` defaults to 2 | Epic→Issue (depth 1) → Task (depth 2) covers the standard ADO hierarchy. Deeper nesting is unusual. |
| DD-5 | Items with unknown types treated as non-terminal | Conservative: if we can't resolve the type's state config, we don't know if it's terminal. Same as `FlowCloseCommand`. |
| DD-6 | Tool placed in `NavigationTools` (not new file) | It's a read-only navigation tool. Consistent with `twig_children` and `twig_parent` placement. |
| DD-7 | Root item itself is NOT verified, only descendants | The root item is the one being closed — its state is the agent's responsibility. The tool verifies everything underneath. |
| DD-8 | Breadth-first traversal (not depth-first) | Allows grouping children fetches per level for potential future parallelization. Also produces natural depth ordering in the `incomplete` list. |
| DD-9 | Prompt-driven rollback (not tool-driven) | The verification tool is read-only (reports state). Rollback decisions remain in the agent's prompt, preserving the existing separation of concerns between read tools and mutation tools. |

### Close_out prompt changes

#### Step 1c enhancement

**Before** (current):
```markdown
1c. **Verify all child items are Done**:
  - twig tree --output json — inspect all children
  - If ANY child Issue or Task is NOT in state "Done": block
```

**After** (proposed):
```markdown
1c. **Verify all descendants are in terminal state**:
  - twig_verify_descendants(id=<epic_id>, maxDepth=2) — recursive check
  - If `verified` is false:
    1. Set `epic_completed: false`
    2. Record the `incomplete` array (IDs, titles, states, parent IDs)
    3. SKIP Steps 2, 3, 4, 9, and 10
    4. Proceed to Step 1e (rollback), then Steps 5–8 (observations)
  - If `verified` is true: set `epic_completed: true`, continue with Step 2
```

#### Step 1e enhancement

**Before** (current):
```markdown
1e. Roll back orphaned "Doing" Issues
```

**After** (proposed):
```markdown
1e. Roll back orphaned "Doing" Items (Tasks first, then Issues):
  - From the `incomplete` array in Step 1c, identify items in "Doing" state
  - For each "Doing" Task (depth=2):
    1. twig set <task_id>
    2. twig note "State rollback: reverted from Doing → To Do"
    3. twig state "To Do" --force
  - For each "Doing" Issue (depth=1):
    1. twig set <issue_id>
    2. twig note "State rollback: reverted from Doing → To Do"
    3. twig state "To Do" --force
  - Task rollback before Issue rollback prevents state inconsistencies
```

#### System prompt postcondition update

**Before**:
```markdown
- If all children Done: root transitioned to Done
```

**After**:
```markdown
- If all descendants (Issues AND Tasks) in terminal state: root transitioned to Done
```

## Dependencies

### External Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| `twig-conductor-workflows` repo | Requires prompt update | Step 1c/1e prompt changes + system prompt postcondition |

### Internal Dependencies

| Component | Status | Notes |
|-----------|--------|-------|
| `StateCategoryResolver` | Existing | Reused as-is |
| `ProcessConfiguration` / `TypeConfig` | Existing | Reused as-is |
| `IWorkItemRepository.GetChildrenAsync()` | Existing | Cache source |
| `IAdoWorkItemService.FetchChildrenAsync()` | Existing | ADO source |
| `McpResultBuilder.BuildJson()` | Existing | JSON formatting pattern |
| `WorkspaceContext` | Existing | Service resolution |
| `WorkItemBuilder` (TestKit) | Existing | Test fixture creation |
| `ProcessConfigBuilder` (TestKit) | Existing | Process config fixtures |

### Sequencing

1. Domain read model + service + tests (in-repo) — can be implemented immediately
2. MCP tool + formatter + tests (in-repo) — depends on domain service
3. Prompt update (external repo) — should be done after the tool is deployed, to avoid the agent calling a non-existent tool

## Impact Analysis

| Area | Impact |
|------|--------|
| Existing MCP tools | None — additive only |
| `FlowCloseCommand` | None — could later be refactored to use shared service |
| Build/AOT | Must compile clean with `TreatWarningsAsErrors` and AOT trimming |
| Performance | One additional MCP tool registration; N+1 ADO calls during verification (where N = number of intermediate-level items). Bounded by `maxDepth`. |
| Backward compatibility | Full — new tool is opt-in, existing behavior unchanged |

## Open Questions

| ID | Question | Severity | Notes |
|----|----------|----------|-------|
| OQ-1 | Should `FlowCloseCommand` be refactored to use `DescendantVerificationService` in this same PR? | Low | Non-goal for this Issue. The CLI command's one-level check still works correctly for its use case. Tracked as follow-up. |
| OQ-2 | Should `detect-state.ps1` also drill into Tasks? | Low | That script handles routing, not closure. The routing gap is a separate concern. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs` | Read models: `DescendantVerificationResult` and `IncompleteDescendant` records |
| `src/Twig.Domain/Services/DescendantVerificationService.cs` | Domain service: recursive descendant verification with ADO-first/cache-fallback |
| `tests/Twig.Domain.Tests/Services/DescendantVerificationServiceTests.cs` | Unit tests for the domain service |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsVerifyDescendantsTests.cs` | Unit tests for the MCP tool endpoint |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Add `VerifyDescendants()` MCP tool method (~25 LoC) |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatVerification()` static method (~30 LoC) |
| `~/.conductor/registries/twig/prompts/close-out.prompt.md` | Enhance Step 1c + Step 1e with `twig_verify_descendants` |
| `~/.conductor/registries/twig/prompts/close-out.system.md` | Update postconditions to require descendant verification |

## ADO Work Item Structure

This is an Issue (#2070) — Tasks are defined directly under it.

### Issue #2070: SDLC close_out: drill into Issues to verify Task states before Epic closure

**Goal**: Add a `twig_verify_descendants` MCP tool that recursively checks all descendants of a work item for terminal state, and update the SDLC close_out agent prompt to use it.

**Prerequisites**: None — this is a standalone enhancement.

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-2070.1 | Create `DescendantVerificationResult` and `IncompleteDescendant` read model records | `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs` | ~15 LoC |
| T-2070.2 | Implement `DescendantVerificationService` with recursive ADO-first/cache-fallback traversal | `src/Twig.Domain/Services/DescendantVerificationService.cs` | ~80 LoC |
| T-2070.3 | Add `FormatVerification()` to `McpResultBuilder` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~30 LoC |
| T-2070.4 | Add `twig_verify_descendants` MCP tool to `NavigationTools` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~25 LoC |
| T-2070.5 | Write unit tests for `DescendantVerificationService` (all terminal, mixed, unmapped types, ADO fallback, depth limits) | `tests/Twig.Domain.Tests/Services/DescendantVerificationServiceTests.cs` | ~200 LoC |
| T-2070.6 | Write unit tests for `NavigationTools.VerifyDescendants` and `McpResultBuilder.FormatVerification` | `tests/Twig.Mcp.Tests/Tools/NavigationToolsVerifyDescendantsTests.cs` | ~120 LoC |
| T-2070.7 | Update close_out prompt: Step 1c (use `twig_verify_descendants`), Step 1e (Task rollback), system prompt postconditions | `close-out.prompt.md`, `close-out.system.md` | ~40 LoC prompt |

#### Acceptance Criteria

- [ ] `twig_verify_descendants` MCP tool exists and is discoverable via MCP tool listing
- [ ] Tool recursively checks descendants to configurable depth (default 2)
- [ ] Tool returns `verified: true` when all descendants are in terminal states (Completed/Resolved/Removed)
- [ ] Tool returns `verified: false` with detailed `incomplete` array when any descendant is non-terminal
- [ ] Unknown/unmapped types are treated as non-terminal (conservative)
- [ ] ADO-first with cache fallback for fetching children at each level
- [ ] Step 1c in close_out prompt calls `twig_verify_descendants` instead of manual tree inspection
- [ ] Step 1e includes Task-level rollback before Issue-level rollback
- [ ] System prompt postconditions explicitly require descendant-level verification
- [ ] All tests pass including existing regression tests
- [ ] Build succeeds with `TreatWarningsAsErrors=true` and AOT trimming

## PR Groups

### PG-1: Domain Service + MCP Tool + Tests (deep)

**Classification**: Deep — 6 files (4 new + 2 modified), complex recursive verification logic

**Contains**: T-2070.1, T-2070.2, T-2070.3, T-2070.4, T-2070.5, T-2070.6

**Estimated scope**: ~500 LoC across 6 files

**Successors**: PG-2

This PR delivers the `twig_verify_descendants` MCP tool end-to-end: read models, domain service, MCP tool method, JSON formatter, and comprehensive tests. All components form a single coherent unit that should be reviewed together.

### PG-2: Close_out prompt update (wide)

**Classification**: Wide — 2 files, prompt text changes

**Contains**: T-2070.7

**Estimated scope**: ~40 LoC across 2 prompt files

**Predecessors**: PG-1 (tool must exist before the prompt references it)

This PR updates the close_out agent's prompt files in the external `twig-conductor-workflows` registry. It modifies Step 1c to call `twig_verify_descendants`, enhances Step 1e with Task-level rollback, and updates the system prompt postconditions.

> **Note**: PG-2 targets the `twig-conductor-workflows` repository, not the twig CLI repository. It will be a separate PR in that repo.

## References

- [`FlowCloseCommand.cs`](../../src/Twig/Commands/FlowCloseCommand.cs) — existing one-level child-state verification pattern
- [`StateCategoryResolver.cs`](../../src/Twig.Domain/Services/StateCategoryResolver.cs) — process-agnostic state classification
- [`NavigationTools.cs`](../../src/Twig.Mcp/Tools/NavigationTools.cs) — existing navigation tools (`twig_children`, `twig_parent`)
- [`McpResultBuilder.cs`](../../src/Twig.Mcp/Services/McpResultBuilder.cs) — JSON formatting patterns
- [`ProcessConfigBuilder.cs`](../../tests/Twig.TestKit/ProcessConfigBuilder.cs) — test fixture builder for process configurations
- [`WorkItemBuilder.cs`](../../tests/Twig.TestKit/WorkItemBuilder.cs) — test fixture builder for work items
- [Streamline Close-Out Fast-Path](./streamline-closeout-fast-path.plan.md) — related close_out optimization

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | `PG-1-domain-mcp-tool-tests` | T-2070.1, T-2070.2, T-2070.3, T-2070.4, T-2070.5, T-2070.6 | None | Deep |
| PG-2 | `PG-2-closeout-prompt-update` | T-2070.7 | PG-1 | Wide |

### Execution Order

**PG-1** is implemented first and is fully self-contained within the `twig` repository. It delivers the complete `twig_verify_descendants` MCP tool end-to-end: read models (`DescendantVerificationResult`, `IncompleteDescendant`), domain service (`DescendantVerificationService`), JSON formatter (`McpResultBuilder.FormatVerification`), MCP tool method (`NavigationTools.VerifyDescendants`), and all unit tests. All components are additive — no existing contracts are modified — so the build and full test suite pass independently.

**PG-2** follows after PG-1 is merged and the twig binary is deployed. It targets the external `twig-conductor-workflows` repository and contains only prompt text changes (Step 1c, Step 1e, and the system prompt postcondition). Because these are prompt files with no compilation step, the PR is self-contained from a build perspective; semantic correctness (the tool being callable) is ensured by the PG-1 dependency.

### Validation Strategy

| Group | Validation |
|-------|-----------|
| PG-1 | `dotnet build` (AOT + `TreatWarningsAsErrors=true`) passes clean; `dotnet test` for `Twig.Domain.Tests` and `Twig.Mcp.Tests` covers all branches (all terminal, mixed states, unmapped types, ADO-first/cache-fallback, depth limits). `twig_verify_descendants` appears in MCP tool listing after local publish. |
| PG-2 | Prompt diff reviewed for correctness; close_out agent integration tested manually or via Conductor dry-run against a prepared Epic with known-incomplete Tasks to confirm the agent blocks closure and performs correct rollback. |
