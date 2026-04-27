# WorkItem Aggregate Consolidation

**Epic:** #2114 — Domain Critique: WorkItem Aggregate Consolidation
**Status:** 📋 Planning
**Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive Summary

The `WorkItem` class in `Twig.Domain.Aggregates` carries too many responsibilities: it simultaneously serves as entity, field bag, seed factory, and copy factory. The three `With*` copy methods (`WithSeedFields`, `WithParentId`, `WithIsSeed`) each manually reconstruct the full object and subtly differ in which properties they preserve — a guaranteed bug factory as properties are added. Additionally, `_seedIdCounter` is static mutable state on a domain entity, coupling all instances and making parallel tests nondeterministic. This plan introduces a `WorkItemCopier` helper to centralize copy logic with a single property-list, extracts seed creation from `WorkItem` into the existing `SeedFactory` service (converted from static to a DI-registered singleton), and adds a reflection-based property-preservation theory test as a permanent safety net.

---

## Background

### Current Architecture

`WorkItem` is a `sealed class` with init-only properties for identity (`Id`, `Type`, `Title`, `State`, `AssignedTo`, `IterationPath`, `AreaPath`, `ParentId`), seed metadata (`IsSeed`, `SeedCreatedAt`), cache staleness (`LastSyncedAt`), and internal state (`IsDirty`, `Revision`, `_fields`, `_pendingNotes`). Mutations flow through `ChangeState()`, `UpdateField()`, and `AddNote()` — direct methods that return `FieldChange` values and set `IsDirty`. The command queue pattern was recently simplified (Epic #2115) to these direct methods.

Three copy methods exist on `WorkItem`:

| Method | Overrides | Copies Fields | Preserves IsDirty | Preserves PendingNotes |
|--------|-----------|---------------|-------------------|----------------------|
| `WithSeedFields(title, fields)` | Title, Fields (replaces) | No — uses provided fields | ❌ No | ❌ No |
| `WithParentId(newParentId)` | ParentId | ✅ Yes (source.Fields) | ✅ Yes | ❌ No |
| `WithIsSeed(isSeed)` | IsSeed | ✅ Yes (source.Fields) | ❌ No (by design) | ❌ No |

Each method manually lists all 11 init-only properties in its `new WorkItem { ... }` initializer. When a new property is added, each method must be independently updated — but the compiler provides no warning if one is missed (init properties default to `default`).

Seed creation lives as `WorkItem.CreateSeed()` (static factory) with `_seedIdCounter` (static `int` with `Interlocked` access) and `InitializeSeedCounter()` (static). The existing `SeedFactory` service (static class in `Twig.Domain.Services`) already wraps `CreateSeed` with parent/child type validation, but delegates the actual construction to `WorkItem.CreateSeed()`.

### Call-Site Audit

#### `WithSeedFields` — 3 production + 5 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/NewCommand.cs:114` | `ExecuteAsync` | Apply editor-parsed fields to seed | Delegation — no API change |
| `src/Twig/Commands/SeedEditCommand.cs:74` | `ExecuteAsync` | Apply editor-parsed fields to seed | Delegation — no API change |
| `src/Twig/Commands/SeedNewCommand.cs:99` | `ExecuteAsync` | Apply editor-parsed fields to seed | Delegation — no API change |
| `tests/.../WorkItemTests.cs` | 5 test methods | Copy behavior validation | Unchanged — tests exercise public API |

#### `WithParentId` — 0 production + 10 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `tests/.../WorkItemCopyTests.cs` | 6 test methods | Copy behavior validation | Unchanged |
| `tests/.../SetCommandTests.cs` | 4 test setups | Creating child items for testing | Unchanged |

#### `WithIsSeed` — 2 production + 5 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs:130` | `PublishSeedAsync` | Mark fetched-back item as seed | Delegation — no API change |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs:174` | Post-publish refresh | Mark refreshed item as seed | Delegation — no API change |
| `tests/.../WorkItemCopyTests.cs` | 5 test methods | Copy behavior validation | Unchanged |

#### `WorkItem.CreateSeed` — 2 production + ~40 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig.Domain/Services/SeedFactory.cs:67` | `Create` | Construct seed from parent context | Method moves into SeedFactory |
| `src/Twig.Domain/Services/SeedFactory.cs:93` | `CreateUnparented` | Construct seed with explicit paths | Method moves into SeedFactory |
| Tests (~40 sites across 10 files) | Various | Create seeds for test setup | Migrate to `TestSeedFactory` helper |

#### `WorkItem.InitializeSeedCounter` — 2 production + ~10 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/SeedChainCommand.cs:69` | `ExecuteAsync` | Init counter from DB before batch | Call `seedFactory.InitializeSeedCounter()` |
| `src/Twig/Commands/SeedNewCommand.cs:67` | `ExecuteAsync` | Init counter from DB before create | Call `seedFactory.InitializeSeedCounter()` |
| Tests (~10 sites across 5 files) | Various | Init counter for test isolation | Migrate to `TestSeedFactory` helper |

---

## Problem Statement

1. **Copy method divergence**: Three `With*` methods each manually enumerate all 11 init-only properties in an object initializer. They subtly differ in state preservation: `WithSeedFields` doesn't preserve `IsDirty`; `WithParentId` does; `WithIsSeed` doesn't. These differences are intentional but undiscoverable — the only way to verify correctness is to read all three methods line-by-line. When a new property is added to `WorkItem`, the compiler provides no warning if one copy method omits it, because init properties silently default.

2. **Static mutable state in domain entity**: `_seedIdCounter` is a `static int` with `Interlocked` access inside `WorkItem`. This couples all `WorkItem` instances process-wide, makes parallel tests nondeterministic (tests that create seeds share the counter), and violates the principle that domain entities should not carry infrastructure concerns like ID generation.

3. **Misplaced responsibility**: `CreateSeed()` and `InitializeSeedCounter()` are static factory methods on `WorkItem` that deal with seed lifecycle — a concern that already has a dedicated `SeedFactory` service. Having the factory logic split between two classes creates confusion about where seed creation logic lives.

---

## Goals and Non-Goals

### Goals

1. **Single property list**: All `With*` copy logic goes through one central method that enumerates `WorkItem` properties exactly once.
2. **Compile-time or test-time safety**: A reflection-based theory test catches any `WorkItem` property not handled by the copier.
3. **No static mutable state in domain entity**: `_seedIdCounter` moves to `SeedFactory`, which becomes a DI-registered singleton.
4. **Preserved semantics**: All existing behavior — which properties each `With*` method preserves or overrides — remains identical. Existing tests pass without modification.

### Non-Goals

- **Field storage refactoring**: No changes to `_fields`, `ImportFields`, `SetField`, or `TryGetField`.
- **Identity pattern changes**: `Id` remains `int`, init-only. No ID generation strategy changes.
- **Domain invariant enforcement**: Adding state validation to `ChangeState()` is out of scope (future epic).
- **`WorkItemBuilder` (TestKit) consolidation**: The test builder is separate from the copier and unchanged.
- **PendingNotes copying**: None of the three methods currently copy pending notes. This is intentional and won't change.

---

## Requirements

### Functional

1. `WorkItemCopier` must produce identical output to the current `With*` methods for all property combinations.
2. `SeedFactory.CreateSeed()` must produce identical output to the current `WorkItem.CreateSeed()`.
3. `SeedFactory.InitializeSeedCounter()` must maintain the same thread-safety guarantees via `Interlocked`.
4. All existing tests must pass without behavioral changes.

### Non-Functional

1. AOT-compatible — no reflection at runtime (test-only reflection is fine).
2. No new external dependencies.
3. No changes to public API surface beyond deprecating `WorkItem.CreateSeed` and `WorkItem.InitializeSeedCounter`.

---

## Proposed Design

### Architecture Overview

```
WorkItem (aggregate)
  ├── With* methods (public API, unchanged signatures)
  │   └── delegates to WorkItemCopier (internal static)
  │       └── CopyCore() — single property-list construction
  │
  └── CreateSeed / InitializeSeedCounter (REMOVED)
        └── moved to SeedFactory (singleton, DI-registered)

SeedFactory (sealed class, singleton)
  ├── _seedIdCounter (instance field, not static)
  ├── InitializeSeedCounter(int minExistingId)
  ├── CreateSeed(type, title, parentId?, areaPath, iterationPath, assignedTo?)
  ├── Create(title, parentContext, processConfig, typeOverride?, assignedTo?)
  └── CreateUnparented(title, type, areaPath, iterationPath, assignedTo?, parentId?)

TestSeedFactory (TestKit, static convenience wrapper)
  └── delegates to a shared SeedFactory instance for test ergonomics
```

### Key Components

#### 1. WorkItemCopier (new — `src/Twig.Domain/Aggregates/WorkItemCopier.cs`)

An `internal static class` in `Twig.Domain.Aggregates` that centralizes all `WorkItem` copy construction. The single `CopyCore` method takes every property as an explicit parameter — no defaults, no optionals — so that adding a new property to `WorkItem` forces a compile error in `CopyCore` callers until they supply the value.

```csharp
internal static class WorkItemCopier
{
    /// <summary>
    /// Single construction point for all WorkItem copies.
    /// Every init-only property is an explicit parameter.
    /// </summary>
    internal static WorkItem CopyCore(
        int id,
        WorkItemType type,
        string title,
        string state,
        string? assignedTo,
        IterationPath iterationPath,
        AreaPath areaPath,
        int? parentId,
        bool isSeed,
        DateTimeOffset? seedCreatedAt,
        DateTimeOffset? lastSyncedAt,
        int revision,
        IEnumerable<KeyValuePair<string, string?>> fields,
        bool isDirty)
    {
        var copy = new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = state,
            AssignedTo = assignedTo,
            IterationPath = iterationPath,
            AreaPath = areaPath,
            ParentId = parentId,
            IsSeed = isSeed,
            SeedCreatedAt = seedCreatedAt,
            LastSyncedAt = lastSyncedAt,
        };

        if (revision > 0)
            copy.MarkSynced(revision);

        copy.ImportFields(fields);

        if (isDirty)
            copy.SetDirty();

        return copy;
    }
}
```

The three `With*` methods on `WorkItem` become thin wrappers:

```csharp
public WorkItem WithSeedFields(string title, IReadOnlyDictionary<string, string?> fields) =>
    WorkItemCopier.CopyCore(
        Id, Type, title, State, AssignedTo, IterationPath, AreaPath,
        ParentId, IsSeed, SeedCreatedAt, LastSyncedAt, Revision,
        fields, isDirty: false);

public WorkItem WithParentId(int? newParentId) =>
    WorkItemCopier.CopyCore(
        Id, Type, Title, State, AssignedTo, IterationPath, AreaPath,
        newParentId, IsSeed, SeedCreatedAt, LastSyncedAt, Revision,
        Fields, isDirty: IsDirty);

public WorkItem WithIsSeed(bool isSeed) =>
    WorkItemCopier.CopyCore(
        Id, Type, Title, State, AssignedTo, IterationPath, AreaPath,
        ParentId, isSeed, SeedCreatedAt, LastSyncedAt, Revision,
        Fields, isDirty: false);
```

**Why explicit parameters over an options object**: An options object with defaults would silently use `default` for any new property — exactly the bug we're fixing. Explicit parameters cause compile errors when a new property is added to `CopyCore` but not forwarded by callers.

#### 2. SeedFactory refactoring (modified — `src/Twig.Domain/Services/SeedFactory.cs`)

Convert from `static class` to `sealed class`. Move `_seedIdCounter`, `CreateSeed()`, and `InitializeSeedCounter()` from `WorkItem` into `SeedFactory` as instance members. The existing `Create()` and `CreateUnparented()` methods remain but call the now-local `CreateSeed()` instead of `WorkItem.CreateSeed()`.

```csharp
public sealed class SeedFactory
{
    private int _seedIdCounter;

    public void InitializeSeedCounter(int minExistingId)
    {
        Interlocked.Exchange(ref _seedIdCounter, Math.Min(minExistingId, 0));
    }

    public WorkItem CreateSeed(
        WorkItemType type, string title, int? parentId = null,
        AreaPath areaPath = default, IterationPath iterationPath = default,
        string? assignedTo = null)
    {
        var seed = new WorkItem
        {
            Id = Interlocked.Decrement(ref _seedIdCounter),
            Type = type, Title = title, IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = parentId, AreaPath = areaPath,
            IterationPath = iterationPath, AssignedTo = assignedTo,
        };

        if (!string.IsNullOrWhiteSpace(assignedTo))
            seed.SetField("System.AssignedTo", assignedTo);

        return seed;
    }

    // Create() and CreateUnparented() remain — now call this.CreateSeed()
}
```

Register as singleton in `TwigServiceRegistration.AddTwigCoreServices()`:
```csharp
services.AddSingleton<SeedFactory>();
```

And in MCP `Program.cs`:
```csharp
builder.Services.AddSingleton<SeedFactory>();
```

#### 3. TestSeedFactory (new — `tests/Twig.TestKit/TestSeedFactory.cs`)

A static convenience wrapper so tests don't need to instantiate `SeedFactory` manually:

```csharp
public static class TestSeedFactory
{
    private static readonly SeedFactory Instance = new();

    public static void InitializeSeedCounter(int minExistingId) =>
        Instance.InitializeSeedCounter(minExistingId);

    public static WorkItem CreateSeed(
        WorkItemType type, string title, int? parentId = null,
        AreaPath areaPath = default, IterationPath iterationPath = default,
        string? assignedTo = null) =>
        Instance.CreateSeed(type, title, parentId, areaPath, iterationPath, assignedTo);
}
```

#### 4. Property-Preservation Theory Test (new — `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyPreservationTests.cs`)

A reflection-based test that enumerates all public/internal properties of `WorkItem` and verifies each copy method preserves or correctly overrides them. This test fails if a new property is added to `WorkItem` but not accounted for in the copy logic or in the test's "known overrides" list.

```csharp
public class WorkItemCopyPreservationTests
{
    private static readonly PropertyInfo[] AllProperties =
        typeof(WorkItem).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Theory]
    [InlineData(nameof(WithSeedFields))]
    [InlineData(nameof(WithParentId))]
    [InlineData(nameof(WithIsSeed))]
    public void Copy_PreservesOrOverrides_AllProperties(string methodName)
    {
        // Creates a WorkItem with non-default values for every property,
        // calls the copy method, and asserts that every property either
        // matches the source or is in the "known overrides" set for that method.
    }
}
```

### Design Decisions

1. **Internal static class, not instance service**: `WorkItemCopier` is `internal static` because it needs access to `WorkItem`'s internal members (`ImportFields`, `SetDirty`, `MarkSynced`) and has no state. It's a pure helper, not a service.

2. **SeedFactory as singleton, not scoped**: The seed ID counter is inherently per-process state (like the current static field). A singleton matches this lifetime. Scoped registration would reset the counter per request, causing ID collisions.

3. **No ISeedFactory interface**: `SeedFactory` has no external dependencies and is trivially constructible. Tests create instances directly. An interface would add ceremony without testability benefit.

4. **TestSeedFactory static wrapper**: Tests that create seeds for setup (not testing seed creation itself) need a one-liner, not `new SeedFactory()` boilerplate. The static wrapper provides this while keeping the real `SeedFactory` non-static.

5. **PendingNotes deliberately excluded from copy**: All three `With*` methods currently don't copy pending notes. This is correct: `WithSeedFields` replaces content, `WithIsSeed` is used on fetched-back items (no pending notes), and `WithParentId` operates on metadata. The preservation test explicitly documents this as a known exclusion.

---

## Dependencies

### Internal

- **Command Queue Simplification (Epic #2115)**: Must be complete before this work begins. The current `WorkItem.cs` already reflects the simplified direct-mutation pattern (no more `IWorkItemCommand` queue). ✅ Already completed.
- **No blocking internal dependencies**: This epic touches only `WorkItem`, `SeedFactory`, and their direct callers. No other in-flight epics conflict.

### External

- None. No new NuGet packages or external services required.

### Sequencing Constraints

- Issue 1 (WorkItemCopier) must complete before Issue 2 (SeedFactory Extraction) because the property-preservation test from Issue 1 serves as a safety net for the structural changes in Issue 2.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| New `WorkItem` property added during implementation, missed by one copier call | Low | High | Property-preservation theory test (Issue 1, Task 1) catches this at test time |
| `SeedFactory` DI singleton counter reset between MCP tool calls | Low | Medium | Singleton lifetime matches current static lifetime — no behavioral change |
| Test churn from `WorkItem.CreateSeed` → `TestSeedFactory.CreateSeed` migration | Medium | Low | Mechanical find-replace; no behavioral change in tests |
| MCP `CreationTools` doesn't call `InitializeSeedCounter` (pre-existing) | Low | Low | Pre-existing behavior — MCP seeds are published immediately, ephemeral negative IDs never persisted locally |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `WithParentId` copy pending notes? Currently it doesn't, but it preserves dirty state. If a user changes parent on a seed with pending notes, the notes are lost. | Low | No production path currently creates pending notes on seeds. Document as known limitation. |
| 2 | Should `SeedFactory` expose an `ISeedFactory` interface for future mockability? | Low | Current design uses concrete registration. Can add an interface later if needed — backward-compatible change. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItemCopier.cs` | Internal static helper centralizing all With* copy construction |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyPreservationTests.cs` | Reflection-based theory test validating property preservation across all copy paths |
| `tests/Twig.TestKit/TestSeedFactory.cs` | Static convenience wrapper for SeedFactory in tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs` | (PG-1) Refactor With* methods to delegate to WorkItemCopier. (PG-2) Remove `_seedIdCounter`, `CreateSeed`, `InitializeSeedCounter`. |
| `src/Twig.Domain/Services/SeedFactory.cs` | Convert from static to sealed class. Add `_seedIdCounter`, `CreateSeed`, `InitializeSeedCounter` as instance members. |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Add `services.AddSingleton<SeedFactory>()` registration |
| `src/Twig/Commands/SeedNewCommand.cs` | Inject `SeedFactory`, call `seedFactory.InitializeSeedCounter()` instead of `WorkItem.InitializeSeedCounter()` |
| `src/Twig/Commands/SeedChainCommand.cs` | Inject `SeedFactory`, call `seedFactory.InitializeSeedCounter()` instead of `WorkItem.InitializeSeedCounter()` |
| `src/Twig/Commands/NewCommand.cs` | Inject `SeedFactory`, call `seedFactory.CreateUnparented()` instead of `SeedFactory.CreateUnparented()` (static) |
| `src/Twig.Mcp/Program.cs` | Add `builder.Services.AddSingleton<SeedFactory>()` registration |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Inject `SeedFactory` via constructor, call instance methods |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Replace `WorkItem.CreateSeed` → `TestSeedFactory.CreateSeed`, `WorkItem.InitializeSeedCounter` → `TestSeedFactory.InitializeSeedCounter` |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs` | Replace `WorkItem.CreateSeed` → `TestSeedFactory.CreateSeed` |
| `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` | Instantiate `SeedFactory` as instance, test instance methods |
| `tests/Twig.Domain.Tests/ReadModels/WorkspaceTests.cs` | Replace `WorkItem.CreateSeed` → `TestSeedFactory.CreateSeed` |
| `tests/Twig.Infrastructure.Tests/Persistence/PhantomDirtyCleansingTests.cs` | Replace static calls → `TestSeedFactory` |
| `tests/Twig.Infrastructure.Tests/Persistence/ClearDirtyFlagTests.cs` | Replace static calls → `TestSeedFactory` |
| `tests/Twig.Infrastructure.Tests/Persistence/ClearAllChangesTests.cs` | Replace static calls → `TestSeedFactory` |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | Replace static calls → `TestSeedFactory` |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Replace static calls → `TestSeedFactory` |
| `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientIntegrationTests.cs` | Replace static calls → `TestSeedFactory` |
| `docs/architecture/domain-model-critique.md` | Mark Item 1 as remediated, update containment practices |

---

## ADO Work Item Structure

### Issue 1: WorkItemCopier — Centralize Copy Logic

**Goal:** Introduce a `WorkItemCopier` helper that centralizes all `With*` copy construction into a single property-list, eliminating divergence risk. Add a property-preservation theory test as a permanent safety net.

**Prerequisites:** None (first issue in sequence).

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2114.1 | **Property-preservation theory test (pre-change):** Create `WorkItemCopyPreservationTests.cs` with a `[Theory]` that uses reflection to enumerate all `WorkItem` properties and validates each `With*` method preserves or overrides them. Run this test against the CURRENT code to establish a green baseline before any refactoring. | `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyPreservationTests.cs` | S |
| T-2114.2 | **Create WorkItemCopier:** Implement `WorkItemCopier` as an `internal static class` in `Twig.Domain.Aggregates` with a `CopyCore` method taking every init-only property as an explicit parameter. Unit test `CopyCore` directly for all three copy variants. | `src/Twig.Domain/Aggregates/WorkItemCopier.cs` | S |
| T-2114.3 | **Wire With* methods through copier:** Refactor `WithSeedFields`, `WithParentId`, and `WithIsSeed` on `WorkItem` to delegate to `WorkItemCopier.CopyCore`. Remove the inline `new WorkItem { ... }` initializers from all three methods. Verify all existing `WorkItemCopyTests` and `WorkItemTests` pass unchanged. | `src/Twig.Domain/Aggregates/WorkItem.cs` | S |

**Acceptance Criteria:**
- [ ] `WorkItemCopyPreservationTests` passes for all three copy methods
- [ ] All 3 `With*` methods delegate to `WorkItemCopier.CopyCore`
- [ ] No inline `new WorkItem { ... }` initializers remain in `With*` methods
- [ ] All existing tests in `WorkItemTests` and `WorkItemCopyTests` pass without modification
- [ ] Adding a new init-only property to `WorkItem` causes a compile error in `WorkItemCopier.CopyCore` callers

### Issue 2: SeedFactory Extraction — Eliminate Static Mutable State

**Goal:** Move seed creation and ID counter management from `WorkItem` (domain entity) to `SeedFactory` (domain service), converting `SeedFactory` from a static class to a DI-registered singleton. Eliminate `_seedIdCounter` as static mutable state on the domain entity.

**Prerequisites:** Issue 1 (WorkItemCopier) — the property-preservation test must be green before structural changes.

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2114.4 | **Convert SeedFactory to non-static singleton:** Remove `static` modifier from `SeedFactory`. Add `_seedIdCounter` instance field. Move `CreateSeed()` and `InitializeSeedCounter()` from `WorkItem` to `SeedFactory` as instance methods. Remove the moved members from `WorkItem`. Register `SeedFactory` as singleton in `TwigServiceRegistration` and MCP `Program.cs`. | `src/Twig.Domain/Services/SeedFactory.cs`, `src/Twig.Domain/Aggregates/WorkItem.cs`, `src/Twig.Infrastructure/TwigServiceRegistration.cs`, `src/Twig.Mcp/Program.cs` | M |
| T-2114.5 | **Update production callers:** Inject `SeedFactory` into `SeedNewCommand`, `SeedChainCommand`, `NewCommand`, and MCP `CreationTools`. Replace `WorkItem.InitializeSeedCounter()` calls with `seedFactory.InitializeSeedCounter()`. Replace `SeedFactory.Create/CreateUnparented` static calls with instance calls. | `src/Twig/Commands/SeedNewCommand.cs`, `src/Twig/Commands/SeedChainCommand.cs`, `src/Twig/Commands/NewCommand.cs`, `src/Twig.Mcp/Tools/CreationTools.cs` | M |
| T-2114.6 | **Create TestSeedFactory and update test callers:** Create `TestSeedFactory` static wrapper in `Twig.TestKit`. Migrate all `WorkItem.CreateSeed()` and `WorkItem.InitializeSeedCounter()` calls in test files to `TestSeedFactory`. Update `SeedFactoryTests` to instantiate `SeedFactory` as an object and test instance methods. Verify all tests pass. | `tests/Twig.TestKit/TestSeedFactory.cs`, ~10 test files | M |

**Acceptance Criteria:**
- [ ] `WorkItem` no longer contains `_seedIdCounter`, `CreateSeed`, or `InitializeSeedCounter`
- [ ] `SeedFactory` is a non-static sealed class registered as singleton
- [ ] All production callers inject and use `SeedFactory` instance
- [ ] All test callers use `TestSeedFactory` convenience wrapper
- [ ] Parallel seed creation tests are deterministic (isolated counter per `SeedFactory` instance)
- [ ] All existing tests pass

### Issue 3: Verification & Documentation

**Goal:** Full test suite verification and documentation update to reflect completed remediation.

**Prerequisites:** Issues 1 and 2.

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-2114.7 | **Full test suite verification:** Run all tests across `Twig.Domain.Tests`, `Twig.Infrastructure.Tests`, `Twig.Cli.Tests`, and `Twig.Mcp.Tests`. Confirm zero regressions. Build with `PublishAot=true` to verify AOT compatibility. | All test projects | S |
| T-2114.8 | **Update domain-model-critique.md:** Mark Item 1 (WorkItem Aggregate — God Object) as partially remediated. Update the "Containment Practices" section to reflect completed work (WorkItemCopier, SeedFactory extraction). Note remaining items (field encapsulation) as future work. | `docs/architecture/domain-model-critique.md` | S |

**Acceptance Criteria:**
- [ ] All tests pass across all test projects
- [ ] AOT build succeeds
- [ ] `domain-model-critique.md` reflects current state

---

## PR Groups

### PG-1: WorkItemCopier & Property Preservation Tests

**Scope:** Issue 1 (T-2114.1, T-2114.2, T-2114.3)
**Classification:** Deep — few files, complex copy logic centralization
**Estimated LoC:** ~250
**Files:** ~4 (WorkItemCopier.cs new, WorkItem.cs modified, WorkItemCopyPreservationTests.cs new)
**Successors:** PG-2

**Rationale:** This PR is self-contained and creates the safety net (preservation test) that de-risks PG-2. The property-preservation test must be green before any structural changes proceed.

### PG-2: SeedFactory Extraction & Documentation

**Scope:** Issue 2 + Issue 3 (T-2114.4 through T-2114.8)
**Classification:** Wide — many files, mechanical migration of static calls to instance calls
**Estimated LoC:** ~400
**Files:** ~20 (SeedFactory.cs modified, WorkItem.cs modified, 4 commands modified, MCP modified, TestSeedFactory.cs new, ~10 test files modified, docs updated)
**Successors:** None

**Rationale:** The SeedFactory conversion, caller migration, and test updates are tightly coupled — you can't partially convert from static to instance. Bundling with documentation keeps the PR coherent. The wide file count is acceptable because most changes are mechanical (replace static call with instance call).

---

## References

- `docs/architecture/domain-model-critique.md` — Item 1 (WorkItem Aggregate — God Object)
- Epic #2115 — Command Queue Pattern Simplification (prerequisite, completed)
- `src/Twig.Domain/Aggregates/WorkItem.cs` — current implementation
- `src/Twig.Domain/Services/SeedFactory.cs` — existing static factory

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | WorkItemCopier & Property Preservation Tests | Issue 1 (T-2114.1, T-2114.2, T-2114.3) | None | Deep |
| PG-2 | SeedFactory Extraction & Documentation | Issue 2 + Issue 3 (T-2114.4 – T-2114.8) | PG-1 | Wide |

### Execution Order

**PG-1 → PG-2** (sequential, one depends on the other).

PG-1 is implemented first: it introduces `WorkItemCopier`, wires the three `With*` methods through a single `CopyCore`, and adds the reflection-based property-preservation theory test. This PR is small (~250 LoC, ~4 files), deep in complexity, and self-contained — no changes outside `WorkItem.cs` and its test files. It establishes the green safety net that de-risks all structural changes in PG-2.

PG-2 follows: it converts `SeedFactory` from a static class to a DI-registered singleton, moves `_seedIdCounter`/`CreateSeed`/`InitializeSeedCounter` from `WorkItem`, injects the instance into production command callers and MCP tools, introduces `TestSeedFactory` in TestKit, migrates ~10 test files from static `WorkItem.*` calls to `TestSeedFactory.*`, and updates `domain-model-critique.md`. The wide scope (~400 LoC, ~20 files) is acceptable because the majority of changes are mechanical find-replace. All changes in PG-2 are tightly coupled — a partial static→instance conversion would leave a broken build — so they ship together.

### Validation Strategy

**PG-1 validation:**
1. `WorkItemCopyPreservationTests` passes for all three copy methods (`WithSeedFields`, `WithParentId`, `WithIsSeed`) against current code (pre-change baseline).
2. Implement `WorkItemCopier.CopyCore` and wire `With*` methods.
3. `WorkItemCopyPreservationTests`, `WorkItemCopyTests`, and `WorkItemTests` all pass without modification.
4. Deliberately add a dummy init-only property to `WorkItem` in a scratch branch and confirm a compile error is raised in `WorkItemCopier.CopyCore` — then revert.
5. `dotnet build` with `TreatWarningsAsErrors=true` passes.

**PG-2 validation:**
1. `WorkItemCopyPreservationTests` continues to pass (regression guard from PG-1).
2. `SeedFactoryTests` instantiate `SeedFactory` as a non-static object and all assertions pass.
3. `TestSeedFactory` wrapper compiles and all migrated test files pass.
4. Full test suite (`Twig.Domain.Tests`, `Twig.Infrastructure.Tests`, `Twig.Cli.Tests`, `Twig.Mcp.Tests`) — zero regressions.
5. AOT build: `dotnet publish -r win-x64 -c Release` succeeds (no reflection at runtime path).
