---
work_item_id: 2070
title: "SDLC close_out: drill into Issues to verify Task states before Epic closure"
type: Issue
---

# SDLC close_out: Drill Into Issues to Verify Task States Before Epic Closure

> **Status**: 🔨 In Progress
> **Work Item**: [#2070](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/2070)
> **Revision**: 0 (initial draft)

---

## Executive Summary

The SDLC `close_out` agent (Phase 5) currently verifies that an Epic's direct child Issues are in a Done state before transitioning the Epic to Done — but it does **not** drill into those Issues to verify that their child Tasks are also in terminal states. This can result in premature Epic closure when Tasks are still Active or To Do under otherwise-Done Issues. This plan introduces a new `twig_verify_descendants` MCP tool that performs recursive child-state verification in deterministic code, and updates the close_out agent prompt to call this tool at Step 1c. The tool returns structured JSON with a pass/fail verdict and a list of any incomplete items, enabling the agent to block closure or surface incomplete Tasks to the user.

## Background

### Current Architecture

The close_out agent is Phase 5 of the twig-sdlc Conductor workflow, defined in the external `twig-conductor-workflows` repository:

| File (external repo) | Role |
|------|------|
| `close-out.prompt.md` | 11-step agent prompt |
| `close-out.system.md` | Agent identity/role |
| `twig-sdlc.yaml` (lines 800–829) | Agent YAML definition |

The close_out agent has an 11-step sequential procedure. Step 1c ("Verify all child items Done") uses `twig tree --output json` to inspect the Epic's children. However, `twig tree` returns a **display-oriented** tree from the active item's perspective, and the agent only checks the top-level children (Issues) — not their children (Tasks).

### Existing Child-State Verification Pattern

The CLI already has a proven child-state verification pattern in `FlowCloseCommand.cs` (lines 119–169):

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

### MCP Tool Architecture

MCP tools are organized into tool classes in `src/Twig.Mcp/Tools/`:

| Class | Category | Tools |
|-------|----------|-------|
| `NavigationTools` | Read-only navigation | `twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint` |
| `ReadTools` | Display-oriented reads | `twig_tree`, `twig_workspace` |
| `MutationTools` | State changes | `twig_state`, `twig_update`, `twig_note`, `twig_discard`, `twig_sync` |
| `ContextTools` | Context management | `twig_set`, `twig_status` |
| `CreationTools` | Work item creation | `twig_new`, `twig_find_or_create`, `twig_link`, `twig_link_artifact` |
| `BatchTools` | Batch operations | `twig_batch` |

All tools resolve per-workspace services through `WorkspaceResolver` and return structured JSON via `McpResultBuilder`.

### Call-Site Audit

The new `twig_verify_descendants` tool is additive — it does not modify any existing contracts. However, several existing components relate to the verification concern:

| File | Component | Current Usage | Impact |
|------|-----------|---------------|--------|
| `FlowCloseCommand.cs` | Child-state gate (lines 119–169) | Checks 1 level of children before CLI `flow-close` | None — parallel concern, could later be refactored to use the shared service |
| `StateCategoryResolver.cs` | `Resolve(state, entries)` | Maps state → `StateCategory` | Reused by new tool (no changes) |
| `ProcessConfigExtensions.cs` | `ComputeChildProgress()` | Counts Resolved/Completed children | Conceptually related but different scope (progress ≠ verification) |
| `NavigationTools.cs` | `twig_children` (line 91) | Lists direct children from cache | New tool uses same `GetChildrenAsync` method recursively |
| `MutationTools.cs` | `twig_state` (line 22) | Raw state change, no child verification | The SDLC agent calls this to close — new tool gates before this call |
| `WorkspaceContext.cs` | `FetchWithFallbackAsync` | Cache-first, ADO-fallback fetch | Reused by new tool for reliable item retrieval |

## Problem Statement

The SDLC `close_out` agent can transition an Epic to "Done" while child Tasks underneath Issues are still in Active or To Do state. The verification gap exists because:

1. **Step 1c only checks one level**: The agent inspects direct children of the Epic (Issues) but does not recursively check Issues' children (Tasks).
2. **No recursive verification tool**: The `twig_children` MCP tool returns direct children only. The agent would need to make N+1 calls to drill into each Issue, which is fragile (LLM may skip some) and expensive.
3. **`twig_state` has no guard**: The MCP `twig_state` tool performs raw state transitions without child verification — it's a mutation primitive, not a workflow tool. The SDLC agent calls `twig state Done` directly, bypassing the `FlowCloseCommand` guard.

The result: an Epic can show "Done" on the ADO board while hidden Tasks underneath remain incomplete, creating board inconsistency and violating the Conductor design principles P7 (Fail Honestly) and P10 (Explicit Invariants).

## Goals and Non-Goals

### Goals

1. **Deterministic recursive verification**: Add a new `twig_verify_descendants` MCP tool that recursively checks all descendants of a work item to a configurable depth, returning a structured pass/fail verdict with details on any incomplete items
2. **Single MCP call**: The close_out agent should be able to verify the entire Epic→Issue→Task hierarchy in one tool call, eliminating N+1 fragility
3. **Process-agnostic terminal state detection**: Use `StateCategoryResolver` with `ProcessConfiguration` to determine terminal states, supporting all ADO process templates
4. **Update close_out prompt**: Modify Step 1c in `close-out.prompt.md` to call `twig_verify_descendants` and block Epic closure if any descendants are incomplete
5. **Actionable output**: The tool's response must list each incomplete item with ID, title, type, state, and parent chain context so the agent can surface meaningful information to the user

### Non-Goals

1. **Modifying `FlowCloseCommand`** — The CLI command has its own one-level verification gate. Refactoring it to use the shared service is future work.
2. **Auto-transitioning incomplete items** — The tool reports state; it does not fix it. The agent decides what to do.
3. **Enforcing closure at the `twig_state` layer** — Adding child verification to the generic `twig_state` MCP tool would break its contract as a primitive. Verification is the agent's responsibility.
4. **ADO webhooks or real-time monitoring** — This is a point-in-time verification check, not a continuous monitor.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | New MCP tool `twig_verify_descendants` accepts a work item ID and max depth (default 2) |
| FR-2 | Tool recursively fetches children up to `maxDepth` levels, starting from the given ID |
| FR-3 | Each descendant is classified as terminal or non-terminal using `StateCategoryResolver` |
| FR-4 | Tool returns structured JSON: `verified` (bool), `totalChecked` (int), `incompleteCount` (int), `incomplete` (array of items with id, title, type, state, parentId, depth) |
| FR-5 | If `maxDepth` is 0, tool checks direct children only (same as current behavior) |
| FR-6 | Items with unmapped types (no `TypeConfig`) are treated as non-terminal (conservative) |
| FR-7 | The close_out prompt Step 1c calls `twig_verify_descendants` with depth=2 to cover Epic→Issue→Task |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Tool completes within the MCP response timeout (uses cache-first with ADO fallback) |
| NFR-2 | AOT-compatible: no reflection, uses existing `TwigJsonContext` patterns |
| NFR-3 | Process-agnostic: no hardcoded state names or type names |
| NFR-4 | Zero impact on existing tools (additive change only) |

## Proposed Design

### Architecture Overview

```
close_out agent (Step 1c)
    │
    ▼
twig_verify_descendants(epic_id, maxDepth=2)
    │
    ▼ WorkspaceResolver
    │
NavigationTools.VerifyDescendants()
    │
    ├─── DescendantVerificationService  (new domain service)
    │         │
    │         ├── IWorkItemRepository.GetChildrenAsync()  (cache)
    │         ├── IAdoWorkItemService.FetchChildrenAsync() (ADO fallback)
    │         ├── ProcessConfiguration.TypeConfigs
    │         └── StateCategoryResolver.Resolve()
    │
    ▼
McpResultBuilder.FormatVerification()  (new format method)
    │
    ▼
Structured JSON response → agent decision
```

### Key Components

#### 1. `DescendantVerificationService` (new domain service)

**Location**: `src/Twig.Domain/Services/DescendantVerificationService.cs`

**Responsibilities**:
- Recursively traverses the work item hierarchy from a root ID to a configurable depth
- At each level, fetches children (ADO-first with cache fallback, matching `FlowCloseCommand` pattern)
- Classifies each descendant's state using `StateCategoryResolver` with process configuration
- Accumulates results into a `DescendantVerificationResult`

**Design rationale**: Placing the verification logic in a domain service (not in the MCP tool directly) enables reuse by `FlowCloseCommand` in the future and keeps the MCP tool thin.

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

#### 2. `DescendantVerificationResult` (new read model)

**Location**: `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs`

```csharp
public sealed record DescendantVerificationResult(
    int RootId,
    bool Verified,
    int TotalChecked,
    IReadOnlyList<IncompleteItem> Incomplete);

public sealed record IncompleteItem(
    int Id, string Title, string Type, string State, int? ParentId, int Depth);
```

#### 3. `NavigationTools.VerifyDescendants()` (new MCP tool)

**Location**: `src/Twig.Mcp/Tools/NavigationTools.cs` (added method)

**Responsibilities**:
- Resolves workspace via `WorkspaceResolver`
- Instantiates `DescendantVerificationService` with workspace-scoped services
- Calls `VerifyAsync(id, maxDepth)` and formats via `McpResultBuilder`

#### 4. `McpResultBuilder.FormatVerification()` (new format method)

**Location**: `src/Twig.Mcp/Services/McpResultBuilder.cs` (added method)

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

1. Agent calls `twig_verify_descendants(id=<epic_id>, maxDepth=2)`
2. `NavigationTools.VerifyDescendants()` resolves workspace, creates `DescendantVerificationService`
3. Service calls `adoService.FetchChildrenAsync(epicId)` → gets Issues (depth 1)
4. For each Issue, service calls `adoService.FetchChildrenAsync(issueId)` → gets Tasks (depth 2)
5. Each Task's state is resolved via `StateCategoryResolver.Resolve(state, typeConfig.StateEntries)`
6. Non-terminal items (not Completed/Resolved/Removed) are collected as `IncompleteItem`
7. Result is formatted as JSON and returned to the agent
8. Agent inspects `verified`: if `false`, lists incomplete items and blocks closure

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | New domain service, not inline in MCP tool | Enables future reuse by `FlowCloseCommand`; keeps MCP tools thin per existing pattern |
| DD-2 | ADO-first with cache fallback (not cache-only) | Children may not be in the local cache if they were created by another tool or process. ADO fetch ensures accuracy. Matches `FlowCloseCommand` pattern. |
| DD-3 | Terminal = Completed ∪ Resolved ∪ Removed | Same definition as `FlowCloseCommand` (line 152). Process-agnostic via `StateCategory`. |
| DD-4 | maxDepth defaults to 2 | Epic→Issue (depth 1) → Task (depth 2) covers the standard ADO hierarchy. Deeper nesting is unusual. |
| DD-5 | Items with unknown types treated as non-terminal | Conservative: if we can't resolve the type's state config, we don't know if it's terminal. Same as `FlowCloseCommand`. |
| DD-6 | Tool placed in `NavigationTools` (not new file) | It's a read-only navigation tool. Consistent with `twig_children` and `twig_parent`. |
| DD-7 | Root item itself is NOT verified, only descendants | The root item is the one being closed — its state is the agent's responsibility. The tool verifies everything underneath. |
| DD-8 | Best-effort cache warm after ADO fetch | Fetched items are saved to cache for subsequent operations (consistent with `FetchWithFallbackAsync`). |

## Dependencies

### External Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| `twig-conductor-workflows` repo | Requires prompt PR | Step 1c prompt update lives in external repo |

### Internal Dependencies

| Component | Status | Notes |
|-----------|--------|-------|
| `StateCategoryResolver` | Existing | Reused as-is |
| `ProcessConfiguration` / `TypeConfig` | Existing | Reused as-is |
| `IWorkItemRepository.GetChildrenAsync()` | Existing | Cache source |
| `IAdoWorkItemService.FetchChildrenAsync()` | Existing | ADO source |
| `McpResultBuilder.BuildJson()` | Existing | JSON formatting pattern |
| `WorkspaceContext` | Existing | Service resolution |

### Sequencing

1. Tasks in this repo (MCP tool + domain service + tests) can be implemented independently
2. The prompt update in `twig-conductor-workflows` should be done after the tool is deployed, to avoid the agent calling a non-existent tool

## Open Questions

| ID | Question | Severity | Notes |
|----|----------|----------|-------|
| OQ-1 | Should the tool also verify the root item's direct children (Issues) in addition to grandchildren (Tasks)? | Low | Current proposal verifies ALL descendants. The close_out agent's Step 1c already checks Issues separately — having the tool also verify them provides a single source of truth and simplifies the prompt. **Recommendation: verify all descendants including Issues.** |
| OQ-2 | Should `FlowCloseCommand` be refactored to use `DescendantVerificationService` in this same PR? | Low | Non-goal for this Issue, but worth noting as follow-up. The current `FlowCloseCommand` one-level check still works correctly for its use case (closing a single item). |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/DescendantVerificationService.cs` | Domain service: recursive descendant verification with ADO-first/cache-fallback |
| `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs` | Read models: `DescendantVerificationResult` and `IncompleteItem` records |
| `tests/Twig.Domain.Tests/Services/DescendantVerificationServiceTests.cs` | Unit tests for the domain service |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsVerifyDescendantsTests.cs` | Unit tests for the MCP tool |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderVerificationTests.cs` | Unit tests for the format method |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Add `VerifyDescendants()` MCP tool method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatVerification()` static method |

## ADO Work Item Structure

This is an Issue (#2070) — Tasks are defined directly under it.

### Issue #2070: SDLC close_out: drill into Issues to verify Task states before Epic closure

**Goal**: Add a `twig_verify_descendants` MCP tool that recursively checks all descendants of a work item for terminal state, and update the SDLC close_out agent prompt to use it at Step 1c.

**Prerequisites**: None

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-2070.1 | Create `DescendantVerificationResult` and `IncompleteItem` read models | `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs` | ~30 LoC |
| T-2070.2 | Implement `DescendantVerificationService` with recursive ADO-first/cache-fallback traversal | `src/Twig.Domain/Services/DescendantVerificationService.cs` | ~80 LoC |
| T-2070.3 | Add `FormatVerification()` to `McpResultBuilder` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~30 LoC |
| T-2070.4 | Add `twig_verify_descendants` MCP tool to `NavigationTools` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~30 LoC |
| T-2070.5 | Write unit tests for `DescendantVerificationService` | `tests/Twig.Domain.Tests/Services/DescendantVerificationServiceTests.cs` | ~200 LoC |
| T-2070.6 | Write unit tests for `NavigationTools.VerifyDescendants` and `McpResultBuilder.FormatVerification` | `tests/Twig.Mcp.Tests/Tools/NavigationToolsVerifyDescendantsTests.cs`, `tests/Twig.Mcp.Tests/Services/McpResultBuilderVerificationTests.cs` | ~150 LoC |

**Acceptance Criteria**:
- [ ] `twig_verify_descendants` MCP tool exists and is discoverable
- [ ] Tool recursively checks descendants to configurable depth (default 2)
- [ ] Tool returns `verified: true` when all descendants are in terminal states
- [ ] Tool returns `verified: false` with detailed `incomplete` array when any descendant is non-terminal
- [ ] Unknown/unmapped types are treated as non-terminal (conservative)
- [ ] ADO-first with cache fallback for fetching children at each level
- [ ] All tests pass including existing regression tests
- [ ] Build succeeds with `TreatWarningsAsErrors=true` and AOT trimming

> **Note**: The close_out prompt update (Step 1c) is tracked separately as a follow-up in the `twig-conductor-workflows` repository. This Issue delivers the MCP tool that the prompt will call.

## PR Groups

### PG-1: Domain Service + MCP Tool + Tests

**Type**: Deep
**Tasks**: T-2070.1, T-2070.2, T-2070.3, T-2070.4, T-2070.5, T-2070.6
**Estimated LoC**: ~520
**Files**: ~7

All changes are tightly coupled — the read models, domain service, MCP tool, formatter method, and tests form a single coherent unit. This is small enough for a single PR and benefits from being reviewed together so the reviewer can see the full data flow from domain service → MCP tool → JSON output.

**Execution order**: No dependencies on other PR groups. This is the only PG.

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | PG-1-descendant-verification | T-2070.1, T-2070.2, T-2070.3, T-2070.4, T-2070.5, T-2070.6 | None | Deep |

### Execution Order

**PG-1-descendant-verification** is the only PR group. All six tasks form a single coherent unit:
- T-2070.1 (read models) and T-2070.2 (domain service) must precede T-2070.4 (MCP tool) and T-2070.5 (domain service tests) because the tool and tests depend on the types defined in those files.
- T-2070.3 (`FormatVerification` method) must precede T-2070.4 (MCP tool) since the tool calls it.
- T-2070.5 and T-2070.6 (tests) are implemented last once all production code is in place.

All changes are strictly additive (new files + two modified files) with no impact on existing tool contracts, so the entire changeset builds and tests independently as a single PR.

### Validation Strategy

**PG-1-descendant-verification**
- `dotnet build` with `TreatWarningsAsErrors=true` and AOT trimming must pass
- All new tests in `DescendantVerificationServiceTests.cs`, `NavigationToolsVerifyDescendantsTests.cs`, and `McpResultBuilderVerificationTests.cs` must pass
- Full regression test suite (`dotnet test`) must remain green
- Verify `twig_verify_descendants` is discoverable in `twig-mcp` tool listing at runtime

## References

- [Child-State Verification Gate plan](./child-state-verification-gate.plan.md) — Prior art for the `FlowCloseCommand` one-level verification pattern
- [Streamline Close-Out Fast-Path plan](./streamline-closeout-fast-path.plan.md) — Related close_out optimization (fast-path for already-Done items)
- [twig-conductor-workflows](https://github.com/PolyphonyRequiem/twig-conductor-workflows) — External repo containing the close_out agent prompt files
