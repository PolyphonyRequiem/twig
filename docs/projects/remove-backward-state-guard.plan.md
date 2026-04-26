# Remove Backward State Transition Guard

**Work Item:** #2086
**Type:** Issue
**Status:** 🔨 In Progress
**Revision:** 0

---

## Executive Summary

This plan removes the client-side backward state transition confirmation guard from
the twig CLI and MCP server. Currently, when a user transitions a work item to an
earlier state (e.g., Done → Doing), the CLI prompts for confirmation and the MCP
server requires `force: true`. This duplicates enforcement that ADO itself provides
natively via process rules. By removing the guard, backward transitions will be
treated identically to forward transitions — the CLI sends the PATCH to ADO and lets
ADO accept or reject the transition. The `TransitionKind.Backward` enum value,
`RequiresConfirmation` property, and `force` parameter on the MCP `twig_state` tool
are all eliminated. `TransitionKind.Cut` (transitions to "Removed") is also
simplified — it retains classification but no longer requires client-side
confirmation.

## Background

### Current Architecture

State transitions flow through a layered evaluation pipeline:

1. **`ProcessConfiguration.BuildTypeConfig()`** generates a transition rules
   dictionary mapping `(fromState, toState)` pairs to `TransitionKind` values
   (Forward, Backward, Cut) based on ordinal position in the state list.

2. **`StateTransitionService.Evaluate()`** consults this dictionary and returns a
   `TransitionResult` with `Kind`, `IsAllowed`, `RequiresConfirmation`, and
   `RequiresReason` properties.

3. **Consumers** (StateCommand, BatchCommand, MutationTools.State) check
   `RequiresConfirmation` and either prompt the user (CLI) or require `force: true`
   (MCP).

The guard exists in three independent consumer paths:
- `StateCommand` (CLI `twig state`)
- `BatchCommand` (CLI `twig batch --state`)
- `MutationTools.State` (MCP `twig_state`)

The `FlowTransitionService` does **not** check `RequiresConfirmation` — it
transitions directly by category and bypasses the guard entirely.

### Call-Site Audit

| File | Method/Area | Current Usage | Impact |
|------|-------------|---------------|--------|
| `src/Twig.Domain/Enums/TransitionKind.cs` | `TransitionKind` enum | Defines `Backward = 2` | Remove `Backward` member |
| `src/Twig.Domain/Services/StateTransitionService.cs` | `TransitionResult` record | `RequiresConfirmation`, `RequiresReason` properties | Remove both properties |
| `src/Twig.Domain/Services/StateTransitionService.cs` | `Evaluate()` | Returns different confirmation flags per kind | Simplify: all allowed transitions return same shape |
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | `BuildTypeConfig()` | Generates `TransitionKind.Backward` for j < i | Map to `TransitionKind.Forward` (or remove distinction) |
| `src/Twig/Commands/StateCommand.cs` | `ExecuteAsync()` | Lines 84-94: confirmation prompt for backward/cut | Remove confirmation block |
| `src/Twig/Commands/BatchCommand.cs` | Lines 393-403 | Confirmation prompt for backward/cut (interactive mode) | Remove confirmation block |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `State()` method | `force` parameter; lines 63-65 confirmation gate | Remove `force` parameter and gate |
| `src/Twig.Mcp/Services/Batch/ToolDispatcher.cs` | `DispatchAsync()` | Passes `force` arg to `mutationTools.State()` | Remove `force` arg |
| `src/Twig.Domain/Commands/ChangeStateCommand.cs` | `Confirmation` property | Stores confirmation text for backward/cut | Remove property |
| `src/Twig.Domain/Interfaces/IConsoleInput.cs` | Interface doc | Doc references "backward/cut transitions" | Update doc comment |
| `src/Twig/CommandExamples.cs` | State examples | No backward-specific examples | No change needed |
| `src/Twig.Domain/Services/FlowTransitionService.cs` | `TransitionStateAsync()` | Does NOT check RequiresConfirmation | No change needed |
| `src/Twig.Domain/Services/ParentStatePropagationService.cs` | `TryPropagateToParentAsync()` | Does NOT check RequiresConfirmation | No change needed |
| `.github/copilot-instructions.md` | Lines 173, 181 | References `force` for backward transitions | Update to remove `force` references |
| `docs/architecture/mcp-server.md` | Lines 131–138 | Documents `force` param and confirmation step | Remove `force` from signature; remove step 4 |

### Test Files Affected

| Test File | Impact |
|-----------|--------|
| `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | Remove/update backward confirmation assertions |
| `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | Update backward transition kind assertions |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Remove backward confirmation prompt tests |
| `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs` | Remove backward confirmation tests |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsStateTests.cs` | Remove force/confirmation tests |
| `tests/Twig.Mcp.Tests/Services/Batch/ToolDispatcherTests.cs` | Remove `force` arg from dispatch |

## Problem Statement

The client-side backward transition guard creates three concrete problems:

1. **Duplicated enforcement**: ADO already enforces process rules at the API level.
   If a transition is invalid, the PATCH call returns a 400/409 error. The client
   guard duplicates this with a less accurate heuristic (ordinal position ≠ actual
   process rules).

2. **Friction for legitimate operations**: Reopening a bug, reverting a task, or
   moving work back to an earlier state are common operations. The confirmation
   prompt interrupts CLI workflows and the `force: true` requirement complicates
   MCP agent automation.

3. **Inconsistency**: `FlowTransitionService` already bypasses the guard for
   flow-done and flow-close operations, proving the guard is not uniformly applied
   even within twig itself.

## Goals and Non-Goals

### Goals

- **G1**: Remove all client-side backward transition confirmation prompts from CLI
  commands (`twig state`, `twig batch`)
- **G2**: Remove the `force` parameter from the MCP `twig_state` tool
- **G3**: Simplify `TransitionKind` enum by removing the `Backward` member
- **G4**: Remove `RequiresConfirmation` and `RequiresReason` from `TransitionResult`
- **G5**: Update all tests to reflect the new behavior
- **G6**: Retain `TransitionKind.Cut` as a classification (for potential future UI
  differentiation) but remove its confirmation requirement

### Non-Goals

- **NG1**: Changing how ADO validates transitions server-side
- **NG2**: Removing `TransitionKind` entirely (Forward/Cut classification is still
  useful for rendering hints)
- **NG3**: Modifying `FlowTransitionService` (it already bypasses the guard)
- **NG4**: Adding new error handling for ADO rejection of transitions (existing
  `AdoException` handling already covers this)

## Requirements

### Functional

- **FR1**: `twig state <name>` must apply any valid transition without prompting,
  regardless of direction
- **FR2**: `twig batch --state <name>` must apply transitions without prompting
- **FR3**: MCP `twig_state` must accept `stateName` and `workspace` only — no
  `force` parameter
- **FR4**: Transitions to "Removed" state no longer prompt for confirmation
- **FR5**: Invalid transitions (state not in config, type not configured) still
  return errors

### Non-Functional

- **NFR1**: No new dependencies introduced
- **NFR2**: All existing tests pass (modified as needed)
- **NFR3**: Zero breaking change for valid CLI usage (backward transitions that
  previously required "y" now just work)

## Proposed Design

### Architecture Overview

The change simplifies the state transition pipeline by removing the confirmation
layer. After this change, the pipeline becomes:

```
User → StateResolver (name → full state) → StateTransitionService.Evaluate()
     → [IsAllowed check only] → ADO PATCH → cache resync
```

The `TransitionResult` becomes a simple allowed/not-allowed signal with a `Kind`
for informational purposes only (Forward vs Cut). The confirmation prompt and
`force` parameter are eliminated at every consumer.

### Key Components

#### 1. `TransitionKind` enum (simplified)

```csharp
public enum TransitionKind
{
    None = 0,     // Invalid or disallowed
    Forward = 1,  // Any valid transition toward or away from completion
    Cut = 3       // To Removed — classification only, no confirmation
}
```

`Backward = 2` is removed. All non-Removed transitions become `Forward`. The
`Cut` value retains its numeric value (3) for serialization stability.

#### 2. `TransitionResult` record (simplified)

```csharp
public record TransitionResult
{
    public TransitionKind Kind { get; init; }
    public bool IsAllowed { get; init; }
}
```

`RequiresConfirmation` and `RequiresReason` are removed entirely.

#### 3. `StateTransitionService.Evaluate()` (simplified)

Returns `IsAllowed = true` for all known transitions (Forward or Cut). Returns
`IsAllowed = false` with `Kind = None` for unknown type/state combinations.

#### 4. Consumer changes

- **StateCommand**: Remove lines 84-94 (confirmation prompt block). The
  `IConsoleInput` dependency remains for `ConflictResolutionFlow` but is no longer
  used for state transition confirmation.
- **BatchCommand**: Remove lines 393-403 (confirmation prompt block in
  `ProcessSingleItemAsync`).
- **MutationTools.State**: Remove `force` parameter and the `RequiresConfirmation`
  gate (lines 63-65).
- **ToolDispatcher**: Remove `force` arg from `twig_state` dispatch.

#### 5. `ChangeStateCommand.Confirmation` property

Remove the `Confirmation` property. It was used to pass confirmation text to ADO,
but ADO state changes don't use comment fields from the confirmation prompt — they
use separate note/comment APIs.

### Data Flow (after change)

```
CLI: twig state Done
  → StateResolver.ResolveByName("Done", stateEntries) → "Done"
  → StateTransitionService.Evaluate(config, type, "Doing", "Done")
  → TransitionResult { Kind=Forward, IsAllowed=true }
  → adoService.FetchAsync(id) → remote
  → ConflictResolutionFlow.ResolveAsync(...)
  → ConflictRetryHelper.PatchWithRetryAsync(...)
  → adoService.FetchAsync(id) → updated (resync)
  → workItemRepo.SaveAsync(updated)

MCP: twig_state("Doing") [no force param]
  → same Evaluate() → same result
  → same ADO PATCH path
```

### Design Decisions

1. **Remove `Backward` entirely rather than keep it informational**: The Backward
   kind served only the confirmation gate. With the gate gone, the distinction
   between Forward and Backward has no behavioral effect. Keeping it would be dead
   code. Cut is retained because "Removed" is semantically distinct (destructive
   intent) and may be useful for future UX hints.

2. **Keep `TransitionKind.Cut`**: Even without confirmation, knowing a transition
   goes to "Removed" is useful for hint text, logging, and potential future UI
   styling. The cost of keeping it is negligible.

3. **Remove `ChangeStateCommand.Confirmation`**: This property was populated from
   the confirmation prompt text but never actually sent to ADO in the current
   implementation — ADO state changes use `FieldChange` arrays, not comment
   payloads.

## Dependencies

- No external dependencies
- No infrastructure changes
- No ADO API changes

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none)    |         |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Enums/TransitionKind.cs` | Remove `Backward = 2` member |
| `src/Twig.Domain/Services/StateTransitionService.cs` | Remove `RequiresConfirmation`, `RequiresReason` from `TransitionResult`; simplify `Evaluate()` switch |
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | Change `TransitionKind.Backward` → `TransitionKind.Forward` in `BuildTypeConfig()` |
| `src/Twig.Domain/Commands/ChangeStateCommand.cs` | Remove `Confirmation` property |
| `src/Twig.Domain/Interfaces/IConsoleInput.cs` | Update doc comment |
| `src/Twig/Commands/StateCommand.cs` | Remove confirmation prompt block (lines 84-94) |
| `src/Twig/Commands/BatchCommand.cs` | Remove confirmation prompt block (lines 393-403) |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Remove `force` parameter and confirmation gate from `State()` |
| `src/Twig.Mcp/Services/Batch/ToolDispatcher.cs` | Remove `force` arg from `twig_state` dispatch |
| `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | Update all backward assertions; remove `RequiresConfirmation` checks |
| `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | Change `TransitionKind.Backward` assertions → `TransitionKind.Forward` |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Remove backward/cut confirmation tests; update to assert no prompting |
| `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs` | Remove backward confirmation tests |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsStateTests.cs` | Remove `force` tests; backward transitions succeed without force |
| `tests/Twig.Mcp.Tests/Services/Batch/ToolDispatcherTests.cs` | Remove `force` arg from twig_state dispatch tests |

### Documentation Files

| File Path | Changes |
|-----------|---------|
| `.github/copilot-instructions.md` | Line 173: remove `(supports force for backward transitions)` from `twig_state` description; Line 181: remove `force: true` bypass reference |
| `docs/architecture/mcp-server.md` | Lines 131–138: remove `force` parameter from `twig_state` signature and remove step 4 (confirmation gate) |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none)    |        |

## ADO Work Item Structure

**Parent:** Issue #2086 — Remove twig CLI backward state transition guard

### Task 1: Remove backward transition guard from domain layer

**Goal:** Eliminate `TransitionKind.Backward`, `RequiresConfirmation`,
`RequiresReason`, and `ChangeStateCommand.Confirmation` from the domain.

**Prerequisites:** None

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1.1 | Remove `Backward = 2` from `TransitionKind` enum | `TransitionKind.cs` | S |
| T1.2 | Remove `RequiresConfirmation` and `RequiresReason` from `TransitionResult`; simplify `Evaluate()` switch | `StateTransitionService.cs` | S |
| T1.3 | Change `TransitionKind.Backward` → `TransitionKind.Forward` in `BuildTypeConfig()` | `ProcessConfiguration.cs` | S |
| T1.4 | Remove `Confirmation` property from `ChangeStateCommand` | `ChangeStateCommand.cs` | XS |
| T1.5 | Update `IConsoleInput` doc comment | `IConsoleInput.cs` | XS |

**Acceptance Criteria:**
- [ ] `TransitionKind.Backward` no longer exists
- [ ] `TransitionResult` has only `Kind` and `IsAllowed`
- [ ] Domain layer compiles with zero warnings
- [ ] All domain tests pass

### Task 2: Remove confirmation prompts from CLI commands

**Goal:** Remove the backward/cut confirmation prompt from `StateCommand` and
`BatchCommand`.

**Prerequisites:** Task 1

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T2.1 | Remove confirmation prompt block from `StateCommand.ExecuteAsync()` | `StateCommand.cs` | S |
| T2.2 | Remove confirmation prompt block from `BatchCommand.ProcessSingleItemAsync()` | `BatchCommand.cs` | S |

**Acceptance Criteria:**
- [ ] `twig state` never prompts for confirmation on any transition
- [ ] `twig batch --state` never prompts for confirmation
- [ ] CLI projects compile with zero warnings
- [ ] All CLI tests pass

### Task 3: Remove `force` parameter from MCP tools

**Goal:** Remove the `force` parameter from `MutationTools.State()` and its
dispatch wiring.

**Prerequisites:** Task 1

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T3.1 | Remove `force` parameter and confirmation gate from `MutationTools.State()` | `MutationTools.cs` | S |
| T3.2 | Remove `force` arg from `ToolDispatcher` `twig_state` dispatch | `ToolDispatcher.cs` | XS |

**Acceptance Criteria:**
- [ ] `twig_state` MCP tool accepts only `stateName` and `workspace`
- [ ] MCP projects compile with zero warnings
- [ ] All MCP tests pass

### Task 4: Update all tests

**Goal:** Update test assertions to reflect the removal of backward transition
guards.

**Prerequisites:** Tasks 1, 2, 3

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T4.1 | Update `StateTransitionServiceTests` — change backward assertions to Forward, remove `RequiresConfirmation` assertions | `StateTransitionServiceTests.cs` | M |
| T4.2 | Update `ProcessConfigurationTests` — change `TransitionKind.Backward` assertions to `Forward` | `ProcessConfigurationTests.cs` | M |
| T4.3 | Update `StateCommandTests` — remove backward/cut confirmation tests, add tests verifying no prompt | `StateCommandTests.cs` | M |
| T4.4 | Update `BatchCommandTests` — remove backward confirmation tests | `BatchCommandTests.cs` | S |
| T4.5 | Update `MutationToolsStateTests` — remove `force` tests, verify backward transitions succeed without force | `MutationToolsStateTests.cs` | S |
| T4.6 | Update `ToolDispatcherTests` — remove `force` arg from twig_state dispatch assertions | `ToolDispatcherTests.cs` | XS |

**Acceptance Criteria:**
- [ ] All tests pass with `dotnet test`
- [ ] No test references `TransitionKind.Backward`, `RequiresConfirmation`, or `force`
- [ ] Test coverage for valid transitions (forward, cut) and invalid transitions remains

### Task 5: Update documentation

**Goal:** Update copilot instructions, architecture docs, and skill files to remove
references to the `force` parameter and backward transition confirmation.

**Prerequisites:** Tasks 1, 2, 3

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T5.1 | Remove `force` references from copilot-instructions.md | `.github/copilot-instructions.md` | XS |
| T5.2 | Update mcp-server.md to remove `force` from `twig_state` signature and confirmation step | `docs/architecture/mcp-server.md` | XS |

**Acceptance Criteria:**
- [ ] No documentation references `force` parameter for `twig_state`
- [ ] No documentation references backward transition confirmation prompts

## PR Groups

### PG-1: Domain + CLI + MCP changes (all source code)

**Type:** Deep
**Scope:** All source code changes + documentation (Tasks 1, 2, 3, 5)
**Files:** ~11 source + doc files
**Estimated LoC:** ~80 lines removed, ~15 lines modified ≈ 95 LoC delta
**Successor:** None

Rationale: The source changes are tightly coupled — removing the enum member
cascades to all consumers. Splitting into separate PRs would require temporary
compilation workarounds. As a single PR, the reviewer sees the complete cause-
and-effect chain.

### PG-2: Test updates

**Type:** Wide
**Scope:** All test changes (Task 4)
**Files:** ~6 test files
**Estimated LoC:** ~100 lines removed, ~40 lines modified ≈ 140 LoC delta
**Successor:** PG-1 (test changes depend on source changes)

Rationale: Test changes are mechanical (remove assertions, change enum values)
and can be reviewed separately from the behavioral source changes. Keeping tests
in a separate PR lets the reviewer verify the source change intent first, then
confirm test coverage in a follow-up.

## Open Questions

1. **Low — Should `TransitionKind.Cut` also be removed?** Cut transitions to
   "Removed" are semantically distinct from regular transitions. Retaining `Cut`
   as a classification (without confirmation) preserves the ability to show
   different hint text or UI styling for destructive transitions. Recommendation:
   keep it.

2. **Low — Should the `IConsoleInput` dependency be removed from `StateCommand`?**
   `IConsoleInput` is still needed for `ConflictResolutionFlow`, so it cannot be
   removed from `StateCommand`. It can potentially be removed from `BatchCommand`
   if confirmation was its only use — but `BatchCommand` also uses it for
   interactive mode prompts in conflict resolution. Recommendation: leave as-is.

## References

- ADO REST API — Work Items - Update:
  https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update
- Work item #2086

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | `PG-1-remove-backward-state-guard` | #2086 / T1.1–T1.5, T2.1–T2.2, T3.1–T3.2, T4.1–T4.6, T5.1–T5.2 | None | Deep |

### Execution Order

**Single PR**: All tasks are bundled into one self-contained PR. The original
two-PR split (source vs. tests) is not viable because the test files directly
reference `TransitionKind.Backward`, `RequiresConfirmation`, and the `force`
parameter — all of which are removed in the source changes. Merging PG-1 without
the test updates leaves the test projects unable to compile, violating the
self-containment constraint.

The total scope (~235 LoC delta across ~17 files) is well within the ≤2,000 LoC
and ≤50 file guardrails, making a single coherent PR the correct choice. The
reviewer sees the complete cause-and-effect chain in one pass: enum removal →
service simplification → consumer cleanup → test alignment → doc updates.

### Validation Strategy

**PG-1 — `PG-1-remove-backward-state-guard`**
- `dotnet build` — all projects (`Twig.Domain`, `Twig`, `Twig.Mcp`, all test
  projects) must compile with zero warnings/errors
- `dotnet test` — 100% pass rate required across all test projects
- No source or test file references `TransitionKind.Backward`,
  `RequiresConfirmation`, `RequiresReason`, or `force` parameter on `twig_state`
- Coverage for valid forward transitions, cut transitions, and invalid transitions
  (unknown state, type not configured) must remain present
- Manual smoke test: `twig state <backward-state>` on a real work item transitions
  without prompting
