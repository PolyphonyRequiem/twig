# State Category Consolidation — Solution Design & Implementation Plan

## Executive Summary

Twig currently discards the authoritative state category data returned by Azure DevOps during `init` and `refresh`, then reconstructs categories from hardcoded string-matching heuristics in three independent switch expressions (`FormatterHelpers.GetShorthand`, `HumanOutputFormatter.GetStateColor`, `PromptCommand.GetStateCategory`). This design introduces a `StateCategory` enum, extends `ProcessTypeRecord` and the SQLite schema to persist state-to-category mappings, creates a centralized `StateCategoryResolver` domain service that resolves categories from stored ADO data with a hardcoded fallback, and refactors all three consumer sites to use it. The result is a single source of truth for state categorization that centralizes the three independent heuristics into one resolver and persists ADO-authoritative category data for future use, while maintaining backward compatibility through graceful schema migration and fallback behavior. **Note**: In this plan, the three refactored CLI call sites pass `null` for state entries (they lack access to `ProcessTypeRecord` at their call sites), so they always fall through to `StateCategoryResolver.FallbackCategory()`. This means custom state names (e.g., "Draft", "Review") will still return `StateCategory.Unknown`. Wiring stored entries into formatter call sites is deferred — see DD-009. A secondary enhancement wires ADO per-state colors into a new `StateEntry` value object, enabling true-color state rendering in future formatters.

## Background

### Current Architecture

Azure DevOps returns work item type states with both `Name` and `Category` fields via the Work Item Types REST API. The `AdoIterationService.SortStates()` method (line 237–263 of `AdoIterationService.cs`) correctly captures this data into `WorkItemTypeState` objects with `Name` and `Category` properties. However, both `InitCommand` (line 239) and `RefreshCommand` (line 162) discard the category by extracting only state names:

```csharp
var stateNames = wit.States.Select(s => s.Name).ToList();
```

The `ProcessTypeRecord` aggregate stores states as `IReadOnlyList<string>` (state names only). The SQLite `process_types` table stores a `states_json` column containing a JSON array of strings. No category information is persisted.

Three CLI-layer switch expressions independently guess state categories from state name strings:

| Location | Purpose | Lines |
|----------|---------|-------|
| `FormatterHelpers.GetShorthand()` | Maps state → single-char shorthand (p/c/s/d/x) | `FormatterHelpers.cs:16–24` |
| `HumanOutputFormatter.GetStateColor()` | Maps state → ANSI color | `HumanOutputFormatter.cs:371–384` |
| `PromptCommand.GetStateCategory()` | Maps state → category string for prompt JSON | `PromptCommand.cs:122–136` |

### Why Now

Custom ADO process templates use arbitrary state names (e.g., "Draft", "Review", "Accepted") that don't match any hardcoded strings, causing them to fall through to default/unknown handling. The authoritative category data is already available at the ADO API boundary — persisting it eliminates guesswork entirely.

### Prior Art

- `StateTransitionService` (`StateTransitionService.cs:21`) is a `public static class` providing state transition classification — the proposed `StateCategoryResolver` follows the same pattern.
- `DynamicProcessConfigProvider` (`DynamicProcessConfigProvider.cs`) demonstrates the pattern of merging dynamic ADO data with hardcoded fallbacks.
- `HexToAnsi.ToForeground()` (`HexToAnsi.cs:14`) accepts both `#`-prefixed and raw 6-char hex, establishing the convention for hex color handling.

## Problem Statement

1. **Data loss**: ADO provides per-state category metadata, but `InitCommand` and `RefreshCommand` discard it, saving only state name strings.
2. **Fragile heuristics**: Three independent switch expressions hardcode state-name-to-category guesses. These fail for custom process templates, inherited templates with renamed states, and any state not in the hardcoded list.
3. **Inconsistency risk**: The three switch expressions can drift out of sync with each other, producing different category results for the same state name.
4. **Missing state colors**: ADO returns per-state hex colors (`AdoWorkItemStateColor.Color`), but `SortStates()` drops them at line 260. These could enable true-color state rendering.

## Goals and Non-Goals

### Goals

- **G-1**: Persist ADO state category data in `ProcessTypeRecord` and SQLite.
- **G-2**: Create a single domain service (`StateCategoryResolver`) for category resolution.
- **G-3**: Refactor all three consumer sites to use `StateCategoryResolver`.
- **G-4**: Handle schema migration gracefully (version bump, drop-and-recreate).
- **G-5**: Preserve ADO per-state hex colors in the data model for future use.
- **G-6**: Maintain AOT compatibility (no reflection-based serialization).

### Non-Goals

- **NG-1**: Wiring state colors into `HumanOutputFormatter.GetStateColor()` rendering — deferred to a follow-up. This plan only stores the data.
- **NG-2**: Changing the `PromptCommand` architecture — it intentionally uses raw SQLite for performance.
- **NG-3**: Modifying `StateShorthand.Resolve()` or `StateTransitionService` — these operate on different axes (shorthand resolution and transition classification).
- **NG-4**: Supporting runtime schema migration (ALTER TABLE) — Twig uses drop-and-recreate via `SchemaVersion` bump.

## Requirements

### Functional Requirements

- **FR-001**: A `StateCategory` enum MUST exist in `Twig.Domain.Enums` with values: `Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`.
- **FR-002**: `ProcessTypeRecord.States` MUST change from `IReadOnlyList<string>` to `IReadOnlyList<StateEntry>` where `StateEntry` is a value object containing `Name` (string), `Category` (StateCategory), and `Color` (string?, raw 6-char hex).
- **FR-003**: `InitCommand` and `RefreshCommand` MUST persist state categories and colors from ADO data.
- **FR-004**: `StateCategoryResolver` MUST resolve a state name to `StateCategory` using stored ADO data, falling back to hardcoded heuristics when no stored data exists.
- **FR-005**: `FormatterHelpers.GetShorthand()`, `HumanOutputFormatter.GetStateColor()`, and `PromptCommand.GetStateCategory()` MUST delegate to `StateCategoryResolver`.
- **FR-006**: The SQLite `process_types.states_json` column MUST store state entries as JSON objects (not plain strings).
- **FR-007**: `SqliteCacheStore.SchemaVersion` MUST be bumped from 3 to 4.

### Non-Functional Requirements

- **NFR-001**: All new types MUST be AOT-compatible (no `System.Reflection` usage, source-generated JSON serialization only).
- **NFR-002**: `PromptCommand` MUST continue using raw SQLite queries (no DI-resolved repositories).
- **NFR-003**: `StateCategoryResolver.FallbackCategory()` MUST be a pure function (no I/O, no state).
- **NFR-004**: `PromptCommand` MUST NOT write to stderr.

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│  CLI Layer (Twig)                                                │
│  ┌──────────────┐  ┌──────────────────┐  ┌───────────────────┐  │
│  │ FormatterHelp │  │ HumanOutput      │  │ PromptCommand     │  │
│  │ .GetShorthand │  │ .GetStateColor   │  │ .GetStateCategory │  │
│  └──────┬───────┘  └────────┬─────────┘  └────────┬──────────┘  │
│         │                   │                      │             │
│         └───────────────────┼──────────────────────┘             │
│                             │                                    │
│                    ┌────────▼────────┐                           │
│                    │ StateCategoryRes│ ← Twig.Domain.Services    │
│                    │  .Resolve()     │                           │
│                    │  .FallbackCat() │                           │
│                    └────────┬────────┘                           │
│                             │                                    │
│                    ┌────────▼────────┐                           │
│                    │ StateCategory   │ ← Twig.Domain.Enums       │
│                    │ enum            │                           │
│                    └─────────────────┘                           │
│                                                                  │
│  Data Flow (init/refresh):                                       │
│  ADO API → WorkItemTypeState{Name,Category} → StateEntry{Name,  │
│            Category,Color} → ProcessTypeRecord → SQLite          │
└──────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `StateCategory` Enum (`Twig.Domain.Enums`)

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]
public enum StateCategory
{
    Proposed = 0,
    InProgress = 1,
    Resolved = 2,
    Completed = 3,
    Removed = 4,
    Unknown = 5,
}
```

Ordinal values match `AdoIterationService.CategoryRank()` for consistency. The `JsonStringEnumConverter<StateCategory>` attribute ensures enum values serialize as strings (e.g., `"Proposed"`) rather than integers. This converter is AOT-compatible in .NET 9+ (no reflection required) and enables human-readable JSON in the `states_json` column.

#### 2. `StateEntry` Value Object (`Twig.Domain.ValueObjects`)

```csharp
public readonly record struct StateEntry(
    string Name,
    StateCategory Category,
    string? Color);
```

Declared as `readonly record struct` for zero-allocation inline storage in collections. `Color` is raw 6-char hex (e.g., `"009CCC"`), matching ADO's format and `HexToAnsi.ToForeground()`'s support for raw hex input. This resolves Open Question #3 — raw 6-char hex without `#` prefix, consistent with `WorkItemTypeAppearance.Color` and `ProcessTypeRecord.ColorHex` conventions in the codebase.

#### 3. `StateCategoryResolver` Static Service (`Twig.Domain.Services`)

```csharp
public static class StateCategoryResolver
{
    /// <summary>
    /// Resolves a state name to its category. Uses stored entries when available,
    /// falls back to hardcoded heuristics.
    /// </summary>
    public static StateCategory Resolve(string state, IReadOnlyList<StateEntry>? entries)
    {
        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Name, state, StringComparison.OrdinalIgnoreCase))
                    return entry.Category;
            }
        }
        return FallbackCategory(state);
    }

    /// <summary>
    /// Parses an ADO category string (e.g., "Proposed", "InProgress") to a StateCategory enum value.
    /// Used by InitCommand and RefreshCommand when building StateEntry objects from ADO data.
    /// </summary>
    public static StateCategory ParseCategory(string? category) => category switch
    {
        "Proposed" => StateCategory.Proposed,
        "InProgress" => StateCategory.InProgress,
        "Resolved" => StateCategory.Resolved,
        "Completed" => StateCategory.Completed,
        "Removed" => StateCategory.Removed,
        _ => StateCategory.Unknown,
    };

    /// <summary>
    /// Hardcoded state-to-category mapping used when no ADO data is available.
    /// </summary>
    internal static StateCategory FallbackCategory(string state)
    {
        if (string.IsNullOrEmpty(state))
            return StateCategory.Unknown;

        return state.ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" => StateCategory.Proposed,
            "active" or "doing" or "committed" or "in progress" or "approved" => StateCategory.InProgress,
            "resolved" => StateCategory.Resolved,
            "closed" or "done" => StateCategory.Completed,
            "removed" => StateCategory.Removed,
            _ => StateCategory.Unknown,
        };
    }
}
```

**Visibility decision (DD-007)**: `FallbackCategory()` is declared `internal`. Since `Twig.Domain.csproj`'s `InternalsVisibleTo` list (lines 8–11) does NOT include the `Twig` assembly (only `Twig.Domain.Tests`, `Twig.Infrastructure`, `Twig.Infrastructure.Tests`, `Twig.Cli.Tests`), all CLI-layer callers MUST use `StateCategoryResolver.Resolve(state, null)` to trigger the fallback path rather than calling `FallbackCategory()` directly. This keeps `FallbackCategory` internal, exposes no extra API surface, and `Resolve(state, null)` already delegates to `FallbackCategory` internally. Specifically:

- `FormatterHelpers.GetShorthand()` calls `StateCategoryResolver.Resolve(state, null)` — NOT `FallbackCategory(state)`.
- `HumanOutputFormatter.GetStateColor()` calls `StateCategoryResolver.Resolve(state, null)` — NOT `FallbackCategory(state)`.
- `PromptCommand.GetStateCategory()` calls `StateCategoryResolver.Resolve(state, null)` — NOT `FallbackCategory(state)`.

This pattern is consistent throughout the codebase: `Resolve()` is the single public entry point, and `FallbackCategory()` remains an internal implementation detail accessible only to the domain and its declared friends.

#### 4. `ProcessTypeRecord` Changes

```csharp
public sealed class ProcessTypeRecord
{
    public string TypeName { get; init; } = string.Empty;
    public IReadOnlyList<StateEntry> States { get; init; } = Array.Empty<StateEntry>();
    // ... remaining properties unchanged
}
```

This is a breaking change to the `States` property type. All consumers must be updated.

#### 5. SQLite Schema Changes

`process_types.states_json` changes from `["New","Active","Closed"]` to:

```json
[{"name":"New","category":"Proposed","color":"b2b2b2"},
 {"name":"Active","category":"InProgress","color":"007acc"},
 {"name":"Closed","category":"Completed","color":"339933"}]
```

Property names are camelCase because `TwigJsonContext` uses `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` (line 50 of `TwigJsonContext.cs`). `StateCategory` enum values are serialized as strings (e.g., `"Proposed"`, `"InProgress"`) via `[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]` applied to the enum type — see ITEM-001. This is AOT-compatible in .NET 9+ and preferred over integer serialization for readability and forward compatibility.

`SchemaVersion` bumps from 3 to 4. Existing databases are drop-and-recreated on next `init` or command invocation, which triggers a `twig refresh` to repopulate.

### Data Flow

#### Init/Refresh Persist Flow

```
ADO API
  └─► AdoIterationService.GetWorkItemTypesWithStatesAsync()
        └─► WorkItemTypeWithStates { States: [WorkItemTypeState{Name, Category}] }
              └─► InitCommand / RefreshCommand
                    └─► Build StateEntry[] from WorkItemTypeState[] + AdoWorkItemStateColor.Color
                          └─► ProcessTypeRecord { States: IReadOnlyList<StateEntry> }
                                └─► SqliteProcessTypeStore.SaveAsync()
                                      └─► JSON serialize → states_json column
```

#### Resolution Flow (Read Path)

```
FormatterHelpers.GetShorthand(state)
  └─► StateCategoryResolver.Resolve(state, null)  // no entries available at call site
        └─► FallbackCategory(state)
              └─► StateCategory enum value
                    └─► switch on category → shorthand char

PromptCommand.GetStateCategory(state)
  └─► StateCategoryResolver.Resolve(state, null)  // raw SQLite, no ProcessTypeRecord
        └─► FallbackCategory(state)
              └─► StateCategory.ToString()
```

**Note**: In the current design, `GetShorthand()` and `GetStateColor()` don't have access to `ProcessTypeRecord` entries at their call sites. They call `Resolve(state, null)` which falls through to `FallbackCategory()`. This is intentional — it centralizes the heuristic in one place. **Consequence**: Custom state names (e.g., "Draft", "Review", "Accepted") will return `StateCategory.Unknown` and use default rendering. The data is persisted in SQLite but not yet consumed at these call sites. A future enhancement could pass state entries through `WorkItem` or via a lookup service, but that is out of scope (avoids pre-flattening — see DD-009).

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-001 | `StateCategory` as enum, not strings | Type safety, exhaustive switch matching, AOT-friendly, prevents typos. Matches `TransitionKind` and `ProcessTemplate` enum patterns in `Twig.Domain.Enums`. |
| DD-002 | `StateEntry` as `readonly record struct` | Zero-allocation inline storage in `IReadOnlyList<StateEntry>`. Value semantics are correct — entries are immutable data tuples. Avoids heap allocation per state entry. |
| DD-003 | `StateCategoryResolver` as `public static class` | Consistent with `StateTransitionService` (public static class at `StateTransitionService.cs:21`). Pure functions, no instance state, no DI needed. |
| DD-004 | Schema version bump (3→4), not ALTER TABLE | Matches existing Twig convention (`SqliteCacheStore.EnsureSchema()` drops and recreates all tables on version mismatch). Simpler than migration scripts. |
| DD-005 | `Color` on `StateEntry` as `string?` (raw 6-char hex) | Matches ADO's format and existing conventions (`ProcessTypeRecord.ColorHex`, `WorkItemTypeAppearance.Color`). `HexToAnsi.ToForeground()` accepts both `#`-prefixed and raw 6-char hex. |
| DD-006 | `PromptCommand` keeps raw SQLite | Performance requirement (NFR-002). Reads `states_json` column directly and deserializes in-process. No DI-resolved repositories. |
| DD-007 | `FallbackCategory()` is `internal`, callers use `Resolve(state, null)` | Avoids exposing implementation details across assembly boundaries. `Twig.Domain.csproj` `InternalsVisibleTo` does NOT include `Twig`. `Resolve(state, null)` delegates to `FallbackCategory` internally — architecturally cleanest option. |
| DD-008 | Preserve `WorkItemTypeState.Color` in ADO→domain mapping | `AdoWorkItemStateColor` has a `Color` property (confirmed). `SortStates()` currently drops it at line 260. Extending `WorkItemTypeState` to carry `Color` enables the full pipeline. |
| DD-009 | Formatters call `Resolve(state, null)` — no pre-flattened lookup table | Avoids coupling formatters to `ProcessTypeRecord` or requiring a shared lookup cache. `Resolve(state, null)` always falls back to `FallbackCategory()`, which is the same heuristic used today but centralized. **This means custom/non-standard state names will return `StateCategory.Unknown` and use default rendering — fixing this requires a future enhancement to pass state entries through `WorkItem` or via a lookup service.** Future enhancement can add entries without changing caller signatures. This avoids pre-flattening state data into a global dictionary at startup. |
| DD-010 | `ParseCategory()` lives on `StateCategoryResolver`, not in CLI commands | ADO category string → `StateCategory` mapping is domain logic. Placing it in `StateCategoryResolver` (alongside `FallbackCategory`) avoids duplicating the same mapping switch expression in both `InitCommand` and `RefreshCommand`. Both commands call `StateCategoryResolver.ParseCategory(s.Category)`. |

### Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Add `InternalsVisibleTo` for `Twig` in `Twig.Domain.csproj` | Simpler call sites (`FallbackCategory()` directly) | Widens assembly coupling, sets precedent for CLI→Domain internal access | Rejected — `Resolve(state, null)` achieves same result without coupling |
| Make `FallbackCategory()` public | Simplest change | Exposes implementation detail; two public APIs for same operation creates confusion | Rejected — one entry point (`Resolve`) is cleaner |
| Store categories in a separate SQLite table | Normalized schema | Over-engineering for a small dataset; adds join complexity | Rejected — JSON array in existing column is sufficient |
| Use `Dictionary<string, StateCategory>` in `ProcessTypeRecord` instead of `StateEntry` | Direct lookup, O(1) | Loses state ordering (important for transition rules); loses color data | Rejected — `StateEntry` preserves all data |
| ALTER TABLE migration instead of schema version bump | Preserves existing cached data | Inconsistent with Twig's convention; complex for SQLite; data gets refreshed anyway | Rejected — convention is drop-and-recreate |
| `ParseCategory()` as private helper in InitCommand/RefreshCommand | No changes to domain layer | Duplicates ADO category mapping logic in two CLI commands; mapping is domain logic | Rejected — `StateCategoryResolver.ParseCategory()` centralizes category mapping alongside `FallbackCategory()` |
| Integer enum serialization (no `JsonStringEnumConverter`) | Slightly smaller JSON; default `System.Text.Json` behavior | Human-unreadable `states_json` values (`0`, `1`, `2`); complicates debugging and manual inspection of SQLite cache | Rejected — `JsonStringEnumConverter<StateCategory>` is AOT-compatible in .NET 9+ and produces readable JSON |

## Dependencies

### Internal Dependencies

- `Twig.Domain.Enums` — new `StateCategory` enum
- `Twig.Domain.ValueObjects` — new `StateEntry` struct
- `Twig.Domain.Services` — new `StateCategoryResolver` class
- `Twig.Domain.Aggregates.ProcessTypeRecord` — breaking change to `States` type
- `Twig.Infrastructure.Persistence.SqliteProcessTypeStore` — serialization format change
- `Twig.Infrastructure.Serialization.TwigJsonContext` — new `[JsonSerializable]` attributes
- `Twig.Infrastructure.Ado.AdoIterationService` — extend `WorkItemTypeState` with `Color`

### External Dependencies

- None — all changes use existing libraries (System.Text.Json, contoso.Data.Sqlite)

### Sequencing Constraints

- EPIC-1 (domain model) must complete before EPIC-2 (infrastructure) and EPIC-3 (CLI refactor)
- EPIC-2 must complete before EPIC-3 (CLI needs serialization support)

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `ProcessTypeRecord` | Breaking change: `States` type changes from `IReadOnlyList<string>` to `IReadOnlyList<StateEntry>` |
| `SqliteProcessTypeStore` | Serialization/deserialization changes for `states_json` |
| `SqliteCacheStore` | `SchemaVersion` bump (3→4) triggers schema rebuild |
| `InitCommand` | Must build `StateEntry` objects instead of extracting name strings |
| `RefreshCommand` | Same as `InitCommand` |
| `FormatterHelpers` | `GetShorthand()` switches on `StateCategory` instead of string matching |
| `HumanOutputFormatter` | `GetStateColor()` switches on `StateCategory` instead of string matching |
| `PromptCommand` | `GetStateCategory()` delegates to `StateCategoryResolver` |
| `TwigJsonContext` | New `[JsonSerializable]` attributes for `StateEntry` and `List<StateEntry>` |
| `WorkItemTypeState` | New `Color` property |
| `AdoIterationService.SortStates()` | Preserves `Color` from `AdoWorkItemStateColor` |

### Backward Compatibility

- **Schema version bump**: Existing databases (SchemaVersion=3) will be dropped and recreated on first command invocation. Users must run `twig refresh` to repopulate data. This is the established pattern.
- **`ProcessTypeRecord.States` type change**: All compile-time consumers of `IReadOnlyList<string>` will get compile errors, ensuring no silent breakage. Specifically:
  - `ProcessConfiguration.ForDynamic()` reads `record.States` — must be updated.
  - `TypeConfig.States` in `ProcessConfigurationForDynamicTests` (lines 78, 98) is `string[]` and remains unaffected — it tests the `ProcessConfiguration` output, not `ProcessTypeRecord` input. These assertions will continue to compile and pass unchanged.
  - `DynamicProcessConfigProviderTests.MakeRecord()` (line 12) constructs `ProcessTypeRecord` with `States = states` where `states` is `params string[]` — must be updated to `params StateEntry[]` or equivalent.
  - `ProcessTypeRecordTests.cs` contains 6 `ProcessTypeRecord` constructions with string `States` assignments (lines 30, 67, 90, 109, 127, 145) plus the `ProcessConfigurationForDynamicTests` class (lines 46–168) — all must be updated. Note: `ProcessConfigurationTests.cs` does NOT construct `ProcessTypeRecord` objects and is unaffected.

### Performance

- No measurable impact. `StateCategoryResolver.Resolve()` is O(n) over state entries (typically 3–5 states per type), same order as the current switch expressions.
- `PromptCommand` adds one `JsonSerializer.Deserialize` call for `states_json` — negligible for a column that's typically <200 bytes.

## Risks and Mitigations

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|------------|--------|------------|
| RISK-001 | Schema bump causes data loss for users who don't refresh | Low | Medium | Schema rebuild is the existing pattern. `SchemaWasRebuilt` flag could trigger a hint to run `twig refresh`. |
| RISK-002 | AOT compatibility regression from new JSON types | Low | High | Add `[JsonSerializable(typeof(StateEntry))]` and `[JsonSerializable(typeof(List<StateEntry>))]` to `TwigJsonContext`. Verify with `dotnet publish -r win-x64` AOT build. |
| RISK-003 | `ProcessTypeRecord.States` type change breaks test helpers | Medium | Low | Update `DynamicProcessConfigProviderTests.MakeRecord()`, `ProcessTypeRecordTests.cs` (6 constructions at lines 30, 67, 90, 109, 127, 145), and the `ProcessConfigurationForDynamicTests` class within the same file. Compiler will catch all breakages. |
| RISK-004 | ADO returns unexpected/null category values | Low | Low | `StateCategory.Unknown` handles any unrecognized category. `FallbackCategory()` provides reasonable defaults. |
| RISK-005 | `HumanOutputFormatter` in DI resolution chain when `.twig/` doesn't exist | Low | Medium | `Program.cs` registers `SqliteCacheStore` as a lazy singleton (lines 51–57) that throws `InvalidOperationException` when `.twig/` doesn't exist. If `HumanOutputFormatter` were to depend on `StateCategoryResolver` with state entries loaded from the store, it would be in the resolution chain for `InitCommand`. **Mitigation**: `HumanOutputFormatter.GetStateColor()` calls `StateCategoryResolver.Resolve(state, null)` — no store dependency. The `null` entries parameter ensures `FallbackCategory()` is always used in the formatter, avoiding any DI chain risk. |

## Open Questions

| # | Question | Status | Resolution |
|---|----------|--------|------------|
| OQ-1 | Should `StateCategoryResolver` be injected via DI or remain static? | **Resolved** | Static — consistent with `StateTransitionService`, pure functions, no instance state needed. |
| OQ-2 | Should `ProcessTypeRecord.States` use `IReadOnlyList<StateEntry>` or `IReadOnlyDictionary<string, StateCategory>`? | **Resolved** | `IReadOnlyList<StateEntry>` — preserves ordering (needed for transition rules) and carries color data. |
| OQ-3 | What hex format should `StateEntry.Color` use? | **Resolved** | Raw 6-char hex without `#` prefix (e.g., `"009CCC"`), matching ADO's format, `ProcessTypeRecord.ColorHex`, `WorkItemTypeAppearance.Color`, and `HexToAnsi.ToForeground()`'s support for raw hex input. |

## Implementation Phases

### Phase 1: Domain Model (EPIC-1)
**Exit criteria**: `StateCategory` enum, `StateEntry` struct, and `StateCategoryResolver` exist with full test coverage. `ProcessTypeRecord.States` type is changed.

### Phase 2: Infrastructure (EPIC-2)
**Exit criteria**: SQLite schema updated, `SqliteProcessTypeStore` serializes/deserializes `StateEntry` objects, `InitCommand` and `RefreshCommand` persist full state data.

### Phase 3: CLI Refactor (EPIC-3)
**Exit criteria**: All three consumer sites delegate to `StateCategoryResolver`. All existing formatter tests pass.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Enums/StateCategory.cs` | `StateCategory` enum (Proposed, InProgress, Resolved, Completed, Removed, Unknown) |
| `src/Twig.Domain/ValueObjects/StateEntry.cs` | `StateEntry` readonly record struct (Name, Category, Color) |
| `src/Twig.Domain/Services/StateCategoryResolver.cs` | Static service for state-to-category resolution with fallback |
| `tests/Twig.Domain.Tests/Services/StateCategoryResolverTests.cs` | Unit tests for resolver and fallback logic |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` | `States` type: `IReadOnlyList<string>` → `IReadOnlyList<StateEntry>` |
| `src/Twig.Domain/ValueObjects/WorkItemTypeWithStates.cs` | Add `Color` property to `WorkItemTypeState` class (string?) |
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | `ForDynamic()` extracts state names from `StateEntry` objects |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | `SchemaVersion` 3 → 4 |
| `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` | Serialize/deserialize `StateEntry` list instead of string list |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for `StateEntry`, `List<StateEntry>` |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | `SortStates()` preserves `Color` from `AdoWorkItemStateColor` |
| `src/Twig/Commands/InitCommand.cs` | Build `StateEntry` objects instead of extracting name strings |
| `src/Twig/Commands/RefreshCommand.cs` | Same as InitCommand |
| `src/Twig/Formatters/FormatterHelpers.cs` | `GetShorthand()` uses `StateCategoryResolver.Resolve(state, null)` |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | `GetStateColor()` uses `StateCategoryResolver.Resolve(state, null)` |
| `src/Twig/Commands/PromptCommand.cs` | `GetStateCategory()` uses `StateCategoryResolver.Resolve(state, null)` |
| `tests/Twig.Domain.Tests/Services/DynamicProcessConfigProviderTests.cs` | `MakeRecord()` helper updated for `StateEntry` |
| `tests/Twig.Domain.Tests/Aggregates/ProcessTypeRecordTests.cs` | All `ProcessTypeRecord` constructions with string `States` updated to `StateEntry`: lines 30, 67, 90, 109, 127, 145. Also affects `ProcessConfigurationForDynamicTests` class (lines 46–168) which is in the same file. |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

## Implementation Plan

### EPIC-1: Domain Model — StateCategory, StateEntry, StateCategoryResolver

**Goal**: Introduce the domain types and centralized resolver.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `StateCategory` enum in `Twig.Domain.Enums` with values: `Proposed=0`, `InProgress=1`, `Resolved=2`, `Completed=3`, `Removed=4`, `Unknown=5`. Ordinals match `AdoIterationService.CategoryRank()`. Apply `[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]` to the enum to ensure AOT-compatible string serialization in JSON (requires `using System.Text.Json.Serialization;`). | `src/Twig.Domain/Enums/StateCategory.cs` | DONE |
| ITEM-002 | IMPL | Create `StateEntry` readonly record struct in `Twig.Domain.ValueObjects` with properties: `string Name`, `StateCategory Category`, `string? Color`. Color is raw 6-char hex without `#` prefix. | `src/Twig.Domain/ValueObjects/StateEntry.cs` | DONE |
| ITEM-003 | IMPL | Create `StateCategoryResolver` public static class in `Twig.Domain.Services` with: (a) `public static StateCategory Resolve(string state, IReadOnlyList<StateEntry>? entries)` — iterates entries for name match, falls back to `FallbackCategory()`. (b) `internal static StateCategory FallbackCategory(string state)` — hardcoded switch expression matching the union of all three existing switch expressions. (c) `public static StateCategory ParseCategory(string? category)` — maps ADO category strings ("Proposed", "InProgress", etc.) to `StateCategory` enum values; used by InitCommand/RefreshCommand when building `StateEntry` objects (see DD-010). | `src/Twig.Domain/Services/StateCategoryResolver.cs` | DONE |
| ITEM-004 | TEST | Create `StateCategoryResolverTests` covering: (a) `Resolve()` with matching entry returns entry's category. (b) `Resolve()` with non-matching entries falls back to `FallbackCategory()`. (c) `Resolve()` with null entries falls back. (d) `FallbackCategory()` for all known state names (new, to do, proposed, active, doing, committed, in progress, approved, resolved, closed, done, removed). (e) `FallbackCategory()` for unknown/empty/null returns `Unknown`. (f) `ParseCategory()` for all ADO category strings ("Proposed", "InProgress", "Resolved", "Completed", "Removed") and null/unknown returns `Unknown`. | `tests/Twig.Domain.Tests/Services/StateCategoryResolverTests.cs` | DONE |
| ITEM-005 | IMPL | Change `ProcessTypeRecord.States` from `IReadOnlyList<string>` to `IReadOnlyList<StateEntry>`. Update the `Array.Empty<string>()` default to `Array.Empty<StateEntry>()`. | `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` | DONE |
| ITEM-006 | IMPL | Update `ProcessConfiguration.ForDynamic()` (line 225 of `ProcessConfiguration.cs`) to extract state names from `StateEntry` objects: change `record.States.ToArray()` to `record.States.Select(s => s.Name).ToArray()`. | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | DONE |

**Acceptance Criteria**:
- [x] `StateCategory` enum compiles with 6 values and `JsonStringEnumConverter<StateCategory>` attribute
- [x] `StateEntry` is a readonly record struct with 3 properties
- [x] `StateCategoryResolver.Resolve()` returns correct category from entries
- [x] `StateCategoryResolver.FallbackCategory()` matches union of existing heuristics
- [x] `StateCategoryResolver.ParseCategory()` maps all ADO category strings correctly
- [x] `ProcessTypeRecord.States` is `IReadOnlyList<StateEntry>`
- [x] All domain tests pass

---

### EPIC-2: Infrastructure — Schema, Serialization, ADO Pipeline ✅ DONE

**Goal**: Persist state categories and colors through the full ADO→SQLite pipeline.

**Completed**: 2026-03-15. SchemaVersion bumped to 4; `TwigJsonContext` registers `StateEntry`, `List<StateEntry>`, and `StateCategory`; `SqliteProcessTypeStore` serializes/deserializes `StateEntry` JSON (AOT-compatible, graceful corrupt-data handling); `WorkItemTypeState.Color` flows through `SortStates()`; `InitCommand` and `RefreshCommand` build `StateEntry` via `StateCategoryResolver.ParseCategory()`; all test constructions updated; integration tests verify full round-trip.

**Prerequisites**: EPIC-1 complete.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-007 | IMPL | Bump `SqliteCacheStore.SchemaVersion` from `3` to `4` (line 15 of `SqliteCacheStore.cs`). No DDL changes needed — `states_json` column remains TEXT, only the JSON format changes. | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | DONE |
| ITEM-008 | IMPL | Add `[JsonSerializable(typeof(StateEntry))]`, `[JsonSerializable(typeof(List<StateEntry>))]`, and `[JsonSerializable(typeof(StateCategory))]` to `TwigJsonContext`. Import `Twig.Domain.ValueObjects` and `Twig.Domain.Enums` namespaces. Note: `StateCategory` string serialization is handled by the `[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]` attribute on the enum type itself (ITEM-001), so no additional converter configuration is needed in `TwigJsonContext`. The existing `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` policy applies automatically to `StateEntry` properties (`name`, `category`, `color`). | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-009 | IMPL | Update `SqliteProcessTypeStore.SaveAsync()` to serialize `record.States` as `List<StateEntry>` using `TwigJsonContext.Default.ListStateEntry`. Change the existing `statesJson` variable (line 61–63 of `SqliteProcessTypeStore.cs`) from `JsonSerializer.Serialize(record.States.ToList(), TwigJsonContext.Default.ListString)` to `JsonSerializer.Serialize(record.States.ToList(), TwigJsonContext.Default.ListStateEntry)`. | `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` | DONE |
| ITEM-009a | IMPL | Update `SqliteProcessTypeStore.MapRow()` to deserialize `states_json` as `List<StateEntry>` instead of `List<string>`. Replace `DeserializeList(statesJson)` call (line 97) with `DeserializeStateEntries(statesJson)`. Add new `DeserializeStateEntries()` method using `TwigJsonContext.Default.ListStateEntry`. | `src/Twig.Infrastructure/Persistence/SqliteProcessTypeStore.cs` | DONE |
| ITEM-010 | IMPL | Add `Color` property to `WorkItemTypeState` (line 24 of `WorkItemTypeWithStates.cs`): `public string? Color { get; init; }`. | `src/Twig.Domain/ValueObjects/WorkItemTypeWithStates.cs` | DONE |
| ITEM-010a | IMPL | Update `AdoIterationService.SortStates()` to preserve `Color` from `AdoWorkItemStateColor`. In the `.Select()` projection (lines 257–261), add `Color = x.state.Color` to the `WorkItemTypeState` initializer. The existing `AdoWorkItemStateColor.Color` property is confirmed at line 66 of `AdoWorkItemTypeResponse.cs`. | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-010b | IMPL | Update `InitCommand` (line 239) to build `StateEntry` objects: change `wit.States.Select(s => s.Name).ToList()` to `wit.States.Select(s => new StateEntry(s.Name, StateCategoryResolver.ParseCategory(s.Category), s.Color)).ToList()`. Add `using Twig.Domain.Services;` and `using Twig.Domain.ValueObjects;`. Assign to `ProcessTypeRecord.States`. No local `ParseCategory` helper needed — uses `StateCategoryResolver.ParseCategory()` (see DD-010). | `src/Twig/Commands/InitCommand.cs` | DONE |
| ITEM-010c | IMPL | Apply the same `StateEntry` construction change to `RefreshCommand` (line 162). Uses `StateCategoryResolver.ParseCategory()` — same as InitCommand, no duplication. | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-010d | IMPL | Update `DynamicProcessConfigProviderTests.MakeRecord()` helper (line 12 of `DynamicProcessConfigProviderTests.cs`) to accept `StateEntry` objects. Change `params string[] states` to construct `StateEntry` objects with `StateCategory.Unknown` default (or add a convenience overload that takes state names and wraps them). Update call sites at lines 29 and any others that construct `ProcessTypeRecord` with string states. | `tests/Twig.Domain.Tests/Services/DynamicProcessConfigProviderTests.cs` | DONE |
| ITEM-010e | TEST | Update all `ProcessTypeRecord` constructions in `ProcessTypeRecordTests.cs` (which also contains `ProcessConfigurationForDynamicTests` class, lines 46–168). Change string `States` assignments to `StateEntry` arrays at: line 30 (`["New","Active","Done"]`), line 67 (`["Draft","Active","Done"]`), line 90 (`["New","InReview","Fixed"]`), line 109 (`Array.Empty<string>()`→`Array.Empty<StateEntry>()`), line 127 (`["New","Done"]`), line 145 (`["Draft","Active","Closed","Removed"]`). Update `States.ShouldBe(...)` assertions at lines 38, 78, 98 to compare state names (e.g., `record.States.Select(s => s.Name).ShouldBe(...)`). Note: `ProcessConfigurationTests.cs` does NOT need changes — it tests `ProcessConfiguration.ForTemplate()` which uses hardcoded `string[]` states, not `ProcessTypeRecord`. | `tests/Twig.Domain.Tests/Aggregates/ProcessTypeRecordTests.cs` | DONE |
| ITEM-011 | TEST | Add integration test verifying `SqliteProcessTypeStore` round-trips `StateEntry` data: save a `ProcessTypeRecord` with `StateEntry` states including categories and colors, read it back, assert all fields match. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteProcessTypeStoreTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `SchemaVersion` is 4
- [x] `TwigJsonContext` includes `StateEntry` serializable types
- [x] `SqliteProcessTypeStore` serializes/deserializes `StateEntry` JSON with camelCase property names and string enum values
- [x] `WorkItemTypeState.Color` is populated from ADO data
- [x] `InitCommand` and `RefreshCommand` build `StateEntry` objects with category and color via `StateCategoryResolver.ParseCategory()`
- [x] All `ProcessTypeRecord` constructions in `ProcessTypeRecordTests.cs` updated for `StateEntry`
- [x] Round-trip test passes
- [x] AOT build succeeds

---

### EPIC-3: CLI Refactor — Centralize Category Resolution ✅ DONE

**Goal**: Replace the three hardcoded switch expressions with `StateCategoryResolver` calls.

**Prerequisites**: EPIC-1 complete. (EPIC-2 is not strictly required — `Resolve(state, null)` works without stored data.)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Refactor `FormatterHelpers.GetShorthand()` (lines 16–24 of `FormatterHelpers.cs`) to: (1) call `StateCategoryResolver.Resolve(state, null)`, (2) switch on `StateCategory` enum values instead of string patterns. Map: `Proposed→"p"`, `InProgress→"c"`, `Resolved→"s"`, `Completed→"d"`, `Removed→"x"`, `Unknown→state[..1].ToLowerInvariant()`. Add `using Twig.Domain.Enums;` and `using Twig.Domain.Services;`. Call sites: `MinimalOutputFormatter` lines 33, 39, 50, 86, 129; `HumanOutputFormatter` lines 72, 80, 96. All call sites pass state name strings — no signature change needed. | `src/Twig/Formatters/FormatterHelpers.cs` | DONE |
| ITEM-013 | IMPL | Refactor `HumanOutputFormatter.GetStateColor()` (lines 371–384 of `HumanOutputFormatter.cs`) to: (1) call `StateCategoryResolver.Resolve(state, null)`, (2) switch on `StateCategory` enum values. Map: `Completed→Green`, `Resolved→Green`, `InProgress→Blue`, `Removed→Red`, `Proposed→Dim`, `Unknown→Reset`. Add `using Twig.Domain.Enums;` and `using Twig.Domain.Services;`. Call sites: lines 47, 81, 97, 127, 145, 223. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-014 | IMPL | Refactor `PromptCommand.GetStateCategory()` (lines 122–136 of `PromptCommand.cs`) to: (1) call `StateCategoryResolver.Resolve(state, null)`, (2) return `category.ToString()`. The method becomes a thin wrapper: `return StateCategoryResolver.Resolve(state, null).ToString();`. Handles empty/null via `Resolve()` returning `Unknown`. Add `using Twig.Domain.Services;`. The `PromptData.StateCategory` field remains `string` for JSON serialization compatibility. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-015 | TEST | Created `FormatterHelpersTests` covering `GetShorthand()` for all known state names (Proposed→p, InProgress→c, Resolved→s, Completed→d, Removed→x), custom/unknown states returning first char, empty string returning "?", and case-insensitivity via `StateCategoryResolver`. | `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | DONE |
| ITEM-016 | TEST | Added tests to `HumanOutputFormatterTests` verifying: unknown/custom state (e.g., "Draft") returns Reset color; empty state returns Dim; all standard categories map to their expected ANSI colors via `StateCategoryResolver`. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-017 | TEST | Existing `StateCategoryTests` in `PromptCommandTests.cs` verified passing unchanged — delegation via `StateCategoryResolver.Resolve()` preserves all category mappings. Added `ParseCategory_LowercaseInProgress_ReturnsUnknown` to `StateCategoryResolverTests` to document case-sensitivity contract. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs`, `tests/Twig.Domain.Tests/Services/StateCategoryResolverTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `FormatterHelpers.GetShorthand()` uses `StateCategoryResolver` — no hardcoded state strings
- [x] `HumanOutputFormatter.GetStateColor()` uses `StateCategoryResolver` — no hardcoded state strings
- [x] `PromptCommand.GetStateCategory()` uses `StateCategoryResolver` — no hardcoded state strings
- [x] All existing formatter tests pass unchanged (behavioral equivalence)
- [x] `MinimalOutputFormatter` call sites (lines 33, 39, 50, 86, 129) unaffected — they call `GetShorthand()` by name, signature unchanged
- [x] `HumanOutputFormatter` call sites (GetShorthand: 72, 80, 96; GetStateColor: 47, 81, 97, 127, 145, 223) unaffected

## References

- [ADO Work Item Types REST API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types/list) — returns states with `category` field
- `AdoIterationService.SortStates()` — `src/Twig.Infrastructure/Ado/AdoIterationService.cs:237–263`
- `StateTransitionService` — `src/Twig.Domain/Services/StateTransitionService.cs:21` (pattern reference)
- `SqliteCacheStore.SchemaVersion` — `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs:15`
- `DynamicProcessConfigProvider` — `src/Twig.Domain/Services/DynamicProcessConfigProvider.cs` (merge pattern reference)
- Existing plan docs: `docs/projects/twig-dynamic-process.plan.md`, `docs/projects/twig-ado-type-colors.plan.md`
