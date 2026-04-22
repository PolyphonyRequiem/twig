# Parent State Propagation on First Task Start

| Field | Value |
|-------|-------|
| **Status** | ✅ Done |
| **Work Item** | #1855 |
| **Parent Issue** | #1845 |
| **Author** | Copilot |
| **Plan Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

When an engineer starts the first child task under an Epic (via `twig flow-start`,
`twig state`, or the MCP `twig_state` tool), the parent Epic currently remains in
its initial state (e.g., "To Do"). This forces manual intervention to transition
the Epic to an in-progress state, causing stale boards and unnecessary toil. This
design introduces a **`ParentStatePropagationService`** in the Domain layer that
detects when a child work item transitions into an active (`InProgress`) state
category and, if the parent is still in the `Proposed` category, automatically
transitions the parent to `InProgress`. The propagation is process-agnostic
(uses `StateCategory`, not hardcoded state names), best-effort (failures never
break the child command), and respects the existing `FlowTransitionService`
pattern.

---

## Background

### Current State

The twig CLI manages Azure DevOps work items with a strict hierarchy:
Epic → Issue → Task (Basic), or Epic → Feature → User Story → Task (Agile/Scrum).
State transitions are explicit, user-initiated commands with no automatic
propagation between parent and child items.

Three code paths trigger state transitions to `InProgress`:

1. **`FlowStartCommand`** (`src/Twig/Commands/FlowStartCommand.cs`) — transitions
   child from `Proposed` → `InProgress` during `twig flow-start`
2. **`StateCommand`** (`src/Twig/Commands/StateCommand.cs`) — explicit
   `twig state <name>` command
3. **MCP `MutationTools.State()`** (`src/Twig.Mcp/Tools/MutationTools.cs`) —
   `twig_state` tool for Copilot agents

The `FlowCloseCommand` already implements the *inverse* pattern: it verifies all
children are in a terminal state before allowing the parent to close (child-state
verification gate at lines 119–169). However, no *forward* propagation exists.

The `HintEngine` already hints about parent state transitions: when all sibling
tasks complete, it suggests `twig up then twig state Done`. But hints require
manual action.

### Existing Patterns Leveraged

- **`FlowTransitionService.TransitionStateAsync()`** — category-based state
  transition with ADO patch, cache update, and `ProtectedCacheWriter` sync.
  This is the established pattern for automated transitions.
- **`StateCategoryResolver.Resolve()`** — process-agnostic state classification.
- **`StateResolver.ResolveByCategory()`** — finds concrete state name from category.
- **`ProcessConfigExtensions.SafeGetConfiguration()`** — safe type config lookup.

### Call-Site Audit

The following call sites perform state transitions and need parent propagation:

| # | File | Method | Current Usage | Propagation Impact |
|---|------|--------|---------------|-------------------|
| 1 | `src/Twig/Commands/FlowStartCommand.cs` | `ExecuteAsync()` (line 169–188) | Transitions child `Proposed` → `InProgress` via direct `PatchAsync` | **Add propagation**: after child transition, propagate to parent |
| 2 | `src/Twig/Commands/StateCommand.cs` | `ExecuteAsync()` (line 104–106) | Transitions active item to any state via `PatchAsync` | **Add propagation**: after successful transition to `InProgress`, propagate to parent |
| 3 | `src/Twig.Mcp/Tools/MutationTools.cs` | `State()` (line 69–71) | Transitions active item via `PatchAsync` | **Add propagation**: after successful transition to `InProgress`, propagate to parent |
| 4 | `src/Twig.Domain/Services/FlowTransitionService.cs` | `TransitionStateAsync()` | Category-based transitions for FlowDone/FlowClose | **No change**: these transition to Resolved/Completed, not InProgress |

---

## Problem Statement

When a child work item (Task, Issue, User Story, etc.) is transitioned to an active
state, the parent work item remains in its initial "Proposed" state. This creates:

1. **Stale ADO boards** — Epics/Features appear unstarted when work is actively underway.
2. **Manual overhead** — Engineers must remember to separately transition parent items.
3. **Workflow friction** — Agent-driven SDLC workflows (via MCP tools) miss this step
   entirely, requiring manual cleanup.

---

## Goals and Non-Goals

### Goals

1. **Automatic parent activation** — When a child transitions to `InProgress`,
   automatically transition the parent from `Proposed` → `InProgress` if it hasn't
   already moved past that state.
2. **Process-agnostic** — Work across all ADO process templates (Agile, Scrum, Basic,
   CMMI) using `StateCategory`, not hardcoded state names.
3. **Best-effort, non-blocking** — Parent propagation failures must never prevent
   the child state transition from succeeding.
4. **Idempotent** — If the parent is already `InProgress` or beyond, no-op silently.
5. **All entry points** — Cover CLI (`flow-start`, `state`) and MCP (`twig_state`).

### Non-Goals

- **Recursive propagation** — Only propagate one level up (child → parent), not
  grandparent and beyond. If deeper propagation is needed, it's a separate feature.
- **Downward propagation** — Do not cascade state changes from parent to children.
- **Completion propagation** — Automatic parent → Done when all children complete is
  out of scope (the existing hint approach is sufficient for now).
- **Configurable on/off** — Not adding a config toggle in this iteration; the feature
  is always-on. If users report issues, a future config flag can disable it.

---

## Requirements

### Functional

1. **FR-1**: When a child work item transitions to `StateCategory.InProgress` and its
   parent is in `StateCategory.Proposed`, the parent must be automatically transitioned
   to the first `InProgress` state for its type.
2. **FR-2**: The propagation must occur in all three state-change entry points:
   `FlowStartCommand`, `StateCommand`, and MCP `MutationTools.State()`.
3. **FR-3**: If the parent is already in `InProgress`, `Resolved`, `Completed`, or
   `Removed`, no propagation occurs.
4. **FR-4**: Propagation is best-effort — failures are logged to stderr (CLI) or
   silently absorbed (MCP) but never affect the child command's exit code.
5. **FR-5**: After successful parent propagation, the parent item in the local cache
   must be updated to reflect the new state.

### Non-Functional

1. **NFR-1**: No additional ADO API calls when the parent is not in `Proposed` state
   (short-circuit using cached parent from local DB).
2. **NFR-2**: The service must be stateless and testable via NSubstitute mocks.
3. **NFR-3**: No hardcoded state names — all resolution via `StateCategory` enum.
4. **NFR-4**: Parent propagation must add ≤1 ADO round-trip (Fetch + Patch) when triggered.

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│  CLI Layer (Twig)                                            │
│  ┌──────────────────┐  ┌─────────────┐  ┌─────────────────┐ │
│  │ FlowStartCommand │  │StateCommand │  │ (output/hints)  │ │
│  └────────┬─────────┘  └──────┬──────┘  └─────────────────┘ │
│           │                   │                              │
│           └───────┬───────────┘                              │
│                   ▼                                          │
│  ┌────────────────────────────────────────┐                  │
│  │  ParentStatePropagationService         │ ◄── NEW          │
│  │  (Domain layer)                        │                  │
│  │  - TryPropagateToParentAsync()         │                  │
│  └────────────────┬───────────────────────┘                  │
│                   │                                          │
│                   ▼                                          │
│  ┌────────────────────────────────────┐                      │
│  │  FlowTransitionService             │                      │
│  │  (existing — reused for ADO patch) │                      │
│  └────────────────────────────────────┘                      │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│  MCP Layer (Twig.Mcp)                                        │
│  ┌──────────────┐                                            │
│  │MutationTools │ ── calls ──▶ ParentStatePropagationService │
│  └──────────────┘                                            │
└──────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `ParentStatePropagationService` (NEW — Domain layer)

**Location**: `src/Twig.Domain/Services/ParentStatePropagationService.cs`

**Responsibility**: Given a child work item that just transitioned to `InProgress`,
check whether its parent needs to be activated and, if so, perform the transition.

```csharp
public sealed class ParentStatePropagationService
{
    // Dependencies: IWorkItemRepository, IAdoWorkItemService,
    //   IProcessConfigurationProvider, ProtectedCacheWriter

    /// <summary>
    /// If the child's parent exists and is still in Proposed state,
    /// transitions the parent to InProgress. Best-effort — never throws.
    /// Returns a result indicating what happened.
    /// </summary>
    public async Task<ParentPropagationResult> TryPropagateToParentAsync(
        WorkItem child,
        StateCategory childNewCategory,
        CancellationToken ct = default);
}
```

**Algorithm**:
1. Guard: `childNewCategory != InProgress` → return `NotApplicable`.
2. Guard: `child.ParentId == null` → return `NoParent`.
3. Fetch parent from cache: `IWorkItemRepository.GetByIdAsync(child.ParentId)`.
4. If parent is null (not in cache), try `IAdoWorkItemService.FetchAsync()`.
5. Resolve parent's current `StateCategory` via `StateCategoryResolver.Resolve()`.
6. If parent is already `InProgress` or beyond → return `AlreadyActive`.
7. Resolve parent's `InProgress` state name via `StateResolver.ResolveByCategory()`.
8. Fetch remote parent for current revision, patch state, update cache via
   `ProtectedCacheWriter.SaveProtectedAsync()`.
9. Return `Propagated` with old/new state names.

**Error handling**: The entire method body is wrapped in `try/catch` — any exception
returns `Failed` with the exception message. This guarantees the caller is never
impacted.

#### 2. `ParentPropagationResult` (NEW — Domain layer)

**Location**: `src/Twig.Domain/Services/ParentStatePropagationService.cs` (same file)

A simple sealed record:

```csharp
public sealed record ParentPropagationResult
{
    public ParentPropagationOutcome Outcome { get; init; }
    public string? ParentOldState { get; init; }
    public string? ParentNewState { get; init; }
    public int? ParentId { get; init; }
    public string? Error { get; init; }
}

public enum ParentPropagationOutcome
{
    NotApplicable,
    NoParent,
    AlreadyActive,
    Propagated,
    Failed
}
```

### Data Flow

**Happy path (FlowStartCommand)**:
1. User runs `twig flow-start 1234`
2. `FlowStartCommand` resolves item, transitions child to InProgress
3. After successful child transition → calls
   `parentPropagationService.TryPropagateToParentAsync(child, InProgress)`
4. Service loads parent from cache, sees it's "To Do" (Proposed), resolves
   "Doing" as the InProgress state, patches ADO, updates cache
5. `FlowStartCommand` includes parent transition info in output summary

**Short-circuit path**:
1. User runs `twig state Active` on a Task
2. `StateCommand` patches the child to "Active"
3. Calls `TryPropagateToParentAsync(child, InProgress)`
4. Parent is already "Active" → returns `AlreadyActive` → no ADO call

### Design Decisions

1. **Domain service, not FlowTransitionService extension**: The propagation is a
   distinct concern from the existing `FlowTransitionService` which handles
   single-item transitions. A separate service keeps responsibilities clean and
   avoids bloating an already well-scoped class.

2. **Cache-first parent lookup**: We check the parent state from the local cache
   before making any ADO calls. This avoids unnecessary network round-trips in the
   common case where the parent is already active.

3. **Direct ADO patch, not command queue**: The parent transition uses
   `IAdoWorkItemService.PatchAsync()` directly (like `FlowTransitionService`) rather
   than the `WorkItem.ChangeState()` command queue. This is because the parent is
   not the "active" work item — we don't want to mark it dirty in the queue.

4. **One-level propagation only**: Intentionally limited to parent only, not
   grandparent. This keeps the feature simple, predictable, and avoids cascading
   ADO API calls. If deeper propagation is needed, it can be layered on later.

5. **Result type, not exceptions**: The method returns a result enum so callers can
   optionally report what happened (e.g., output "Parent Epic #500 → Doing") without
   needing try/catch logic at the call site.

---

## Dependencies

### External Dependencies
- None — uses existing ADO REST API surface (`PatchAsync`, `FetchAsync`).

### Internal Dependencies
- `FlowTransitionService` pattern (reused design, not a code dependency)
- `IProcessConfigurationProvider` (existing)
- `IAdoWorkItemService` (existing)
- `IWorkItemRepository` (existing)
- `ProtectedCacheWriter` (existing)
- `StateCategoryResolver` and `StateResolver` (existing)

### Sequencing Constraints
- None — this is a standalone feature with no prerequisite changes.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Parent ADO patch fails (network, 409 conflict) | Medium | Low | Best-effort with try/catch — child operation always succeeds. Warning emitted to stderr. |
| Stale cached parent state causes unnecessary ADO call | Low | Low | ADO `FetchAsync` gets latest revision; the patch is idempotent if parent is already active. |
| Infinite recursion if parent propagation triggers more propagation | Low | High | Service only triggers on `InProgress` category and only operates on the direct parent. No recursive calls. |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should propagation extend beyond one level (e.g., Epic → Feature → User Story chain)? | Low | Decided: No, one level only for now. Can be revisited if users request it. |
| 2 | Should a `display.propagateParentState` config toggle be added? | Low | Decided: Not in this iteration. Feature is always-on. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/ParentStatePropagationService.cs` | Core service: detects when parent needs activation and performs the transition |
| `tests/Twig.Domain.Tests/Services/ParentStatePropagationServiceTests.cs` | Unit tests for the propagation service |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/FlowStartCommand.cs` | Inject `ParentStatePropagationService`, call after child state transition, include parent info in output |
| `src/Twig/Commands/StateCommand.cs` | Inject `ParentStatePropagationService`, call after successful forward transition to InProgress |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Call propagation service after successful state change to InProgress |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `ParentStatePropagationService` singleton |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Pass `ParentStatePropagationService` to `FlowStartCommand` and `StateCommand` constructors |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Add `ParentStatePropagationService` property |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Construct and pass `ParentStatePropagationService` |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | Add tests for parent propagation during flow-start |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Add tests for parent propagation during state change |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsStateTests.cs` | Add tests for parent propagation via MCP |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsTestBase.cs` | Add propagation service setup to test base |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsTestBase.cs` | Wire propagation service in workspace context factory |

---

## ADO Work Item Structure

This task (#1855) is a **Task** under Issue #1845. It is the only implementation
unit — no further Issue/Task decomposition is needed since this is already a leaf
task in the hierarchy.

### Issue #1845: SDLC Closeout Improvements

**Goal**: Implement improvements identified during Epic #1814 closeout.

**Task #1855**: Implement parent state propagation when first task starts.

#### Tasks

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|----------------|--------|
| T-1 | Create `ParentStatePropagationService` with `TryPropagateToParentAsync()` and result types | `src/Twig.Domain/Services/ParentStatePropagationService.cs` | ~80 LoC | TO DO |
| T-2 | Write unit tests for `ParentStatePropagationService` | `tests/Twig.Domain.Tests/Services/ParentStatePropagationServiceTests.cs` | ~200 LoC | TO DO |
| T-3 | Register service in DI and integrate into `FlowStartCommand` | `CommandServiceModule.cs`, `CommandRegistrationModule.cs`, `FlowStartCommand.cs` | ~40 LoC | TO DO |
| T-4 | Integrate into `StateCommand` | `StateCommand.cs` | ~25 LoC | TO DO |
| T-5 | Integrate into MCP `MutationTools.State()` | `MutationTools.cs`, `WorkspaceContext.cs`, `WorkspaceContextFactory.cs` | ~30 LoC | TO DO |
| T-6 | Add integration tests for all three entry points | `FlowStartCommandTests.cs`, `StateCommandTests.cs`, `MutationToolsStateTests.cs`, test bases | ~150 LoC | TO DO |

**Acceptance Criteria**:
- [ ] Running `twig flow-start` on a child whose parent is in Proposed state automatically transitions the parent to InProgress
- [ ] Running `twig state Doing` on a child triggers parent propagation
- [ ] MCP `twig_state` triggers parent propagation
- [ ] Parent already in InProgress or beyond: no-op, no extra ADO calls
- [ ] Parent propagation failure does not affect child command exit code
- [ ] All tests pass: `dotnet test`
- [ ] Build succeeds with `TreatWarningsAsErrors`: `dotnet build`

---

## PR Groups

| PG | Name | Type | Tasks | Estimated LoC | Files | Successor |
|----|------|------|-------|---------------|-------|-----------|
| PG-1 | Core service + all integrations | Deep | T-1, T-2, T-3, T-4, T-5, T-6 | ~525 | ~12 | — |

**Rationale**: This is a single coherent feature with a new service and its integration
points. The total change is well under the 2000 LoC / 50 file limits, so splitting
into multiple PRs would create unnecessary overhead. All changes are tightly coupled
and should be reviewed together for correctness.

---

## References

- ADO Work Item #1855: task_manager: transition Epic to Doing when first task starts
- ADO Work Item #1845: Parent Issue for closeout improvements
- `FlowCloseCommand` child-state verification gate (inverse pattern): `src/Twig/Commands/FlowCloseCommand.cs:119-169`
- `FlowTransitionService.TransitionStateAsync()` (reused pattern): `src/Twig.Domain/Services/FlowTransitionService.cs:69-133`
