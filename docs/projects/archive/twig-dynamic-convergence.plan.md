---
goal: Retire all hardcoded process template assumptions and converge on dynamic ADO-sourced configuration
version: 1.1
date_created: 2026-03-15
last_updated: 2026-03-16
owner: Twig CLI Team
tags: [architecture, migration, refactoring]
revision_notes: "v1.1 — Address technical review feedback (score 86/100): (1) Resolve InitCommand/IIterationService design gap by replacing DetectProcessTemplateAsync() with DetectTemplateNameAsync() returning Task<string?> on the interface, preserving testability; (2) Add explicit constructor update task for ProcessConfiguration (two-arg → single-arg); (3) Fix method name inconsistency G-2 ForRecords → FromRecords; (4) Add AOT compliance guidance for PromptCommand DeserializeStateEntries using TwigJsonContext; (5) Correct description of InitCommand — it does NOT store process_template in context store."
---

# Twig Dynamic Convergence — Retire Hardcoded Process Templates

## Executive Summary

This plan removes all hardcoded Azure DevOps process template assumptions from the Twig CLI codebase. Prior work (`twig-dynamic-process.plan.md`) opened the type system and added dynamic configuration as an overlay on top of hardcoded fallbacks. This plan completes that transition by **deleting** the hardcoded artifacts entirely: the `ProcessTemplate` enum, the four hardcoded template builders (`BuildBasic`, `BuildAgile`, `BuildScrum`, `BuildCmmi`), the heuristic template detection, the hardcoded state shorthand mappings, the hardcoded type color switch, and the `KnownTypes`/`IsStandard` infrastructure. After this change, all process configuration flows exclusively through `ProcessTypeRecord` data stored in `process_types` SQLite table, populated during `twig init` and `twig refresh` from live ADO API responses.

## Background

### Current Architecture

The Twig CLI currently has two parallel paths for process configuration:

1. **Hardcoded path** (`ProcessConfiguration.ForTemplate()`): Four private builders (`BuildBasic`, `BuildAgile`, `BuildScrum`, `BuildCmmi`) each define state sequences, parent-child hierarchies, and transition rules for the standard ADO process templates. This path is selected by `DetectProcessTemplateAsync()`, which uses a heuristic (sniffing for "User Story" → Agile, "Product Backlog Item" → Scrum, etc.) to guess which template is in use.

2. **Dynamic path** (`ProcessConfiguration.ForDynamic()`): Reads `ProcessTypeRecord` objects from the `process_types` SQLite table (populated during `twig init`/`twig refresh`) and builds configuration dynamically. Currently this path **merges** dynamic data on top of the hardcoded base — `ForDynamic()` calls `ForTemplate()` first, then overlays.

The `DynamicProcessConfigProvider` (registered in DI since `twig-dynamic-process`) attempts dynamic first but falls back to hardcoded if the `process_types` table is empty. Several other components still depend directly on the `ProcessTemplate` enum:

- `StateShorthand.Resolve()` requires `(char, ProcessTemplate, WorkItemType)` to look up state names from a ~40-entry hardcoded dictionary.
- `StateCommand` and `SeedCommand` parse a `process_template` string from the context store and fall back to `ProcessTemplate.Agile`.
- `HumanOutputFormatter.GetTypeColor()` has a hardcoded switch mapping 13 type names to ANSI colors before checking ADO-sourced colors.
- `WorkItemType.KnownTypes` dictionary and `IsStandard` property exist for case normalization and to gate shorthand access.

### Prior Art

| Plan | Status | What it did |
|------|--------|-------------|
| `twig-dynamic-process.plan.md` | ✅ Complete | Opened type system, added `ForDynamic()`, `ProcessTypeRecord`, `IProcessTypeStore` |
| `twig-state-category-cleanup.plan.md` | ✅ Complete | Added `StateCategory` enum, `StateEntry` with categories, `StateCategoryResolver` |

Both are prerequisites for this plan and are marked complete.

## Problem Statement

1. **Dual-path maintenance burden**: Every process template change requires updating both hardcoded builders AND dynamic logic. Custom/inherited ADO templates that don't match the 4 standard names silently get Agile-defaulted behavior.

2. **Fragile heuristic detection**: `DetectProcessTemplateAsync()` guesses templates by sniffing type names. Custom processes with "User Story" but non-Agile states get miscategorized. The heuristic is fundamentally unreliable for non-standard templates.

3. **Closed shorthand system**: `StateShorthand.Mappings` hardcodes ~40 entries for 4 templates × N type groups. Custom processes and custom types get no shorthand support at all — users see "State shorthand codes are not supported for custom type 'X'."

4. **Dead code**: `HardCodedProcessConfigProvider` is no longer registered in DI but still exists. `WorkItemType.KnownTypes` and `IsStandard` are only used by `StateShorthand` (gating) and `Parse()` (case normalization) — both uses become unnecessary.

5. **Hardcoded type colors**: The switch in `GetTypeColor()` maps 13 type names to ANSI escape codes. This ignores ADO-sourced colors for standard types, creating inconsistency with the ADO web UI.

## Goals and Non-Goals

### Goals

- **G-1**: Remove the `ProcessTemplate` enum and all code that branches on its values.
- **G-2**: Make `ProcessConfiguration.ForDynamic()` (renamed to `FromRecords()`) the sole construction path, with no fallback to hardcoded builders.
- **G-3**: Replace the hardcoded `StateShorthand.Mappings` dictionary with a dynamic resolver that maps shorthand chars to `StateCategory` values, then finds the first matching state in the type's `ProcessTypeRecord.States` list.
- **G-4**: Replace `DetectProcessTemplateAsync()` on `IIterationService` with `DetectTemplateNameAsync()` returning `Task<string?>`. This preserves testability (InitCommand's test constructor injects `IIterationService` mock) while changing the return type from `ProcessTemplate` enum to a display-only string.
- **G-5**: Remove `HardCodedProcessConfigProvider` class entirely.
- **G-6**: Remove the hardcoded type color switch from `HumanOutputFormatter.GetTypeColor()`, relying on ADO-sourced hex colors with deterministic hash fallback.
- **G-7**: Remove `WorkItemType.KnownTypes` dictionary and `IsStandard` property. Retain the 13 static constants for ergonomic use in tests and formatting.
- **G-8**: Ensure `twig init` gracefully handles the "no process_types data" scenario, and all commands that require process config fail with a clear "run twig init first" message.
- **G-9**: Update all tests to assert dynamic behavior instead of hardcoded template expectations.
- **G-10**: Maintain AOT compatibility (.NET 9 trimming), backward compatibility for existing `.twig/config` files, and PromptCommand raw SQLite perf constraints.

### Non-Goals

- **NG-1**: Changing the SQLite schema or `process_types` table structure (already sufficient).
- **NG-2**: Adding new ADO API calls — all needed data is already fetched during init/refresh.
- **NG-3**: Changing the user-facing CLI command interface or argument structure.
- **NG-4**: Modifying the `StateCategoryResolver` fallback heuristics (they remain useful for edge cases where `StateEntry` data is unavailable).
- **NG-5**: Removing `IconSet` hardcoded icon mappings (out of scope — icons are orthogonal to process template assumptions).
- **NG-6**: Version updates or changelog entries.

## Requirements

### Functional Requirements

- **FR-1**: `StateShorthand.Resolve()` MUST accept a `ProcessTypeRecord` (or its `States` list) instead of `ProcessTemplate` and resolve shorthand chars via `StateCategory` mapping: `p`→Proposed, `c`→InProgress, `s`→Resolved, `d`→Completed, `x`→Removed. It MUST return the first state entry matching that category, or fail if no match exists.
- **FR-2**: `ProcessConfiguration` MUST be constructable solely from `IReadOnlyList<ProcessTypeRecord>` without a `ProcessTemplate` parameter. The `Template` property MUST be removed.
- **FR-3**: `IProcessConfigurationProvider.GetConfiguration()` MUST NOT require a `ProcessTemplate` parameter. It MUST read from `IProcessTypeStore` and fail clearly if the store is empty.
- **FR-4**: `SeedCommand` and `StateCommand` MUST NOT parse `process_template` from the context store or default to Agile. They MUST obtain process configuration through `IProcessConfigurationProvider.GetConfiguration()`.
- **FR-5**: `IIterationService` MUST replace `DetectProcessTemplateAsync()` (returning `ProcessTemplate`) with `DetectTemplateNameAsync()` (returning `Task<string?>`). The method MUST remain on the interface (not internalized) because `InitCommand` calls it via the `IIterationService` abstraction (line 125) and the test constructor injects `IIterationService` mocks that must be able to stub it. The returned string is informational only (display during `twig init`) and MUST NOT drive behavioral branching.
- **FR-6**: `HumanOutputFormatter.GetTypeColor()` MUST resolve type colors from the `_typeColors` dictionary (ADO-sourced) only, falling through to `DeterministicColor()` hash for types not in the dictionary.
- **FR-7**: `WorkItemType.Parse()` MUST continue to accept any non-empty string. Case normalization for the 13 standard type names SHOULD be retained for backward compatibility using a simpler inline approach (not requiring `KnownTypes` dictionary or `IsStandard` property).
- **FR-8**: `PromptCommand` raw SQLite access MUST NOT depend on `ProcessTemplate`. State category resolution SHOULD query `process_types` for the work item's type to get accurate categories when available. JSON deserialization of `states_json` MUST use the source-generated `TwigJsonContext.Default` serializer (specifically `JsonSerializer.Deserialize(statesJson, TwigJsonContext.Default.ListStateEntry)`) for AOT compliance per NFR-1. `TwigJsonContext` already registers `List<StateEntry>` via `[JsonSerializable]` attribute.
- **FR-9**: All commands that depend on `ProcessConfiguration` MUST produce a clear error message ("Run 'twig init' first") if `process_types` is empty (fresh install before init).

### Non-Functional Requirements

- **NFR-1** (AOT): All changes MUST maintain .NET 9 AOT/trimming compatibility. No new reflection. Source-generated JSON serialization only.
- **NFR-2** (Perf): `PromptCommand` MUST complete in <100ms including SQLite I/O. Any process_types lookup MUST be a single indexed query.
- **NFR-3** (Backward compat): Existing `.twig/config` files with `process_template` context values MUST NOT cause crashes. The value MAY be ignored silently.
- **NFR-4** (Test coverage): Every deleted hardcoded behavior MUST have a replacement dynamic test that exercises the same logical scenario against `ProcessTypeRecord` data.

## Proposed Design

### Architecture Overview

```
                                      ┌──────────────────────┐
                                      │   ADO REST API       │
                                      │  (workitemtypes,     │
                                      │   processconfiguration)│
                                      └──────────┬───────────┘
                                                 │ init/refresh
                                                 ▼
                                      ┌──────────────────────┐
                                      │  process_types table │
                                      │  (SQLite)            │
                                      │  ProcessTypeRecord   │
                                      └──────────┬───────────┘
                                                 │ GetAllAsync()
                                                 ▼
                        ┌────────────────────────────────────────────────┐
                        │         DynamicProcessConfigProvider           │
                        │  IProcessConfigurationProvider.GetConfiguration()│
                        │  (no ProcessTemplate parameter)                 │
                        └──────────┬─────────────┬──────────────────────┘
                                   │             │
                    ┌──────────────┘             └──────────────┐
                    ▼                                           ▼
        ┌────────────────────┐                     ┌────────────────────┐
        │ ProcessConfiguration│                     │ StateShorthand     │
        │ .FromRecords()     │                     │ .Resolve(char,     │
        │ (sole constructor) │                     │  ProcessTypeRecord)│
        └────────────────────┘                     └────────────────────┘
                    │                                           │
                    ▼                                           ▼
        ┌────────────────────┐                     ┌────────────────────┐
        │ StateCommand       │                     │ SeedCommand        │
        │ TreeCommand        │                     │ StateTransition    │
        │ etc.               │                     │ Service            │
        └────────────────────┘                     └────────────────────┘
```

### Key Components

#### 1. `ProcessConfiguration` (Modified)

**Before**: Has `ProcessTemplate Template` property, `ForTemplate()` factory with 4 hardcoded builders, `ForDynamic()` that merges dynamic on top of hardcoded.

**After**: Remove `Template` property. Remove `ForTemplate()` and all `Build{Basic,Agile,Scrum,Cmmi}()` methods. Rename `ForDynamic()` → `FromRecords()`. Update the private constructor from `private ProcessConfiguration(ProcessTemplate template, IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)` (two arguments) to `private ProcessConfiguration(IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)` (single argument, removing the `ProcessTemplate` parameter). This becomes the sole factory:

```csharp
public static ProcessConfiguration FromRecords(IReadOnlyList<ProcessTypeRecord> typeRecords)
{
    var configs = new Dictionary<WorkItemType, TypeConfig>();
    foreach (var record in typeRecords)
    {
        if (string.IsNullOrEmpty(record.TypeName) || record.States.Count == 0)
            continue;

        var type = WorkItemType.Parse(record.TypeName).Value;
        var childTypes = record.ValidChildTypes
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => WorkItemType.Parse(n).Value)
            .ToArray();
        configs[type] = BuildTypeConfig(
            record.States.Select(s => s.Name).ToArray(),
            childTypes);
    }
    return new ProcessConfiguration(configs);
}
```

The `BuildTypeConfig` helper (generating transition rules from state order) is retained unchanged.

#### 2. `IProcessConfigurationProvider` (Modified)

**Before**: `GetConfiguration(ProcessTemplate template)`

**After**: `GetConfiguration()` — parameterless. Throws `InvalidOperationException` with "Run 'twig init' first" message if `process_types` is empty.

```csharp
public interface IProcessConfigurationProvider
{
    ProcessConfiguration GetConfiguration();
}
```

#### 3. `DynamicProcessConfigProvider` (Modified)

**Before**: Takes `ProcessTemplate`, falls back to `ForTemplate()` when records empty.

**After**: Parameterless `GetConfiguration()`. Throws if records empty (no silent fallback).

```csharp
public ProcessConfiguration GetConfiguration()
{
    if (_cachedConfig is not null)
        return _cachedConfig;

    var records = _processTypeStore.GetAllAsync().GetAwaiter().GetResult();
    if (records.Count == 0)
        throw new InvalidOperationException(
            "Process configuration not available. Run 'twig init' to initialize.");

    _cachedConfig = ProcessConfiguration.FromRecords(records);
    return _cachedConfig;
}
```

#### 4. `StateShorthand` (Rewritten)

**Before**: ~120-line hardcoded dictionary keyed by `(char, ProcessTemplate, string typeGroup)`.

**After**: Dynamic resolver using `StateCategory`:

```csharp
public static class StateShorthand
{
    private static readonly IReadOnlyDictionary<char, StateCategory> CategoryMap =
        new Dictionary<char, StateCategory>
        {
            ['p'] = StateCategory.Proposed,
            ['c'] = StateCategory.InProgress,
            ['s'] = StateCategory.Resolved,
            ['d'] = StateCategory.Completed,
            ['x'] = StateCategory.Removed,
        };

    public static Result<string> Resolve(char code, IReadOnlyList<StateEntry> states)
    {
        if (!CategoryMap.TryGetValue(code, out var targetCategory))
            return Result.Fail<string>(
                $"Invalid shorthand code: '{code}'. Valid codes are: p, c, s, d, x.");

        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Category == targetCategory)
                return Result.Ok(states[i].Name);
        }

        return Result.Fail<string>(
            $"No state with category '{targetCategory}' found for this work item type.");
    }
}
```

This approach:
- Works for ALL process templates (standard and custom) without hardcoding.
- Returns the first state matching the target category (respecting ADO's category-sorted order).
- Fails gracefully when a category doesn't exist (e.g., Basic has no Resolved or Removed states).

#### 5. `StateCommand` (Modified)

**Before**: Parses `process_template` from context store, defaults to Agile, calls `StateShorthand.Resolve(code, template, type)`.

**After**: Loads `ProcessTypeRecord` for the work item's type from `IProcessTypeStore`, calls `StateShorthand.Resolve(code, record.States)`. Falls back to `StateCategoryResolver.FallbackCategory` heuristic if no record found (rare edge case for types not in `process_types`).

```csharp
// Get process type record for this work item type
var processTypeStore = /* injected */;
var typeRecord = await processTypeStore.GetByNameAsync(item.Type.Value);
if (typeRecord is null || typeRecord.States.Count == 0)
{
    Console.Error.WriteLine(fmt.FormatError(
        $"No state data for type '{item.Type}'. Run 'twig refresh' to update."));
    return 1;
}

var resolveResult = StateShorthand.Resolve(shorthand[0], typeRecord.States);
```

#### 6. `SeedCommand` (Modified)

**Before**: Parses `process_template` from context, defaults to Agile, gets config.

**After**: Calls `processConfigProvider.GetConfiguration()` (parameterless).

#### 7. `HumanOutputFormatter.GetTypeColor()` (Simplified)

**Before**: Checks `_typeColors` dict first, then falls through to hardcoded switch (13 types), then `DeterministicColor()`.

**After**: Checks `_typeColors` dict first, then `DeterministicColor()`. No hardcoded switch.

```csharp
private string GetTypeColor(WorkItemType type)
{
    if (_typeColors is not null &&
        _typeColors.TryGetValue(type.Value, out var hex))
    {
        var trueColor = HexToAnsi.ToForeground(hex);
        if (trueColor is not null)
            return trueColor;
    }
    return DeterministicColor(type.Value);
}
```

#### 8. `WorkItemType` (Simplified)

**Before**: 13-member `KnownTypes` dictionary for case normalization, `IsStandard` property.

**After**: Remove `KnownTypes` dictionary and `IsStandard` property. Retain the 13 static readonly constants (`Epic`, `Feature`, etc.) for ergonomic use in production code and tests. Retain case normalization using a simpler pattern in `Parse()`:

```csharp
public static Result<WorkItemType> Parse(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return Result.Fail<WorkItemType>("Work item type cannot be empty.");

    var trimmed = raw.Trim();

    // Case-normalize known standard type names
    var normalized = NormalizeCasing(trimmed);
    return Result.Ok(new WorkItemType(normalized ?? trimmed));
}

private static string? NormalizeCasing(string value) => value.ToLowerInvariant() switch
{
    "epic" => "Epic",
    "feature" => "Feature",
    "task" => "Task",
    "bug" => "Bug",
    "test case" => "Test Case",
    "user story" => "User Story",
    "product backlog item" => "Product Backlog Item",
    "impediment" => "Impediment",
    "requirement" => "Requirement",
    "change request" => "Change Request",
    "review" => "Review",
    "risk" => "Risk",
    "issue" => "Issue",
    _ => null,
};
```

This preserves backward compatibility (case normalization) without requiring a dictionary or `IsStandard` property.

#### 9. `IIterationService` (Modified)

Replace `DetectProcessTemplateAsync()` (returning `Task<ProcessTemplate>`) with `DetectTemplateNameAsync()` (returning `Task<string?>`). The method MUST remain on the interface because `InitCommand.cs` (line 125) calls it via the `IIterationService` abstraction, and the test constructor (lines 45-52) accepts an injected `IIterationService` mock. Internalizing the method would break both the production call path and test mock setup.

In `AdoIterationService`, the implementation changes from returning a `ProcessTemplate` enum value to returning the template name as a `string?`. The heuristic logic is unchanged — it still checks for "User Story" → `"Agile"`, "Product Backlog Item" → `"Scrum"`, etc. — but the return value is now informational-only and does not drive any behavioral branching.

#### 10. `PromptCommand` (Modified)

The `GetStateCategory()` method currently calls `StateCategoryResolver.Resolve(state, null)` — passing null entries means it always uses the fallback heuristic. Enhancement: query `process_types` for the work item's type name to get accurate `StateEntry` data:

```csharp
// In ReadPromptData(), after reading work item row:
IReadOnlyList<StateEntry>? stateEntries = null;
try
{
    using var ptCmd = conn.CreateCommand();
    ptCmd.CommandText = "SELECT states_json FROM process_types WHERE type_name = @type";
    ptCmd.Parameters.AddWithValue("@type", type);
    var statesJson = ptCmd.ExecuteScalar() as string;
    if (statesJson is not null)
        stateEntries = JsonSerializer.Deserialize(statesJson, TwigJsonContext.Default.ListStateEntry);
}
catch (SqliteException) { /* fall through to heuristic */ }

var stateCategory = StateCategoryResolver.Resolve(state, stateEntries).ToString();
```

**AOT compliance note**: `PromptCommand` currently has no JSON deserialization (it uses manual `Utf8JsonWriter` for output). This enhancement introduces deserialization of `states_json`, which MUST use the source-generated `TwigJsonContext.Default.ListStateEntry` typeinfo — NOT `JsonSerializer.Deserialize<List<StateEntry>>()` (which would require runtime reflection, violating NFR-1). `TwigJsonContext` already registers `[JsonSerializable(typeof(List<StateEntry>))]`, so no changes to the context class are needed.

### Data Flow

#### `twig init` flow (updated)

```
User runs `twig init --org X --project Y`
  → AdoIterationService.GetWorkItemTypesWithStatesAsync()
  → AdoIterationService.GetProcessConfigurationAsync()
  → InferParentChildMap()
  → For each type: persist ProcessTypeRecord to process_types table
  → AdoIterationService.DetectTemplateNameAsync() → returns string? (e.g., "Agile")
  → Display template name informationally: "Process: Agile"
  → Note: InitCommand does NOT store process_template in the context store
```

#### `twig state d` flow (updated)

```
User runs `twig state d`
  → StateCommand reads active work item from cache
  → StateCommand loads ProcessTypeRecord from IProcessTypeStore for item.Type
  → StateShorthand.Resolve('d', record.States) → finds first Completed state
  → StateCommand loads ProcessConfiguration via provider.GetConfiguration()
  → StateTransitionService.Evaluate(config, type, fromState, toState)
  → Patch to ADO
```

#### `twig seed "title"` flow (updated)

```
User runs `twig seed "my task"`
  → SeedCommand loads active parent from cache
  → SeedCommand calls processConfigProvider.GetConfiguration() (no template param)
  → SeedFactory.Create() validates parent-child rules
  → Push to ADO
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Remove `ProcessTemplate` enum entirely rather than keeping it as a display-only concept | The enum implies branching behavior. Keeping it invites future hardcoded branches. A simple `string?` for display is sufficient. |
| DD-2 | Throw `InvalidOperationException` when process_types is empty instead of silent fallback | Silent fallback masks init problems. Explicit failure with "run twig init" message is clearer UX and prevents stale/wrong behavior. |
| DD-3 | Map shorthand chars to `StateCategory` rather than introducing per-type shorthand config | `StateCategory` already classifies every state via ADO metadata. The mapping is deterministic and works for all templates without configuration. |
| DD-4 | Retain the 13 `WorkItemType` static constants but remove `KnownTypes` dict | The constants are used throughout tests and production code for ergonomic equality checks (`== WorkItemType.Bug`). Removing them would cause unnecessary churn. `KnownTypes` is only for `Parse()` normalization and `IsStandard` — both replaced. |
| DD-5 | Keep case normalization in `Parse()` via switch expression | ADO returns canonical casing, but user CLI input (`twig seed --type bug`) needs normalization. A switch expression is simpler than a dictionary and AOT-safe. |
| DD-6 | Keep `StateCategoryResolver.FallbackCategory()` heuristic | Edge cases exist where `StateEntry` data may be unavailable (e.g., corrupt DB, types added after last refresh). The heuristic provides a reasonable degraded experience. |
| DD-7 | Retain template name detection as informational-only string in `twig init` output | Users find it helpful to see "Process: Agile" during init. This is display-only and doesn't drive behavior. |
| DD-8 | Inject `IProcessTypeStore` into `StateCommand` rather than passing through `ProcessConfiguration` for shorthand | Shorthand resolution needs per-type state data (with categories), not the aggregate `ProcessConfiguration`. Direct access to `IProcessTypeStore` is more precise. |
| DD-9 | Replace `DetectProcessTemplateAsync()` with `DetectTemplateNameAsync()` on `IIterationService` (not internalize it) | `InitCommand.cs` (line 125) calls this method via the `IIterationService` abstraction. The test constructor (lines 45-52) injects an `IIterationService` mock. Internalizing the method would break both the production call and mock-based tests. Keeping it on the interface with a `string?` return type preserves testability while removing the `ProcessTemplate` dependency. |
| DD-10 | Update `ProcessConfiguration` private constructor from two arguments to single argument | Existing constructor is `private ProcessConfiguration(ProcessTemplate template, IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)`. Since `Template` property is removed, the constructor must become `private ProcessConfiguration(IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)`. Without this, `FromRecords()` calling `new ProcessConfiguration(configs)` would fail to compile. |
| DD-11 | Use `processConfigProvider.GetConfiguration().TypeConfigs[item.Type].StateEntries` in `StateCommand` instead of injecting `IProcessTypeStore` (supersedes DD-8) | DD-8 originally planned direct `IProcessTypeStore` injection. During implementation it was found that `IProcessConfigurationProvider` (already injected for `StateTransitionService.Evaluate`) already carries per-type `StateEntries` via `TypeConfigs`. Using it avoids an additional constructor dependency and keeps a single source of truth for process data in `StateCommand`. The `IProcessTypeStore` approach would have added redundant DI wiring with no behavioral difference. |

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Keep `ProcessTemplate` as string enum for display | Backward compatible, no config file changes | Still implies behavioral branching, attracts hardcoded additions | Rejected — string field in config is sufficient |
| Build shorthand config per-type in `ProcessTypeRecord` | Maximum flexibility, per-type shorthand overrides | Over-engineering for the shorthand use case, increased DB schema complexity | Rejected — `StateCategory` mapping is simpler and sufficient |
| Lazy-init fallback: if `process_types` empty, auto-run init | Better UX for first-time users | Complex, requires auth to be available, breaks offline-first principle | Rejected — explicit "run twig init" is clearer |
| Remove case normalization entirely from `Parse()` | Simpler code | Breaking change for existing scripts using `twig seed --type bug` | Rejected — backward compat risk |

## Dependencies

### Internal Dependencies (Complete)

- **`twig-dynamic-process.plan.md`** ✅: Provides `ProcessTypeRecord`, `IProcessTypeStore`, `SqliteProcessTypeStore`, `ForDynamic()`, `process_types` table, open type system in `WorkItemType.Parse()`.
- **`twig-state-category-cleanup.plan.md`** ✅: Provides `StateCategory` enum, `StateEntry` with category fields, `StateCategoryResolver`, category-sorted state ordering.

### External Dependencies

- contoso.Data.Sqlite (existing — no new dependency)
- .NET 9 AOT/trimming (existing constraint)

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `ProcessTemplate` enum | **Deleted** | Low — no external consumers |
| `ProcessConfiguration` | Major refactor — remove Template property, ForTemplate(), 4 builders | Medium — many tests reference these |
| `StateShorthand` | **Rewritten** — new signature, new resolution logic | Medium — tests fully rewritten |
| `IProcessConfigurationProvider` | Interface change — remove parameter | Medium — all callers updated |
| `DynamicProcessConfigProvider` | Simplified — remove fallback | Low |
| `HardCodedProcessConfigProvider` | **Deleted** | Low — already unused in DI |
| `IIterationService` | Replace one method signature (enum return → string return) | Low |
| `AdoIterationService` | Method renamed, return type changed | Low |
| `StateCommand` | Inject `IProcessTypeStore`, change shorthand call | Medium |
| `SeedCommand` | Remove template parsing | Low |
| `InitCommand` | Replace `DetectProcessTemplateAsync()` call with `DetectTemplateNameAsync()` | Low |
| `RefreshCommand` | Remove template dependency | Low |
| `HumanOutputFormatter` | Remove hardcoded color switch | Low |
| `WorkItemType` | Remove `KnownTypes`, `IsStandard`, simplify `Parse()` | Low |
| `PromptCommand` | Add process_types query for state category | Low |
| 13+ test files | Updated to test dynamic behavior | Medium — significant test rewrite |

### Backward Compatibility

- **`.twig/config` files**: May contain `process_template` values in context store. These will be ignored (not read). No crash. The field can be cleaned up in a future migration.
- **SQLite databases**: The `process_types` table is unchanged. Existing data works as-is.
- **CLI commands**: All command-line arguments and flags remain identical. No user-facing changes.

### Performance

- `StateShorthand.Resolve()` changes from dictionary lookup O(1) to linear scan of states list O(n) where n ≤ ~10. Negligible impact.
- `PromptCommand` adds one indexed SQLite query (~0.1ms). Within 100ms budget.
- `ProcessConfiguration` construction is unchanged (still O(n*m) for n types × m states). Called once per CLI invocation and cached.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Empty `process_types` table causes confusing errors | Medium | Medium | FR-9: All config-dependent commands check and emit "Run 'twig init' first" message before proceeding |
| `StateShorthand` category-based resolution returns different state than hardcoded mapping for edge cases | Low | Medium | ADO's category assignments are canonical. Verify against all 4 standard templates in tests. The first-match behavior matches ADO's own category ordering. |
| Removing `IsStandard` breaks downstream code we haven't identified | Low | Low | grep confirms only `StateShorthand` uses `IsStandard`. Tests verify. |
| `PromptCommand` process_types query adds latency | Low | Low | Single indexed query on primary key. Measured at <0.2ms on SQLite WAL. Graceful fallback if query fails. |
| Custom processes where ADO returns unexpected category values | Low | Medium | `StateCategoryResolver.FallbackCategory()` heuristic remains as safety net. `StateCategory.Unknown` maps to `?` shorthand. |

## Open Questions

| # | Question | Status | Impact |
|---|----------|--------|--------|
| OQ-1 | Should `twig init` continue to display the heuristically detected template name (e.g., "Process: Agile")? | **Proposed: Yes** — informational only, useful for user orientation. Implement as internal helper returning `string?`. | Low — display only |
| OQ-2 | Should the `process_template` key in context store be actively cleaned up during `twig refresh`, or left as dead data? | **Proposed: Leave** — cleaning up is a schema migration concern deferred to a future plan. | Low |
| OQ-3 | Should `GetTypeBadge()` in `HumanOutputFormatter` also be made dynamic using icon data from `ProcessTypeRecord.IconId`? | **Proposed: Out of scope** — `IconSet` handles this separately, and icon mapping is orthogonal to process template assumptions. | Low |

## Implementation Phases

### Phase 1: Foundation — Remove ProcessTemplate enum and ForTemplate()

Remove the core hardcoded infrastructure. All downstream compilation errors surface here and are fixed in subsequent phases.

### Phase 2: Dynamic StateShorthand

Rewrite `StateShorthand` to use `StateCategory`-based resolution from `ProcessTypeRecord.States`.

### Phase 3: Simplify consumers (StateCommand, SeedCommand, InitCommand, RefreshCommand)

Update all CLI commands to use the new parameterless `IProcessConfigurationProvider.GetConfiguration()` and dynamic `StateShorthand.Resolve()`.

### Phase 4: Clean up WorkItemType, HumanOutputFormatter, PromptCommand

Remove `KnownTypes`/`IsStandard`, hardcoded color switch, and enhance PromptCommand state category resolution.

### Phase 5: Test migration and validation

Rewrite all tests that assert hardcoded behavior to assert dynamic behavior.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| *(none)* | All changes are modifications or deletions of existing files |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | Remove `Template` property, `ForTemplate()`, `BuildBasic/Agile/Scrum/Cmmi()`. **Update private constructor** to single-arg `(IReadOnlyDictionary<WorkItemType, TypeConfig>)`. Rename `ForDynamic()` → `FromRecords()`. |
| `src/Twig.Domain/Interfaces/IProcessConfigurationProvider.cs` | Change `GetConfiguration(ProcessTemplate)` → `GetConfiguration()` |
| `src/Twig.Domain/Interfaces/IIterationService.cs` | Replace `DetectProcessTemplateAsync()` with `DetectTemplateNameAsync()` returning `Task<string?>` |
| `src/Twig.Domain/Services/DynamicProcessConfigProvider.cs` | Remove `ProcessTemplate` parameter, remove fallback to `ForTemplate()`, throw on empty records |
| `src/Twig.Domain/ValueObjects/StateShorthand.cs` | Complete rewrite: remove `Mappings` dict, new `Resolve(char, IReadOnlyList<StateEntry>)` signature |
| `src/Twig.Domain/ValueObjects/WorkItemType.cs` | Remove `KnownTypes` dict, `IsStandard` property. Replace `Parse()` normalization with switch expression. |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Rename `DetectProcessTemplateAsync()` → `DetectTemplateNameAsync()`, change return type to `string?`, update heuristic returns from enum values to string literals |
| `src/Twig/Commands/StateCommand.cs` | Inject `IProcessTypeStore`, use `StateShorthand.Resolve(code, record.States)`, remove `process_template` parsing |
| `src/Twig/Commands/SeedCommand.cs` | Remove `process_template` parsing, call `GetConfiguration()` (parameterless) |
| `src/Twig/Commands/InitCommand.cs` | Replace `DetectProcessTemplateAsync()` call with `DetectTemplateNameAsync()`. Update display to use returned string. Note: InitCommand does NOT store process_template in context store — no cleanup needed. |
| `src/Twig/Commands/RefreshCommand.cs` | Remove any `ProcessTemplate` references |
| `src/Twig/Commands/PromptCommand.cs` | Add `process_types` query for state category resolution. Deserialize `states_json` using AOT-safe `TwigJsonContext.Default.ListStateEntry`. |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Remove hardcoded type color switch in `GetTypeColor()` |
| `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | Rewrite: test `FromRecords()` with `ProcessTypeRecord` data instead of `ForTemplate()` |
| `tests/Twig.Domain.Tests/Aggregates/ProcessTypeRecordTests.cs` | Update for `FromRecords()` rename |
| `tests/Twig.Domain.Tests/ValueObjects/StateShorthandTests.cs` | Complete rewrite: test dynamic resolution with `StateEntry` data |
| `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | Remove `IsStandard` tests, verify case normalization via switch |
| `tests/Twig.Domain.Tests/Services/DynamicProcessConfigProviderTests.cs` | Update for parameterless `GetConfiguration()`, test empty-store exception |
| `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | Update to use `FromRecords()` instead of `ForTemplate()` |
| `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` | Update to use `FromRecords()` instead of `ForTemplate()` |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Update mock setup for new `IProcessTypeStore` dependency |
| `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | Update for parameterless `GetConfiguration()` |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Remove template detection assertions, update for informational display |
| `tests/Twig.Cli.Tests/Commands/InitUserDetectionTests.cs` | Remove `ProcessTemplate` references from mock setup |
| `tests/Twig.Cli.Tests/Commands/MultiContextInitTests.cs` | Remove `ProcessTemplate` references from mock setup |
| `tests/Twig.Cli.Tests/Commands/ConflictUxTests.cs` | Remove `ProcessTemplate` references from mock setup |
| `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Rename `DetectProcessTemplateAsync` tests to `DetectTemplateNameAsync`, change assertions from enum to string |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig.Domain/Enums/ProcessTemplate.cs` | Enum retired — no longer drives behavior |
| `src/Twig.Domain/Services/HardCodedProcessConfigProvider.cs` | Trivial wrapper around deleted `ForTemplate()`. Already unused in DI. |

---

## Implementation Plan

### EPIC-1: Core Infrastructure — Remove ProcessTemplate Enum and Hardcoded Builders

**Goal**: Delete the `ProcessTemplate` enum, remove `ForTemplate()` and the four hardcoded builders from `ProcessConfiguration`, update `IProcessConfigurationProvider` interface, update `DynamicProcessConfigProvider`, and delete `HardCodedProcessConfigProvider`.

**Prerequisites**: None (first epic)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Delete `ProcessTemplate.cs` enum file | `src/Twig.Domain/Enums/ProcessTemplate.cs` | DONE |
| ITEM-002 | IMPL | Delete `HardCodedProcessConfigProvider.cs` | `src/Twig.Domain/Services/HardCodedProcessConfigProvider.cs` | DONE |
| ITEM-003 | IMPL | Remove `Template` property from `ProcessConfiguration`. Remove `ForTemplate()` method and all four `Build{Basic,Agile,Scrum,Cmmi}()` private methods. **Update private constructor** from `private ProcessConfiguration(ProcessTemplate template, IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)` to `private ProcessConfiguration(IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)` (remove `ProcessTemplate` parameter, remove `Template = template` assignment). Rename `ForDynamic()` → `FromRecords(IReadOnlyList<ProcessTypeRecord>)` removing the `ProcessTemplate baseTemplate` parameter. Update `FromRecords()` to build configs directly from records without calling `ForTemplate()` as base — call `new ProcessConfiguration(configs)` with single argument. Retain `BuildTypeConfig()` helper unchanged. | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | DONE |
| ITEM-004 | IMPL | Change `IProcessConfigurationProvider.GetConfiguration(ProcessTemplate)` → `GetConfiguration()` (parameterless). Update XML doc. | `src/Twig.Domain/Interfaces/IProcessConfigurationProvider.cs` | DONE |
| ITEM-005 | IMPL | Update `DynamicProcessConfigProvider`: remove `ProcessTemplate` parameter from `GetConfiguration()`, remove `_cachedTemplate` field, change cache key to simple boolean, throw `InvalidOperationException("Process configuration not available. Run 'twig init' to initialize.")` when records are empty instead of falling back to `ForTemplate()`. Call `ProcessConfiguration.FromRecords()`. | `src/Twig.Domain/Services/DynamicProcessConfigProvider.cs` | DONE |
| ITEM-006 | TEST | Rewrite `ProcessConfigurationTests.cs`: replace all `ForTemplate()` tests with `FromRecords()` tests using `ProcessTypeRecord` data. Cover: basic type hierarchy, transition rules (forward/backward/cut), unknown type returns empty child list, multi-type records. | `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | DONE |
| ITEM-007 | TEST | Rewrite `DynamicProcessConfigProviderTests.cs`: test parameterless `GetConfiguration()`, test empty-store throws `InvalidOperationException`, test caching behavior (single call, not per-template), test with custom type records. | `tests/Twig.Domain.Tests/Services/DynamicProcessConfigProviderTests.cs` | DONE |
| ITEM-008 | TEST | Update `ProcessTypeRecordTests.cs`: change `ForDynamic()` calls to `FromRecords()`. | `tests/Twig.Domain.Tests/Aggregates/ProcessTypeRecordTests.cs` | DONE |
| ITEM-009 | TEST | Update `StateTransitionServiceTests.cs`: build `ProcessConfiguration` via `FromRecords()` with `ProcessTypeRecord` data instead of `ForTemplate()`. | `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | DONE |
| ITEM-010 | TEST | Update `SeedFactoryTests.cs`: build `ProcessConfiguration` via `FromRecords()` with `ProcessTypeRecord` data instead of `ForTemplate()`. | `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `ProcessTemplate.cs` file deleted
- [x] `HardCodedProcessConfigProvider.cs` file deleted
- [x] `ProcessConfiguration` has no `Template` property and no `ForTemplate()` method
- [x] `ProcessConfiguration` private constructor takes single `IReadOnlyDictionary<WorkItemType, TypeConfig>` parameter (no `ProcessTemplate`)
- [x] `FromRecords()` builds config without any hardcoded base
- [x] `IProcessConfigurationProvider.GetConfiguration()` is parameterless
- [x] `DynamicProcessConfigProvider` throws on empty store
- [x] All domain tests pass with dynamic data

---

### EPIC-2: Dynamic State Shorthand

**Goal**: Rewrite `StateShorthand` to resolve shorthand codes using `StateCategory` from `ProcessTypeRecord.States` instead of the hardcoded dictionary.

**Prerequisites**: EPIC-1 (ProcessTemplate enum deleted)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-011 | IMPL | Rewrite `StateShorthand.cs`: remove the `Mappings` dictionary, `GetTypeGroup()` method, and `Resolve(char, ProcessTemplate, WorkItemType)` signature. Implement new `Resolve(char, IReadOnlyList<StateEntry>)` that maps char → `StateCategory` → first matching `StateEntry.Name`. Add `CategoryMap` dictionary: `p→Proposed, c→InProgress, s→Resolved, d→Completed, x→Removed`. | `src/Twig.Domain/ValueObjects/StateShorthand.cs` | DONE |
| ITEM-012 | TEST | Rewrite `StateShorthandTests.cs`: test dynamic resolution with `StateEntry` lists. Cover: all 5 shorthand codes against Agile-like states, Scrum-like states (Committed for InProgress), Basic-like states (no Resolved/Removed), CMMI-like states, custom states with non-standard names, invalid codes, empty state list. Verify same behavioral outcomes as before for standard templates. | `tests/Twig.Domain.Tests/ValueObjects/StateShorthandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `StateShorthand` has no reference to `ProcessTemplate`
- [x] Resolves `d` → "Done" for states `[New, Active, Done]` (Completed category)
- [x] Resolves `c` → "Committed" for Scrum PBI states (InProgress category)
- [x] Returns failure for `s` when no Resolved-category state exists
- [x] All shorthand tests pass with dynamic data

---

### EPIC-3: Update CLI Commands

**Goal**: Update `StateCommand`, `SeedCommand`, `InitCommand`, and `RefreshCommand` to use the new parameterless interfaces and dynamic shorthand. Remove `process_template` parsing from context store.

**Prerequisites**: EPIC-1, EPIC-2

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-013 | IMPL | Update `StateCommand`: add `IProcessTypeStore` to constructor injection. Remove `process_template` context store parsing and `ProcessTemplate.Agile` fallback. Load `ProcessTypeRecord` via `processTypeStore.GetByNameAsync(item.Type.Value)`. Call `StateShorthand.Resolve(code, record.States)`. Call `processConfigProvider.GetConfiguration()` (parameterless). Add error message if `ProcessTypeRecord` not found. | `src/Twig/Commands/StateCommand.cs` | DONE |
| ITEM-014 | IMPL | Update `SeedCommand`: remove `process_template` context store parsing and `ProcessTemplate.Agile` fallback. Call `processConfigProvider.GetConfiguration()` (parameterless). | `src/Twig/Commands/SeedCommand.cs` | DONE |
| ITEM-015 | IMPL | Update `InitCommand`: replace `DetectProcessTemplateAsync()` call (line 125) with `DetectTemplateNameAsync()` which returns `string?` instead of `ProcessTemplate`. Update the console output line (line 126) to display the returned string directly (e.g., `"Process: Agile"`). Note: `InitCommand` does NOT currently write `process_template` to the context store — the template is fetched and displayed only. No context store cleanup is needed. | `src/Twig/Commands/InitCommand.cs` | DONE |
| ITEM-016 | IMPL | Replace `DetectProcessTemplateAsync()` with `DetectTemplateNameAsync()` on `IIterationService`: change return type from `Task<ProcessTemplate>` to `Task<string?>`, update XML doc. In `AdoIterationService`, rename method and change return statements from `ProcessTemplate.Agile` → `"Agile"`, `ProcessTemplate.Scrum` → `"Scrum"`, `ProcessTemplate.CMMI` → `"CMMI"`, `ProcessTemplate.Basic` → `"Basic"`. Remove `using Twig.Domain.Enums` if no longer needed. | `src/Twig.Domain/Interfaces/IIterationService.cs`, `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | DONE |
| ITEM-017 | IMPL | Update `RefreshCommand`: remove any `ProcessTemplate` references (currently minimal — verify no template parsing exists). | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-018 | IMPL | Update `Program.cs` DI registration if needed (verify `DynamicProcessConfigProvider` registration works with parameterless interface). | `src/Twig/Program.cs` | DONE |
| ITEM-019 | TEST | Update `StateCommandTests.cs`: add `IProcessTypeStore` mock to test constructor. Supply `ProcessTypeRecord` data in mock setup. Verify shorthand resolution against dynamic states. | `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | DONE |
| ITEM-020 | TEST | Update `SeedCommandTests.cs`: remove `ProcessTemplate` from mock setup. Verify parameterless `GetConfiguration()` is called. | `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | DONE |
| ITEM-021 | TEST | Update `InitCommandTests.cs`: change mock setup from `DetectProcessTemplateAsync()` returning `ProcessTemplate.Agile` to `DetectTemplateNameAsync()` returning `"Agile"`. Verify init still succeeds and populates `process_types`. Verify informational template name display. | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | DONE |
| ITEM-022 | TEST | Update `InitUserDetectionTests.cs`, `MultiContextInitTests.cs`, `ConflictUxTests.cs`: remove `ProcessTemplate` references from mock setups. | `tests/Twig.Cli.Tests/Commands/InitUserDetectionTests.cs`, `tests/Twig.Cli.Tests/Commands/MultiContextInitTests.cs`, `tests/Twig.Cli.Tests/Commands/ConflictUxTests.cs` | DONE |
| ITEM-023 | TEST | Update `AdoIterationServiceTests.cs`: rename `DetectProcessTemplateAsync` tests to `DetectTemplateNameAsync`, change assertions from `ProcessTemplate.Agile` to `"Agile"` (string), etc. | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | DONE |

**Acceptance Criteria**:
- [x] No source file references `ProcessTemplate` enum
- [x] `StateCommand` resolves shorthand from `ProcessTypeRecord.States`
- [x] `SeedCommand` calls parameterless `GetConfiguration()`
- [x] `InitCommand` displays template name informationally (if retained)
- [x] `IIterationService` has `DetectTemplateNameAsync()` returning `Task<string?>` (replaces `DetectProcessTemplateAsync()`)
- [x] All CLI command tests pass

---

### EPIC-4: Clean Up WorkItemType, Formatter, and PromptCommand

**Goal**: Remove `KnownTypes`/`IsStandard` from `WorkItemType`, remove hardcoded color switch from `HumanOutputFormatter`, and enhance `PromptCommand` state category resolution.

**Prerequisites**: EPIC-3 (all ProcessTemplate references removed — `IsStandard` is only used by old `StateShorthand`)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-024 | IMPL | Simplify `WorkItemType.Parse()`: replace `KnownTypes.TryGetValue()` lookup with a `NormalizeCasing()` switch expression. Remove the `KnownTypes` dictionary. Remove the `IsStandard` property. Retain 13 static readonly constants. | `src/Twig.Domain/ValueObjects/WorkItemType.cs` | DONE |
| ITEM-025 | IMPL | Remove hardcoded type color switch from `HumanOutputFormatter.GetTypeColor()`. After checking `_typeColors` dict, fall through directly to `DeterministicColor()`. Remove the `hardcoded` variable and `type.Value.ToLowerInvariant() switch` block. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-026 | IMPL | Enhance `PromptCommand.ReadPromptData()`: after reading work item row, query `process_types` for the type name to get `states_json`. Deserialize using `JsonSerializer.Deserialize(statesJson, TwigJsonContext.Default.ListStateEntry)` — this is the AOT-safe source-generated path (NFR-1). Do NOT use `JsonSerializer.Deserialize<List<StateEntry>>()` as that requires runtime reflection. `PromptCommand` currently has no JSON deserialization (only manual `Utf8JsonWriter` for output); this is new AOT-sensitive surface area. Add `using System.Text.Json` and `using Twig.Infrastructure.Serialization` imports. Pass deserialized entries to `StateCategoryResolver.Resolve(state, entries)` instead of passing `null`. Graceful fallback on `SqliteException` or parse failure. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-027 | TEST | Update `WorkItemTypeTests.cs`: remove `IsStandard_KnownType_ReturnsTrue`, `IsStandard_CustomType_ReturnsFalse` tests. Verify case normalization still works via `Parse()`. Verify constants unchanged. | `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | DONE |
| ITEM-028 | TEST | Add formatter test: verify `GetTypeColor()` returns `DeterministicColor()` for standard types when no `_typeColors` configured (i.e., no hardcoded switch fallback). Verify ADO-sourced color takes priority. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-029 | TEST | Add `PromptCommand` test: verify state category uses `process_types` data when available, falls back to heuristic when not. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `WorkItemType` has no `KnownTypes` dictionary or `IsStandard` property
- [x] `Parse()` still normalizes "bug" → "Bug" and accepts custom types
- [x] `GetTypeColor()` has no hardcoded switch for standard type names
- [x] ADO-sourced colors take priority, `DeterministicColor()` is the only fallback
- [x] `PromptCommand` uses `process_types` for accurate state category when available
- [x] All tests pass

---

### EPIC-5: Final Validation

**Goal**: Run full test suite, verify AOT compatibility, check for any remaining `ProcessTemplate` references.

**Prerequisites**: EPIC-1 through EPIC-4

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-030 | TEST | Run `dotnet test` across all three test projects. Verify zero failures. | All test projects | DONE |
| ITEM-031 | TEST | Run `dotnet publish -c Release` with AOT/trimming to verify no trimming warnings or AOT breaks. | `src/Twig/Twig.csproj` | DONE |
| ITEM-032 | TEST | Grep entire codebase for any remaining `ProcessTemplate` references (should be zero except in this plan document, prior plan docs, and `DetectTemplateNameAsync` string return values). | All files | DONE |
| ITEM-033 | TEST | Verify backward compatibility: create a `.twig/config` file with a `process_template` context key, run commands, verify no crash. | Manual/integration | DONE |

**Acceptance Criteria**:
- [x] All tests pass (zero failures)
- [x] AOT publish succeeds with no trimming warnings
- [x] No source code references `ProcessTemplate` (excluding docs/plans)
- [x] Existing `.twig/config` files with stale `process_template` values don't crash

---

## References

- [twig-dynamic-process.plan.md](twig-dynamic-process.plan.md) — Predecessor plan that opened type system and added dynamic config overlay
- [twig-state-category-cleanup.plan.md](twig-state-category-cleanup.plan.md) — Added `StateCategory` enum, `StateEntry`, `StateCategoryResolver`
- [Azure DevOps Work Item Types API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types) — Source of type metadata including states and categories
- [Azure DevOps Process Configuration API](https://learn.microsoft.com/en-us/rest/api/azure/devops/work/process-configuration) — Source of backlog hierarchy data
