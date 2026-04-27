# WorkItem Aggregate Consolidation

**Epic:** #2114 — Domain Critique: WorkItem Aggregate Consolidation
**Status:** 📋 Planning
**Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive Summary

The `WorkItem` class in `Twig.Domain.Aggregates` has accumulated three copy methods (`WithSeedFields`, `WithParentId`, `WithIsSeed`) that each manually reconstruct all properties with subtly different state-preservation semantics — a guaranteed bug factory as properties are added. It also hosts a static mutable `_seedIdCounter` that couples all instances and makes parallel tests nondeterministic. This plan introduces a `WorkItemCopier` helper to centralize all copy logic (tested once for property preservation), extracts `CreateSeed` and the seed ID counter from `WorkItem` into the existing `SeedFactory` service (converted from static to DI-registered), and adds a theory-based test harness that validates property preservation across all copy paths before making structural changes.

---

## Background

### Current Architecture

`WorkItem` (249 lines, `src/Twig.Domain/Aggregates/WorkItem.cs`) is the root aggregate for Azure DevOps work items. It exposes:

- **Identity & metadata** — `Id`, `Type`, `Title`, `State`, `AssignedTo`, `IterationPath`, `AreaPath`, `ParentId`, `Revision`, `IsSeed`, `SeedCreatedAt`, `LastSyncedAt`
- **Dirty tracking** — `IsDirty` (private set), `SetDirty()` (internal)
- **Field bag** — `Fields` (read-only dictionary), `ImportFields`/`SetField` (internal), `TryGetField` (internal)
- **Pending notes** — `PendingNotes` (read-only list), `AddPendingNote` (internal)
- **Direct mutation** — `ChangeState()`, `UpdateField()`, `AddNote()`, `MarkSynced()`
- **Copy methods** — `WithSeedFields()`, `WithParentId()`, `WithIsSeed()`
- **Seed factory** — `CreateSeed()` (static), `InitializeSeedCounter()` (static), `_seedIdCounter` (static mutable)

The `SeedFactory` service (`src/Twig.Domain/Services/SeedFactory.cs`) is a `static class` that validates parent/child type rules and then delegates actual seed creation to `WorkItem.CreateSeed()`. It does not own the counter.

### Copy Method Divergence Analysis

The three `With*` methods each manually reconstruct all 12 init properties. Their state-preservation semantics differ:

| Behavior | `WithSeedFields` | `WithParentId` | `WithIsSeed` |
|----------|:-:|:-:|:-:|
| Preserves `IsDirty` | ❌ | ✅ | ❌ |
| Preserves `Fields` | ❌ (replaced) | ✅ | ✅ |
| Preserves `PendingNotes` | ❌ | ❌ | ❌ |
| Preserves `Revision` | ✅ | ✅ | ✅ |
| Overrides `Title` | ✅ (param) | — | — |
| Overrides `ParentId` | — | ✅ (param) | — |
| Overrides `IsSeed` | — | — | ✅ (param) |

The `WithIsSeed` not preserving `IsDirty` is documented as intentional (used on fetched-back items). `WithSeedFields` not preserving `IsDirty` is also intentional (editor-driven field replacement resets dirty). But these decisions are encoded implicitly in duplicated code rather than explicitly in a centralized copy mechanism.

### Call-Site Audit

#### `WithSeedFields` — 3 production call sites

| File | Method | Context |
|------|--------|---------|
| `src/Twig/Commands/NewCommand.cs:114` | Editor flow | Applies editor-parsed fields to seed before ADO create |
| `src/Twig/Commands/SeedEditCommand.cs:74` | Edit flow | Applies editor-parsed fields to existing local seed |
| `src/Twig/Commands/SeedNewCommand.cs:99` | Editor flow | Applies editor-parsed fields to seed before local save |

#### `WithParentId` — 1 production call site

| File | Method | Context |
|------|--------|---------|
| (none in production) | — | Only used in tests. Listed in SetCommandTests for test fixtures. |

Note: `WithParentId` has **zero** production call sites. It is used only in test setup (`SetCommandTests.cs:149,171,352,353`). This is an important finding — the method exists for future use or was needed during development but never made it into production flow.

#### `WithIsSeed` — 2 production call sites

| File | Method | Context |
|------|--------|---------|
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs:130` | Post-create | Marks fetched-back ADO item as seed provenance |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs:174` | Post-refresh | Re-marks refreshed item as seed provenance |

#### `WorkItem.CreateSeed` — 2 production call sites (via SeedFactory)

| File | Method | Context |
|------|--------|---------|
| `src/Twig.Domain/Services/SeedFactory.cs:67` | `Create()` | Creates seed under parent context |
| `src/Twig.Domain/Services/SeedFactory.cs:93` | `CreateUnparented()` | Creates seed with explicit paths |

#### `WorkItem.InitializeSeedCounter` — 2 production call sites

| File | Method | Context |
|------|--------|---------|
| `src/Twig/Commands/SeedChainCommand.cs:69` | Chain creation | Initializes counter before batch seed creation |
| `src/Twig/Commands/SeedNewCommand.cs:67` | Seed creation | Initializes counter before single seed creation |

#### `ImportFields` — 3 production call sites (excludes WorkItem internal use)

| File | Method | Context |
|------|--------|---------|
| `tests/Twig.TestKit/WorkItemBuilder.cs:85` | `Build()` | Test fixture construction |
| (WorkItem internal) | `WithSeedFields`, `WithParentId`, `WithIsSeed` | Copy methods |
| (no other production) | — | — |

#### `SetField` — 5 production call sites

| File | Method | Context |
|------|--------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs:245` | `CreateSeed` | Sets System.AssignedTo on seed |
| `src/Twig.Mcp/Tools/CreationTools.cs:83` | Unparented create | Sets System.Description on seed |
| `src/Twig.Mcp/Tools/CreationTools.cs:264` | Parented create | Sets System.Description on seed |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs:498` | Hydration | Restores fields from DB |
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs:55` | ADO mapping | Imports fields from ADO response |

---

## Problem Statement

1. **Copy method divergence is a bug factory.** Three `With*` methods each manually list all 12 init properties. When a new property is added to `WorkItem`, each method must be independently updated — and the dirty/field semantics differ subtly per method, making it easy to get wrong silently.

2. **Static mutable state in a domain entity.** `_seedIdCounter` is a `static int` with `Interlocked` access inside `WorkItem`. This couples all `WorkItem` instances globally, makes parallel test execution nondeterministic, and violates the principle that domain entities should not contain infrastructure concerns like ID generation.

3. **Seed creation responsibility is split.** `SeedFactory` validates type rules and path inheritance, but `WorkItem.CreateSeed` actually constructs the instance and manages the counter. This split makes the creation responsibility unclear and prevents proper unit testing of `SeedFactory` without side-effecting global state.

---

## Goals and Non-Goals

### Goals

1. **Single copy path:** All `With*` methods delegate to a centralized `WorkItemCopier` that copies all properties exactly once, with explicit parameters controlling dirty/field behavior.
2. **Property preservation guarantee:** A theory-based test validates that every `WorkItem` property is preserved (or intentionally overridden) across all copy paths. Adding a new property without updating the copier causes a test failure.
3. **No static mutable state in WorkItem:** The seed ID counter moves to a dedicated `ISeedIdCounter` service, testable and injectable.
4. **Unified seed creation:** `SeedFactory` owns the full seed creation lifecycle — type validation, path inheritance, ID allocation, and instance construction.

### Non-Goals

- **Field storage refactoring** — The `_fields` dictionary, `ImportFields`/`SetField` internal access, and the field bag design are out of scope.
- **Identity/init pattern changes** — The `init` property pattern and `WorkItem` constructor design are not changed.
- **ChangeState validation** — Adding process-aware validation to `ChangeState` is tracked separately.
- **PendingNotes copy behavior** — None of the current `With*` methods copy `PendingNotes`, and no current use case requires it. This is deferred.
- **Command queue pattern** — Tracked separately under Epic #2115.

---

## Requirements

### Functional

1. `WorkItemCopier.Copy()` must accept explicit overrides for title, parentId, isSeed, fields, and dirty-preservation, producing a correctly-initialized `WorkItem`.
2. All three `With*` methods must delegate to `WorkItemCopier.Copy()` with no inline property copying.
3. `ISeedIdCounter` must support `Initialize(int minId)` and `Next()` operations, thread-safe.
4. `SeedFactory` must be a non-static class registered in DI, accepting `ISeedIdCounter` via constructor.
5. `WorkItem.CreateSeed`, `WorkItem.InitializeSeedCounter`, and `WorkItem._seedIdCounter` must be removed.

### Non-Functional

1. No behavioral changes to any existing `With*` method — the refactor is purely structural.
2. All existing tests must continue to pass (modulo call-site updates for `CreateSeed` → `SeedFactory`).
3. AOT compatibility must be maintained — no reflection, no dynamic dispatch.
4. `TreatWarningsAsErrors` must remain clean — no new warnings.

---

## Proposed Design

### Architecture Overview

```
WorkItem (aggregate)
  ├── WithSeedFields()  ──┐
  ├── WithParentId()    ──┼── delegate to ──► WorkItemCopier.Copy()
  └── WithIsSeed()      ──┘

SeedFactory (DI service)
  ├── Create()           ── validates types, calls CreateSeedInternal()
  ├── CreateUnparented() ── validates title, calls CreateSeedInternal()
  └── (private) CreateSeedInternal() ── uses ISeedIdCounter.Next()

ISeedIdCounter (DI service)
  ├── Initialize(int minId)
  └── Next() → int
```

### Key Components

#### 1. WorkItemCopier (`src/Twig.Domain/Aggregates/WorkItemCopier.cs`)

A `static` class with a single `Copy` method that handles all property transfer:

```csharp
internal static class WorkItemCopier
{
    internal static WorkItem Copy(
        WorkItem source,
        string? titleOverride = null,
        bool overrideParentId = false,
        int? parentIdValue = null,
        bool? isSeedOverride = null,
        IReadOnlyDictionary<string, string?>? fieldsOverride = null,
        bool preserveExistingFields = true,
        bool preserveDirty = false)
    {
        var copy = new WorkItem
        {
            Id = source.Id,
            Type = source.Type,
            Title = titleOverride ?? source.Title,
            State = source.State,
            AssignedTo = source.AssignedTo,
            IterationPath = source.IterationPath,
            AreaPath = source.AreaPath,
            ParentId = overrideParentId ? parentIdValue : source.ParentId,
            IsSeed = isSeedOverride ?? source.IsSeed,
            SeedCreatedAt = source.SeedCreatedAt,
            LastSyncedAt = source.LastSyncedAt,
        };

        if (source.Revision > 0) copy.MarkSynced(source.Revision);
        if (preserveExistingFields) copy.ImportFields(source.Fields);
        if (fieldsOverride is not null) copy.ImportFields(fieldsOverride);
        if (preserveDirty && source.IsDirty) copy.SetDirty();

        return copy;
    }
}
```

The `overrideParentId` bool is necessary because `ParentId` is `int?` — we need to distinguish "set to null" from "don't change." The alternative (an `Optional<int?>` wrapper) was rejected as unnecessary infrastructure for three call sites.

#### 2. Refactored With* Methods

Each method becomes a one-liner delegating to `WorkItemCopier.Copy()`:

```csharp
public WorkItem WithSeedFields(string title, IReadOnlyDictionary<string, string?> fields) =>
    WorkItemCopier.Copy(this, titleOverride: title,
        preserveExistingFields: false, fieldsOverride: fields);

public WorkItem WithParentId(int? newParentId) =>
    WorkItemCopier.Copy(this, overrideParentId: true,
        parentIdValue: newParentId, preserveDirty: true);

public WorkItem WithIsSeed(bool isSeed) =>
    WorkItemCopier.Copy(this, isSeedOverride: isSeed);
```

#### 3. ISeedIdCounter (`src/Twig.Domain/Interfaces/ISeedIdCounter.cs`)

```csharp
public interface ISeedIdCounter
{
    void Initialize(int minExistingId);
    int Next();
}
```

#### 4. SeedIdCounter (`src/Twig.Domain/Services/SeedIdCounter.cs`)

```csharp
internal sealed class SeedIdCounter : ISeedIdCounter
{
    private int _counter;

    public void Initialize(int minExistingId) =>
        Interlocked.Exchange(ref _counter, Math.Min(minExistingId, 0));

    public int Next() => Interlocked.Decrement(ref _counter);
}
```

#### 5. SeedFactory (refactored to non-static)

```csharp
public sealed class SeedFactory(ISeedIdCounter seedIdCounter)
{
    public Result<WorkItem> Create(string title, WorkItem? parentContext,
        ProcessConfiguration processConfig, WorkItemType? typeOverride = null,
        string? assignedTo = null)
    {
        // ... existing validation logic unchanged ...
        return Result.Ok(CreateSeedInternal(childType, title,
            parentContext?.Id, parentContext?.AreaPath ?? default,
            parentContext?.IterationPath ?? default, assignedTo));
    }

    public Result<WorkItem> CreateUnparented(string title, WorkItemType type,
        AreaPath areaPath, IterationPath iterationPath,
        string? assignedTo = null, int? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Fail<WorkItem>("Title cannot be empty.");
        return Result.Ok(CreateSeedInternal(type, title, parentId,
            areaPath, iterationPath, assignedTo));
    }

    private WorkItem CreateSeedInternal(WorkItemType type, string title,
        int? parentId, AreaPath areaPath, IterationPath iterationPath,
        string? assignedTo)
    {
        var seed = new WorkItem
        {
            Id = seedIdCounter.Next(),
            Type = type, Title = title, IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = parentId, AreaPath = areaPath,
            IterationPath = iterationPath, AssignedTo = assignedTo,
        };
        if (!string.IsNullOrWhiteSpace(assignedTo))
            seed.SetField("System.AssignedTo", assignedTo);
        return seed;
    }
}
```

### Design Decisions

1. **`WorkItemCopier` is `static` and `internal`** — It has no dependencies, operates purely on data, and is only called from `WorkItem.With*` methods. Making it static keeps the design simple. Making it internal prevents external callers from bypassing the `With*` API.

2. **`overrideParentId` bool pattern over `Optional<T>`** — Three call sites don't justify introducing a generic `Optional<int?>` type. The bool is explicit, readable, and zero-allocation.

3. **`SeedFactory` uses primary constructor for DI** — Follows the project's established convention for DI injection (e.g., `SeedPublishOrchestrator`, `RefreshOrchestrator`).

4. **`ISeedIdCounter` is a separate interface, not folded into `SeedFactory`** — Keeps seed ID allocation testable independently. Tests can inject a deterministic counter without needing the full SeedFactory.

5. **Test-side migration from `WorkItem.CreateSeed()` to `WorkItemBuilder.AsSeed()`** — Most test files using `WorkItem.CreateSeed()` only need a seed WorkItem for fixture setup. `WorkItemBuilder(-1, "title").AsSeed().Build()` is equivalent and avoids coupling tests to static factory methods. Tests specifically validating seed ID generation will use `ISeedIdCounter` directly.

---

## Dependencies

### Internal

- Epic #2115 (Command Queue Simplification) — runs in parallel but touches overlapping code in `WorkItem.cs`. Must coordinate merges. Neither depends on the other.
- `WorkItemBuilder` in `Twig.TestKit` — will need updating if `WorkItem.CreateSeed()` is removed (it doesn't use it, so no change needed).

### Sequencing

- Issue 1 (Test Harness) must complete before Issue 2 (WorkItemCopier) — the tests validate current behavior before structural changes.
- Issue 3 (SeedFactory) is independent of Issues 1-2 and can run in parallel.

---

## Impact Analysis

| Component | Impact |
|-----------|--------|
| `WorkItem.cs` | Copy methods reduced to one-liners; `CreateSeed`/`InitializeSeedCounter`/`_seedIdCounter` removed |
| `SeedFactory.cs` | Converted from static to instance class; owns seed creation end-to-end |
| `TwigServiceRegistration.cs` | 2 new registrations (`ISeedIdCounter`, `SeedFactory`) |
| `SeedNewCommand.cs` | Inject `SeedFactory`; remove `WorkItem.InitializeSeedCounter` call |
| `SeedChainCommand.cs` | Inject `SeedFactory`; remove `WorkItem.InitializeSeedCounter` call |
| `NewCommand.cs` | Inject `SeedFactory` (already uses `SeedFactory.CreateUnparented`) |
| `CreationTools.cs` (MCP) | Inject `SeedFactory` |
| ~15 test files | Replace `WorkItem.CreateSeed()` with `WorkItemBuilder.AsSeed()` |
| `SeedFactoryTests.cs` | Update to test instance-based `SeedFactory` with injected counter |

### Backward Compatibility

- All public API surfaces (`With*` methods) retain identical signatures and behavior.
- `SeedFactory.Create/CreateUnparented` retain identical signatures (now instance methods).
- No serialization changes — `WorkItem` shape is unchanged.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|:---:|:---:|------|
| New `WorkItem` property added without updating `WorkItemCopier` | Medium | High | Theory test explicitly lists all properties; new property without test data → failure |
| Merge conflict with Epic #2115 (command queue simplification) | Medium | Low | Both touch `WorkItem.cs` but in different regions (copy methods vs. mutation methods). Coordinate PR merge order. |
| Test migration misses a `WorkItem.CreateSeed` call site | Low | Low | Compiler error — removing the static method causes build failures at all remaining call sites |
| `SeedFactory` DI registration missing in MCP or TUI entry point | Low | Medium | `TwigServiceRegistration.AddTwigCoreServices()` is shared by all entry points. Add integration test. |

---

## Open Questions

1. **[Low] Should `WorkItemCopier.Copy` also accept a `PendingNotes` preservation flag?** — No current `With*` method copies pending notes, and no use case requires it. Deferring until a concrete need arises. The copier's design supports adding this trivially later.

2. **[Low] Should `WithParentId` remain on `WorkItem` given zero production call sites?** — It's used in tests and may be needed in future parent-reparenting features. Keeping it is low-cost and removing it would require test refactoring with no benefit.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItemCopier.cs` | Centralized copy logic for all `With*` methods |
| `src/Twig.Domain/Interfaces/ISeedIdCounter.cs` | Interface for seed ID allocation |
| `src/Twig.Domain/Services/SeedIdCounter.cs` | Thread-safe seed ID counter implementation |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopierTests.cs` | Property preservation theory tests + copier unit tests |
| `tests/Twig.Domain.Tests/Services/SeedIdCounterTests.cs` | Counter initialization and thread-safety tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs` | `With*` methods delegate to `WorkItemCopier`; remove `CreateSeed`, `InitializeSeedCounter`, `_seedIdCounter` |
| `src/Twig.Domain/Services/SeedFactory.cs` | Convert from `static class` to `sealed class` with DI; absorb `CreateSeedInternal` logic |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `ISeedIdCounter` → `SeedIdCounter` and `SeedFactory` |
| `src/Twig/Commands/SeedNewCommand.cs` | Inject `SeedFactory` + `ISeedIdCounter`; remove `WorkItem.InitializeSeedCounter` call |
| `src/Twig/Commands/SeedChainCommand.cs` | Inject `SeedFactory` + `ISeedIdCounter`; remove `WorkItem.InitializeSeedCounter` call |
| `src/Twig/Commands/NewCommand.cs` | Inject `SeedFactory` (static → instance method call) |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Inject `SeedFactory` (static → instance method call) |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Move `CreateSeed`/`InitializeSeedCounter` tests to `SeedFactoryTests`/`SeedIdCounterTests` |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs` | Update to validate copier-based implementation |
| `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` | Update to test instance-based `SeedFactory` with injected counter |
| `tests/Twig.Domain.Tests/ReadModels/WorkspaceTests.cs` | Replace `WorkItem.CreateSeed()` with `WorkItemBuilder.AsSeed()` |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | Replace `WorkItem.CreateSeed()` with `WorkItemBuilder.AsSeed()` |
| `tests/Twig.Infrastructure.Tests/Persistence/PhantomDirtyCleansingTests.cs` | Replace `WorkItem.CreateSeed()`/`InitializeSeedCounter` |
| `tests/Twig.Infrastructure.Tests/Persistence/ClearDirtyFlagTests.cs` | Replace `WorkItem.CreateSeed()`/`InitializeSeedCounter` |
| `tests/Twig.Infrastructure.Tests/Persistence/ClearAllChangesTests.cs` | Replace `WorkItem.CreateSeed()`/`InitializeSeedCounter` |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Replace `WorkItem.CreateSeed()` with `WorkItemBuilder.AsSeed()` |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Replace local `CreateSeed` helpers |
| `tests/Twig.Cli.Tests/Formatters/MinimalOutputFormatterTests.cs` | Replace local `CreateSeed` helpers |
| `tests/Twig.Cli.Tests/Commands/SeedDiscardCommandTests.cs` | Replace local `CreateSeed` helpers |
| `tests/Twig.Cli.Tests/Commands/SeedEditCommandTests.cs` | Replace local `CreateSeed` helpers |
| `tests/Twig.Cli.Tests/Commands/SeedViewCommandTests.cs` | Replace local `CreateSeed` helpers |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Replace local `CreateSeedItem` helpers |

---

## ADO Work Item Structure

### Issue 1: Property Preservation Test Harness

**Goal:** Establish a regression safety net that validates every `WorkItem` property is preserved (or intentionally overridden) across all three copy paths, *before* any structural changes to the copy methods.

**Prerequisites:** None

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1.1 | Create `WorkItemCopierTests.cs` with `WorkItem_Copy_Preserves_All_Properties` theory test. Use `MemberData` to enumerate all three copy methods. Build a fully-populated source `WorkItem` and assert every property on the copy. | `tests/Twig.Domain.Tests/Aggregates/WorkItemCopierTests.cs` | Small |
| T1.2 | Add specific tests for edge cases: copy of clean item stays clean, copy of dirty item with `preserveDirty=false` stays clean, copy with `fieldsOverride` replaces fields correctly, copy with `Revision=0` does not call `MarkSynced`. | `tests/Twig.Domain.Tests/Aggregates/WorkItemCopierTests.cs` | Small |

**Acceptance Criteria:**
- [ ] Theory test covers `WithSeedFields`, `WithParentId`, and `WithIsSeed` copy paths
- [ ] Every public property of `WorkItem` is explicitly asserted in the test
- [ ] Tests pass against the current (pre-refactor) implementation
- [ ] Adding a new `init` property to `WorkItem` without updating the test causes a compile or assertion failure

### Issue 2: WorkItemCopier — Centralized Copy Logic

**Goal:** Eliminate copy-method divergence by introducing `WorkItemCopier` and refactoring all `With*` methods to delegate to it.

**Prerequisites:** Issue 1

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T2.1 | Create `WorkItemCopier.cs` with the `Copy` method implementing centralized property transfer. | `src/Twig.Domain/Aggregates/WorkItemCopier.cs` | Small |
| T2.2 | Refactor `WithSeedFields`, `WithParentId`, and `WithIsSeed` on `WorkItem` to delegate to `WorkItemCopier.Copy()` with appropriate parameters. Remove inline property copying. | `src/Twig.Domain/Aggregates/WorkItem.cs` | Small |
| T2.3 | Run all existing `WorkItemCopyTests` and `WorkItemTests` (WithSeedFields section) to confirm behavioral equivalence. Fix any regressions. | `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs`, `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Small |

**Acceptance Criteria:**
- [ ] `WorkItemCopier.Copy()` is the single path for all property copying
- [ ] All three `With*` methods are one-liners delegating to `WorkItemCopier`
- [ ] All existing tests pass without modification (behavioral equivalence)
- [ ] No new warnings under `TreatWarningsAsErrors`

### Issue 3: SeedFactory — Extract CreateSeed and Counter

**Goal:** Remove static mutable state from `WorkItem` by extracting seed ID generation to `ISeedIdCounter` and consolidating seed creation in `SeedFactory` as a DI service.

**Prerequisites:** None (independent of Issues 1-2)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T3.1 | Create `ISeedIdCounter` interface and `SeedIdCounter` implementation. Port `_seedIdCounter`, `InitializeSeedCounter`, and `Interlocked.Decrement` logic. | `src/Twig.Domain/Interfaces/ISeedIdCounter.cs`, `src/Twig.Domain/Services/SeedIdCounter.cs` | Small |
| T3.2 | Create `SeedIdCounterTests.cs` — tests for initialization, clamping to zero, unique negative IDs, and thread-safety. Port relevant tests from `WorkItemTests.cs`. | `tests/Twig.Domain.Tests/Services/SeedIdCounterTests.cs` | Small |
| T3.3 | Convert `SeedFactory` from `static class` to `sealed class` with `ISeedIdCounter` constructor injection. Move `WorkItem.CreateSeed` body into private `CreateSeedInternal` method. | `src/Twig.Domain/Services/SeedFactory.cs` | Small |
| T3.4 | Register `ISeedIdCounter` → `SeedIdCounter` (singleton) and `SeedFactory` (singleton) in `TwigServiceRegistration.cs`. | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Small |
| T3.5 | Update command call sites: inject `SeedFactory` and `ISeedIdCounter` into `SeedNewCommand`, `SeedChainCommand`, `NewCommand`. Replace `WorkItem.InitializeSeedCounter()` with `seedIdCounter.Initialize()`. Replace `SeedFactory.Create()` / `SeedFactory.CreateUnparented()` static calls with instance calls. | `src/Twig/Commands/SeedNewCommand.cs`, `src/Twig/Commands/SeedChainCommand.cs`, `src/Twig/Commands/NewCommand.cs`, `src/Twig.Mcp/Tools/CreationTools.cs` | Medium |
| T3.6 | Remove `WorkItem.CreateSeed`, `WorkItem.InitializeSeedCounter`, and `WorkItem._seedIdCounter`. | `src/Twig.Domain/Aggregates/WorkItem.cs` | Small |
| T3.7 | Update all test files: replace `WorkItem.CreateSeed()` with `WorkItemBuilder.AsSeed().Build()`, replace `WorkItem.InitializeSeedCounter()` with test-scoped `SeedIdCounter` instances. Update `SeedFactoryTests` for instance-based SeedFactory. | ~15 test files (see Files Affected) | Medium |

**Acceptance Criteria:**
- [ ] `WorkItem` has zero static mutable state
- [ ] `SeedFactory` is registered as a singleton in DI
- [ ] `ISeedIdCounter` is injectable and testable in isolation
- [ ] All existing tests pass with updated call sites
- [ ] `SeedNewCommand` and `SeedChainCommand` no longer reference `WorkItem.InitializeSeedCounter`
- [ ] Parallel test execution is deterministic (no shared counter)

---

## PR Groups

### PG-1: Test Harness + WorkItemCopier (Issues 1 + 2)

**Type:** Deep
**Estimated LoC:** ~350
**Estimated Files:** ~5
**Successor:** None

| Issue | Tasks |
|-------|-------|
| Issue 1 | T1.1, T1.2 |
| Issue 2 | T2.1, T2.2, T2.3 |

**Rationale:** The test harness validates current behavior; the copier refactor must be validated by those same tests. Shipping them together ensures the regression suite is in place before and after the structural change. Few files, complex logic — classic deep PR.

**Key Files:**
- `src/Twig.Domain/Aggregates/WorkItemCopier.cs` (new)
- `src/Twig.Domain/Aggregates/WorkItem.cs` (With* methods refactored)
- `tests/Twig.Domain.Tests/Aggregates/WorkItemCopierTests.cs` (new)

### PG-2: SeedFactory Extraction (Issue 3)

**Type:** Wide
**Estimated LoC:** ~500
**Estimated Files:** ~22
**Successor:** None (independent of PG-1)

| Issue | Tasks |
|-------|-------|
| Issue 3 | T3.1, T3.2, T3.3, T3.4, T3.5, T3.6, T3.7 |

**Rationale:** Touches many files but changes are mechanical — replacing static calls with instance calls and `WorkItem.CreateSeed()` with `WorkItemBuilder.AsSeed()`. The static-to-instance migration is the most impactful change. Wide PR with predictable, low-risk edits per file.

**Key Files:**
- `src/Twig.Domain/Interfaces/ISeedIdCounter.cs` (new)
- `src/Twig.Domain/Services/SeedIdCounter.cs` (new)
- `src/Twig.Domain/Services/SeedFactory.cs` (refactored)
- `src/Twig.Domain/Aggregates/WorkItem.cs` (static members removed)
- `src/Twig.Infrastructure/TwigServiceRegistration.cs` (registrations added)
- `src/Twig/Commands/SeedNewCommand.cs`, `SeedChainCommand.cs`, `NewCommand.cs` (DI injection)

**Execution Order:** PG-1 and PG-2 can proceed in parallel. Neither depends on the other. If merged sequentially, PG-1 first is preferred (smaller PR, establishes test coverage).

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | PG-1-test-harness-and-copier | Issue 1 (T1.1, T1.2), Issue 2 (T2.1, T2.2, T2.3) | None | Deep |
| PG-2 | PG-2-seedfactory-extraction | Issue 3 (T3.1–T3.7) | None (parallel; PG-1 preferred first) | Wide |

### Execution Order

PG-1 and PG-2 are independent and can be implemented in parallel. If merging sequentially, merge PG-1 first: it is smaller (~350 LoC, ~5 files), establishes the property-preservation test harness, and de-risks the refactor before the broader mechanical changes in PG-2 land.

**PG-1 — Deep (few files, complex logic):**
Creates `WorkItemCopier`, refactors all three `With*` methods to one-liners, and establishes the theory-based property-preservation test suite. The coupling between the test harness (Issue 1) and the copier (Issue 2) makes them a natural single PR — the tests validate current behavior before the change and confirm behavioral equivalence after.

**PG-2 — Wide (many files, mechanical):**
Extracts `ISeedIdCounter` / `SeedIdCounter`, converts `SeedFactory` from static to DI-injected, registers both in `TwigServiceRegistration`, updates command and MCP call sites, and migrates ~15 test files from `WorkItem.CreateSeed()` to `WorkItemBuilder.AsSeed()`. Changes are low-risk and repetitive per file.

### Validation Strategy

**PG-1:**
- `dotnet build` — confirms no warnings under `TreatWarningsAsErrors`
- `dotnet test --filter "FullyQualifiedName~WorkItemCopier"` — new theory tests pass
- `dotnet test --filter "FullyQualifiedName~WorkItemCopy"` — existing copy tests pass (behavioral equivalence confirmed)
- `dotnet test --filter "FullyQualifiedName~WorkItem"` — full WorkItem test suite green

**PG-2:**
- `dotnet build` — compiler error if any `WorkItem.CreateSeed` or `InitializeSeedCounter` call site was missed
- `dotnet test --filter "FullyQualifiedName~SeedIdCounter"` — counter tests pass
- `dotnet test --filter "FullyQualifiedName~SeedFactory"` — instance-based factory tests pass
- `dotnet test` (full suite) — all ~15 migrated test files green; no parallel nondeterminism

---

## References

- `docs/architecture/domain-model-critique.md` — Item 1: WorkItem Aggregate God Object
- Epic #2115 — Command Queue Pattern Simplification (parallel work, shared file: `WorkItem.cs`)
- `.github/instructions/pr-grouping.instructions.md` — PR group sizing and classification guidelines
