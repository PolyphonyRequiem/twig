# Domain-Infrastructure Boundary: DTO Layer

> **Epic:** #2122 — Domain Critique: Domain-Infrastructure Boundary (DTO Layer)
> **Status**: 🔨 In Progress
> **Revision Notes:** Initial draft.

---

## Executive Summary

This plan introduces a Data Transfer Object (DTO) layer at the boundary between Twig's
domain and infrastructure layers. Today, `IAdoWorkItemService` (a domain interface) returns
fully constructed `WorkItem` aggregates, and the infrastructure layer (`AdoRestClient`,
`SqliteWorkItemRepository`) directly instantiates domain objects during deserialization.
This means `WorkItem` simultaneously serves as the API response model, persistence model,
and domain model — a triple coupling that causes changes to cascade across layers and into
every test that constructs a `WorkItem`.

The plan introduces a `WorkItemSnapshot` immutable record as the domain-level data carrier,
a `WorkItemMapper` domain service for aggregate construction, and a `CreateWorkItemRequest`
DTO for the write path. These types decouple the infrastructure from domain construction,
making `WorkItem` property changes a domain-only concern. The write path (lowest risk) ships
first, followed by internal refactoring of the ADO and persistence read paths — all behind
stable public interfaces so consumers are unaffected.

## Background

### Current Architecture

The Twig domain layer defines `IAdoWorkItemService` in `Twig.Domain.Interfaces` with methods
that return domain `WorkItem` aggregates:

```csharp
public interface IAdoWorkItemService
{
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default);
    Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default);
    Task<int> CreateAsync(WorkItem seed, CancellationToken ct = default);
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default);
    // ... other methods taking primitives
}
```

The infrastructure implementation (`AdoRestClient`) deserializes ADO REST responses into
`AdoWorkItemResponse` DTOs, then immediately converts them to domain `WorkItem` objects
via `AdoResponseMapper.MapWorkItem()`. Similarly, `SqliteWorkItemRepository.MapRow()`
constructs `WorkItem` directly from SQLite rows. Both infrastructure classes use
`WorkItem`'s `internal` mutators (`SetField`, `ImportFields`, `MarkSynced`, `SetDirty`)
to hydrate state.

### Prior Art

- **`ExportedWorkItem`** (`Twig.Domain.ValueObjects`) — an existing DTO-like record
  (`int Id, int Revision, string TypeName, IReadOnlyDictionary<string, string?> Fields`)
  used as a round-trip carrier for work item export/import. Validates the pattern of
  domain-level data records.
- **`AdoWorkItemResponse`** (`Twig.Infrastructure.Ado.Dtos`) — the existing ADO REST
  response DTO. Currently `internal sealed` and only consumed within `AdoRestClient`.
- **`WorkItemCopier`** — centralizes copy construction, ensuring all `init`-only
  properties are explicitly transferred. Provides compiler-safety when new properties
  are added.
- **`WorkItemBuilder`** (`Twig.TestKit`) — test-only fluent builder used by 200+
  test sites. Isolates test construction from `WorkItem` init-property changes.
- **`FieldChange`** — already a clean DTO for the write path (`PatchAsync`).

### Dependency: Epic #2114 (WorkItem Aggregate Consolidation)

The domain-model-critique document (Item 9) recommends deferring the DTO layer until
after Epic #2114. However, `WorkItemCopier` already exists and centralizes copy
construction, and the `WorkItem` init-property surface is stable (11 properties). The
DTO layer work can proceed — the write-path DTO (`CreateWorkItemRequest`) actually
reduces coupling to `WorkItem` rather than increasing it. The read-path refactor is
internal to infrastructure and does not change public APIs.

### Call-Site Audit

#### IAdoWorkItemService Consumers (Source Code — excluding tests)

| File | Method/Context | Methods Used | Impact |
|------|----------------|-------------|--------|
| `SyncCoordinator.cs` | Constructor injection | `FetchAsync`, `FetchChildrenAsync`, `FetchBatchAsync`, `FetchWithLinksAsync` | Low — interface stable |
| `RefreshOrchestrator.cs` | Primary constructor | `QueryByWiqlAsync`, `FetchBatchAsync`, `FetchChildrenAsync`, `FetchAsync` | Low |
| `SyncCoordinatorFactory.cs` | Constructor injection | Passed through to `SyncCoordinator` | Low |
| `ActiveItemResolver.cs` | Constructor injection | `FetchAsync` | Low |
| `ContextChangeService.cs` | Primary constructor | `FetchAsync` (via SyncCoordinator) | Low |
| `BranchLinkService.cs` | Primary constructor | `AddArtifactLinkAsync` | None |
| `ParentStatePropagationService.cs` | Primary constructor | `FetchAsync`, `PatchAsync` | Low |
| `DescendantVerificationService.cs` | Constructor injection | `FetchChildrenAsync` | Low |
| `BacklogOrderer.cs` | Constructor injection | `FetchAsync`, `PatchAsync` | Low |
| `SeedPublishOrchestrator.cs` | Constructor injection | `CreateAsync`, `FetchAsync` | **Moderate** — CreateAsync signature changes |
| `SeedLinkPromoter.cs` | Constructor injection | `AddLinkAsync`, `RemoveLinkAsync` | None |
| `DuplicateGuard.cs` | Static parameter | `QueryByWiqlAsync`, `FetchAsync` | Low |
| `PendingChangeFlusher.cs` | Primary constructor | `FetchAsync`, `PatchAsync`, `AddCommentAsync` | Low |
| `ConflictRetryHelper.cs` | Static parameter | `PatchAsync`, `FetchAsync` | Low |
| `AutoPushNotesHelper.cs` | Static parameter | `AddCommentAsync` | None |
| `NewCommand.cs` | Constructor injection | `CreateAsync` | **Moderate** — CreateAsync signature changes |
| `BatchCommand.cs` | Constructor injection | `CreateAsync`, `FetchAsync` | **Moderate** |
| `StateCommand.cs` | Constructor injection | `PatchAsync`, `FetchAsync` | Low |
| `UpdateCommand.cs` | Constructor injection | `PatchAsync`, `FetchAsync` | Low |
| `EditCommand.cs` | Constructor injection | `FetchAsync`, `PatchAsync` | Low |
| `NoteCommand.cs` | Constructor injection | `AddCommentAsync` | None |
| `LinkCommand.cs` | Constructor injection | `AddLinkAsync`, `RemoveLinkAsync` | None |
| `QueryCommand.cs` | Constructor injection | `QueryByWiqlAsync`, `FetchBatchAsync` | Low |
| `ArtifactLinkCommand.cs` | Constructor injection | `AddArtifactLinkAsync` | None |
| `SeedPublishCommand.cs` | Constructor injection | Passed to `SeedPublishOrchestrator` | Low |
| `McpPendingChangeFlusher.cs` | Constructor injection | `PatchAsync`, `FetchAsync`, `AddCommentAsync` | Low |
| `WorkspaceContext.cs` | Constructor injection | `FetchAsync`, `FetchWithLinksAsync` | Low |
| `CreationTools.cs` (MCP) | Via WorkspaceContext | `CreateAsync`, `FetchAsync` | **Moderate** |

#### WorkItem Direct Construction Sites (infrastructure only)

| File | Method | Properties Set | Impact |
|------|--------|---------------|--------|
| `AdoResponseMapper.MapWorkItem()` | Constructs from ADO DTO | All 7 init props + fields + MarkSynced | **Critical** — primary mapping target |
| `SqliteWorkItemRepository.MapRow()` | Constructs from SQLite row | All 8 init props + MarkSynced + SetField + SetDirty | **Critical** — persistence mapping target |
| `SeedFactory.CreateSeedInternal()` | Constructs new seeds | 8 init props + SetField | **Not affected** — domain-internal construction |

## Problem Statement

Three concrete problems motivate this work:

1. **No DTO/mapping layer.** ADO REST responses are deserialized into infrastructure DTOs
   (`AdoWorkItemResponse`) then immediately mapped into domain `WorkItem` aggregates by
   `AdoResponseMapper`. SQLite rows are mapped directly into `WorkItem` by
   `SqliteWorkItemRepository.MapRow`. Adding a new property to `WorkItem` requires
   updating the ADO mapper, SQLite persistence mapper, `WorkItemCopier`, and every
   test that constructs a `WorkItem` — a four-way cascade.

2. **Domain purity violation.** `IAdoWorkItemService.CreateAsync` accepts a full
   `WorkItem` aggregate when it only needs ~6 properties (type, title, area path,
   iteration path, parent ID, custom fields). Infrastructure constructs domain
   aggregates during deserialization — responsibility that belongs to the domain.
   The anti-corruption boundary (`AdoResponseMapper`) is in the wrong layer.

3. **Test fragility.** While `WorkItemBuilder` mitigates direct constructor coupling
   in tests (~200+ usages), 37 direct `new WorkItem { ... }` sites remain (19 in
   domain tests, 10 in sync tests, 5 in persistence tests, 3 in infrastructure).
   Any init-property change touches all of them.

## Goals and Non-Goals

### Goals

1. **Write-path decoupling**: `CreateAsync` accepts a purpose-built DTO instead of
   a full `WorkItem` aggregate, eliminating the seed→aggregate→DTO round-trip.
2. **Domain-owned construction**: `WorkItem` aggregate construction from external
   data (ADO, SQLite) is performed by a domain service, not by infrastructure code.
3. **Cascade reduction**: Adding a new property to `WorkItem` requires changes only
   in the domain (`WorkItemMapper`) and `WorkItemCopier`, not in `AdoResponseMapper`
   or `SqliteWorkItemRepository`.
4. **Zero consumer changes**: All refactoring is internal to `AdoRestClient` and
   `SqliteWorkItemRepository` — public interfaces (`IAdoWorkItemService`,
   `IWorkItemRepository`) remain unchanged.
5. **Incremental adoption**: Each Issue is independently shippable and testable.

### Non-Goals

- **Changing `IAdoWorkItemService` read-method return types** — returning DTOs instead
  of `WorkItem` from fetch methods is a future consideration, not in scope.
- **Persistence schema changes** — the SQLite `work_items` table schema is unchanged.
- **Test builder replacement** — `WorkItemBuilder` remains the test construction
  standard; no migration to snapshot-based test construction.
- **Full CQRS or Event Sourcing** — this is a structural refactor, not an
  architectural paradigm shift.
- **Refactoring `SeedFactory`** — seed construction stays in the domain; only the
  handoff to `CreateAsync` changes.

## Requirements

### Functional

- **FR-1**: `CreateAsync` must accept a `CreateWorkItemRequest` record containing
  type name, title, area path, iteration path, parent ID, and custom fields.
- **FR-2**: A `WorkItemSnapshot` immutable record must carry all data needed to
  construct a `WorkItem` aggregate (identity, metadata, fields, revision).
- **FR-3**: A `WorkItemMapper` domain service must construct `WorkItem` from
  `WorkItemSnapshot`, handling value object parsing (IterationPath, AreaPath,
  WorkItemType) and state restoration (Revision, IsDirty, Fields).
- **FR-4**: `AdoResponseMapper.MapWorkItem` must produce `WorkItemSnapshot` instead
  of `WorkItem`. `AdoRestClient` must use `WorkItemMapper` to convert snapshots
  to aggregates before returning.
- **FR-5**: `SqliteWorkItemRepository.MapRow` must produce `WorkItemSnapshot` and
  use `WorkItemMapper` for aggregate construction.

### Non-Functional

- **NFR-1**: Zero runtime reflection — all new types must be AOT-compatible.
  `WorkItemSnapshot` and `CreateWorkItemRequest` need not be registered in
  `TwigJsonContext` unless serialized over the wire.
- **NFR-2**: No public API changes — `IAdoWorkItemService` and `IWorkItemRepository`
  return types are unchanged.
- **NFR-3**: All existing tests must pass without modification (except tests
  directly testing changed internals like `AdoResponseMapper`).
- **NFR-4**: No performance regression — snapshot construction must be allocation-
  equivalent to current direct construction.

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                     Domain Layer                         │
│                                                         │
│  ┌──────────────────┐   ┌──────────────────────────┐   │
│  │ IAdoWorkItemService│   │ IWorkItemRepository      │   │
│  │ (returns WorkItem) │   │ (returns WorkItem)       │   │
│  └────────┬─────────┘   └────────┬─────────────────┘   │
│           │                      │                      │
│  ┌────────┴──────────────────────┴─────────────────┐   │
│  │            WorkItemMapper                        │   │
│  │   WorkItemSnapshot → WorkItem aggregate          │   │
│  └────────┬──────────────────────┬─────────────────┘   │
│           │                      │                      │
│  ┌────────┴─────────┐   ┌───────┴──────────────────┐   │
│  │ WorkItemSnapshot  │   │ CreateWorkItemRequest    │   │
│  │ (immutable DTO)   │   │ (write-path DTO)         │   │
│  └──────────────────┘   └──────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
                    │                      │
┌───────────────────┴──────────────────────┴──────────────┐
│                  Infrastructure Layer                    │
│                                                         │
│  ┌──────────────────┐   ┌──────────────────────────┐   │
│  │ AdoRestClient     │   │ SqliteWorkItemRepository  │   │
│  │ ADO DTO → Snapshot│   │ SQL Row → Snapshot        │   │
│  └──────────────────┘   └──────────────────────────┘   │
│                                                         │
│  ┌──────────────────┐                                   │
│  │ AdoResponseMapper │                                   │
│  │ ADO DTO → Snapshot│                                   │
│  └──────────────────┘                                   │
└─────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkItemSnapshot` (Domain — `Twig.Domain.ValueObjects`)

Immutable record carrying raw work item data without domain behavior. Uses
primitive/string types for all fields — no value objects. This is the boundary type
that both ADO and SQLite mappers produce.

```csharp
public sealed record WorkItemSnapshot
{
    public int Id { get; init; }
    public int Revision { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? AssignedTo { get; init; }
    public string? IterationPath { get; init; }
    public string? AreaPath { get; init; }
    public int? ParentId { get; init; }
    public bool IsSeed { get; init; }
    public DateTimeOffset? SeedCreatedAt { get; init; }
    public DateTimeOffset? LastSyncedAt { get; init; }
    public bool IsDirty { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; }
        = new Dictionary<string, string?>();
}
```

Design rationale:
- **Primitive types only** — `TypeName` is `string` (not `WorkItemType`), `IterationPath`
  is `string?` (not the value object). Parsing happens in the mapper.
- **Mirrors `WorkItem` properties** — one-to-one with init-only properties plus `Revision`
  and `IsDirty` for state restoration.
- **No methods** — purely a data carrier.

#### 2. `CreateWorkItemRequest` (Domain — `Twig.Domain.ValueObjects`)

Immutable record for the `CreateAsync` write path. Carries only the data needed
to create a work item in ADO.

```csharp
public sealed record CreateWorkItemRequest
{
    public required string TypeName { get; init; }
    public required string Title { get; init; }
    public string? AreaPath { get; init; }
    public string? IterationPath { get; init; }
    public int? ParentId { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; }
        = new Dictionary<string, string?>();
}
```

A convenience extension on `WorkItem` provides migration support:

```csharp
public static CreateWorkItemRequest ToCreateRequest(this WorkItem seed)
    => new()
    {
        TypeName = seed.Type.Value,
        Title = seed.Title,
        AreaPath = seed.AreaPath.Value,
        IterationPath = seed.IterationPath.Value,
        ParentId = seed.ParentId,
        Fields = new Dictionary<string, string?>(seed.Fields, StringComparer.OrdinalIgnoreCase),
    };
```

#### 3. `WorkItemMapper` (Domain — `Twig.Domain.Services`)

Domain service that constructs `WorkItem` aggregates from `WorkItemSnapshot`.
Owns all value object parsing and state restoration logic.

```csharp
public sealed class WorkItemMapper
{
    public WorkItem Map(WorkItemSnapshot snapshot)
    {
        var item = new WorkItem
        {
            Id = snapshot.Id,
            Type = WorkItemType.Parse(snapshot.TypeName).Value,
            Title = snapshot.Title,
            State = snapshot.State,
            AssignedTo = snapshot.AssignedTo,
            IterationPath = ParseIterationPath(snapshot.IterationPath),
            AreaPath = ParseAreaPath(snapshot.AreaPath),
            ParentId = snapshot.ParentId,
            IsSeed = snapshot.IsSeed,
            SeedCreatedAt = snapshot.SeedCreatedAt,
            LastSyncedAt = snapshot.LastSyncedAt,
        };

        if (snapshot.Revision > 0)
            item.MarkSynced(snapshot.Revision);

        item.ImportFields(snapshot.Fields);

        if (snapshot.IsDirty)
            item.SetDirty();

        return item;
    }
}
```

This consolidates parsing logic currently duplicated in `AdoResponseMapper` and
`SqliteWorkItemRepository.MapRow`.

### Data Flow

#### Write Path (CreateAsync) — After

```
SeedFactory.Create() → WorkItem seed
    → seed.ToCreateRequest() → CreateWorkItemRequest
    → IAdoWorkItemService.CreateAsync(request) → int newId
    → AdoRestClient builds JSON Patch from request
```

#### Read Path (FetchAsync) — After

```
AdoRestClient.FetchAsync(id)
    → HTTP GET → AdoWorkItemResponse (existing DTO)
    → AdoResponseMapper.MapToSnapshot(dto) → WorkItemSnapshot
    → WorkItemMapper.Map(snapshot) → WorkItem
    → return WorkItem (unchanged contract)
```

#### Persistence Path (GetByIdAsync) — After

```
SqliteWorkItemRepository.GetByIdAsync(id)
    → SQL SELECT → SqliteDataReader
    → MapRowToSnapshot(reader) → WorkItemSnapshot
    → WorkItemMapper.Map(snapshot) → WorkItem
    → return WorkItem (unchanged contract)
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **DTO lives in Domain, not Infrastructure** | Both ADO and SQLite produce it; domain owns the construction contract. Follows `ExportedWorkItem` precedent. |
| **Primitive types in snapshot** | Decouples infrastructure from domain value object parsing. Adding a new value object type is a mapper-only change. |
| **Keep `IAdoWorkItemService` returning `WorkItem`** | Zero consumer changes. Interface evolution (returning DTOs) is a future consideration. |
| **Extension method for `ToCreateRequest`** | Minimizes caller changes — `seed.ToCreateRequest()` replaces `seed` at call sites. |
| **Single `WorkItemMapper` class (not interface)** | No polymorphism needed; sealed class is AOT-friendly and testable without mocking. |

## Dependencies

### Internal

- **`WorkItemCopier`** (already implemented) — centralizes copy construction; the
  mapper follows the same property-enumeration pattern.
- **`WorkItemBuilder`** (TestKit) — remains unchanged; provides test-side isolation.
- **`TwigJsonContext`** — new types only need registration if serialized/deserialized
  via `System.Text.Json`. `WorkItemSnapshot` does not need registration (it's an
  in-memory transfer type). `CreateWorkItemRequest` does not need registration (it's
  mapped to `List<AdoPatchOperation>` before serialization).

### External

- None. No new NuGet packages or external services.

### Sequencing

- Issue 1 (write-path DTO) has no dependencies and can ship immediately.
- Issue 2 (snapshot + mapper foundation) has no dependencies.
- Issue 3 (ADO read-path refactor) depends on Issue 2.
- Issue 4 (persistence read-path refactor) depends on Issue 2.
- Issues 3 and 4 are independent of each other and can proceed in parallel.

## Impact Analysis

| Area | Impact | Details |
|------|--------|---------|
| **IAdoWorkItemService interface** | `CreateAsync` signature changes | `WorkItem seed` → `CreateWorkItemRequest request`. All other methods unchanged. |
| **AdoResponseMapper** | Internal return type changes | `MapWorkItem` returns `WorkItemSnapshot` instead of `WorkItem`. `MapSeedToCreatePayload` accepts `CreateWorkItemRequest`. |
| **AdoRestClient** | Gains `WorkItemMapper` dependency | Constructor adds mapper parameter. Internal mapping pipeline changes. |
| **SqliteWorkItemRepository** | Gains `WorkItemMapper` dependency | Constructor adds mapper parameter. `MapRow` refactored. |
| **IWorkItemRepository** | No change | Public interface unchanged. |
| **SeedPublishOrchestrator** | Caller update | `CreateAsync(seed)` → `CreateAsync(seed.ToCreateRequest())` |
| **NewCommand** | Caller update | Same `ToCreateRequest()` pattern |
| **CreationTools (MCP)** | Caller update | Same `ToCreateRequest()` pattern |
| **BatchCommand** | Caller update | Same `ToCreateRequest()` pattern |
| **Test mocks** | Mock setup for `CreateAsync` changes | Parameter type changes from `WorkItem` to `CreateWorkItemRequest` |
| **Performance** | Neutral | Snapshot construction is allocation-equivalent to current direct construction. One additional object per read (snapshot + aggregate), but snapshots are short-lived. |
| **Backward compatibility** | Binary breaking for `IAdoWorkItemService.CreateAsync` | All callers must update. Source-compatible via extension method. |

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `CreateAsync` signature change breaks test mocks across 15+ test files | High | Low | Mechanical change; `WorkItem` → `CreateWorkItemRequest` in mock setup. Extension method makes source migration trivial. |
| `WorkItemMapper` misses a property, causing data loss | Low | High | Property-preservation theory test (mirrors `WorkItemCopier` pattern). Round-trip test: Snapshot → WorkItem → verify all fields. |
| Performance regression from double construction (snapshot + aggregate) | Low | Low | Snapshots are stack-friendly records; profile if needed. Current construction already allocates similarly. |
| Concurrent changes to `WorkItem` properties conflict with mapper | Medium | Medium | `WorkItemMapper` and `WorkItemCopier` both enumerate all properties — adding a property causes a compile error in both, providing dual safety nets. |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `WorkItemSnapshot` be a `sealed record` (reference type) or `readonly record struct` (value type)? | **Low** | Record class is safer for the field dictionary. Struct would require careful copy semantics. Recommend `sealed record`. |
| 2 | Should `WorkItemMapper` be registered in DI or used as a direct dependency (no interface)? | **Low** | No polymorphism needed. Direct `new WorkItemMapper()` in infrastructure constructors is simpler. DI registration adds no value. Recommend direct instantiation. |
| 3 | Should `CreateWorkItemRequest.ToCreateRequest()` be an extension method on `WorkItem` or a static factory on `CreateWorkItemRequest`? | **Low** | Extension method keeps `WorkItem` clean. Static factory is more discoverable. Either works. Recommend extension method in `Twig.Domain.Extensions`. |
| 4 | Should `SqliteWorkItemRepository.SaveWorkItem` also use a snapshot intermediary for the write direction? | **Low** | Current direct property access is fine for writes. Introducing a write-side snapshot adds complexity without reducing coupling. Recommend keeping direct access for now. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/WorkItemSnapshot.cs` | Immutable data carrier record |
| `src/Twig.Domain/ValueObjects/CreateWorkItemRequest.cs` | Write-path DTO for `CreateAsync` |
| `src/Twig.Domain/Extensions/WorkItemExtensions.cs` | `ToCreateRequest()` extension method |
| `src/Twig.Domain/Services/WorkItemMapper.cs` | Domain service: Snapshot → WorkItem |
| `tests/Twig.Domain.Tests/ValueObjects/WorkItemSnapshotTests.cs` | Snapshot construction tests |
| `tests/Twig.Domain.Tests/Services/WorkItemMapperTests.cs` | Mapper round-trip and property-preservation tests |
| `tests/Twig.Domain.Tests/ValueObjects/CreateWorkItemRequestTests.cs` | Write DTO tests |
| `tests/Twig.Domain.Tests/Extensions/WorkItemExtensionsTests.cs` | ToCreateRequest conversion tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | `CreateAsync(WorkItem)` → `CreateAsync(CreateWorkItemRequest)` |
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | `MapWorkItem` returns `WorkItemSnapshot`; `MapSeedToCreatePayload` accepts `CreateWorkItemRequest` |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Inject `WorkItemMapper`; use snapshot→aggregate pipeline; `CreateAsync` accepts DTO |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Pass `WorkItemMapper` to `AdoRestClient` constructor |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Inject `WorkItemMapper`; refactor `MapRow` to produce snapshot first |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Pass `WorkItemMapper` to `SqliteWorkItemRepository` |
| `src/Twig.Domain/Services/Seed/SeedPublishOrchestrator.cs` | `CreateAsync(seed)` → `CreateAsync(seed.ToCreateRequest())` |
| `src/Twig/Commands/NewCommand.cs` | `CreateAsync(seed)` → `CreateAsync(seed.ToCreateRequest())` |
| `src/Twig/Commands/BatchCommand.cs` | `CreateAsync(seed)` → `CreateAsync(seed.ToCreateRequest())` |
| `src/Twig.Mcp/Tools/CreationTools.cs` | `CreateAsync(seed)` → `CreateAsync(seed.ToCreateRequest())` |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Update assertions for snapshot return type |
| `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientBatchTests.cs` | Update mock setup for `CreateAsync` parameter type |
| `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | Update mock: `CreateAsync` parameter type |
| `tests/Twig.Cli.Tests/Commands/SeedPublishCommandTests.cs` | Update mock: `CreateAsync` parameter type |
| `tests/Twig.Cli.Tests/Commands/BatchCommandTests.cs` | Update mock: `CreateAsync` parameter type |
| `tests/Twig.Domain.Tests/Services/Seed/SeedPublishOrchestratorTests.cs` | Update mock: `CreateAsync` parameter type |
| `tests/Twig.Mcp.Tests/Services/McpPendingChangeFlusherTests.cs` | Update mock if `CreateAsync` is used |

---

## ADO Work Item Structure

### Issue 1: Write-Path DTO — CreateWorkItemRequest

**Goal:** Decouple `IAdoWorkItemService.CreateAsync` from the `WorkItem` aggregate by
introducing a purpose-built `CreateWorkItemRequest` DTO.

**Prerequisites:** None.

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2122.1 | Define `CreateWorkItemRequest` sealed record in `Twig.Domain.ValueObjects` | `CreateWorkItemRequest.cs` | Small |
| T-2122.2 | Add `ToCreateRequest()` extension method on `WorkItem` in `Twig.Domain.Extensions` | `WorkItemExtensions.cs` | Small |
| T-2122.3 | Change `IAdoWorkItemService.CreateAsync` signature from `WorkItem` to `CreateWorkItemRequest`; update `AdoRestClient.CreateAsync` and `AdoResponseMapper.MapSeedToCreatePayload` | `IAdoWorkItemService.cs`, `AdoRestClient.cs`, `AdoResponseMapper.cs` | Medium |
| T-2122.4 | Update all callers: `SeedPublishOrchestrator`, `NewCommand`, `BatchCommand`, `CreationTools` (MCP) to use `.ToCreateRequest()` | 4 source files | Small |
| T-2122.5 | Update test mocks for `CreateAsync` parameter type across CLI, domain, and MCP test projects | ~8 test files | Medium |
| T-2122.6 | Add unit tests for `CreateWorkItemRequest` construction and `ToCreateRequest()` round-trip | 2 new test files | Small |

**Acceptance Criteria:**
- [ ] `CreateAsync` accepts `CreateWorkItemRequest`, not `WorkItem`
- [ ] All callers use `seed.ToCreateRequest()` to convert
- [ ] Existing seed-publish integration tests pass unchanged
- [ ] `ToCreateRequest()` preserves all fields from the source `WorkItem`
- [ ] No new `WorkItem` construction in infrastructure for the write path

### Issue 2: Domain Snapshot & Mapper Foundation

**Goal:** Introduce `WorkItemSnapshot` and `WorkItemMapper` as domain-level types
for aggregate construction from external data.

**Prerequisites:** None (can proceed in parallel with Issue 1).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2122.7 | Define `WorkItemSnapshot` sealed record in `Twig.Domain.ValueObjects` with all WorkItem data fields using primitive types | `WorkItemSnapshot.cs` | Small |
| T-2122.8 | Implement `WorkItemMapper` sealed class in `Twig.Domain.Services` with `Map(WorkItemSnapshot) → WorkItem` method, consolidating parsing logic from `AdoResponseMapper` and `SqliteWorkItemRepository.MapRow` | `WorkItemMapper.cs` | Medium |
| T-2122.9 | Write comprehensive mapper tests: property preservation theory test, round-trip validation, edge cases (null fields, unknown type, empty paths) | `WorkItemMapperTests.cs`, `WorkItemSnapshotTests.cs` | Medium |

**Acceptance Criteria:**
- [ ] `WorkItemSnapshot` has one-to-one correspondence with all `WorkItem` init-only properties plus `Revision`, `IsDirty`, and `Fields`
- [ ] `WorkItemMapper.Map()` produces a `WorkItem` identical to one constructed directly with the same data
- [ ] Property-preservation theory test enumerates all `WorkItem` properties and asserts none are missed
- [ ] Value object parsing errors (bad iteration path, unknown type) produce sensible defaults (same as current behavior)

### Issue 3: ADO Read-Path Internal Refactor

**Goal:** Move `WorkItem` aggregate construction out of the infrastructure ADO layer
by having `AdoResponseMapper` produce `WorkItemSnapshot` and `AdoRestClient` use
`WorkItemMapper` for the final conversion.

**Prerequisites:** Issue 2 (WorkItemSnapshot & WorkItemMapper must exist).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2122.10 | Refactor `AdoResponseMapper.MapWorkItem` to return `WorkItemSnapshot` instead of `WorkItem`. Rename to `MapToSnapshot`. Extract shared parsing helpers. | `AdoResponseMapper.cs` | Medium |
| T-2122.11 | Refactor `AdoResponseMapper.MapWorkItemWithLinks` to return `(WorkItemSnapshot, IReadOnlyList<WorkItemLink>)` | `AdoResponseMapper.cs` | Small |
| T-2122.12 | Add `WorkItemMapper` as a constructor dependency to `AdoRestClient`. Update `FetchAsync`, `FetchBatchAsync`, `FetchWithLinksAsync`, and `FetchChildrenAsync` to use snapshot→mapper pipeline. | `AdoRestClient.cs` | Medium |
| T-2122.13 | Update `NetworkServiceModule` to pass `WorkItemMapper` instance to `AdoRestClient` constructor | `NetworkServiceModule.cs` | Small |
| T-2122.14 | Update `AdoResponseMapper` tests for snapshot return types. Add integration tests verifying end-to-end ADO→Snapshot→WorkItem pipeline. | `AdoResponseMapperTests.cs`, `AdoResponseMapperLinkTests.cs` | Medium |

**Acceptance Criteria:**
- [ ] `AdoResponseMapper` no longer references `WorkItem` directly (only `WorkItemSnapshot`)
- [ ] `AdoRestClient` uses `WorkItemMapper` for all WorkItem construction
- [ ] All existing `IAdoWorkItemService` consumers work unchanged (return type is still `WorkItem`)
- [ ] `AdoResponseMapper` tests verify snapshot field fidelity
- [ ] No regression in `AdoRestClient` integration tests

### Issue 4: Persistence Read-Path Internal Refactor

**Goal:** Move `WorkItem` aggregate construction out of `SqliteWorkItemRepository`
by using `WorkItemSnapshot` as an intermediary and `WorkItemMapper` for conversion.

**Prerequisites:** Issue 2 (WorkItemSnapshot & WorkItemMapper must exist).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2122.15 | Refactor `SqliteWorkItemRepository.MapRow` to produce `WorkItemSnapshot` from `SqliteDataReader`. Extract to `MapRowToSnapshot`. | `SqliteWorkItemRepository.cs` | Medium |
| T-2122.16 | Add `WorkItemMapper` as a constructor dependency to `SqliteWorkItemRepository`. Update all read methods to use snapshot→mapper pipeline. | `SqliteWorkItemRepository.cs` | Small |
| T-2122.17 | Update `TwigServiceRegistration` to pass `WorkItemMapper` instance to `SqliteWorkItemRepository` | `TwigServiceRegistration.cs` | Small |
| T-2122.18 | Update persistence tests to verify snapshot intermediate step. Add round-trip test: save WorkItem → read back → verify identical. | Persistence test files | Medium |

**Acceptance Criteria:**
- [ ] `SqliteWorkItemRepository.MapRow` no longer directly constructs `WorkItem`
- [ ] `WorkItemMapper` is used for all `WorkItem` construction in the persistence layer
- [ ] All existing `IWorkItemRepository` consumers work unchanged
- [ ] Persistence round-trip tests pass (save → read → compare)
- [ ] `SaveWorkItem` continues to access `WorkItem` properties directly (write path not changed)

---

## PR Groups

### PG-1: Write-Path DTO (Issue 1)

**Classification:** Deep — few files, focused interface change

**Contents:**
- Issue 1, Tasks T-2122.1 through T-2122.6

**Estimated LoC:** ~350
**Estimated Files:** ~15 (4 new + 11 modified)

**Rationale:** The write-path DTO is the lowest-risk change and independently
valuable. It breaks the `WorkItem`→`CreateAsync` coupling without touching
the read path. Can be reviewed and merged independently.

**Successors:** None — PG-1 is independent of PG-2 and PG-3.

---

### PG-2: Snapshot Foundation + ADO Refactor (Issues 2 + 3)

**Classification:** Deep — foundational types plus infrastructure internals

**Contents:**
- Issue 2, Tasks T-2122.7 through T-2122.9 (foundation types)
- Issue 3, Tasks T-2122.10 through T-2122.14 (ADO refactor)

**Estimated LoC:** ~700
**Estimated Files:** ~12 (4 new + 8 modified)

**Rationale:** Issues 2 and 3 are tightly coupled — you need the snapshot type
and mapper (Issue 2) to refactor the ADO layer (Issue 3). Combining them into
one PR provides a complete, reviewable unit: "here are the new types, and here's
how the ADO layer uses them." Separately they'd require reviewing Issue 2 without
seeing its real usage.

**Successors:** PG-3 (persistence refactor uses same foundation types).

---

### PG-3: Persistence Refactor (Issue 4)

**Classification:** Deep — focused on one infrastructure class

**Contents:**
- Issue 4, Tasks T-2122.15 through T-2122.18

**Estimated LoC:** ~250
**Estimated Files:** ~5 (0 new + 5 modified)

**Rationale:** The persistence refactor is the final step. It follows the same
pattern established in PG-2 (snapshot intermediary + mapper) applied to the
SQLite layer. Small, focused, independently reviewable.

**Predecessors:** PG-2 (needs `WorkItemSnapshot` and `WorkItemMapper`).

---

**PR Group Justification (3 groups):** Three PGs are justified because:
1. PG-1 (write path) and PG-2 (read path) are architecturally independent and
   can proceed in parallel, reducing wall-clock time.
2. PG-3 (persistence) touches a different infrastructure subsystem than PG-2 (ADO)
   and benefits from seeing the ADO refactor merged first as a reference.
3. Total LoC (~1300) is well under the 3×2000 per-PG ceiling.

**Execution Order:**
```
PG-1 ──────────────────→ (merge)
PG-2 ──────────────────→ (merge)
                              └──→ PG-3 ──→ (merge)
```

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type |
|-------|------|----------------|--------------|------|
| PG-1 | `PG-1-write-path-dto` | Issue 1 — T-2122.1–T-2122.6 | None | Deep |
| PG-2 | `PG-2-snapshot-foundation-ado-refactor` | Issues 2 + 3 — T-2122.7–T-2122.14 | None | Deep |
| PG-3 | `PG-3-persistence-refactor` | Issue 4 — T-2122.15–T-2122.18 | PG-2 | Deep |

### Execution Order

PG-1 and PG-2 are architecturally independent and can be developed and reviewed in parallel.
PG-3 depends on PG-2 (requires `WorkItemSnapshot` and `WorkItemMapper` to exist) and must
be merged after PG-2.

```
PG-1 ──────────────────────────────────────────────────→ (merge — independent)
PG-2 ──────────────────────────────────────────────────→ (merge — independent)
                                                              └──→ PG-3 ──→ (merge)
```

### Validation Strategy per PG

**PG-1 — Write-Path DTO**
- Build must pass with no warnings (`TreatWarningsAsErrors=true`).
- All new unit tests in `CreateWorkItemRequestTests.cs` and `WorkItemExtensionsTests.cs` pass.
- Updated mock setup in ~8 test files compiles and all tests pass.
- Integration smoke: `twig new` and `twig batch` commands create items without error.

**PG-2 — Snapshot Foundation + ADO Refactor**
- `WorkItemMapperTests.cs` property-preservation theory test enumerates all `WorkItem`
  init-only properties and asserts none are missed by the mapper.
- Round-trip test: construct a `WorkItemSnapshot` → `WorkItemMapper.Map()` → compare all
  fields to expected `WorkItem`.
- `AdoResponseMapperTests.cs` updated assertions verify snapshot field fidelity.
- All `AdoRestClient` integration tests pass unchanged (return type is still `WorkItem`).

**PG-3 — Persistence Refactor**
- `SqliteWorkItemRepository` tests verify the snapshot intermediate step.
- Round-trip test: save a `WorkItem` → read back via repository → assert all fields equal.
- All `IWorkItemRepository` consumers pass existing test suite without modification.
- Confirm `SaveWorkItem` still accesses `WorkItem` properties directly (write path unchanged).

---

## References

- `docs/architecture/domain-model-critique.md` — Item 9 (Domain ↔ Infrastructure Boundary Leak)
- `docs/projects/workitem-aggregate-consolidation.plan.md` — Epic #2114 (prerequisite context)
- `src/Twig.Domain/ValueObjects/ExportedWorkItem.cs` — existing domain DTO precedent
- `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` — current anti-corruption layer
