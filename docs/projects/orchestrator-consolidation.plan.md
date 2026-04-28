# Orchestrator Consolidation — Epic #2119

> **Status**: 🔨 In Progress

## Executive Summary

Five orchestrator/coordinator patterns exist in the twig domain layer with overlapping dependency subsets: `StatusOrchestrator`, `SyncCoordinator`, `SyncCoordinatorFactory`, `RefreshOrchestrator`, and `SeedPublishOrchestrator`. A comprehensive call-site audit reveals that the actionable consolidation targets are narrow: **`StatusOrchestrator` should be absorbed** into its sole consumer (MCP `ContextTools.Status()`), and **`SyncCoordinatorFactory` should be renamed** to `SyncCoordinatorPair` to accurately reflect its pair-holder semantics. The remaining three orchestrators (`SyncCoordinator`, `RefreshOrchestrator`, `SeedPublishOrchestrator`) are well-factored 1:1 delegations with substantial business logic and should be left untouched. This plan eliminates the identified duplication between `StatusOrchestrator` and `ActiveItemResolver`, fixes the misleading factory naming, and documents the audit findings — all via separate, purely structural PRs with no behavioral changes.

## Background

### Current Architecture

The domain layer (`Twig.Domain`) contains six orchestrator/coordinator services that evolved independently:

| Component | Location | Lines | Deps | Consumers | Role |
|-----------|----------|-------|------|-----------|------|
| `SyncCoordinator` | `Services/Sync/` | 211 | 6 | 20+ sites | Core cache sync with ADO (staleness, batch fetch, protected writes) |
| `SyncCoordinatorFactory` | `Services/Sync/` | 43 | 7 | 14 src files | Two-tier TTL pair holder (ReadOnly / ReadWrite) |
| `RefreshOrchestrator` | `Services/Sync/` | 193 | 9 | 1 (RefreshCommand) | Full refresh lifecycle: WIQL fetch, conflicts, ancestor hydration |
| `SeedPublishOrchestrator` | `Services/Seed/` | 245 | 7+1 | 1 (SeedPublishCommand) | Transactional seed publish with topological ordering |
| `StatusOrchestrator` | `Services/Workspace/` | 88 | 6 | 1 (MCP ContextTools) | Thin wrapper: active item + pending changes + seeds |
| `SeedReconcileOrchestrator` | `Services/Seed/` | 110 | 3 | 1 (SeedReconcileCommand) | Orphan/stale seed link repair |

All six are **concrete sealed classes** with no interface abstraction, registered as singletons. DI registration is split between `CommandServiceModule.cs` (CLI layer) and `WorkspaceContextFactory.cs` (MCP layer, constructs directly without DI).

### Context

This work implements Item 6 from `docs/architecture/domain-model-critique.md`, which identifies the orchestrator proliferation as a domain modeling concern. The service folder reorganization (Item 5) has already been completed — orchestrators now live in semantically named subdirectories (`Sync/`, `Workspace/`, `Seed/`, `Navigation/`).

### Call-Site Audit

#### StatusOrchestrator Call Sites

| File | Method | Usage | Impact of Removal |
|------|--------|-------|-------------------|
| `src/Twig.Mcp/Tools/ContextTools.cs:100` | `Status()` | `ctx.StatusOrchestrator.GetSnapshotAsync(ct)` | **Must be migrated** — only functional consumer |
| `src/Twig.Mcp/Services/WorkspaceContext.cs:30` | Property | `StatusOrchestrator StatusOrchestrator { get; }` | Remove property + constructor param |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs:146-152` | `CreateContext()` | Constructs `new StatusOrchestrator(...)` | Remove construction |
| `tests/Twig.Domain.Tests/Services/Workspace/StatusOrchestratorTests.cs` | 6 tests | Direct unit tests | Delete file |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTestBase.cs:145-147` | `BuildContext()` | Constructs for test infrastructure | Remove from construction |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | 9 tests | Integration tests via `ContextTools.Status()` | Tests still pass (behavior preserved) |
| `tests/Twig.Mcp.Tests/Tools/MultiWorkspaceIsolationTests.cs` | Indirect | Via `ReadToolsTestBase.BuildContext()` | Indirect update via base class |
| `tests/Twig.Mcp.Tests/Services/WorkspaceContextFactoryTests.cs` | Factory test | References `StatusOrchestrator` property | Remove assertions |

**Critical finding**: The CLI `StatusCommand` does **not** use `StatusOrchestrator`. It performs identical work inline using `ActiveItemResolver` directly. `StatusOrchestrator` exists exclusively for the MCP layer.

**Secondary finding**: `StatusOrchestrator.SyncWorkingSetAsync()` is **dead code** — no production caller invokes it. Only one test exercises it.

#### SyncCoordinatorFactory Call Sites (Source — 14 files)

| File | Usage |
|------|-------|
| `src/Twig.Domain/Services/Sync/SyncCoordinatorFactory.cs` | Class definition |
| `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs` | Constructor dependency |
| `src/Twig.Domain/Services/Workspace/StatusOrchestrator.cs` | Constructor dependency |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | DI registration (6 refs) |
| `src/Twig/Commands/StatusCommand.cs` | Constructor dependency |
| `src/Twig/Commands/SetCommand.cs` | Constructor dependency (2 refs) |
| `src/Twig/Commands/ShowCommand.cs` | Constructor dependency |
| `src/Twig/Commands/TreeCommand.cs` | Constructor dependency |
| `src/Twig/Commands/LinkCommand.cs` | Constructor dependency |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Property (3 refs) |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Construction |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Via `ctx.SyncCoordinatorFactory` |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Via `ctx.SyncCoordinatorFactory` |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Via `ctx.SyncCoordinatorFactory` (2 refs) |

#### SyncCoordinatorFactory Call Sites (Tests — 33 files)

All test files construct `SyncCoordinatorFactory` as part of test harness setup. Key files include `SyncCoordinatorFactoryTests.cs` (5 dedicated tests), `StatusOrchestratorTests.cs`, `RefreshOrchestratorTests.cs`, `ReadToolsTestBase.cs`, and 25+ command test files that build the factory for command-level testing.

**Total impact of rename: ~47 files** across source and test projects. All changes are mechanical identifier substitution.

#### StatusSnapshot Consumers

| File | Usage |
|------|-------|
| `src/Twig.Domain/Services/Workspace/StatusOrchestrator.cs:65-88` | Type definition (co-located) |
| `src/Twig.Mcp/Services/McpResultBuilder.cs:35` | `FormatStatus(StatusSnapshot, string?)` |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | Indirect (via MCP tool output) |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Direct construction |

`StatusSnapshot` is consumed by exactly **2 production files** — the orchestrator that produces it and the `McpResultBuilder` that formats it. The type is useful as a data transfer object even without the orchestrator.

### Dependency Overlap Matrix

| Dependency | StatusOrch | SyncCoord | SyncFactory | RefreshOrch | SeedPublishOrch |
|------------|:----------:|:---------:|:-----------:|:-----------:|:---------------:|
| `IWorkItemRepository` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IAdoWorkItemService` | — | ✅ | ✅ | ✅ | ✅ |
| `IPendingChangeStore` | ✅ | ✅ | ✅ | ✅ | — |
| `IContextStore` | ✅ | — | — | ✅ | — |
| `ProtectedCacheWriter` | — | ✅ | ✅ | ✅ | — |
| `SyncCoordinatorFactory` | ✅ | — | (self) | ✅ | — |
| `WorkingSetService` | ✅ | — | — | ✅ | — |
| `ActiveItemResolver` | ✅ | — | — | — | — |
| `IIterationService` | — | — | — | ✅ | — |
| `IUnitOfWork` | — | — | — | — | ✅ |
| `ISeedLinkRepository` | — | — | — | — | ✅ |

The overlap is real but not actionable for consolidation. `RefreshOrchestrator` (9 deps) and `StatusOrchestrator` (6 deps) share 4 dependencies, but they serve entirely different purposes — refresh is a batch lifecycle operation while status is a point-in-time snapshot. The shared dependencies are fundamental infrastructure (`IWorkItemRepository`, `IContextStore`) that most domain services naturally depend on.

## Problem Statement

Three specific issues exist in the current orchestrator layer:

1. **StatusOrchestrator duplication**: `StatusOrchestrator.GetSnapshotAsync()` (~20 lines of unique logic) wraps `ActiveItemResolver.GetActiveItemAsync()` with pending change and seed loading, producing a `StatusSnapshot`. The CLI `StatusCommand` performs identical work inline without using the orchestrator. The orchestrator's only consumer is MCP `ContextTools.Status()`. This creates a maintenance hazard: changes to status logic must be synchronized between two independent code paths.

2. **SyncCoordinatorFactory misnaming**: The class holds two pre-built `SyncCoordinator` instances with different `cacheStaleMinutes` values (ReadOnly = longer TTL for display, ReadWrite = shorter TTL for mutations). The "Factory" name implies a creation pattern (create-on-demand), but the class is actually a named pair with clamping logic. This mismatch between name and behavior confuses new contributors.

3. **StatusOrchestrator dead code**: `SyncWorkingSetAsync()` on `StatusOrchestrator` has zero production callers. It's a 4-line method tested by one unit test but never invoked.

## Goals and Non-Goals

### Goals

1. **Eliminate the StatusOrchestrator class** by inlining its ~20 lines of data-gathering logic into MCP `ContextTools.Status()`, reducing the class count and removing the maintenance hazard of duplicated status resolution paths.
2. **Rename SyncCoordinatorFactory to SyncCoordinatorPair** so the type name accurately reflects its pair-holder semantics rather than implying a factory pattern.
3. **Remove dead code** — the unused `StatusOrchestrator.SyncWorkingSetAsync()` method and its test.
4. **Document the orchestrator audit findings** by updating architecture docs with the call-site inventory and rationale for keeping the three healthy orchestrators.
5. **Preserve all existing behavior** — zero behavioral changes, purely structural refactoring.

### Non-Goals

- **Consolidating RefreshOrchestrator, SeedPublishOrchestrator, or SeedReconcileOrchestrator** — these are well-factored 1:1 delegations with substantial logic (193–245 lines each). The audit confirms they should remain as-is.
- **Modifying SyncCoordinator internals** — this is load-bearing infrastructure with 20+ call sites and ~40 unit tests. No changes.
- **Expanding StatusSnapshot** — the type is useful but expanding it to cover the full CLI `StatusCommand` rendering needs is a separate concern (critique Item 7 / Item 8).
- **Introducing interfaces for orchestrators** — the concrete sealed class pattern is consistent with codebase conventions and appropriate for these internal services.
- **Refactoring StatusCommand** to use a shared orchestrator — the CLI command has extensive rendering logic that requires fine-grained control beyond what a snapshot provides.

## Requirements

### Functional

1. MCP `twig_status` tool must produce identical JSON output before and after the change.
2. All existing tests must pass without behavioral modifications (test structure may change, assertions must not).
3. `StatusSnapshot` type must remain available for `McpResultBuilder.FormatStatus()`.
4. `SyncCoordinatorPair.ReadOnly` and `.ReadWrite` properties must maintain identical semantics to the current `SyncCoordinatorFactory`.

### Non-Functional

1. No new dependencies introduced.
2. AOT compatibility preserved (`PublishAot=true`, `TrimMode=full`).
3. Zero behavioral changes — all PRs are purely structural.
4. Each PR is independently mergeable and independently revertible.

## Proposed Design

### Architecture Overview

The consolidation makes two structural changes:

```
BEFORE:
  MCP ContextTools.Status() → StatusOrchestrator → ActiveItemResolver
                                                  → IPendingChangeStore
                                                  → IWorkItemRepository

AFTER:
  MCP ContextTools.Status() → ActiveItemResolver
                             → IPendingChangeStore
                             → IWorkItemRepository
                             → McpResultBuilder.FormatStatus(StatusSnapshot)
```

`StatusSnapshot` is retained as a data transfer type but moves to its own file. The orchestrator class is eliminated; its ~20 lines of logic are inlined into `ContextTools.Status()`.

For the factory rename:

```
BEFORE: SyncCoordinatorFactory { ReadOnly, ReadWrite }
AFTER:  SyncCoordinatorPair    { ReadOnly, ReadWrite }
```

Pure identifier rename — no structural or behavioral changes.

### Key Components

#### 1. StatusSnapshot (retained, relocated)

`StatusSnapshot` moves from being co-located in `StatusOrchestrator.cs` to its own file at `src/Twig.Domain/Services/Workspace/StatusSnapshot.cs`. No changes to the type itself — it remains a sealed class with `HasContext`, `ActiveId`, `Item`, `PendingChanges`, `Seeds`, `UnreachableId`, `UnreachableReason`, `IsSuccess`, and the two factory methods `NoContext()` and `Unreachable(...)`.

#### 2. ContextTools.Status() (expanded)

The MCP tool method gains the inlined snapshot-building logic that was previously in `StatusOrchestrator.GetSnapshotAsync()`. The method will:
1. Resolve the workspace context (unchanged)
2. Read active work item ID from `IContextStore` (moved from orchestrator)
3. Resolve the item via `ActiveItemResolver.GetActiveItemAsync()` (moved from orchestrator)
4. Load pending changes from `IPendingChangeStore` (moved from orchestrator)
5. Load seeds from `IWorkItemRepository` (moved from orchestrator)
6. Build a `StatusSnapshot` (moved from orchestrator)
7. Format and return via `McpResultBuilder.FormatStatus()` (unchanged)

This is ~15 lines of straightforward data-gathering code. The `ContextTools` class already has access to all required services via `WorkspaceContext`.

#### 3. WorkspaceContext (simplified)

The `StatusOrchestrator` property and constructor parameter are removed. All remaining properties are unchanged.

#### 4. SyncCoordinatorPair (renamed)

The class, file, XML doc comments, and doc references are updated from `SyncCoordinatorFactory` to `SyncCoordinatorPair`. No changes to properties, constructor logic, or clamping behavior.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Inline into ContextTools rather than create StatusSnapshotBuilder | The logic is 15 lines and has exactly one caller. A new builder class would be over-engineering. |
| Keep StatusSnapshot as a Domain type | `McpResultBuilder.FormatStatus()` depends on it, and it's a clean data contract. Moving it to MCP would create a cross-layer dependency issue if CLI ever needs it. |
| Don't absorb into ActiveItemResolver | The resolver has a single responsibility (item resolution). Adding pending changes and seeds would violate SRP. The critique suggested this option but the audit shows inlining is cleaner. |
| Rename to "Pair" not "Set" or "Coordinators" | "Pair" precisely communicates "exactly two named instances." "Set" implies arbitrary count; "Coordinators" is ambiguous. |
| Separate PRs for each change | The epic's containment practices explicitly require this. Structural changes must not mix with behavioral changes or with each other. |

## Alternatives Considered

### Alternative A: Absorb StatusOrchestrator into ActiveItemResolver

Add a `GetStatusSnapshotAsync()` method to `ActiveItemResolver` that includes pending changes and seeds.

**Pros**: Single class for "resolve active item + related data."
**Cons**: Violates SRP — `ActiveItemResolver` currently handles only item resolution (cache-first + ADO fallback). Adding pending changes and seeds mixes concerns. The resolver is used by 15+ callers who don't need snapshot data.

**Rejected**: Clean separation of concerns outweighs class count reduction.

### Alternative B: Make StatusCommand use StatusOrchestrator

Register `StatusOrchestrator` in CLI DI and refactor `StatusCommand` to delegate to it.

**Pros**: Eliminates duplication by unifying both consumers on one implementation.
**Cons**: `StatusCommand` has extensive rendering logic requiring data beyond what `StatusSnapshot` provides (links, children, parent chain, field definitions, child progress, git context). `StatusSnapshot` would need significant expansion, turning this from a structural refactor into a behavioral change.

**Rejected**: The epic explicitly prohibits behavioral changes. This approach is a potential future direction (critique Item 7/8) but not in scope here.

## Dependencies

### Internal

- No dependencies on other in-flight work items.
- The service folder reorganization (critique Item 5) is already complete — orchestrators live in their target directories.

### Sequencing

- **PG-1 (StatusOrchestrator absorption) and PG-2 (Factory rename) are independent** and can proceed in any order or in parallel.
- If both PRs are in flight simultaneously, the second to merge will need a trivial conflict resolution (both touch `WorkspaceContext.cs` and `WorkspaceContextFactory.cs`).

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|:----------:|:------:|------------|
| MCP `twig_status` behavior regression | Low | High | ContextToolsStatusTests (9 tests) validate identical JSON output. No assertion changes needed — only setup wiring changes. |
| Merge conflicts between PG-1 and PG-2 | Medium | Low | Both touch overlapping files. The second PR to merge resolves trivially (remove a deleted property vs rename a type). |
| SyncCoordinatorFactory rename breaks downstream forks | Low | Low | No known forks. All references are internal. |
| Missed call site for StatusOrchestrator | Very Low | Medium | Exhaustive grep confirms exactly 1 production consumer (MCP ContextTools). The type is not registered in CLI DI at all. |

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should `StatusSnapshot` gain a `WorkspaceKey` property now that it's being restructured, or defer to a future PR? | Low | Defer — the workspace is passed separately to `FormatStatus()` and that pattern is consistent across all MCP result builders. |
| 2 | Should the `SyncCoordinatorPair` rename also update the DI backward-compat registration (`services.AddSingleton<SyncCoordinator>(sp => sp.GetRequiredService<SyncCoordinatorPair>().ReadWrite)`)? | Low | Yes — the registration comment should reference the new name. The registration itself stays. |
| 3 | Should the 6 architecture docs referencing `SyncCoordinatorFactory` be updated in PG-2 or a separate docs-only PR? | Low | Include in PG-2 — the rename is mechanical and docs should stay in sync. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/Workspace/StatusSnapshot.cs` | Extracted `StatusSnapshot` type (currently co-located in `StatusOrchestrator.cs`) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Tools/ContextTools.cs` | Inline snapshot-building logic in `Status()` method |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Remove `StatusOrchestrator` property + constructor param |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Remove `StatusOrchestrator` construction |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Update `using` for `StatusSnapshot` new location (if namespace changes) |
| `src/Twig.Domain/Services/Sync/SyncCoordinatorFactory.cs` | Rename class to `SyncCoordinatorPair` |
| `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs` | Update `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Update DI registration type names |
| `src/Twig/Commands/StatusCommand.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig/Commands/SetCommand.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig/Commands/ShowCommand.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig/Commands/TreeCommand.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig/Commands/LinkCommand.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig.Mcp/Tools/ReadTools.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `src/Twig.Mcp/Tools/CreationTools.cs` | `SyncCoordinatorFactory` → `SyncCoordinatorPair` |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTestBase.cs` | Remove `StatusOrchestrator` construction; rename factory |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsStatusTests.cs` | Test wiring updates (assertions unchanged) |
| `tests/Twig.Mcp.Tests/Services/WorkspaceContextFactoryTests.cs` | Remove `StatusOrchestrator` assertions; rename factory |
| `tests/Twig.Mcp.Tests/Tools/MultiWorkspaceIsolationTests.cs` | Indirect update via base class changes |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Update `using` for `StatusSnapshot` |
| `tests/Twig.Domain.Tests/Services/Sync/SyncCoordinatorFactoryTests.cs` | Rename references |
| `tests/Twig.Domain.Tests/Services/Sync/RefreshOrchestratorTests.cs` | Rename references |
| `tests/Twig.Domain.Tests/Services/Workspace/StatusOrchestratorTests.cs` | Rename references (only for PG-2 if PG-1 hasn't deleted it yet) |
| ~25 additional CLI test files | Mechanical `SyncCoordinatorFactory` → `SyncCoordinatorPair` rename |
| `docs/architecture/domain-model-critique.md` | Update with audit findings; rename references |
| `docs/architecture/overview.md` | Rename references |
| `docs/architecture/mcp-server.md` | Rename references |
| `docs/architecture/data-layer.md` | Rename references |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig.Domain/Services/Workspace/StatusOrchestrator.cs` | Absorbed into MCP inline logic; `StatusSnapshot` extracted to own file |
| `tests/Twig.Domain.Tests/Services/Workspace/StatusOrchestratorTests.cs` | Tests for deleted class; coverage preserved by MCP integration tests |

## ADO Work Item Structure

### Issue 1: Absorb StatusOrchestrator into MCP Inline Logic

**Goal**: Eliminate `StatusOrchestrator` by inlining its ~20 lines of data-gathering logic into MCP `ContextTools.Status()`. Preserve `StatusSnapshot` as a standalone type. Remove dead `SyncWorkingSetAsync` method.

**Prerequisites**: None.

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1.1 | Extract `StatusSnapshot` to standalone file `StatusSnapshot.cs` in `Services/Workspace/` | `StatusOrchestrator.cs`, new `StatusSnapshot.cs` | S |
| T1.2 | Inline `GetSnapshotAsync` logic into `ContextTools.Status()` — read active ID, resolve via `ActiveItemResolver`, load pending changes and seeds, build `StatusSnapshot` | `ContextTools.cs` | S |
| T1.3 | Remove `StatusOrchestrator` from `WorkspaceContext` (property, constructor param) and `WorkspaceContextFactory` (construction lines) | `WorkspaceContext.cs`, `WorkspaceContextFactory.cs` | S |
| T1.4 | Delete `StatusOrchestrator.cs` (class is now empty after snapshot extraction) | `StatusOrchestrator.cs` | XS |
| T1.5 | Update MCP test infrastructure: remove `StatusOrchestrator` from `ReadToolsTestBase.BuildContext()`, update `WorkspaceContextFactoryTests`, delete `StatusOrchestratorTests.cs` | 4 test files | M |

**Acceptance Criteria**:
- [ ] `StatusOrchestrator.cs` is deleted
- [ ] `StatusSnapshot.cs` exists as a standalone file with identical type definition
- [ ] MCP `twig_status` returns identical JSON (verified by unchanged `ContextToolsStatusTests` assertions)
- [ ] All existing tests pass (domain + MCP + CLI)
- [ ] No new warnings (`TreatWarningsAsErrors`)
- [ ] `dotnet build` succeeds with AOT publish

### Issue 2: Rename SyncCoordinatorFactory to SyncCoordinatorPair

**Goal**: Rename the class to accurately reflect its pair-holder semantics. The class holds two pre-built `SyncCoordinator` instances with different TTLs — it does not create coordinators on demand.

**Prerequisites**: None (independent of Issue 1).

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T2.1 | Rename class and file: `SyncCoordinatorFactory` → `SyncCoordinatorPair`, `SyncCoordinatorFactory.cs` → `SyncCoordinatorPair.cs`. Update XML doc comments. | 1 file (rename) | S |
| T2.2 | Update all source references across `src/` (~14 files): domain services, CLI commands, MCP tools, DI registration, `WorkspaceContext` | 14 source files | M |
| T2.3 | Update all test references across `tests/` (~33 files): test bases, command tests, domain tests, MCP tests | 33 test files | M |
| T2.4 | Update architecture docs referencing `SyncCoordinatorFactory` | 4 doc files | S |

**Acceptance Criteria**:
- [ ] No file or symbol named `SyncCoordinatorFactory` exists in the codebase
- [ ] `SyncCoordinatorPair.ReadOnly` and `.ReadWrite` behave identically to before
- [ ] All existing tests pass with zero assertion changes
- [ ] `dotnet build` succeeds
- [ ] Doc references updated

### Issue 3: Document Orchestrator Audit Findings

**Goal**: Capture the call-site audit results and consolidation rationale in architecture docs so future contributors understand why the remaining orchestrators were kept.

**Prerequisites**: Issues 1 and 2 (audit findings reference the post-consolidation state).

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T3.1 | Update `domain-model-critique.md` Item 6 with audit findings: mark StatusOrchestrator as resolved, SyncCoordinatorFactory rename as resolved, document why RefreshOrchestrator/SeedPublishOrchestrator/SeedReconcileOrchestrator are retained | `domain-model-critique.md` | S |
| T3.2 | Add summary doc comments to retained orchestrators explaining their scope, consumer count, and why they exist as separate classes | `RefreshOrchestrator.cs`, `SeedPublishOrchestrator.cs`, `SeedReconcileOrchestrator.cs` | S |

**Acceptance Criteria**:
- [ ] `domain-model-critique.md` Item 6 reflects completed audit
- [ ] Each retained orchestrator has a doc comment explaining its purpose and consumer
- [ ] `dotnet build` succeeds (no warning from malformed XML docs)

## PR Groups

### PG-1: StatusOrchestrator Absorption

**Issues covered**: Issue 1 (all tasks)
**Classification**: **Deep** — few files (≤12), complex logic (inlining orchestrator, rewiring DI and test infrastructure)
**Estimated LoC**: ~300 (deletions dominate — removing 88-line class + 145-line test file; additions are ~30 lines of inlined logic + new StatusSnapshot.cs file)
**Files**: ≤12
**Successors**: PG-3

### PG-2: SyncCoordinatorFactory → SyncCoordinatorPair Rename

**Issues covered**: Issue 2 (all tasks)
**Classification**: **Wide** — many files (~51), mechanical rename
**Estimated LoC**: ~250 (identifier substitution across ~47 source/test files + 4 doc files)
**Files**: ~51
**Successors**: PG-3

### PG-3: Audit Documentation

**Issues covered**: Issue 3 (all tasks)
**Classification**: **Deep** — few files (4), thoughtful content
**Estimated LoC**: ~50 (doc comments and architecture doc updates)
**Files**: ≤5
**Predecessors**: PG-1, PG-2
**Successors**: None

### Execution Order

```
PG-1 (StatusOrchestrator) ──┐
                             ├──→ PG-3 (Documentation)
PG-2 (Factory rename)  ─────┘
```

PG-1 and PG-2 are fully independent and can be developed/reviewed/merged in parallel. PG-3 depends on both being complete since it documents the final state.

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | StatusOrchestrator Absorption | Issue 1 (T1.1–T1.5) | None | Deep |
| PG-2 | SyncCoordinatorFactory Rename | Issue 2 (T2.1–T2.4) | None | Wide |
| PG-3 | Audit Documentation | Issue 3 (T3.1–T3.2) | PG-1, PG-2 | Deep |

### Execution Order

```
PG-1 (StatusOrchestrator Absorption) ──┐
                                        ├──→ PG-3 (Audit Documentation)
PG-2 (SyncCoordinatorFactory Rename) ──┘
```

PG-1 and PG-2 are fully independent and can be developed, reviewed, and merged in parallel. PG-3 documents the post-consolidation state and therefore depends on both PG-1 and PG-2 being merged first.

### Validation Strategy

**PG-1 — StatusOrchestrator Absorption**
- `dotnet build` must succeed with no warnings (AOT + trim)
- `ContextToolsStatusTests` (9 tests) must pass with zero assertion changes — they validate that `twig_status` JSON output is identical
- `SyncCoordinatorFactoryTests`, `RefreshOrchestratorTests`, all CLI command tests must continue to pass (unchanged by this PG)
- `StatusOrchestratorTests.cs` is deleted — its coverage is subsumed by the MCP integration tests
- Confirm `StatusOrchestrator.cs` is deleted and `StatusSnapshot.cs` exists

**PG-2 — SyncCoordinatorFactory Rename**
- `dotnet build` must succeed with no warnings
- Full test suite must pass with zero assertion changes (only identifier substitutions)
- `grep -r "SyncCoordinatorFactory" src/ tests/` must return zero results
- Confirm `SyncCoordinatorPair.ReadOnly` and `.ReadWrite` behave identically (covered by existing `SyncCoordinatorFactoryTests` renamed to `SyncCoordinatorPairTests`)

**PG-3 — Audit Documentation**
- `dotnet build` must succeed (XML doc comment validity)
- `domain-model-critique.md` Item 6 reflects completed audit
- Each retained orchestrator (`RefreshOrchestrator`, `SeedPublishOrchestrator`, `SeedReconcileOrchestrator`) has a summary doc comment

## References

- `docs/architecture/domain-model-critique.md` — Item 6: Orchestrator/Coordinator proliferation
- Epic #2119 — Domain Critique: Orchestrator Consolidation
- Issue #1614 — Original tiered TTL implementation (created `SyncCoordinatorFactory`)

