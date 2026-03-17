---
goal: Extract duplicated process type sync logic into shared domain services
version: 1.1
date_created: 2026-03-15
last_updated: 2026-03-15
completed_epics: EPIC-001, EPIC-002, EPIC-003
owner: Twig CLI Team
tags: refactoring, architecture, domain-services
---

# Process Type Sync Cleanup — Solution Design & Implementation Plan

**Revision notes**: Revision 1.1 — Addressed technical review feedback (score 74/100). See [Revision History](#revision-history) at end of document for detailed change list.

---

## Executive Summary

InitCommand and RefreshCommand both contain ~30 lines of near-identical code that fetches work item types with states from ADO, infers parent-child relationships from the backlog hierarchy, constructs `ProcessTypeRecord` objects, and persists them via `SqliteProcessTypeStore`. Additionally, `AdoIterationService` makes three redundant HTTP calls to the same `_apis/wit/workitemtypes` endpoint during `twig init` because `DetectProcessTemplateAsync`, `GetWorkItemTypeAppearancesAsync`, and `GetWorkItemTypesWithStatesAsync` each independently fetch the full type list. This plan extracts the duplicated sync logic into a `ProcessTypeSyncService` in the Domain layer, moves the `InferParentChildMap` algorithm into a dedicated `BacklogHierarchyService`, caches the workitemtypes response in `AdoIterationService`, and fixes RefreshCommand's direct construction of `SqliteProcessTypeStore` to use DI-resolved `IProcessTypeStore`.

---

## Background

### Current Architecture

The Twig CLI follows a three-layer architecture:

| Layer | Project | Responsibility |
|-------|---------|----------------|
| Presentation | `Twig` (CLI) | Commands, formatters, DI composition |
| Domain | `Twig.Domain` | Value objects, aggregates, interfaces, domain services |
| Infrastructure | `Twig.Infrastructure` | ADO HTTP client, SQLite persistence, config I/O |

**Process type sync flow** (duplicated in both commands):
1. `IIterationService.GetWorkItemTypesWithStatesAsync()` → fetches types + states from ADO
2. `IIterationService.GetProcessConfigurationAsync()` → fetches backlog hierarchy
3. `InitCommand.InferParentChildMap()` → infers parent→children map from hierarchy
4. Loop over types: build `ProcessTypeRecord`, call `IProcessTypeStore.SaveAsync()`

**Redundant HTTP calls in AdoIterationService during `twig init`:**
- `DetectProcessTemplateAsync()` → `GET /{project}/_apis/wit/workitemtypes` (line 66)
- `GetWorkItemTypeAppearancesAsync()` → `GET /{project}/_apis/wit/workitemtypes` (line 103)
- `GetWorkItemTypesWithStatesAsync()` → `GET /{project}/_apis/wit/workitemtypes` (line 126)

All three methods hit the exact same URL. A code comment on lines 95-99 of `AdoIterationService.cs` explicitly acknowledges this inefficiency.

**RefreshCommand DI issue:** RefreshCommand accepts `SqliteCacheStore` directly (line 24 of the primary constructor parameter list) and constructs `new SqliteProcessTypeStore(cacheStore)` on line 161 instead of accepting `IProcessTypeStore` via DI like other commands.

### Context

- The CLI is AOT-published (.NET 9) with `PublishAot=true`, `TrimMode=full`, and `JsonSerializerIsReflectionEnabledByDefault=false`. All JSON serialization uses source-generated `TwigJsonContext`.
- The CLI is single-threaded (one command per process). No thread-safety concerns.
- `InitCommand` has a special constraint: the SQLite database does not exist at DI resolution time. It creates `.twig/` and the DB during execution. This is why it accepts `IAuthenticationProvider` + `HttpClient` instead of `IIterationService` in its production constructor.

---

## Problem Statement

1. **Code duplication**: The fetch→infer→build→persist flow is duplicated across `InitCommand.cs` (lines 211-253) and `RefreshCommand.cs` (lines 139-176). Any change to the sync logic must be applied in two places.

2. **Misplaced domain logic**: `InferParentChildMap` (InitCommand lines 305-333) encodes domain rules about backlog hierarchy but lives on a CLI command class. RefreshCommand references it via `InitCommand.InferParentChildMap(processConfig)` (line 160), creating an awkward cross-command dependency.

3. **Redundant HTTP calls**: `AdoIterationService` makes 3 identical HTTP requests to `_apis/wit/workitemtypes` during a single `twig init` invocation, wasting time and bandwidth.

4. **Leaking infrastructure concern**: `RefreshCommand` takes `SqliteCacheStore` as a constructor parameter (line 24) and constructs `SqliteProcessTypeStore` directly (line 161), bypassing the `IProcessTypeStore` abstraction registered in DI.

---

## Goals and Non-Goals

### Goals

- **G-1**: Eliminate duplicated process type sync code by extracting it into a single domain service
- **G-2**: Move `InferParentChildMap` to an appropriate domain service (`BacklogHierarchyService`)
- **G-3**: Cache the workitemtypes HTTP response in `AdoIterationService` to eliminate 2 redundant calls per init
- **G-4**: Fix `RefreshCommand` to accept `IProcessTypeStore` via DI instead of constructing it from `SqliteCacheStore`
- **G-5**: Maintain AOT compatibility (no reflection, no dynamic code generation)
- **G-6**: All existing tests pass without modification (except updating call sites)

### Non-Goals

- **NG-1**: Changing the `IIterationService` interface contract (methods remain the same)
- **NG-2**: Modifying the SQLite schema or `process_types` table structure
- **NG-3**: Extracting the type-appearance sync logic (config file updates) — that flow is simpler and less duplicated
- **NG-4**: Adding async DI resolution or lazy service patterns beyond what currently exists

---

## Requirements

### Functional Requirements

- **FR-001**: `BacklogHierarchyService.InferParentChildMap(ProcessConfigurationData?)` MUST produce identical output to the current `InitCommand.InferParentChildMap` for all inputs
- **FR-002**: `ProcessTypeSyncService.SyncAsync()` MUST fetch types with states, fetch process configuration, infer parent-child map, build `ProcessTypeRecord` objects, and persist them — matching the behavior currently in both commands. MUST return `Task<int>` where the int is the count of types synced, to enable callers to report progress.
- **FR-003**: `AdoIterationService` MUST cache the `workitemtypes` API response and reuse it across `DetectProcessTemplateAsync`, `GetWorkItemTypeAppearancesAsync`, and `GetWorkItemTypesWithStatesAsync` within a single CLI invocation
- **FR-004**: `RefreshCommand` MUST accept `IProcessTypeStore` via DI and remove direct `SqliteCacheStore` dependency
- **FR-005**: `InitCommand` MUST continue to work when the database does not exist at DI time (it creates the DB during execution)

### Non-Functional Requirements

- **NFR-001**: AOT compatibility — no new reflection usage, all types must be trim-safe
- **NFR-002**: The cache in `AdoIterationService` is per-instance (the CLI creates one instance per invocation); no cross-process caching needed
- **NFR-003**: Console output preservation — All user-visible progress and error messages MUST be preserved. Specifically, `InitCommand` MUST continue to emit: (a) `"Fetching type state sequences..."` before sync begins, (b) `"  Loaded state sequences for {count} type(s)"` after sync completes (using the count returned by `SyncAsync`), and (c) `"Fetching process configuration..."` before sync begins. The `"Fetching process configuration..."` message changes from appearing between two independent fetches to appearing before `SyncAsync` (which encapsulates both fetches). This minor ordering change is accepted. See [RD-007](#design-decisions).

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│  Twig (CLI Layer)                                        │
│  ┌──────────────┐  ┌────────────────┐                    │
│  │ InitCommand   │  │ RefreshCommand  │                   │
│  │ (retains      │  │  delegates to   │                   │
│  │  progress     │  │                 │                   │
│  │  output)      │  │                 │                   │
│  └──────┬───────┘  └───────┬────────┘                    │
│         │                  │                              │
│         └──────┬───────────┘                              │
│                ▼                                          │
├──────────────────────────────────────────────────────────┤
│  Twig.Domain (Domain Layer)                              │
│  ┌──────────────────────┐  ┌───────────────────────────┐ │
│  │ ProcessTypeSyncService│  │ BacklogHierarchyService   │ │
│  │ SyncAsync(            │  │ InferParentChildMap(      │ │
│  │   IIterationService,  │  │   ProcessConfigData?)     │ │
│  │   IProcessTypeStore)  │◄─┤ : Dict<str, List<str>>   │ │
│  │ : Task<int>           │  │                           │ │
│  └──────────────────────┘  └───────────────────────────┘ │
│                                                          │
│  IIterationService    IProcessTypeStore                   │
├──────────────────────────────────────────────────────────┤
│  Twig.Infrastructure                                     │
│  ┌──────────────────────┐  ┌───────────────────────────┐ │
│  │ AdoIterationService   │  │ SqliteProcessTypeStore    │ │
│  │ (cached workitemtypes)│  │                           │ │
│  └──────────────────────┘  └───────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### D-1: `BacklogHierarchyService` (new — `Twig.Domain/Services/BacklogHierarchyService.cs`)

A static class containing the `InferParentChildMap` method, moved from `InitCommand`. This is pure domain logic with no dependencies.

```csharp
namespace Twig.Domain.Services;

public static class BacklogHierarchyService
{
    public static Dictionary<string, List<string>> InferParentChildMap(
        ProcessConfigurationData? config)
    {
        // Exact same implementation as InitCommand.InferParentChildMap
    }
}
```

**Rationale**: Static method on a static class because it's a pure function with no state. Matches the existing pattern of `StateCategoryResolver` in the same namespace.

#### D-2: `ProcessTypeSyncService` (new — `Twig.Domain/Services/ProcessTypeSyncService.cs`)

Encapsulates the fetch→infer→build→persist flow. Accepts `IIterationService` and `IProcessTypeStore` as method parameters (not constructor-injected) because `InitCommand` constructs its own `IIterationService` at runtime and its own `IProcessTypeStore` from a newly-created DB.

**Returns `Task<int>`** — the count of types synced. This enables callers (specifically `InitCommand`) to display the `"Loaded state sequences for {count} type(s)"` progress message without the domain service needing any knowledge of console output.

```csharp
namespace Twig.Domain.Services;

public static class ProcessTypeSyncService
{
    public static async Task<int> SyncAsync(
        IIterationService iterationService,
        IProcessTypeStore processTypeStore,
        CancellationToken ct = default)
    {
        // 1. Fetch types with states
        var typesWithStates = await iterationService.GetWorkItemTypesWithStatesAsync(ct)
            ?? Array.Empty<WorkItemTypeWithStates>();

        // 2. Fetch process configuration
        var processConfig = await iterationService.GetProcessConfigurationAsync(ct)
            ?? new ProcessConfigurationData();

        // 3. Infer parent-child map
        var parentChildMap = BacklogHierarchyService.InferParentChildMap(processConfig);

        // 4. Build and persist ProcessTypeRecord objects
        foreach (var wit in typesWithStates)
        {
            parentChildMap.TryGetValue(wit.Name, out var children);
            var defaultChild = children is { Count: > 0 } ? children[0] : null;

            await processTypeStore.SaveAsync(new ProcessTypeRecord
            {
                TypeName = wit.Name,
                States = wit.States.Select(s =>
                    new StateEntry(s.Name, StateCategoryResolver.ParseCategory(s.Category), s.Color))
                    .ToList(),
                DefaultChildType = defaultChild,
                ValidChildTypes = children ?? [],
                ColorHex = wit.Color,
                IconId = wit.IconId,
            }, ct);
        }

        return typesWithStates.Count;
    }
}
```

**Design decision — static class with method parameters vs. instance with constructor injection**: The static approach is chosen because:
1. `InitCommand` cannot use DI-resolved `IProcessTypeStore` (DB doesn't exist at DI time) — it constructs one after creating the DB
2. `InitCommand` constructs its own `AdoIterationService` with runtime org/project args
3. A static method avoids the need for a factory or deferred construction pattern
4. Matches the pattern of `BacklogHierarchyService` and `StateCategoryResolver`

**Error handling**: The service does NOT catch exceptions from `GetWorkItemTypesWithStatesAsync` or `GetProcessConfigurationAsync`. Error handling is the caller's responsibility because the desired behavior differs:
- `InitCommand` logs warnings and continues (best-effort during init)
- `RefreshCommand` logs to stderr and continues

Both commands will wrap the `SyncAsync` call in their own try/catch blocks.

**Important — behavioral change from current code**: Currently, `InitCommand` has two independent try/catch blocks: one around `GetWorkItemTypesWithStatesAsync` (lines 213-221) and one around `GetProcessConfigurationAsync` (lines 225-232). If the first fetch throws a non-`OperationCanceledException`, the current code still attempts the second fetch. With `SyncAsync` encapsulating both fetches in a single flow, an exception from the first fetch will propagate out of `SyncAsync` entirely, skipping the second fetch. This is an intentional simplification: if the type list cannot be fetched, the process configuration is useless (the build-persist loop would be a no-op anyway since `typesWithStates` would be empty). See [RD-008](#design-decisions).

#### D-3: Command Refactoring

**InitCommand** (modified): Replace lines 211-253 with progress messages and a try/catch wrapping `ProcessTypeSyncService.SyncAsync()`. The three user-visible progress messages are retained in the command:

```csharp
// Fetch state sequences and process configuration for all types
Console.WriteLine(fmt.FormatInfo("Fetching type state sequences..."));
Console.WriteLine(fmt.FormatInfo("Fetching process configuration..."));
var processTypeStore = new Infrastructure.Persistence.SqliteProcessTypeStore(cacheStore);
try
{
    var count = await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore);
    Console.WriteLine(fmt.FormatInfo($"  Loaded state sequences for {count} type(s)"));
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.WriteLine(fmt.FormatInfo($"  ⚠ Could not fetch type data: {ex.Message}"));
}
```

**Note on message ordering**: The `"Fetching process configuration..."` message now appears before `SyncAsync` rather than between the two independent fetches. This is a minor ordering change accepted under [NFR-003](#non-functional-requirements) and documented in [RD-007](#design-decisions).

**RefreshCommand** (modified):
- Remove `SqliteCacheStore cacheStore` from constructor parameter list (line 24)
- Add `IProcessTypeStore processTypeStore` to constructor parameter list
- Replace lines 139-176 with a call to `ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore)`
- Note: `RefreshCommand` does not emit progress messages for the sync flow, so it simply calls `SyncAsync` and ignores the returned count

**Program.cs**: No changes required. `RefreshCommand` is registered at line 135 as `services.AddSingleton<RefreshCommand>()` — a simple registration with no factory and no explicit parameter list. The DI container auto-resolves constructor parameters. Since `IProcessTypeStore` is already registered at line 64, the container will automatically resolve the new `IProcessTypeStore` parameter after `SqliteCacheStore` is removed from `RefreshCommand`'s constructor. The `SqliteCacheStore` registration (line 51) remains unchanged because other services still depend on it.

#### D-4: `AdoIterationService` Caching (modified — `Twig.Infrastructure/Ado/AdoIterationService.cs`)

Add a `Task<AdoWorkItemTypeListResponse?>?` field that caches the parsed response from the workitemtypes endpoint. The first method to call it stores the Task; subsequent methods await the same Task.

```csharp
private Task<AdoWorkItemTypeListResponse?>? _workItemTypesCache;

private async Task<AdoWorkItemTypeListResponse?> GetWorkItemTypesResponseAsync(CancellationToken ct)
{
    // Lazy initialization — safe because CLI is single-threaded
    _workItemTypesCache ??= FetchWorkItemTypesAsync(ct);
    return await _workItemTypesCache;
}

private async Task<AdoWorkItemTypeListResponse?> FetchWorkItemTypesAsync(CancellationToken ct)
{
    var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/workitemtypes?api-version={ApiVersion}";
    using var response = await SendAsync(url, ct);
    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    return await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoWorkItemTypeListResponse, ct);
}
```

Then `DetectProcessTemplateAsync`, `GetWorkItemTypeAppearancesAsync`, and `GetWorkItemTypesWithStatesAsync` all call `GetWorkItemTypesResponseAsync()` instead of making independent HTTP requests.

**Important**: The cache stores the deserialized DTO, not the HTTP response or stream (streams are one-shot). This is safe because the CLI creates one `AdoIterationService` instance per invocation and disposes it at process exit.

### Data Flow

**Before (during `twig init`):**
```
InitCommand
  ├─ DetectProcessTemplateAsync()       → HTTP GET /workitemtypes  (1st call)
  ├─ GetWorkItemTypeAppearancesAsync()  → HTTP GET /workitemtypes  (2nd call)
  ├─ Console: "Fetching type state sequences..."
  ├─ GetWorkItemTypesWithStatesAsync()  → HTTP GET /workitemtypes  (3rd call)
  ├─ Console: "  Loaded state sequences for N type(s)"
  ├─ Console: "Fetching process configuration..."
  ├─ GetProcessConfigurationAsync()     → HTTP GET /processconfiguration
  ├─ InferParentChildMap()              (inline domain logic)
  └─ loop: build + SaveAsync()          (inline persistence)
```

**After:**
```
InitCommand
  ├─ DetectProcessTemplateAsync()       → HTTP GET /workitemtypes  (1st call, cached)
  ├─ GetWorkItemTypeAppearancesAsync()  → reuse cached response    (no HTTP)
  ├─ Console: "Fetching type state sequences..."
  ├─ Console: "Fetching process configuration..."
  ├─ ProcessTypeSyncService.SyncAsync()
  │   ├─ GetWorkItemTypesWithStatesAsync()  → reuse cached response (no HTTP)
  │   ├─ GetProcessConfigurationAsync()     → HTTP GET /processconfiguration
  │   ├─ BacklogHierarchyService.InferParentChildMap()
  │   └─ loop: build + SaveAsync()
  │   └─ return count
  └─ Console: "  Loaded state sequences for {count} type(s)"
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| RD-001 | `ProcessTypeSyncService` is a static class with method parameters | InitCommand cannot use DI-resolved IProcessTypeStore/IIterationService (DB doesn't exist at DI time). Static avoids factory/lazy patterns. |
| RD-002 | Error handling stays in the calling commands, not in the service | InitCommand and RefreshCommand have different error-handling UX (console output formatting). The service should be a pure orchestrator. |
| RD-003 | Cache the deserialized DTO, not the raw HTTP response | Streams are one-shot and cannot be re-read. The DTO is a small in-memory object. |
| RD-004 | Cache is a `Task<T>?` field, not a `T?` field | Caching the Task avoids duplicate in-flight requests if methods were ever called concurrently (defensive, even though CLI is single-threaded). |
| RD-005 | `BacklogHierarchyService` is a separate static class from `ProcessTypeSyncService` | Single Responsibility — hierarchy inference is reusable domain logic independent of the sync workflow. |
| RD-006 | `InitCommand` creates its own `IProcessTypeStore` inside `ExecuteAsync` | The DB file doesn't exist at DI time. InitCommand creates the `SqliteCacheStore` at line 208 after creating `.twig/` and passes it to `ProcessTypeSyncService.SyncAsync()`. This existing pattern is preserved. |
| RD-007 | `SyncAsync` returns `Task<int>` (count of types synced) | Enables `InitCommand` to display `"Loaded state sequences for {count} type(s)"` without the domain service knowing about console output. The `"Fetching process configuration..."` message moves from between the two fetches to before `SyncAsync` — this minor ordering change is accepted as the message content remains identical and the user sees the same information. |
| RD-008 | Independent try/catch blocks in InitCommand are merged into a single try/catch around `SyncAsync` | Currently, if `GetWorkItemTypesWithStatesAsync` throws (lines 213-221), `GetProcessConfigurationAsync` is still attempted (lines 225-232). With `SyncAsync`, the first exception exits entirely. This is an intentional simplification: if the type list fetch fails, `typesWithStates` would be empty and the build-persist loop would be a no-op — the `GetProcessConfigurationAsync` call would be wasted work. `RefreshCommand` already has this same merged behavior (both fetches fail together). |
| RD-009 | No changes required to `Program.cs` for RefreshCommand DI | `Program.cs` line 135 registers `RefreshCommand` as `services.AddSingleton<RefreshCommand>()` with no factory or explicit parameter list. The DI container auto-resolves constructor parameters. `IProcessTypeStore` is already registered at line 64, so replacing `SqliteCacheStore` with `IProcessTypeStore` in RefreshCommand's constructor requires zero `Program.cs` changes. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Instance-based `ProcessTypeSyncService` with constructor DI | Standard DI pattern; easier to mock | Doesn't work for InitCommand (DB doesn't exist at DI time); would need factory or Lazy<T> | Rejected — adds complexity for no benefit |
| Extract `IProcessTypeSyncService` interface | Mockable in tests | Service is a thin orchestrator calling already-mockable interfaces; interface adds boilerplate without value | Rejected — YAGNI |
| Cache HTTP response bytes instead of deserialized DTO | Avoids deserializing twice if only one method is called | Stream handling complexity; negligible perf difference for small payload | Rejected — DTO caching is simpler |
| Move all process type sync to Infrastructure layer | Closer to the ADO/SQLite implementations | Violates layering — sync orchestration is domain logic | Rejected |
| `SyncAsync` returns `Task` (void) | Simpler signature | Callers cannot report synced-type count (breaks NFR-003 for InitCommand's `"Loaded state sequences for N type(s)"` message) | Rejected — returning int is trivial and preserves console output |
| Keep independent try/catch in InitCommand by splitting `SyncAsync` into `FetchAsync` + `PersistAsync` | Preserves exact current error-handling behavior | Defeats the purpose of encapsulation; callers still manually orchestrate the two-phase flow | Rejected — the independent-catch behavior provides no user value (second fetch is wasted if first fails) |

---

## Dependencies

| ID | Dependency | Type | Notes |
|----|-----------|------|-------|
| DEP-001 | `Twig.Domain` project | Internal | New files added; no new package dependencies |
| DEP-002 | `Twig.Infrastructure` project | Internal | `AdoIterationService` modified; no new dependencies |
| DEP-003 | `Twig` (CLI) project | Internal | Commands refactored; no DI registration changes needed |
| DEP-004 | `AdoWorkItemTypeListResponse` DTO | Internal | Must be accessible within `AdoIterationService` (already is — `internal` in same assembly) |
| DEP-005 | `NSubstitute` in `Twig.Domain.Tests` | Test dependency | `Twig.Domain.Tests.csproj` must add `<PackageReference Include="NSubstitute" />`. The version (5.3.0) is already defined in `Directory.Packages.props`. Required for mocking `IIterationService` and `IProcessTypeStore` in `ProcessTypeSyncServiceTests`. |

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|------------|------|
| `InitCommand.cs` | Modified — extract sync logic, remove `InferParentChildMap` | Low |
| `RefreshCommand.cs` | Modified — extract sync logic, change constructor params | Medium (constructor change) |
| `AdoIterationService.cs` | Modified — add response caching | Low |
| `BacklogHierarchyService.cs` | New file | None |
| `ProcessTypeSyncService.cs` | New file | None |
| `Twig.Domain.Tests.csproj` | Modified — add NSubstitute PackageReference | None |
| `InitCommandTests.cs` | Modified — update `InferParentChildMap` call sites | Low |
| `RefreshCommandTests.cs` | Modified — update constructor calls | Low |

**Note**: `Program.cs` is NOT modified. See [RD-009](#design-decisions).

### Backward Compatibility

- **CLI behavior**: All user-visible console messages are preserved. The ordering of `"Fetching process configuration..."` changes relative to the first fetch in `InitCommand` (see [RD-007](#design-decisions)), but message content and count are identical. Exit codes unchanged.
- **Database schema**: No changes to `process_types` table
- **Config file**: No changes to `.twig/config` format
- **Public API**: `InferParentChildMap` moves from `InitCommand` (internal) to `BacklogHierarchyService` (public); callers updated in same PR

### Performance Implications

- **Positive**: Eliminates 2 redundant HTTP calls during `twig init` (saving ~200-500ms depending on network latency)
- **Neutral**: No performance change for `twig refresh` (it only called `GetWorkItemTypesWithStatesAsync` and `GetWorkItemTypeAppearancesAsync` — 2 calls reduced to 1, but refresh also calls other endpoints)

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| InitCommand test breakage due to `InferParentChildMap` relocation | High | Low | Tests updated in same PR; method signature is identical |
| RefreshCommand constructor change breaks DI auto-resolution | Medium | Medium | `IProcessTypeStore` is already registered in DI (line 64 of `Program.cs`). Verify with `dotnet build` + existing integration tests. No `Program.cs` changes needed. |
| Cached DTO shared across methods produces subtle behavior difference | Low | Medium | DTO is immutable (all properties are `init`); each method reads different fields from the same response |
| CancellationToken passed to first caller used for cached Task | Low | Low | CLI is single-threaded; all calls use the same or compatible token |
| InitCommand error handling changes: merged try/catch means `GetProcessConfigurationAsync` is skipped if `GetWorkItemTypesWithStatesAsync` throws | Low | Low | Intentional simplification (see [RD-008](#design-decisions)). If type list fetch fails, the persist loop would be a no-op regardless. `RefreshCommand` already has this merged behavior. `AdoIterationService.GetProcessConfigurationAsync` handles `AdoNotFoundException`/`AdoException` internally, so the most common ADO errors wouldn't propagate to the outer catch anyway. |

---

## Open Questions

1. **Should the cache in `AdoIterationService` be clearable?** Not needed for CLI (one instance per process), but if the class were ever reused in a long-lived service, a `ClearCache()` method would be useful. Deferred as YAGNI.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/BacklogHierarchyService.cs` | Static class with `InferParentChildMap` — moved from `InitCommand` |
| `src/Twig.Domain/Services/ProcessTypeSyncService.cs` | Static class encapsulating the fetch→infer→build→persist sync flow, returns `Task<int>` |
| `tests/Twig.Domain.Tests/Services/BacklogHierarchyServiceTests.cs` | Tests for `InferParentChildMap` (moved from `InitCommandTests.cs`) |
| `tests/Twig.Domain.Tests/Services/ProcessTypeSyncServiceTests.cs` | Tests for `SyncAsync` using NSubstitute mocks |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/InitCommand.cs` | Remove `InferParentChildMap` method (lines 305-333); replace inline sync logic (lines 211-253) with progress messages + `ProcessTypeSyncService.SyncAsync()` call; retain all three console output messages |
| `src/Twig/Commands/RefreshCommand.cs` | Remove `SqliteCacheStore cacheStore` from primary constructor (line 24); add `IProcessTypeStore processTypeStore`; remove `new SqliteProcessTypeStore(cacheStore)` (line 161); replace sync block (lines 139-176) with `ProcessTypeSyncService.SyncAsync()` call |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Add cached `_workItemTypesCache` field; extract `GetWorkItemTypesResponseAsync()` and `FetchWorkItemTypesAsync()`; refactor 3 methods to use cached response; remove redundancy comment (lines 95-99) |
| `tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj` | Add `<PackageReference Include="NSubstitute" />` (version managed centrally in `Directory.Packages.props`) |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Update `InferParentChildMap` call sites from `InitCommand.InferParentChildMap` to `BacklogHierarchyService.InferParentChildMap` (or remove if tests are fully moved to `BacklogHierarchyServiceTests.cs`) |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Update `RefreshCommand` constructor calls: remove `_cacheStore` arg (line 67), add `IProcessTypeStore` mock |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### EPIC-001: Extract Domain Services (BacklogHierarchyService + ProcessTypeSyncService)

**Goal**: Create the two new domain service classes and verify they produce identical results to the inline code.

**Prerequisites**: None

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `BacklogHierarchyService` static class in `Twig.Domain/Services/` with `InferParentChildMap` method — exact copy from `InitCommand.InferParentChildMap` (lines 305-333). Include the XML doc comment. | `src/Twig.Domain/Services/BacklogHierarchyService.cs` | DONE |
| ITEM-002 | IMPL | Create `ProcessTypeSyncService` static class in `Twig.Domain/Services/` with `SyncAsync(IIterationService, IProcessTypeStore, CancellationToken)` returning `Task<int>` (count of types synced). Extracts the infer→build→persist logic and both fetch calls from InitCommand lines 211-253. Does not catch exceptions (callers handle errors). | `src/Twig.Domain/Services/ProcessTypeSyncService.cs` | DONE |
| ITEM-003 | TEST | Move `InferParentChildMapTests` from `InitCommandTests.cs` to a new `BacklogHierarchyServiceTests.cs` in `tests/Twig.Domain.Tests/Services/`. Update call sites from `InitCommand.InferParentChildMap` to `BacklogHierarchyService.InferParentChildMap`. | `tests/Twig.Domain.Tests/Services/BacklogHierarchyServiceTests.cs`, `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | DONE |
| ITEM-004a | IMPL | Add `<PackageReference Include="NSubstitute" />` to `tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj`. The version (5.3.0) is already centrally managed in `Directory.Packages.props`. | `tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj` | DONE |
| ITEM-004b | TEST | Add unit tests for `ProcessTypeSyncService.SyncAsync` in `tests/Twig.Domain.Tests/Services/ProcessTypeSyncServiceTests.cs`. Use NSubstitute to mock `IIterationService` and `IProcessTypeStore`. Verify: (1) calls `GetWorkItemTypesWithStatesAsync` and `GetProcessConfigurationAsync`, (2) calls `BacklogHierarchyService.InferParentChildMap` (verified via correct `ProcessTypeRecord.ValidChildTypes` in saved records), (3) calls `IProcessTypeStore.SaveAsync` with correct `ProcessTypeRecord` values, (4) returns count of types synced, (5) exceptions propagate uncaught. | `tests/Twig.Domain.Tests/Services/ProcessTypeSyncServiceTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `BacklogHierarchyService.InferParentChildMap` passes all existing `InferParentChildMapTests`
- [x] `ProcessTypeSyncService.SyncAsync` unit tests pass (including return-value assertion)
- [x] `dotnet build` succeeds for all projects
- [x] No new analyzer warnings

---

### EPIC-002: Refactor Commands to Use Domain Services

**Goal**: Replace duplicated inline sync logic in InitCommand and RefreshCommand with calls to `ProcessTypeSyncService`. Fix RefreshCommand DI.

**Prerequisites**: EPIC-001

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-005 | IMPL | In `InitCommand.cs`: (a) Remove `InferParentChildMap` method (lines 305-333). (b) Replace the sync block (lines 211-253) with: print `"Fetching type state sequences..."` and `"Fetching process configuration..."` before `SyncAsync`, then call `var count = await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore)` in a try/catch, then print `"  Loaded state sequences for {count} type(s)"` on success. On exception (non-`OperationCanceledException`), print `"  ⚠ Could not fetch type data: {ex.Message}"`. All three original progress messages are preserved. See D-3 section for exact code pattern. | `src/Twig/Commands/InitCommand.cs` | DONE |
| ITEM-006 | IMPL | In `RefreshCommand.cs`: (a) Remove `SqliteCacheStore cacheStore` from primary constructor parameter list (line 24). (b) Add `IProcessTypeStore processTypeStore` to primary constructor parameter list. (c) Replace sync block (lines 139-176) with try/catch wrapping `await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore)`. Keep error-handling stderr output (`Console.Error.WriteLine`). The returned count can be discarded (`_ = await ...` or just `await`). | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-007 | TEST | Update `RefreshCommandTests.cs`: (a) Replace `_cacheStore` constructor arg at line 67 with an `IProcessTypeStore` mock (via `Substitute.For<IProcessTypeStore>()`). (b) Remove direct `_cacheStore` usage for process type assertions — use the mock's received calls instead. (c) Verify all existing tests still pass. The `RefreshCommand` constructor call changes from `new RefreshCommand(..., _cacheStore, ...)` to `new RefreshCommand(..., processTypeStoreMock, ...)`. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | DONE |
| ITEM-008 | TEST | Verify `InitCommandTests.Init_PopulatesProcessTypesTable_WithStateDataAndChildRelationships` still passes after the refactor (it reads directly from SQLite, so the flow must produce identical results). | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `InitCommand` no longer contains `InferParentChildMap`
- [x] `InitCommand` still emits all three progress messages (`"Fetching type state sequences..."`, `"Loaded state sequences for N type(s)"`, `"Fetching process configuration..."`)
- [x] `RefreshCommand` no longer references `SqliteCacheStore` or constructs `SqliteProcessTypeStore`
- [x] `RefreshCommand` accepts `IProcessTypeStore` via constructor
- [x] No changes to `Program.cs`
- [x] All existing `InitCommandTests` pass
- [x] All existing `RefreshCommandTests` pass (with updated constructor)
- [x] `dotnet test` passes for all test projects

---

### EPIC-003: Cache workitemtypes Response in AdoIterationService

**Goal**: Eliminate redundant HTTP calls to the workitemtypes endpoint by caching the deserialized response.

**Prerequisites**: None (can be done in parallel with EPIC-001/002, but logically applied after)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-009 | IMPL | Add `private Task<AdoWorkItemTypeListResponse?>? _workItemTypesCache` field to `AdoIterationService`. Create `private async Task<AdoWorkItemTypeListResponse?> GetWorkItemTypesResponseAsync(CancellationToken ct)` that lazily fetches and caches. Create `private async Task<AdoWorkItemTypeListResponse?> FetchWorkItemTypesAsync(CancellationToken ct)` with the existing HTTP logic. | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-010 | IMPL | Refactor `DetectProcessTemplateAsync` to call `GetWorkItemTypesResponseAsync()` and operate on the cached result instead of making its own HTTP call. | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-011 | IMPL | Refactor `GetWorkItemTypeAppearancesAsync` to call `GetWorkItemTypesResponseAsync()` and operate on the cached result. Remove the obsolete code comment about redundant calls (lines 95-99). | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-012 | IMPL | Refactor `GetWorkItemTypesWithStatesAsync` to call `GetWorkItemTypesResponseAsync()` and operate on the cached result. | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-013 | TEST | Add integration-style test (or verify via existing tests) that calling `DetectProcessTemplateAsync`, `GetWorkItemTypeAppearancesAsync`, and `GetWorkItemTypesWithStatesAsync` in sequence produces correct results from the same cached response. Can use a mock `HttpClient` handler to assert only 1 HTTP request is made. | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceCacheTests.cs` | DONE |

**Acceptance Criteria**:
- [x] Only 1 HTTP call to `_apis/wit/workitemtypes` when all three methods are called
- [x] Each method still returns correct, independently valid results
- [x] Existing `AdoIterationService` tests (if any) continue to pass
- [x] `dotnet test` passes for all test projects

---

## References

- `InitCommand.cs` lines 211-253, 305-333 — current sync logic and `InferParentChildMap`
- `RefreshCommand.cs` lines 139-176 — duplicated sync logic
- `RefreshCommand.cs` line 24 — `SqliteCacheStore cacheStore` primary constructor parameter
- `AdoIterationService.cs` lines 64-159 — three methods hitting same endpoint, with acknowledgement comment at lines 95-99
- `Program.cs` line 64 — DI registration for `IProcessTypeStore`
- `Program.cs` line 135 — `services.AddSingleton<RefreshCommand>()` — simple registration, no factory
- `twig-dynamic-process.plan.md` §7 — documents the backlog hierarchy → parent-child inference algorithm
- `StateCategoryResolver.cs` — existing static domain service pattern to follow
- `Directory.Packages.props` line 18 — `NSubstitute` version 5.3.0 centrally managed

---

## Revision History

### v1.1 (2026-03-15) — Technical review feedback

- **Critical Fix — Console output preservation (NFR-003)**: Changed `SyncAsync` return type from `Task` (void) to `Task<int>` (count of types synced). Updated ITEM-005 to specify that `InitCommand` retains all three progress messages (`"Fetching type state sequences..."`, `"Loaded state sequences for {count} type(s)"`, `"Fetching process configuration..."`) around the `SyncAsync` call. Added RD-007 documenting the minor message-ordering change. Resolved Open Question 1 in favor of `Task<int>`. Updated architecture diagram, code samples, and FR-002.
- **Critical Fix — NSubstitute dependency for Twig.Domain.Tests**: Added ITEM-004a to explicitly add `<PackageReference Include="NSubstitute" />` to `Twig.Domain.Tests.csproj`. Added DEP-005. The version (5.3.0) is already in `Directory.Packages.props` so only one line is needed.
- **Critical Fix — Removed misleading ITEM-007 (Program.cs changes)**: `Program.cs` line 135 is `services.AddSingleton<RefreshCommand>()` — no factory, no explicit parameter list. DI auto-resolves parameters. No `Program.cs` change is required. Removed the old ITEM-007, added RD-009 explaining why, removed `Program.cs` from Impact Analysis "Components Affected" table, and updated Files Affected.
- **Medium Fix — Documented error-handling behavioral change**: Added RD-008 explicitly documenting that InitCommand's two independent try/catch blocks merge into one around `SyncAsync`. Added a risk row for this change with mitigation reasoning. Added the `SyncAsync` vs split alternative to Alternatives Considered table.
- **Minor Fix — RefreshCommand line number**: Corrected `SqliteCacheStore cacheStore` line reference from "line 27" to "line 24" (the actual primary constructor parameter position).
- Renumbered ITEM IDs in EPIC-003 (old ITEM-010 through ITEM-014 → ITEM-009 through ITEM-013) after removing old ITEM-007.
- Added `Twig.Domain.Tests.csproj` to Modified Files table.
- Added new test files to New Files table.
