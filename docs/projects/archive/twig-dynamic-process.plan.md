# Dynamic Process Template Type Resolution

> **Revision**: 8 (marks EPIC-2 through EPIC-5 as DONE, checks acceptance criteria; adds inline algorithm references and review-feedback fixes for error handling and thread-safety comments)  
> **Date**: 2026-03-15  
> **Status**: Implemented

---

## Executive Summary

This design addresses a critical data-loss bug in the Twig CLI where custom Azure DevOps work item types (e.g., `Scenario`, `Deliverable`, `Custom Bug`) from inherited/custom process templates are silently mapped to `Task` at the ADO→domain boundary. The root cause is a closed type system: `WorkItemType.Parse()` rejects any string not in its 13-member hardcoded dictionary, and `AdoResponseMapper` falls back to `WorkItemType.Task` on failure—permanently discarding the original type name. This design opens the type system to accept any valid string, connects the already-fetched-but-unused type metadata (appearances, colors, icons) stored in `process_types` to runtime display logic, and introduces dynamic process configuration for custom templates by fetching state sequences and parent-child relationships from the ADO REST API during `init`/`refresh`.

---

## Background

### Current Architecture

Twig is a .NET 9 Native AOT CLI tool that provides a local-first workflow for Azure DevOps work items. Its architecture follows a clean layered design:

- **Domain Layer** (`Twig.Domain`): Value objects (`WorkItemType`, `IterationPath`, `AreaPath`), aggregates (`WorkItem`, `ProcessConfiguration`), services (`SeedFactory`, `StateTransitionService`, `ConflictResolver`), and interfaces.
- **Infrastructure Layer** (`Twig.Infrastructure`): ADO REST client (`AdoRestClient`, `AdoIterationService`), SQLite persistence (`SqliteCacheStore`, `SqliteWorkItemRepository`), configuration (`TwigConfiguration`), and the anti-corruption mapper (`AdoResponseMapper`).
- **CLI Layer** (`Twig`): Commands, output formatters (`HumanOutputFormatter`, `MinimalOutputFormatter`, `JsonOutputFormatter`), DI wiring (`Program.cs`).

The system currently supports exactly four standard ADO process templates (Basic, Agile, Scrum, CMMI) with fully hardcoded state sequences, parent-child hierarchies, and transition rules in `ProcessConfiguration`.

### What Already Exists But Is Disconnected

1. **TypeAppearances API**: Both `InitCommand` and `RefreshCommand` already call `IIterationService.GetWorkItemTypeAppearancesAsync()`, which fetches ALL work item types from ADO (including custom types) via `GET /{project}/_apis/wit/workitemtypes`. The results include `name`, `color` (hex), and `icon.id`. These are stored in:
   - `config.TypeAppearances` (in `.twig/config` JSON file)
   - `process_types` SQLite table (during refresh)

2. **`process_types` table**: Schema at `SqliteCacheStore.cs:159-167` already has columns for `type_name`, `states_json`, `default_child_type`, `valid_child_types_json`, `color_hex`, `icon_id`, `last_synced_at`. However, `states_json` is always written as `'[]'`, child type columns are always `NULL`. **The table is write-only—nothing reads from it.**

3. **`DisplayConfig.TypeColors`**: The config section exists (`TwigConfiguration.cs:153`, inside `DisplayConfig` class starting at `:148`) and `HumanOutputFormatter` accepts it via constructor injection (`Program.cs:104-106`). Both `InitCommand` (`InitCommand.cs:138-141`) and `RefreshCommand` (`RefreshCommand.cs:130-133`) already bridge TypeAppearances → Display.TypeColors with inline comment `ITEM-157`. This mapping is already operational for both commands.

4. **`HexToAnsi.ToForeground()`**: Already converts hex color strings to ANSI true-color escape sequences. `HumanOutputFormatter.GetTypeColor()` already calls this if `_typeColors` has a match—but the dictionary is always null/empty for auto-discovered types.

### Why Now

Organizations using inherited or custom process templates (which are common in enterprise environments) cannot use Twig at all—their work items are silently corrupted on first sync, with custom type names permanently lost and replaced with "Task". There is no workaround.

---

## Problem Statement

1. **Data Loss at the ADO→Domain Boundary**: `WorkItemType.Parse()` (`WorkItemType.cs:60-69`) returns `Result.Fail` for any type name not in the 13-member `KnownTypes` dictionary. `AdoResponseMapper.ParseWorkItemType()` (`AdoResponseMapper.cs:197-204`) silently falls back to `WorkItemType.Task` on failure. The `work_items.type` column in SQLite then stores `"Task"` instead of the original type name (e.g., `"Scenario"`). This is irreversible—the original name is gone.

2. **Read-Back Double-Failure**: `SqliteWorkItemRepository.MapRow()` (`SqliteWorkItemRepository.cs:237`, with `WorkItemType.Parse()` at line 239) calls `WorkItemType.Parse()` again on the stored string. Even if the DB had the correct custom type name, `Parse()` would reject it and fall back to `Task` again.

3. **Hardcoded Process Configuration**: `ProcessConfiguration.ForTemplate()` (`ProcessConfiguration.cs:76-86`) throws `ArgumentOutOfRangeException` for any template not in {Basic, Agile, Scrum, CMMI}. The `ProcessTemplate` enum itself has only 4 members. Custom/inherited templates cannot be represented.

4. **Broken Display Pipeline**: `GetTypeBadge()` and `GetTypeColor()` in `HumanOutputFormatter` use hardcoded switch expressions (`HumanOutputFormatter.cs:301-333`) that return defaults for any unknown type. The already-available `TypeAppearances` color data is never connected.

5. **Broken Seed Creation**: `SeedCommand` (`SeedCommand.cs:51-54`) calls `WorkItemType.Parse()` on user-supplied type strings, rejecting custom types. `SeedFactory.Create()` validates against `ProcessConfiguration`, which has no entries for custom types—so even if `Parse()` accepted the type, seed creation would fail.

6. **Broken State Transitions**: `StateShorthand.Resolve()` (`StateShorthand.cs:102-133`) returns `null` type groups for unknown types. `StateTransitionService.Evaluate()` returns `TransitionKind.None` (disallowed) for types not in the `ProcessConfiguration`.

---

## Goals and Non-Goals

### Goals

1. **G-1**: `WorkItemType.Parse()` MUST succeed for any non-empty, non-whitespace string, preserving the original casing. Known standard types MUST continue to normalize to their canonical casing (e.g., `"user story"` → `"User Story"`).
2. **G-2**: The `work_items.type` column MUST store the original ADO type name with full fidelity.
3. **G-3**: Custom types MUST be displayable with colors and badges derived from `TypeAppearances` data already fetched from ADO.
4. **G-4**: `init` and `refresh` MUST fetch and store per-type state sequences from the ADO REST API, enabling state transition validation for custom types.
5. **G-5**: `init` and `refresh` MUST fetch and store parent-child type relationships from the ADO process configuration API.
6. **G-6**: All 827+ existing tests MUST continue passing.
7. **G-7**: All changes MUST be AOT-safe (no reflection, source-generated JSON serialization).

### Non-Goals

- **NG-1**: Full parity with ADO's workflow rule engine (conditional rules, group-based restrictions). Only ordered state sequences and basic Forward/Backward/Cut classification.
- **NG-2**: Custom state shorthand mappings for custom types. Custom types will not have shorthand codes (p/c/s/d/x); users must use `twig update System.State "<state>"` for custom types.
- **NG-3**: Runtime fetching of type metadata (lazy loading from ADO). All metadata comes from the `process_types` cache populated during `init`/`refresh`.
- **NG-4**: Migration of existing corrupted databases. Users with affected databases will need `twig init --force` + `twig refresh`.
- **NG-5**: Nerd Font icon mapping for custom types. Custom types will use a deterministic fallback badge.

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1 | `WorkItemType.Parse()` MUST return `Result.Ok` for any non-empty, non-whitespace string | High |
| FR-2 | `WorkItemType` MUST expose an `IsStandard` property indicating whether the type is one of the 13 known standard types | Medium |
| FR-3 | `AdoResponseMapper.ParseWorkItemType()` MUST preserve the original type name from ADO for all types | High |
| FR-4 | `init`/`refresh` MUST extract state sequences per work item type from the `GET /{project}/_apis/wit/workitemtypes` response (states are included per-type in the list response; no `$expand` parameter is needed — see OQ-5 resolution), sorted by `stateCategory` rank | High |
| FR-5 | `init`/`refresh` MUST fetch process configuration via `GET /{project}/_apis/work/processconfiguration` (project-scoped, no team segment) to obtain backlog hierarchy (parent-child relationships) | High |
| FR-6 | `process_types.states_json` MUST be populated with the ordered state array from ADO | High |
| FR-7 | `process_types.default_child_type` and `valid_child_types_json` MUST be populated from process configuration | High |
| FR-8 | `IProcessConfigurationProvider` MUST support dynamic configuration built from `process_types` data | High |
| FR-9 | `HumanOutputFormatter.GetTypeColor()` MUST use `TypeAppearances` color data for types not in the hardcoded map | Medium |
| FR-10 | `HumanOutputFormatter.GetTypeBadge()` MUST return a deterministic badge for unknown types (first letter of type name) | Medium |
| FR-11 | `SeedCommand` MUST accept custom type names via `--type` | Medium |
| FR-12 | `DisplayConfig.TypeColors` MUST be auto-populated from `TypeAppearances` during `init` and `refresh`. **Status**: Already implemented — both `InitCommand` (`InitCommand.cs:138-141`) and `RefreshCommand` (`RefreshCommand.cs:130-133`) perform this mapping via `ITEM-157`. No additional work needed. | Medium |

### Non-Functional Requirements

| ID | Requirement | Metric |
|----|-------------|--------|
| NFR-1 | All changes MUST be compatible with .NET 9 Native AOT (`PublishAot=true`) | Build succeeds with `dotnet publish -c Release` |
| NFR-2 | No reflection usage | Static analysis: zero `System.Reflection` imports in changed files |
| NFR-3 | JSON serialization MUST use source-generated `TwigJsonContext` | All new DTOs registered in `TwigJsonContext.cs` |
| NFR-4 | Schema version bump MUST trigger automatic DB rebuild | `SqliteCacheStore.SchemaVersion` incremented |
| NFR-5 | Backward compatibility: existing `.twig/config` files without `TypeAppearances` MUST load without error | Test with empty config |

---

## Proposed Design

### Architecture Overview

The design makes five coordinated changes across the three layers:

```
┌─────────────────────────────────────────────────────────────────┐
│  CLI Layer (Twig)                                               │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │ InitCommand      │  │ RefreshCommand   │  │ Formatters   │  │
│  │ + fetch states   │  │ + fetch states   │  │ + dynamic    │  │
│  │ + fetch process  │  │ + fetch process  │  │   color/badge│  │
│  │   config         │  │   config         │  │   resolution │  │
│  │ + populate       │  │ + populate       │  │              │  │
│  │   TypeColors     │  │   TypeColors     │  │              │  │
│  └──────────────────┘  └──────────────────┘  └──────────────┘  │
├─────────────────────────────────────────────────────────────────┤
│  Domain Layer (Twig.Domain)                                     │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │ WorkItemType     │  │ ProcessConfig    │  │ IProcessType │  │
│  │ (open type       │  │ + ForTemplate()  │  │ Store        │  │
│  │  system)         │  │ + ForDynamic()   │  │ (new intf)   │  │
│  │ + IsStandard     │  │   NEW factory    │  │              │  │
│  └──────────────────┘  └──────────────────┘  └──────────────┘  │
├─────────────────────────────────────────────────────────────────┤
│  Infrastructure Layer (Twig.Infrastructure)                      │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │ AdoResponseMapper│  │ AdoIteration     │  │ SqliteProcess│  │
│  │ (no fallback)    │  │ Service          │  │ TypeStore    │  │
│  │                  │  │ + GetTypeStates  │  │ (read/write) │  │
│  │                  │  │ + GetProcessCfg  │  │              │  │
│  └──────────────────┘  └──────────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. Open WorkItemType (Domain Layer)

**File**: `src/Twig.Domain/ValueObjects/WorkItemType.cs`

The `Parse()` method changes from rejecting unknown types to accepting any non-empty string:

```csharp
// BEFORE: Returns Result.Fail for unknown types
// AFTER: Always succeeds for non-empty strings
public static Result<WorkItemType> Parse(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return Result.Fail<WorkItemType>("Work item type cannot be empty.");

    var trimmed = raw.Trim();
    
    // Known types normalize to canonical casing
    if (KnownTypes.TryGetValue(trimmed, out var known))
        return Result.Ok(known);

    // Custom types: accept as-is, preserving original casing
    return Result.Ok(new WorkItemType(trimmed));
}

/// <summary>
/// Returns true if this is one of the 13 standard ADO work item types.
/// </summary>
public bool IsStandard => KnownTypes.ContainsKey(Value);
```

The static `KnownTypes` dictionary and all `static readonly` constants remain unchanged. Equality still works via the `record struct`'s value-based equality on `Value`.

**Impact**: This is the foundational fix. Every downstream consumer that calls `Parse()` will now receive the original type name instead of a fallback.

#### 2. AdoResponseMapper Fix (Infrastructure Layer)

**File**: `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs`

The `ParseWorkItemType` method simplifies because `Parse()` now always succeeds for non-empty strings:

```csharp
private static WorkItemType ParseWorkItemType(string? typeName)
{
    if (string.IsNullOrEmpty(typeName))
        return WorkItemType.Task; // fallback only for truly missing data

    var result = WorkItemType.Parse(typeName);
    return result.IsSuccess ? result.Value : WorkItemType.Task;
}
```

The logic is identical in structure but the fallback path is now unreachable for valid ADO responses (ADO always provides a non-empty type name). The fallback is retained defensively for null/empty edge cases.

#### 3. IProcessTypeStore Interface (Domain Layer)

**New File**: `src/Twig.Domain/Interfaces/IProcessTypeStore.cs`

```csharp
public interface IProcessTypeStore
{
    /// <summary>
    /// Gets the stored process type metadata for a given type name.
    /// Returns null if the type is not in the store.
    /// </summary>
    Task<ProcessTypeRecord?> GetByNameAsync(string typeName, CancellationToken ct = default);
    
    /// <summary>
    /// Gets all stored process type records.
    /// </summary>
    Task<IReadOnlyList<ProcessTypeRecord>> GetAllAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Saves or updates a process type record.
    /// </summary>
    Task SaveAsync(ProcessTypeRecord record, CancellationToken ct = default);
}
```

**New File**: `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs`

```csharp
public sealed class ProcessTypeRecord
{
    public string TypeName { get; init; } = string.Empty;
    public IReadOnlyList<string> States { get; init; } = Array.Empty<string>();
    public string? DefaultChildType { get; init; }
    public IReadOnlyList<string> ValidChildTypes { get; init; } = Array.Empty<string>();
    public string? ColorHex { get; init; }
    public string? IconId { get; init; }
}
```

#### 4. SqliteProcessTypeStore (Infrastructure Layer)

**New File**: `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs`

Implements `IProcessTypeStore` against the existing `process_types` table. Reads `states_json` and `valid_child_types_json` using source-generated JSON deserialization.

#### 5. Dynamic ProcessConfiguration (Domain Layer)

**File**: `src/Twig.Domain/Aggregates/ProcessConfiguration.cs`

Add a new factory method. `BuildTypeConfig()` remains `private`—`ForDynamic()` is declared inside the same class and already has access:

```csharp
/// <summary>
/// Builds a ProcessConfiguration dynamically from stored process type records.
/// Used for custom/inherited templates where hardcoded configuration doesn't exist.
/// Falls back to the nearest standard template's configuration for any types
/// that are missing from the dynamic data.
/// </summary>
public static ProcessConfiguration ForDynamic(
    ProcessTemplate baseTemplate,
    IReadOnlyList<ProcessTypeRecord> typeRecords)
{
    // Start with the base template's configuration for standard types
    var baseConfig = ForTemplate(baseTemplate);
    var configs = new Dictionary<WorkItemType, TypeConfig>(baseConfig.TypeConfigs);

    // Add/override with dynamic type data
    foreach (var record in typeRecords)
    {
        if (string.IsNullOrEmpty(record.TypeName))
            continue; // Skip records with missing type name (Parse would fail)

        if (record.States.Count == 0)
            continue; // Skip types with no state data

        // Parse() always succeeds for non-empty strings after EPIC-1
        var type = WorkItemType.Parse(record.TypeName).Value;

        var childTypes = new List<WorkItemType>();
        foreach (var childName in record.ValidChildTypes)
        {
            childTypes.Add(WorkItemType.Parse(childName).Value);
        }

        var typeConfig = BuildTypeConfig(
            record.States.ToArray(),
            childTypes.ToArray());

        configs[type] = typeConfig;
    }

    return new ProcessConfiguration(baseTemplate, configs);
}
```

#### 6. Enhanced IProcessConfigurationProvider (Domain Layer)

**File**: `src/Twig.Domain/Interfaces/IProcessConfigurationProvider.cs`

The interface signature does not change. The implementation changes:

**File**: `src/Twig.Domain/Services/HardCodedProcessConfigProvider.cs` → renamed/replaced

**New File**: `src/Twig.Domain/Services/DynamicProcessConfigProvider.cs`

```csharp
public sealed class DynamicProcessConfigProvider : IProcessConfigurationProvider
{
    private readonly IProcessTypeStore _processTypeStore;
    private ProcessConfiguration? _cachedConfig;
    private ProcessTemplate? _cachedTemplate;

    public DynamicProcessConfigProvider(IProcessTypeStore processTypeStore)
    {
        _processTypeStore = processTypeStore;
    }

    public ProcessConfiguration GetConfiguration(ProcessTemplate template)
    {
        // Lazy initialization: cache after first load to avoid repeated SQLite reads
        // within a single command invocation. The CLI is short-lived (one command per
        // process), so cache invalidation is not needed — a fresh process starts
        // for each command. If the template changes (shouldn't happen within a single
        // invocation), the cache is also invalidated.
        if (_cachedConfig is not null && _cachedTemplate == template)
            return _cachedConfig;

        // Get the base hardcoded config (always available for 4 standard templates)
        var baseConfig = ProcessConfiguration.ForTemplate(template);

        // Attempt to enhance with dynamic data
        var records = _processTypeStore.GetAllAsync().GetAwaiter().GetResult();
        if (records.Count == 0)
        {
            _cachedConfig = baseConfig;
            _cachedTemplate = template;
            return baseConfig;
        }

        var config = ProcessConfiguration.ForDynamic(template, records);
        _cachedConfig = config;
        _cachedTemplate = template;
        return config;
    }
}
```

**Note**: The synchronous `.GetAwaiter().GetResult()` call mirrors the existing pattern where `IProcessConfigurationProvider.GetConfiguration()` is synchronous. This is safe because console applications (like Twig) do not install a custom `SynchronizationContext`, so `.GetAwaiter().GetResult()` on an async method that performs local I/O (SQLite reads) will not deadlock. The interface signature remains sync to avoid a breaking change across all consumers. The lazy cache ensures the SQLite read happens at most once per process invocation, eliminating the repeated-reads overhead that would otherwise regress compared to the pure in-memory `HardCodedProcessConfigProvider`.

#### 7. Enhanced IIterationService (Domain Layer + Infrastructure)

**File**: `src/Twig.Domain/Interfaces/IIterationService.cs`

Add two new methods:

```csharp
/// <summary>
/// Gets all work item types with their state definitions.
/// Reuses the existing GET /_apis/wit/workitemtypes endpoint (same as
/// GetWorkItemTypeAppearancesAsync) but extracts the states array.
/// Each state includes name, category (Proposed/InProgress/Resolved/Completed/Removed),
/// and color. States are returned sorted in workflow order (see state ordering algorithm below).
///
/// The classic WIT list endpoint returns states as a standard property of the
/// WorkItemType response object — no $expand parameter is needed. (The
/// GetWorkItemTypeExpand enum is for the separate Process API endpoint at
/// /_apis/work/processes/{processId}/workitemtypes, not the WIT endpoint.)
///
/// FILTERING: Disabled types are excluded. Null-color types are RETAINED
/// (unlike GetWorkItemTypeAppearancesAsync which filters them) because a
/// custom type may have no color but still have valid states.
///
/// DEFENSIVE VALIDATION: If any non-disabled type in the response has zero
/// states, callers MUST treat that type's state list as unavailable — the type
/// will get an empty states array in process_types. A warning SHOULD be logged.
/// </summary>
Task<IReadOnlyList<WorkItemTypeWithStates>> GetWorkItemTypesWithStatesAsync(
    CancellationToken ct = default);

/// <summary>
/// Gets the process configuration (backlog hierarchy) for the project.
/// Returns a domain-level representation of backlog level definitions from which
/// parent-child type relationships are inferred (see backlog hierarchy inference
/// algorithm below).
///
/// ARCHITECTURAL NOTE: This method returns <see cref="ProcessConfigurationData"/>
/// (a domain DTO defined in Twig.Domain) — NOT the infrastructure-level
/// <c>AdoProcessConfigurationResponse</c> which is <c>internal sealed</c> in
/// Twig.Infrastructure.Ado.Dtos. The domain interface cannot reference infrastructure
/// types because Twig.Domain has no ProjectReference to Twig.Infrastructure
/// (the dependency is unidirectional: Infrastructure → Domain). This follows the
/// same pattern as GetWorkItemTypeAppearancesAsync() which returns domain-level
/// WorkItemTypeAppearance, not the infrastructure AdoWorkItemTypeResponse.
/// </summary>
Task<ProcessConfigurationData> GetProcessConfigurationAsync(
    CancellationToken ct = default);
```

**New domain DTO**: `WorkItemTypeWithStates` (returned by the domain interface):

```csharp
/// <summary>
/// A work item type with its ordered state sequence, used during init/refresh.
/// </summary>
public sealed class WorkItemTypeWithStates
{
    public string Name { get; init; } = string.Empty;
    public string? Color { get; init; }
    public string? IconId { get; init; }
    public IReadOnlyList<WorkItemTypeState> States { get; init; } = Array.Empty<WorkItemTypeState>();
}

public sealed class WorkItemTypeState
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty; // Proposed, InProgress, Resolved, Completed, Removed
}
```

**New domain DTO**: `ProcessConfigurationData` (returned by the domain interface — NOT the infrastructure DTO):

**File**: `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs`

```csharp
/// <summary>
/// Domain-level representation of an ADO process configuration (backlog hierarchy).
/// Mapped from the infrastructure-level <c>AdoProcessConfigurationResponse</c> inside
/// <see cref="AdoIterationService"/>. This DTO lives in the Domain layer so
/// <see cref="IIterationService"/> can reference it without crossing the
/// Domain → Infrastructure dependency boundary.
/// </summary>
public sealed class ProcessConfigurationData
{
    public BacklogLevelConfiguration? TaskBacklog { get; init; }
    public BacklogLevelConfiguration? RequirementBacklog { get; init; }
    public IReadOnlyList<BacklogLevelConfiguration> PortfolioBacklogs { get; init; }
        = Array.Empty<BacklogLevelConfiguration>();
    public BacklogLevelConfiguration? BugWorkItems { get; init; }
}

/// <summary>
/// A single backlog level containing a name and the work item type names that
/// belong to that level.
/// </summary>
public sealed class BacklogLevelConfiguration
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> WorkItemTypeNames { get; init; } = Array.Empty<string>();
}
```

**New infrastructure DTO**: `AdoProcessConfigurationResponse` (internal to Infrastructure, maps the ADO REST response — used only inside `AdoIterationService` for deserialization, then mapped to `ProcessConfigurationData`):

```csharp
/// <summary>
/// DTO for GET /{project}/_apis/work/processconfiguration?api-version=7.1
/// Maps the complete ADO process configuration response structure.
/// INTERNAL to Twig.Infrastructure — callers receive ProcessConfigurationData instead.
/// </summary>
internal sealed class AdoProcessConfigurationResponse
{
    [JsonPropertyName("taskBacklog")]
    public AdoCategoryConfiguration? TaskBacklog { get; set; }

    [JsonPropertyName("requirementBacklog")]
    public AdoCategoryConfiguration? RequirementBacklog { get; set; }

    [JsonPropertyName("portfolioBacklogs")]
    public List<AdoCategoryConfiguration>? PortfolioBacklogs { get; set; }

    [JsonPropertyName("bugWorkItems")]
    public AdoCategoryConfiguration? BugWorkItems { get; set; }
}

/// <summary>
/// A single backlog category configuration — shared structure for
/// taskBacklog, requirementBacklog, each portfolioBacklog entry, and bugWorkItems.
/// </summary>
internal sealed class AdoCategoryConfiguration
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    [JsonPropertyName("workItemTypes")]
    public List<AdoWorkItemTypeRef>? WorkItemTypes { get; set; }
}

/// <summary>
/// Minimal work item type reference within a backlog category.
/// </summary>
internal sealed class AdoWorkItemTypeRef
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
```

**Existing DTO enhancement**: Add `states` to `AdoWorkItemTypeResponse` (captures state data from the existing `GET /_apis/wit/workitemtypes` response, avoiding per-type API calls):

```csharp
// Add to AdoWorkItemTypeResponse.cs:
[JsonPropertyName("states")]
public List<AdoWorkItemStateColor>? States { get; set; }

// New nested DTO:
internal sealed class AdoWorkItemStateColor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}
```

> **`category` field name confirmed (R6)**: The official ADO REST API reference for the `WorkItemStateColor` object at the `GET /_apis/wit/workitemtypes` endpoint documents three fields: `name` (string), `color` (string), and `category` (string — "Category of state"). This is the classic WIT endpoint, distinct from the Process API where the analogous field is named `stateCategory`. The `[JsonPropertyName("category")]` annotation above is verified correct against the [official schema](https://learn.microsoft.com/rest/api/azure/devops/wit/work-item-types/list?view=azure-devops-rest-7.1#workitemstatecolor). Note: the sample response in the docs is truncated (9700+ characters dropped for a 15-type response), so `states` arrays are not shown in the example JSON, but the `WorkItemType` schema explicitly lists `states: WorkItemStateColor[]` as a standard response property and the `WorkItemStateColor` object defines `category` (not `stateCategory`).

**Design change — state fetching optimization**: The `WorkItemType` schema in the [ADO REST API docs](https://learn.microsoft.com/rest/api/azure/devops/wit/work-item-types/list?view=azure-devops-rest-7.1) includes `states: WorkItemStateColor[]` as a property of the `WorkItemType` object returned by the list endpoint. The current `AdoWorkItemTypeResponse` DTO discards this data. By adding the `states` property to the existing DTO, we can extract states from the same HTTP response used by `GetWorkItemTypeAppearancesAsync()` — eliminating N per-type HTTP calls. The new method `GetWorkItemTypesWithStatesAsync()` replaces the per-type `GetWorkItemTypeStatesAsync()` from the previous revision.

> **⚠ STATES AVAILABILITY CLARIFICATION (R5)**: The classic WIT list endpoint (`GET /_apis/wit/workitemtypes`) does NOT have a documented `$expand` parameter — the `WorkItemType` schema includes `states` as a standard response property. The `GetWorkItemTypeExpand` enum (`States = 1`) in the ADO .NET SDK belongs to the **Process API** namespace (`contoso.TeamFoundation.WorkItemTracking.Process.WebApi.Models`), which operates on a different endpoint (`GET /_apis/work/processes/{processId}/workitemtypes`). These are distinct APIs: the Process API manages process-level type definitions with optional expansion; the classic WIT API returns project-level type instances with all properties populated by default. The official sample response is truncated in the docs (9700+ characters dropped from the middle for a 15-type response), consistent with states and transitions being included. No `$expand` parameter is needed or available for the WIT list endpoint.
>
> **Defensive validation**: Despite the above analysis, the implementation MUST still validate: after deserializing the list response, if **any non-disabled type has zero states**, the implementation SHOULD log a warning (`"⚠ States not populated in list response; state transition validation unavailable for affected types"`) and store empty state arrays for those types. This means those types will not have transition validation — but the system remains functional. This is a defensive measure, not an expected failure path.

##### State Ordering Algorithm

The ADO `states` array in the work item type response may not be in workflow sequence order. The implementation MUST sort states into workflow order using the following algorithm:

```
Input:  WorkItemStateColor[] (name, category, color) from ADO response
Output: string[] ordered state names

1. Define category sort order:
   Proposed=0, InProgress=1, Resolved=2, Completed=3, Removed=4, Unknown=5

2. Assign each state a category rank using the mapping above.
   If a state's category is null or not recognized, assign rank Unknown=5.

3. Sort states by:
   a. Primary key: category rank (ascending — Proposed first, Removed last)
   b. Secondary key: original array index (ascending — preserves ADO's
      within-category ordering, which reflects the order states were defined)

4. Extract the sorted state names into the output array.
```

Example for an Agile "User Story":
```
ADO returns: [New(Proposed), Active(InProgress), Resolved(Resolved), Closed(Completed), Removed(Removed)]
After sort:  [New, Active, Resolved, Closed, Removed]  (unchanged — already in order)
```

Example for a custom type with interleaved categories:
```
ADO returns: [Active(InProgress), Draft(Proposed), Review(InProgress), Done(Completed)]
After sort:  [Draft, Active, Review, Done]  (Proposed→InProgress→Completed)
```

This ordering directly determines Forward/Backward/Cut transition classification: a transition from index `i` to index `j` where `j > i` is Forward, `j < i` is Backward, and any transition to "Removed" is Cut (matching `BuildTypeConfig()` logic at `ProcessConfiguration.cs:200-228`).

##### Backlog Hierarchy → Parent-Child Inference Algorithm

The ADO `processconfiguration` response expresses a backlog hierarchy through four structural levels, NOT through explicit parent-child links. Parent-child relationships MUST be inferred positionally using the following algorithm:

```
Input:  ProcessConfigurationData (domain DTO, mapped from ADO response) with:
        - PortfolioBacklogs[]  (array — see ORDERING ASSUMPTION below)
        - RequirementBacklog   (single level — e.g., Stories)
        - TaskBacklog          (single level — e.g., Tasks)
        - BugWorkItems         (separate category — handled by team settings)

Output: Dictionary<string, List<string>> parentToChildTypes

ORDERING ASSUMPTION (R4): The algorithm assumes portfolioBacklogs[0] is the
highest hierarchy level (e.g., Epics) and the array is ordered top-to-bottom.
The ADO REST API sample response shows this ordering ([Epics, Features]),
and the ADO CategoryConfiguration schema has NO 'rank' or 'order' field to
serve as a defensive fallback sort key. This ordering is observed empirically
in all standard and custom process templates tested, but is NOT formally
guaranteed in the API documentation. If a future ADO API change reorders
this array, the inferred hierarchy would be incorrect. The implementation
SHOULD log the inferred hierarchy at debug level so ordering issues are
diagnosable.

Algorithm:

1. Build an ordered list of backlog levels from TOP to BOTTOM:
   levels = PortfolioBacklogs[0], PortfolioBacklogs[1], ..., PortfolioBacklogs[N-1],
            RequirementBacklog, TaskBacklog

2. For each adjacent pair (levels[i], levels[i+1]):
   - All work item type names in levels[i] get levels[i+1]'s type names as valid children.
   - The FIRST type name in levels[i+1] becomes the default child type for levels[i] types.

3. BugWorkItems types are NOT assigned as children in the hierarchy.
   Bug parent-child behavior depends on team-level "Working with bugs" settings
   (tracked as requirements, tasks, or neither). This is controlled per-team and
   cannot be reliably inferred from the processconfiguration response alone.
   Bugs are stored in process_types with empty valid_child_types.

4. TaskBacklog types (bottom level) have no children: valid_child_types = [].
```

Example for Agile process:
```
ADO response:
  portfolioBacklogs = [
    { name: "Epics",    workItemTypes: [{name: "Epic"}] },
    { name: "Features", workItemTypes: [{name: "Feature"}] }
  ]
  requirementBacklog = { name: "Stories", workItemTypes: [{name: "User Story"}] }
  taskBacklog = { name: "Tasks", workItemTypes: [{name: "Task"}] }

Inferred hierarchy:
  levels = [Epics, Features, Stories, Tasks]
  Epic    → children: [Feature],    defaultChild: Feature
  Feature → children: [User Story], defaultChild: User Story
  User Story → children: [Task],    defaultChild: Task
  Task    → children: [],           defaultChild: null
```

Example for custom process with multiple requirement types:
```
ADO response:
  portfolioBacklogs = [
    { name: "Epics",       workItemTypes: [{name: "Epic"}] },
    { name: "Initiatives", workItemTypes: [{name: "Initiative"}, {name: "Scenario"}] }
  ]
  requirementBacklog = { name: "Requirements", workItemTypes: [{name: "Deliverable"}] }
  taskBacklog = { name: "Tasks", workItemTypes: [{name: "Task"}, {name: "Subtask"}] }

Inferred hierarchy:
  Epic       → children: [Initiative, Scenario], defaultChild: Initiative
  Initiative → children: [Deliverable],          defaultChild: Deliverable
  Scenario   → children: [Deliverable],          defaultChild: Deliverable
  Deliverable → children: [Task, Subtask],       defaultChild: Task
  Task       → children: [],                     defaultChild: null
  Subtask    → children: [],                     defaultChild: null
```

**File**: `src/Twig.Infrastructure/Ado/AdoIterationService.cs`

Implement the two new methods:
- `GetWorkItemTypesWithStatesAsync()`: Calls `GET /{project}/_apis/wit/workitemtypes?api-version=7.1` (same endpoint as existing `GetWorkItemTypeAppearancesAsync()`; no `$expand` parameter needed — states are a standard response property, see States Availability Clarification above). Extracts name, color, icon, AND states from each type. Applies the state ordering algorithm above. **Filtering**: filters out disabled types (`isDisabled == true`), but does NOT filter null-color types (unlike `GetWorkItemTypeAppearancesAsync()` which filters `type.Color is null`) — a custom type may have no color but still have valid states and must be included in the process type registry. Applies the **states validation step**: if any non-disabled type has zero states in the response, logs a warning and returns that type with an empty states list. **Note**: This shares the same endpoint as `GetWorkItemTypeAppearancesAsync()` and `DetectProcessTemplateAsync()`. A future optimization could merge these calls (see existing code comment at `AdoIterationService.cs:96-100`).
- `GetProcessConfigurationAsync()`: Calls `GET /{project}/_apis/work/processconfiguration?api-version=7.1`. Deserializes into `AdoProcessConfigurationResponse` (internal infrastructure DTO), then maps to `ProcessConfigurationData` (domain DTO) by extracting backlog level names and work item type names. **Note**: This endpoint does NOT require the `{team}` path segment (unlike `GetCurrentIterationAsync()` and `GetTeamAreaPathsAsync()`). The process configuration is project-scoped, not team-scoped. Verified in [ADO REST API docs](https://learn.microsoft.com/rest/api/azure/devops/work/processconfiguration/get?view=azure-devops-rest-7.1): URL pattern is `GET /{organization}/{project}/_apis/work/processconfiguration`.

The mapping from `AdoProcessConfigurationResponse` → `ProcessConfigurationData` in `AdoIterationService`:
```csharp
// Inside AdoIterationService.GetProcessConfigurationAsync():
var adoResponse = await JsonSerializer.DeserializeAsync(stream,
    TwigJsonContext.Default.AdoProcessConfigurationResponse, ct);

// Map infrastructure DTO → domain DTO
return new ProcessConfigurationData
{
    TaskBacklog = MapBacklogLevel(adoResponse?.TaskBacklog),
    RequirementBacklog = MapBacklogLevel(adoResponse?.RequirementBacklog),
    PortfolioBacklogs = adoResponse?.PortfolioBacklogs?
        .Select(MapBacklogLevel)
        .Where(b => b is not null)
        .Cast<BacklogLevelConfiguration>()
        .ToList()
        ?? (IReadOnlyList<BacklogLevelConfiguration>)Array.Empty<BacklogLevelConfiguration>(),
    BugWorkItems = MapBacklogLevel(adoResponse?.BugWorkItems),
};

static BacklogLevelConfiguration? MapBacklogLevel(AdoCategoryConfiguration? cat)
{
    if (cat is null) return null;
    return new BacklogLevelConfiguration
    {
        Name = cat.Name ?? string.Empty,
        WorkItemTypeNames = cat.WorkItemTypes?
            .Where(t => t.Name is not null)
            .Select(t => t.Name!)
            .ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>(),
    };
}
```

#### 8. Enhanced Init/Refresh Commands (CLI Layer)

**Files**: `src/Twig/Commands/InitCommand.cs`, `src/Twig/Commands/RefreshCommand.cs`

After fetching type appearances (which already happens), add:

1. Call `GetWorkItemTypesWithStatesAsync()` once (reuses the same endpoint) to get states for all types
2. Fetch the process configuration via `GetProcessConfigurationAsync()` to get parent-child relationships (returned as `ProcessConfigurationData` domain DTO)
3. Apply the backlog hierarchy → parent-child inference algorithm (Section 7) to compute `default_child_type` and `valid_child_types` for each type
4. Populate `process_types` table with complete data (states, child types, colors) via `SqliteProcessTypeStore.SaveAsync()`
5. **RefreshCommand only**: Auto-populate `DisplayConfig.TypeColors` from `TypeAppearances` (InitCommand already does this at `InitCommand.cs:138-141`)

**InitCommand connection lifecycle change**: Currently, `InitCommand.ExecuteAsync()` creates a `SqliteCacheStore` and immediately disposes it (`InitCommand.cs:205-206`). This must change because `SqliteProcessTypeStore.SaveAsync()` requires an active connection. The fix:

```csharp
// BEFORE (lines 205-206):
var cacheStore = new Infrastructure.Persistence.SqliteCacheStore($"Data Source={contextPaths.DbPath}");
cacheStore.Dispose();

// AFTER: Keep cacheStore alive for type data persistence, dispose after all writes complete.
using var cacheStore = new Infrastructure.Persistence.SqliteCacheStore($"Data Source={contextPaths.DbPath}");

// ... fetch type states via GetWorkItemTypesWithStatesAsync() ...
// ... fetch process config via GetProcessConfigurationAsync() ...
// ... apply parent-child inference algorithm ...

var processTypeStore = new Infrastructure.Persistence.SqliteProcessTypeStore(cacheStore);
foreach (var record in processTypeRecords)
    await processTypeStore.SaveAsync(record);

// cacheStore.Dispose() called automatically by `using` at end of scope
```

**RefreshCommand** already has an injected `SqliteCacheStore` (`cacheStore` constructor parameter, `RefreshCommand.cs:21`), so no lifecycle change is needed there. Replace the manual SQL inserts (`RefreshCommand.cs:136-152`) with `SqliteProcessTypeStore.SaveAsync()` calls. TypeColors auto-population is already implemented (`RefreshCommand.cs:130-133`, `ITEM-157`) — no additional work needed for FR-12.

#### 9. Dynamic Display Resolution (CLI Layer)

**File**: `src/Twig/Formatters/HumanOutputFormatter.cs`

Modify `GetTypeColor()` and `GetTypeBadge()` to handle arbitrary types:

```csharp
private string GetTypeColor(WorkItemType type)
{
    // 1. Check user-configured TypeColors (already works via _typeColors)
    if (_typeColors is not null &&
        _typeColors.TryGetValue(type.Value, out var hex))
    {
        var trueColor = HexToAnsi.ToForeground(hex);
        if (trueColor is not null)
            return trueColor;
    }

    // 2. Hardcoded colors for standard types (unchanged)
    var hardcoded = type.Value.ToLowerInvariant() switch
    {
        "epic" => Magenta,
        "feature" => Cyan,
        // ... existing cases ...
        _ => (string?)null, // Changed: null instead of Reset
    };
    
    if (hardcoded is not null)
        return hardcoded;

    // 3. Fallback: deterministic color from type name hash
    return DeterministicColor(type.Value);
}

private static string GetTypeBadge(WorkItemType type)
{
    return type.Value.ToLowerInvariant() switch
    {
        // ... existing cases ...
        _ => type.Value.Length > 0 
            ? type.Value[0].ToString().ToUpperInvariant()  // First letter
            : "■",
    };
}

private static string DeterministicColor(string typeName)
{
    // Simple hash-based color selection from a palette of 6 ANSI colors.
    // Bitmask 0x7FFFFFFF clears the sign bit to avoid OverflowException
    // from Math.Abs(int.MinValue) when unchecked multiplication wraps.
    var hash = 0;
    foreach (var c in typeName)
        hash = hash * 31 + c;
    
    return ((hash & 0x7FFFFFFF) % 6) switch
    {
        0 => Magenta,
        1 => Cyan,
        2 => Blue,
        3 => Yellow,
        4 => Green,
        _ => Red,
    };
}
```

### Data Flow

#### Init/Refresh: Type Metadata Population

```
ADO REST API
    │
    ├─ GET /_apis/wit/workitemtypes  (single call, already exists)
    │   → TypeAppearances (name, color, icon) + States (name, category)
    │
    └─ GET /_apis/work/processconfiguration  (new, 1 call)
        → Backlog hierarchy (portfolioBacklogs → requirementBacklog → taskBacklog)
        → Parent-child relationships inferred via backlog level ordering algorithm
    │
    ▼
┌─────────────────────────────────┐
│ process_types table (SQLite)    │
│ ┌─────────────────────────────┐ │
│ │ type_name: "Scenario"      │ │
│ │ states_json: ["New",...]   │ │
│ │ default_child_type: "Task" │ │
│ │ valid_child_types_json:... │ │
│ │ color_hex: "009CCC"        │ │
│ │ icon_id: "icon_list"       │ │
│ └─────────────────────────────┘ │
└─────────────────────────────────┘
    │
    ▼
config.Display.TypeColors (auto-populated)
    {"Scenario": "009CCC", "Deliverable": "773B93", ...}
```

#### Work Item Fetch: Type Preservation

```
ADO REST API → AdoWorkItemResponse (System.WorkItemType = "Scenario")
    │
    ▼
AdoResponseMapper.ParseWorkItemType("Scenario")
    │
    ▼
WorkItemType.Parse("Scenario")  →  Result.Ok(WorkItemType("Scenario"))
    │                                   (IsStandard = false)
    ▼
WorkItem.Type = WorkItemType("Scenario")
    │
    ▼
SqliteWorkItemRepository.SaveWorkItem()
    work_items.type = "Scenario"  ← PRESERVED
    │
    ▼
SqliteWorkItemRepository.MapRow()
    WorkItemType.Parse("Scenario")  →  WorkItemType("Scenario")  ← PRESERVED
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Open `WorkItemType.Parse()` to accept any string rather than introducing a separate `CustomWorkItemType` | Avoids a parallel type hierarchy. `WorkItemType` is already a `readonly record struct` with a string `Value`—it's already structurally ready to hold any string. Adding `IsStandard` provides the discrimination needed without type explosion. |
| DD-2 | Replace `HardCodedProcessConfigProvider` in DI with `DynamicProcessConfigProvider`; `HardCodedProcessConfigProvider` becomes dead code (retained for reference/tests only) | `DynamicProcessConfigProvider` calls `ProcessConfiguration.ForTemplate()` directly for standard template configurations — it never delegates to `HardCodedProcessConfigProvider`. Standard template configurations are domain knowledge encoded in `ForTemplate()`, which doesn't require a database round-trip. Dynamic data from `process_types` enhances/overrides the baseline via `ForDynamic()`. |
| DD-3 | Sync `IProcessConfigurationProvider.GetConfiguration()` reads from SQLite synchronously via `.GetAwaiter().GetResult()` with lazy caching | The interface is sync across 2 consumers (`SeedCommand`, `StateCommand`). This is safe because console applications do not install a custom `SynchronizationContext`, so `.GetAwaiter().GetResult()` on async local I/O will not deadlock. Changing to async would cascade across the entire command chain for negligible benefit. Lazy caching ensures the SQLite read happens at most once per process invocation, maintaining parity with the pure in-memory `HardCodedProcessConfigProvider`. |
| DD-4 | Extract states from existing `GET /_apis/wit/workitemtypes` list response rather than making per-type calls | The ADO `WorkItemType` schema includes `states: WorkItemStateColor[]`. By adding the `states` property to the existing DTO, we extract states from the same HTTP response used by `GetWorkItemTypeAppearancesAsync()` — eliminating N per-type HTTP calls. The classic WIT list endpoint returns states by default (no `$expand` needed — `GetWorkItemTypeExpand` is for the separate Process API). A defensive validation step logs a warning if any non-disabled type has zero states. |
| DD-5 | Auto-populate `DisplayConfig.TypeColors` from `TypeAppearances` rather than requiring manual config | Zero-configuration experience for custom types. Users can still override via `twig config` if desired. |
| DD-6 | Deterministic badge for unknown types uses first letter rather than a configurable mapping | Simple, predictable, no configuration needed. "S" for "Scenario" is immediately recognizable. |
| DD-7 | Increment `SqliteCacheStore.SchemaVersion` to trigger DB rebuild | The `process_types` table schema doesn't change, but the data contract does (states_json goes from empty `[]` to populated). A version bump ensures clean data on upgrade. |
| DD-8 | Retain `ProcessTemplate` enum with 4 members, store detected template name separately in context | Adding enum members for every possible custom template is impractical. The enum identifies the "base" template for hardcoded fallback. Custom/inherited templates map to their nearest standard parent. |
| DD-9 | `IIterationService.GetProcessConfigurationAsync()` returns domain DTO `ProcessConfigurationData` instead of infrastructure DTO `AdoProcessConfigurationResponse` | The `IIterationService` interface is declared in `Twig.Domain`, which has no `ProjectReference` to `Twig.Infrastructure`. `AdoProcessConfigurationResponse` is `internal sealed` in `Twig.Infrastructure.Ado.Dtos` — referencing it from the domain interface would be a compile-time error. This follows the existing pattern: `GetWorkItemTypeAppearancesAsync()` returns `WorkItemTypeAppearance` (domain), not `AdoWorkItemTypeResponse` (infrastructure). |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **A1**: Separate `CustomWorkItemType` class | Strong typing; clear distinction | Requires `IWorkItemType` interface or union type; touches every consumer; polymorphism issues with `record struct` | Rejected — too invasive for marginal benefit |
| **A2**: Make `IProcessConfigurationProvider` async | Cleaner async-all-the-way pattern | Cascading signature changes across 2 commands, `SeedFactory`, `StateTransitionService`; ~30 test files affected | Rejected — disproportionate churn for local SQLite reads |
| **A3**: Lazy-fetch type metadata on cache miss | No extra HTTP calls during init/refresh | Adds latency to user commands; requires network during normal operations; breaks offline-first model | Rejected — contradicts Twig's local-first architecture |
| **A4**: Store type metadata in config JSON only (not SQLite) | Simpler persistence | Config file is human-edited, harder to query, no schema enforcement | Rejected — SQLite table already exists and is better suited |
| **A5**: Return `AdoProcessConfigurationResponse` from `IIterationService.GetProcessConfigurationAsync()` | Simpler — no mapping layer needed | Compile-time error: domain interface cannot reference `internal sealed` infrastructure DTO; violates unidirectional dependency (Domain → Infrastructure is prohibited) | Rejected — architectural violation (DD-9) |
| **A6**: Move backlog hierarchy inference algorithm into `AdoIterationService` so it returns `Dictionary<string, List<string>>` directly | Clean separation; callers don't need to understand backlog structure | Less flexible if future callers need raw backlog data; moves domain logic into infrastructure | Considered — viable alternative but chosen approach (return `ProcessConfigurationData` domain DTO) is more consistent with existing patterns and keeps inference logic testable in the caller |

---

## Dependencies

### External Dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| ADO REST API v7.1 | 7.1 | Work item type states endpoint, process configuration endpoint |
| contoso.Data.Sqlite | (existing) | Process type store persistence |
| System.Text.Json source gen | (existing) | New DTO serialization |

### Internal Dependencies

| Component | Dependency | Nature |
|-----------|------------|--------|
| Epic 2 (Dynamic Config) | Epic 1 (Open Type System) | `ProcessConfiguration.ForDynamic()` needs open `WorkItemType` |
| Epic 3 (Display Bridge) | Epic 1 (Open Type System) | Formatters need to receive custom type names |
| Epic 4 (Init/Refresh) | Epic 2 (Dynamic Config) + ADO endpoints | Must populate process_types with state/child data |
| Epic 5 (Commands) | Epic 1 + Epic 2 | SeedCommand/StateCommand need open types + dynamic config |

### Sequencing Constraints

Epics MUST be implemented in order: 1 → 2 → 3 → 4 → 5. Each epic builds on the previous one's changes.

---

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `WorkItemType` | Core behavior change — `Parse()` semantics | Medium — 19 direct test references |
| `AdoResponseMapper` | Simplified fallback logic | Low — behavior aligns with fix |
| `SqliteWorkItemRepository` | Read-back path now works for custom types | Low — no code change needed if Parse() is opened |
| `ProcessConfiguration` | New factory method | Low — additive change |
| `HumanOutputFormatter` | Badge/color for unknown types | Low — additive change |
| `InitCommand` / `RefreshCommand` | Additional API calls during sync | Medium — new HTTP calls could fail |
| `SeedCommand` / `StateCommand` | Accept custom types | Medium — behavior change |
| `StateShorthand` | Custom types return "not recognized" | Low — existing behavior for unknown types |
| `SqliteCacheStore` | Schema version bump | Low — triggers clean rebuild |

### Backward Compatibility

- **Config files**: Existing `.twig/config` files without `TypeColors` will work (property is nullable, defaults to null).
- **Databases**: Schema version bump forces rebuild on first run after upgrade. Users lose cached work items (re-fetched on `twig refresh`).
- **CLI behavior**: `twig seed --type "Custom Type"` will now work instead of erroring. This is a net improvement.
- **Test impact**: `WorkItemTypeTests.Parse_UnknownType_ReturnsFail` test must be updated (it now succeeds). `AdoResponseMapperTests.MapWorkItem_UnknownWorkItemType_FallsBackToTask` test must be updated (it now preserves the custom type). Additionally, any tests that assert the `"■"` default badge from `GetTypeBadge()` for unrecognized types must be updated—the new default returns the first letter of the type name instead. This affects ALL currently-unrecognized types (including any future ADO built-in types not yet in the switch), not only custom process types.

### Performance

- `init`: 1 additional HTTP request for process configuration (`GET /_apis/work/processconfiguration`). State data is extracted from the existing `GET /_apis/wit/workitemtypes` response (no per-type calls needed). Total init time increases by ~0.5-1 second.
- `refresh`: Same additional request. This is acceptable for an infrequent operation.
- Runtime: `DynamicProcessConfigProvider.GetConfiguration()` replaces the pure in-memory `HardCodedProcessConfigProvider` with a provider that performs a single synchronous SQLite table scan on first invocation. Results are cached via lazy initialization (see Section 6), so subsequent calls within the same CLI process return the cached `ProcessConfiguration` with no additional I/O. The first-call cost is sub-millisecond for the expected table size (<50 rows). `WorkItemType.Parse()` adds one dictionary lookup (same as before) plus a direct construction on miss—negligible.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| ADO states API returns states in non-sequential order | Medium | Medium | Sort states by their `stateCategory` (Proposed → InProgress → Resolved → Completed → Removed) and within category by original array index |
| Process configuration API unavailable for some project types | Low | Medium | Graceful fallback: if API returns 404, log warning and skip child-type population. `process_types` will have empty child types, which means seed creation requires explicit `--type` |
| Custom type names with special characters break SQLite or JSON | Low | Medium | Type names come from ADO API (already validated server-side). Parameterized SQL prevents injection. |
| `record struct` equality breaks for custom types with different casing | Medium | High | `Parse()` preserves original casing for custom types. Two `WorkItemType` values with different casing (e.g., "scenario" vs "Scenario") will NOT be equal. This is correct behavior—ADO type names are case-sensitive. |
| States not populated in work item type list response | Low | Medium | The classic WIT list endpoint (`/_apis/wit/workitemtypes`) returns `WorkItemType` objects with `states` as a standard property — no `$expand` is needed (the `GetWorkItemTypeExpand` enum is for the separate Process API at `/_apis/work/processes/{processId}/workitemtypes`). Risk is low but a defensive validation step logs a warning if any non-disabled type has zero states. See DD-4 and States Availability Clarification. |
| `portfolioBacklogs` array ordering not formally guaranteed | Low | High | The ADO sample response shows top-to-bottom ordering ([Epics, Features]) and this is consistent across all tested process templates. However, the `CategoryConfiguration` schema has no `rank` field. If ordering changes, the inferred hierarchy would be incorrect. Mitigation: log inferred hierarchy at debug level for diagnosability. See backlog hierarchy inference algorithm ordering assumption. |

---

## Open Questions

| # | Question | Context | Impact |
|---|----------|---------|--------|
| OQ-1 | Should `StateShorthand` support dynamic mappings for custom types? | Currently returns "not recognized" for custom types. Could map p/c/d/x to first/middle/last states dynamically. | Medium — usability improvement but adds complexity. Recommend deferring to a follow-up. |
| OQ-2 | Should `ProcessTemplate` enum be extended or replaced with a string? | Custom templates map to the nearest standard template via heuristic detection. Enum is used in `StateShorthand` switch expressions extensively. | High — replacing with string cascades through many files. Recommend keeping enum as "base template" identifier. |
| OQ-3 | ~~How should the ADO states response `stateCategory` field be used?~~ **Resolved in R3**: State categories are now used as the primary sort key in the state ordering algorithm (Section 7). Categories map to transition classification: Proposed/InProgress states enable Forward/Backward, Removed enables Cut. | Resolved — see Section 7 state ordering algorithm |
| OQ-4 | ~~Should `twig refresh` parallelize state-fetching HTTP calls?~~ **Resolved in R3**: Moot — states are now extracted from the existing `GET /_apis/wit/workitemtypes` list response in a single HTTP call, not fetched per-type. | Resolved — no per-type calls needed |
| OQ-5 | ~~Are states populated in the `GET /_apis/wit/workitemtypes` list response without `$expand`?~~ **Resolved in R5**: Yes. The classic WIT list endpoint (`/_apis/wit/workitemtypes`) has no documented `$expand` parameter — `states` is a standard property of the `WorkItemType` response object. The `GetWorkItemTypeExpand` enum (`States = 1`) belongs to the **Process API** namespace (`contoso.TeamFoundation.WorkItemTracking.Process.WebApi.Models`), which operates on a different endpoint (`/_apis/work/processes/{processId}/workitemtypes`). The official sample response is truncated (9700+ characters dropped), consistent with states and transitions being included. A defensive validation step is retained (empty-states warning) but this is not expected to trigger. | Resolved — see States Availability Clarification in Section 7 |

---

## Implementation Phases

### Phase 1: Foundation (Epic 1-2)
Open the type system and add dynamic process configuration support. This phase eliminates the data-loss bug and establishes the infrastructure for dynamic type handling.

**Exit Criteria**: Custom type names survive ADO→SQLite→domain round-trip. All existing tests pass (with 2 tests updated).

### Phase 2: Data Population (Epic 3-4)
Fetch and store complete type metadata from ADO. Connect appearance data to display pipeline.

**Exit Criteria**: `process_types` table populated with state sequences and child types during init/refresh. Colors auto-populate.

### Phase 3: Command Integration (Epic 5)
Enable custom types in seed creation, state transitions, and display rendering.

**Exit Criteria**: `twig seed --type "Scenario" "My scenario"` works. Custom types display with ADO colors and deterministic badges.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IProcessTypeStore.cs` | Interface for reading/writing process type metadata |
| `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` | Domain record for process type metadata (states, child types, color, icon) |
| `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` | SQLite implementation of `IProcessTypeStore` against `process_types` table |
| `src/Twig.Domain/Services/DynamicProcessConfigProvider.cs` | `IProcessConfigurationProvider` impl that merges hardcoded + dynamic type data |
| `src/Twig.Domain/ValueObjects/WorkItemTypeWithStates.cs` | Domain DTOs for work item type with state definitions (`WorkItemTypeWithStates`, `WorkItemTypeState`) used during init/refresh |
| `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs` | Domain DTOs for process configuration (`ProcessConfigurationData`, `BacklogLevelConfiguration`). Lives in the Domain layer so `IIterationService` can reference it without crossing the Domain→Infrastructure boundary (DD-9). |
| `src/Twig.Infrastructure/Ado/Dtos/AdoProcessConfigurationResponse.cs` | Infrastructure DTOs for `GET _apis/work/processconfiguration` response: `AdoProcessConfigurationResponse`, `AdoCategoryConfiguration`, `AdoWorkItemTypeRef`. Internal to Infrastructure — mapped to `ProcessConfigurationData` inside `AdoIterationService`. |
| `tests/Twig.Domain.Tests/Aggregates/ProcessTypeRecordTests.cs` | Unit tests for ProcessTypeRecord |
| `tests/Twig.Domain.Tests/Services/DynamicProcessConfigProviderTests.cs` | Unit tests for dynamic config provider |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteProcessTypeStoreTests.cs` | Integration tests for process type store |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/WorkItemType.cs` | Open `Parse()` to accept any string; add `IsStandard` property |
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | Add `ForDynamic()` factory method; `BuildTypeConfig()` remains `private` (accessible from `ForDynamic()` in the same class) |
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Simplify `ParseWorkItemType()` (fallback path becomes unreachable for valid ADO data) |
| `src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs` | Add `States` property (`List<AdoWorkItemStateColor>?`) and new `AdoWorkItemStateColor` DTO to capture state data from the existing list endpoint |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Add `GetWorkItemTypesWithStatesAsync()` (extracts states from existing list endpoint with ordering algorithm, states validation step, and filtering: disabled types excluded, null-color types retained) and `GetProcessConfigurationAsync()` (deserializes `AdoProcessConfigurationResponse`, maps to domain `ProcessConfigurationData`) |
| `src/Twig.Domain/Interfaces/IIterationService.cs` | Add `GetWorkItemTypesWithStatesAsync()` (returns `IReadOnlyList<WorkItemTypeWithStates>`) and `GetProcessConfigurationAsync()` (returns `ProcessConfigurationData`) method signatures |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Increment `SchemaVersion` from 2 to 3 |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | No schema change needed (TypeColors already exists) |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Register new DTO types for source-generated serialization: `AdoProcessConfigurationResponse`, `AdoCategoryConfiguration`, `AdoWorkItemTypeRef`, `AdoWorkItemStateColor`, `List<string>` (required for AOT-correct deserialization of `states_json` and `valid_child_types_json`) |
| `src/Twig/Commands/InitCommand.cs` | Add state/process-config fetching. **Connection lifecycle fix**: change `SqliteCacheStore` from create-and-dispose (`InitCommand.cs:205-206`) to `using var` pattern so connection stays open for `SqliteProcessTypeStore.SaveAsync()` calls. Note: TypeColors auto-population already exists at `InitCommand.cs:138-141` — no change needed for that. |
| `src/Twig/Commands/RefreshCommand.cs` | Add state/process-config fetching. Replace manual SQL inserts (`RefreshCommand.cs:136-152`) with `SqliteProcessTypeStore.SaveAsync()`. TypeColors auto-population already exists (`RefreshCommand.cs:130-133`, `ITEM-157`). |
| `src/Twig/Commands/SeedCommand.cs` | Remove `WorkItemType.Parse()` gate on `--type` (now accepts custom types) |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add deterministic badge/color for unknown types |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | **No changes required.** `MinimalOutputFormatter` uses `{item.Type}` (line 18) which calls `WorkItemType.ToString()` → returns `Value`. After EPIC-1 opens the type system, custom types will display their original name correctly without modification. This formatter has no badge/color logic to update. |
| `src/Twig/Program.cs` | Wire `DynamicProcessConfigProvider` + `IProcessTypeStore` in DI |
| `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | Update `Parse_UnknownType_ReturnsFail` → now succeeds; add custom type tests |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Update `MapWorkItem_UnknownWorkItemType_FallsBackToTask` → now preserves type |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Add tests for custom type badge/color rendering |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | `HardCodedProcessConfigProvider.cs` is retained in the codebase for reference and tests but is no longer wired in DI after ITEM-010 replaces it with `DynamicProcessConfigProvider`. It becomes dead code — `DynamicProcessConfigProvider` calls `ProcessConfiguration.ForTemplate()` directly and never delegates to `HardCodedProcessConfigProvider`. |

---

## Implementation Plan

### EPIC-1: Open the Type System

**Status**: DONE (completed 2026-03-15)

**Goal**: Make `WorkItemType.Parse()` accept any non-empty string, preserving full type name fidelity through the ADO→domain→SQLite→domain round-trip.

**Prerequisites**: None (first epic).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Modify `WorkItemType.Parse()` to return `Result.Ok` for unknown types (accept any non-empty trimmed string). Known types continue to normalize to canonical casing. Add `IsStandard` property. | `src/Twig.Domain/ValueObjects/WorkItemType.cs` | DONE |
| ITEM-002 | TEST | Update `WorkItemTypeTests.Parse_UnknownType_ReturnsFail` to verify custom types now succeed. Add tests: `Parse_CustomType_ReturnsSuccess`, `Parse_CustomType_PreservesCasing`, `IsStandard_KnownType_ReturnsTrue`, `IsStandard_CustomType_ReturnsFalse`, `Equality_DifferentCasing_CustomType`. | `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | DONE |
| ITEM-003 | TEST | Update `AdoResponseMapperTests.MapWorkItem_UnknownWorkItemType_FallsBackToTask` to verify custom type is preserved (e.g., `"CustomType"` → `WorkItemType("CustomType")`). | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | DONE |
| ITEM-004 | TEST | Add `SqliteWorkItemRepositoryTests` test verifying custom type name round-trips through save→load without data loss. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `WorkItemType.Parse("Scenario")` returns `Result.Ok` with `Value == "Scenario"`
- [x] `WorkItemType.Parse("Scenario").Value.IsStandard` returns `false`
- [x] `WorkItemType.Parse("Bug").Value.IsStandard` returns `true`
- [x] All 827+ existing tests pass (with 2 updated tests)
- [x] Custom type name survives SQLite round-trip

---

### EPIC-2: Dynamic Process Configuration Infrastructure

**Status**: DONE (completed 2026-03-15)

**Goal**: Create the domain interfaces and infrastructure for reading/writing process type metadata, and build a dynamic `ProcessConfiguration` from stored data.

**Prerequisites**: EPIC-1 (WorkItemType must accept custom types).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-005 | IMPL | Create `ProcessTypeRecord` domain aggregate with properties: `TypeName`, `States` (ordered list), `DefaultChildType`, `ValidChildTypes`, `ColorHex`, `IconId`. | `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` | DONE |
| ITEM-006 | IMPL | Create `IProcessTypeStore` interface with `GetByNameAsync`, `GetAllAsync`, `SaveAsync`. | `src/Twig.Domain/Interfaces/IProcessTypeStore.cs` | DONE |
| ITEM-007 | IMPL | Implement `SqliteProcessTypeStore` reading from/writing to the existing `process_types` table. Use source-generated JSON for `states_json` and `valid_child_types_json` deserialization. **IMPORTANT**: `[JsonSerializable(typeof(List<string>))]` is NOT currently registered in `TwigJsonContext.cs` — it MUST be added for strict AOT correctness. While `System.Text.Json` source generation may handle `List<string>` implicitly in some configurations, explicit registration is required to guarantee correct behavior under `PublishAot=true` with `JsonSerializerIsReflectionEnabledByDefault=false`. | `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-008 | IMPL | Add `ProcessConfiguration.ForDynamic()` factory method that merges hardcoded base config with dynamic `ProcessTypeRecord` data. `BuildTypeConfig()` remains `private` since `ForDynamic()` is declared inside the same class and already has access. | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | DONE |
| ITEM-009 | IMPL | Create `DynamicProcessConfigProvider` that reads from `IProcessTypeStore` and merges with `ForTemplate()`. | `src/Twig.Domain/Services/DynamicProcessConfigProvider.cs` | DONE |
| ITEM-010 | IMPL | Wire `DynamicProcessConfigProvider` and `SqliteProcessTypeStore` in DI (`Program.cs`), replacing `HardCodedProcessConfigProvider`. | `src/Twig/Program.cs` | DONE |
| ITEM-011 | TEST | Unit tests for `ProcessConfiguration.ForDynamic()`: verify custom type config built from records, verify standard types from base config preserved, verify empty records fall back to base. | `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | DONE |
| ITEM-012 | TEST | Unit tests for `DynamicProcessConfigProvider`: verify merge behavior, verify fallback when store is empty, verify lazy caching (second call returns same instance without additional store reads). | `tests/Twig.Domain.Tests/Services/DynamicProcessConfigProviderTests.cs` | DONE |
| ITEM-013 | TEST | Integration tests for `SqliteProcessTypeStore`: save/load round-trip, GetAll, GetByName. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteProcessTypeStoreTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `ProcessConfiguration.ForDynamic()` produces `TypeConfig` entries for custom types with correct state sequences and child types
- [x] `DynamicProcessConfigProvider.GetConfiguration()` returns merged config with both standard and custom types
- [x] `DynamicProcessConfigProvider.GetConfiguration()` caches result after first call; subsequent calls return cached instance without additional SQLite reads
- [x] `SqliteProcessTypeStore` correctly reads/writes `process_types` table
- [x] All tests pass

---

### EPIC-3: Display Pipeline for Custom Types

**Status**: DONE (completed 2026-03-15)

**Goal**: Connect TypeAppearances data to the display formatters so custom types render with their ADO-assigned colors and deterministic badges.

**Prerequisites**: EPIC-1 (formatters must receive custom type names).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-014 | IMPL | Modify `HumanOutputFormatter.GetTypeBadge()` to return the first letter of the type name (uppercased) for types not in the hardcoded switch. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-015 | IMPL | Modify `HumanOutputFormatter.GetTypeColor()` to fall through to a deterministic hash-based color when type is not in `_typeColors` and not in the hardcoded switch. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-016 | TEST | Add tests for custom type badge rendering: `GetTypeBadge_CustomType_ReturnsFirstLetter`, `GetTypeColor_CustomType_WithTypeColors_UsesConfiguredColor`, `GetTypeColor_CustomType_NoConfig_ReturnsDeterministicColor`. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `GetTypeBadge(WorkItemType.Parse("Scenario").Value)` returns `"S"`
- [x] `GetTypeBadge(WorkItemType.Parse("Deliverable").Value)` returns `"D"`
- [x] Custom type with configured hex color renders in that color
- [x] Custom type without configured color gets a deterministic ANSI color

---

### EPIC-4: ADO API Integration & Data Population

**Status**: DONE (completed 2026-03-15)

**Goal**: Fetch complete type metadata (state sequences, parent-child relationships) from ADO during `init`/`refresh` and populate the `process_types` table and `DisplayConfig.TypeColors`.

**Prerequisites**: EPIC-2 (SqliteProcessTypeStore must exist), EPIC-3 (TypeColors auto-population target must exist).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-017 | IMPL | Create DTOs: (1) `AdoWorkItemStateColor` with `name`, `color`, `category` properties for deserializing the `states` array in the work item type response. (2) Add `States` property of type `List<AdoWorkItemStateColor>?` to existing `AdoWorkItemTypeResponse`. (3) `AdoProcessConfigurationResponse` (internal sealed, Infrastructure only) with `TaskBacklog`, `RequirementBacklog`, `PortfolioBacklogs`, `BugWorkItems` properties (all of type `AdoCategoryConfiguration`). (4) `AdoCategoryConfiguration` with `Name`, `ReferenceName`, `WorkItemTypes` properties. (5) `AdoWorkItemTypeRef` with `Name`, `Url` properties. (6) `ProcessConfigurationData` and `BacklogLevelConfiguration` domain DTOs in `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs`. Register all new infrastructure types in `TwigJsonContext`. See Section 7 for complete DTO definitions. | `src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs`, `src/Twig.Infrastructure/Ado/Dtos/AdoProcessConfigurationResponse.cs`, `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-018 | IMPL | Add `GetWorkItemTypesWithStatesAsync()` to `IIterationService` and implement in `AdoIterationService`. Calls `GET /{project}/_apis/wit/workitemtypes?api-version=7.1` (same endpoint as `GetWorkItemTypeAppearancesAsync()`; no `$expand` parameter needed — see OQ-5 resolution). For each type, extracts the `states` array and applies the **state ordering algorithm** (Section 7): sort by `stateCategory` rank (Proposed=0 → InProgress=1 → Resolved=2 → Completed=3 → Removed=4, unrecognized=5), then by original array index within the same category. Returns `IReadOnlyList<WorkItemTypeWithStates>` with name, color, icon, and sorted states. Create `WorkItemTypeWithStates` and `WorkItemTypeState` domain DTOs (see Section 7). **Filtering**: filters out disabled types but NOT null-color types (custom types may lack colors but still have valid states). **States validation step**: if any non-disabled type has zero states in the response, log a warning (`"⚠ States not populated in list response"`) and return that type with an empty states list. See OQ-5 resolution. | `src/Twig.Domain/Interfaces/IIterationService.cs`, `src/Twig.Domain/ValueObjects/WorkItemTypeWithStates.cs`, `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-019 | IMPL | Add `GetProcessConfigurationAsync()` to `IIterationService` and implement in `AdoIterationService`. Calls `GET /{project}/_apis/work/processconfiguration?api-version=7.1` (project-scoped, no `{team}` segment). Deserializes into `AdoProcessConfigurationResponse` (internal infrastructure DTO), then maps to `ProcessConfigurationData` (domain DTO) inside `AdoIterationService` — see Section 7 for the mapping code. Returns `ProcessConfigurationData`. **CRITICAL (DD-9)**: The return type MUST be the domain DTO `ProcessConfigurationData`, NOT the infrastructure DTO `AdoProcessConfigurationResponse`. `IIterationService` is in `Twig.Domain` which has no `ProjectReference` to `Twig.Infrastructure` — returning the infrastructure DTO would be a compile-time error. The backlog hierarchy → parent-child inference algorithm (Section 7) is applied in the caller (InitCommand/RefreshCommand) when building `ProcessTypeRecord` objects. | `src/Twig.Domain/Interfaces/IIterationService.cs`, `src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs`, `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-020 | IMPL | Enhance `InitCommand.ExecuteAsync()`: **Connection lifecycle fix** — change `SqliteCacheStore` from create-and-dispose (`InitCommand.cs:205-206`) to `using var cacheStore = ...` so the connection remains open during type-fetching and persistence. After fetching type appearances, call `GetWorkItemTypesWithStatesAsync()` for all types (single HTTP call). Call `GetProcessConfigurationAsync()` once (returns `ProcessConfigurationData` domain DTO). Apply backlog hierarchy inference algorithm (Section 7) to compute parent-child mappings. Build `ProcessTypeRecord` objects and persist via `new SqliteProcessTypeStore(cacheStore).SaveAsync()`. The `using` ensures `cacheStore.Dispose()` after all writes. **Note**: TypeColors auto-population is NOT needed here — `InitCommand` already performs this at `InitCommand.cs:138-141`. See Section 8. | `src/Twig/Commands/InitCommand.cs` | DONE |
| ITEM-021 | IMPL | Enhance `RefreshCommand.ExecuteAsync()`: call `GetWorkItemTypesWithStatesAsync()` (single HTTP call) for all types. Call `GetProcessConfigurationAsync()` once (returns `ProcessConfigurationData` domain DTO). Apply backlog hierarchy inference algorithm. Replace existing manual SQL inserts (`RefreshCommand.cs:136-152`) with `SqliteProcessTypeStore.SaveAsync()` calls using fully populated `ProcessTypeRecord` objects. **Note**: TypeColors auto-population is already implemented (`RefreshCommand.cs:130-133`, `ITEM-157`) — no additional work needed for FR-12. `RefreshCommand` already has injected `cacheStore` (`RefreshCommand.cs:21`) — no connection lifecycle change needed. | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-022 | IMPL | Increment `SqliteCacheStore.SchemaVersion` from 2 to 3. | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | DONE |
| ITEM-023 | TEST | Unit tests for `AdoIterationService`: (1) `GetWorkItemTypesWithStatesAsync` — verify state ordering algorithm: states returned sorted by category rank (Proposed=0→InProgress=1→Resolved=2→Completed=3→Removed=4), within-category ordering preserved from ADO response. Test with interleaved categories (e.g., `[Active(InProgress), Draft(Proposed), Review(InProgress), Done(Completed)]` → `[Draft, Active, Review, Done]`). Test with unrecognized category. Test **states validation step**: verify warning is logged when a non-disabled type has zero states. Verify disabled types are filtered out but null-color types are retained. (2) `GetProcessConfigurationAsync` — verify deserialization and mapping to `ProcessConfigurationData` domain DTO (NOT `AdoProcessConfigurationResponse`). Verify `BacklogLevelConfiguration` fields populated correctly. Test with sample response containing 2 portfolio backlogs + requirement + task levels. (3) Backlog hierarchy inference (tested in caller): test with Agile-like response (Epic→Feature→UserStory→Task), test with custom process with multiple types per level, test with empty portfolioBacklogs, test that bugWorkItems get empty valid_child_types. All tests use mocked HTTP responses. | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | DONE |
| ITEM-024 | TEST | Integration test: `RefreshCommand` populates `process_types` with state data and TypeColors in config. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] After `twig init`, `process_types.states_json` contains actual state arrays (not `'[]'`)
- [x] After `twig init`, `process_types.valid_child_types_json` contains child type arrays from process configuration
- [x] TypeColors auto-population already works for both `init` and `refresh` (FR-12 satisfied by `ITEM-157`)
- [x] API failures for states/process-config are handled gracefully (warning logged, operation continues)
- [x] If states are absent for any type (defensive validation), a warning is logged and types get empty state arrays — system remains functional
- [x] `GetProcessConfigurationAsync()` returns `ProcessConfigurationData` (domain DTO), not the infrastructure DTO
- [x] `GetWorkItemTypesWithStatesAsync()` filters disabled types but retains null-color types

---

### EPIC-5: Command Integration & End-to-End

**Status**: DONE (completed 2026-03-15)

**Goal**: Enable custom type usage in seed creation and ensure state transitions work for custom types with dynamic configuration.

**Prerequisites**: EPIC-1 + EPIC-2 + EPIC-4 (all infrastructure must be in place).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-025 | IMPL | `SeedCommand`: The existing `WorkItemType.Parse()` call on `--type` argument now accepts custom types (no code change needed—`Parse()` was fixed in EPIC-1). Verify behavior. **Scope note**: `twig seed --type CustomType Title` with no parent context works after EPIC-1 alone (type is parsed and stored). However, `twig seed --type CustomType Title` with a custom-type *parent* (where `SeedFactory` validates the child type against `ProcessConfiguration.GetAllowedChildTypes()`) only works correctly after EPIC-2 + EPIC-4, which populate the dynamic config with parent-child relationships. Before those epics, child-type validation for custom-type parents returns an empty list, so the seed would fail with a "type not allowed as child" error. | `src/Twig/Commands/SeedCommand.cs` | DONE |
| ITEM-026 | IMPL | `StateCommand`/`StateShorthand`: Custom types will get `null` from `GetTypeGroup()` which already returns `Result.Fail`. Add a clear error message suggesting `twig update System.State "<state>"` for custom types. | `src/Twig.Domain/ValueObjects/StateShorthand.cs` | DONE |
| ITEM-027 | TEST | End-to-end test: create a work item with custom type "Scenario", verify it round-trips through save/load/display with correct type name, badge, and color. | `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | DONE |
| ITEM-028 | TEST | Test: `StateShorthand.Resolve()` with custom type returns descriptive error message. | `tests/Twig.Domain.Tests/ValueObjects/StateShorthandTests.cs` | DONE |
| ITEM-029 | TEST | Run full test suite, fix any remaining failures. | All test projects | DONE |

**Acceptance Criteria**:
- [x] `twig seed --type "Scenario" "My custom scenario"` creates a work item with type "Scenario"
- [x] `twig state d` with a custom-type active item gives a clear error suggesting `twig update`
- [x] All 827+ tests pass
- [x] `dotnet build` succeeds with no warnings related to changes

---

## References

- [ADO REST API: Work Item Types - List](https://learn.microsoft.com/rest/api/azure/devops/wit/work-item-types/list?view=azure-devops-rest-7.1) — endpoint used for type appearances and state extraction; `WorkItemType` schema includes `states: WorkItemStateColor[]` as a standard property (no `$expand` needed — see OQ-5 resolution); `GetWorkItemTypeExpand` enum is for the separate Process API
- [ADO REST API: Work Item Types - Get](https://learn.microsoft.com/rest/api/azure/devops/wit/work-item-types/get?view=azure-devops-rest-7.1) — includes state information per-type
- [ADO REST API: Process Configuration - Get](https://learn.microsoft.com/rest/api/azure/devops/work/processconfiguration/get?view=azure-devops-rest-7.1) — backlog hierarchy; `CategoryConfiguration` schema has no `rank` field; `portfolioBacklogs` array ordering assumed top-to-bottom per sample response
- [ADO Inherited Process Customization](https://learn.microsoft.com/azure/devops/organizations/settings/work/apply-rules-to-workflow-states?view=azure-devops) — context on custom process templates
- `WorkItemType.cs:38-54` — current hardcoded KnownTypes dictionary
- `AdoResponseMapper.cs:197-204` — current silent Task fallback (now unreachable after EPIC-1)
- `ProcessConfiguration.cs:76-86` — current 4-template ForTemplate switch
- `SqliteCacheStore.cs:125-181` — existing DDL including process_types table (lines 159-167)
- `InitCommand.cs:126-142` — existing TypeAppearances fetch + TypeColors bridge (lines 138-141)
- `InitCommand.cs:205-206` — current create-and-dispose SqliteCacheStore pattern (connection lifecycle fix target)
- `RefreshCommand.cs:125-152` — existing TypeAppearances fetch (lines 125-134) + TypeColors bridge (lines 130-133, `ITEM-157`) + write-only SQL insert (lines 136-152)
- `RefreshCommand.cs:21` — `SqliteCacheStore cacheStore` constructor parameter (injected via DI)
- `TwigConfiguration.cs:153` — `DisplayConfig.TypeColors` declaration (inside `DisplayConfig` class starting at line 148)
- `Program.cs:104-106` — `HumanOutputFormatter` DI registration with TypeColors injection
- `SqliteWorkItemRepository.cs:237-239` — `MapRow()` method (line 237) with `WorkItemType.Parse()` call (line 239); `ReadAll()` starts at line 226
