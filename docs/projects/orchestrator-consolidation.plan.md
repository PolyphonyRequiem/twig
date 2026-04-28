# Epic #2119 — Domain Critique: Orchestrator Consolidation

> **Revision**: 0 (Initial draft)

## Executive Summary

Five orchestrator/coordinator patterns exist in the twig domain layer with overlapping
dependency subsets: `SyncCoordinator`, `SyncCoordinatorFactory`, `RefreshOrchestrator`,
`StatusOrchestrator`, and `SeedPublishOrchestrator`. This plan addresses naming clarity
and duplication: `StatusOrchestrator` is absorbed into `ActiveItemResolver` plus inline
command logic (its only non-trivial consumer is the MCP `twig_status` tool);
`SyncCoordinatorFactory` is renamed to `SyncCoordinatorPair` for honest naming; and
`StatusSnapshot` is converted from a boolean-flag class to a discriminated-union record
matching the codebase's established `ActiveItemResult` / `SyncResult` pattern. Each
change ships in a separate PR to contain blast radius. `RefreshOrchestrator` and
`SeedPublishOrchestrator` are left untouched — they are genuine orchestrators with
substantial, non-duplicated logic.

## Background

### Current Architecture

The domain layer organizes sync/orchestration into five classes across three
subdirectories:

| Class | Location | Dependencies | Responsibility |
|-------|----------|-------------|----------------|
| `SyncCoordinator` | `Services/Sync/` | `IWorkItemRepository`, `IAdoWorkItemService`, `ProtectedCacheWriter`, `IPendingChangeStore`, `IWorkItemLinkRepository?`, `int cacheStaleMinutes` | Per-item and batch sync with staleness checks |
| `SyncCoordinatorFactory` | `Services/Sync/` | Same as `SyncCoordinator` × 2 + two `int` TTL values | Holds ReadOnly (display) and ReadWrite (mutation) `SyncCoordinator` instances |
| `RefreshOrchestrator` | `Services/Sync/` | `IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService`, `IPendingChangeStore`, `ProtectedCacheWriter`, `WorkingSetService`, `SyncCoordinatorFactory`, `IIterationService`, `ITrackingService?` | Full refresh cycle: WIQL fetch → conflict detect → save → ancestor hydration → working set sync |
| `StatusOrchestrator` | `Services/Workspace/` | `IContextStore`, `IWorkItemRepository`, `IPendingChangeStore`, `ActiveItemResolver`, `WorkingSetService`, `SyncCoordinatorFactory` | Wraps `ActiveItemResolver` + pending changes into a `StatusSnapshot` |
| `SeedPublishOrchestrator` | `Services/Seed/` | `IWorkItemRepository`, `IAdoWorkItemService`, `ISeedLinkRepository`, `IPublishIdMapRepository`, `ISeedPublishRulesProvider`, `IUnitOfWork`, `BacklogOrderer` | Seed validate → ADO create → fetch back → transactional remap → link promotion |

### Dependency Overlap Matrix

| Dependency | SyncCoord | SyncCoordFactory | RefreshOrch | StatusOrch | SeedPublishOrch |
|-----------|:---------:|:----------------:|:-----------:|:----------:|:---------------:|
| `IWorkItemRepository` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `IAdoWorkItemService` | ✓ | ✓ | ✓ | | ✓ |
| `ProtectedCacheWriter` | ✓ | ✓ | ✓ | | |
| `IPendingChangeStore` | ✓ | ✓ | ✓ | ✓ | |
| `IContextStore` | | | ✓ | ✓ | |
| `SyncCoordinatorFactory` | | | ✓ | ✓ | |
| `WorkingSetService` | | | ✓ | ✓ | |
| `ActiveItemResolver` | | | | ✓ | |
| `IWorkItemLinkRepository` | ✓ | ✓ | | | |
| `IIterationService` | | | ✓ | | |
| `ITrackingService?` | | | ✓ | | |
| `ISeedLinkRepository` | | | | | ✓ |
| `IPublishIdMapRepository` | | | | | ✓ |
| `ISeedPublishRulesProvider` | | | | | ✓ |
| `IUnitOfWork` | | | | | ✓ |
| `BacklogOrderer` | | | | | ✓ |

Key observations:
- **StatusOrchestrator** shares 4/6 deps with the `StatusCommand` itself (which already injects `ActiveItemResolver`, `WorkingSetService`, `SyncCoordinatorFactory`, etc.). The orchestrator adds no logic beyond composing these calls.
- **SyncCoordinatorFactory** is not a factory — it never creates instances dynamically. It's a pair holder with a validation clamp.
- **RefreshOrchestrator** and **SeedPublishOrchestrator** have substantial, non-overlapping logic that justifies their existence.

### Call-Site Audit

#### StatusOrchestrator Call Sites

| File | Method | Usage | Impact of Removal |
|------|--------|-------|-------------------|
| `Twig.Mcp/Tools/ContextTools.cs:100` | `Status()` | `ctx.StatusOrchestrator.GetSnapshotAsync(ct)` | Must inline snapshot-building logic using `ctx.ActiveItemResolver` + `ctx.PendingChangeStore` + `ctx.WorkItemRepo` (all already on `WorkspaceContext`) |
| `Twig.Mcp/Services/WorkspaceContextFactory.cs:146` | `CreateContext()` | Constructs `StatusOrchestrator` | Remove construction |
| `Twig.Mcp/Services/WorkspaceContext.cs:30` | Property | `StatusOrchestrator` property | Remove property |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTestBase.cs:145` | Test setup | Constructs `StatusOrchestrator` | Remove construction |
| `tests/Twig.Domain.Tests/Services/Workspace/StatusOrchestratorTests.cs` | Test class (145 lines) | 5 unit tests | Delete file; MCP integration tests cover the same paths |
| `tests/Twig.Mcp.Tests/Services/WorkspaceContextFactoryTests.cs:82,187` | Test assertions | `context.StatusOrchestrator.ShouldNotBeNull()` | Remove assertions |
| `tests/Twig.Mcp.Tests/Tools/MultiWorkspaceIsolationTests.cs:171` | Comment only | References `StatusOrchestrator` in a comment | Update comment |

**Note**: `StatusCommand` does **not** use `StatusOrchestrator`. It already resolves via `ActiveItemResolver` directly (line 67) and calls `pendingChangeStore`, `workItemRepo.GetSeedsAsync()`, `syncCoordinatorFactory` inline. The orchestrator is used only by MCP.

#### StatusSnapshot Call Sites

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `Twig.Mcp/Services/McpResultBuilder.cs:35` | `FormatStatus()` | Accepts `StatusSnapshot`, reads `.HasContext`, `.Item`, `.PendingChanges`, `.Seeds`, `.UnreachableId/.UnreachableReason` | Must accept new DU type |
| `Twig.Mcp/Tools/ContextTools.cs:100-105` | `Status()` | Checks `!snapshot.HasContext`, calls `McpResultBuilder.FormatStatus()` | Pattern-match on new DU |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | 6 test methods | Construct `StatusSnapshot` with various states | Update to new DU |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs:44` | Test | Calls through to snapshot | Update |

#### SyncCoordinatorFactory Call Sites (Source)

| File | Method | Usage | Impact of Rename |
|------|--------|-------|-----------------|
| `CommandServiceModule.cs:52` | Registration | `services.AddSingleton<SyncCoordinatorFactory>` | Update type name |
| `CommandServiceModule.cs:66` | Backward compat | `sp.GetRequiredService<SyncCoordinatorFactory>()` | Update type name |
| `CommandServiceModule.cs:114` | RefreshOrch ctor | `sp.GetRequiredService<SyncCoordinatorFactory>()` | Update type name |
| `StatusCommand.cs:26` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name |
| `SetCommand.cs:28` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name |
| `ShowCommand.cs:26` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name |
| `TreeCommand.cs:25` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name |
| `LinkCommand.cs:18` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name |
| `RefreshOrchestrator.cs:22` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name |
| `StatusOrchestrator.cs:20` | Ctor param | `SyncCoordinatorFactory syncCoordinatorFactory` | Update type name (if still exists) |
| `WorkspaceContext.cs:28` | Property | `SyncCoordinatorFactory SyncCoordinatorFactory` | Update type + property name |
| `WorkspaceContextFactory.cs:118` | Construction | `new SyncCoordinatorFactory(...)` | Update type name |
| ~30 test files | Test setup | `new SyncCoordinatorFactory(...)` | Mechanical rename |

### Reference

See `docs/architecture/domain-model-critique.md` Item 6 (Orchestrator Proliferation)
and Item 7 (Result Type Proliferation — StatusSnapshot specifically).

## Problem Statement

1. **StatusOrchestrator is a thin wrapper with no unique logic.** Its `GetSnapshotAsync`
   method calls `contextStore.GetActiveWorkItemIdAsync()`, then
   `activeItemResolver.GetActiveItemAsync()`, then `pendingChangeStore.GetChangesAsync()`
   and `workItemRepo.GetSeedsAsync()` — four sequential calls that could be inlined.
   Its `SyncWorkingSetAsync` is a try/catch around two existing calls. The only consumer
   is MCP's `ContextTools.Status()`. The CLI `StatusCommand` already does the same
   resolution inline (lines 46–75) without using the orchestrator.

2. **StatusSnapshot encodes tri-state as boolean flags.** It has `HasContext` (bool),
   `Item` (nullable), `UnreachableId` (nullable int), and `UnreachableReason` (nullable
   string). The `IsSuccess` computed property checks `HasContext && Item is not null`.
   Three distinct states (NoContext, Unreachable, Success) are encoded via flag
   combinations rather than the discriminated-union pattern used everywhere else
   (`ActiveItemResult`, `SyncResult`). This makes pattern-matching impossible and
   consumers must check multiple properties.

3. **SyncCoordinatorFactory is misnamed.** It does not create instances on demand — it
   holds exactly two pre-built `SyncCoordinator` instances with different TTL values.
   The name "Factory" implies a creation pattern (e.g., `Create(config)`) that doesn't
   exist. The class is ~30 lines and functionally correct; only the name is misleading.

## Goals and Non-Goals

### Goals

1. **Absorb `StatusOrchestrator`** into inline logic at its single MCP call site,
   removing an unnecessary indirection layer.
2. **Convert `StatusSnapshot`** to a discriminated-union record (`StatusResult`) matching
   the established `ActiveItemResult` / `SyncResult` pattern, improving type safety
   and pattern-matching ergonomics.
3. **Rename `SyncCoordinatorFactory`** to `SyncCoordinatorPair` to accurately describe
   its pair-holder semantics.
4. **Maintain all existing behavior** — no functional changes, only structural.
5. **Ship each change in a separate PR** per the containment practice in the critique doc.

### Non-Goals

- **Consolidating RefreshOrchestrator** — it has substantial, unique logic (WIQL fetch,
  conflict detection, ancestor hydration, tracked tree sync, cleanup policy). Leave as-is.
- **Consolidating SeedPublishOrchestrator** — it has complex transactional logic
  (validate → create → fetch-back → remap → promote). Leave as-is.
- **Refactoring SyncCoordinator itself** — its API and implementation are sound.
- **Reducing command constructor parameter counts** — that's Epic #2121 (CommandContext).
- **Introducing a shared dependency aggregate** across orchestrators — the overlap is
  not harmful enough to justify a new abstraction.
- **Behavioral changes** — no changes to sync timing, staleness thresholds, or error handling.

## Requirements

### Functional

- FR-1: `twig_status` MCP tool produces identical JSON output before and after changes.
- FR-2: `twig status` CLI command behavior is unchanged (it already doesn't use
  `StatusOrchestrator`).
- FR-3: All commands using `SyncCoordinatorFactory` continue to resolve correctly after
  rename.
- FR-4: The `StatusResult` discriminated union exposes the same data as `StatusSnapshot`
  in a type-safe pattern-matchable form.

### Non-Functional

- NFR-1: Zero new warnings (`TreatWarningsAsErrors=true`).
- NFR-2: All existing tests pass after each change.
- NFR-3: No new runtime allocations beyond what's already present.
- NFR-4: AOT compatibility maintained (no reflection, source-gen JSON contexts unaffected
  since `StatusSnapshot` is not serialized via `TwigJsonContext`).

## Proposed Design

### Issue 1: StatusOrchestrator Absorption

**Approach**: Delete `StatusOrchestrator` class. Introduce a `StatusResult` discriminated
union to replace `StatusSnapshot`. Move the snapshot-building logic (5 lines) into a
static helper method or inline it at the single MCP call site.

**StatusResult DU** (replaces `StatusSnapshot`):

```csharp
public abstract record StatusResult
{
    private StatusResult() { }

    public sealed record NoContext : StatusResult;

    public sealed record Unreachable(int ActiveId, int ErrorId, string? Reason) : StatusResult;

    public sealed record Success(
        int ActiveId,
        WorkItem Item,
        IReadOnlyList<PendingChangeRecord> PendingChanges,
        IReadOnlyList<WorkItem> Seeds) : StatusResult;
}
```

**MCP call site** (`ContextTools.Status`):

```csharp
// Before:
var snapshot = await ctx.StatusOrchestrator.GetSnapshotAsync(ct);
if (!snapshot.HasContext)
    return McpResultBuilder.ToError("No active work item...");
return McpResultBuilder.FormatStatus(snapshot, ctx.Key.ToString());

// After:
var result = await StatusResultBuilder.BuildAsync(
    ctx.ContextStore, ctx.ActiveItemResolver,
    ctx.PendingChangeStore, ctx.WorkItemRepo, ct);
return result switch
{
    StatusResult.NoContext => McpResultBuilder.ToError("No active work item..."),
    StatusResult.Unreachable u => McpResultBuilder.FormatUnreachable(u, ctx.Key.ToString()),
    StatusResult.Success s => McpResultBuilder.FormatStatus(s, ctx.Key.ToString()),
    _ => McpResultBuilder.ToError("Unexpected status result")
};
```

The builder is a small static class (~20 lines) that replaces the orchestrator constructor
and method body, living in `Services/Workspace/StatusResultBuilder.cs`.

**SyncWorkingSetAsync**: The `StatusOrchestrator.SyncWorkingSetAsync` method is not called
by MCP. The only CLI caller (`StatusCommand`) already has its own inline sync logic
(lines 118, 130, 252–259). This method is simply deleted.

### Issue 2: SyncCoordinatorFactory → SyncCoordinatorPair Rename

**Approach**: Rename the class, file, and all references. The internal structure is
unchanged — `ReadOnly` and `ReadWrite` properties remain. This is a mechanical
find-and-replace with namespace updates.

```csharp
// Before:
public sealed class SyncCoordinatorFactory { ... }

// After:
public sealed class SyncCoordinatorPair { ... }
```

All constructor parameters named `syncCoordinatorFactory` become `syncCoordinatorPair`
or `syncCoordinators` for clarity.

### Design Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| DD-1 | Use static builder instead of keeping orchestrator | The orchestrator has zero state, zero lifecycle, and one consumer. A static builder communicates "I'm a function" more honestly. |
| DD-2 | Keep `StatusResult` in `Services/Workspace/` | Maintains current namespace locality; consumers already import this namespace. |
| DD-3 | Don't move `StatusResult` to `Services/Navigation/` alongside `ActiveItemResult` | Despite structural similarity, the result types serve different concerns. Moving would couple navigation and workspace namespaces. |
| DD-4 | Rename to `SyncCoordinatorPair` not `SyncCoordinators` | "Pair" precisely communicates cardinality (exactly two). "Coordinators" is ambiguous about count and collection semantics. |
| DD-5 | Ship StatusOrchestrator absorption and StatusSnapshot DU conversion together | They're tightly coupled — can't remove the orchestrator without updating its return type's consumers, and converting the return type without the orchestrator removal would leave dead code. |

## Dependencies

- **Internal**: No dependencies on other epics. This is a standalone cleanup.
- **External**: None.
- **Sequencing**: Issue 1 (StatusOrchestrator + StatusSnapshot) must complete before
  Issue 2 (rename) to avoid two renames of the same orchestrator parameter in
  `StatusOrchestrator.cs`.

## Impact Analysis

| Area | Impact |
|------|--------|
| `Twig.Domain` | Delete `StatusOrchestrator.cs` (class + `StatusSnapshot`), add `StatusResult.cs` + `StatusResultBuilder.cs`, rename `SyncCoordinatorFactory.cs` → `SyncCoordinatorPair.cs` |
| `Twig.Mcp` | Update `WorkspaceContext` (remove `StatusOrchestrator` property, rename `SyncCoordinatorFactory` property), update `WorkspaceContextFactory`, update `ContextTools.Status()`, update `McpResultBuilder.FormatStatus()` |
| `Twig` (CLI) | Rename `SyncCoordinatorFactory` references in commands and DI registration. No `StatusOrchestrator` references to update. |
| Tests | Delete `StatusOrchestratorTests.cs`, update MCP test files, rename ~30 test file references to `SyncCoordinatorFactory` |
| Backward compat | Fully backward compatible — no public API changes, no serialization changes |
| Performance | Neutral — same number of async calls, same allocations |

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Missed `SyncCoordinatorFactory` reference causes build break | Low | Low | Compiler will catch all references; `TreatWarningsAsErrors` ensures nothing slips through |
| MCP `twig_status` output format changes subtly | Low | Medium | Add regression test comparing JSON output before/after; existing `McpResultBuilderTests` cover all branches |
| Renaming churn makes git blame harder | Low | Low | Single-purpose PR with only rename changes; git `--follow` handles renames |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Should `SyncCoordinatorPair` also rename its `ReadOnly`/`ReadWrite` properties to something more descriptive (e.g., `Display`/`Mutation`)? | Low | Current names are clear; renaming would increase blast radius for marginal clarity gain. Recommend leaving as-is. |
| OQ-2 | Should the backward-compat `SyncCoordinator` DI registration (line 66 in `CommandServiceModule`) be removed since `ContextChangeService` is the only direct `SyncCoordinator` consumer? | Low | It's a one-liner that prevents breaking changes if new code needs a single coordinator. Recommend keeping for now. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/Workspace/StatusResult.cs` | Discriminated-union record replacing `StatusSnapshot` |
| `src/Twig.Domain/Services/Workspace/StatusResultBuilder.cs` | Static builder that replaces `StatusOrchestrator.GetSnapshotAsync()` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/Sync/SyncCoordinatorFactory.cs` | Rename class to `SyncCoordinatorPair`, rename file |
| `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs` | Update `SyncCoordinatorFactory` → `SyncCoordinatorPair` parameter |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Remove `StatusOrchestrator` property; rename `SyncCoordinatorFactory` property |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Remove `StatusOrchestrator` construction; rename `SyncCoordinatorFactory` |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Update `FormatStatus()` to accept `StatusResult.Success` instead of `StatusSnapshot`; add `FormatUnreachable()` |
| `src/Twig.Mcp/Tools/ContextTools.cs` | Replace `StatusOrchestrator.GetSnapshotAsync()` with `StatusResultBuilder.BuildAsync()` + pattern match |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Rename `SyncCoordinatorFactory` → `SyncCoordinatorPair` in registration |
| `src/Twig/Commands/StatusCommand.cs` | Rename `SyncCoordinatorFactory` parameter |
| `src/Twig/Commands/SetCommand.cs` | Rename `SyncCoordinatorFactory` parameter |
| `src/Twig/Commands/ShowCommand.cs` | Rename `SyncCoordinatorFactory` parameter |
| `src/Twig/Commands/TreeCommand.cs` | Rename `SyncCoordinatorFactory` parameter |
| `src/Twig/Commands/LinkCommand.cs` | Rename `SyncCoordinatorFactory` parameter |
| `tests/Twig.Domain.Tests/Services/Workspace/StatusOrchestratorTests.cs` | Delete — covered by MCP integration tests |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Update `StatusSnapshot` → `StatusResult` construction |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | Update for new pattern |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTestBase.cs` | Remove `StatusOrchestrator` construction |
| `tests/Twig.Mcp.Tests/Services/WorkspaceContextFactoryTests.cs` | Remove `StatusOrchestrator` assertions |
| ~30 test files across `tests/Twig.Cli.Tests/` and `tests/Twig.Domain.Tests/` | Mechanical rename `SyncCoordinatorFactory` → `SyncCoordinatorPair` |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig.Domain/Services/Workspace/StatusOrchestrator.cs` | Absorbed into `StatusResultBuilder` + inline MCP logic |
| `tests/Twig.Domain.Tests/Services/Workspace/StatusOrchestratorTests.cs` | Tests for deleted class |

## ADO Work Item Structure

### Issue 1: Absorb StatusOrchestrator and Convert StatusSnapshot to DU

**Goal**: Remove `StatusOrchestrator` class, replace `StatusSnapshot` with a discriminated-union
`StatusResult`, update the single MCP consumer to use `StatusResultBuilder`, and ensure
MCP output is byte-identical.

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1.1 | Create `StatusResult` discriminated-union record with `NoContext`, `Unreachable`, and `Success` cases | `src/Twig.Domain/Services/Workspace/StatusResult.cs` | S |
| T1.2 | Create `StatusResultBuilder.BuildAsync()` static method that replicates `StatusOrchestrator.GetSnapshotAsync()` logic | `src/Twig.Domain/Services/Workspace/StatusResultBuilder.cs` | S |
| T1.3 | Update `McpResultBuilder.FormatStatus()` to accept `StatusResult.Success`; add `FormatUnreachable()` for the unreachable case | `src/Twig.Mcp/Services/McpResultBuilder.cs` | S |
| T1.4 | Update `ContextTools.Status()` to use `StatusResultBuilder` + pattern match; remove `StatusOrchestrator` dependency | `src/Twig.Mcp/Tools/ContextTools.cs` | S |
| T1.5 | Remove `StatusOrchestrator` property from `WorkspaceContext`; remove construction from `WorkspaceContextFactory` | `src/Twig.Mcp/Services/WorkspaceContext.cs`, `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | S |
| T1.6 | Delete `StatusOrchestrator.cs` (contains both the class and `StatusSnapshot`) | `src/Twig.Domain/Services/Workspace/StatusOrchestrator.cs` | S |
| T1.7 | Update MCP tests: delete `StatusOrchestratorTests.cs`, update `McpResultBuilderTests.cs`, `ContextToolsStatusTests.cs`, `ReadToolsTestBase.cs`, `WorkspaceContextFactoryTests.cs` | `tests/` (5 files) | M |

**Acceptance Criteria**:
- [ ] `StatusOrchestrator.cs` deleted
- [ ] `StatusResult` DU exists with three cases matching `ActiveItemResult` pattern
- [ ] `twig_status` MCP tool returns identical JSON for all three states (no context, unreachable, success)
- [ ] All existing MCP tests pass
- [ ] No new warnings
- [ ] Build succeeds with AOT publish

### Issue 2: Rename SyncCoordinatorFactory to SyncCoordinatorPair

**Goal**: Rename the class, file, and all references to honestly communicate that this
is a pair holder, not a factory.

**Prerequisites**: Issue 1 (to avoid renaming the parameter in the about-to-be-deleted
`StatusOrchestrator`)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T2.1 | Rename class `SyncCoordinatorFactory` → `SyncCoordinatorPair` and rename file | `src/Twig.Domain/Services/Sync/SyncCoordinatorFactory.cs` → `SyncCoordinatorPair.cs` | S |
| T2.2 | Update `RefreshOrchestrator` constructor parameter | `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs` | S |
| T2.3 | Update CLI command constructor parameters (`StatusCommand`, `SetCommand`, `ShowCommand`, `TreeCommand`, `LinkCommand`) | `src/Twig/Commands/` (5 files) | S |
| T2.4 | Update DI registration in `CommandServiceModule` | `src/Twig/DependencyInjection/CommandServiceModule.cs` | S |
| T2.5 | Update MCP `WorkspaceContext` and `WorkspaceContextFactory` | `src/Twig.Mcp/Services/` (2 files) | S |
| T2.6 | Update all test files (~30 files) — mechanical rename | `tests/` | M |

**Acceptance Criteria**:
- [ ] No remaining references to `SyncCoordinatorFactory` in source or tests
- [ ] `SyncCoordinatorPair` class exists with identical `ReadOnly`/`ReadWrite` API
- [ ] All tests pass
- [ ] DI registration resolves both `SyncCoordinatorPair` and backward-compat `SyncCoordinator`
- [ ] No new warnings

## PR Groups

### PG-1: StatusOrchestrator Absorption + StatusSnapshot DU (deep)

**Scope**: Issue 1 — all tasks T1.1 through T1.7

**Classification**: Deep — few files (12), complex changes (new DU type, builder pattern,
MCP call site rewrite, test updates)

**Estimated LoC**: ~400 (new: ~80, modified: ~150, deleted: ~210)

**Estimated Files**: 12

**Successor**: PG-2

**Review focus**: Verify `StatusResult` DU cases are exhaustive; verify `McpResultBuilder`
output is identical; verify no `StatusOrchestrator` references remain.

### PG-2: SyncCoordinatorFactory → SyncCoordinatorPair Rename (wide)

**Scope**: Issue 2 — all tasks T2.1 through T2.6

**Classification**: Wide — many files (~35), mechanical changes (rename only)

**Estimated LoC**: ~300 (all modifications, no new/deleted logic)

**Estimated Files**: ~35

**Predecessor**: PG-1

**Review focus**: Verify no functional changes; confirm all references updated;
check DI registration resolves correctly.

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | StatusOrchestrator Absorption + StatusSnapshot DU | Issue 1: T1.1–T1.7 | None | Deep |
| PG-2 | SyncCoordinatorFactory → SyncCoordinatorPair Rename | Issue 2: T2.1–T2.6 | PG-1 | Wide |

### Execution Order

**PG-1 first, PG-2 second.** The sequencing is mandatory: Issue 1 must land before Issue 2 to avoid a redundant rename of the `SyncCoordinatorFactory` parameter in `StatusOrchestrator.cs` (which PG-1 deletes). Once PG-1 merges and the `StatusOrchestrator` file is gone, PG-2 is a purely mechanical rename with no logical coupling to PG-1's content.

### Validation Strategy

**PG-1 — StatusOrchestrator Absorption + StatusSnapshot DU**
- Build: `dotnet build` must pass with zero warnings (warnings-as-errors).
- Tests: `dotnet test` — all existing MCP and domain tests must pass.
- Key checks: no remaining references to `StatusOrchestrator` or `StatusSnapshot`; `StatusResult` DU covers `NoContext`, `Unreachable`, and `Success`; `twig_status` MCP tool returns semantically identical JSON for all three states; AOT publish (`dotnet publish`) succeeds.
- Estimated: ~400 LoC across 12 files.

**PG-2 — SyncCoordinatorFactory → SyncCoordinatorPair Rename**
- Build: `dotnet build` must pass with zero warnings.
- Tests: `dotnet test` — all tests pass (DI registration resolves `SyncCoordinatorPair` and backward-compat `SyncCoordinator`).
- Key checks: zero remaining references to `SyncCoordinatorFactory` in source or tests; `ReadOnly`/`ReadWrite` API unchanged; no behavioral changes.
- Estimated: ~300 LoC across ~35 files (all modifications, no new/deleted logic).

## References

- `docs/architecture/domain-model-critique.md` — Item 6 (Orchestrator Proliferation), Item 7 (Result Type Proliferation)
- ADO Epic #2119 — Domain Critique: Orchestrator Consolidation
- ADO Issue #1614 — Tiered TTL for sync coordinators (context for `SyncCoordinatorFactory`)
