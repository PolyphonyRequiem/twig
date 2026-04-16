# Child-State Verification Gate — Implementation Plan

**Issue:** #1618 — MCP Server (Epic 1484) Closeout Findings
**Focused Task:** #1622 — Add task-level state verification gate before Issue closure
**Parent Plan:** [`mcp-server-closeout-findings.plan.md`](mcp-server-closeout-findings.plan.md) (Rev 9)
> **Status**: 🔨 In Progress

---

## Executive Summary

This plan documents the implementation of task #1622: a child-state verification gate in `FlowCloseCommand` that prevents premature Issue closure when child Tasks remain in non-terminal states. The gate fetches children via a cache-first/ADO-fallback strategy, resolves each child's state to a `StateCategory` using the process-agnostic `StateCategoryResolver`, and blocks closure unless all children are in a terminal category (Completed, Resolved, or Removed). The `--force` flag bypasses the gate. The implementation is complete on branch `feature/1620-1621-1622-flow-close-hardening` with 8 dedicated unit tests, builds with zero warnings, and all 26 FlowCloseCommand tests pass. This is the final task under Issue #1618; four sibling tasks (#1619, #1620, #1621, #1633) are already Done.

## Background

### Current State

The `FlowCloseCommand` implements `twig flow-close`: an 8-step command that guards unsaved changes, checks for open PRs, verifies child state, transitions the target work item to Completed, optionally deletes the feature branch, and clears the active context. Prior to this task, `flow-close` had only two guards (unsaved changes and open PRs); it would transition any work item to Completed without verifying that child items were finished.

The sibling tasks on Issue #1618 addressed four other gaps found during the MCP Server epic (#1484) closeout review:

| Task | Description | Status |
|------|-------------|--------|
| #1619 | Enforce branch naming consistency — `FlowStartCommand` uses `BranchNamingService.Generate()` | ✅ Done |
| #1620 | Add pre-close-out sync step for pending notes | ✅ Done |
| #1621 | Add worktree-aware close-out flow — skip branch cleanup in linked worktrees | ✅ Done |
| #1633 | Add explicit `--id` flag to `twig update` command | ✅ Done |
| **#1622** | **Add task-level state verification gate before Issue closure** | **🟡 Doing** |

### Relevant Architecture

**Guard sequence in `FlowCloseCommand.ExecuteAsync()`** (post-implementation):

| Step | Guard | `--force` Bypass | Exit Code |
|------|-------|------------------|-----------|
| 1 | Resolve target via `FlowTransitionService.ResolveItemAsync()` | No | 1 on error |
| 2 | Unsaved-changes guard (`IPendingChangeStore`) | Yes | 1 |
| 3 | Open PR guard (`IAdoGitService`) | Yes | 2 (non-TTY), 0 (user declines) |
| **4** | **Child-state verification gate** ← **this task** | **Yes** | **1** |
| 5 | State transition to Completed (`FlowTransitionService`) | No | — |
| 6 | Branch cleanup (worktree-aware) | `--noBranchCleanup` | — |
| 7 | Clear context | No | — |
| 8 | Print summary | No | 0 |

**Key supporting services used by the gate:**

- **`IWorkItemRepository.GetChildrenAsync(parentId)`** — SQLite cache query (`WHERE parent_id = @parentId`)
- **`IAdoWorkItemService.FetchChildrenAsync(parentId)`** — ADO WIQL query + batch fetch (fallback when cache is empty)
- **`IProcessConfigurationProvider.GetConfiguration()`** — Returns `ProcessConfiguration` with `TypeConfigs` dictionary mapping `WorkItemType` → `TypeConfig` (containing `StateEntries`)
- **`StateCategoryResolver.Resolve(state, entries)`** — Maps state name → `StateCategory` via authoritative `StateEntry` list with hardcoded fallback heuristics

## Problem Statement

When closing an Issue-level work item with `twig flow-close`, the command transitions it to Completed without verifying that all child Tasks are finished. This creates an inconsistent ADO board state: a "Closed" Issue can have "Active" or "New" Tasks underneath it. Users must manually verify child states before closing, which is error-prone — especially for AI agents using the CLI programmatically.

The gate must be:
- **Process-agnostic** — No hardcoded state names; uses `StateCategory` from `IProcessConfigurationProvider`
- **Offline-tolerant** — Cache-first resolution; ADO fallback only on cache miss
- **Bypassable** — `--force` skips the check for power users who know what they're doing
- **Informative** — Lists each incomplete child with ID, title, and current state

## Goals and Non-Goals

### Goals
- **G-1**: Prevent premature Issue closure by verifying all child Tasks are in a terminal `StateCategory` (Completed, Resolved, or Removed) before transitioning.
- **G-2**: Provide clear, actionable error output listing each incomplete child item when the gate blocks.
- **G-3**: Support `--force` bypass for the gate, consistent with existing guard bypass behavior.
- **G-4**: Maintain offline-tolerance via cache-first child resolution with ADO fallback.
- **G-5**: Achieve full unit test coverage of the gate (happy path, failure modes, force bypass, cache miss, edge cases).

### Non-Goals
- Recursive/deep child verification (only direct children are checked).
- Filtering by child type (all children are checked regardless of `WorkItemType`).
- Interactive TTY prompt for the child gate (unlike the open-PR guard, which prompts in TTY mode).
- Changes to `FlowDoneCommand` or other flow commands (child verification is specific to `flow-close`).
- Telemetry for the child verification gate.

## Requirements

### Functional
- **FR-1**: `FlowCloseCommand` must, when the target item has children, fetch child items and verify all are in a terminal `StateCategory` (Completed, Resolved, or Removed).
- **FR-2**: If any child is not in a terminal state, block closure and return exit code 1 with a per-child listing.
- **FR-3**: `--force` must bypass the child-state verification gate entirely.
- **FR-4**: When `--force` bypasses the gate, emit an informational message to stderr for audit trail.
- **FR-5**: When the local cache has no children, fall back to `IAdoWorkItemService.FetchChildrenAsync()`.
- **FR-6**: When both cache and ADO fallback fail (network error), return exit code 1 with a suggestion to use `--force`.
- **FR-7**: Children whose `WorkItemType` is not found in `ProcessConfiguration.TypeConfigs` must be treated as non-terminal (conservative/safe default).

### Non-Functional
- **NFR-1**: AOT-compatible — no reflection; all types use source-gen JSON.
- **NFR-2**: Unit tests use xUnit + NSubstitute + Shouldly, consistent with existing test conventions.
- **NFR-3**: Zero telemetry changes (no new telemetry properties emitted).
- **NFR-4**: Backward compatible — existing `flow-close` invocations without `--force` continue to work; the gate is additive.
- **NFR-5**: TreatWarningsAsErrors — zero compiler warnings.

## Proposed Design

### Architecture Overview

The child-state verification gate is a new guard step (step 4) inserted into `FlowCloseCommand.ExecuteAsync()` between the open-PR guard (step 3) and the state transition (step 5). It follows the same procedural guard pattern used by the existing unsaved-changes and open-PR guards: a conditional block gated on `!force` that returns a non-zero exit code when the check fails.

No new services, interfaces, or domain types are introduced. The gate composes three existing services:

```
FlowCloseCommand.ExecuteAsync()
  Step 4: Child-State Verification Gate
  ├─ IWorkItemRepository.GetChildrenAsync(targetId)     ← cache tier
  │   └─ (if empty) IAdoWorkItemService.FetchChildrenAsync(targetId) ← ADO tier
  ├─ IProcessConfigurationProvider.GetConfiguration()   ← type metadata
  │   └─ ProcessConfiguration.TypeConfigs[child.Type]   ← state entries
  └─ StateCategoryResolver.Resolve(child.State, entries) ← category check
      └─ Terminal? Completed | Resolved | Removed
```

### Key Components

#### Child Resolution Strategy

**Cache-first, ADO-fallback**: Try `IWorkItemRepository.GetChildrenAsync(targetId)` first (local SQLite). If the result is empty (`Count == 0`), fall back to `IAdoWorkItemService.FetchChildrenAsync(targetId)` which issues a WIQL query against ADO.

**Known limitation**: If the cache holds a *partial* subset of children (e.g., 3 of 5 Tasks were cached), the gate evaluates only the cached subset. Uncached children in non-terminal states would not be detected. This is an accepted trade-off — the alternative (always fetching from ADO) would make `flow-close` fail offline even with a warm cache. In practice, `twig refresh` and `twig tree` populate the full child set.

**Exception handling**: The ADO fallback uses `when (ex is not OutOfMemoryException and not OperationCanceledException)` — `OperationCanceledException` propagates immediately because cancellation should abort the command, while network errors are caught and reported as exit code 1.

#### Terminal State Logic

For each child:
1. Look up `child.Type` in `ProcessConfiguration.TypeConfigs`
2. If found, call `StateCategoryResolver.Resolve(child.State, typeConfig.StateEntries)`
3. If the resolved category is `Completed`, `Resolved`, or `Removed` → terminal (passes)
4. If the type is not found in `TypeConfigs` → treated as **non-terminal** (conservative)
5. If the resolved category is `Proposed`, `InProgress`, or `Unknown` → non-terminal (blocks)

#### Implementation (as committed)

```csharp
// Step 4: Child-state verification gate
if (!force)
{
    var children = await workItemRepo.GetChildrenAsync(targetId, ct);
    if (children.Count == 0)
    {
        try
        {
            children = await adoService.FetchChildrenAsync(targetId, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
                                    and not OperationCanceledException)
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Could not fetch children for #{targetId}: {ex.Message}. "
                + "Use --force to skip child verification."));
            return 1;
        }
    }

    if (children.Count > 0)
    {
        var processConfig = processConfigProvider.GetConfiguration();
        var incomplete = children.Where(child =>
            !processConfig.TypeConfigs.TryGetValue(child.Type, out var cfg) ||
            StateCategoryResolver.Resolve(child.State, cfg.StateEntries)
                is not (StateCategory.Completed
                     or StateCategory.Resolved
                     or StateCategory.Removed))
            .ToList();

        if (incomplete.Count > 0)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Cannot close #{targetId}: {incomplete.Count} child item(s) "
                + "not in terminal state."));
            foreach (var c in incomplete)
                Console.Error.WriteLine(fmt.FormatInfo(
                    $"  #{c.Id} {c.Title} [{c.State}]"));
            return 1;
        }
    }
}
else
{
    Console.Error.WriteLine(fmt.FormatInfo(
        $"Skipping child state verification for #{targetId} (--force)."));
}
```

#### Constructor Changes

Three new constructor dependencies were added to `FlowCloseCommand`:

```csharp
public sealed class FlowCloseCommand(
    // ... existing 6 parameters ...
    IWorkItemRepository workItemRepo,              // NEW — cache-tier child fetch
    IAdoWorkItemService adoService,                // NEW — ADO-tier child fetch
    IProcessConfigurationProvider processConfigProvider, // NEW — state metadata
    // ... existing 3 optional parameters ...
)
```

The DI factory in `CommandRegistrationModule.cs` was updated to resolve these three additional services.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **DD-1**: Cache-first, ADO-fallback child resolution | Avoids network calls when cache is warm; fails gracefully offline with `--force` escape hatch. |
| **DD-2**: Terminal = Completed ∪ Resolved ∪ Removed | Resolved and Removed items are finished work. Only Proposed and InProgress indicate unfinished work. |
| **DD-3**: Unmapped types → non-terminal (conservative) | Surfaces unmapped/custom types rather than silently ignoring them. Users can `--force` if needed. |
| **DD-4**: `--force` bypasses entire gate | Consistent with existing guard pattern — `--force` is a general "skip all guards" flag, not per-guard. |
| **DD-5**: Gate lives in command, not FlowTransitionService | The gate is a pre-condition specific to `flow-close`. Placing it in `FlowTransitionService` would affect `flow-done` or require a conditional flag (code smell). |
| **DD-6**: No TTY prompt for child gate (unlike PR guard) | Incomplete children require deliberate action (closing them). A "Continue anyway?" prompt doesn't add value — `--force` is the explicit override. |

## Dependencies

### Internal
- `IWorkItemRepository` (existing) — No interface changes needed
- `IAdoWorkItemService` (existing) — No interface changes needed
- `IProcessConfigurationProvider` (existing) — No interface changes needed
- `StateCategoryResolver` (existing) — No API changes needed
- `CommandRegistrationModule` — DI factory updated for new constructor parameters

### Sequencing
- Tasks #1619, #1620, #1621, #1633 are prerequisites (all Done)
- This task (#1622) is the final implementation task under Issue #1618

## Impact Analysis

### Components Affected
- **`FlowCloseCommand.cs`** — 3 new constructor deps, new guard step (44 lines of gate logic)
- **`CommandRegistrationModule.cs`** — 3 additional `GetRequiredService<T>()` calls in DI factory
- **`FlowCloseCommandTests.cs`** — 8 new tests + updated `CreateCommand()` helper with new parameters

### Backward Compatibility
Fully backward compatible. The gate is additive — existing `flow-close` invocations continue to work. Items without children pass through the gate (no-op). `--force` remains the universal guard bypass.

### Performance
Minimal impact. Cache-tier lookup is a single SQLite query (indexed on `parent_id`). ADO fallback only fires on cache miss. No additional network calls when the cache is warm.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Stale partial cache passes verification gate | Low | Medium | Accepted trade-off. `twig refresh` and `twig tree` populate full child sets. Alternative (always-ADO) breaks offline. |
| Offline + cold cache blocks close-out | Low | Medium | Error message explicitly suggests `--force`. This is correct behavior — closing with unverified children is riskier. |
| Constructor parameter count (12) | Medium | Low | Functional and all parameters are genuinely needed. Builder/options refactor is future tech debt, not blocking. |

## Files Affected

### New Files

None.

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/FlowCloseCommand.cs` | +3 constructor deps (`IWorkItemRepository`, `IAdoWorkItemService`, `IProcessConfigurationProvider`); +44 lines for child-state verification gate (step 4); restructured PR guard to hoist `isInWorkTree` for reuse; added `using Twig.Domain.Aggregates` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | +3 `GetRequiredService<T>()` calls in `FlowCloseCommand` DI factory lambda |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | +8 child-gate tests; updated `CreateCommand()` helper with 3 new mock parameters; added `CreateTaskItem()` factory method |
| `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | +1 line: updated `FlowCloseCommand` instantiation with new parameters |

## ADO Work Item Structure

### Issue #1618: MCP Server Closeout Findings

**Goal**: Address five gaps found during MCP Server epic (#1484) closeout review.
**Prerequisites**: None.

#### Tasks

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| #1619 | Enforce branch naming consistency in FlowStartCommand | `FlowStartCommand.cs`, `FlowStartCommandTests.cs` | ~30 LoC | ✅ Done |
| #1620 | Add pre-close-out sync step for pending notes | `FlowCloseCommand.cs`, `FlowCloseCommandTests.cs` | ~70 LoC | ✅ Done |
| #1621 | Add worktree-aware close-out flow | `FlowCloseCommand.cs`, `FlowCloseCommandTests.cs` | ~40 LoC | ✅ Done |
| #1633 | Add explicit work-item ID flag to twig update | `UpdateCommand.cs`, `Program.cs`, `UpdateCommandTests.cs` | ~50 LoC | ✅ Done |
| **#1622** | **Add task-level state verification gate before Issue closure** | **`FlowCloseCommand.cs`, `CommandRegistrationModule.cs`, `FlowCloseCommandTests.cs`** | **~130 LoC** | **🟡 Doing** |

#### Acceptance Criteria

- [x] `twig flow-close` on an Issue with all child Tasks in terminal state succeeds (exit 0)
- [x] `twig flow-close` on an Issue with incomplete child Tasks returns exit 1
- [x] Error output lists each incomplete child with `#{id} {title} [{state}]`
- [x] `twig flow-close --force` bypasses child-state gate with info message
- [x] Cache-miss falls back to ADO `FetchChildrenAsync`
- [x] ADO fallback failure (network error) returns exit 1 with `--force` suggestion
- [x] Unmapped work item types are treated as non-terminal
- [x] Resolved and Removed children pass the gate (not just Completed)
- [x] No children → gate passes (no-op)
- [x] `dotnet build` succeeds with zero warnings
- [x] `dotnet test` — all 26 FlowCloseCommand tests pass
- [x] All new code paths have unit tests (8 dedicated tests)
- [ ] PR merged to main
- [ ] ADO work item #1622 transitioned to Done

## PR Groups

### PG-2: FlowClose hardening (worktree + child verification)
**Type**: Deep (few files, complex logic changes)
**Tasks**: #1621, #1622
**Estimated LoC**: ~170
**Files**: `FlowCloseCommand.cs`, `CommandRegistrationModule.cs`, `FlowCloseCommandTests.cs`, `PromptStateIntegrationTests.cs`
**Branch**: `feature/1620-1621-1622-flow-close-hardening`
**Status**: Implementation complete. 26/26 tests pass. Zero warnings. Pending merge.
**Predecessors**: PG-1 (Done — #1619, #1633)

## Test Coverage Summary

| Test Name | Scenario | Expected |
|-----------|----------|----------|
| `ChildVerification_AllChildrenTerminal_Succeeds` | 2 Tasks in "Closed" state | Exit 0 |
| `ChildVerification_IncompleteChild_ReturnsExit1` | 1 "Closed" + 1 "Active" | Exit 1, context not cleared |
| `ChildVerification_NoChildren_Succeeds` | No children (cache + ADO empty) | Exit 0 |
| `ChildVerification_Force_BypassesGate` | 1 "Active" child + `--force` | Exit 0, `GetChildrenAsync` not called |
| `ChildVerification_CacheMiss_FallsBackToAdo` | Cache empty, ADO returns "Closed" task | Exit 0, `FetchChildrenAsync` called |
| `ChildVerification_CacheMissAndAdoFailure_ReturnsExit1` | Cache empty, ADO throws `HttpRequestException` | Exit 1, context not cleared |
| `ChildVerification_UnmappedType_TreatedAsNonTerminal` | "CustomType" child in "Done" state | Exit 1 (type not in TypeConfigs) |
| `ChildVerification_ResolvedAndRemovedChildrenPass` | Mix of Resolved + Removed + Closed children | Exit 0 |

---

*Rev 1 — Initial draft documenting completed implementation.*
