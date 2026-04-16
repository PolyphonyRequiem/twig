# MCP Server Closeout Findings — Remaining Work Plan

> **Status**: ✅ Done

**Issue:** #1618 — MCP Server (Epic 1484) Closeout Findings
**Parent Epic:** #1484 — MCP Server


## Executive Summary

Issue #1618 tracks five follow-up findings from the MCP Server epic (#1484) closeout review. Codebase analysis reveals that **four of five tasks are already implemented**: branch naming consistency (#1619), worktree-aware close-out (#1621), child-state verification gate (#1622), and `--id` flag on `twig update` (#1633). The sole remaining task is **#1620 — pre-close-out note sync**, which adds an `AutoPushNotesHelper.PushAndClearAsync()` call in `FlowCloseCommand` before the unsaved-changes guard, ensuring pending notes are flushed to ADO before close-out evaluation. This is a surgical, ~30 LoC change to `FlowCloseCommand.cs` with corresponding test additions.

## Background

### Current Architecture

The twig CLI implements a three-stage flow lifecycle:

1. **`flow-start`** — Resolves a work item, sets active context, transitions Proposed → InProgress, assigns to self, creates/checks out a git branch via `BranchNamingService.Generate()`.
2. **`flow-done`** — Flushes pending changes (notes + field edits) via `PendingChangeFlusher`, transitions InProgress → Resolved (with Completed fallback), offers PR creation.
3. **`flow-close`** — Guards unsaved changes and open PRs, verifies child task states, transitions to Completed, handles worktree-aware branch cleanup, clears active context.

Key supporting services:
- **`AutoPushNotesHelper`** — Static helper in `Twig.Infrastructure.Ado`. Pushes pending notes as ADO comments and clears them. Signature: `PushAndClearAsync(int workItemId, IPendingChangeStore, IAdoWorkItemService)`. Used by `UpdateCommand`, `EditCommand`, `StateCommand`, and `MutationTools.State/Update` (MCP).
- **`IPendingChangeStore`** — Interface for reading/writing pending changes. Key methods: `GetDirtyItemIdsAsync()`, `GetChangesAsync()`, `ClearChangesByTypeAsync()`.
- **`PendingChangeRecord`** — Record with `WorkItemId`, `ChangeType` ("note" or "field"), `FieldName`, `OldValue`, `NewValue`.

### Call-Site Audit

`AutoPushNotesHelper.PushAndClearAsync()` is called from every mutation command except `FlowCloseCommand`:

| File | Method / Location | Exception Handling |
|------|-------------------|-------------------|
| `EditCommand.cs` | `ExecuteAsync()`, after `PatchWithRetryAsync` | `catch (Exception ex) when (ex is not OperationCanceledException)` — warns to stderr |
| `StateCommand.cs` | `ExecuteAsync()`, after `PatchWithRetryAsync` | None (unwrapped) — failure propagates to caller |
| `UpdateCommand.cs` | `ExecuteAsync()`, after `PatchWithRetryAsync` | None (unwrapped) — failure propagates to caller |
| `MutationTools.cs` | `State()`, after `PatchWithRetryAsync` | None (unwrapped) — failure propagates to MCP error handler |
| `MutationTools.cs` | `Update()`, after `PatchWithRetryAsync` | None (unwrapped) — failure propagates to MCP error handler |
| **`FlowCloseCommand.cs`** | **Not called** | **N/A — this is the gap** |

### The Gap

`FlowCloseCommand` is the only mutation-adjacent command that does not call `AutoPushNotesHelper.PushAndClearAsync()`.

## Problem Statement

`FlowCloseCommand` checks for dirty items (unsaved changes) and blocks if the target item has pending changes. However, it does not distinguish between notes (additive, conflict-free) and field edits (conflict-prone). When a user has pending notes (staged locally during offline use or via seed workflows), `flow-close` blocks with "unsaved changes" but offers no automatic resolution. This creates unnecessary friction — the user must manually run `twig sync` or discard notes before closing.

## Goals and Non-Goals

### Goals
- **G-1**: Automatically flush pending notes before close-out guards evaluate, eliminating false-positive "unsaved changes" blocks caused by additive notes.
- **G-2**: Ensure note flush failure (network error) does not block close-out — warn and continue; the unsaved-changes guard handles field edits.

### Non-Goals
- Refactoring the flow lifecycle into a single orchestrator.
- Adding note flush to `flow-done` (already handled by `PendingChangeFlusher.FlushAsync()`).
- Adding `--sync` or `--no-sync` flags to `flow-close`.

## Requirements

### Functional
- **FR-1**: `FlowCloseCommand` must call `AutoPushNotesHelper.PushAndClearAsync(targetId, pendingChangeStore, adoService)` between the resolve step (step 1) and the unsaved-changes guard (step 3, formerly step 2).
- **FR-2**: If `AutoPushNotesHelper.PushAndClearAsync()` throws, the exception must be caught (except `OutOfMemoryException`), a warning emitted to stderr, and the flow must continue to the unsaved-changes guard.
- **FR-3**: The note flush must execute regardless of the `--force` flag (notes are always safe to push).

### Non-Functional
- **NFR-1**: New code path must have unit tests with NSubstitute mocks and Shouldly assertions.
- **NFR-2**: Zero telemetry changes.

## Proposed Design

### Architecture Overview

This task modifies a single command (`FlowCloseCommand`) by inserting one new step in its guard sequence. No new components, interfaces, or DI registrations are needed — all required dependencies (`IPendingChangeStore`, `IAdoWorkItemService`) are already injected into the constructor via the primary constructor, and `AutoPushNotesHelper` is a static class accessed via a `using` directive.

#### Post-Change Guard Sequence

After this change, the step numbering in `FlowCloseCommand.ExecuteAsync()` shifts — existing comments labelled steps 2–8 become steps 3–9. Update the inline comments accordingly during implementation.

```
1. Resolve target        — FlowTransitionService.ResolveItemAsync()     [existing]
2. ★ Flush pending notes — AutoPushNotesHelper.PushAndClearAsync()      [NEW — Task #1620]
3. Unsaved-changes guard — Block if dirty items remain after flush       [existing, was step 2]
4. Open PR guard         — Check for active pull requests                [existing, was step 3]
5. Child-state gate      — Verify all children are terminal              [existing, was step 4]
6. State transition      — TransitionStateAsync() to Completed           [existing, was step 5]
7. Branch cleanup        — Worktree-aware checkout + delete              [existing, was step 6]
8. Clear context         — Clear active work item                        [existing, was step 7]
9. Print summary         — Emit output                                   [existing, was step 8]
```

### Key Component: Note Flush Step

**Location**: `FlowCloseCommand.cs`, in `ExecuteAsync()` between the resolve block (`var item = resolveResult.Item!;`) and the unsaved-changes guard (`if (!force) { var dirtyIds = ...`).

**Implementation**:

```csharp
// 2. Flush pending notes — always runs (notes are additive, cannot conflict)
try
{
    await AutoPushNotesHelper.PushAndClearAsync(targetId, pendingChangeStore, adoService);
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine(fmt.FormatInfo(
        $"Could not flush pending notes for #{targetId}: {ex.Message}. Continuing with close-out."));
}
```

**Step renumbering**: After inserting the flush as step 2, update the inline comments on existing steps: `// 2. Guard: unsaved changes` → `// 3. Guard: unsaved changes`, and so on through `// 8. Print summary` → `// 9. Print summary`.

**Design rationale**:
- **Unconditional execution**: The flush runs regardless of `--force`. Notes are always safe to push (additive ADO comments). There's no reason to skip this step even when forcing close-out.
- **Broad exception filter**: The filter `catch (Exception ex) when (ex is not OutOfMemoryException)` is deliberately broader than the only other call site with a filter — `EditCommand`, which uses `catch (Exception ex) when (ex is not OperationCanceledException)`. The remaining call sites (`StateCommand`, `UpdateCommand`, `MutationTools.State`, `MutationTools.Update`) have no exception handling around the `AutoPushNotesHelper` call at all; failures propagate directly to the caller. The broader catch here is deliberate: in `EditCommand`, cancellation should abort the overall command. In `StateCommand`/`UpdateCommand`/MCP, failures propagate because note push follows a successful patch and should surface visibly. In `flow-close`, the note flush is a proactive cleanup step — it should never prevent the close-out flow from proceeding, so even `OperationCanceledException` is caught. The unsaved-changes guard (step 3) still fires on failure, and notes remain in the pending store for retry.
- **No `CancellationToken` forwarding**: `AutoPushNotesHelper.PushAndClearAsync()` does not accept a `CancellationToken` parameter. Cancellation can still propagate from inner HTTP calls, but is caught by the broad filter.
- **Warning output**: Uses `fmt.FormatInfo()` (not `FormatError()`) because this is a non-blocking advisory message. The primary failure path — network errors — is expected in offline scenarios.

**Required import**: `using Twig.Infrastructure.Ado;` must be added to the file's using directives (after `using Twig.Formatters;`). This is the namespace containing `AutoPushNotesHelper`.

## Alternatives Considered

1. **Flushing notes inside `PendingChangeFlusher.FlushAsync()`**: Rejected — `PendingChangeFlusher` is used by `flow-done`, not `flow-close`. Adding the call there would affect unrelated commands and violate single-responsibility; the flush belongs in `flow-close`'s own guard sequence.

No other alternatives were identified. The direct `AutoPushNotesHelper.PushAndClearAsync()` call is the same pattern used by all five existing call sites, making it the natural and idiomatic choice.

## Dependencies

All required dependencies are already available in `FlowCloseCommand`:
- **`IPendingChangeStore`** — injected via primary constructor.
- **`IAdoWorkItemService`** — injected via primary constructor.
- **`OutputFormatterFactory`** — injected via primary constructor; `fmt` is resolved from it at the start of `ExecuteAsync()`.
- **`AutoPushNotesHelper`** — static class, accessed via `using Twig.Infrastructure.Ado;` directive (the only new addition).

The note flush makes the same `IAdoWorkItemService.AddCommentAsync()` HTTP call used by all other mutation commands — network availability is not guaranteed and is handled by try/catch.


## Impact Analysis

### Components Affected
- **`FlowCloseCommand`** — Insert note flush step, add `using` directive.
- **`FlowCloseCommandTests`** — Add test cases for note flush behavior.

### Backward Compatibility
Fully backward compatible — no signature changes; the flush is a no-op when no pending notes exist.

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| OQ-1 | Should `AutoPushNotesHelper` accept a `CancellationToken` for more granular cancellation control? | Low | Deferred — out of scope for this task; the broad catch handles cancellation adequately for the flow-close use case. |

## Files Affected

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/FlowCloseCommand.cs` | Add `using Twig.Infrastructure.Ado;` directive; insert `AutoPushNotesHelper.PushAndClearAsync()` call with try/catch between resolve step and unsaved-changes guard; renumber step comments 2–8 → 3–9 |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | Add tests: (1) note flush success clears notes before unsaved-changes guard evaluates (ordering verified via `Received.InOrder`), (2) note flush network failure warns to stderr and continues to completion, (3) note flush runs even with `--force`, (4) note flush precedes dirty-items check — item with only pending notes succeeds after flush clears them |

## ADO Work Item Structure

### Issue #1618: MCP Server Closeout Findings

**Goal**: Address five gaps found during MCP Server epic closeout review.
**Prerequisites**: None.

#### Tasks

| Task ID | Description | Traces To | Files | Effort | Status |
|---------|-------------|-----------|-------|--------|--------|
| #1620 | Add pre-close-out sync step for pending notes | FR-1, FR-2, FR-3, G-1, G-2 | `FlowCloseCommand.cs`, `FlowCloseCommandTests.cs` | ~30 LoC | TO DO |

#### Acceptance Criteria

**Remaining:**
- [ ] `twig flow-close` flushes pending notes before evaluating unsaved-changes guard
- [ ] `twig flow-close` warns but continues if note flush fails (network error)
- [ ] `twig flow-close` note flush runs even when `--force` is used
- [ ] All new code paths (note flush) have unit tests
- [ ] `dotnet build` succeeds with zero warnings
- [ ] `dotnet test` passes

**Previously Completed:**
- [x] `twig flow-start <id>` generates the same branch name as `twig branch` for the same work item (#1619)
- [x] `twig flow-close` skips branch cleanup in a linked worktree with a warning (#1621)
- [x] `twig flow-close` on an Issue with incomplete child Tasks returns exit 1 (unless `--force`) (#1622)
- [x] `twig flow-close` with `--force` emits a warning about skipping child verification (#1622)
- [x] `twig update --id 1234 System.Title "New title"` updates work item #1234 without changing active context (#1633)

## References

- Original plan: `docs/projects/mcp-server-closeout-findings.plan.md` (Rev 9, Status: Done — but #1620 was not implemented)
- `AutoPushNotesHelper`: `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs`
- `FlowCloseCommand`: `src/Twig/Commands/FlowCloseCommand.cs`
