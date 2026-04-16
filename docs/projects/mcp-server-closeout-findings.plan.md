# MCP Server (Epic 1484) Closeout Findings — Implementation Plan

**Issue:** #1618  
**Parent Epic:** #1603 Follow Up on Closeout Findings  
**Status**: ✅ Done  
**Revision:** Rev 1 — addresses tech/readability review feedback

---

## Executive Summary

This plan addresses five post-implementation findings discovered during the close-out of the MCP Server epic (#1484). Each finding represents a gap in the flow/lifecycle tooling that, left unaddressed, causes inconsistent behavior, silent data loss, or forces multi-step workarounds. The fixes span three layers: (1) branch naming consistency between `BranchCommand` and `FlowStartCommand`, (2) close-out flow hardening (pre-close note sync, worktree awareness, child-state verification gate), and (3) a UX improvement to the `twig update` command allowing explicit work-item targeting without first changing context. Tasks 1620 and 1622 introduce two new DI dependencies (`IAdoWorkItemService`, `IProcessConfigurationProvider`) to `FlowCloseCommand`, growing its constructor from 9 to 11 parameters — a modest but acknowledged increase in responsibility. Total estimated scope is ~350 LoC of production code and ~450 LoC of tests.

---

## Background

### Current Architecture

The twig CLI implements a **flow lifecycle** for work items through three commands:

| Command | Entry Point | Purpose |
|---------|------------|---------|
| `flow-start` | `FlowStartCommand.cs` | Set context → transition Proposed→InProgress → create branch |
| `flow-done` | `FlowDoneCommand.cs` | Flush work tree → transition to Resolved → offer PR |
| `flow-close` | `FlowCloseCommand.cs` | Guard unsaved/PRs → transition to Completed → delete branch → clear context |

These commands share `FlowTransitionService` for state resolution and transition logic. Branch naming uses two related but inconsistent APIs:

- **`BranchNamingService.Generate()`** — full-featured service with `WorkItem` input and type-map support (used by `BranchCommand`)
- **`BranchNameTemplate.Generate()`** — low-level template engine with raw string inputs (used by `FlowStartCommand`)

The `UpdateCommand` resolves work items exclusively via `ActiveItemResolver.GetActiveItemAsync()` — there is no mechanism to target a specific work item by ID.

### Call-Site Audit: Branch Name Generation

| File | Method | Current API | TypeMap Used? |
|------|--------|-------------|---------------|
| `src/Twig/Commands/BranchCommand.cs:53` | `ExecuteAsync` | `BranchNamingService.Generate(item, template, typeMap)` | ✅ Yes |
| `src/Twig/Commands/FlowStartCommand.cs:233` | `ExecuteAsync` | `BranchNameTemplate.Generate(template, id, type, title)` | ❌ No |
| `src/Twig/Hints/HintEngine.cs:226` | `TryDetectBranchHintAsync` | `BranchNameTemplate.ExtractWorkItemId(branchName, branchPattern)` | N/A (extraction, not generation) |

### Call-Site Audit: `AutoPushNotesHelper.PushAndClearAsync()`

| File | Method | Called When |
|------|--------|-------------|
| `src/Twig/Commands/UpdateCommand.cs:113` | `ExecuteAsync` | After successful field update |
| `src/Twig/Commands/StateCommand.cs` | `ExecuteAsync` | After state transition |
| `src/Twig/Commands/EditCommand.cs` | `ExecuteAsync` | After field edit |
| `src/Twig/Commands/FlowCloseCommand.cs` | `ExecuteAsync` | **NOT CALLED** ← gap |
| `src/Twig/Commands/FlowDoneCommand.cs` | `ExecuteAsync` | **NOT CALLED** (flush handles it via PendingChangeFlusher) |

### Call-Site Audit: Active Item Resolution in Mutation Commands

| Command | ID Parameter | Resolution Method |
|---------|-------------|-------------------|
| `state` | None | `ActiveItemResolver.GetActiveItemAsync()` |
| `update` | None | `ActiveItemResolver.GetActiveItemAsync()` |
| `note` | None | `ActiveItemResolver.GetActiveItemAsync()` |
| `flow-start` | `string? idOrPattern` | Pattern or ID + `ActiveItemResolver.ResolveByIdAsync()` |
| `flow-done` | `int? id` | `FlowTransitionService.ResolveItemAsync(id)` |
| `flow-close` | `int? id` | `FlowTransitionService.ResolveItemAsync(id)` |
| `save` | `int? targetId` | Scoped flush by target ID |

---

## Problem Statement

1. **Branch naming inconsistency** — `FlowStartCommand` bypasses `BranchNamingService` and calls `BranchNameTemplate.Generate()` directly with raw `item.Type.Value`, skipping custom type-map resolution. When users configure `git.typeMap` in their workspace config, `twig branch` respects it but `twig flow-start` does not. This produces divergent branch names for the same work item depending on which command created the branch.

2. **Silent note loss on close-out** — `FlowCloseCommand` transitions to Completed and clears context without flushing pending notes. If a user runs `twig note --text "Final summary"` then `twig flow-close`, the note is silently lost because: (a) the pending note is keyed to the work item ID, (b) the context is cleared, and (c) no future operation will flush it.

3. **Worktree-unaware context clearing** — `FlowCloseCommand` calls `contextStore.ClearActiveWorkItemIdAsync()` unconditionally. In a git-worktree setup where multiple worktrees share a `.twig` directory, closing in one worktree clears context for all worktrees. The command also doesn't account for the fact that deleting a branch while it's checked out in a linked worktree requires different handling.

4. **No child-state verification** — `FlowCloseCommand` transitions an Issue to Completed without checking whether child Tasks are in a terminal state. This allows closing Issues with active/in-progress Tasks, creating inconsistent ADO board state that confuses team members.

5. **Forced context switch for field updates** — `twig update` requires the target work item to be the active context. To update a non-active item, users must `twig set <id>` → `twig update <field> <value>` → `twig set <original-id>`, which is a three-step dance that disrupts workflow, especially in scripted scenarios and MCP tool invocations.

---

## Goals and Non-Goals

### Goals

- **G1:** Unify branch naming across `BranchCommand` and `FlowStartCommand` so both respect `git.typeMap` configuration.
- **G2:** Ensure pending notes are flushed to ADO before close-out state transition, preventing silent data loss.
- **G3:** Make `FlowCloseCommand` worktree-aware: detect linked worktrees and handle context/branch cleanup appropriately.
- **G4:** Add a pre-closure verification gate that checks child work items are in a terminal state category before allowing Issue closure.
- **G5:** Add an optional `--id <int>` flag to `twig update` that targets a specific work item without changing context.

### Non-Goals

- **NG1:** Creating a "PR Group Manager" service — the task title references PR group management but the actual finding is branch naming consistency only.
- **NG2:** Adding worktree-scoped context stores — worktree awareness here means detecting and handling worktrees correctly, not building a per-worktree context system.
- **NG3:** Recursive child-state verification — the gate checks immediate children only, not grandchildren.
- **NG4:** Adding `--id` to `twig state` or `twig note` — scope is limited to `twig update` per the finding.
- **NG5:** MCP tool changes — MCP tools are placeholder stubs; these CLI fixes will naturally flow to MCP tools when they're implemented.

---

## Requirements

### Functional Requirements

| ID | Requirement | Task |
|----|-------------|------|
| FR-1 | `FlowStartCommand` must generate branch names using `BranchNamingService.Generate()` | 1619 |
| FR-2 | Branch names from `flow-start` and `branch` must be identical for the same work item | 1619 |
| FR-3 | `FlowCloseCommand` must flush pending notes via `AutoPushNotesHelper` before state transition | 1620 |
| FR-4 | Note flush failure must be logged but must not block closure when `--force` is used | 1620 |
| FR-5 | `FlowCloseCommand` must detect linked worktrees via `GetWorktreeRootAsync()` | 1621 |
| FR-6 | In a linked worktree, branch deletion must check out the default branch before deleting | 1621 |
| FR-7 | Before closing, `FlowCloseCommand` must fetch child items and verify their state categories | 1622 |
| FR-8 | If any child is not in Completed/Removed category, closure must be blocked (unless `--force`) | 1622 |
| FR-9 | `twig update` must accept `--id <int>` to target a specific work item | 1633 |
| FR-10 | When `--id` is provided, `twig update` must not change the active context | 1633 |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | All changes must be AOT-compatible (no reflection, no new `[JsonSerializable]` types) |
| NFR-2 | TreatWarningsAsErrors must pass — no new warnings |
| NFR-3 | Existing tests must continue to pass (no behavioral regressions) |
| NFR-4 | Each task must have ≥90% code coverage for new code paths |

---

## Proposed Design

### Architecture Overview

All five tasks modify existing components — no new services or interfaces are introduced. However, Tasks 1620 and 1622 each add a new DI dependency to `FlowCloseCommand`, growing the constructor from 9 to 11 parameters. No new serialization types or `[JsonSerializable]` entries are required.

### Key Components

#### Task 1619: Branch Naming Fix in FlowStartCommand

**Change:** Replace `BranchNameTemplate.Generate(template, id, type, title)` call at line 233 of `FlowStartCommand.cs` with `BranchNamingService.Generate(item, template, typeMap)`.

**Current code (FlowStartCommand.cs:233-234):**
```csharp
branchName = BranchNameTemplate.Generate(
    config.Git.BranchTemplate, item.Id, item.Type.Value, item.Title);
```

**New code:**
```csharp
branchName = BranchNamingService.Generate(
    item, config.Git.BranchTemplate, config.Git.TypeMap);
```

This is a one-line fix that aligns `flow-start` with the existing `branch` command behavior. `BranchNamingService.Generate()` internally calls `BranchNameTemplate.Generate()` after resolving the type through the configured type map.

#### Task 1620: Pre-Close-Out Note Sync

**Change:** Insert an `AutoPushNotesHelper.PushAndClearAsync()` call in `FlowCloseCommand.ExecuteAsync()` between the unsaved-changes guard (step 2) and the state transition (step 4). The sync targets the resolved work item ID.

**Insertion point:** After line 56 (end of unsaved changes guard), before line 100 (state transition).

**Design decisions:**
- Sync ALL pending notes for the target item, not just notes — consistent with `StateCommand` behavior
- On failure: log warning and continue if `--force`; fail with exit code 1 otherwise
- `IPendingChangeStore` is already injected; `IAdoWorkItemService` is **not** — it must be added to the constructor and `CommandRegistrationModule.cs` factory (growing the constructor from 9 to 10 parameters)
- Track whether notes were synced in the action summary for output

#### Task 1621: Worktree-Aware Close-Out

**Change:** Enhance `FlowCloseCommand` to detect linked worktrees via `gitService.GetWorktreeRootAsync()` and adjust branch cleanup behavior.

**Key behaviors:**
1. Before branch cleanup, call `GetWorktreeRootAsync()` to detect linked worktrees
2. In a linked worktree, the branch being closed may be the worktree's branch — deleting it would corrupt the worktree. Branch deletion is skipped and the user is warned.
3. The worktree itself is **left in place** — removing a worktree is a disruptive git operation that may have uncommitted changes. The user is responsible for `git worktree remove` if desired.
4. Add `worktreeDetected` boolean to the JSON output actions for observability
5. The existing `IsInsideWorkTreeAsync()` check remains — worktree detection is additive

**Design note:** The context store is already path-scoped via `.twig/{org}/{project}/twig.db`. Git worktrees that share the same `.twig` directory share context by design. The worktree awareness here is about safe branch operations, not context isolation (which is NG2).

#### Task 1622: Child-State Verification Gate

**Change:** Add a pre-closure gate in `FlowCloseCommand` that fetches child work items and checks their state categories.

**Insertion point:** After the PR guard (line 98), before state transition (line 100).

**Algorithm:**
1. Fetch children via `IAdoWorkItemService.FetchChildrenAsync(parentId)`
2. For each child, resolve state category via `StateCategoryResolver.Resolve()`
3. If any child is in `Proposed` or `InProgress` category → block closure
4. Report which children are blocking (IDs and states)
5. `--force` bypasses the gate with a warning

**Dependencies added to `FlowCloseCommand`:**
- `IAdoWorkItemService` — **not currently injected** (must be added to constructor and `CommandRegistrationModule.cs`); needed for `FetchChildrenAsync(parentId)`
- `IProcessConfigurationProvider` — **not currently injected** (must be added); needed for `StateCategoryResolver`
- Together with the `IAdoWorkItemService` added by Task 1620, these grow the constructor from 9 to 11 parameters

#### Task 1633: Explicit --id Flag for UpdateCommand

**Change:** Add `int? id` parameter to `UpdateCommand.ExecuteAsync()` and `TwigCommands.Update()`.

**Resolution logic:**
```
if (id is not null)
    → ActiveItemResolver.ResolveByIdAsync(id.Value)
else
    → ActiveItemResolver.GetActiveItemAsync()  // existing behavior
```

**Wire-up in Program.cs:**
```csharp
public async Task<int> Update(
    [Argument] string field,
    [Argument] string? value = null,
    int? id = null,           // ← NEW
    string output = ...,
    string? format = null,
    string? file = null,
    bool stdin = false,
    CancellationToken ct = default)
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Note sync before state transition, not after | After transition + context clear, the note would be orphaned |
| DD-2 | Child gate fetches from ADO, not cache | Cache may be stale; ADO is the source of truth for state |
| DD-3 | `--force` bypasses all new gates | Consistent with existing `--force` semantics in the command |
| DD-4 | No `--id` for `state`/`note` | Scope limited to explicit finding; can be added later if needed |
| DD-5 | Worktree detection is best-effort | Follows DD-007 pattern: git operations are skipped, not errored |
| DD-6 | Use `BranchNamingService` not inline fix | Single responsibility — service already encapsulates the logic |
| DD-7 | HintEngine does not need `BranchNamingService` | HintEngine only calls `ExtractWorkItemId()` for ID detection — it never generates branch names, so the naming inconsistency does not affect it |
| DD-8 | Worktree is left in place on close-out | Removing a git worktree (`git worktree remove`) may discard uncommitted changes; the user is responsible for worktree lifecycle management |

---

## Alternatives Considered

### Branch Naming Fix: Inline Fix vs. Service Delegation

**Option A (rejected):** Apply the type-map lookup inline in `FlowStartCommand` by adding a `ResolveType()` call before calling `BranchNameTemplate.Generate()`. This would fix the immediate issue but duplicate the type resolution logic that `BranchNamingService` already encapsulates.

**Option B (chosen — DD-6):** Replace the `BranchNameTemplate.Generate()` call with `BranchNamingService.Generate()`. This is a single-line change that keeps type resolution in one place and naturally inherits any future enhancements to `BranchNamingService`.

### FlowCloseCommand Constructor Growth: Accept vs. Extract

Adding `IAdoWorkItemService` and `IProcessConfigurationProvider` grows the constructor from 9 to 11 parameters. Two alternatives were considered:

**Option A (rejected):** Extract a `FlowCloseGuardService` that encapsulates the note sync, worktree check, and child-state gates. This would reduce `FlowCloseCommand`'s constructor but introduce a new service class and move orchestration logic away from the command, making the flow harder to follow in a single file.

**Option B (chosen):** Accept the constructor growth. Each gate is a discrete, early-return code block with clear comments. If the command exceeds ~250 LoC, individual gates can be extracted to static helper methods (similar to `AutoPushNotesHelper`) without changing the DI surface.

---

## Dependencies

### External Dependencies
None — all required libraries are already in the project.

### Internal Dependencies
- `BranchNamingService` (existing, no changes)
- `AutoPushNotesHelper` (existing, no changes)
- `FlowTransitionService` (existing, no changes)
- `ActiveItemResolver` (existing, no changes)
- `StateCategoryResolver` (existing, no changes)

### Sequencing Constraints
- Task 1620 (note sync) and Task 1621 (worktree) both modify `FlowCloseCommand` — they should be implemented sequentially to avoid merge conflicts, but either can go first.
- Task 1622 (child gate) also modifies `FlowCloseCommand` and should follow 1620/1621.
- Tasks 1619 and 1633 are independent of all other tasks.

---

## Impact Analysis

### Components Affected

| Component | Changes | Risk |
|-----------|---------|------|
| `FlowStartCommand` | Branch naming API change (1 line) | Low — behavioral alignment |
| `FlowCloseCommand` | Note sync + worktree + child gate (~80 LoC) | Medium — three additions to critical path |
| `UpdateCommand` | ID parameter + resolution (~20 LoC) | Low — additive, no existing behavior changed |
| `TwigCommands` (Program.cs) | Wire `--id` parameter | Low — parameter addition |
| `FlowCloseCommandTests` | New test cases (~200 LoC) | Low — additive |
| `UpdateCommandTests` | New test cases (~100 LoC) | Low — additive |

### Backward Compatibility
All changes are backward-compatible:
- Existing command signatures retain default parameter values
- No existing parameters change meaning
- `--force` continues to bypass all gates
- Default behavior (no `--id`) is unchanged

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `FlowCloseCommand` accumulates too many responsibilities | Medium | Medium | Each gate is a discrete block with early-return; extract to helper methods if > 250 LoC |
| Child-state gate adds latency (ADO round-trip) | Low | Low | `FetchChildrenAsync` is a single WIQL query; consistent with flow-done which also fetches children |
| Worktree detection fails on unusual git configurations | Low | Low | DD-007 pattern: wrapped in try/catch, skipped on error |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should the child-state gate apply only to `flow-close` or also to `flow-done`? | Low | Scoped to `flow-close` per finding; `flow-done` transitions to Resolved which is less terminal |

> **Resolved:** ~~Should `HintEngine` branch name display also use `BranchNamingService`?~~ — Moot. Investigation confirmed HintEngine only calls `BranchNameTemplate.ExtractWorkItemId()` for passive ID detection (line 226). It never generates or displays branch names, so the naming inconsistency does not affect it. Promoted to DD-7.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | All changes modify existing files |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/FlowStartCommand.cs` | Replace `BranchNameTemplate.Generate()` with `BranchNamingService.Generate()` (Task 1619) |
| `src/Twig/Commands/FlowCloseCommand.cs` | Add note sync step (1620), worktree detection (1621), child-state gate (1622) |
| `src/Twig/Commands/UpdateCommand.cs` | Add `int? id` parameter, dual-path resolution logic (Task 1633) |
| `src/Twig/Program.cs` | Wire `--id` parameter to `TwigCommands.Update()` (Task 1633) |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Add `IAdoWorkItemService` (Task 1620) and `IProcessConfigurationProvider` (Task 1622) to `FlowCloseCommand` factory |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | Add test for type-map branch naming consistency (Task 1619) |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | Add tests for note sync, worktree, child-state gate (Tasks 1620-1622) |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Add tests for `--id` parameter (Task 1633) |

---

## ADO Work Item Structure

**Input:** Issue #1618 (parent: Epic #1603)

All tasks are defined under Issue #1618 directly.

### Task 1619 — Enforce Branch Naming Consistency in FlowStartCommand

**Goal:** Align `FlowStartCommand` branch naming with `BranchCommand` by using `BranchNamingService.Generate()` instead of `BranchNameTemplate.Generate()`.

**Prerequisites:** None

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T-1619.1 | Replace `BranchNameTemplate.Generate()` with `BranchNamingService.Generate()` in `FlowStartCommand.ExecuteAsync()` at line 233 | `src/Twig/Commands/FlowStartCommand.cs` | ~5 LoC | DONE |
| T-1619.2 | Add test: branch name from `flow-start` matches branch name from `branch` for same work item with custom type map | `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | ~40 LoC | DONE |

**Acceptance Criteria:**
- [x] `FlowStartCommand` uses `BranchNamingService.Generate()` with config type map
- [x] Test proves identical branch names for both code paths with custom type maps
- [x] Existing `FlowStartCommand` tests pass unchanged

---

### Task 1620 — Add Pre-Close-Out Sync Step for Pending Notes

**Goal:** Flush pending notes to ADO before `FlowCloseCommand` transitions state to Completed, preventing silent note loss.

**Prerequisites:** None

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T-1620.1 | Insert `AutoPushNotesHelper.PushAndClearAsync()` call between unsaved-changes guard and state transition; add `IAdoWorkItemService` to constructor (not currently injected) and wire in `CommandRegistrationModule.cs` | `src/Twig/Commands/FlowCloseCommand.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | ~30 LoC | DONE |
| T-1620.2 | Add note-sync tracking to action summary and JSON output | `src/Twig/Commands/FlowCloseCommand.cs` | ~15 LoC | DONE |
| T-1620.3 | Add tests: notes flushed before close, flush failure blocks unless --force, no notes = no-op | `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | ~80 LoC | DONE |

**Acceptance Criteria:**
- [x] Pending notes are pushed to ADO before state transition
- [x] Note sync failure returns exit code 1 (without `--force`)
- [x] `--force` bypasses note sync failure
- [x] JSON output includes `notesSynced` action
- [x] No-op when no pending notes exist

---

### Task 1621 — Add Worktree-Aware Close-Out Flow

**Goal:** Detect git linked worktrees during close-out and handle branch cleanup safely.

**Prerequisites:** None (can be implemented in parallel with 1620)

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T-1621.1 | Add `GetWorktreeRootAsync()` detection before branch cleanup in `FlowCloseCommand` | `src/Twig/Commands/FlowCloseCommand.cs` | ~15 LoC | DONE |
| T-1621.2 | When in a linked worktree, skip branch deletion and warn user; add `worktreeDetected` field to JSON output | `src/Twig/Commands/FlowCloseCommand.cs` | ~20 LoC | DONE |
| T-1621.3 | Add tests: worktree detected → skip branch delete with warning, worktree not detected → existing behavior | `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | ~60 LoC | DONE |

**Acceptance Criteria:**
- [x] Linked worktree is detected via `GetWorktreeRootAsync()`
- [x] Branch deletion is skipped in linked worktrees with informative message
- [x] Non-worktree behavior is unchanged
- [x] JSON output reports worktree detection

---

### Task 1622 — Add Task-Level State Verification Gate Before Issue Closure

**Goal:** Prevent closing an Issue when child work items are still in non-terminal states (Proposed, InProgress).

**Prerequisites:** Task 1620 (note sync goes first in the command flow)

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T-1622.1 | Implement child-state verification gate: fetch children via `FetchChildrenAsync(parentId)`, check state categories, block if non-terminal; add `IProcessConfigurationProvider` to constructor (not currently injected) and wire in `CommandRegistrationModule.cs` | `src/Twig/Commands/FlowCloseCommand.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | ~50 LoC | DONE |
| T-1622.2 | Add `--force` bypass with warning for non-terminal children | `src/Twig/Commands/FlowCloseCommand.cs` | ~10 LoC | DONE |
| T-1622.3 | Add tests: all children completed → pass, child in Proposed → block, child in InProgress → block, --force bypasses, no children → pass | `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | ~120 LoC | DONE |

**Acceptance Criteria:**
- [x] Closure blocked with clear error when children are in non-terminal states
- [x] Error message lists the specific blocking children (IDs and states)
- [x] `--force` bypasses gate with warning
- [x] Items with no children pass the gate silently
- [x] Items with all children in Completed/Removed pass the gate

---

### Task 1633 — Add Explicit Work-Item ID Flag to `twig update` Command

**Goal:** Allow `twig update --id <int> <field> <value>` to target a specific work item without changing active context.

**Prerequisites:** None

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T-1633.1 | Add `int? id` parameter to `UpdateCommand.ExecuteAsync()` and implement dual-path resolution | `src/Twig/Commands/UpdateCommand.cs` | ~15 LoC | DONE |
| T-1633.2 | Wire `--id` parameter through `TwigCommands.Update()` in `Program.cs` | `src/Twig/Program.cs` | ~5 LoC | DONE |
| T-1633.3 | Add tests: `--id` targets specific item, no `--id` uses active context, `--id` with invalid ID returns error | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | ~80 LoC | DONE |

**Acceptance Criteria:**
- [x] `twig update --id 1234 System.Title "New Title"` updates item #1234
- [x] Active context is NOT changed when `--id` is provided
- [x] Omitting `--id` preserves existing behavior (uses active context)
- [x] Invalid/missing ID returns exit code 1 with clear error message

---

## PR Groups

PR groups cluster work items for reviewable pull requests. These are sized for a single, focused review.

### PG-1: Branch Naming + Update ID Flag (wide)

**Type:** Wide — multiple files, mechanical changes  
**Tasks:** T-1619.1, T-1619.2, T-1633.1, T-1633.2, T-1633.3  
**Estimated LoC:** ~145  
**Files:** 4 production, 2 test  
**Rationale:** Both are independent, small changes that don't touch `FlowCloseCommand`. Grouping avoids two tiny PRs.  
**Successors:** None (independent of PG-2)

### PG-2: Close-Out Flow Hardening (deep)

**Type:** Deep — concentrated in `FlowCloseCommand.cs` with complex logic  
**Tasks:** T-1620.1–T-1620.3, T-1621.1–T-1621.3, T-1622.1–T-1622.3  
**Estimated LoC:** ~390  
**Files:** 3 production, 1 test  
**Rationale:** All three tasks modify `FlowCloseCommand` — reviewing them together shows the full picture of the enhanced close-out flow. Sequential within the PR: note sync → worktree awareness → child-state gate (ordered by where they appear in the command flow).  
**Successors:** None (independent of PG-1)

### Execution Order

PG-1 and PG-2 can be developed and merged in parallel — they have zero file overlap (except potentially `CommandRegistrationModule.cs` for DI registration, which is a trivial merge).

---

## References

- [MCP Server Plan](./twig-mcp-server.plan.md) — original MCP server implementation plan
- `FlowCloseCommand.cs` — primary file for Tasks 1620-1622
- `FlowStartCommand.cs:233` — the specific line with the branch naming inconsistency
- `BranchNamingService.cs` — the correct API to use for branch naming
- `AutoPushNotesHelper.cs` — existing note push helper
- ADO Work Item: #1618 — MCP Server Closeout Findings (parent: Epic #1603)

