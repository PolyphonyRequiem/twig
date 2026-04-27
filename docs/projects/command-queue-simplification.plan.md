# Command Queue Pattern Simplification

**Epic:** #2115 — Domain Critique: Command Queue Pattern Simplification
**Status:** Draft
**Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive Summary

The `IWorkItemCommand` queue/apply pattern in `WorkItem` mimics Event Sourcing preparation — commands are enqueued, executed, and produce `FieldChange` records — but delivers none of ES's benefits. Commands are never persisted, never replayed, and every production call site enqueues exactly one command before immediately draining the queue. This plan replaces the command queue with direct mutation methods on `WorkItem` that return `FieldChange` values inline, eliminating the `IWorkItemCommand` interface, three command classes, the internal `Queue<IWorkItemCommand>`, and the temporal coupling between `Execute()` and `ToFieldChange()`. The refactor preserves the `FieldChange` return contract, `IsDirty` tracking, `PendingNote` append behavior, and all existing caller semantics.

---

## Background

### Current Architecture

The `WorkItem` aggregate exposes three enqueue methods — `ChangeState(string)`, `UpdateField(string, string?)`, `AddNote(PendingNote)` — each of which instantiates a command object, pushes it onto a `Queue<IWorkItemCommand>`, and sets `IsDirty = true`. Callers must then call `ApplyCommands()` to drain the queue, which iterates the queue calling `Execute(this)` then `ToFieldChange()` on each command, collecting non-null `FieldChange` results.

The command objects are:

| Command | Execute Effect | ToFieldChange |
|---------|---------------|---------------|
| `ChangeStateCommand` | Captures `_oldState`, sets `target.State` | `FieldChange("System.State", old, new)` |
| `UpdateFieldCommand` | Captures `_oldValue`, sets `target.SetField()` | `FieldChange(fieldName, old, new)` |
| `AddNoteCommand` | Appends to `target.AddPendingNote()` | `null` (notes aren't field changes) |

The pattern has a documented temporal coupling: `ToFieldChange()` on `ChangeStateCommand` and `UpdateFieldCommand` requires `Execute()` to have been called first — otherwise `_oldState`/`_oldValue` is null, producing misleading data.

### Call-Site Audit

Every production call site follows the exact same 3-step sequence: (1) enqueue one command, (2) `ApplyCommands()`, (3) `MarkSynced()` or `SaveAsync()`. No site enqueues more than one command before draining.

#### `ApplyCommands()` — 6 production call sites

| File | Method | Pattern |
|------|--------|---------|
| `src/Twig/Commands/BranchCommand.cs` | Branch creation | `item.ChangeState(newState)` → `ApplyCommands()` → `MarkSynced(newRevision)` |
| `src/Twig/Commands/EditCommand.cs` | `StageLocallyAsync` | `item.UpdateField("_edited", "true")` → `ApplyCommands()` → `SaveAsync(item)` |
| `src/Twig/Commands/FlowStartCommand.cs` | Flow start transition | `item.ChangeState(newState)` → `ApplyCommands()` → `MarkSynced(currentRevision)` |
| `src/Twig/Commands/NoteCommand.cs` | `StageLocallyAsync` | `item.AddNote(...)` → `ApplyCommands()` → `SaveAsync(item)` |
| `src/Twig.Domain/Services/FlowTransitionService.cs` | `TransitionStateAsync` | `item.ChangeState(newState)` → `ApplyCommands()` → `MarkSynced(newRevision)` |
| `src/Twig.Domain/Services/ParentStatePropagationService.cs` | `TryPropagateToParentAsync` | `parent.ChangeState(newState)` → `ApplyCommands()` → `MarkSynced(newRevision)` |

#### `ChangeState()` — 4 production call sites

| File | Context |
|------|---------|
| `BranchCommand.cs` | State transition after branch creation |
| `FlowStartCommand.cs` | Flow start state change |
| `FlowTransitionService.cs` | State transition service |
| `ParentStatePropagationService.cs` | Parent auto-activation |

#### `UpdateField()` — 1 production call site

| File | Context |
|------|---------|
| `EditCommand.cs` | Staging sentinel field `"_edited"` |

#### `AddNote()` — 1 production call site

| File | Context |
|------|---------|
| `NoteCommand.cs` | Staging a pending note locally |

#### `IWorkItemCommand` — 5 structural references (no external consumers)

| File | Context |
|------|---------|
| `WorkItem.cs` | Private queue declaration |
| `IWorkItemCommand.cs` | Interface definition |
| `AddNoteCommand.cs` | Implements interface |
| `ChangeStateCommand.cs` | Implements interface |
| `UpdateFieldCommand.cs` | Implements interface |

No external code ever handles `IWorkItemCommand` directly — it is a purely internal abstraction.

#### Test call sites — `ApplyCommands()` (10 test files)

These test files call `UpdateField()`/`ChangeState()`/`AddNote()` followed by `ApplyCommands()` in setup code to make work items dirty or to test mutation behavior. After the refactor, each `ApplyCommands()` call is simply removed.

| File | Sites | Pattern |
|------|-------|---------|
| `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | ~16 | Core mutation tests (ITEM-040/041/042/044) + guard clause tests referencing `ChangeStateCommand`/`UpdateFieldCommand` directly |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs` | 2 | `UpdateField()` + `ApplyCommands()` to make items dirty in setup |
| `tests/Twig.Cli.Tests/Hints/HintEngineTests.cs` | 3 | `AddNote()`/`UpdateField()` + `ApplyCommands()` in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | 6 | `UpdateField()` + `ApplyCommands()` in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | 2 | `UpdateField()` + `ApplyCommands()` in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/JsonCompactOutputFormatterTests.cs` | 1 | `UpdateField()` + `ApplyCommands()` in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/MinimalOutputFormatterTests.cs` | 2 | `UpdateField()` + `ApplyCommands()` in dirty-item setup |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | 2 | `UpdateField()` + `ApplyCommands()` in dirty-item setup |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | 4 | `UpdateField()` + `ApplyCommands()` in conflict-resolution setup |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | 1 | `ChangeState()` + `ApplyCommands()` in state-change test setup |

**Not affected** (confirmed by grep): `StateCommandTests.cs`, `NoteCommandTests.cs` — these test CLI orchestration that constructs `FieldChange` directly and never touches the command queue.

#### Test files with `using Twig.Domain.Commands` (2 files, excluding deleted command test files)

| File | Usage |
|------|-------|
| `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Guard clause tests directly instantiate `ChangeStateCommand`/`UpdateFieldCommand` — must rewrite to test `WorkItem` methods |

#### Test files to delete (2 files — test deleted types)

| File | Reason |
|------|--------|
| `tests/Twig.Domain.Tests/Commands/ChangeStateCommandTests.cs` | Tests `ChangeStateCommand` which is being deleted |
| `tests/Twig.Domain.Tests/Commands/AddNoteAndUpdateFieldCommandTests.cs` | Tests `AddNoteCommand`/`UpdateFieldCommand` which are being deleted |

---

## Problem Statement

1. **Unnecessary indirection**: The command queue adds four types (`IWorkItemCommand`, `ChangeStateCommand`, `UpdateFieldCommand`, `AddNoteCommand`) and a `Queue<>` field to deliver what direct methods could achieve in ~10 lines each.

2. **Temporal coupling**: `ToFieldChange()` requires `Execute()` to be called first. Calling `ToFieldChange()` before `Execute()` silently produces wrong data (null old values). This is documented but not enforced.

3. **Stateful single-use commands**: `_oldState` and `_oldValue` are captured during `Execute()`, making command instances single-use and order-dependent. This state-capture pattern only matters because of the indirection — a direct method can capture old values in a local variable.

4. **Two-step ceremony**: Every caller must call `Enqueue()` then `ApplyCommands()` for what is semantically a single operation. No caller ever batches multiple commands or defers execution.

5. **Dead architectural promise**: The queue suggests batched/deferred execution, but every production call site immediately drains after enqueuing exactly one command.

---

## Goals and Non-Goals

### Goals

1. **Eliminate the command queue pattern** — remove `IWorkItemCommand`, all three command classes, and the `Queue<IWorkItemCommand>` from `WorkItem`.
2. **Replace with direct mutation methods** — `ChangeState()` returns `FieldChange`, `UpdateField()` returns `FieldChange`, `AddNote()` mutates directly with no return (notes don't produce field changes).
3. **Preserve the `FieldChange` return contract** — all callers that currently use the return value of `ApplyCommands()` must receive equivalent data.
4. **Preserve `IsDirty` tracking** — the dirty flag must continue to be set on mutation and cleared by `MarkSynced()`.
5. **Preserve `PendingNote` behavior** — notes must continue to be appended to the internal list.
6. **Remove `ApplyCommands()`** — callers no longer need a two-step ceremony.
7. **Update all tests** — existing test coverage must be migrated to the new API with equivalent assertions.

### Non-Goals

1. **Restructuring WorkItem beyond command removal** — no changes to seed factory, copy methods, field storage, or identity properties.
2. **Changing caller control flow** — callers should need minimal changes (signature adaptation only, not logic rework).
3. **Modifying the `FieldChange` type** — the value object is unchanged.
4. **Touching MCP tools or CLI commands that don't use the command pattern** — `StateCommand`, `UpdateCommand`, and MCP `MutationTools` construct `FieldChange` directly and bypass the command queue.
5. **Adding new features** — this is a pure simplification refactor.

---

## Requirements

### Functional

- **FR-1**: `WorkItem.ChangeState(string newState)` must mutate `State`, set `IsDirty = true`, and return a `FieldChange("System.State", oldValue, newValue)`.
- **FR-2**: `WorkItem.UpdateField(string fieldName, string? value)` must set the field, set `IsDirty = true`, and return a `FieldChange(fieldName, oldValue, newValue)`.
- **FR-3**: `WorkItem.AddNote(PendingNote note)` must append to `PendingNotes` and set `IsDirty = true`. No return value needed (notes don't produce field changes).
- **FR-4**: `MarkSynced(int revision)` behavior unchanged.
- **FR-5**: Guard clauses preserved — `ChangeState` throws on null/whitespace, `UpdateField` throws on null/whitespace field name.

### Non-Functional

- **NFR-1**: AOT-compatible — no reflection, no dynamic dispatch. Direct methods are inherently simpler for AOT.
- **NFR-2**: Zero behavioral change observable from any caller.
- **NFR-3**: Net reduction in lines of code and type count.

---

## Proposed Design

### Architecture Overview

The refactor collapses the command pattern's four-layer call chain:

```
BEFORE: caller → WorkItem.ChangeState() → new ChangeStateCommand → _commandQueue.Enqueue()
        caller → WorkItem.ApplyCommands() → cmd.Execute(this) → cmd.ToFieldChange()

AFTER:  caller → WorkItem.ChangeState() → mutate State, return FieldChange
```

The `WorkItem` aggregate retains the same three public mutation methods with the same names. Signature changes are minimal:

| Method | Before | After |
|--------|--------|-------|
| `ChangeState(string)` | `void` (enqueues) | `FieldChange` (mutates + returns) |
| `UpdateField(string, string?)` | `void` (enqueues) | `FieldChange` (mutates + returns) |
| `AddNote(PendingNote)` | `void` (enqueues) | `void` (mutates directly) |
| `ApplyCommands()` | `IReadOnlyList<FieldChange>` | **Removed** |

### Key Components

#### `WorkItem` (modified)

The three mutation methods become direct:

```csharp
public FieldChange ChangeState(string newState)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(newState);
    var oldState = State;
    State = newState;
    IsDirty = true;
    return new FieldChange("System.State", oldState, newState);
}

public FieldChange UpdateField(string fieldName, string? value)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
    TryGetField(fieldName, out var oldValue);
    SetField(fieldName, value);
    IsDirty = true;
    return new FieldChange(fieldName, oldValue, value);
}

public void AddNote(PendingNote note)
{
    AddPendingNote(note);
    IsDirty = true;
}
```

Removed:
- `private readonly Queue<IWorkItemCommand> _commandQueue`
- `public IReadOnlyList<FieldChange> ApplyCommands()`
- `using Twig.Domain.Commands;`

#### `Commands/` directory (deleted)

All four files are deleted:
- `IWorkItemCommand.cs`
- `ChangeStateCommand.cs`
- `UpdateFieldCommand.cs`
- `AddNoteCommand.cs`

### Data Flow

**Before (ChangeState):**
```
caller calls item.ChangeState("Active")
  → new ChangeStateCommand("Active") enqueued
  → IsDirty = true
caller calls item.ApplyCommands()
  → cmd.Execute(this): captures _oldState="New", sets State="Active"
  → cmd.ToFieldChange(): returns FieldChange("System.State", "New", "Active")
  → returns [FieldChange(...)]
caller calls item.MarkSynced(rev)
```

**After (ChangeState):**
```
caller calls var change = item.ChangeState("Active")
  → captures oldState="New", sets State="Active", IsDirty=true
  → returns FieldChange("System.State", "New", "Active")
caller calls item.MarkSynced(rev)
```

### Caller Migration Patterns

Each call site follows a predictable migration. Since every site enqueues exactly one command and immediately drains:

**Pattern A — ChangeState callers (4 sites):**
```csharp
// Before:
item.ChangeState(newState);
item.ApplyCommands();  // return value not used
item.MarkSynced(newRevision);

// After:
item.ChangeState(newState);  // return value ignored (callers don't use it)
item.MarkSynced(newRevision);
```

**Pattern B — UpdateField staging (1 site, EditCommand):**
```csharp
// Before:
item.UpdateField("_edited", "true");
item.ApplyCommands();

// After:
item.UpdateField("_edited", "true");  // return value ignored
```

**Pattern C — AddNote staging (1 site, NoteCommand):**
```csharp
// Before:
item.AddNote(new PendingNote(...));
item.ApplyCommands();

// After:
item.AddNote(new PendingNote(...));  // no change needed (was void, still void)
```

### Design Decisions

1. **Guard clauses move into WorkItem** — `ChangeState` and `UpdateField` currently have guards in their command constructors. These guards move to the method bodies. This is simpler and keeps validation at the point of call.

2. **`ChangeState` returns `FieldChange` even though no current caller uses the return value** — This preserves the contract that state changes produce trackable field changes. Future callers may want this data. The cost is zero (a return value that can be discarded).

3. **`AddNote` remains void** — `AddNoteCommand.ToFieldChange()` always returned null. Notes don't produce field changes, so there's nothing to return.

4. **No `ApplyCommands` deprecation period** — The method is removed outright. Since `IWorkItemCommand` is purely internal (no external consumers), there's no compatibility concern.

---

## Dependencies

### Internal Dependencies
- `WorkItem` is the root aggregate — all command/service callers must be updated in the same PR.
- Test files must be updated simultaneously to avoid broken builds (`TreatWarningsAsErrors`).

### External Dependencies
- None. The command pattern is entirely internal to `Twig.Domain`.

### Sequencing Constraints
- None. This refactor is self-contained and has no prerequisites.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `WorkItem` aggregate | Core change — method signatures modified, queue removed |
| `Commands/` directory | **Deleted** — 4 files removed |
| `BranchCommand` | Remove `ApplyCommands()` call |
| `EditCommand` | Remove `ApplyCommands()` call |
| `FlowStartCommand` | Remove `ApplyCommands()` call |
| `NoteCommand` | Remove `ApplyCommands()` call |
| `FlowTransitionService` | Remove `ApplyCommands()` call |
| `ParentStatePropagationService` | Remove `ApplyCommands()` call |
| Domain tests (6+ files) | Update to new API, remove command-specific tests |
| CLI/infra tests (10 files) | Remove `ApplyCommands()` calls in setup code |

### Backward Compatibility
- **Binary**: Breaking (method signatures change). This is internal code with no public NuGet surface.
- **Source**: Breaking for the 6 call sites (all in-repo). Migration is mechanical.
- **Behavioral**: Non-breaking. All observable behavior is preserved.

### Performance
- Negligible improvement: removes one heap allocation per mutation (the command object) and the queue overhead. Not measurable in practice.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Missed call site causes build break | Low | Low | Comprehensive grep audit completed; `TreatWarningsAsErrors` catches unused variables |
| Test migration introduces false-passing tests | Low | Medium | Review each test individually; ensure assertions match original intent |
| Dirty flag regression | Low | High | Dedicated test cases for `IsDirty` tracking; existing `SyncGuard` tests provide integration coverage |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `ChangeState` return `FieldChange` or remain `void` since no caller uses the return value? | Low | Returning `FieldChange` preserves the option for callers to use it. Zero cost. Recommended: return it. |
| 2 | Should we keep empty `Commands/` directory or remove it entirely? | Low | Remove entirely — no remaining contents. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| *(none)* | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs` | Remove queue, rewrite `ChangeState`/`UpdateField`/`AddNote` as direct methods, remove `ApplyCommands()`, move guard clauses inline |
| `src/Twig/Commands/BranchCommand.cs` | Remove `ApplyCommands()` call |
| `src/Twig/Commands/EditCommand.cs` | Remove `ApplyCommands()` call |
| `src/Twig/Commands/FlowStartCommand.cs` | Remove `ApplyCommands()` call |
| `src/Twig/Commands/NoteCommand.cs` | Remove `ApplyCommands()` call |
| `src/Twig.Domain/Services/FlowTransitionService.cs` | Remove `ApplyCommands()` call |
| `src/Twig.Domain/Services/ParentStatePropagationService.cs` | Remove `ApplyCommands()` call |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Remove `using Twig.Domain.Commands;`, rewrite guard clause tests to test `WorkItem` methods directly, remove all `ApplyCommands()` calls, assert on `ChangeState`/`UpdateField` return values |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs` | Remove `ApplyCommands()` calls (2 sites) in dirty-item setup |
| `tests/Twig.Cli.Tests/Hints/HintEngineTests.cs` | Remove `ApplyCommands()` calls (3 sites) in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Remove `ApplyCommands()` calls (6 sites) in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | Remove `ApplyCommands()` calls (2 sites) in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/JsonCompactOutputFormatterTests.cs` | Remove `ApplyCommands()` call (1 site) in dirty-item setup |
| `tests/Twig.Cli.Tests/Formatters/MinimalOutputFormatterTests.cs` | Remove `ApplyCommands()` calls (2 sites) in dirty-item setup |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | Remove `ApplyCommands()` calls (2 sites) in dirty-item setup |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Remove `ApplyCommands()` calls (4 sites) in conflict-resolution setup |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Remove `ApplyCommands()` call (1 site) in state-change test setup |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig.Domain/Commands/IWorkItemCommand.cs` | Interface no longer needed |
| `src/Twig.Domain/Commands/ChangeStateCommand.cs` | Command class replaced by direct method |
| `src/Twig.Domain/Commands/UpdateFieldCommand.cs` | Command class replaced by direct method |
| `src/Twig.Domain/Commands/AddNoteCommand.cs` | Command class replaced by direct method |
| `tests/Twig.Domain.Tests/Commands/ChangeStateCommandTests.cs` | Tests for deleted type |
| `tests/Twig.Domain.Tests/Commands/AddNoteAndUpdateFieldCommandTests.cs` | Tests for deleted types |

---

## ADO Work Item Structure

### Issue 1: Refactor WorkItem Aggregate — Direct Mutation Methods (#2127)

**Goal:** Replace the command queue in `WorkItem` with direct mutation methods that return `FieldChange` values inline. This is the core domain change.

**Prerequisites:** None.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1.1 | Rewrite `ChangeState()` to directly mutate `State`, capture old value, set `IsDirty`, and return `FieldChange`. Move guard clause from `ChangeStateCommand` constructor. | `WorkItem.cs` | Small |
| T1.2 | Rewrite `UpdateField()` to directly mutate field, capture old value, set `IsDirty`, and return `FieldChange`. Move guard clause from `UpdateFieldCommand` constructor. | `WorkItem.cs` | Small |
| T1.3 | Rewrite `AddNote()` to directly call `AddPendingNote()` and set `IsDirty`. No return value change needed. | `WorkItem.cs` | Small |
| T1.4 | Remove `ApplyCommands()` method, `Queue<IWorkItemCommand>` field, and `using Twig.Domain.Commands` directive from `WorkItem.cs`. | `WorkItem.cs` | Small |
| T1.5 | Delete all four files in `src/Twig.Domain/Commands/`: `IWorkItemCommand.cs`, `ChangeStateCommand.cs`, `UpdateFieldCommand.cs`, `AddNoteCommand.cs`. | `Commands/*.cs` | Small |

**Acceptance Criteria:**
- [ ] `WorkItem.ChangeState()` returns `FieldChange` with correct old/new values
- [ ] `WorkItem.UpdateField()` returns `FieldChange` with correct old/new values
- [ ] `WorkItem.AddNote()` mutates directly, sets `IsDirty`
- [ ] `ApplyCommands()` no longer exists
- [ ] `Commands/` directory is empty/deleted
- [ ] `IsDirty` is set on every mutation
- [ ] Guard clauses throw `ArgumentException` on null/whitespace input

### Issue 2: Update Production Callers (#2128)

**Goal:** Update all 6 production call sites to remove `ApplyCommands()` calls and adapt to the new method signatures.

**Prerequisites:** Issue 1.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T2.1 | Update `BranchCommand.cs` — remove `ApplyCommands()` call after `ChangeState()`. | `BranchCommand.cs` | Small |
| T2.2 | Update `FlowStartCommand.cs` — remove `ApplyCommands()` call after `ChangeState()`. | `FlowStartCommand.cs` | Small |
| T2.3 | Update `FlowTransitionService.cs` — remove `ApplyCommands()` call after `ChangeState()`. | `FlowTransitionService.cs` | Small |
| T2.4 | Update `ParentStatePropagationService.cs` — remove `ApplyCommands()` call after `ChangeState()`. | `ParentStatePropagationService.cs` | Small |
| T2.5 | Update `EditCommand.cs` — remove `ApplyCommands()` call after `UpdateField()`. | `EditCommand.cs` | Small |
| T2.6 | Update `NoteCommand.cs` — remove `ApplyCommands()` call after `AddNote()`. | `NoteCommand.cs` | Small |

**Acceptance Criteria:**
- [ ] No remaining references to `ApplyCommands()` in production code
- [ ] All 6 call sites compile and function identically
- [ ] `IsDirty` tracking preserved at all call sites
- [ ] `MarkSynced()` calls unchanged

### Issue 3: Migrate Tests and Validate (#2129)

**Goal:** Update all test files to use the new direct-method API, delete obsolete command tests, and verify full test suite passes.

**Prerequisites:** Issues 1 and 2.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T3.1 | Update `WorkItemTests.cs` — rewrite ITEM-040 (state transition), ITEM-041 (field update), ITEM-042 (note), ITEM-044 (multi-command) tests to use direct methods. Remove `ApplyCommands()` calls, assert on return values directly. Rewrite guard clause tests (lines 106–121) to test `WorkItem.ChangeState`/`UpdateField` directly instead of command constructors. Delete `ChangeStateCommand.NewState` property test. Remove `using Twig.Domain.Commands;`. | `WorkItemTests.cs` | Medium |
| T3.2 | Update `WorkItemCopyTests.cs` — remove `ApplyCommands()` calls in 2 dirty-item setup sites. | `WorkItemCopyTests.cs` | Small |
| T3.3 | Delete `ChangeStateCommandTests.cs` and `AddNoteAndUpdateFieldCommandTests.cs` — these test deleted types. Equivalent coverage is provided by the updated `WorkItemTests`. | `Commands/*.cs` test files | Small |
| T3.4 | Update remaining test files that use `ApplyCommands()` in setup — remove the `ApplyCommands()` call at each site. Files: `HintEngineTests.cs` (3 sites), `HumanOutputFormatterTests.cs` (6 sites), `JsonOutputFormatterTests.cs` (2 sites), `JsonCompactOutputFormatterTests.cs` (1 site), `MinimalOutputFormatterTests.cs` (2 sites), `SqliteWorkItemRepositoryTests.cs` (2 sites), `UpdateCommandTests.cs` (4 sites), `EditSaveCommandTests.cs` (1 site). All are mechanical one-line removals. | 8 test files | Medium |
| T3.5 | Run full test suite (`dotnet test`), fix any remaining compilation errors or test failures. Verify no references to `ApplyCommands`, `IWorkItemCommand`, `ChangeStateCommand`, `UpdateFieldCommand`, or `AddNoteCommand` remain in src/ or tests/. | All | Medium |

**Acceptance Criteria:**
- [ ] All existing tests pass (with API updates applied)
- [ ] No references to `IWorkItemCommand`, `ChangeStateCommand`, `UpdateFieldCommand`, `AddNoteCommand` remain in test code
- [ ] No references to `ApplyCommands` remain in src/ or tests/
- [ ] ITEM-040, ITEM-041, ITEM-042, ITEM-044 equivalent coverage preserved
- [ ] Guard clause tests rewritten to test `WorkItem` methods directly
- [ ] `IsDirty` tracking tests pass
- [ ] Full `dotnet test` green

---

## PR Groups

### PG-1: Core Refactor + Caller Updates + Tests

**Type:** Deep
**Scope:** All changes in a single PR — domain model refactor, caller updates, and test migration.
**Rationale:** This is a tightly coupled refactor where the domain change, caller updates, and test changes must be atomic. Splitting into multiple PRs would create broken intermediate states (removed `ApplyCommands()` with callers still referencing it). The total change is well within the ≤2000 LoC / ≤50 files guideline — estimated ~500 LoC changed across ~23 files (6 deleted, 17 modified).
**Successor:** None.

**Contents:**
- Issue 1: All tasks (T1.1–T1.5) — WorkItem refactor + command deletion
- Issue 2: All tasks (T2.1–T2.6) — Caller updates
- Issue 3: All tasks (T3.1–T3.5) — Test migration + validation

**Estimated Impact:** ~500 LoC changed, ~23 files touched (17 modified, 6 deleted), net deletion of ~200 lines.

---

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type |
|-------|------|---------------|--------------|------|
| PG-1 | core-refactor-and-tests | I1 (T1.1–T1.5), I2 (T2.1–T2.6), I3 (T3.1–T3.5) | None | Deep |

### Execution Order

**PG-1 — core-refactor-and-tests** is the only PR and is fully self-contained.

All three issues are tightly coupled: the domain change (I1) removes `ApplyCommands()`, the caller updates (I2) remove the only production call sites, and the test migration (I3) deletes tests for the removed types and removes `ApplyCommands()` from 10 test files. Splitting these into separate PRs would create broken intermediate states that do not build. Because the total impact is ~500 LoC across ~23 files (well within the ≤2,000 LoC / ≤50 files guardrails), a single atomic PR is the correct grouping.

Recommended implementation order within the PR:
1. Rewrite `WorkItem.cs` (T1.1–T1.4) — establishes the new API.
2. Delete `Commands/` source files (T1.5) — removes deleted types.
3. Update 6 production callers (T2.1–T2.6) — each is a one-line removal.
4. Delete obsolete command test files (T3.3) — removes references to deleted types.
5. Update `WorkItemTests.cs` (T3.1) — rewrite guard clause tests and migrate domain tests to new API.
6. Update `WorkItemCopyTests.cs` (T3.2) — remove `ApplyCommands()` from setup.
7. Update 8 remaining test files (T3.4) — mechanical `ApplyCommands()` removal.
8. Run `dotnet test` and fix any remaining issues (T3.5).

### Validation Strategy — PG-1

| Check | Method |
|-------|--------|
| Build passes | `dotnet build` — `TreatWarningsAsErrors` will catch unused variables and broken references |
| No remaining `ApplyCommands` references | `grep -r "ApplyCommands" src/ tests/` — must return empty |
| No remaining command-type references | `grep -r "IWorkItemCommand\|ChangeStateCommand\|UpdateFieldCommand\|AddNoteCommand" src/ tests/` — must return empty |
| All tests pass | `dotnet test` — full suite green |
| `IsDirty` tracking preserved | Existing `WorkItemTests.cs` ITEM-040/041/042/044 equivalents assert dirty flag |
| LoC budget respected | ~500 LoC changed, ~23 files, well under limits |

---

## References

- `docs/architecture/domain-model-critique.md` — Item 2: "Command Queue Pattern — Complexity Without Payoff"
- `src/Twig.Domain/Aggregates/WorkItem.cs` — Current implementation
- `src/Twig.Domain/Commands/` — Files to be deleted
