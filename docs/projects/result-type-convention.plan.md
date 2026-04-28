# Result Type Convention — Domain Critique Item 7

> **Epic:** #2120 — Domain Critique: Result Type Convention
> **Status**: ✅ Approved
> **Revision:** 0

---

## Executive Summary

The twig domain layer has accumulated 9+ distinct result types using incompatible
patterns: discriminated unions (`ActiveItemResult`, `SyncResult`), generic
`Result<T>`, enum+class hybrids (`BranchLinkResult`, `SeedPublishResult`), boolean
tri-state (`StatusSnapshot`), and pure data bags (`RefreshFetchResult`). This plan
establishes a convention — discriminated unions via `abstract record` for operations
with distinct outcome paths, `Result<T>` for simple success/fail — and migrates the
worst offenders incrementally, one type per PR. `FlowResolveResult` and
`FlowTransitionResult` were already removed during prior refactoring and are no longer
in scope.

## Background

### Current State

The codebase contains these result type patterns:

| Type | Pattern | Location | Consumers |
|------|---------|----------|-----------|
| `Result` / `Result<T>` | `readonly record struct`, `IsSuccess`+`Error` | `Common/Result.cs` | 30+ files |
| `ActiveItemResult` | DU (`abstract record`, 4 sealed subtypes) | `Services/Navigation/` | 8 files |
| `SyncResult` | DU (`abstract record`, 5 sealed subtypes) | `Services/Sync/` | 10 files |
| `StatusSnapshot` | `sealed class` with `bool HasContext`, `WorkItem?`, nullable error fields | `Services/Workspace/` | 4 files |
| `RefreshFetchResult` | `sealed class` data bag (counters + conflict list) | `Services/Sync/RefreshOrchestrator.cs` | 1 file |
| `BranchLinkResult` | `sealed record` + `BranchLinkStatus` enum, computed `IsSuccess` | `ValueObjects/` | 3 files |
| `SeedPublishResult` | `sealed class` + `SeedPublishStatus` enum, computed `IsSuccess` | `ValueObjects/` | 11 files |
| `SeedPublishBatchResult` | `sealed class` aggregating `SeedPublishResult[]` + cycle errors | `ValueObjects/` | 8 files |
| `SeedValidationResult` | `sealed class` with `Passed` computed from `Failures.Count` | `ValueObjects/` | 13 files |
| `SeedReconcileResult` | `sealed class` with counter fields, `NothingToDo` computed | `ValueObjects/` | 7 files |
| `QueryResult` | `sealed record` data carrier (no success/fail) | `ReadModels/` | 15 files |
| `DescendantVerificationResult` | `sealed record` with `Verified` bool | `ReadModels/` | 7 files |

### Prior Resolutions

- **FlowResolveResult / FlowTransitionResult** — mentioned in the domain critique
  but no longer exist in the codebase. They were removed during prior refactoring.
  No action needed.
- **StatusOrchestrator** — absorbed into `ContextTools.Status()` per Item 6
  (orchestrator consolidation). `StatusSnapshot` was retained as a standalone DTO.

### Call-Site Audit: StatusSnapshot (Primary Migration Target)

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `Twig.Domain/Services/Workspace/StatusSnapshot.cs` | (type definition) | Defines type with `NoContext()`, `Unreachable()` factory methods | **Modified** — replaced by DU |
| `Twig.Mcp/Tools/ContextTools.cs` | `Status()` | Constructs `StatusSnapshot` inline (lines 108-124), uses `StatusSnapshot.Unreachable()` | **Modified** — switch to DU construction |
| `Twig.Mcp/Services/McpResultBuilder.cs` | `FormatStatus()` | Reads `HasContext`, `Item`, `PendingChanges`, `Seeds`, `UnreachableId/Reason` | **Modified** — pattern match on DU |
| `Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | 8 test methods | Verifies JSON output shape (indirectly tests snapshot) | **Modified** — update expected construction |
| `Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | `FormatStatus_*` | Tests `FormatStatus` with `StatusSnapshot` instances | **Modified** — update construction |

### Call-Site Audit: BranchLinkResult (Secondary Migration Target)

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `Twig.Domain/ValueObjects/BranchLinkResult.cs` | (type definition) | Defines `sealed record` + `BranchLinkStatus` enum | **Replaced** by DU |
| `Twig.Domain/Services/Navigation/BranchLinkService.cs` | `LinkBranchAsync()` | Constructs 4 different `BranchLinkResult` instances via object initializers | **Modified** — switch to DU factory methods |
| `Twig.Mcp/Services/McpResultBuilder.cs` | `FormatBranchLinked()` | Reads `.Status`, `.WorkItemId`, `.BranchName`, `.ArtifactUri` | **Modified** — pattern match on DU |

## Problem Statement

1. **Pattern inconsistency**: Three different result patterns coexist with no
   convention guiding new code. Contributors must reverse-engineer which pattern
   to use for new operations.

2. **Invalid states representable**: `StatusSnapshot` encodes three distinct
   outcomes (no-context / unreachable / success) as a single class with nullable
   fields and boolean flags. The `IsSuccess` computed property papers over the
   problem but doesn't prevent constructing nonsensical combinations
   (e.g., `HasContext = false` with a non-null `Item`).

3. **No composition**: Result types don't share a common shape. Chaining
   `ActiveItemResult` → `StatusSnapshot` requires manual unwrapping in
   `ContextTools.Status()` (lines 106-124).

4. **Enum+class hybrid anti-pattern**: `BranchLinkResult` and `SeedPublishResult`
   use a status enum alongside the result class. The enum duplicates information
   that the type system could enforce — e.g., `ErrorMessage` is only meaningful
   when `Status == Failed`, but the compiler can't enforce this.

## Goals

1. **Establish convention**: Document when to use discriminated unions vs `Result<T>`
   vs data bags, with examples.
2. **Migrate StatusSnapshot**: Convert to DU pattern with exhaustive match
   enforcement, eliminating all invalid-state combinations.
3. **Migrate BranchLinkResult**: Convert from enum+class to DU pattern, proving
   the convention works for the `ValueObjects/` result types.
4. **Document convention**: Add `docs/architecture/result-type-conventions.md` with
   the decision record and examples.

## Non-Goals

- **Bulk unification**: Not migrating all result types in one pass. The blast radius
  is enormous and the risk of regressions is high.
- **Migrating Result<T>**: The generic result type is fine for simple success/fail
  operations (e.g., `SeedFactory.Create()`). Don't force DU everywhere.
- **Migrating seed result types**: `SeedPublishResult`, `SeedValidationResult`,
  `SeedReconcileResult`, and `SeedPublishBatchResult` are data bags with many
  properties and wide formatter surface area (4 formatter implementations each).
  Converting them to DUs would require touching 40+ files for marginal benefit.
  They are candidates for a future epic, not this one.
- **Migrating RefreshFetchResult**: It's a pure data bag (counters + conflict list)
  with a single consumer. No distinct outcome paths — not a DU candidate.
- **Migrating QueryResult or DescendantVerificationResult**: These are read models,
  not operation results. They don't represent success/failure.
- **Changing SyncResult or ActiveItemResult**: Already correctly use the DU pattern.
  They serve as reference implementations for the convention.

## Requirements

### Functional

- **FR-1**: `StatusSnapshot` is replaced by a discriminated union (`abstract record`
  with sealed subtypes) matching the `ActiveItemResult` pattern.
- **FR-2**: `BranchLinkResult` + `BranchLinkStatus` enum are replaced by a
  discriminated union with distinct subtypes per outcome.
- **FR-3**: All call sites are updated to use pattern matching on the new DU types.
- **FR-4**: `McpResultBuilder.FormatStatus()` and `FormatBranchLinked()` produce
  identical JSON output after migration (backward-compatible wire format).
- **FR-5**: A convention document exists at `docs/architecture/result-type-conventions.md`.

### Non-Functional

- **NFR-1**: No new `TreatWarningsAsErrors` violations.
- **NFR-2**: All existing tests pass after migration.
- **NFR-3**: AOT/trim compatibility maintained (no reflection-based patterns).
- **NFR-4**: No runtime behavioral changes — same exit codes, same JSON output,
  same error messages.

## Proposed Design

### Architecture Overview

The design establishes a three-tier convention for operation results:

```
Tier 1: Discriminated Union (abstract record + sealed subtypes)
├── Use when: Operation has 2+ distinct outcome paths with different data shapes
├── Pattern: abstract record with private ctor, sealed record subtypes
├── Examples: ActiveItemResult, SyncResult, StatusSnapshot (migrated)
└── Enforcement: Exhaustive switch + UnreachableException in default

Tier 2: Result<T> / Result (generic success/fail)
├── Use when: Operation either succeeds with a value or fails with a message
├── Pattern: readonly record struct with IsSuccess, Value, Error
├── Examples: SeedFactory.Create() → Result<WorkItem>
└── Keep: No changes needed

Tier 3: Data Bag (sealed class/record with properties)
├── Use when: Operation always "succeeds" but returns varying amounts of data
├── Pattern: sealed class/record with init properties, computed summaries
├── Examples: RefreshFetchResult, SeedPublishBatchResult, QueryResult
└── Keep: No changes needed
```

### Key Component: StatusSnapshot → StatusResult DU

Current `StatusSnapshot` (invalid states representable):

```csharp
public sealed class StatusSnapshot
{
    public bool HasContext { get; init; }
    public int ActiveId { get; init; }
    public WorkItem? Item { get; init; }
    public IReadOnlyList<PendingChangeRecord> PendingChanges { get; init; } = [];
    public IReadOnlyList<WorkItem> Seeds { get; init; } = [];
    public int? UnreachableId { get; init; }
    public string? UnreachableReason { get; init; }
    public bool IsSuccess => HasContext && Item is not null;
}
```

Proposed `StatusResult` (make invalid states unrepresentable):

```csharp
public abstract record StatusResult
{
    private StatusResult() { }

    /// <summary>No active work item is set.</summary>
    public sealed record NoContext : StatusResult;

    /// <summary>Active item exists but could not be fetched.</summary>
    public sealed record Unreachable(
        int ActiveId,
        int UnreachableId,
        string Reason) : StatusResult;

    /// <summary>Active item resolved successfully.</summary>
    public sealed record Success(
        WorkItem Item,
        IReadOnlyList<PendingChangeRecord> PendingChanges,
        IReadOnlyList<WorkItem> Seeds) : StatusResult;
}
```

This eliminates:
- `HasContext` boolean (replaced by type: `NoContext` vs others)
- `WorkItem?` nullability (guaranteed non-null in `Success`)
- `IsSuccess` computed property (replaced by `is StatusResult.Success`)
- All "nullable error fields on success" invalid combinations

### Key Component: BranchLinkResult → DU

Current `BranchLinkResult` + enum:

```csharp
public sealed record BranchLinkResult
{
    public required BranchLinkStatus Status { get; init; }
    public required int WorkItemId { get; init; }
    public required string BranchName { get; init; }
    public string ArtifactUri { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
    public bool IsSuccess => Status is BranchLinkStatus.Linked or BranchLinkStatus.AlreadyLinked;
}

public enum BranchLinkStatus { Linked, AlreadyLinked, GitContextUnavailable, Failed }
```

Proposed DU:

```csharp
public abstract record BranchLinkResult
{
    private BranchLinkResult() { }

    public sealed record Linked(
        int WorkItemId,
        string BranchName,
        string ArtifactUri) : BranchLinkResult;

    public sealed record AlreadyLinked(
        int WorkItemId,
        string BranchName,
        string ArtifactUri) : BranchLinkResult;

    public sealed record GitContextUnavailable(
        int WorkItemId,
        string BranchName,
        string ErrorMessage) : BranchLinkResult;

    public sealed record Failed(
        int WorkItemId,
        string BranchName,
        string ArtifactUri,
        string ErrorMessage) : BranchLinkResult;
}
```

This eliminates:
- `BranchLinkStatus` enum (each case is a type)
- `ErrorMessage` being non-empty on success (only present in error subtypes)
- `ArtifactUri` being empty before resolution (only present when available)
- `IsSuccess` computed property (replaced by `is Linked or AlreadyLinked`)

### Data Flow: StatusResult in ContextTools.Status()

```
ContextTools.Status()
    │
    ├── No active ID → return McpResultBuilder.ToError(...)   [unchanged]
    │
    ├── ActiveItemResolver.GetActiveItemAsync()
    │   ├── TryGetWorkItem fails → StatusResult.Unreachable(...)
    │   └── TryGetWorkItem succeeds
    │       ├── Fetch pending changes
    │       ├── Fetch seeds
    │       └── StatusResult.Success(item, pending, seeds)
    │
    └── McpResultBuilder.FormatStatus(StatusResult, workspace)
        ├── case NoContext: (won't reach — handled above)
        ├── case Unreachable: write hasContext:true, item:null, unreachableId/Reason
        └── case Success: write hasContext:true, item:{...}, pendingChanges, seeds
```

### Design Decisions

1. **Name `StatusResult` not `StatusSnapshot`**: The "snapshot" name implied a
   point-in-time data capture. The new name reflects that this is an operation
   result with distinct outcome paths. Existing code already uses `*Result` for
   this pattern (`ActiveItemResult`, `SyncResult`).

2. **Keep `NoContext` in the DU even though it's caught earlier**: The
   `ContextTools.Status()` handler catches no-context before constructing the
   result. However, the DU should still model this case for completeness — other
   future consumers might not have the same guard. The `NoContext` case costs
   nothing and prevents partial modeling.

3. **Flatten `ActiveId` into `Unreachable` only**: In the current `StatusSnapshot`,
   `ActiveId` is set in both success and unreachable cases. In the new DU, the
   success case carries the `WorkItem` (which has `.Id`), so `ActiveId` is only
   needed in `Unreachable` where there's no `WorkItem` to read it from.

4. **BranchLinkResult: keep common fields in each subtype**: Rather than putting
   `WorkItemId` and `BranchName` in the base record, they stay in each subtype.
   This matches `ActiveItemResult`'s pattern (no shared properties on the abstract
   base) and avoids introducing a different DU style.

## Dependencies

### Internal
- No dependencies on other planned work items.
- Convention document should reference `ActiveItemResult` and `SyncResult` as
  exemplars — these must remain stable.

### Sequencing
- Issue 1 (Convention Document) should land first — it establishes the rules that
  Issues 2 and 3 follow.
- Issue 2 (StatusSnapshot) and Issue 3 (BranchLinkResult) are independent and can
  proceed in parallel after Issue 1.

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `Twig.Domain/Services/Workspace/StatusSnapshot.cs` | Replaced by `StatusResult.cs` | Low — 4 call sites |
| `Twig.Domain/ValueObjects/BranchLinkResult.cs` | Replaced by DU version | Low — 3 call sites |
| `Twig.Mcp/Tools/ContextTools.cs` | Updated StatusResult construction | Low — mechanical |
| `Twig.Mcp/Services/McpResultBuilder.cs` | Updated pattern matching | Medium — must preserve JSON shape |
| `Twig.Domain/Services/Navigation/BranchLinkService.cs` | Updated DU construction | Low — mechanical |
| Test files (5 files) | Updated to use new DU types | Low — mechanical |

### Backward Compatibility

- **JSON wire format**: `FormatStatus()` and `FormatBranchLinked()` must produce
  identical JSON after migration. Tests verify this.
- **No public API breaks**: These types are internal to the domain/MCP layers.
  No external consumers.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| JSON output shape changes break MCP consumers | Low | High | Test-first: verify JSON shape before and after migration |
| Convention document becomes shelfware | Medium | Low | Reference it in PR reviews; enforce in code review guidelines |
| Seed result type owners push for immediate migration | Low | Medium | Non-goal is explicitly documented; defer to future epic |

## Open Questions

1. **[Low] Should `BranchLinkResult` subtypes share `WorkItemId`/`BranchName` via
   the abstract base?** Both approaches are valid. Keeping them in subtypes matches
   `ActiveItemResult` style. Sharing via base reduces duplication. Decision: match
   existing convention (subtypes) for consistency.

2. **[Low] Should `StatusResult.NoContext` be removed since it's never constructed
   in current code?** Keeping it models the full state space and costs nothing.
   Removing it is equally valid. Decision: keep for completeness.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/Workspace/StatusResult.cs` | DU replacing `StatusSnapshot` |
| `docs/architecture/result-type-conventions.md` | Convention document |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/Workspace/StatusSnapshot.cs` | Deleted (replaced by `StatusResult.cs`) |
| `src/Twig.Domain/ValueObjects/BranchLinkResult.cs` | Rewritten as DU, `BranchLinkStatus` enum removed |
| `src/Twig.Mcp/Tools/ContextTools.cs` | `Status()` method updated to construct `StatusResult` instead of `StatusSnapshot` |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | `FormatStatus()` updated to pattern-match on `StatusResult`; `FormatBranchLinked()` updated for DU |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | Updated assertions for new DU construction |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | `FormatStatus_*` tests updated |
| `src/Twig.Domain/Services/Navigation/BranchLinkService.cs` | Updated to construct DU subtypes |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig.Domain/Services/Workspace/StatusSnapshot.cs` | Replaced by `StatusResult.cs` |

## ADO Work Item Structure

### Issue 1: Establish Result Type Convention Document

**Goal**: Create a living convention document that codifies when to use each result
pattern, with examples from the existing codebase.

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1.1 | Write convention document with Tier 1/2/3 taxonomy, decision matrix, and examples | `docs/architecture/result-type-conventions.md` | ~150 LoC |
| T1.2 | Update domain-model-critique.md Item 7 to reference the convention and note FlowResolveResult/FlowTransitionResult removal | `docs/architecture/domain-model-critique.md` | ~20 LoC |

**Acceptance Criteria**:
- [ ] Convention document exists with clear decision matrix
- [ ] Each tier has at least 2 codebase examples
- [ ] DU pattern shows complete example with exhaustive matching
- [ ] domain-model-critique.md Item 7 updated with cross-reference

### Issue 2: Migrate StatusSnapshot to Discriminated Union

**Goal**: Replace `StatusSnapshot` (boolean tri-state with nullable fields) with
`StatusResult` discriminated union, eliminating all invalid-state combinations while
preserving identical JSON wire format.

**Prerequisites**: Issue 1 (convention document establishes the pattern)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T2.1 | Create `StatusResult` DU type with `NoContext`, `Unreachable`, `Success` subtypes | `src/Twig.Domain/Services/Workspace/StatusResult.cs` (new) | ~30 LoC |
| T2.2 | Update `ContextTools.Status()` to construct `StatusResult` instead of `StatusSnapshot` | `src/Twig.Mcp/Tools/ContextTools.cs` | ~30 LoC |
| T2.3 | Update `McpResultBuilder.FormatStatus()` to pattern-match on `StatusResult`, preserving JSON shape | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~50 LoC |
| T2.4 | Update tests for new DU construction and verify JSON backward compatibility | `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs`, `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | ~80 LoC |
| T2.5 | Delete `StatusSnapshot.cs` and verify clean build | `src/Twig.Domain/Services/Workspace/StatusSnapshot.cs` (delete) | ~5 LoC |

**Acceptance Criteria**:
- [ ] `StatusSnapshot` class no longer exists
- [ ] `StatusResult` DU with 3 subtypes compiles and passes AOT validation
- [ ] `FormatStatus()` produces identical JSON for all 3 cases (no-context handled upstream, unreachable, success)
- [ ] All 8 ContextToolsStatusTests pass
- [ ] All McpResultBuilderTests pass
- [ ] No `TreatWarningsAsErrors` violations

### Issue 3: Migrate BranchLinkResult to Discriminated Union

**Goal**: Replace `BranchLinkResult` sealed record + `BranchLinkStatus` enum with
a discriminated union, eliminating the enum and enforcing that error-specific fields
only exist on error subtypes.

**Prerequisites**: Issue 1 (convention document)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T3.1 | Rewrite `BranchLinkResult.cs` as DU with `Linked`, `AlreadyLinked`, `GitContextUnavailable`, `Failed` subtypes; remove `BranchLinkStatus` enum | `src/Twig.Domain/ValueObjects/BranchLinkResult.cs` | ~40 LoC |
| T3.2 | Update `BranchLinkService.LinkBranchAsync()` to construct DU subtypes | `src/Twig.Domain/Services/Navigation/BranchLinkService.cs` | ~30 LoC |
| T3.3 | Update `McpResultBuilder.FormatBranchLinked()` to pattern-match on DU | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~20 LoC |
| T3.4 | Update any formatter/command references and tests | Formatter files, test files | ~40 LoC |

**Acceptance Criteria**:
- [ ] `BranchLinkStatus` enum no longer exists
- [ ] `BranchLinkResult` is an abstract record DU with 4 sealed subtypes
- [ ] `BranchLinkService` compiles with DU construction
- [ ] `FormatBranchLinked()` produces identical JSON for linked/already-linked cases
- [ ] All existing tests pass
- [ ] No `TreatWarningsAsErrors` violations

## PR Groups

### PG-1: Convention Document (Issue 1)

**Type**: Deep
**Issues**: Issue 1 (T1.1, T1.2)
**Estimated LoC**: ~170
**Files**: 2 (docs only)
**Predecessors**: None

Establishes the result type convention. Documentation-only — no code changes,
no build risk. Must land before PG-2 and PG-3 so migration PRs can reference it.

### PG-2: StatusSnapshot Migration (Issue 2)

**Type**: Deep
**Issues**: Issue 2 (T2.1–T2.5)
**Estimated LoC**: ~200
**Files**: ~5
**Predecessors**: PG-1

Migrates the worst offender. Small blast radius (4 production files, 2 test files).
JSON backward compatibility is the key review concern.

### PG-3: BranchLinkResult Migration (Issue 3)

**Type**: Deep
**Issues**: Issue 3 (T3.1–T3.4)
**Estimated LoC**: ~130
**Files**: ~4
**Predecessors**: PG-1

Independent from PG-2. Proves the convention works for `ValueObjects/` result types.
Smaller scope than PG-2.

## References

- `docs/architecture/domain-model-critique.md` — Item 7 (Result Type Proliferation)
- `src/Twig.Domain/Services/Navigation/ActiveItemResult.cs` — Reference DU implementation
- `src/Twig.Domain/Services/Sync/SyncResult.cs` — Reference DU implementation

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | convention-document | Issue 1 (T1.1, T1.2) | None | Deep |
| PG-2 | statusresult-migration | Issue 2 (T2.1–T2.5) | PG-1 | Deep |
| PG-3 | branchlinkresult-migration | Issue 3 (T3.1–T3.4) | PG-1 | Deep |

### Execution Order

**PG-1** lands first (documentation-only, no build risk). It establishes the
convention document and updates the domain critique cross-reference. Both PG-2 and
PG-3 depend on PG-1 so that their PRs can reference the convention. After PG-1 merges,
**PG-2** and **PG-3** may proceed independently and in parallel — they touch disjoint
files (`StatusSnapshot`/`ContextTools`/`McpResultBuilder.FormatStatus` vs
`BranchLinkResult`/`BranchLinkService`/`McpResultBuilder.FormatBranchLinked`).

### Validation Strategy

**PG-1** — No build/test step required; reviewer validates prose quality and accuracy
of the decision matrix against existing codebase examples.

**PG-2** — `dotnet build` must pass with zero warnings. All tests in
`Twig.Mcp.Tests` must pass. Key review concern: JSON wire format backward
compatibility for `FormatStatus()`. Verify `ContextToolsStatusTests` and
`McpResultBuilderTests` cover all three `StatusResult` cases (NoContext, Unreachable,
Success).

**PG-3** — `dotnet build` must pass with zero warnings. All tests in
`Twig.Mcp.Tests` must pass. Key review concern: JSON wire format backward
compatibility for `FormatBranchLinked()`. Verify all four `BranchLinkResult` subtypes
(Linked, AlreadyLinked, GitContextUnavailable, Failed) are exercised by tests.
